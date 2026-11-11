using System.Threading.Channels;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Storage;

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>
/// How long a lookup holds back before showing what it has, and how long each source may take. Slow sources keep
/// answering in the background while their answer could still matter. The defaults are the Mac's.
/// </summary>
public sealed record LyricLookupTiming
{
    public static LyricLookupTiming Standard { get; } = new();

    /// <summary>The best match found by then is shown even if sources are pending.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>A pending upgrade waits this long for something better still.</summary>
    public TimeSpan UpgradeSettle { get; init; } = TimeSpan.FromSeconds(0.75);

    /// <summary>Apple Music can finish loading its own lyrics just after a track change: it is asked again after this.</summary>
    public TimeSpan AppleRetry { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>How long LRCLIB may take.</summary>
    public TimeSpan LrclibLimit { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long every other source may take.</summary>
    public TimeSpan SourceLimit { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>An automatic lookup reads Apple Music's lyrics before asking any service, for at most this long, so a hit there needs no network.</summary>
    public TimeSpan MusicFirst { get; init; } = TimeSpan.FromSeconds(0.7);
}

/// <summary>
/// The progressive lyric lookup: a saved choice first, then the cached choice, then every enabled source at once,
/// reporting progress as they answer. Ported from the Mac's <c>LyricsFetcher.lookup</c> (Lunalyr's own code; the
/// per-source time limit re-implemented from a behaviour description).
/// </summary>
/// <remarks>
/// A match is shown as soon as nothing pending could beat it, or when the grace period ends; after that only a match
/// that ranks higher replaces it, never a lower one. Saved explicit choices always win. An automatic lookup reads
/// Apple Music's lyrics first and stops asking sources whose answer could no longer change anything; a search
/// (<c>force</c>) asks every source so all the alternatives are listed. Only the online choice is cached, and only
/// when nothing stopped early could have beaten it; misses are remembered briefly unless the network was down.
/// </remarks>
public sealed class LyricLookup
{
    /// <summary>State of a source stopped because Apple Music's own word or mixed timing is showing.</summary>
    public const string AppleMusicShowing = LyricRanking.NotNeeded + "Apple Music’s own lyrics are showing";

    /// <summary>State of a source stopped because its best possible answer could not beat the match showing.</summary>
    public const string CouldNotBeat = LyricRanking.NotNeeded + "it couldn’t beat the match showing";

    public const string Searching = "Searching…";
    public const string NoUsableMatch = "No usable match / unavailable";
    public const string MatchFound = "Match found";
    public const string TimedOut = "Timed out";
    public const string Cancelled = "Cancelled";
    public const string RecentMiss = "No match a few minutes ago";
    public const string SavedMatch = "Saved match";
    public const string NotCheckedSaved = "Not checked: a saved match was used";
    public const string CheckingAppleAgain = "Checking Apple Music again shortly…";
    public const string AppleStillMissing = "No matching lyrics in Apple Music yet. Open the song's lyrics there to load them.";

    private readonly SavedLyrics _saved;
    private readonly LyricSourceFetch _fetch;
    private readonly ILyricConnectivity? _connectivity;
    private readonly LyricLookupTiming _timing;
    private readonly LyricMissCache _misses;
    private readonly TimeProvider _time;
    private readonly Func<LyricQuery, string, string?>? _localFingerprint;
    private readonly CategoryLogger _log;

    /// <param name="saved">Saved choices and the automatic cache.</param>
    /// <param name="fetch">Asks one source for its best match.</param>
    /// <param name="connectivity">Whether the network is up; assumed up when null.</param>
    /// <param name="timing">Grace periods and limits; the Mac's when null.</param>
    /// <param name="misses">Recent misses; a fresh ten-minute cache when null.</param>
    /// <param name="timeProvider">Clock for every wait.</param>
    /// <param name="localFingerprint">
    /// Identity of the local lyric files that match a query in a folder (the local-files workstream provides it), so
    /// <see cref="RefreshLocalAsync"/> knows when they changed. Null when there is no local source.
    /// </param>
    /// <param name="logger">Where lookup summaries go.</param>
    public LyricLookup(SavedLyrics saved, LyricSourceFetch fetch, ILyricConnectivity? connectivity = null,
        LyricLookupTiming? timing = null, LyricMissCache? misses = null, TimeProvider? timeProvider = null,
        Func<LyricQuery, string, string?>? localFingerprint = null, Logger? logger = null)
    {
        _saved = saved;
        _fetch = fetch;
        _connectivity = connectivity;
        _timing = timing ?? LyricLookupTiming.Standard;
        _time = timeProvider ?? TimeProvider.System;
        _misses = misses ?? new LyricMissCache(timeProvider: _time);
        _localFingerprint = localFingerprint;
        _log = (logger ?? Logger.Null).Lyrics;
    }

    /// <summary>
    /// Test hook: called on the lookup's own flow whenever it is about to wait with no event queued, so a fake clock
    /// can move to its next timer. Returns false when there was nothing to move to.
    /// </summary>
    internal Func<bool>? WhenIdle { get; set; }

    /// <summary>The key the automatic cache and the miss cache use for a query under these options.</summary>
    public static string CacheKey(LyricQuery query, LyricSourceSnapshot options) => query.RecordingKey + "|" + options.Fingerprint;

    /// <summary>
    /// Looks up lyrics for <paramref name="query"/>, calling <paramref name="onUpdate"/> with progress while sources
    /// are still answering (awaited before the lookup continues).
    /// </summary>
    /// <param name="force">A search: ignores the saved choice for what is asked (it still shows), the cache, the miss
    /// cache and the stream check, and asks every source.</param>
    /// <exception cref="OperationCanceledException">The lookup was cancelled; nothing is cached.</exception>
    public async Task<LyricLookupReport> LookupAsync(LyricQuery query, LyricSourceSnapshot options, bool force = false,
        Func<LyricLookupReport, ValueTask>? onUpdate = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = CacheKey(query, options);
        var pinned = _saved.Read(query.RecordingKey, pin: true);
        if (!force && pinned is not null)
        {
            return new LyricLookupReport { Result = pinned, Pinned = true, Cached = true, Revision = 1 };
        }
        // Station names make junk matches, and a stream's clock is not the song's.
        if (!force && query.Duration <= 0)
        {
            return new LyricLookupReport { SkippedStream = true };
        }
        var local = options.Enabled.Where(LyricSourceInfo.IsLocal).ToList();
        var online = options.Enabled.Where(s => !s.IsLocal()).ToList();
        LyricCandidate? saved = null;
        var seeded = new List<LyricCandidate>();
        var states = new Dictionary<LyricSourceId, string>();
        if (!force && online.Count > 0)
        {
            if (_saved.ReadCandidate(key) is { } candidate && online.Contains(candidate.Source))
            {
                saved = candidate;
                seeded.Add(candidate);
                foreach (var id in online)
                {
                    states[id] = id == candidate.Source ? SavedMatch : NotCheckedSaved;
                }
                online = [];
            }
            else
            {
                var recent = _misses.Answered(key);
                foreach (var id in online.Where(recent.Sources.Contains))
                {
                    states[id] = RecentMiss;
                }
                online.RemoveAll(recent.Sources.Contains);
                // A close match they offered is listed again, with its reason.
                seeded.AddRange(recent.Rejected);
            }
        }
        var localPrint = local.Contains(LyricSourceId.Lyrimuse) ? _localFingerprint?.Invoke(query, options.LocalFolder) : null;
        var session = new Session(this, query, options, local, online, seeded, states, pinned, automatic: !force, localPrint,
            onUpdate, cancellationToken);
        var collected = await session.RunAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var queried = collected.Asked.Overlaps(online);
        var report = collected.Report with { Cached = saved is not null, Offline = queried && collected.Report.Offline };
        if (!queried)
        {
            return report;
        }
        // Only the online choice is cached; local sources are read afresh every time, so editing or adding a file
        // needs no network lookup.
        if (LyricRanking.Choose(report.Candidates.Where(c => !c.Source.IsLocal()), query, options) is { } best)
        {
            // A choice that a source stopped early might have beaten is not kept.
            if (LyricRanking.IsSettled(best, collected.Stopped.Where(s => !s.IsLocal()), query, options))
            {
                _saved.Save(best, key);
                _misses.Remove(key);
            }
        }
        else if (!force && !report.Offline)
        {
            // Only sources that replied are remembered, each with the close match it offered, if any; ones that
            // failed are asked again.
            var replied = collected.Answered.Where(online.Contains).ToHashSet();
            _misses.Insert(key, replied, report.Rejected.Select(r => r.Candidate).Where(c => replied.Contains(c.Source)));
        }
        _log.Debug($"Lyric lookup done: {report.Candidates.Count} matches, revision {report.Revision}"
            + (report.Result is null ? ", nothing found" : $", showing {report.Result.Provenance.Label}"));
        return report;
    }

    /// <summary>
    /// Re-reads only the local lyric files after they change, keeping the online candidates already found. Null when
    /// nothing relevant changed.
    /// </summary>
    public async Task<LyricLookupReport?> RefreshLocalAsync(LyricQuery query, LyricLookupReport previous, LyricSourceSnapshot options,
        CancellationToken cancellationToken = default)
    {
        if (query.Duration <= 0 || previous.Pinned || !options.Enabled.Contains(LyricSourceId.Lyrimuse))
        {
            return null;
        }
        var print = _localFingerprint?.Invoke(query, options.LocalFolder);
        if (print == previous.LocalPrint)
        {
            return null;
        }
        var done = new TaskCompletionSource<SourceOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new SourceRun(this, LyricSourceId.Lyrimuse, query, options.LocalFolder, cancellationToken, outcome => done.TrySetResult(outcome));
        run.Start();
        var result = await done.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var known = previous.Candidates.Concat(previous.Rejected.Select(r => r.Candidate)).ToList();
        var before = known.FirstOrDefault(c => c.Source == LyricSourceId.Lyrimuse);
        var report = previous with { LocalPrint = print };
        if (LyricTimingRules.SameResult(before?.Result, result.Candidate?.Result))
        {
            return report;
        }
        var found = known.Where(c => c.Source != LyricSourceId.Lyrimuse).ToList();
        if (result.Candidate is { } candidate)
        {
            found.Add(candidate);
        }
        var screened = LyricRanking.Screen(found, query, options);
        var best = LyricRanking.Choose(screened.Accepted, query, options);
        // Only the local file changed: switch when it wins now or was what showed.
        var localWasShown = before is not null && LyricTimingRules.SameResult(before.Result, previous.Result);
        var shown = best?.Source == LyricSourceId.Lyrimuse || localWasShown
            ? best
            : known.FirstOrDefault(c => LyricTimingRules.SameResult(c.Result, previous.Result));
        var states = new Dictionary<LyricSourceId, string>(previous.States) { [LyricSourceId.Lyrimuse] = result.State };
        report = report with
        {
            Candidates = screened.Accepted,
            Rejected = screened.Rejected,
            States = LyricRanking.Annotate(states, screened, shown?.Source, query),
        };
        if (!LyricTimingRules.SameResult(shown?.Result, previous.Result))
        {
            report = report with { Result = shown?.Result, Revision = previous.Revision + 1 };
        }
        return report;
    }

    private TimeSpan LimitFor(LyricSourceId id) => id == LyricSourceId.Lrclib ? _timing.LrclibLimit : _timing.SourceLimit;

    /// <summary>One source's answer, or why there is none.</summary>
    private sealed record SourceOutcome(LyricSourceId Id, LyricCandidate? Candidate, string State, bool Offline = false, bool Answered = false);

    private abstract record LookupEvent;

    private sealed record OutcomeEvent(SourceOutcome Outcome) : LookupEvent;

    private sealed record GraceEvent : LookupEvent;

    private sealed record SettleEvent(int Token) : LookupEvent;

    /// <summary>Apple Music took too long; the services are asked anyway.</summary>
    private sealed record OnlineDueEvent : LookupEvent;

    /// <summary>What a lookup's collection gathered, and which sources it asked or stopped.</summary>
    private sealed record Collected(LyricLookupReport Report, HashSet<LyricSourceId> Answered, HashSet<LyricSourceId> Stopped,
        HashSet<LyricSourceId> Asked);

    /// <summary>
    /// One fetch from one source, bounded by the source's time limit. Exactly one outcome is delivered: the answer,
    /// "Timed out", "Cancelled" (stopped, or the lookup ended), or the failure.
    /// </summary>
    private sealed class SourceRun
    {
        private readonly LyricLookup _owner;
        private readonly LyricQuery _query;
        private readonly string _folder;
        private readonly CancellationTokenSource _cts;
        private readonly Action<SourceOutcome> _deliver;
        private int _finished;
        private ITimer? _limit;
        private CancellationTokenRegistration _registration;

        public SourceRun(LyricLookup owner, LyricSourceId id, LyricQuery query, string folder, CancellationToken lookup,
            Action<SourceOutcome> deliver)
        {
            _owner = owner;
            Id = id;
            _query = query;
            _folder = folder;
            _deliver = deliver;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(lookup);
        }

        public LyricSourceId Id { get; }

        public void Start()
        {
            _limit = _owner._time.CreateTimer(_ => Finish(new SourceOutcome(Id, null, TimedOut)), null,
                _owner.LimitFor(Id), Timeout.InfiniteTimeSpan);
            _registration = _cts.Token.UnsafeRegister(_ => Finish(new SourceOutcome(Id, null, Cancelled)), null);
            _ = RunAsync();
        }

        /// <summary>Stops the fetch; its answer, if it still comes, is not delivered.</summary>
        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished and cleaned up.
            }
        }

        private async Task RunAsync()
        {
            try
            {
                var candidate = await _owner._fetch(Id, _query, _folder, _cts.Token).ConfigureAwait(false);
                Finish(new SourceOutcome(Id, candidate, candidate is null ? NoUsableMatch : MatchFound, Answered: true));
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                Finish(new SourceOutcome(Id, null, Cancelled));
            }
            catch (Exception ex)
            {
                Finish(new SourceOutcome(Id, null, ex.Message, Offline: LyricSourceException.IsOfflineError(ex)));
            }
            finally
            {
                // The fetch no longer holds the token.
                _registration.Unregister();
                _cts.Dispose();
            }
        }

        private void Finish(SourceOutcome outcome)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0)
            {
                return;
            }
            _limit?.Dispose();
            if (outcome.State is TimedOut)
            {
                // The fetch is abandoned: let it know.
                Cancel();
            }
            _deliver(outcome);
        }
    }

    /// <summary>The state of one progressive lookup, driven by a single event loop.</summary>
    private sealed class Session
    {
        private readonly LyricLookup _owner;
        private readonly LyricQuery _query;
        private readonly LyricSourceSnapshot _options;
        private readonly List<LyricSourceId> _local;
        private readonly List<LyricSourceId> _online;
        private readonly LyricResult? _pinned;
        private readonly bool _automatic;
        private readonly string? _localPrint;
        private readonly Func<LyricLookupReport, ValueTask>? _onUpdate;
        private readonly CancellationToken _cancellation;
        private readonly Channel<LookupEvent> _events = Channel.CreateUnbounded<LookupEvent>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Dictionary<LyricSourceId, LyricCandidate> _found = [];
        private readonly Dictionary<LyricSourceId, string> _states;
        private readonly HashSet<LyricSourceId> _pending;
        private readonly HashSet<LyricSourceId> _answered = [];
        private readonly HashSet<LyricSourceId> _stopped = [];
        private readonly Dictionary<LyricSourceId, SourceRun> _running = [];
        private readonly List<SourceRun> _retries = [];
        private readonly List<ITimer> _timers = [];
        private readonly LyricPublication _publication;
        private bool _offline;
        private bool _closed;

        public Session(LyricLookup owner, LyricQuery query, LyricSourceSnapshot options, List<LyricSourceId> local, List<LyricSourceId> online,
            List<LyricCandidate> seeded, Dictionary<LyricSourceId, string> states, LyricResult? pinned, bool automatic, string? localPrint,
            Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellation)
        {
            _owner = owner;
            _query = query;
            _options = options;
            _local = local;
            _online = online;
            _pinned = pinned;
            _automatic = automatic;
            _localPrint = localPrint;
            _onUpdate = onUpdate;
            _cancellation = cancellation;
            foreach (var candidate in seeded)
            {
                _found[candidate.Source] = candidate;
            }
            _states = states;
            _pending = [.. local, .. online];
            foreach (var id in _pending)
            {
                _states[id] = Searching;
            }
            _publication = new LyricPublication(options, query, locked: pinned is not null);
        }

        public async Task<Collected> RunAsync()
        {
            var sources = _local.Concat(_online).ToList();
            var retryApple = _automatic && _local.Contains(LyricSourceId.AppleMusicCache);
            // An automatic lookup reads Apple Music's lyrics before asking any service: a hit there needs no network.
            // The services are asked once Apple Music has answered, or after a short wait.
            var musicFirst = retryApple && _online.Count > 0;
            try
            {
                Start(musicFirst ? _local : sources);
                bool onlineStarted = !musicFirst, musicAnswered = false, onlineDue = false;
                if (musicFirst)
                {
                    Schedule(new OnlineDueEvent(), _owner._timing.MusicFirst);
                }
                if (_pinned is null && sources.Count > 0)
                {
                    Schedule(new GraceEvent(), _owner._timing.Grace);
                }
                bool graceOver = false, settleOver = false, settleArmed = false;
                var settleToken = 0;
                // Apple Music's second look is an upgrade opportunity; it never holds back the first result, so it
                // is not counted as pending.
                bool appleRetried = false, appleRetrying = false;
                while (_pending.Count > 0 || appleRetrying)
                {
                    var next = await NextAsync().ConfigureAwait(false);
                    switch (next)
                    {
                        case OutcomeEvent { Outcome: var outcome } when _stopped.Contains(outcome.Id):
                            // Stopped: what it says can no longer change anything.
                            continue;
                        case OutcomeEvent { Outcome: var outcome } when outcome.Id == LyricSourceId.AppleMusicCache && outcome.Candidate is null
                            && retryApple && !appleRetried && !_cancellation.IsCancellationRequested:
                            musicAnswered = true;
                            appleRetried = true;
                            appleRetrying = true;
                            _pending.Remove(LyricSourceId.AppleMusicCache);
                            _states[LyricSourceId.AppleMusicCache] = CheckingAppleAgain;
                            ScheduleAppleRetry();
                            break;
                        case OutcomeEvent { Outcome: var outcome }:
                            if (outcome.Id == LyricSourceId.AppleMusicCache)
                            {
                                musicAnswered = true;
                                if (appleRetried)
                                {
                                    appleRetrying = false;
                                }
                            }
                            _pending.Remove(outcome.Id);
                            _states[outcome.Id] = outcome.State;
                            if (outcome.Candidate is { } candidate)
                            {
                                _found[outcome.Id] = candidate;
                            }
                            if (outcome.Answered)
                            {
                                _answered.Add(outcome.Id);
                            }
                            _offline |= outcome.Offline;
                            break;
                        case GraceEvent:
                            graceOver = true;
                            break;
                        case SettleEvent { Token: var token }:
                            settleOver = settleOver || token == settleToken;
                            break;
                        case OnlineDueEvent:
                            onlineDue = true;
                            break;
                    }
                    var screened = Screen();
                    var best = LyricRanking.Choose(screened.Accepted, _query, _options);
                    var settled = LyricRanking.IsSettled(best, _pending, _query, _options);
                    switch (_publication.Step(best, settled, graceOver, settleOver))
                    {
                        case PublicationStep.Publish:
                            settleArmed = false;
                            settleOver = false;
                            settleToken++;
                            break;
                        case PublicationStep.HoldForUpgrade when !settleArmed:
                            settleArmed = true;
                            settleToken++;
                            Schedule(new SettleEvent(settleToken), _owner._timing.UpgradeSettle);
                            break;
                    }
                    // Stop early once a source's answer could no longer change what shows or what replays use:
                    // everything stops only once the best possible match is in hand. A search asks every source, so
                    // all the alternatives are listed.
                    if (_automatic && _publication.Shown is not null && best is not null)
                    {
                        var onlineBest = LyricRanking.Choose(screened.Accepted.Where(c => !c.Source.IsLocal()), _query, _options);
                        var idle = _pending.Where(id => !LyricRanking.Matters(id, best, onlineBest, _query, _options)).ToList();
                        if (idle.Count > 0)
                        {
                            Stop(idle, best.Source == LyricSourceId.AppleMusicCache ? AppleMusicShowing : CouldNotBeat);
                        }
                    }
                    if (!onlineStarted && (musicAnswered || onlineDue))
                    {
                        onlineStarted = true;
                        Start(_online);
                    }
                    if ((_pending.Count > 0 || appleRetrying) && !_cancellation.IsCancellationRequested && _onUpdate is not null)
                    {
                        await _onUpdate(Report(screened, complete: false)).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _closed, true);
                foreach (var timer in _timers)
                {
                    timer.Dispose();
                }
                List<SourceRun> retries;
                lock (_retries)
                {
                    retries = [.. _retries];
                }
                foreach (var run in _running.Values.Concat(retries))
                {
                    run.Cancel();
                }
            }
            var final = Screen();
            // Every source has answered or been stopped, so whatever is best now is settled.
            _publication.Step(LyricRanking.Choose(final.Accepted, _query, _options), settled: true, graceOver: true, settleOver: true);
            _offline = _offline || !(_owner._connectivity?.IsOnline ?? true);
            return new Collected(Report(final, complete: true), _answered, _stopped, [.. _running.Keys]);
        }

        private LyricScreening Screen() =>
            LyricRanking.Screen(_found.Values.OrderBy(c => _options.OrderOf(c.Source)).ThenBy(c => c.Source), _query, _options);

        private LyricLookupReport Report(LyricScreening screened, bool complete) => new()
        {
            Result = _publication.Shown?.Result ?? _pinned,
            Candidates = screened.Accepted,
            States = LyricRanking.Annotate(_states, screened, _publication.Shown?.Source, _query),
            Pinned = _pinned is not null,
            Rejected = screened.Rejected,
            Revision = _publication.Revision,
            IsComplete = complete,
            Offline = _offline,
            LocalPrint = _localPrint,
        };

        private async ValueTask<LookupEvent> NextAsync()
        {
            var reader = _events.Reader;
            if (reader.TryRead(out var next))
            {
                return next;
            }
            // A fake clock moves to its next timer while nothing is queued; a timer may only start more work.
            while (_owner.WhenIdle is { } idle && idle())
            {
                if (reader.TryRead(out next))
                {
                    return next;
                }
            }
            return await reader.ReadAsync(_cancellation).ConfigureAwait(false);
        }

        private void Post(LookupEvent next) => _events.Writer.TryWrite(next);

        /// <summary>Each source runs on its own, so one can be stopped on its own. A cancelled lookup asks nothing more.</summary>
        private void Start(IEnumerable<LyricSourceId> ids)
        {
            foreach (var id in ids)
            {
                if (_running.ContainsKey(id) || _stopped.Contains(id) || _cancellation.IsCancellationRequested)
                {
                    continue;
                }
                var run = new SourceRun(_owner, id, _query, _options.LocalFolder, _cancellation, outcome => Post(new OutcomeEvent(outcome)));
                _running[id] = run;
                run.Start();
            }
        }

        private void Stop(IEnumerable<LyricSourceId> ids, string reason)
        {
            foreach (var id in ids)
            {
                _stopped.Add(id);
                _pending.Remove(id);
                _states[id] = reason;
                if (_running.TryGetValue(id, out var run))
                {
                    run.Cancel();
                }
            }
        }

        private void Schedule(LookupEvent next, TimeSpan after) =>
            _timers.Add(_owner._time.CreateTimer(_ => Post(next), null, after < TimeSpan.Zero ? TimeSpan.Zero : after, Timeout.InfiniteTimeSpan));

        /// <summary>Asks Apple Music again after a short wait: it may have finished loading the lyrics by then.</summary>
        private void ScheduleAppleRetry()
        {
            _timers.Add(_owner._time.CreateTimer(_ =>
            {
                if (Volatile.Read(ref _closed))
                {
                    return;
                }
                var run = new SourceRun(_owner, LyricSourceId.AppleMusicCache, _query, _options.LocalFolder, _cancellation, outcome =>
                {
                    if (outcome.Candidate is null && outcome.State != Cancelled)
                    {
                        outcome = outcome with { State = AppleStillMissing };
                    }
                    Post(new OutcomeEvent(outcome));
                });
                lock (_retries)
                {
                    _retries.Add(run);
                }
                run.Start();
            }, null, _owner._timing.AppleRetry, Timeout.InfiniteTimeSpan));
        }
    }
}

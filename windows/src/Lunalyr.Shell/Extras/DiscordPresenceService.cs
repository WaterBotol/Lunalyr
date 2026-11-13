// Discord status's running part, ported from the Mac's DiscordPresence class (start, stop, update, the tick, what is
// playing, and the connection status; DiscordPresence.swift, Lunalyr's own lines). Listen-along's join handling isn't
// ported: Windows has no listen-along.

using Lunalyr.Core.Extras;
using Lunalyr.Core.Logging;

namespace Lunalyr.Shell.Extras;

/// <summary>
/// Shows what is playing on the user's Discord profile (Rich Presence) through <see cref="DiscordTransport"/>. Nothing is
/// sent until <see cref="Start"/> (the setting is off by default); the activity is cleared when playback stops (unless
/// "Keep showing while paused"), when the setting is switched off and when Lunalyr quits.
/// </summary>
public sealed class DiscordPresenceService : IAsyncDisposable
{
    private readonly Func<ExtrasNow> _read;
    private readonly Func<DiscordOptions> _options;
    private readonly DiscordTransport _transport;
    private readonly CatalogueMatches _catalogue;
    private readonly TimeProvider _time;
    private readonly CategoryLogger _log;
    private readonly int _pid;
    private readonly Lock _gate = new();
    private readonly DiscordUpdateBudget _budget = new();

    private bool _running;
    private ITimer? _timer;
    private TimeSpan _timerInterval;
    private bool _sentBefore;
    private DiscordPlaying? _lastSent;
    private (long Start, string TrackKey)? _anchor;
    private DiscordConnectionStatus _status = new DiscordConnectionStatus.Off();
    private int _nonce;

    public DiscordPresenceService(Func<ExtrasNow> read, Func<DiscordOptions> options, DiscordTransport transport,
        CatalogueMatches? catalogue = null, TimeProvider? timeProvider = null, Logger? logger = null, int? processId = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        _read = read;
        _options = options;
        _transport = transport;
        _catalogue = catalogue ?? new CatalogueMatches(null);
        _time = timeProvider ?? TimeProvider.System;
        _log = (logger ?? Logger.Null).App;
        _pid = processId ?? Environment.ProcessId;
        _transport.OnEvent = Handle;
        _catalogue.Changed += Update;
    }

    /// <summary>Whether Discord is there for the presence (for Settings).</summary>
    public DiscordConnectionStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>Raised (on any thread) when <see cref="Status"/> changes.</summary>
    public event Action? StatusChanged;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _status = new DiscordConnectionStatus.WaitingForDiscord();
        }
        _log.Info("Discord status is on: showing what plays once the Discord app answers.");
        StatusChanged?.Invoke();
        _transport.StartListening();
        Update();
    }

    /// <summary>Clears the activity and lets Discord go.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            _timer?.Dispose();
            _timer = null;
            _timerInterval = TimeSpan.Zero;
            _transport.StopListening();
            _transport.Send(DiscordPresence.SetActivityPayload(null, _pid, NextNonce()), thenDisconnect: true);
            _sentBefore = false;
            _lastSent = null;
            _anchor = null;
            _status = new DiscordConnectionStatus.Off();
        }
        _log.Info("Discord status is off: the activity was cleared.");
        StatusChanged?.Invoke();
    }

    /// <summary>Something that may change the activity happened (the song, playback, the lyrics, the settings).</summary>
    public void Update()
    {
        if (!IsRunning) return;
        // Read outside the gate: the controller takes its own lock, and its events call in here from its threads.
        ExtrasNow now;
        try
        {
            now = _read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Debug($"Discord status: couldn't read the song ({ex.Message}).");
            now = ExtrasNow.Nothing;
        }
        var options = _options();
        lock (_gate)
        {
            if (!_running) return;
            var heldBack = !SendIfChanged(now, options);
            ScheduleTimer(DiscordPresence.TickFor(now.Playing, heldBack));
        }
    }

    /// <summary>Stops (clearing the activity) and waits, briefly, for the clear to reach Discord.</summary>
    public async ValueTask DisposeAsync()
    {
        _catalogue.Changed -= Update;
        Stop();
        await _transport.FlushAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private void Handle(DiscordEvent evt)
    {
        lock (_gate)
        {
            if (!_running) return;
            switch (evt)
            {
                case DiscordEvent.Ready ready:
                    _status = new DiscordConnectionStatus.Connected(ready.UserName);
                    break;
                case DiscordEvent.Disconnected:
                    _status = new DiscordConnectionStatus.WaitingForDiscord();
                    break;
                default:
                    return;
            }
        }
        StatusChanged?.Invoke();
    }

    private string NextNonce() => $"{Guid.NewGuid():N}-{Interlocked.Increment(ref _nonce)}";

    /// <summary>(Re)starts the tick at <paramref name="interval"/> when it differs from the running one.</summary>
    private void ScheduleTimer(TimeSpan interval)
    {
        if (!_running || interval == _timerInterval) return;
        _timer?.Dispose();
        _timerInterval = interval;
        _timer = _time.CreateTimer(_ => Update(), null, interval, interval);
    }

    /// <summary>Sends the activity if it changed; false when a change is held back for Discord's rate limit.</summary>
    private bool SendIfChanged(ExtrasNow current, DiscordOptions options)
    {
        var playing = CurrentPlaying(current, options);
        if (_sentBefore && _lastSent == playing) return true;
        // Within Discord's limit; a new song, a pause or a clear goes first. Held back, the latest goes on a later tick.
        var now = _time.GetUtcNow();
        if (!_budget.Allows(DiscordPresence.IsUrgent(_sentBefore, _lastSent, playing), now)) return false;
        _budget.Record(now);
        _sentBefore = true;
        _lastSent = playing;
        if (playing is null) _anchor = null;
        _transport.Send(DiscordPresence.SetActivityPayload(playing is null ? null : DiscordPresence.Activity(playing), _pid, NextNonce()));
        return true;
    }

    /// <summary>What Discord shows now, or null for nothing (no song, or paused without "Keep showing while paused").</summary>
    private DiscordPlaying? CurrentPlaying(ExtrasNow now, DiscordOptions options)
    {
        if (!now.HasTrack || !(now.Playing || options.ShowsWhenPaused)) return null;
        var title = now.Title!;
        var position = double.IsFinite(now.Position) ? now.Position : 0;
        var unix = _time.GetUtcNow().ToUnixTimeSeconds();
        var key = $"{title}|{now.Artist}|{now.Album ?? ""}";
        var start = unix - (long)position;
        // Keep the bar still unless the song was scrubbed or restarted.
        if (_anchor is { } anchor && anchor.TrackKey == key && Math.Abs(anchor.Start - start) <= 3) start = anchor.Start;
        _anchor = (start, key);
        long? end = now.Duration is > 0 and var duration ? start + (long)duration : null;
        var line = now.Playing && !now.WallpaperPaused
            ? DiscordPresence.Line(now.Lines, now.LyricTime + options.LyricLead, now.Replacements)
            : null;
        // Windows' media sessions carry no Spotify track ID: the link always comes from Apple's catalogue.
        const string? spotifyUri = null;
        var found = DiscordPresence.NeedsAppleSearch(options, spotifyUri) ? _catalogue.Match(title, now.Artist, now.Album) : null;
        return new DiscordPlaying
        {
            Title = title,
            Artist = now.Artist,
            Album = now.Album,
            Line = line,
            Start = start,
            End = end,
            Paused = !now.Playing,
            CoverUrl = options.ShowsCover ? found?.Cover : null,
            SongUrl = options.ShowsSongButton ? DiscordPresence.SongLink(spotifyUri, found?.TrackId) : null,
            Options = options,
        };
    }
}

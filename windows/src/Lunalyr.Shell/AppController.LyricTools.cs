// The lyric tools of Settings › Timing and the tray flyout: Tap to sync, searching for other lyrics, using one of them
// for the song (a pin), importing an LRC or TTML file, and going back to automatic. Searching follows the Mac's
// LyricsViewModel.refresh(force:title:artist:) and the Timing page (Lunalyr's own lines). Choosing, importing and
// forgetting a choice (LyricsViewModel.select, importLyrics, forgetSelection) sit on lines that trace back to the
// squashed Verso import and are re-implemented here from their behaviour: an explicit choice stops whatever the lookup
// was doing, shows at once, and is saved for the recording, never for the words typed into the search.

using System.Diagnostics;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Shell.LyricTools;

namespace Lunalyr.Shell;

/// <summary>What Settings › Timing shows about the song's lyrics: the matches found, and the result of the last action.</summary>
public sealed record LyricToolsState
{
    public static LyricToolsState Empty { get; } = new();

    /// <summary>Accepted matches, best first, from the last lookup or search.</summary>
    public IReadOnlyList<LyricCandidate> Candidates { get; init; } = [];

    /// <summary>Close matches that failed the recording check, with the reason.</summary>
    public IReadOnlyList<LyricRejection> Rejected { get; init; } = [];

    /// <summary>What each source said (the ranking's source-list labels).</summary>
    public IReadOnlyDictionary<LyricSourceId, string> States { get; init; } = new Dictionary<LyricSourceId, string>();

    /// <summary>The lyrics showing are the user's saved choice for this recording.</summary>
    public bool Pinned { get; init; }

    /// <summary>A search is running.</summary>
    public bool IsSearching { get; init; }

    /// <summary>Lyrics are showing while slower sources are still answering.</summary>
    public bool IsRefining { get; init; }

    /// <summary>A calm note about the last action ("Saved as your choice for this recording."), or null.</summary>
    public string? Note { get; init; }

    /// <summary>Why a file couldn't be imported, or null.</summary>
    public string? Warning { get; init; }

    /// <summary>Some sources weren't asked further because they couldn't have beaten the match showing.</summary>
    public bool SourcesStoppedEarly => States.Values.Any(state => state.StartsWith(LyricRanking.NotNeeded, StringComparison.Ordinal));
}

public sealed partial class AppController
{
    private LyricToolsState _tools = LyricToolsState.Empty;
    private LyricTapSyncController? _tapSync;

    /// <summary>The matches found for the song playing and the note from the last action.</summary>
    public LyricToolsState LyricTools
    {
        get { lock (_gate) return _tools; }
    }

    /// <summary>The song playing, as lyrics are looked up for it, or null while there is none (or no length yet).</summary>
    public LyricQuery? CurrentQuery
    {
        get { lock (_gate) return _query; }
    }

    /// <summary>Why searching can't run in this process (<c>--no-network</c>), or null when it can.</summary>
    public string? SearchDisabledReason => _lyrics.DisabledReason;

    // ---- Searching ----

    /// <summary>
    /// Asks every enabled source for <paramref name="title"/> and <paramref name="artist"/> (the song's own where left
    /// blank), for the song playing: the matches are listed in <see cref="LyricTools"/>, and the best one shows, as
    /// after any lookup. False when no song is playing or lookups are off for this run.
    /// </summary>
    public bool SearchLyrics(string? title = null, string? artist = null)
    {
        LyricQuery query, asked;
        long generation;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed || _query is not { } current || _lyrics.DisabledReason is not null) return false;
            generation = RestartLocked();
            cts = new CancellationTokenSource();
            _lookupCts = cts;
            query = current;
            asked = new LyricQuery(Blank(title) ? current.Title : title!.Trim(), Blank(artist) ? current.Artist : artist!.Trim(),
                current.Album, current.Duration) { Genre = current.Genre };
            _tools = _tools with { IsSearching = true, IsRefining = false, Note = null, Warning = null };
            _status = _status with { Status = StatusText.LookingUp, Lookups = _status.Lookups + 1 };
        }

        _lyricLog.Info(asked.Title == query.Title && asked.Artist == query.Artist
            ? "Searching every source for the song playing."
            : "Searching every source under the title and artist typed in Settings.");
        RaiseStatusChanged();
        Track(RunSearchAsync(generation, query, asked, cts));
        return true;

        static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);
    }

    private async Task RunSearchAsync(long generation, LyricQuery query, LyricQuery asked, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await Task.Yield();
            await LookUpAndShowAsync(generation, query, asked, search: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A choice, another search or a new song took over.
        }
        catch (Exception ex)
        {
            _lyricLog.Error("The lyric search failed.", ex);
            ShowStatus(generation, StatusText.LookupFailed);
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation) _tools = _tools with { IsSearching = false, IsRefining = false };
                if (ReferenceEquals(_lookupCts, cts)) _lookupCts = null;
            }

            cts.Dispose();
            RaiseStatusChanged();
        }
    }

    // ---- Choosing ----

    /// <summary>
    /// Shows <paramref name="candidate"/>'s lyrics for the song playing at once and saves them as its lyrics from now on
    /// (a pin), whatever the sources say later. False when no song is playing.
    /// </summary>
    public bool UseLyrics(LyricCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Choose(candidate.Result, LyricToolsText.Chosen, $"Using {candidate.Provenance.Label} for this song (your choice).");
    }

    /// <summary>Forgets the saved choice for the song playing and asks every source again.</summary>
    public bool ReturnToAutomatic()
    {
        LyricQuery? query;
        lock (_gate) query = _query;
        if (query is null) return false;
        _lyrics.Unpin(query);
        lock (_gate) _tools = _tools with { Pinned = false, Note = null, Warning = null };
        _lyricLog.Info("Forgot the saved lyrics for this song: choosing automatically again.");
        if (!SearchLyrics()) RaiseStatusChanged();
        return true;
    }

    // ---- Importing ----

    /// <summary>
    /// Reads an LRC or TTML file of up to 1 MB and uses it for the song playing, as <see cref="ImportLyrics"/> does.
    /// False, with a <see cref="LyricToolsState.Warning"/>, when the file can't be used.
    /// </summary>
    public bool ImportLyricsFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        LyricDecodeResult decoded;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > LyricToolsText.ImportLimit)
            {
                decoded = new LyricDecodeResult(null, LyricDecodeFailure.TooLarge);
            }
            else
            {
                decoded = LyricTextDecoder.Lyrics(File.ReadAllBytes(path), LyricToolsText.ImportLimit);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            decoded = new LyricDecodeResult(null, LyricDecodeFailure.Unopenable);
        }

        if (decoded.Text is { } text) return ImportLyrics(text);
        var warning = LyricDecodeResult.Message(decoded.Failure ?? LyricDecodeFailure.NoTimestamps);
        lock (_gate) _tools = _tools with { Warning = warning, Note = null };
        _lyricLog.Info($"A lyric file couldn't be imported: {warning}");
        RaiseStatusChanged();
        return false;
    }

    /// <summary>
    /// Uses LRC or TTML text (TTML when it starts with "&lt;") for the song playing, saved as its lyrics from now on.
    /// False when no song is playing or the text has no usable timestamps.
    /// </summary>
    public bool ImportLyrics(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        LyricQuery? query;
        lock (_gate) query = _query;
        if (query is null) return false;
        var parsed = text.TrimStart().StartsWith('<')
            ? TtmlParser.Parse(text, query.Duration, distribute: _lyrics.WordDistributor)
            : LrcParser.Parse(text, query.Duration);
        if (parsed.Count == 0)
        {
            lock (_gate) _tools = _tools with { Note = LyricToolsText.NoTimestamps, Warning = null };
            RaiseStatusChanged();
            return false;
        }

        var result = new LyricResult.Synced(parsed, new LyricProvenance(LyricTimingRules.Precision(parsed), LyricToolsText.ImportedSource));
        return Choose(result, LyricToolsText.Imported, $"Using imported lyrics for this song ({parsed.Count} lines).");
    }

    /// <summary>Stops the lookup, shows <paramref name="result"/> for the song playing and saves it as its lyrics.</summary>
    private bool Choose(LyricResult result, Func<bool, string> note, string log)
    {
        LyricQuery query;
        long generation;
        bool repair;
        lock (_gate)
        {
            if (_disposed || _query is not { } current) return false;
            generation = RestartLocked();
            query = current;
            repair = _settings.RepairQQGaps;
            _tools = _tools with { IsSearching = false, IsRefining = false };
        }

        Show(generation, query, result, repair, complete: true);
        var saved = _lyrics.Pin(query, result);
        lock (_gate)
        {
            if (generation == _generation) _tools = _tools with { Pinned = saved, Note = note(saved), Warning = null };
        }

        _lyricLog.Info(log + (saved ? "" : " It couldn't be saved."));
        RaiseStatusChanged();
        return true;
    }

    // Must hold _gate. A new generation for the song playing: whatever its lookup was doing is dropped, but the song,
    // its offsets and the lyrics showing stay.
    private long RestartLocked()
    {
        _generation++;
        _lookupCts?.Cancel();
        _lookupCts = null;
        _retryWhenOnline = false;
        Interlocked.Increment(ref _swapTicket);
        return _generation;
    }

    /// <summary>Keeps a lookup's report for Settings › Timing (the matches, what each source said, a saved choice).</summary>
    private void KeepReport(long generation, LyricLookupReport report)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            _tools = _tools with
            {
                Candidates = report.Candidates,
                Rejected = report.Rejected,
                States = report.States,
                Pinned = report.Pinned,
                IsRefining = !report.IsComplete && report.Revision > 0,
            };
        }

        RaiseStatusChanged();
    }

    /// <summary>A new song: nothing found for the last one is shown for this one.</summary>
    private void ForgetLyricTools()
    {
        lock (_gate) _tools = LyricToolsState.Empty;
    }

    // ---- Tap to sync ----

    /// <summary>
    /// Tap to sync for the song playing (the Mac's <c>LyricTapSyncController</c>, headless): taps are timed against the
    /// player's own position, never sent to it, and the offset is saved in the song offsets for the lyric source on
    /// screen. Use from the UI thread only.
    /// </summary>
    public LyricTapSyncController TapSync => _tapSync ??= new LyricTapSyncController(_offsets, TapContext(),
        () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
        (text, _) =>
        {
            _lyricLog.Info(text + (text.EndsWith('.') ? "" : "."));
            RaiseStatusChanged();
        });

    /// <summary>
    /// What Tap to sync reads: the recording, the sung lines' starts of the lyrics on screen, and the player's own
    /// position and state (without the offsets, which <see cref="LyricTapSync.TapTime"/> adds itself). Read only.
    /// </summary>
    public LyricTapContext TapContext() => new(
        RecordingKey: () => Volatile.Read(ref _recordingKey),
        LineStarts: () =>
        {
            LyricResult? shown;
            lock (_gate) shown = _shown;
            return shown is LyricResult.Synced synced ? LyricTapSync.LineStarts(synced.Lines) : [];
        },
        IsPlaying: () => Volatile.Read(ref _hasSession) != 0 && Monitor.Clock.Read() is { HasTimeline: true, IsPlaying: true },
        PlaybackTime: () => Monitor.Clock.Read().Position.TotalSeconds,
        GlobalOffset: () => Volatile.Read(ref _globalOffset));
}

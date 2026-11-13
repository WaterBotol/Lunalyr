// Ported from the Mac's MusicVideoSearch.swift (MusicVideoSearchRow, MusicVideoSearchModel; Lunalyr's own code). Search
// YouTube…: the user searches for the playing song and picks the upload to use. Thumbnails only: nothing plays here. A pick
// goes through the pasted-link path. On Windows the picker sits in Settings › Wallpaper › Music video, and a row's still is
// the one the Data API returned (the Mac builds an i.ytimg.com address by hand).

using System.Globalization;

namespace Lunalyr.Core.MusicVideo;

/// <summary>One result as the picker shows it.</summary>
public sealed record MusicVideoSearchRow
{
    public const string SameLengthHint = "Same length as the song";
    public const string PlayingNowLabel = "Playing now";
    public const string VerifiedLabel = "Verified artist";

    /// <summary>Results shown at most.</summary>
    public const int Limit = 20;

    public MusicVideoSearchRow(YouTubeVideoResult result, double? songDuration, string? playing)
    {
        VideoId = result.VideoId;
        Title = result.Title.Length == 0 ? "Untitled video" : result.Title;
        Channel = result.Channel;
        VerifiedArtist = result.VerifiedArtist;
        DurationText = Duration(result.Duration);
        Details = DetailsOf(result.Views, result.Uploaded);
        SameLength = IsSameLength(result.Duration, songDuration);
        PlayingNow = playing == result.VideoId;
        ThumbnailUrl = result.ThumbnailUrl;
    }

    public string Id => VideoId;
    public string VideoId { get; }
    public string Title { get; }
    public string Channel { get; }
    public bool VerifiedArtist { get; }

    /// <summary>"3:45", for the badge on the thumbnail.</summary>
    public string? DurationText { get; }

    /// <summary>"12M views · 3 years ago", or whichever of the two is known.</summary>
    public string? Details { get; }

    /// <summary>Within <see cref="YouTubeSearch.LengthMatch"/> of the song's length.</summary>
    public bool SameLength { get; }

    /// <summary>The video on the wallpaper now.</summary>
    public bool PlayingNow { get; }

    /// <summary>The still the API returned (an image, never the video), or null.</summary>
    public string? ThumbnailUrl { get; }

    /// <summary>"0:07", "3:45", "1:02:03"; null without a usable length.</summary>
    public static string? Duration(double? seconds)
    {
        if (seconds is not { } s || !double.IsFinite(s) || s < 0) return null;
        var total = (int)Math.Round(s, MidpointRounding.AwayFromZero);
        int hours = total / 3600, minutes = total / 60 % 60, rest = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    public static string? DetailsOf(string? views, string? uploaded)
    {
        var parts = new[] { views, uploaded }.Select(p => p?.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    public static bool IsSameLength(double? video, double? song) =>
        video is { } v && song is { } s && s > 0 && double.IsFinite(v) && Math.Abs(v - s) <= YouTubeSearch.LengthMatch;

    /// <summary>What Narrator reads for the row.</summary>
    public string AccessibilityLabel
    {
        get
        {
            var parts = new List<string> { Title };
            if (Channel.Length > 0) parts.Add(VerifiedArtist ? $"{Channel}, {VerifiedLabel}" : Channel);
            if (DurationText is not null) parts.Add(DurationText);
            if (Details is not null) parts.Add(Details);
            if (SameLength) parts.Add(SameLengthHint);
            if (PlayingNow) parts.Add(PlayingNowLabel);
            return string.Join(", ", parts);
        }
    }

    /// <summary>The rows for <paramref name="results"/>: YouTube's order, each video once, the one playing now first, at most <paramref name="limit"/>.</summary>
    public static IReadOnlyList<MusicVideoSearchRow> Rows(IReadOnlyList<YouTubeVideoResult> results, double? songDuration,
        string? playing, int limit = Limit)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = results.Where(r => seen.Add(r.VideoId)).Select(r => new MusicVideoSearchRow(r, songDuration, playing)).ToList();
        var index = rows.FindIndex(r => r.PlayingNow);
        if (index > 0)
        {
            var row = rows[index];
            rows.RemoveAt(index);
            rows.Insert(0, row);
        }
        return rows.Take(Math.Max(0, limit)).ToList();
    }
}

/// <summary>What the picker needs from the music video controller; a seam for tests.</summary>
public interface IMusicVideoSearching
{
    MusicVideoSong? SearchSong { get; }
    string? ShowingYouTubeId { get; }
    string? PickedVideoId { get; }
    Task<MusicVideoSearchOutcome> SearchYouTubeAsync(string query, CancellationToken ct);
    bool UsePickedVideo(string videoId, string songKey);
    void ClearPastedLink();
}

/// <summary>The Search YouTube… picker's state: the song it is for, the query, the results and the keyboard selection.</summary>
public sealed class MusicVideoSearchModel(IMusicVideoSearching source)
{
    public enum SearchPhase
    {
        /// <summary>Nothing searched yet (or the song changed).</summary>
        Idle,
        Loading,
        Results,
        Empty,
        RateLimited,
        Failed,
        /// <summary>No song is playing.</summary>
        NoSong,
        /// <summary>Windows: no YouTube Data API key is set, or it was refused.</summary>
        NeedsKey,
    }

    public const string SongChangedNote = "The song changed. Search again for the one playing now.";

    private IReadOnlyList<YouTubeVideoResult> _results = [];
    private double? _songDuration;
    private CancellationTokenSource? _searchCts;
    private Task _searchTask = Task.CompletedTask;

    public string Query { get; set; } = "";
    public SearchPhase Phase { get; private set; } = SearchPhase.Idle;
    public IReadOnlyList<MusicVideoSearchRow> Rows { get; private set; } = [];
    public string? Selection { get; set; }

    /// <summary>A short note above the results (the song changed under a pick).</summary>
    public string? Notice { get; private set; }

    /// <summary>"“Blinding Lights” by The Weeknd".</summary>
    public string? SongLabel { get; private set; }

    public string? SongKey { get; private set; }

    /// <summary>The key was refused (rather than missing), for the NeedsKey phase's words.</summary>
    public bool KeyRejected { get; private set; }

    /// <summary>Closes the picker: after a pick, Back to automatic, or Close.</summary>
    public Action OnClose { get; set; } = () => { };

    /// <summary>Raised after the state changes (any thread).</summary>
    public event Action? Changed;

    /// <summary>"&lt;artist&gt; &lt;title&gt;": what the search field starts with.</summary>
    public static string Prefill(MusicVideoSong song) =>
        string.Join(" ", new[] { song.Artist.Trim(), song.Title.Trim() }.Where(s => s.Length > 0));

    public static string Label(MusicVideoSong song)
    {
        var artist = song.Artist.Trim();
        return artist.Length == 0 ? $"“{song.Title}”" : $"“{song.Title}” by {artist}";
    }

    /// <summary>Whether the playing song uses a pick (or pasted link) to go back from.</summary>
    public bool CanGoBackToAutomatic => SongKey is not null && source.SearchSong?.Key == SongKey && source.PickedVideoId is not null;

    /// <summary>
    /// Called each time the picker is shown: a new song (or one that failed or was never searched) is searched afresh; the
    /// same song keeps its results, with "Playing now" brought up to date.
    /// </summary>
    public void Prepare()
    {
        Notice = null;
        var song = source.SearchSong;
        if (song?.Key != SongKey || Phase is not (SearchPhase.Results or SearchPhase.Empty or SearchPhase.Loading))
        {
            Reset(song);
            if (song is not null) Search();
        }
        else
        {
            RebuildRows();
        }
        Changed?.Invoke();
    }

    private void Reset(MusicVideoSong? song)
    {
        _searchCts?.Cancel();
        SongKey = song?.Key;
        _songDuration = song?.Duration;
        SongLabel = song is null ? null : Label(song);
        Query = song is null ? "" : Prefill(song);
        _results = [];
        Rows = [];
        Selection = null;
        Phase = song is null ? SearchPhase.NoSong : SearchPhase.Idle;
    }

    /// <summary>Searches YouTube for the query (Enter in the field).</summary>
    public void Search()
    {
        if (SongKey is null)
        {
            Phase = SearchPhase.NoSong;
            Changed?.Invoke();
            return;
        }
        var text = Query.Trim();
        if (text.Length == 0) return;
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        Notice = null;
        _results = [];
        Rows = [];
        Selection = null;
        Phase = SearchPhase.Loading;
        Changed?.Invoke();
        _searchTask = RunAsync(text, cts);
    }

    private async Task RunAsync(string text, CancellationTokenSource cts)
    {
        MusicVideoSearchOutcome outcome;
        try
        {
            outcome = await source.SearchYouTubeAsync(text, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested) return;
        Show(outcome);
    }

    private void Show(MusicVideoSearchOutcome outcome)
    {
        switch (outcome)
        {
            case MusicVideoSearchOutcome.Results found:
                _results = found.Videos;
                RebuildRows();
                Selection = Rows.FirstOrDefault()?.Id;
                Phase = Rows.Count == 0 ? SearchPhase.Empty : SearchPhase.Results;
                break;
            case MusicVideoSearchOutcome.RateLimited:
                Phase = SearchPhase.RateLimited;
                break;
            case MusicVideoSearchOutcome.Failed:
                Phase = SearchPhase.Failed;
                break;
            case MusicVideoSearchOutcome.NeedsKey key:
                KeyRejected = key.Rejected;
                Phase = SearchPhase.NeedsKey;
                break;
            default:
                return;
        }
        Changed?.Invoke();
    }

    private void RebuildRows()
    {
        Rows = MusicVideoSearchRow.Rows(_results, _songDuration, source.ShowingYouTubeId);
        if (Selection is { } selected && Rows.All(r => r.Id != selected)) Selection = Rows.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// Uses the video for the song and closes. When another song has started since the search, nothing is used: the picker
    /// moves to the new song and says so.
    /// </summary>
    public void Pick(string videoId)
    {
        if (SongKey is not { } key) return;
        if (source.UsePickedVideo(videoId, key))
        {
            Close();
        }
        else if (source.SearchSong?.Key != key)
        {
            Reset(source.SearchSong);
            Notice = SongChangedNote;
            Changed?.Invoke();
        }
    }

    public void PickSelection()
    {
        if (Selection is { } selected) Pick(selected);
    }

    /// <summary>Up and down through the rows (keyboard), staying at either end.</summary>
    public void MoveSelection(int delta)
    {
        if (Rows.Count == 0) return;
        var current = Selection is { } id ? Rows.ToList().FindIndex(r => r.Id == id) : -1;
        var next = current >= 0 ? Math.Clamp(current + delta, 0, Rows.Count - 1) : delta < 0 ? Rows.Count - 1 : 0;
        Selection = Rows[next].Id;
        Changed?.Invoke();
    }

    /// <summary>Stops using the pick for the song and lets Lunalyr choose again.</summary>
    public void BackToAutomatic()
    {
        if (!CanGoBackToAutomatic) return;
        source.ClearPastedLink();
        Close();
    }

    public void Close()
    {
        Stop();
        OnClose();
    }

    /// <summary>The picker closed: any search in flight is dropped.</summary>
    public void Stop()
    {
        _searchCts?.Cancel();
        if (Phase == SearchPhase.Loading) Phase = SearchPhase.Idle;
    }

    /// <summary>Waits for the search in flight; for tests.</summary>
    public Task SettleAsync() => _searchTask;
}

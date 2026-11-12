// Ported from the Mac's Sources/Player/DesktopPlayerModel.swift (Lunalyr's own code: no lines from a99fd65): the
// card's panes, the search with its debounce, the results and their selection, the queue strip with its history and
// shuffle note, and the note after a play. Windows differences: one search per player (IPlayerSearch), time passed in
// as a clock instead of Combine's schedulers, results posted back to the card's thread, and the queue's own message
// (the Mac shares one status line between the search and the queue).

using System.Globalization;
using Lunalyr.Core.Logging;
using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Core.Player;

/// <summary>What the line over the results says.</summary>
public enum PlayerSearchStatusKind
{
    Idle = 0,
    Searching,
    Message,
}

/// <summary>The line over the results: nothing, "Searching…", or a message (nothing found, a failure in plain words).</summary>
public readonly record struct PlayerSearchStatus(PlayerSearchStatusKind Kind, string Text = "")
{
    public static PlayerSearchStatus Idle => default;

    public static PlayerSearchStatus Searching => new(PlayerSearchStatusKind.Searching, "Searching…");

    public static PlayerSearchStatus Message(string text) => new(PlayerSearchStatusKind.Message, text);
}

/// <summary>A song as the strip's history remembers it (the Mac's <c>NowPlayingMonitor.Track</c>, cut down).</summary>
public sealed record PlayedTrack(string Title, string Artist, string? PersistentId = null);

/// <summary>
/// The desktop player's state behind its search and queue: which pane shows, what was typed, what came back, the queue
/// strip, and the note after a play. Lives on the card's UI thread; searches and plays run on the thread pool and their
/// answers are posted back (<c>post</c>), so every member is read and written on one thread.
/// </summary>
/// <remarks>
/// Plays are playback commands: <see cref="Play(PlayerSearchResult)"/> and <see cref="Play(PlayerQueueItem)"/> are for
/// the user's own clicks only, and the searches they call go through the process's playback guard.
/// </remarks>
public sealed class DesktopPlayerModel
{
    /// <summary>Typing settles for this long before a search is sent (the Mac's 300 ms debounce).</summary>
    public const double SearchDelay = 0.3;

    /// <summary>Seconds a play note stays on record: longer than the card shows it (the Mac's <c>playNoteLife</c>).</summary>
    public const double PlayNoteLife = 9;

    /// <summary>Songs that played, kept for the strip.</summary>
    public const int HistoryLimit = 24;

    /// <summary>After a queue click the strip is read again at these delays, as the player catches up.</summary>
    public static IReadOnlyList<double> RefreshesAfterQueueClick { get; } = [0.9, 2.5, 5];

    private readonly Func<double> _clock;
    private readonly Action<Action> _post;
    private readonly CategoryLogger _log;
    private IPlayerSearch _searcher;
    private IPlayerSearch _queueSearcher;
    private string? _queueName;
    private string _query = "";
    private double? _searchDue;
    private readonly List<double> _queueDue = [];
    private IReadOnlyList<PlayerSearchResult> _results = [];
    private IReadOnlyList<PlayerQueueItem> _fetched = [];
    private readonly List<PlayerQueueItem> _history = [];
    private int _historyCount;
    private PlayedTrack? _lastTrack;
    private MediaSourceKind? _lastKind;
    private int _searchGeneration, _queueGeneration;
    private CancellationTokenSource? _searchCancel, _queueCancel;
    private double _noteUntil = double.NegativeInfinity;
    private int _shuffleNoteShown;
    private PlayerPane _pane;

    /// <param name="clock">Monotonic seconds (the card's clock; a manual one in tests).</param>
    /// <param name="post">Runs an answer on the card's thread; null runs it where it arrives (tests with finished tasks).</param>
    public DesktopPlayerModel(Func<double> clock, Action<Action>? post = null, Logger? logger = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _post = post ?? (action => action());
        _log = (logger ?? Logger.Null).Player;
        _searcher = _queueSearcher = new NoPlayerSearch("This player");
    }

    /// <summary>Moves on with every change the card draws, so it redraws only then.</summary>
    public int Version { get; private set; }

    /// <summary>The player the search box talks to (the picker's choice).</summary>
    public IPlayerSearch Searcher => _searcher;

    /// <summary>The player that is playing: the strip shows its songs, whatever the search is pointed at.</summary>
    public IPlayerSearch QueueSearcher => _queueSearcher;

    /// <summary>The pane chosen with the buttons (the column shows the search whenever something is typed).</summary>
    public PlayerPane Pane
    {
        get => _pane;
        set
        {
            if (_pane == value) return;
            _pane = value;
            Changed();
        }
    }

    public string Query => _query;

    public IReadOnlyList<PlayerSearchResult> Results => _results;

    /// <summary>The result Enter plays; ↑ and ↓ move it.</summary>
    public int Selection { get; private set; }

    public PlayerSearchStatus Status { get; private set; }

    /// <summary>What the strip shows: songs that played, the one playing, what is next.</summary>
    public IReadOnlyList<PlayerQueueItem> Queue { get; private set; } = [];

    /// <summary>Shuffle is on, so the order of what comes next is not the play order.</summary>
    public bool ShuffleOn { get; private set; }

    /// <summary>Under the strip when it has nothing to show: why (nothing playing, no queue shared, a failure).</summary>
    public string? QueueMessage { get; private set; }

    /// <summary>Moves on each time shuffle is switched or the playlist changes.</summary>
    public int ShuffleNoteToken { get; private set; }

    /// <summary>A short note about the song just picked (that it plays alone and why, or that it couldn't play).</summary>
    public string? PlayNote { get; private set; }

    /// <summary>Moves on with each new play note: the card shows each one once.</summary>
    public int PlayNoteToken { get; private set; }

    /// <summary>The strip row a press on Next moved to before the player answered (dormant on Windows; see <see cref="OptimisticNext"/>).</summary>
    public string? OptimisticCurrent { get; private set; }

    public bool IsSearching => _query.Trim().Length > 0;

    /// <summary>Whether the player stays on screen without the pointer: a search is typed.</summary>
    public bool IsPinned => IsSearching;

    /// <summary>The column shows the search: opened, or something is typed.</summary>
    public bool ShowsSearch => _pane == PlayerPane.Search || IsSearching;

    public bool ShowsQueue => _pane == PlayerPane.Queue && !IsSearching;

    /// <summary>The pane the column shows.</summary>
    public PlayerPane Shown => ShowsSearch ? PlayerPane.Search : ShowsQueue ? PlayerPane.Queue : PlayerPane.Song;

    /// <summary>Whether ↑ ↓ and Enter pick a result.</summary>
    public bool HasResults => IsSearching && _results.Count > 0;

    /// <summary>The search's player changed (the picker): the results go, and what is typed is searched again there.</summary>
    public void SetSearcher(IPlayerSearch searcher)
    {
        ArgumentNullException.ThrowIfNull(searcher);
        if (ReferenceEquals(searcher, _searcher)) return;
        _searcher = searcher;
        SetResults([]);
        Status = PlayerSearchStatus.Idle;
        Changed();
        RunSearch();
    }

    /// <summary>The playing player changed: the strip reads its queue from now on.</summary>
    public void SetQueueSearcher(IPlayerSearch searcher)
    {
        ArgumentNullException.ThrowIfNull(searcher);
        _queueSearcher = searcher;
    }

    /// <summary>What is typed; the search runs once typing settles (<see cref="Tick"/>).</summary>
    public void SetQuery(string text)
    {
        text ??= "";
        if (text == _query) return;
        _query = text;
        _searchDue = _clock() + SearchDelay;
        Changed();
    }

    /// <summary>Search or queue, from the transport's buttons: a second press closes it.</summary>
    public void Toggle(PlayerPane wanted)
    {
        if (wanted == PlayerPane.Search && IsSearching)
        {
            Pane = PlayerPane.Search;
            return;
        }

        Pane = _pane == wanted ? PlayerPane.Song : wanted;
    }

    /// <summary>The player going away with nothing typed: back to the song (a typed search stays).</summary>
    public void ClosePane()
    {
        if (IsSearching) return;
        Pane = PlayerPane.Song;
    }

    /// <summary>The search's × (and Esc): nothing typed, back to the song.</summary>
    public void CloseSearch()
    {
        SetQuery("");
        _searchDue = null;
        RunSearch();
        Pane = PlayerPane.Song;
    }

    /// <summary>Runs what is due: a settled search, a queue refresh after a click, and lets an old play note go.</summary>
    public void Tick()
    {
        var now = _clock();
        if (_searchDue is { } due && now >= due)
        {
            _searchDue = null;
            RunSearch();
        }

        if (_queueDue.Count > 0 && now >= _queueDue[0])
        {
            _queueDue.RemoveAll(t => now >= t);
            RefreshQueue();
        }

        if (PlayNote is not null && now >= _noteUntil)
        {
            PlayNote = null;
            Changed();
        }
    }

    public void RunSearch()
    {
        _searchCancel?.Cancel();
        var generation = ++_searchGeneration;
        var text = _query.Trim();
        if (new StringInfo(text).LengthInTextElements < 2)
        {
            SetResults([]);
            Status = PlayerSearchStatus.Idle;
            Changed();
            return;
        }

        Status = PlayerSearchStatus.Searching;
        Changed();
        var searcher = _searcher;
        var cancel = _searchCancel = new CancellationTokenSource();
        Task<IReadOnlyList<PlayerSearchResult>> task;
        try
        {
            task = searcher.SearchAsync(text, cancel.Token);
        }
        catch (Exception ex)
        {
            task = Task.FromException<IReadOnlyList<PlayerSearchResult>>(ex);
        }

        Await(task, found =>
        {
            if (generation != _searchGeneration) return;
            SetResults(found);
            Status = found.Count == 0 ? PlayerSearchStatus.Message($"Nothing found for “{text}”") : PlayerSearchStatus.Idle;
            Changed();
        }, error =>
        {
            if (generation != _searchGeneration || error is OperationCanceledException) return;
            _log.Info($"Desktop player: search failed ({PlayerSearchText.LogText(error)}).");
            SetResults([]);
            Status = PlayerSearchStatus.Message(PlayerSearchText.Words(error, searcher.PlayerName));
            Changed();
        });
    }

    /// <summary>Reads the strip again: the songs before this one, this one, the songs after.</summary>
    public void RefreshQueue()
    {
        _queueCancel?.Cancel();
        var generation = ++_queueGeneration;
        var searcher = _queueSearcher;
        if (_queueName != searcher.PlayerName)
        {
            Queue = [];
            _fetched = [];
            _queueName = searcher.PlayerName;
            QueueMessage = null;
            Changed();
        }

        var cancel = _queueCancel = new CancellationTokenSource();
        Task<PlayerQueueSnapshot> task;
        try
        {
            task = searcher.GetQueueAsync(cancel.Token);
        }
        catch (Exception ex)
        {
            task = Task.FromException<PlayerQueueSnapshot>(ex);
        }

        Await(task, snapshot =>
        {
            if (generation != _queueGeneration) return;
            var items = snapshot.Items;
            // A player between two songs answers with nothing; keep what the strip already shows.
            if (items.Count == 0 && _fetched.Count > 0) return;
            if (snapshot.Shuffle != ShuffleOn || PlaylistChanged(_fetched, items)) ShuffleNoteToken++;
            ShuffleOn = snapshot.Shuffle;
            _fetched = items;
            Recompose();
            QueueMessage = items.Count == 0 ? PlayerSearchText.EmptyQueueMessage(searcher.PlayerName, searcher.SharesQueue) : null;
            if (OptimisticCurrent is { } optimistic && Queue.FirstOrDefault(q => q.IsCurrent)?.Id == optimistic) OptimisticCurrent = null;
            Changed();
        }, error =>
        {
            if (generation != _queueGeneration || error is OperationCanceledException) return;
            _log.Info($"Desktop player: the queue couldn't be read ({PlayerSearchText.LogText(error)}).");
            if (Queue.Count == 0)
            {
                QueueMessage = PlayerSearchText.Words(error, searcher.PlayerName);
                Changed();
            }
        });
    }

    /// <summary>The song playing changed: one that finished goes into the strip's history (see <see cref="BelongsInHistory"/>).</summary>
    public void NoteTrack(PlayedTrack? track, MediaSourceKind? kind)
    {
        var previous = _lastTrack;
        var previousKind = _lastKind;
        _lastTrack = track;
        _lastKind = kind;
        if (previous is null || track is null || !BelongsInHistory(previous, previousKind, track, kind)) return;
        _historyCount++;
        _history.Add(new PlayerQueueItem($"h-{_historyCount}", previous.Title, previous.Artist, 0, PersistentId: previous.PersistentId));
        if (_history.Count > HistoryLimit) _history.RemoveRange(0, _history.Count - HistoryLimit);
        Recompose();
        Changed();
    }

    /// <summary>↑ / ↓ in the results.</summary>
    public void MoveSelection(int step)
    {
        if (_results.Count == 0) return;
        var next = Math.Clamp(Selection + step, 0, _results.Count - 1);
        if (next == Selection) return;
        Selection = next;
        Changed();
    }

    /// <summary>Enter in the search: the selected result.</summary>
    public void PlaySelection()
    {
        if (Selection < 0 || Selection >= _results.Count) return;
        Play(_results[Selection]);
    }

    /// <summary>A click on a result: it plays, and the column goes back to the song (the note says how it plays).</summary>
    public void Play(PlayerSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var searcher = _searcher;
        // Collapse after playing: nothing typed, back to the song (the answer arrives after, with its note).
        _query = "";
        _searchDue = null;
        _searchGeneration++;
        SetResults([]);
        Status = PlayerSearchStatus.Idle;
        _pane = PlayerPane.Song;
        Changed();
        Perform(searcher, () => searcher.PlayAsync(result));
    }

    /// <summary>A click on a strip row: the strip moves to it at once, the player catches up after.</summary>
    public void Play(PlayerQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsPlayable) return;
        var searcher = _queueSearcher;
        if (_fetched.Any(q => q.Id == item.Id))
        {
            _fetched = [.. _fetched.Select(q => q with { IsCurrent = q.Id == item.Id })];
            Recompose();
            Changed();
        }

        Perform(searcher, () => searcher.PlayQueueItemAsync(item));
        var now = _clock();
        foreach (var delay in RefreshesAfterQueueClick) _queueDue.Add(now + delay);
        _queueDue.Sort();
    }

    /// <summary>A press on Next: the strip's row moves at once when what comes next is known (never a guess).</summary>
    public void NoteNext(MediaSourceKind? kind)
    {
        OptimisticCurrent = OptimisticNext(Queue, ShuffleOn, kind);
        if (OptimisticCurrent is not null) Changed();
    }

    /// <summary>Whether the shuffle note should show on this reveal: once per change.</summary>
    public bool TakeShuffleNote()
    {
        if (!ShuffleOn || ShuffleNoteToken == _shuffleNoteShown) return false;
        _shuffleNoteShown = ShuffleNoteToken;
        return true;
    }

    /// <summary>
    /// Whether a fresh read of the strip is a different playlist: a position both reads cover holds a different song,
    /// or the two share no position at all.
    /// </summary>
    public static bool PlaylistChanged(IReadOnlyList<PlayerQueueItem> old, IReadOnlyList<PlayerQueueItem> fresh)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(fresh);
        if (old.Count == 0 || fresh.Count == 0) return (old.Count == 0) != (fresh.Count == 0);
        var before = new Dictionary<int, string>();
        foreach (var item in old) before.TryAdd(item.Index, Key(item));
        var overlap = false;
        foreach (var item in fresh)
        {
            if (!before.TryGetValue(item.Index, out var was)) continue;
            overlap = true;
            if (was != Key(item)) return true;
        }

        return !overlap;

        static string Key(PlayerQueueItem item) => item.PersistentId ?? $"{item.Title}|{item.Artist}";
    }

    /// <summary>
    /// The strip row a press on Next can slide to before the player answers: only for Apple Music with shuffle off, where
    /// what comes next is known (the Mac's rule, kept as is). Apple Music for Windows shares no queue, so on Windows this
    /// never fires today; Spotify, whose queue Lunalyr reads over the Web API, is left out as on the Mac.
    /// </summary>
    public static string? OptimisticNext(IReadOnlyList<PlayerQueueItem> queue, bool shuffleOn, MediaSourceKind? kind)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (kind != MediaSourceKind.AppleMusic || shuffleOn) return null;
        var now = -1;
        for (var i = 0; i < queue.Count; i++)
        {
            if (queue[i].IsCurrent)
            {
                now = i;
                break;
            }
        }

        if (now < 0 || now + 1 >= queue.Count || !queue[now + 1].IsPlayable) return null;
        return queue[now + 1].Id;
    }

    /// <summary>
    /// Only Apple Music's own songs go into the strip's history, and only when the next song is Apple Music's too: another
    /// player's song means nothing to it (the Mac's rule, kept as is; dormant on Windows, where Apple Music shares no queue).
    /// </summary>
    public static bool BelongsInHistory(PlayedTrack previous, MediaSourceKind? previousKind, PlayedTrack next, MediaSourceKind? kind)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        if (previous.Title.Length == 0) return false;
        if (previous.Title == next.Title && previous.Artist == next.Artist) return false;
        return previousKind == MediaSourceKind.AppleMusic && kind is MediaSourceKind.AppleMusic or null;
    }

    /// <summary>History first (what really played, in order), then the song playing, then what the player says comes next.</summary>
    private void Recompose()
    {
        var nowIndex = -1;
        for (var i = 0; i < _fetched.Count; i++)
        {
            if (_fetched[i].IsCurrent)
            {
                nowIndex = i;
                break;
            }
        }

        if (nowIndex < 0)
        {
            Queue = _fetched;
            return;
        }

        var now = _fetched[nowIndex];
        var after = _fetched.Skip(nowIndex + 1).ToList();
        var before = _history.TakeLast(HistoryLimit).Where(h => h.PersistentId is null || h.PersistentId != now.PersistentId).ToList();
        // As many before as after: where less has played than is queued, the player's own earlier songs fill the older end.
        var missing = after.Count - before.Count;
        if (missing > 0)
        {
            var known = before.Where(b => b.PersistentId is not null).Select(b => b.PersistentId!).ToHashSet();
            var earlier = _fetched.Take(nowIndex).Where(e => e.PersistentId is null || !known.Contains(e.PersistentId)).TakeLast(missing);
            before = [.. earlier, .. before];
        }

        Queue = [.. before, now, .. after];
    }

    private void SetResults(IReadOnlyList<PlayerSearchResult> results)
    {
        var same = results.Count == _results.Count && results.SequenceEqual(_results);
        _results = results;
        if (!same) Selection = 0;
    }

    private void Perform(IPlayerSearch searcher, Func<Task<PlayOutcome>> work)
    {
        Task<PlayOutcome> task;
        try
        {
            task = work();
        }
        catch (Exception ex)
        {
            task = Task.FromException<PlayOutcome>(ex);
        }

        Await(task, outcome => ShowPlayNote(outcome.Note), error =>
        {
            if (error is OperationCanceledException) return;
            _log.Info($"Desktop player: a play was refused or failed ({PlayerSearchText.LogText(error)}).");
            var words = PlayerSearchText.Words(error, searcher.PlayerName);
            Status = PlayerSearchStatus.Message(words);
            ShowPlayNote(words);
        });
    }

    private void ShowPlayNote(string? text)
    {
        if (text is null) return;
        PlayNote = text;
        PlayNoteToken++;
        _noteUntil = _clock() + PlayNoteLife;
        Changed();
    }

    /// <summary>Runs <paramref name="done"/> or <paramref name="failed"/> on the card's thread once <paramref name="task"/> ends.</summary>
    private void Await<T>(Task<T> task, Action<T> done, Action<Exception> failed)
    {
        if (task.IsCompleted)
        {
            Settle(task, done, failed);
            return;
        }

        task.ContinueWith(t => _post(() => Settle(t, done, failed)), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void Settle<T>(Task<T> task, Action<T> done, Action<Exception> failed)
    {
        if (task.IsCompletedSuccessfully) done(task.Result);
        else if (task.IsCanceled) failed(new OperationCanceledException());
        else failed(task.Exception!.InnerExceptions.Count == 1 ? task.Exception.InnerException! : task.Exception);
    }

    private void Changed() => Version++;
}

using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Player;

namespace Lunalyr.Core.Tests.Player;

/// <summary>
/// The card's search and queue state (DesktopPlayerModel), against a fake search that never reaches a player, and the
/// Mac's card tests ported with the same inputs.
/// </summary>
public sealed class DesktopPlayerModelTests
{
    private double _now = 100;

    private DesktopPlayerModel Model(FakeSearch? search = null)
    {
        var model = new DesktopPlayerModel(() => _now);
        if (search is not null)
        {
            model.SetSearcher(search);
            model.SetQueueSearcher(search);
        }

        return model;
    }

    private static PlayerQueueItem Item(string id, int index, bool current = false, string? pid = null) =>
        new(id, $"t{id}", "a", index, current, pid ?? id);

    private static PlayerSearchResult Result(string id, string title = "Evening") =>
        new(id, PlayerSearchResultKind.Song, title, "Artist", "Album", null, new PlayerSearchTarget($"spotify:track:{id}"));

    // ---- Ported from PlayerSleeveTests.swift ----

    // Ported from PlayerSleeveTests.swift: testSearchAndQueueButtonsOpenAndClose
    [Fact]
    public void SearchAndQueueButtonsOpenAndClose()
    {
        var model = Model();
        Assert.Equal(PlayerPane.Song, model.Pane);
        model.Toggle(PlayerPane.Search);
        Assert.True(model.ShowsSearch);
        model.Toggle(PlayerPane.Search);
        Assert.Equal(PlayerPane.Song, model.Pane);
        model.Toggle(PlayerPane.Queue);
        Assert.True(model.ShowsQueue);
        model.Toggle(PlayerPane.Search);
        Assert.True(model.ShowsSearch); // the search replaces the queue
        Assert.False(model.ShowsQueue);
    }

    // Ported from PlayerSleeveTests.swift: testATypedSearchStaysOpen (the Mac's preview model is loaded through the
    // public setters here)
    [Fact]
    public void ATypedSearchStaysOpen()
    {
        var model = Model();
        model.SetQuery("evening");
        model.Pane = PlayerPane.Song;
        Assert.True(model.ShowsSearch); // something typed shows the search
        model.ClosePane();
        Assert.True(model.ShowsSearch); // the player going away keeps a typed search
        model.SetQuery("");
        model.Pane = PlayerPane.Queue;
        Assert.True(model.ShowsQueue);
        model.ClosePane();
        Assert.Equal(PlayerPane.Song, model.Pane); // the queue closes with the player
    }

    // ---- Ported from DesktopScrubberTests.swift (PlayerOptimismTests) ----

    // Ported from DesktopScrubberTests.swift: testOptimisticNextOnlyWhenTheNextSongIsKnown (the Mac's .appleMusic and
    // .spotify are MediaSourceKind.AppleMusic and .Spotify)
    [Fact]
    public void OptimisticNextOnlyWhenTheNextSongIsKnown()
    {
        PlayerQueueItem[] queue = [Item("1", 1), Item("2", 2, current: true), Item("3", 3)];
        Assert.Equal("3", DesktopPlayerModel.OptimisticNext(queue, shuffleOn: false, MediaSourceKind.AppleMusic));
        Assert.Null(DesktopPlayerModel.OptimisticNext(queue, shuffleOn: true, MediaSourceKind.AppleMusic)); // shuffle: unknown
        Assert.Null(DesktopPlayerModel.OptimisticNext(queue, shuffleOn: false, MediaSourceKind.Spotify));
        Assert.Null(DesktopPlayerModel.OptimisticNext(queue[..2], shuffleOn: false, MediaSourceKind.AppleMusic));
        PlayerQueueItem[] stream = [queue[0], queue[1], queue[2] with { PersistentId = null }];
        Assert.Null(DesktopPlayerModel.OptimisticNext(stream, shuffleOn: false, MediaSourceKind.AppleMusic));
    }

    // Ported from DesktopScrubberTests.swift: testPlaylistChange
    [Fact]
    public void PlaylistChange()
    {
        PlayerQueueItem[] a = [Item("1", 1), Item("2", 2), Item("3", 3)];
        PlayerQueueItem[] advanced = [Item("2", 2), Item("3", 3), Item("4", 4)];
        Assert.False(DesktopPlayerModel.PlaylistChanged(a, advanced)); // the same playlist, moved on
        Assert.True(DesktopPlayerModel.PlaylistChanged(a, [Item("9", 2), Item("8", 3)]));
        Assert.True(DesktopPlayerModel.PlaylistChanged([], a)); // first read
        Assert.True(DesktopPlayerModel.PlaylistChanged(a, [Item("7", 50)])); // no overlap
    }

    // Ported from DesktopScrubberTests.swift: testShuffleNoteShowsOncePerChange. Its time-label half (the Mac's
    // custom.desktopPlayer.timeMode) is the setting desktopPlayerTimeMode on Windows, tested with the settings.
    [Fact]
    public void ShuffleNoteShowsOncePerChange()
    {
        var search = new FakeSearch();
        var model = Model(search);
        Assert.False(model.TakeShuffleNote()); // shuffle off: no note
        search.Queue = new PlayerQueueSnapshot([Item("1", 1, current: true), Item("2", 2)], Shuffle: true);
        model.RefreshQueue();
        Assert.True(model.TakeShuffleNote());
        Assert.False(model.TakeShuffleNote()); // once per change
        model.RefreshQueue();
        Assert.False(model.TakeShuffleNote()); // the same playlist again: nothing new to say
    }

    // ---- Ported from PlayerTextAndQueueSafetyTests.swift (PlayerDisplayAndHistoryTests) ----

    // Ported from PlayerTextAndQueueSafetyTests.swift: testASearchStaysOnTheDisplayItWasTypedOn (HoverState.showsPlayer
    // is HoverPolicy.ShowsPlayer on Windows)
    [Fact]
    public void ASearchStaysOnTheDisplayItWasTypedOn()
    {
        const long left = 1, right = 2;
        Assert.True(HoverPolicy.ShowsPlayer(left, active: left, pinned: left, searching: false));
        Assert.False(HoverPolicy.ShowsPlayer(right, active: left, pinned: left, searching: false));
        Assert.True(HoverPolicy.ShowsPlayer(left, active: null, pinned: left, searching: true));
        Assert.False(HoverPolicy.ShowsPlayer(right, active: null, pinned: left, searching: true));
        Assert.False(HoverPolicy.ShowsPlayer(right, active: left, pinned: left, searching: true));
        Assert.False(HoverPolicy.ShowsPlayer(left, active: null, pinned: left, searching: false));
    }

    // Ported from PlayerTextAndQueueSafetyTests.swift: testOnlyMusicSongsGoIntoTheStripsHistory (.chrome is a browser)
    [Fact]
    public void OnlyMusicSongsGoIntoTheStripsHistory()
    {
        var a = new PlayedTrack("A", "X", "0A");
        var b = new PlayedTrack("B", "X", "0B");
        Assert.True(DesktopPlayerModel.BelongsInHistory(a, MediaSourceKind.AppleMusic, b, MediaSourceKind.AppleMusic));
        Assert.False(DesktopPlayerModel.BelongsInHistory(a, MediaSourceKind.Spotify, b, MediaSourceKind.AppleMusic));
        Assert.False(DesktopPlayerModel.BelongsInHistory(a, MediaSourceKind.AppleMusic, b, MediaSourceKind.Browser));
        Assert.False(DesktopPlayerModel.BelongsInHistory(a, MediaSourceKind.AppleMusic, a, MediaSourceKind.AppleMusic));
    }

    // ---- Ported from QueueContinuationTests.swift ----

    // Ported from QueueContinuationTests.swift: testEveryWayOfPlayingAloneSaysWhyInPlainWords (its PlayerNoteToast half;
    // the notes themselves are in PlayerSearchModelTests)
    [Fact]
    public void ANoteStaysLongEnoughToRead()
    {
        Assert.True(PlayerCard.NoteSeconds(PlayOutcome.SoloNote(QueueSolo.NotInLibrary)) > 2.5);
        Assert.Equal(2.5, PlayerCard.NoteSeconds(null));
    }

    // ---- Windows: the model's own behaviour ----

    [Fact]
    public void ASearchWaitsForTypingToSettleAndNeedsTwoCharacters()
    {
        var search = new FakeSearch { Found = [Result("1"), Result("2")] };
        var model = Model(search);
        model.SetQuery("e");
        _now += 1;
        model.Tick();
        Assert.Empty(search.Queries); // one character asks nobody
        model.SetQuery("ev");
        _now += 0.2;
        model.SetQuery("eve");
        _now += 0.2;
        model.Tick();
        Assert.Empty(search.Queries); // still typing
        _now += 0.15;
        model.Tick();
        Assert.Equal(["eve"], search.Queries);
        Assert.Equal(2, model.Results.Count);
        Assert.Equal(PlayerSearchStatus.Idle, model.Status);
        Assert.True(model.HasResults);
    }

    [Fact]
    public void NothingFoundAndFailuresAreSaidCalmly()
    {
        var search = new FakeSearch();
        var model = Model(search);
        model.SetQuery("  zzz ");
        model.RunSearch();
        Assert.Equal(PlayerSearchStatus.Message("Nothing found for “zzz”"), model.Status);
        search.SearchError = new PlayerSearchException(PlayerSearchError.Offline);
        model.RunSearch();
        Assert.Equal(PlayerSearchStatus.Message("You’re offline. Try again when you’re back online."), model.Status);
        Assert.Empty(model.Results);
    }

    [Fact]
    public void ALateAnswerToAnOlderSearchIsDropped()
    {
        var search = new FakeSearch { Pending = true };
        var model = Model(search);
        model.SetQuery("first");
        model.RunSearch();
        var first = search.Waiting!;
        model.SetQuery("second");
        model.RunSearch();
        var second = search.Waiting!;
        Assert.Equal(PlayerSearchStatus.Searching, model.Status);
        second.SetResult([Result("2", "Second")]);
        first.SetResult([Result("1", "First")]);
        Assert.Equal("Second", Assert.Single(model.Results).Title);
    }

    [Fact]
    public void SelectionMovesWithinTheResultsAndEnterPlaysIt()
    {
        var search = new FakeSearch { Found = [Result("1"), Result("2"), Result("3")] };
        var model = Model(search);
        model.SetQuery("evening");
        model.RunSearch();
        model.MoveSelection(-1);
        Assert.Equal(0, model.Selection);
        model.MoveSelection(1);
        model.MoveSelection(1);
        model.MoveSelection(1);
        Assert.Equal(2, model.Selection);
        model.PlaySelection();
        Assert.Equal("3", Assert.Single(search.Played).Id);
    }

    [Fact]
    public void PlayingAResultClosesTheSearchAndSaysHowItPlays()
    {
        var search = new FakeSearch { Found = [Result("1")], Outcome = PlayOutcome.Alone(QueueSolo.Single) };
        var model = Model(search);
        model.Pane = PlayerPane.Search;
        model.SetQuery("evening");
        model.RunSearch();
        model.Play(model.Results[0]);
        Assert.Equal(PlayerPane.Song, model.Shown);
        Assert.Equal("", model.Query);
        Assert.Equal("Plays this song only.", model.PlayNote);
        Assert.Equal(1, model.PlayNoteToken);
        _now += DesktopPlayerModel.PlayNoteLife;
        model.Tick();
        Assert.Null(model.PlayNote);
    }

    [Fact]
    public void ARefusedPlayIsSaidCalmly()
    {
        // --demo and --no-playback: the search refuses before anything is sent.
        var search = new FakeSearch { Found = [Result("1")], PlayError = new PlayerSearchException(PlayerSearchError.PlaybackOff) };
        var model = Model(search);
        model.SetQuery("evening");
        model.RunSearch();
        model.Play(model.Results[0]);
        Assert.Equal("Playback commands are off for this run.", model.PlayNote);
        Assert.Equal(PlayerSearchStatus.Message("Playback commands are off for this run."), model.Status);
    }

    [Fact]
    public void SwitchingThePlayerSearchesThereAgain()
    {
        var first = new FakeSearch { Found = [Result("1")] };
        var second = new FakeSearch { Found = [Result("2"), Result("3")] };
        var model = Model(first);
        model.SetQuery("evening");
        model.RunSearch();
        model.SetSearcher(second);
        Assert.Equal(["evening"], second.Queries);
        Assert.Equal(2, model.Results.Count);
    }

    [Fact]
    public void TheStripKeepsWhatItShowsWhenThePlayerIsBetweenSongs()
    {
        var search = new FakeSearch { Queue = new PlayerQueueSnapshot([Item("1", 1, current: true), Item("2", 2)]) };
        var model = Model(search);
        model.RefreshQueue();
        Assert.Equal(2, model.Queue.Count);
        Assert.Null(model.QueueMessage);
        search.Queue = PlayerQueueSnapshot.Empty;
        model.RefreshQueue();
        Assert.Equal(2, model.Queue.Count);
    }

    [Fact]
    public void AnEmptyOrFailedStripSaysWhy()
    {
        var search = new FakeSearch { Queue = PlayerQueueSnapshot.Empty };
        var model = Model(search);
        model.RefreshQueue();
        Assert.Equal("Nothing is playing.", model.QueueMessage);
        var other = new FakeSearch { QueueError = new PlayerSearchException(PlayerSearchError.SignInExpired) };
        model.SetQueueSearcher(other);
        model.RefreshQueue();
        Assert.Equal("Your Spotify sign-in has ended. Sign in again in Lunalyr’s settings.", model.QueueMessage);
        model.SetQueueSearcher(new NoPlayerSearch("VLC"));
        model.RefreshQueue();
        Assert.Equal("VLC doesn’t share what plays next.", model.QueueMessage);
    }

    [Fact]
    public void AQueueClickMovesTheStripAtOnceAndReadsItAgainAsThePlayerCatchesUp()
    {
        var search = new FakeSearch { Queue = new PlayerQueueSnapshot([Item("1", 1, current: true), Item("2", 2), Item("3", 3)]) };
        var model = Model(search);
        model.RefreshQueue();
        model.Play(model.Queue[2]);
        Assert.Equal("3", model.Queue.Single(q => q.IsCurrent).Id);
        Assert.Equal("3", Assert.Single(search.QueuePlayed).Id);
        Assert.Equal(1, search.QueueReads);
        _now += 1;
        model.Tick();
        Assert.Equal(2, search.QueueReads);
        _now += 5;
        model.Tick();
        Assert.Equal(3, search.QueueReads); // the later two were both due: one read
        // A row with no id can't be played.
        model.Play(new PlayerQueueItem("x", "Radio", "", 0));
        Assert.Single(search.QueuePlayed);
    }

    [Fact]
    public void HistoryComesBeforeTheSongPlaying()
    {
        var search = new FakeSearch { Queue = new PlayerQueueSnapshot([Item("3", 3, current: true), Item("4", 4)]) };
        var model = Model(search);
        model.NoteTrack(new PlayedTrack("A", "X", "0A"), MediaSourceKind.AppleMusic);
        model.NoteTrack(new PlayedTrack("B", "X", "0B"), MediaSourceKind.AppleMusic);
        model.RefreshQueue();
        Assert.Equal(["h-1", "3", "4"], model.Queue.Select(q => q.Id));
        // Spotify's songs don't go in (the Mac's rule).
        model.NoteTrack(new PlayedTrack("C", "X"), MediaSourceKind.Spotify);
        model.NoteTrack(new PlayedTrack("D", "X"), MediaSourceKind.Spotify);
        Assert.Equal(3, model.Queue.Count);
    }

    [Fact]
    public void ThePromptSaysWhatToDo()
    {
        var spotify = MediaSourceApp.FromAppId("Spotify.exe");
        var demo = MediaSourceApp.FromAppId("Lunalyr.Demo");
        Assert.Null(PlayerSearchPrompt.For(PlayerSearchAvailability.Ready, spotify));
        var setUp = PlayerSearchPrompt.For(PlayerSearchAvailability.NeedsSetUp, spotify)!;
        Assert.Equal((PlayerSearchPromptAction.SetUp, "Set up Spotify in Settings"), (setUp.Action, setUp.ActionLabel));
        var signIn = PlayerSearchPrompt.For(PlayerSearchAvailability.NeedsSignIn, spotify)!;
        Assert.Equal(PlayerSearchPromptAction.SignIn, signIn.Action);
        Assert.Contains("Sign in to Spotify", signIn.Text, StringComparison.Ordinal);
        var none = PlayerSearchPrompt.For(PlayerSearchAvailability.None, demo)!;
        Assert.Equal(PlayerSearchPromptAction.None, none.Action);
        Assert.StartsWith("Lunalyr.Demo has no search Lunalyr can use. Search needs the Spotify app", none.Text, StringComparison.Ordinal);
        Assert.StartsWith("Search needs the Spotify app", PlayerSearchPrompt.For(PlayerSearchAvailability.None, null)!.Text, StringComparison.Ordinal);
        Assert.Equal("Spotify search is off for this run (--no-network).",
            PlayerSearchPrompt.For(PlayerSearchAvailability.None, spotify, "--no-network")!.Text);
    }

    [Theory]
    [InlineData("hello world", 11, 6)]
    [InlineData("hello world  ", 13, 6)]
    [InlineData("hello", 5, 0)]
    [InlineData("hello, world", 6, 5)]
    [InlineData("", 0, 0)]
    [InlineData("夕暮れ の窓", 6, 4)]
    [InlineData("a 😀😀", 6, 2)]
    public void CtrlBackspaceDeletesTheWordBeforeTheCaret(string text, int caret, int expected) =>
        Assert.Equal(expected, SearchText.WordStartBefore(text, caret));

    /// <summary>A search and queue that answer at once (or when told), record what was asked, and never reach a player.</summary>
    private sealed class FakeSearch : IPlayerSearch
    {
        public IReadOnlyList<PlayerSearchResult> Found { get; set; } = [];
        public PlayerQueueSnapshot Queue { get; set; } = PlayerQueueSnapshot.Empty;
        public Exception? SearchError { get; set; }
        public Exception? PlayError { get; set; }
        public Exception? QueueError { get; set; }
        public PlayOutcome Outcome { get; set; } = PlayOutcome.Queued(1, 1, fromAlbum: true);
        public bool Pending { get; set; }
        public TaskCompletionSource<IReadOnlyList<PlayerSearchResult>>? Waiting { get; private set; }
        public List<string> Queries { get; } = [];
        public List<PlayerSearchResult> Played { get; } = [];
        public List<PlayerQueueItem> QueuePlayed { get; } = [];
        public int QueueReads { get; private set; }

        public string PlayerName => "Spotify";

        public bool SharesQueue => true;

        public Task<IReadOnlyList<PlayerSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            if (SearchError is not null) return Task.FromException<IReadOnlyList<PlayerSearchResult>>(SearchError);
            if (!Pending) return Task.FromResult(Found);
            Waiting = new TaskCompletionSource<IReadOnlyList<PlayerSearchResult>>();
            return Waiting.Task;
        }

        public Task<PlayOutcome> PlayAsync(PlayerSearchResult result, CancellationToken cancellationToken = default)
        {
            Played.Add(result);
            return PlayError is null ? Task.FromResult(Outcome) : Task.FromException<PlayOutcome>(PlayError);
        }

        public Task<PlayerQueueSnapshot> GetQueueAsync(CancellationToken cancellationToken = default)
        {
            QueueReads++;
            return QueueError is null ? Task.FromResult(Queue) : Task.FromException<PlayerQueueSnapshot>(QueueError);
        }

        public Task<PlayOutcome> PlayQueueItemAsync(PlayerQueueItem item, CancellationToken cancellationToken = default)
        {
            QueuePlayed.Add(item);
            return PlayError is null ? Task.FromResult(Outcome) : Task.FromException<PlayOutcome>(PlayError);
        }
    }
}

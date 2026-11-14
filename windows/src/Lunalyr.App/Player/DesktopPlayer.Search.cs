using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Player;
using Lunalyr.Shell;

namespace Lunalyr.App.Player;

// Search and the queue in the card (the Mac's DesktopPlayerView search and queue columns over DesktopPlayerModel): the
// picker beside the search box, the results with their covers, the queue strip, the note after a play, and what to do
// when search can't be used. The state is DesktopPlayerModel (Core, tested); this file wires it to the window, the
// search box (SearchField) and the painter. Plays go through the player's IPlayerSearch, which asks the playback guard.
internal sealed unsafe partial class DesktopPlayer
{
    private DesktopPlayerModel? _search;
    private RowCovers? _covers;
    private IReadOnlyList<MediaSourceApp> _pickerApps = [];
    private MediaSourceApp? _searchApp;
    private bool _pickedByUser;
    private long? _pinned;
    private int _searchVersion = -1, _noteToken;
    private double _playersReadAt = double.NegativeInfinity, _queueRefreshAt = double.PositiveInfinity;
    private string? _queueTrack;
    private float _cardHeightDip = CardPainter.Height;
    private bool _debugPaneOpened;
    private (string? App, PlayerSearchAvailability Availability, string Name) _searcherKey;
    private (string? App, PlayerSearchAvailability Availability) _queueKey = ("\0", default);

    /// <summary>The host's side of search and the queue; set before <see cref="Start"/>.</summary>
    public PlayerSearchSource Search { get; set; } = new();

    private volatile string _queryText = "";

    /// <summary>What is typed, for UI Automation's threads.</summary>
    internal string QueryText => _queryText;

    /// <summary>A UI Automation client set the search box's value: it goes through the real box, as typing does.</summary>
    internal void SetQueryFromAutomation(string text)
    {
        if (_field is { IsShown: true } field) field.Text = text;
        else _search?.SetQuery(text);
        Invalidate();
        StartFrames(16);
    }

    /// <summary>Where a part is on the card, in DIPs (UI Automation's bounding rectangles).</summary>
    internal RectF PartRectDip(CardPart part) => _painter.PartRect(_model.Pane, part);

    private void StartSearch()
    {
        _search = new DesktopPlayerModel(Now, _post, _logger);
        _covers = new RowCovers(Search.LoadCover, _post, Invalidate);
        _model.RowCover = _covers.Get;
        try
        {
            var field = new SearchField(_surface.Hwnd, _log);
            field.Changed = text =>
            {
                _search.SetQuery(text);
                Invalidate();
                StartFrames(16);
            };
            field.Key = FieldKey;
            field.FocusChanged = focused =>
            {
                _interaction.SearchFocused = focused;
                _model.FieldFocused = focused;
                if (focused) _automation?.Focus(CardPart.SearchField);
                // The keyboard left through the box for another app: Esc must not hand it back to an older window later.
                else _post(() =>
                {
                    if (!HasKeyboard) _previousForeground = 0;
                });
                Invalidate();
                StartFrames(16);
            };
            field.Wheel = delta => Scroll(delta);
            _field = field;
        }
        catch (InvalidOperationException ex)
        {
            // The card works without its search box; the pane says so through its status line.
            _log.Warn("Desktop player: couldn't make the search box.", ex);
        }
    }

    private void StopSearch()
    {
        _covers?.Cancel();
        _field?.Dispose();
        _field = null;
    }

    // ---- Players ----

    /// <summary>
    /// The picker's players and the searches they use: read on reveal and once a second while shown (sessions come and
    /// go, a sign-in changes Spotify's availability).
    /// </summary>
    private void ReadSearchPlayers(double now, bool force)
    {
        if (_search is null || (!force && now - _playersReadAt < 1)) return;
        _playersReadAt = now;
        var playing = _controller()?.Monitor.Session?.SourceApp;
        var apps = new List<MediaSourceApp>();
        if (Search.Preferred is { } preferred) apps.Add(preferred);
        foreach (var app in Search.Apps())
            if (!apps.Any(a => a.AppId == app.AppId)) apps.Add(app);
        if (playing is not null && !apps.Any(a => a.AppId == playing.AppId)) apps.Add(playing);
        _pickerApps = apps;

        // The search follows what is playing when that can search (the Mac's followNowPlaying), else the first that can.
        if (!_pickedByUser || _searchApp is null || !apps.Any(a => a.AppId == _searchApp.AppId))
        {
            _pickedByUser = false;
            MediaSourceApp? choice = null;
            if (playing is not null && Search.For(playing).Availability != PlayerSearchAvailability.None) choice = playing;
            choice ??= apps.FirstOrDefault(a => Search.For(a).Availability != PlayerSearchAvailability.None) ?? playing ?? apps.FirstOrDefault();
            _searchApp = choice;
        }

        var (availability, searcher) = Search.For(_searchApp);
        // A player with no search is made afresh each time: only a different player or availability changes the search
        // (setting it clears the results and searches again).
        var key = (_searchApp?.AppId, availability, searcher.PlayerName);
        if (key != _searcherKey)
        {
            _searcherKey = key;
            _search.SetSearcher(searcher);
        }

        var queueKey = (playing?.AppId, availability);
        if (queueKey != _queueKey)
        {
            _queueKey = queueKey;
            _search.SetQueueSearcher(Search.QueueFor?.Invoke(playing) ?? Search.For(playing).Search);
        }
        _model.Prompt = PlayerSearchPrompt.For(availability, _searchApp, Search.SpotifyOffReason());
        _model.Pickers = [.. apps.Select(a => a.DisplayName)];
        _model.PickerSelected = _searchApp is null ? -1 : apps.FindIndex(a => a.AppId == _searchApp.AppId);
        _model.SearchPlaceholder = $"Search {searcher.PlayerName}";
        _field?.SetPlaceholder(_model.SearchPlaceholder);
    }

    private void Pick(int index)
    {
        if (_search is null || index < 0 || index >= _pickerApps.Count) return;
        _searchApp = _pickerApps[index];
        _pickedByUser = true;
        _log.Debug($"Desktop player: search now talks to {_searchApp.DisplayName}.");
        ReadSearchPlayers(Now(), force: true);
        Invalidate();
    }

    // ---- Revealing and hiding ----

    /// <summary>The card came up: the strip is read again (cheap for players that share no queue), a debug pane opens.</summary>
    private void SearchShown(double now)
    {
        if (_search is null) return;
        ReadSearchPlayers(now, force: true);
        _search.RefreshQueue();
        if (!_debugPaneOpened && Search.DebugPane is { } pane)
        {
            _debugPaneOpened = true;
            _search.Pane = pane;
            if (Search.DebugQuery is { Length: > 0 } query)
            {
                _search.SetQuery(query);
                _search.RunSearch();
            }
        }
    }

    /// <summary>The card went: the queue closes with it, a typed search stays (and keeps the card on its display).</summary>
    private void SearchHidden()
    {
        _search?.ClosePane();
        _field?.Show(false);
    }

    /// <summary>
    /// The Mac's pinning: while a search is typed the card stays on the display it was typed on even when the pointer
    /// leaves (HoverPolicy.ShowsPlayer), so it isn't lost mid-search.
    /// </summary>
    private long? Pinned(long? active)
    {
        if (active is { } a)
        {
            _pinned = a;
            return a;
        }

        if (_search?.IsPinned == true && _pinned is { } p && _displays.Any(d => d.Id == p)
            && HoverPolicy.ShowsPlayer(p, active, _pinned, searching: true)) return p;
        return null;
    }

    // ---- Every frame ----

    /// <summary>Runs the model's timers and copies its state into the card's (redrawing only when something changed).</summary>
    private void UpdateSearch(double now)
    {
        if (_search is not { } s) return;
        if (_shownOn is not null) ReadSearchPlayers(now, force: false);
        if (now >= _queueRefreshAt)
        {
            _queueRefreshAt = double.PositiveInfinity;
            s.RefreshQueue();
        }

        s.Tick();
        if (s.PlayNoteToken != _noteToken)
        {
            _noteToken = s.PlayNoteToken;
            if (s.PlayNote is { } note) ShowNote(note);
        }

        if (s.Version != _searchVersion)
        {
            _searchVersion = s.Version;
            var m = _model;
            var pane = s.Shown;
            if (pane != m.Pane)
            {
                m.Pane = pane;
                m.ResultsScroll = 0;
                if (pane == PlayerPane.Queue) ScrollQueueToCurrent();
                if (pane != PlayerPane.Song && m.FocusPart is CardPart.SeekBar or CardPart.TimeLabel or CardPart.SearchButton or CardPart.QueueButton or CardPart.PlayerLabel)
                    m.FocusPart = CardPart.PlayPause;
            }

            m.Query = s.Query;
            _queryText = s.Query;
            if (!ReferenceEquals(m.Results, s.Results)) m.ResultsScroll = 0;
            m.Results = s.Results;
            m.Selection = s.Selection;
            m.Status = s.Status;
            m.ShuffleOn = s.ShuffleOn;
            (m.Queue, m.QueueMessage) = DisplayedQueue(s);
            m.QueueRows = m.Queue.Count;
            if (_field is { } field && field.Text != s.Query) field.Text = s.Query;
            EnsureSelectionVisible();
            Invalidate();
        }

        var height = CardPainter.HeightFor(_model.Pane, _model.QueueRows);
        if (Math.Abs(height - _cardHeightDip) > 0.5f)
        {
            _cardHeightDip = height;
            _displaysRead = double.NegativeInfinity;
            if (_shownOn is not null) Place(now);
        }

        PlaceField(now);
    }

    /// <summary>The strip as drawn: a player sharing no queue shows the song playing alone, with why under it.</summary>
    private (IReadOnlyList<PlayerQueueItem> Items, string? Footnote) DisplayedQueue(DesktopPlayerModel s)
    {
        var searcher = s.QueueSearcher;
        if (s.Queue.Count == 0)
        {
            var why = s.QueueMessage ?? PlayerSearchText.EmptyQueueMessage(searcher.PlayerName, searcher.SharesQueue);
            if (!_model.HasSong) return ([], why);
            return ([new PlayerQueueItem("now", _model.Title, _model.Artist ?? "", 0, IsCurrent: true)], why);
        }

        if (s.OptimisticCurrent is { } moved && s.Queue.Any(q => q.Id == moved))
            return ([.. s.Queue.Select(q => q with { IsCurrent = q.Id == moved })], null);
        return (s.Queue, null);
    }

    /// <summary>The search box's window over the drawn field: shown only while the search pane is up and the card is.</summary>
    private void PlaceField(double now)
    {
        if (_field is not { } field) return;
        var reveal = _reveal.Value(now);
        var wanted = _shownOn is not null && _visible && _model.Pane == PlayerPane.Search && _model.Prompt is null && reveal > 0.02f;
        if (!wanted)
        {
            if (field.IsShown)
            {
                var hadKeyboard = field.HasFocus || PlayerNative.GetForegroundWindow() == field.Hwnd;
                field.Show(false);
                if (hadKeyboard && _shownOn is not null)
                {
                    // The keyboard stays on the card (a play collapsed the search, Esc closed it).
                    PlayerNative.SetForegroundWindow(_surface.Hwnd);
                    PlayerNative.SetFocus(_surface.Hwnd);
                }
            }

            return;
        }

        var r = CardLayout.FieldTextRect;
        var s = _scale;
        var look = _model;
        var well = CardPainter.FieldColour(look);
        field.SetColours(CardPainter.ColorRef(well), CardPainter.ColorRef(look.Theme.Ink), CardPainter.ColorRef(CardPainter.Mix(look.Theme.Ink, well, 0.5f)));
        field.SetFont((int)Math.Round(15 * s));
        field.Place(_cardX + (int)Math.Round(r.X * s), _cardY + (int)Math.Round(r.Y * s), (int)Math.Round(r.Width * s), (int)Math.Round(r.Height * s));
        field.SetAlpha(reveal);
        field.Show(true);
    }

    // ---- Song changes ----

    /// <summary>The song changed while the card is up: the strip is read again once it settles (the Mac waits 0.5 s).</summary>
    private void SearchTrackChanged(PlayerNow song, double now)
    {
        if (_search is null) return;
        var key = song.HasSong ? $"{song.Title}|{song.Artist}" : null;
        if (key == _queueTrack) return;
        _queueTrack = key;
        _search.NoteTrack(song.HasSong ? new PlayedTrack(song.Title!, song.Artist ?? "") : null,
            _controller()?.Monitor.Session?.SourceApp.Kind);
        if (_shownOn is not null) _queueRefreshAt = now + 0.5;
    }

    // ---- Actions ----

    /// <summary>Opens the search (the button, the player's label, Ctrl+F or /) and gives its box the keyboard.</summary>
    private void OpenSearch(bool toggle, bool focus)
    {
        if (_search is null) return;
        if (toggle) _search.Toggle(PlayerPane.Search);
        else _search.Pane = PlayerPane.Search;
        UpdateSearch(Now());
        if (_search.Shown == PlayerPane.Search && focus) FocusField();
        else if (_search.Shown != PlayerPane.Search) ReturnFocusToCard();
        Invalidate();
        StartFrames(16);
    }

    private void FocusField()
    {
        if (_field is null || _model.Prompt is not null) return;
        PlaceField(Now());
        TakeKeyboard();
        _field.Focus();
    }

    private void ReturnFocusToCard()
    {
        if (_field is null || !(_field.HasFocus || PlayerNative.GetForegroundWindow() == _field.Hwnd)) return;
        PlayerNative.SetForegroundWindow(_surface.Hwnd);
        PlayerNative.SetFocus(_surface.Hwnd);
    }

    /// <summary>Esc in the search: clears and closes it (true), or closes the queue; false when there is nothing to close.</summary>
    private bool ClosePaneFromKey()
    {
        if (_search is not { } s) return false;
        if (s.Query.Length > 0 || _field?.HasFocus == true || s.Shown == PlayerPane.Search)
        {
            s.CloseSearch();
            ReturnFocusToCard();
            UpdateSearch(Now());
            return true;
        }

        if (s.Shown == PlayerPane.Song) return false;
        s.Pane = PlayerPane.Song;
        return true;
    }

    /// <summary>A click (or Narrator's Invoke, or Enter on a ringed part) on one of search's and the queue's parts.</summary>
    private bool ClickSearchPart(CardPart part)
    {
        if (_search is not { } s) return false;
        switch (part)
        {
            case CardPart.SearchButton:
                OpenSearch(toggle: true, focus: true);
                return true;
            case CardPart.QueueButton:
                s.Toggle(PlayerPane.Queue);
                if (s.Shown == PlayerPane.Queue) s.RefreshQueue();
                return true;
            case CardPart.PlayerLabel:
                OpenSearch(toggle: false, focus: true);
                return true;
            case CardPart.PaneClose:
                ClosePaneFromKey();
                return true;
            case CardPart.ClearSearch:
                s.SetQuery("");
                s.RunSearch();
                FocusField();
                return true;
            case CardPart.SearchField:
                FocusField();
                return true;
            case CardPart.PromptAction:
                _log.Info("Desktop player: opening Settings › Music players for Spotify.");
                Search.OpenSettingsPage?.Invoke("players");
                return true;
        }

        if (CardParts.IsPicker(part, out var picker))
        {
            Pick(picker);
            return true;
        }

        if (CardParts.IsResult(part, out var result))
        {
            if (result < s.Results.Count)
            {
                _log.Debug("Desktop player: a search result was clicked.");
                s.Play(s.Results[result]);
                ReturnFocusToCard();
            }

            return true;
        }

        if (CardParts.IsQueueRow(part, out var row))
        {
            if (row < _model.Queue.Count)
            {
                var item = _model.Queue[row];
                // The song playing: play or pause (the transport's job); another: it plays, and what was listed after it.
                if (item.IsCurrent) PlayPause();
                else s.Play(item);
            }

            return true;
        }

        return false;
    }

    /// <summary>A key in the search box that the box doesn't keep: true when the card used it.</summary>
    private bool FieldKey(ushort vk)
    {
        var ctrl = Ctrl;
        var alt = Alt;
        var system = PlayerNative.KeyDown(PlayerNative.VK_LWIN) || PlayerNative.KeyDown(PlayerNative.VK_RWIN);
        if (vk == 0x09 && !ctrl && !alt && !system)
        {
            TakeKeyboard();
            PlayerNative.SetFocus(_surface.Hwnd);
            _model.FocusPart = CardPart.SearchField;
            MoveFocus(back: PlayerNative.KeyDown(PlayerNative.VK_SHIFT));
            return true;
        }

        var character = (char)(PlayerNative.MapVirtualKey(vk, PlayerNative.MAPVK_VK_TO_CHAR) & 0xFFFF);
        var characters = character == '\0' ? "" : char.ToLowerInvariant(character).ToString();
        if (PlayerKeyMap.Action(vk, characters, ctrl, alt, system, editingText: true, hasResults: _search?.HasResults ?? false) is not { } action)
            return false;
        if (action.Kind == PlayerKeyKind.FocusSearch)
        {
            // Ctrl+F in the box: everything typed, selected.
            if (_field is { } field) PlayerNative.SendMessage(field.Hwnd, PlayerNative.EM_SETSEL, 0, -1);
            return true;
        }

        Perform(action);
        return true;
    }

    // ---- Scrolling ----

    private void Scroll(int wheelDelta)
    {
        var step = -wheelDelta / 120f * 2 * (CardLayout.ResultRow + CardLayout.RowGap) / 2;
        if (_model.Pane == PlayerPane.Search)
            _model.ResultsScroll = Math.Clamp(_model.ResultsScroll + step, 0, Math.Max(0, _painter.ResultsContent - ResultsViewport));
        else if (_model.Pane == PlayerPane.Queue)
            _model.QueueScroll = Math.Clamp(_model.QueueScroll + step, 0, Math.Max(0, _painter.QueueContent - QueueViewport));
        else return;
        Invalidate();
        StartFrames(16);
    }

    private static float ResultsViewport => CardLayout.ColumnBottom(PlayerPane.Search, 0) - CardLayout.ResultsTop;

    private float QueueViewport => CardLayout.ColumnBottom(PlayerPane.Queue, _model.QueueRows) - CardLayout.QueueTop;

    /// <summary>↑ and ↓ keep the selected result in view.</summary>
    private void EnsureSelectionVisible()
    {
        var m = _model;
        if (m.Results.Count == 0) return;
        var statusHeight = m.Status.Kind == PlayerSearchStatusKind.Idle ? 0 : 36;
        var top = statusHeight + m.Selection * (CardLayout.ResultRow + CardLayout.RowGap);
        var bottom = top + CardLayout.ResultRow;
        if (top < m.ResultsScroll) m.ResultsScroll = top;
        else if (bottom > m.ResultsScroll + ResultsViewport) m.ResultsScroll = bottom - ResultsViewport;
    }

    /// <summary>The queue opens on the song playing, in the middle of the list.</summary>
    private void ScrollQueueToCurrent()
    {
        var m = _model;
        var current = -1;
        for (var i = 0; i < m.Queue.Count; i++)
        {
            if (m.Queue[i].IsCurrent)
            {
                current = i;
                break;
            }
        }

        if (current < 0)
        {
            m.QueueScroll = 0;
            return;
        }

        var y = current * (CardLayout.QueueRow + CardLayout.RowGap) + (current > 0 ? CardLayout.QueueSection : 0);
        var height = CardLayout.ColumnBottom(PlayerPane.Queue, m.Queue.Count) - CardLayout.QueueTop;
        var content = m.Queue.Count * (CardLayout.QueueRow + CardLayout.RowGap) + 2 * CardLayout.QueueSection;
        m.QueueScroll = Math.Clamp(y - (height - CardLayout.QueueRow) / 2, 0, Math.Max(0, content - height));
    }
}

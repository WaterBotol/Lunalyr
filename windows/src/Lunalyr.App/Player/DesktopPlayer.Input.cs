using System.Runtime.InteropServices;
using Lunalyr.Core.Player;
using Lunalyr.Shell.Player;

namespace Lunalyr.App.Player;

// The card's window procedure: the pointer on the card (hover, presses, the seek bar's drag with its modifiers), keys
// once a click has given it the keyboard, and the commands those send. Ctrl turns snapping off and Alt scrubs finely
// while dragging (the Mac's ⌘ and ⌥); PlayerKeyMap has the keys.
internal sealed unsafe partial class DesktopPlayer
{
    /// <summary>Called when something inside the window procedure throws (it can't propagate to Windows).</summary>
    public Action<Exception>? OnError { get; set; }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var self = s_instance;
        if (self is null || hwnd != self._surface.Hwnd) return PlayerNative.DefWindowProc(hwnd, msg, wParam, lParam);
        try
        {
            return self.OnMessage(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            try
            {
                self.OnError?.Invoke(ex);
            }
            catch (Exception)
            {
                // Nothing more can be done inside a window procedure.
            }

            return 0;
        }
    }

    private nint OnMessage(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg is PlayerNative.WM_LBUTTONDOWN or PlayerNative.WM_LBUTTONUP) _log.Debug(msg == PlayerNative.WM_LBUTTONDOWN ? "Desktop player: button down." : "Desktop player: button up.");
        switch (msg)
        {
            case PlayerNative.WM_TIMER:
                switch (wParam)
                {
                    case PollTimer: Poll(); break;
                    case FrameTimer: Frame(); break;
                    case CoverTimer:
                        PlayerNative.KillTimer(hwnd, CoverTimer);
                        if (_coverPending)
                        {
                            _coverPending = false;
                            PlayPause();
                        }

                        break;
                }

                return 0;

            case PlayerNative.WM_ERASEBKGND:
                return 1;

            case PlayerNative.WM_PAINT:
                ValidateRect(hwnd, 0);
                Invalidate();
                return 0;

            case PlayerNative.WM_MOUSEACTIVATE:
                // A click gives the card the keyboard (Space, the arrows…), as a click on the Mac's panel makes it key.
                if (_previousForeground == 0)
                {
                    var foreground = PlayerNative.GetForegroundWindow();
                    if (foreground != hwnd && !IsSearchField(foreground)) _previousForeground = foreground;
                }

                return (nint)PlayerNative.MA_ACTIVATE;

            case PlayerNative.WM_SETCURSOR:
                PlayerNative.SetCursor(PlayerNative.LoadCursor(0, (nint)(_model.HoverPart switch
                {
                    CardPart.None => PlayerNative.IDC_ARROW,
                    CardPart.SearchField => 32513, // IDC_IBEAM
                    _ => PlayerNative.IDC_HAND,
                })));
                return 1;

            case PlayerNative.WM_MOUSEWHEEL:
                Scroll((short)(((long)wParam >> 16) & 0xFFFF));
                return 0;

            case PlayerNative.WM_MOUSEMOVE:
                MouseMove(lParam, wParam);
                return 0;

            case PlayerNative.WM_MOUSELEAVE:
                _tracking = false;
                if (!_scrub.IsDragging)
                {
                    _scrub.PointerExited();
                    _model.HoverPart = CardPart.None;
                    Invalidate();
                }

                return 0;

            case PlayerNative.WM_LBUTTONDOWN:
            case PlayerNative.WM_LBUTTONDBLCLK:
                MouseDown(lParam, wParam);
                return 0;

            case PlayerNative.WM_LBUTTONUP:
                MouseUp(lParam, wParam);
                return 0;

            case PlayerNative.WM_CAPTURECHANGED:
                // Capture taken away mid-drag (a system dialog, Alt+Tab): nothing is sent.
                if (_scrub.IsDragging && lParam != hwnd && double.IsNaN(_debugScrub)) _scrub.Cancel();
                _pressed = CardPart.None;
                Invalidate();
                return 0;

            case PlayerNative.WM_KEYDOWN:
            case PlayerNative.WM_SYSKEYDOWN:
                if (KeyDown((ushort)wParam, lParam)) return 0;
                break;

            case PlayerNative.WM_KEYUP:
            case PlayerNative.WM_SYSKEYUP:
                // Ctrl and Alt let go mid-drag change the scrub; Alt alone never opens a window menu here.
                if (_scrub.IsDragging) _scrub.ModifiersChanged(fine: PlayerNative.KeyDown(PlayerNative.VK_MENU), noSnap: PlayerNative.KeyDown(PlayerNative.VK_CONTROL));
                if (msg == PlayerNative.WM_SYSKEYUP) return 0;
                break;

            case PlayerNative.WM_SYSCHAR:
            case PlayerNative.WM_CHAR:
                // Handled as key-downs; a stray character must not beep.
                return 0;

            case PlayerNative.WM_SYSCOMMAND:
                // SC_KEYMENU (Alt, Alt+Space) would open a menu the card doesn't have, and beep.
                if (((uint)wParam & 0xFFF0) == 0xF100) return 0;
                break;

            case PlayerNative.WM_KILLFOCUS:
                // The keyboard moving into the search box stays "on the card"; anywhere else, it has left.
                if (!IsSearchField((nint)wParam)) _previousForeground = 0;
                break;

            case PlayerNative.WM_SETTINGCHANGE:
            case PlayerNative.WM_THEMECHANGED:
            case PlayerNative.WM_DWMCOLORIZATIONCOLORCHANGED:
                // Light or dark, transparency, contrast themes, animation effects: read again (never changed here).
                RefreshLook();
                _paletteCover = null; // the theme is made again with the colours
                break;

            case PlayerNative.WM_DISPLAYCHANGE:
            case PlayerNative.WM_DPICHANGED:
                _displaysRead = double.NegativeInfinity;
                if (msg == PlayerNative.WM_DPICHANGED) return 0; // the card sizes itself for its display
                break;

            case PlayerNative.WM_CTLCOLOREDIT:
            case PlayerNative.WM_CTLCOLORSTATIC:
                if (_field is not null && lParam == _field.Hwnd) return _field.CtlColor((nint)wParam);
                break;

            case PlayerNative.WM_COMMAND:
                if (_field?.Command(wParam, lParam) == true) return 0;
                break;

            case PlayerNative.WM_GETOBJECT:
                if (_automation?.HandleGetObject(wParam, lParam) is { } result) return result;
                break;
        }

        return PlayerNative.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ValidateRect(nint hwnd, nint rect);

    // ---- Pointer ----

    private (float X, float Y) Dips(nint lParam) => (PlayerNative.LoWord(lParam) / (float)_scale, PlayerNative.HiWord(lParam) / (float)_scale);

    private static (double X, double Y) BarLocal(float x, float y) => (x - CardLayout.ColumnLeft, y - CardLayout.BarCentre);

    private CardPart PartAt(float x, float y)
    {
        var m = _model;
        var pane = m.Pane;
        if (CardLayout.CoverRect.Contains(x, y)) return m.HasSong ? CardPart.Cover : CardPart.None;
        if (CardLayout.PlayFor(pane).Inflate(2).Contains(x, y)) return CardPart.PlayPause;
        if (CardLayout.PreviousFor(pane).Contains(x, y)) return m.HasSong ? CardPart.Previous : CardPart.None;
        if (CardLayout.NextFor(pane).Contains(x, y)) return m.HasSong ? CardPart.Next : CardPart.None;
        if (pane == PlayerPane.Song)
        {
            if (m.RowKind == ScrubRowKind.Seekable && CardLayout.BarHitRect.Contains(x, y)) return CardPart.SeekBar;
            var times = CardLayout.TimesRect;
            if (m.RowKind is ScrubRowKind.Seekable or ScrubRowKind.ReadOnly
                && new RectF(times.Right - 64, times.Y, 64, times.Height).Inflate(2).Contains(x, y)) return CardPart.TimeLabel;
            if (_search is not null && CardLayout.SearchButtonRect.Contains(x, y)) return CardPart.SearchButton;
            if (_search is not null && CardLayout.QueueButtonRect.Contains(x, y)) return CardPart.QueueButton;
        }

        // Rows, the picker and the pane's buttons, where they were last drawn (a row scrolled out of its list is gone).
        foreach (var (part, rect) in _painter.Hits)
        {
            if (!rect.Contains(x, y)) continue;
            if (CardParts.IsResult(part, out _) && (y < CardLayout.ResultsTop || y > CardLayout.ColumnBottom(PlayerPane.Search, 0))) continue;
            if (CardParts.IsQueueRow(part, out var row)
                && (y < CardLayout.QueueTop || y > CardLayout.ColumnBottom(PlayerPane.Queue, m.QueueRows) || row >= m.Queue.Count || !m.Queue[row].IsPlayable)) continue;
            return part;
        }

        if (pane == PlayerPane.Search && m.Prompt is null && CardLayout.FieldRect.Contains(x, y)) return CardPart.SearchField;
        return CardPart.None;
    }

    private bool IsSearchField(nint hwnd) => hwnd != 0 && _field is not null && hwnd == _field.Hwnd;

    private static bool Ctrl => PlayerNative.KeyDown(PlayerNative.VK_CONTROL);

    private static bool Alt => PlayerNative.KeyDown(PlayerNative.VK_MENU);

    private void MouseMove(nint lParam, nuint wParam)
    {
        if (!_tracking)
        {
            var track = new PlayerNative.TRACKMOUSEEVENT
            {
                cbSize = (uint)sizeof(PlayerNative.TRACKMOUSEEVENT), dwFlags = PlayerNative.TME_LEAVE, hwndTrack = _surface.Hwnd,
            };
            _tracking = PlayerNative.TrackMouseEvent(ref track);
        }

        var (x, y) = Dips(lParam);
        var (bx, by) = BarLocal(x, y);
        if (_scrub.IsDragging)
        {
            if (!double.IsNaN(_debugScrub)) return;
            _scrub.MouseDragged(bx, by, fine: Alt, noSnap: Ctrl);
            StartFrames(16);
            return;
        }

        var part = PartAt(x, y);
        if (part != _model.HoverPart)
        {
            _model.HoverPart = part;
            Invalidate();
            StartFrames(16);
        }

        if (part == CardPart.SeekBar) _scrub.PointerMoved(bx, by, noSnap: Ctrl);
        else _scrub.PointerExited();
        if (_scrub.Version != _drawnVersion) StartFrames(16);
    }

    private void MouseDown(nint lParam, nuint wParam)
    {
        var (x, y) = Dips(lParam);
        var part = PartAt(x, y);
        TakeKeyboard();
        _model.FocusVisible = false;
        if (part != CardPart.None)
        {
            _model.FocusPart = part;
            _automation?.Focus(part);
        }

        _interaction.NoteClick(Now());
        PlayerNative.SetCapture(_surface.Hwnd);
        if (part == CardPart.SeekBar)
        {
            var (bx, by) = BarLocal(x, y);
            _scrub.MouseDown(bx, by, fine: Alt, noSnap: Ctrl);
            _automation?.Focus(CardPart.SeekBar);
        }
        else
        {
            _pressed = part;
        }

        Invalidate();
        StartFrames(16);
    }

    private void MouseUp(nint lParam, nuint wParam)
    {
        var (x, y) = Dips(lParam);
        if (_scrub.IsDragging && double.IsNaN(_debugScrub))
        {
            var (bx, by) = BarLocal(x, y);
            _scrub.MouseUp(bx, by, fine: Alt, noSnap: Ctrl);
            if (PartAt(x, y) != CardPart.SeekBar) _scrub.PointerExited();
        }
        else if (_pressed != CardPart.None && PartAt(x, y) == _pressed)
        {
            Click(_pressed);
        }

        _pressed = CardPart.None;
        if (PlayerNative.GetCapture() == _surface.Hwnd) PlayerNative.ReleaseCapture();
        Invalidate();
        StartFrames(16);
    }

    /// <summary>A click on one of the card's parts (or Narrator's Invoke on it).</summary>
    internal void Click(CardPart part)
    {
        _interaction.NoteClick(Now());
        switch (part)
        {
            case CardPart.Cover:
                // A click plays or pauses; a double-click brings the player forward instead, so the single click waits
                // out a short double-click window.
                if (_coverPending)
                {
                    _coverPending = false;
                    PlayerNative.KillTimer(_surface.Hwnd, CoverTimer);
                    OpenPlayer();
                }
                else
                {
                    _coverPending = true;
                    PlayerNative.SetTimer(_surface.Hwnd, CoverTimer, Math.Min(PlayerNative.GetDoubleClickTime(), 300u), 0);
                }

                break;
            case CardPart.PlayPause: PlayPause(); break;
            case CardPart.Previous: Skip(forward: false); break;
            case CardPart.Next: Skip(forward: true); break;
            case CardPart.TimeLabel:
                var remaining = !_model.ShowsRemaining;
                _model.ShowsRemaining = remaining;
                _updateSettings(s => s with { DesktopPlayerTimeMode = remaining ? "remaining" : "total" });
                Invalidate();
                break;
            default:
                ClickSearchPart(part);
                Invalidate();
                StartFrames(16);
                break;
        }
    }

    // ---- Keyboard ----

    private void TakeKeyboard()
    {
        var foreground = PlayerNative.GetForegroundWindow();
        if (foreground == _surface.Hwnd || IsSearchField(foreground)) return;
        if (_previousForeground == 0) _previousForeground = foreground;
        PlayerNative.SetForegroundWindow(_surface.Hwnd);
    }

    /// <summary>Esc, or the card going away: the keyboard goes back to where it was before the click.</summary>
    private void ReleaseKeyboard()
    {
        var foreground = PlayerNative.GetForegroundWindow();
        if (foreground != _surface.Hwnd && !IsSearchField(foreground)) return;
        var previous = _previousForeground;
        _previousForeground = 0;
        if (previous != 0 && PlayerNative.IsWindowVisible(previous)) PlayerNative.SetForegroundWindow(previous);
        else
        {
            var desktop = PlayerNative.FindWindow("Progman", null);
            if (desktop != 0) PlayerNative.SetForegroundWindow(desktop);
        }
    }

    private bool KeyDown(ushort vk, nint lParam)
    {
        var ctrl = Ctrl;
        var alt = Alt || ((long)lParam & (1 << 29)) != 0;
        if (vk is PlayerNative.VK_CONTROL or PlayerNative.VK_MENU or PlayerNative.VK_SHIFT)
        {
            if (_scrub.IsDragging) _scrub.ModifiersChanged(fine: alt, noSnap: ctrl);
            return true;
        }

        var system = PlayerNative.KeyDown(PlayerNative.VK_LWIN) || PlayerNative.KeyDown(PlayerNative.VK_RWIN);
        if (vk == 0x09 && !ctrl && !alt && !system)
        {
            MoveFocus(back: PlayerNative.KeyDown(PlayerNative.VK_SHIFT));
            return true;
        }

        if (vk == PlayerKeyMap.KeyCode.Return && !ctrl && !alt && _model.FocusVisible && _model.FocusPart is not (CardPart.None or CardPart.SeekBar))
        {
            Click(_model.FocusPart);
            return true;
        }

        var character = (char)(PlayerNative.MapVirtualKey(vk, PlayerNative.MAPVK_VK_TO_CHAR) & 0xFFFF);
        // The numeric keypad's digits map to no character: they are the digits too.
        if (vk is >= 0x60 and <= 0x69) character = (char)('0' + vk - 0x60);
        if (vk == 0x6F) character = '/';
        var characters = character == '\0' ? "" : char.ToLowerInvariant(character).ToString();
        if (PlayerKeyMap.Action(vk, characters, ctrl, alt, system, editingText: false, hasResults: _search?.HasResults ?? false) is not { } action)
            return false;
        Perform(action);
        return true;
    }

    private void Perform(PlayerKeyAction action)
    {
        switch (action.Kind)
        {
            case PlayerKeyKind.PlayPause: PlayPause(); break;
            case PlayerKeyKind.SeekBy: _scrub.SeekBy(action.Seconds); break;
            case PlayerKeyKind.Line: _scrub.SeekLine(action.Forward); break;
            case PlayerKeyKind.Track: Skip(action.Forward); break;
            case PlayerKeyKind.Jump: _scrub.Jump(action.Value); break;
            case PlayerKeyKind.FocusSearch:
                OpenSearch(toggle: false, focus: true);
                break;
            case PlayerKeyKind.MoveSelection:
                _search?.MoveSelection(action.Value);
                break;
            case PlayerKeyKind.PlaySelection:
                _search?.PlaySelection();
                ReturnFocusToCard();
                break;
            case PlayerKeyKind.Escape:
                // A drag first, then the search (cleared and closed) or the queue, then the keyboard goes back.
                if (_scrub.IsDragging) _scrub.Cancel();
                else if (!ClosePaneFromKey()) ReleaseKeyboard();
                break;
        }

        Invalidate();
        StartFrames(16);
    }

    // ---- Commands ----

    /// <summary>The glyph flips now; the player's answer settles it (or, refused, it goes back at once).</summary>
    internal void PlayPause()
    {
        if (_now is not { HasSong: true } song) return;
        var now = Now();
        _toggle.Press(song.IsPlaying, now);
        Invalidate();
        StartFrames(16);
        Run(_commands.PlayPauseAsync(song.IsPlaying), result =>
        {
            _toggle.Withdraw();
            ShowNote(result.Note!);
        });
    }

    internal void Skip(bool forward)
    {
        if (_now is not { HasSong: true }) return;
        // The strip's row moves at once only where what comes next is known (never a guess; see OptimisticNext).
        if (forward) _search?.NoteNext(_controller()?.Monitor.Session?.SourceApp.Kind);
        Run(_commands.SkipAsync(forward), result => ShowNote(result.Note!));
    }

    /// <summary>The seek bar's one seek, on release (or a key's): refused, the bar goes back to the player's time.</summary>
    private void Seek(double songSeconds)
    {
        _log.Debug($"Desktop player: seek to {songSeconds:0.##} s (sent once, through the guard).");
        Run(_commands.SeekAsync(songSeconds), result =>
        {
            _scrub.Release();
            ShowNote(result.Note!);
        });
    }

    /// <summary>Waits for a command off the UI thread and handles a refusal back on it.</summary>
    private void Run(Task<CommandResult> command, Action<CommandResult> refused)
    {
        void Done(CommandResult result)
        {
            if (result.Ok) return;
            if (result.Note is null) return;
            refused(result);
        }

        if (command.IsCompletedSuccessfully)
        {
            Done(command.Result);
            return;
        }

        command.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) _post(() => Done(task.Result));
        }, TaskScheduler.Default);
    }

    /// <summary>A double-click on the cover: the playing app's window forward (packaged players aren't found this way).</summary>
    private void OpenPlayer()
    {
        var id = _controller()?.Monitor.Session?.SourceAppUserModelId;
        _log.Info(id is null ? "Desktop player: nothing to bring forward." : $"Desktop player: double-click on the cover ({id}).");
        PlayerWindows.BringForward(id);
    }

    // ---- Narrator and the keyboard's focus ----

    internal void PostFromAutomation(Action action) => _post(action);

    internal double Scale => _scale;

    internal bool HasKeyboard => PlayerNative.GetForegroundWindow() is var f && (f == _surface.Hwnd || IsSearchField(f));

    /// <summary>The card's place on screen, in pixels.</summary>
    internal PlayerNative.RECT ScreenRect() => new() { Left = _cardX, Top = _cardY, Right = _cardX + _cardW, Bottom = _cardY + _cardH };

    internal CardPart PartAtScreen(double x, double y) =>
        _shownOn is null ? CardPart.None : PartAt((float)((x - _cardX) / _scale), (float)((y - _cardY) / _scale));

    /// <summary>
    /// What Tab and Shift+Tab walk, in order (Windows' own convention; the Mac has no Tab here), and what Narrator lists:
    /// the cover and the transport, then the song's row or the pane's own parts.
    /// </summary>
    internal List<CardPart> FocusOrder()
    {
        var m = _model;
        var order = new List<CardPart>();
        if (m.HasSong) order.Add(CardPart.Cover);
        if (m.Pane == PlayerPane.Song && m.PlayerName.Length > 0 && _search is not null) order.Add(CardPart.PlayerLabel);
        if (m.HasSong) order.Add(CardPart.Previous);
        order.Add(CardPart.PlayPause);
        if (m.HasSong) order.Add(CardPart.Next);
        switch (m.Pane)
        {
            case PlayerPane.Song:
                if (m.RowKind == ScrubRowKind.Seekable) order.Add(CardPart.SeekBar);
                if (m.RowKind is ScrubRowKind.Seekable or ScrubRowKind.ReadOnly) order.Add(CardPart.TimeLabel);
                if (_search is not null)
                {
                    order.Add(CardPart.SearchButton);
                    order.Add(CardPart.QueueButton);
                }

                break;
            case PlayerPane.Search:
                for (var i = 0; i < m.Pickers.Count; i++) order.Add(CardParts.Picker(i));
                order.Add(CardPart.PaneClose);
                if (m.Prompt is { ActionLabel: not null }) order.Add(CardPart.PromptAction);
                if (m.Prompt is null)
                {
                    order.Add(CardPart.SearchField);
                    if (m.Query.Length > 0) order.Add(CardPart.ClearSearch);
                    for (var i = 0; i < m.Results.Count; i++) order.Add(CardParts.Result(i));
                }

                break;
            case PlayerPane.Queue:
                order.Add(CardPart.PaneClose);
                for (var i = 0; i < m.Queue.Count; i++)
                    if (m.Queue[i].IsPlayable) order.Add(CardParts.QueueRow(i));
                break;
        }

        return order;
    }

    internal void FocusPart(CardPart part)
    {
        if (part == CardPart.None) return;
        if (part == CardPart.SearchField)
        {
            _model.FocusPart = part;
            FocusField();
            return;
        }

        TakeKeyboard();
        if (IsSearchField(PlayerNative.GetForegroundWindow()) || _field?.HasFocus == true) ReturnFocusToCard();
        _model.FocusPart = part;
        _automation?.Focus(part);
        RevealFocused(part);
        Invalidate();
        StartFrames(16);
    }

    private void MoveFocus(bool back)
    {
        var order = FocusOrder();
        if (order.Count == 0) return;
        var index = order.IndexOf(_model.FocusPart);
        var next = order[((index < 0 ? (back ? 0 : -1) : index) + (back ? -1 : 1) + order.Count * 2) % order.Count];
        _model.FocusVisible = true;
        FocusPart(next);
    }

    /// <summary>A row reached with Tab scrolls into view.</summary>
    private void RevealFocused(CardPart part)
    {
        var rect = _painter.PartRect(_model.Pane, part);
        if (rect.Height <= 0) return;
        if (CardParts.IsResult(part, out _))
        {
            var bottom = CardLayout.ColumnBottom(PlayerPane.Search, 0);
            if (rect.Y < CardLayout.ResultsTop) _model.ResultsScroll = Math.Max(0, _model.ResultsScroll - (CardLayout.ResultsTop - rect.Y));
            else if (rect.Bottom > bottom) _model.ResultsScroll += rect.Bottom - bottom;
        }
        else if (CardParts.IsQueueRow(part, out _))
        {
            var bottom = CardLayout.ColumnBottom(PlayerPane.Queue, _model.QueueRows);
            if (rect.Y < CardLayout.QueueTop) _model.QueueScroll = Math.Max(0, _model.QueueScroll - (CardLayout.QueueTop - rect.Y));
            else if (rect.Bottom > bottom) _model.QueueScroll += rect.Bottom - bottom;
        }
    }

    /// <summary>Narrator set the seek bar's value: a seek there, as a key would make.</summary>
    internal void SeekFromAutomation(double seconds)
    {
        _log.Debug("Desktop player: UI Automation set the seek bar.");
        _scrub.SeekNow(seconds, flash: true);
    }

    // ---- The wallpaper under a drag ----

    private void PreviewChanged()
    {
        var controller = _controller();
        var time = _preview.AnySongTime();
        if (time is { } t) controller?.SetScrubPreview(t);
        else controller?.ClearScrubPreview();
        ApplyLyricFocus();
    }

    /// <summary>
    /// The wallpaper lyrics under the card: faint while it is up, clearer while its bar is dragged, whole once it has
    /// gone. Called whenever one of those changes and every frame (the engine may arrive after the card shows; a
    /// repeat costs nothing).
    /// </summary>
    private void ApplyLyricFocus()
    {
        if (_engine() is not { } engine) return;
        var target = _shownOn is null ? 1 : PlayerHoverFocus.WallpaperOpacity(hovered: true, previewing: _preview.AnySongTime() is not null);
        var reduce = _model.Look.ReduceMotion;
        if (target != _focusTarget)
        {
            _focusTarget = target;
            _log.Debug($"Desktop player: wallpaper lyrics to {target:0.##} (preview {_controller()?.IsPreviewing}).");
        }

        engine.SetLyricFocus(target, _shownOn is null ? (reduce ? 0.12 : 0.25) : (reduce ? 0.15 : 0.3));
    }

    private double _focusTarget = double.NaN;

    /// <summary><c>--player-debug scrub=x</c>: a drag held at that share of the bar (the preview only; never a seek).</summary>
    private void StartDebugScrub()
    {
        if (_scrub.IsDragging || !_scrub.Current.CanSeek) return;
        var x = _debugScrub * ScrubMath.BarWidth;
        _scrub.MouseDown(x, 0);
        _scrub.MouseDragged(x, 0);
        _log.Info($"Desktop player: holding a scrub at {ScrubMath.Timestamp(_scrub.Session?.TargetTime ?? 0)} (--player-debug).");
        Invalidate();
    }
}

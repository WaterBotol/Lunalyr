using System.Runtime.InteropServices.WindowsRuntime;
using Lunalyr.App.Hosting;
using Lunalyr.App.Interop;
using Lunalyr.Core.Colour;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Shell;
using Lunalyr.Shell.Flyout;
using Lunalyr.Shell.LyricTools;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;

namespace Lunalyr.App.Tray;

/// <summary>
/// The tray icon's flyout: a borderless, always-on-top Acrylic window beside the taskbar, with Windows 11's rounded
/// corners, following light/dark and the accent colour. It opens on a left click, and closes on a click elsewhere
/// (deactivation), Esc, or another click on the icon. Its state comes from <see cref="TrayFlyoutState"/> (tested in
/// Lunalyr.Shell); this class only draws it and passes clicks on to <see cref="AppHost"/>.
/// </summary>
public sealed partial class TrayFlyout : Window
{
    /// <summary>The panel's width, as the Mac's menu-bar panel (364 points).</summary>
    private const double WidthDip = 364;

    /// <summary>A click on the icon this soon after the flyout closed was the click that closed it: don't reopen.</summary>
    private const long ReopenGuardMs = 300;

    private readonly AppHost _host;
    private readonly nint _hwnd;
    private readonly FlyoutBackdrop _backdrop = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private PixelRect _anchor;
    private bool _placedOnce;
    private bool _updating;
    private long _hiddenAt;
    private bool? _pending;
    private long _pendingAt;
    private string? _transportProblem;
    private CoverArt? _coverShown;
    private TrayFlyoutState? _state;
    private int _chromeHeight;
    private FocusState? _focusWhenPlaced;
    private bool _capturing;

    internal TrayFlyout(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Title = FlyoutText.WindowTitle;
        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, e) =>
        {
            // Alt+F4 hides it like Esc; it is made once and kept until Lunalyr quits.
            if (_host.IsQuitting) return;
            e.Cancel = true;
            Hide();
        };
        SystemBackdrop = _backdrop;
        Activated += OnActivated;
        Root.ActualThemeChanged += (_, _) => ApplyTheme();
        Panel.SizeChanged += (_, _) => Place();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => RefreshLive();

        SetUpStaticContent();
        SetUpMusicVideo();
        ApplyTheme();
    }

    /// <summary>Whether the flyout is on screen.</summary>
    internal bool IsOpen => AppWindow.IsVisible;

    /// <summary>A click on the tray icon: opens the flyout beside <paramref name="anchor"/>, or closes it.</summary>
    internal void Toggle(PixelRect anchor, bool keyboard)
    {
        if (IsOpen)
        {
            Hide();
            return;
        }

        // The click that just closed it (by taking the focus away) must not reopen it.
        if (Environment.TickCount64 - _hiddenAt < ReopenGuardMs) return;
        Open(anchor, keyboard);
    }

    internal void Open(PixelRect anchor, bool keyboard)
    {
        _anchor = anchor;
        _transportProblem = null;
        Refresh();
        if (!_placedOnce)
        {
            // The first time, the panel has never been measured: show it off screen, then place it once it has been laid out.
            AppWindow.MoveAndResize(new RectInt32(-32000, -32000, FlyoutPlacement.ToPixels(WidthDip, 96), 600));
        }
        else
        {
            Place();
        }

        Activate();
        Win32.SetForegroundWindow(_hwnd);
        _timer.Start();
        var how = keyboard ? FocusState.Keyboard : FocusState.Programmatic;
        // The first time the panel isn't laid out yet: the focus goes in once it is placed.
        if (_placedOnce) Focus(how);
        else _focusWhenPlaced = how;
        _host.Log.App.Info("Tray panel opened.");
    }

    /// <summary>
    /// <c>--flyout-capture</c> (checks): the flyout as it opens, but off screen, not topmost and never activated (no
    /// focus goes in); after <paramref name="after"/> it is written as a PNG with PrintWindow, then <paramref name="then"/>.
    /// </summary>
    /// <summary>The panel's content, drawn by XAML (RenderTargetBitmap), over the theme's base colour, as a PNG.</summary>
    private async Task WriteContentPng(string path)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(Panel);
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) return;
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        var (r, g, b) = Root.ActualTheme == ElementTheme.Dark ? (0x20, 0x20, 0x20) : (0xF3, 0xF3, 0xF3);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var keep = 255 - pixels[i + 3];
            pixels[i] = (byte)(pixels[i] + b * keep / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] + g * keep / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] + r * keep / 255);
            pixels[i + 3] = 255;
        }
        await System.IO.File.WriteAllBytesAsync(path, Ticker.TickerPng.FromBgra(pixels, bitmap.PixelWidth, bitmap.PixelHeight));
        _host.Log.App.Info($"Tray panel content written to {path} ({bitmap.PixelWidth}×{bitmap.PixelHeight}).");
    }

    internal void CaptureQuietly(PixelRect anchor, string path, TimeSpan after, Action then)
    {
        _capturing = true;
        _anchor = anchor;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;
        Refresh();
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, FlyoutPlacement.ToPixels(WidthDip, 96), 600));
        AppWindow.Show(activateWindow: false);
        Win32.SendToBottom(_hwnd);
        _timer.Start();
        var started = DateTime.UtcNow;
        var wait = DispatcherQueue.CreateTimer();
        wait.Interval = TimeSpan.FromMilliseconds(250);
        wait.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - started < after) return;
            wait.Stop();
            Refresh();
            Place();
            // One more layout pass for what the refresh changed, then the picture.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    // The panel's content drawn by XAML itself as well: the window's pixels are blank while the
                    // displays are off (an overnight check), the content is not.
                    await WriteContentPng(System.IO.Path.ChangeExtension(path, null) + "-content.png");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _host.Log.App.Warn("Couldn't draw the tray panel's content for the capture.", ex);
                }
                try
                {
                    var size = Onboarding.WindowCapture.WritePng(_hwnd, path);
                    _host.Log.App.Info(size is { } s
                        ? $"Tray panel capture written to {path} ({s.Width}×{s.Height}); foreground: {Onboarding.WindowCapture.IsForeground(_hwnd)}."
                        : "Tray panel capture couldn't be drawn.");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _host.Log.App.Warn($"Couldn't write the tray panel capture to {path}.", ex);
                }
                then();
            });
        };
        wait.Start();
        _host.Log.App.Info($"Tray panel opened off screen for --flyout-capture (captured after {after.TotalSeconds:0.#} s).");
    }

    internal void Hide()
    {
        if (!AppWindow.IsVisible) return;
        _timer.Stop();
        AppWindow.Hide();
        _hiddenAt = Environment.TickCount64;
    }

    /// <summary>The controller's status or the settings changed: show the new state if open.</summary>
    internal void RefreshIfOpen()
    {
        if (IsOpen) Refresh();
    }

    // ---- Window behaviour ----

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // A click anywhere else takes the focus away: the flyout goes, as the system's own do.
        _host.Log.App.Debug($"Tray panel: {args.WindowActivationState}.");
        if (_capturing) return;
        if (args.WindowActivationState == WindowActivationState.Deactivated) Hide();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // An open drop-down handles Esc itself (and closes) before this sees it.
        if (e.Key == VirtualKey.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    /// <summary>Sizes the window to its content (up to the work area, which then scrolls) and puts it beside the taskbar.</summary>
    private void Place()
    {
        var (monitor, work, dpi) = FlyoutNative.MonitorAt(_anchor);
        Panel.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        var contentHeight = Panel.DesiredSize.Height;
        if (contentHeight <= 0) return;
        if (_chromeHeight == 0 && AppWindow.IsVisible) _chromeHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
        // The window's rectangle includes invisible resize borders; the gap to the taskbar is measured from what shows.
        var hidden = FlyoutNative.InvisibleBorders(_hwnd);
        var width = FlyoutPlacement.ToPixels(WidthDip, dpi) + Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width) - hidden.Left - hidden.Right;
        var height = FlyoutPlacement.ToPixels(contentHeight, dpi) + _chromeHeight - hidden.Top - hidden.Bottom;
        var visible = FlyoutPlacement.Place(width, height, _anchor, monitor, work, FlyoutPlacement.ToPixels(FlyoutPlacement.MarginDip, dpi));
        var rect = new PixelRect(visible.Left - hidden.Left, visible.Top - hidden.Top, visible.Right + hidden.Right, visible.Bottom + hidden.Bottom);
        // --flyout-capture: the size it would have beside the taskbar, but off screen.
        if (_capturing) rect = new PixelRect(-32000, -32000, -32000 + rect.Width, -32000 + rect.Height);
        var current = new PixelRect(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Position.X + AppWindow.Size.Width,
            AppWindow.Position.Y + AppWindow.Size.Height);
        if (current != rect) AppWindow.MoveAndResize(new RectInt32(rect.Left, rect.Top, rect.Width, rect.Height));
        _placedOnce = true;
        if (_focusWhenPlaced is { } how && !_capturing)
        {
            _focusWhenPlaced = null;
            Focus(how);
        }
    }

    private void ApplyTheme()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        _backdrop.SetTheme(dark);
        FlyoutNative.StyleWindow(_hwnd, dark);
    }

    private void Focus(FocusState how)
    {
        Control[] order = [PlayButton, PreviousButton, NextButton, EarlierButton, _modeTiles[0], SettingsButton];
        foreach (var control in order)
        {
            if (control.IsEnabled && control.Visibility == Visibility.Visible && control.Focus(how)) return;
        }
    }

    // ---- Content ----

    private void SetUpStaticContent()
    {
        AutomationProperties.SetName(Root, FlyoutText.WindowTitle);
        AutomationProperties.SetName(Transport, FlyoutText.MusicPlayback);
        AutomationProperties.SetName(PreviousButton, FlyoutText.PreviousTrack);
        AutomationProperties.SetName(NextButton, FlyoutText.NextTrack);
        ToolTipService.SetToolTip(PreviousButton, FlyoutText.PreviousTrack);
        ToolTipService.SetToolTip(NextButton, FlyoutText.NextTrack);

        TimingHeader.Text = FlyoutText.LyricTiming;
        EarlierButton.Content = FlyoutText.Earlier;
        LaterButton.Content = FlyoutText.Later;
        AutomationProperties.SetName(EarlierButton, FlyoutText.LyricsEarlier);
        AutomationProperties.SetHelpText(EarlierButton, FlyoutText.EarlierHelp);
        ToolTipService.SetToolTip(EarlierButton, FlyoutText.EarlierHelp);
        AutomationProperties.SetName(LaterButton, FlyoutText.LyricsLater);
        AutomationProperties.SetHelpText(LaterButton, FlyoutText.LaterHelp);
        ToolTipService.SetToolTip(LaterButton, FlyoutText.LaterHelp);
        AutomationProperties.SetName(ResetButton, FlyoutText.ResetSongOffset);
        AutomationProperties.SetHelpText(ResetButton, FlyoutText.ResetHelp);
        ToolTipService.SetToolTip(ResetButton, FlyoutText.ResetHelp);

        TapSyncButton.Content = FlyoutText.TapToSync;
        AutomationProperties.SetName(TapSyncButton, "Tap to sync");
        AutomationProperties.SetHelpText(TapSyncButton, FlyoutText.TapToSyncHelp);
        ToolTipService.SetToolTip(TapSyncButton, FlyoutText.TapToSyncHelp);
        TapUndoButton.Content = LyricToolsText.FlyoutUndoTapSync;
        AutomationProperties.SetName(TapUndoButton, LyricToolsText.FlyoutUndoTapSync);
        AutomationProperties.SetHelpText(TapUndoButton, LyricToolsText.UndoTapToSyncHelp);
        ToolTipService.SetToolTip(TapUndoButton, LyricToolsText.UndoTapToSyncHelp);

        AutomationProperties.SetName(SearchButton, FlyoutText.SearchYouTube);
        AutomationProperties.SetHelpText(SearchButton, FlyoutText.SearchYouTubeHelp);
        ToolTipService.SetToolTip(SearchButton, FlyoutText.SearchYouTubeHelp);

        AutomationProperties.SetName(OptionsButton, FlyoutText.WallpaperOptions);
        ToolTipService.SetToolTip(OptionsButton, FlyoutText.OptionsHelp);
        AutomationProperties.SetName(ModeTiles, FlyoutText.WallpaperType);
        BuildModeTiles();

        // Items, not strings: "Custom Mix" says which layout the song took ("Custom Mix · Hinge") without being replaced.
        foreach (var layout in TrayFlyoutState.Layouts) LayoutBox.Items.Add(new ComboBoxItem { Content = layout.Title });
        AutomationProperties.SetName(LayoutBox, FlyoutText.Layout);

        AutomationProperties.SetName(ReadingsToggle, FlyoutText.ReadingsTitle);
        AutomationProperties.SetHelpText(ReadingsToggle, FlyoutText.ReadingsHint);
        ToolTipService.SetToolTip(ReadingsToggle, FlyoutText.ReadingsTitle);
        AutomationProperties.SetName(CreditsToggle, FlyoutText.CreditsTitle);
        AutomationProperties.SetHelpText(CreditsToggle, FlyoutText.CreditsHint);
        ToolTipService.SetToolTip(CreditsToggle, FlyoutText.CreditsTitle);
        ToolTipService.SetToolTip(DiscordToggle, "Show what's playing on your Discord profile");
        PausedNote.Text = FlyoutText.PausedNote;

        AutomationProperties.SetName(SettingsButton, FlyoutText.Settings);
        AutomationProperties.SetName(QuitButton, FlyoutText.Quit);
        // Ctrl+, and Ctrl+Q while the panel has the focus, as ⌘, and ⌘Q in the Mac's.
        SettingsButton.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = (VirtualKey)188 });
        QuitButton.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = VirtualKey.Q });
        SettingsButton.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        QuitButton.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private TrayFlyoutState BuildState()
    {
        var controller = _host.Controller;
        if (_pending is not null && Environment.TickCount64 - _pendingAt > (long)FlyoutText.PendingTimeout.TotalMilliseconds) _pending = null;
        return TrayFlyoutState.From(new FlyoutInputs
        {
            Status = controller?.Status,
            Settings = _host.Settings.Current,
            Capabilities = controller?.Monitor.Capabilities ?? PlayerCapabilities.None,
            PlaybackBlocked = _host.Guard.Reason,
            SongTimingAvailable = controller?.CanAdjustSongOffset ?? false,
            SongOffset = controller?.SongOffset ?? 0,
            Lyric = controller?.CurrentLyric() ?? LyricNow.None,
            PendingPlaying = _pending,
            ShowingLayout = controller?.ShownComposition,
        });
    }

    /// <summary>Everything, from the controller and the settings.</summary>
    internal void Refresh()
    {
        var state = BuildState();
        _state = state;
        RefreshLyricCardTile();
        _updating = true;
        try
        {
            TitleText.Text = state.Title;
            SubtitleText.Text = state.Subtitle;
            SubtitleText.Visibility = state.Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            PlayerText.Text = state.Player ?? "";
            PlayerText.Visibility = state.Player is null ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(TitleText, state.HasTrack ? $"{state.Title}, {state.Subtitle}" : state.Title);
            ShowCover();
            ShowTransport(state);
            ShowLyric(state);

            EarlierButton.IsEnabled = LaterButton.IsEnabled = state.TimingEnabled;
            ResetButton.IsEnabled = state.ResetEnabled;
            ShowTapSync();
            OffsetText.Text = state.OffsetText;
            // No offset reads quieter, as the Mac's secondary style.
            OffsetText.Opacity = state.ResetEnabled ? 1 : 0.62;
            AutomationProperties.SetName(OffsetText, $"{FlyoutText.TimingGroup}: {state.OffsetSpoken}");

            SearchButton.IsEnabled = state.SearchEnabled;
            SearchNote.Text = state.SearchNote ?? "";

            foreach (var tile in _modeTiles) tile.IsChecked = state.Background == (string)tile.Tag;
            if (LayoutBox.Items.Count > 0 && LayoutBox.Items[0] is ComboBoxItem mix && (string)mix.Content != state.CustomMixTitle)
                mix.Content = state.CustomMixTitle;
            if (LayoutBox.SelectedIndex != state.LayoutIndex) LayoutBox.SelectedIndex = state.LayoutIndex;

            PauseToggle.IsChecked = state.WallpaperPaused;
            PauseGlyph.Glyph = state.WallpaperPaused ? "" : "";
            PauseLabel.Text = state.PauseLabel;
            AutomationProperties.SetName(PauseToggle, state.PauseTitle);
            AutomationProperties.SetHelpText(PauseToggle, state.PauseHint);
            AutomationProperties.SetItemStatus(PauseToggle, state.WallpaperPaused ? FlyoutText.WallpaperPausedState : "");
            ToolTipService.SetToolTip(PauseToggle, state.PauseTitle);
            ReadingsToggle.IsChecked = state.ReadingsOn;
            CreditsToggle.IsChecked = state.CreditsOn;
            var settings = _host.Settings.Current;
            DiscordToggle.IsChecked = settings.DiscordPresence;
            HideLyricsToggle.IsChecked = settings.LyricsHidden;
            FloatingToggle.IsChecked = settings.IsFloating;
            EditPositionToggle.IsChecked = settings.IsFloating && _host.FloatingEditing;
            EditPositionToggle.IsEnabled = settings.IsFloating;
            // "Stay visible": the floating lyrics don't fade while the pointer is over them.
            StayVisibleToggle.IsChecked = !settings.FloatingFadeUnderPointer;
            StayVisibleToggle.IsEnabled = settings.IsFloating;
            RefreshPlayers(settings.Player);
            PausedNote.Visibility = state.WallpaperPaused ? Visibility.Visible : Visibility.Collapsed;
            RefreshMusicVideo();
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>A few times a second while open: the line being sung, the transport and the cover.</summary>
    private void RefreshLive()
    {
        var state = BuildState();
        _state = state;
        ShowLyric(state);
        ShowTransport(state);
        ShowCover();
        RefreshMusicVideoLive();
    }

    private void ShowTransport(TrayFlyoutState state)
    {
        PreviousButton.IsEnabled = state.Previous.Enabled;
        NextButton.IsEnabled = state.Next.Enabled;
        PlayButton.IsEnabled = state.PlayPause.Enabled;
        PlayGlyph.Glyph = state.ShowsPause ? "" : "";
        AutomationProperties.SetName(PlayButton, state.PlayPause.Title);
        ToolTipService.SetToolTip(PlayButton, state.PlayPause.Title);
        var note = _transportProblem ?? state.TransportNote;
        TransportNote.Text = note ?? "";
        TransportNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowLyric(TrayFlyoutState state)
    {
        switch (state.Lyric)
        {
            case LyricContent.Line line:
                SetLyric(line.Text, line.Next, primary: true);
                break;
            case LyricContent.Between between:
                SetLyric("♪", between.Next, primary: false);
                break;
            default:
                SetLyric(state.LyricPlaceholder, null, primary: false);
                break;
        }

        LyricFooter.Text = state.LyricFooter ?? "";
        AutomationProperties.SetName(LyricBlock, $"{FlyoutText.CurrentLyric}: {state.LyricSpoken}");
    }

    private void SetLyric(string text, string? next, bool primary)
    {
        // The line in the lyric style; a stand-in ("♪", "Looking for lyrics…") quieter, in the theme's secondary text.
        var shown = primary ? LyricLine : LyricQuiet;
        var other = primary ? LyricQuiet : LyricLine;
        if (shown.Text != text) shown.Text = text;
        shown.Visibility = Visibility.Visible;
        other.Visibility = Visibility.Collapsed;
        LyricNext.Text = next ?? "";
        LyricNext.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowCover()
    {
        var cover = _host.Controller?.Scene.Cover;
        if (ReferenceEquals(cover, _coverShown)) return;
        _coverShown = cover;
        if (cover is null)
        {
            CoverImage.Source = null;
            CoverGlyph.Visibility = Visibility.Visible;
            return;
        }

        // Premultiplied BGRA, as WriteableBitmap wants it.
        var bitmap = new WriteableBitmap(cover.Width, cover.Height);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(cover.Pixels, 0, cover.Pixels.Length);
        bitmap.Invalidate();
        CoverImage.Source = bitmap;
        CoverGlyph.Visibility = Visibility.Collapsed;
    }

    private void Announce(FrameworkElement element, string text)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent,
            text, "LunalyrPanel");
    }

    // ---- Actions (each one a click by the user on this panel) ----

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => _ = SendAsync(TransportCommand.Previous);

    private void PlayButton_Click(object sender, RoutedEventArgs e) => _ = SendAsync(TransportCommand.PlayPause);

    private void NextButton_Click(object sender, RoutedEventArgs e) => _ = SendAsync(TransportCommand.Next);

    private async Task SendAsync(TransportCommand command)
    {
        var shownPlaying = _state?.ShowsPause ?? false;
        var outcome = await _host.SendTransportAsync(command, shownPlaying);
        if (outcome == TransportOutcome.Sent && command == TransportCommand.PlayPause)
        {
            // Shown at once; the player confirms it, or after a moment it goes back to what the player says.
            _pending = !shownPlaying;
            _pendingAt = Environment.TickCount64;
        }

        _transportProblem = outcome == TransportOutcome.Failed ? FlyoutText.CommandFailed(_state?.Player) : null;
        RefreshLive();
    }

    private void EarlierButton_Click(object sender, RoutedEventArgs e) => Nudge(AppController.NudgeStep);

    private void LaterButton_Click(object sender, RoutedEventArgs e) => Nudge(-AppController.NudgeStep);

    private void Nudge(double delta)
    {
        if (_host.Controller?.NudgeSongOffset(delta) is { } saved) Announce(OffsetText, FlyoutText.NudgeNote(delta, saved));
        Refresh();
    }

    // ---- The offset typed by hand ----

    private void OffsetText_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ApplyTypedOffset();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Refresh(); // back to the offset as it is
            e.Handled = true;
        }
    }

    private void OffsetText_LostFocus(object sender, RoutedEventArgs e) => ApplyTypedOffset();

    /// <summary>Sets the song's offset to what was typed; anything that isn't an offset puts the current one back.</summary>
    private void ApplyTypedOffset()
    {
        if (_updating || _state is not { } state || OffsetText.Text == state.OffsetText) return;
        if (FlyoutText.ParseOffset(OffsetText.Text) is { } seconds && _host.Controller?.SetSongOffset(seconds) == true)
            Announce(OffsetText, FlyoutText.SpokenOffset(seconds));
        Refresh();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _host.Controller?.ResetSongOffset();
        Announce(OffsetText, FlyoutText.ResetNote);
        Refresh();
        EarlierButton.Focus(FocusState.Keyboard);
    }

    /// <summary>Tap to sync (the Mac's MenuBarLyricTiming): the panel takes the keyboard, so the flyout steps aside.</summary>
    private void TapSyncButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _host.ToggleTapSync();
    }

    private void TapUndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host.Controller?.TapSync is not { CanUndo: true } tapSync || tapSync.LastApplied is not { } applied) return;
        tapSync.Undo();
        Announce(OffsetText, LyricToolsText.Undone(applied.Previous));
        Refresh();
    }

    /// <summary>Tap to sync's row: start or stop, Undo while the last tap sync's offset is in force; shown, but off, with nothing playing.</summary>
    private void ShowTapSync()
    {
        var tapSync = _host.Controller?.TapSync;
        var listening = tapSync?.IsListening == true;
        TapSyncButton.Content = listening ? LyricToolsText.FlyoutStopTapToSync : LyricToolsText.FlyoutTapToSync;
        AutomationProperties.SetName(TapSyncButton, listening ? LyricToolsText.FlyoutStopTapToSync : "Tap to sync");
        TapSyncButton.IsEnabled = listening || _host.Controller?.CanAdjustSongOffset == true;
        TapUndoButton.Visibility = tapSync?.CanUndo == true ? Visibility.Visible : Visibility.Collapsed;
        TapSyncNote.Text = "";
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _host.OpenMusicVideoSearch();
    }

    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _host.OpenSettings();
    }

    private readonly List<ToggleButton> _modeTiles = [];

    private Style ModeTileStyle() => (Style)Root.Resources["ModeTile"];

    /// <summary>
    /// Every wallpaper type as a tile (the Mac's menu-bar grid): five small ones for what Lunalyr draws, three wider ones
    /// for what starts from something of yours. Built here rather than in XAML, from <see cref="TrayFlyoutState.Backgrounds"/>.
    /// Every tile puts its glyph over its name: beside it, "Your wallpaper" and "Image or video" were cut off in the
    /// 364-DIP panel (a horizontal panel gives its text unlimited width, so they were clipped rather than trimmed).
    /// </summary>
    private void BuildModeTiles()
    {
        ModeTiles.Children.Clear();
        ModeTiles.ColumnDefinitions.Clear();
        var rows = new StackPanel { Spacing = 6 };
        var types = TrayFlyoutState.Backgrounds;
        foreach (var row in new[] { types.Take(5).ToArray(), types.Skip(5).ToArray() })
        {
            var line = new Grid { ColumnSpacing = 6 };
            for (var i = 0; i < row.Length; i++)
            {
                var type = row[i];
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var content = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
                content.Children.Add(new FontIcon { Glyph = type.Glyph, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center });
                content.Children.Add(new TextBlock { Text = type.ShortTitle, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                var tile = new ToggleButton
                {
                    Style = ModeTileStyle(), Content = content, Tag = type.Value,
                    Height = 50, Padding = new Thickness(2), IsEnabled = type.Available,
                };
                AutomationProperties.SetName(tile, type.Title);
                AutomationProperties.SetHelpText(tile, type.Summary);
                ToolTipService.SetToolTip(tile, $"{type.Title}: {type.Summary}");
                tile.Click += ModeTile_Click;
                Grid.SetColumn(tile, i);
                line.Children.Add(tile);
                _modeTiles.Add(tile);
            }
            rows.Children.Add(line);
        }
        ModeTiles.Children.Add(rows);
    }

    private void ModeTile_Click(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not ToggleButton { Tag: string value }) return;
        // Imported image or video with nothing imported: the panel opens no pickers of its own, it sends you to Settings.
        if (value == "custom" && !Lunalyr.Platform.Wallpaper.CustomWallpaperStore.IsAvailable(_host.Settings.Current.WallpaperPath))
        {
            Hide();
            _host.OpenSettingsPage("wallpaper");
            return;
        }
        _host.Settings.Update(s => s with { BackgroundMode = value });
        Refresh();
    }

    private void LayoutBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || LayoutBox.SelectedIndex < 0) return;
        var value = TrayFlyoutState.Layouts[LayoutBox.SelectedIndex].Value;
        _host.Settings.Update(s => s with { LyricComposition = value });
    }

    private void PauseToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _host.TogglePause();
        Refresh();
    }

    private void ReadingsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var on = ReadingsToggle.IsChecked == true;
        _host.Settings.Update(s => s with { RomanizationDisplay = TrayFlyoutState.ReadingsValue(on, s.RomanizationDisplay) });
        Refresh();
    }

    // ---- The Mac panel's rows Windows had not (the owner, 15 Nov 2026): Player, Hide lyrics, Floating, Edit position ----

    private IReadOnlyList<string> _playersShown = [];

    /// <summary>The players offered (Automatic, Apple Music, Spotify, each app sharing now, the current one), as Settings › Music players.</summary>
    private void RefreshPlayers(string? current)
    {
        IReadOnlyList<Lunalyr.Core.NowPlaying.MediaSourceApp> apps;
        try
        {
            apps = _host.SharingPlayers();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            apps = [];
        }
        var offered = Lunalyr.Core.NowPlaying.PlayerChoice.Offered(apps, current);
        if (!offered.SequenceEqual(_playersShown))
        {
            PlayerBox.Items.Clear();
            foreach (var choice in offered)
                PlayerBox.Items.Add(new ComboBoxItem { Content = Lunalyr.Core.NowPlaying.PlayerChoice.Title(choice), Tag = choice });
            _playersShown = offered;
        }
        var wanted = Lunalyr.Core.NowPlaying.PlayerChoice.Normalize(current);
        var index = -1;
        for (var i = 0; i < _playersShown.Count; i++) if (_playersShown[i] == wanted) index = i;
        if (PlayerBox.SelectedIndex != index) PlayerBox.SelectedIndex = index;
    }

    private void PlayerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || PlayerBox.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        _host.Settings.Update(s => s with { Player = value });
    }

    private void HideLyricsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var hide = HideLyricsToggle.IsChecked == true;
        _host.Settings.Update(s => s with { LyricsHidden = hide });
        Refresh();
    }

    private void FloatingToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var floating = FloatingToggle.IsChecked == true;
        _host.Settings.Update(s => s.WithLyricsSurface(floating ? Lunalyr.Core.Floating.LyricsSurface.Floating : Lunalyr.Core.Floating.LyricsSurface.Desktop));
        Refresh();
    }

    private void StayVisibleToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var stay = StayVisibleToggle.IsChecked == true;
        _host.Settings.Update(s => s with { FloatingFadeUnderPointer = !stay });
        Refresh();
    }

    private void EditPositionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _host.SetFloatingEditing(EditPositionToggle.IsChecked == true);
        Refresh();
    }

    /// <summary>Discord status on or off; the rest of it (what it shows) is in Settings.</summary>
    private void DiscordToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var on = DiscordToggle.IsChecked == true;
        _host.Settings.Update(s => s with { DiscordPresence = on });
        Refresh();
    }

    private void CreditsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var on = CreditsToggle.IsChecked == true;
        _host.Settings.Update(s => s with { CreditsLayer = on });
        Refresh();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _host.OpenSettings();
    }

    private void QuitButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _host.Quit();
    }
}

/// <summary>
/// The flyout's Acrylic. Its own controller rather than <c>DesktopAcrylicBackdrop</c>, so it stays Acrylic while the
/// panel is open even when Windows hasn't given it the focus (a panel opened without the foreground right), and follows
/// the panel's theme (which <c>--theme</c> can set) rather than only the system's.
/// </summary>
internal sealed partial class FlyoutBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private bool _dark;

    public void SetTheme(bool dark)
    {
        _dark = dark;
        if (_configuration is not null) _configuration.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _configuration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        };
        _controller = new DesktopAcrylicController();
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}

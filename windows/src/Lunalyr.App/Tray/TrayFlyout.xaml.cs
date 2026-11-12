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
        var current = new PixelRect(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Position.X + AppWindow.Size.Width,
            AppWindow.Position.Y + AppWindow.Size.Height);
        if (current != rect) AppWindow.MoveAndResize(new RectInt32(rect.Left, rect.Top, rect.Width, rect.Height));
        _placedOnce = true;
        if (_focusWhenPlaced is { } how)
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
        Control[] order = [PlayButton, PreviousButton, NextButton, EarlierButton, GradientTile, SettingsButton];
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
        var tiles = TrayFlyoutState.Backgrounds;
        GradientTileText.Text = tiles[0].Title;
        GradientTile.Tag = tiles[0].Value;
        AutomationProperties.SetName(GradientTile, tiles[0].Title);
        AutomationProperties.SetHelpText(GradientTile, tiles[0].Summary);
        ToolTipService.SetToolTip(GradientTile, $"{tiles[0].Title}: {tiles[0].Summary}");
        ColourTileText.Text = tiles[1].Title;
        ColourTile.Tag = tiles[1].Value;
        AutomationProperties.SetName(ColourTile, tiles[1].Title);
        AutomationProperties.SetHelpText(ColourTile, tiles[1].Summary);
        ToolTipService.SetToolTip(ColourTile, $"{tiles[1].Title}: {tiles[1].Summary}");

        foreach (var layout in TrayFlyoutState.Layouts) LayoutBox.Items.Add(layout.Title);
        AutomationProperties.SetName(LayoutBox, FlyoutText.Layout);

        AutomationProperties.SetName(ReadingsToggle, FlyoutText.ReadingsTitle);
        AutomationProperties.SetHelpText(ReadingsToggle, FlyoutText.ReadingsHint);
        ToolTipService.SetToolTip(ReadingsToggle, FlyoutText.ReadingsTitle);
        AutomationProperties.SetName(CreditsToggle, FlyoutText.CreditsTitle);
        AutomationProperties.SetHelpText(CreditsToggle, FlyoutText.CreditsHint);
        ToolTipService.SetToolTip(CreditsToggle, FlyoutText.CreditsTitle);
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
        });
    }

    /// <summary>Everything, from the controller and the settings.</summary>
    internal void Refresh()
    {
        var state = BuildState();
        _state = state;
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

            GradientTile.IsChecked = state.Background == (string)GradientTile.Tag;
            ColourTile.IsChecked = state.Background == (string)ColourTile.Tag;
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
            PausedNote.Visibility = state.WallpaperPaused ? Visibility.Visible : Visibility.Collapsed;
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
        if (_host.SearchYouTube()) Hide();
        else Refresh();
    }

    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _host.OpenSettings();
    }

    private void ModeTile_Click(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not ToggleButton { Tag: string value }) return;
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

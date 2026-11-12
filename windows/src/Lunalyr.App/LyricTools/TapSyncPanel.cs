using System.Runtime.InteropServices;
using Lunalyr.App.Hosting;
using Lunalyr.App.Interop;
using Lunalyr.App.Tray;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Shell.Flyout;
using Lunalyr.Shell.LyricTools;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.System;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;

namespace Lunalyr.App.LyricTools;

/// <summary>
/// The Tap to sync panel (the Mac's LyricTapPanel and LyricTapSyncView, in Windows' style): a small Acrylic window at
/// the top of the display with the pointer, always on top, with no title bar. It takes the keyboard while listening:
/// every plain key is a tap, Esc stops. Once the taps agree the song's offset is saved by the controller
/// (<see cref="LyricTapSyncController"/>) and the result shows with Undo (Ctrl+Z) and Done (Esc or Enter); keys pressed
/// a beat late are kept from the app behind. Clicking elsewhere while listening stops it. A Tap button serves a touch
/// screen or a pointer. Nothing here sends anything to a player: a tap only reads the song's position.
/// </summary>
internal sealed partial class TapSyncPanel : Window
{
    private const double WidthDip = 440;
    private const double TopGapDip = 24;

    private readonly AppHost _host;
    private readonly nint _hwnd;
    private readonly FlyoutBackdrop _backdrop = new();
    private readonly Grid _root;
    private readonly FontIcon _symbol;
    private readonly TextBlock _message;
    private readonly StackPanel _dots;
    private readonly Button _tap;
    private readonly Button _stop;
    private readonly Button _undo;
    private readonly Button _done;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _ticker;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _dismissal;
    private nint _previous;
    private bool _hovering;
    private string? _note;
    private int _announced;
    private string _shownMessage = "";

    internal TapSyncPanel(AppHost host)
    {
        _host = host;
        Title = LyricToolsText.PanelWindowTitle;
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
            // Alt+F4 stops and hides it like Esc; it is made once and kept until Lunalyr quits.
            if (_host.IsQuitting) return;
            e.Cancel = true;
            Controller?.Cancel();
            Dismiss();
        };
        SystemBackdrop = _backdrop;

        _symbol = new FontIcon { FontSize = 26, Width = 32, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(_symbol, AccessibilityView.Raw);
        var heading = new TextBlock { Text = LyricToolsText.PanelTitle, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        _message = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        };
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        _dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 4, 0, 0), Height = 8 };
        AutomationProperties.SetAccessibilityView(_dots, AccessibilityView.Raw);
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(heading);
        texts.Children.Add(_message);
        texts.Children.Add(_dots);

        _tap = Make(LyricToolsText.Tap, LyricToolsText.TapHelp, accent: true, (_, _) => Tap());
        _stop = Make(LyricToolsText.Stop, LyricToolsText.StopHelp, accent: false, (_, _) => Stop());
        _undo = Make(LyricToolsText.Undo, LyricToolsText.UndoHelp, accent: false, (_, _) => Undo());
        AutomationProperties.SetAcceleratorKey(_undo, "Ctrl+Z");
        AutomationProperties.SetName(_undo, LyricToolsText.UndoTapToSync);
        AutomationProperties.SetName(_stop, LyricTapSyncController.StopTitle);
        _done = Make(LyricToolsText.Done, LyricToolsText.DoneHelp, accent: false, (_, _) => Dismiss());
        AutomationProperties.SetAcceleratorKey(_done, "Escape");
        var buttons = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, MinWidth = 84 };
        foreach (var button in new[] { _tap, _stop, _undo, _done }) buttons.Children.Add(button);

        _root = new Grid { Padding = new Thickness(16, 14, 16, 14), ColumnSpacing = 14, Background = new SolidColorBrush(Colors.Transparent) };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.Children.Add(_symbol);
        Grid.SetColumn(texts, 1);
        _root.Children.Add(texts);
        Grid.SetColumn(buttons, 2);
        _root.Children.Add(buttons);
        AutomationProperties.SetName(_root, LyricToolsText.PanelTitle);
        _root.PreviewKeyDown += OnPreviewKeyDown;
        _root.PreviewKeyUp += OnPreviewKeyUp;
        _root.PointerEntered += (_, _) => Hover(true);
        _root.PointerExited += (_, _) => Hover(false);
        _root.ActualThemeChanged += (_, _) => ApplyTheme();
        Content = _root;
        Activated += OnActivated;

        _ticker = DispatcherQueue.CreateTimer();
        _ticker.Interval = TimeSpan.FromMilliseconds(500);
        _ticker.Tick += (_, _) => Expire();
        _dismissal = DispatcherQueue.CreateTimer();
        _dismissal.IsRepeating = false;
        _dismissal.Tick += (_, _) => Dismiss();
        ApplyTheme();

        static Button Make(string text, string help, bool accent, RoutedEventHandler click)
        {
            var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 4, 12, 5) };
            if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            AutomationProperties.SetName(button, text);
            AutomationProperties.SetHelpText(button, help);
            ToolTipService.SetToolTip(button, help);
            button.Click += click;
            return button;
        }
    }

    private LyricTapSyncController? Controller => _host.Controller?.TapSync;

    /// <summary>Starts Tap to sync for the song playing, or stops a session listening (the menu item does both).</summary>
    internal void Toggle()
    {
        if (Controller is not { } controller) return;
        if (controller.IsListening)
        {
            Stop();
            return;
        }

        var foreground = GetForegroundWindow();
        _previous = foreground == _hwnd ? 0 : foreground;
        _note = null;
        _announced = 0;
        _dismissal.Stop();
        controller.Start();
        _host.Log.Lyrics.Info(controller.IsListening
            ? $"Tap to sync: listening (song position {SongPosition()})."
            : $"Tap to sync couldn't start: {controller.Session.Message}");
        Refresh();
        Present();
        if (controller.IsListening) _ticker.Start();
        else ScheduleDismissal();
        _host.LyricToolsChanged();
    }

    private string SongPosition() =>
        _host.Controller?.TapContext().PlaybackTime() is { } seconds
            ? TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";

    // ---- Actions ----

    private void Tap()
    {
        if (Controller is not { IsListening: true } controller) return;
        controller.Tap();
        var taps = controller.Session.Taps.Count;
        if (controller.IsListening && taps > _announced)
        {
            _announced = taps;
            Announce(LyricToolsText.TapAnnouncement(taps));
        }
        if (!controller.IsListening) Finished();
        Refresh();
    }

    private void Stop()
    {
        if (Controller is not { IsListening: true } controller) return;
        controller.Cancel();
        Finished();
        Refresh();
    }

    private void Undo()
    {
        if (Controller is not { CanUndo: true } controller || controller.LastApplied is not { } applied) return;
        controller.Undo();
        // The panel says what Undo did for a moment (the Mac posts it as a note).
        _note = LyricToolsText.Undone(applied.Previous);
        Refresh();
        Announce(_note);
        ScheduleDismissal();
        _host.LyricToolsChanged();
    }

    private void Expire()
    {
        if (Controller is not { } controller || !controller.IsListening)
        {
            _ticker.Stop();
            return;
        }
        controller.ExpireIfDue();
        if (!controller.IsListening)
        {
            Finished();
            Refresh();
        }
    }

    /// <summary>
    /// A session ended. Stopping hands the keyboard back at once; a result keeps it while it shows, so a tap a beat late
    /// can't reach the app behind (Space there could pause the song).
    /// </summary>
    private void Finished()
    {
        _ticker.Stop();
        if (Controller?.Session.Phase is TapSyncPhase.Failed { Failure: TapSyncFailure.Cancelled }) HandBackKeyboard();
        if (Controller is { } controller && controller.Session.Phase is not TapSyncPhase.IdlePhase)
        {
            _host.Log.Lyrics.Info($"Tap to sync ended: {controller.Session.Message}");
            Announce(controller.Session.Message);
        }
        ScheduleDismissal();
        _host.LyricToolsChanged();
    }

    /// <summary>Hides the result, and gives the keyboard back to the window that had it.</summary>
    internal void Dismiss()
    {
        _dismissal.Stop();
        _ticker.Stop();
        if (Controller is { IsListening: false } controller) controller.Dismiss();
        _note = null;
        if (AppWindow.IsVisible)
        {
            var hadKeyboard = GetForegroundWindow() == _hwnd;
            AppWindow.Hide();
            if (hadKeyboard) HandBackKeyboard();
        }
        _previous = 0;
        _host.LyricToolsChanged();
    }

    private void HandBackKeyboard()
    {
        var previous = _previous;
        _previous = 0;
        if (previous != 0 && IsWindowVisible(previous)) Win32.SetForegroundWindow(previous);
    }

    private void ScheduleDismissal()
    {
        _dismissal.Stop();
        if (_hovering || Controller is not { } controller) return;
        var delay = _note is not null ? 3 : LyricTapSyncController.DismissDelay(controller.Session.Phase);
        _dismissal.Interval = TimeSpan.FromSeconds(delay);
        _dismissal.Start();
    }

    /// <summary>The pointer over the panel keeps a result up while it is there.</summary>
    private void Hover(bool inside)
    {
        _hovering = inside;
        if (Controller is not { } controller || controller.IsListening || !AppWindow.IsVisible) return;
        if (inside) _dismissal.Stop();
        else ScheduleDismissal();
    }

    // ---- Keyboard ----

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Controller is not { } controller) return;
        var listening = controller.IsListening;
        var ctrl = IsDown(VirtualKey.Control);
        var shortcut = ctrl || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows);
        var repeat = e.KeyStatus.WasKeyDown || e.KeyStatus.RepeatCount > 1;
        switch (TapSyncKeys.Response(listening, (int)e.Key, shortcut, repeat, textInputFocused: false))
        {
            case TapKeyResponse.Tap:
                Tap();
                e.Handled = true;
                break;
            case TapKeyResponse.Stop:
                Stop();
                e.Handled = true;
                break;
            case TapKeyResponse.Close:
                Dismiss();
                e.Handled = true;
                break;
            case TapKeyResponse.Swallow:
                e.Handled = true;
                break;
            default:
                // Windows' Undo shortcut for the result's Undo.
                if (!listening && ctrl && e.Key == VirtualKey.Z && controller.CanUndo)
                {
                    Undo();
                    e.Handled = true;
                }
                break;
        }
    }

    // A key's release never reaches a focused button either (Space presses a button on release).
    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        var shortcut = IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows);
        if (TapSyncKeys.Response(listening: false, (int)e.Key, shortcut, isRepeat: false, textInputFocused: false) != TapKeyResponse.Pass)
            e.Handled = true;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        // Clicking elsewhere takes the keyboard away: the keys would no longer reach Tap to sync, so it stops rather than
        // waiting blind. The listener chose where the keyboard went, so nothing is brought back in front.
        _previous = 0;
        if (Controller is { IsListening: true })
        {
            Stop();
        }
    }

    // ---- Showing ----

    private void Refresh()
    {
        if (Controller is not { } controller) return;
        var session = controller.Session;
        var listening = controller.IsListening;
        // After Undo: the undo arrow (the Mac's note symbol), in the quiet colour.
        var look = _note is not null ? TapSyncLook.Tapping : TapSyncKeys.Look(session.Phase);
        _symbol.Glyph = _note is not null ? "" : TapSyncKeys.Glyph(look);
        _symbol.Foreground = new SolidColorBrush(Tint(look, _root.ActualTheme == ElementTheme.Dark));
        var message = _note ?? session.Message;
        if (message != _shownMessage)
        {
            _shownMessage = message;
            _message.Text = message;
        }

        // Always laid out, so the text doesn't jump when the result shows.
        var count = session.Taps.Count;
        var dots = Math.Max(LyricTapSync.RequiredTaps, count);
        _dots.Opacity = listening ? 1 : 0;
        while (_dots.Children.Count < dots) _dots.Children.Add(new Ellipse { Width = 7, Height = 7 });
        while (_dots.Children.Count > dots) _dots.Children.RemoveAt(_dots.Children.Count - 1);
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var empty = (Brush)Application.Current.Resources["ControlStrongFillColorDisabledBrush"];
        for (var i = 0; i < _dots.Children.Count; i++) ((Ellipse)_dots.Children[i]).Fill = i < count ? accent : empty;

        var focused = _root.XamlRoot is { } xamlRoot ? FocusManager.GetFocusedElement(xamlRoot) as Button : null;
        Show(_tap, listening);
        Show(_stop, listening);
        Show(_undo, !listening && _note is null && session.Phase is TapSyncPhase.Applied && controller.CanUndo);
        Show(_done, !listening);
        // A button that went away hands the focus on, so keys still reach the panel.
        if (focused is null || focused.Visibility != Visibility.Visible)
            (listening ? _tap : _done).Focus(FocusState.Programmatic);
        Fit();

        static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Windows.UI.Color Tint(TapSyncLook look, bool dark) => look switch
    {
        // Windows' success and caution colours (SystemFillColorSuccess / SystemFillColorCaution).
        TapSyncLook.Success => dark ? Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F) : Windows.UI.Color.FromArgb(255, 0x0F, 0x7B, 0x0F),
        TapSyncLook.Warning => dark ? Windows.UI.Color.FromArgb(255, 0xFC, 0xE1, 0x00) : Windows.UI.Color.FromArgb(255, 0x9D, 0x5D, 0x00),
        _ => dark ? Windows.UI.Color.FromArgb(200, 255, 255, 255) : Windows.UI.Color.FromArgb(158, 0, 0, 0),
    };

    /// <summary>Shows the panel at the top of the display with the pointer and gives it the keyboard.</summary>
    private void Present()
    {
        ApplyTheme();
        var (_, work, dpi) = FlyoutNative.MonitorAt(FlyoutNative.Cursor());
        Fit(dpi);
        var x = work.Left + (work.Width - AppWindow.Size.Width) / 2;
        var y = work.Top + FlyoutPlacement.ToPixels(TopGapDip, dpi);
        AppWindow.Move(new PointInt32(x, y));
        Activate();
        Win32.SetForegroundWindow(_hwnd);
        (Controller?.IsListening == true ? _tap : _done).Focus(FocusState.Programmatic);
    }

    /// <summary>Sizes the window to its content at the panel's width.</summary>
    private void Fit(uint dpi = 0)
    {
        if (dpi == 0) dpi = (uint)Math.Round((_root.XamlRoot?.RasterizationScale ?? 1) * 96);
        _root.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        var height = Math.Max(96, _root.DesiredSize.Height);
        var size = new SizeInt32(FlyoutPlacement.ToPixels(WidthDip, dpi), FlyoutPlacement.ToPixels(height, dpi));
        if (AppWindow.ClientSize.Width != size.Width || AppWindow.ClientSize.Height != size.Height) AppWindow.ResizeClient(size);
    }

    private void ApplyTheme()
    {
        var theme = _host.ThemeOverride ?? _host.Settings.Current.AppTheme;
        _root.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        var dark = _root.ActualTheme == ElementTheme.Dark;
        _backdrop.SetTheme(dark);
        FlyoutNative.StyleWindow(_hwnd, dark);
    }

    private void Announce(string text)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(_message) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_message);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent,
            text, "LunalyrTapSync");
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);
}

using Lunalyr.App.Hosting;
using Lunalyr.App.Settings;
using Lunalyr.Shell.Onboarding;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Onboarding;

/// <summary>
/// The first-run welcome's window (the Mac's OnboardingView and OnboardingWindow, in Windows' controls): the step on
/// screen, and under it the step dots, Back and Continue (Done and Open Settings at the end). A fixed-size Mica window
/// with only a close button, built in code like the settings pages; the choices it offers are the real settings.
/// <para>
/// The Mac window's bring-forward, activation and close plumbing (lines that trace back to the squashed Verso import)
/// is not translated: showing, quiet showing and closing follow this app's own pattern (<see cref="App.ShowSettings"/>,
/// the lyric card window). How it ends is reported once, when the window has closed.
/// </para>
/// Keys: Enter presses the primary button (Continue, Open Settings or Done) unless the focused control takes Enter
/// itself; Alt+Left is Back; Esc or the close button closes it early (which counts as finishing).
/// </summary>
internal sealed partial class OnboardingWindow : Window
{
    private readonly AppHost _host;
    private readonly OnboardingFlow _flow;
    private readonly bool _capture;
    private readonly Func<bool> _isLaunchAtLoginOn;
    private readonly Func<bool, string?> _setLaunchAtLogin;
    private readonly Grid _root = new();
    private readonly Grid _stepHost = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private SettingsContext? _ctx;
    private TextBlock? _stepTitle;
    private Button? _primary;
    private SettingsPreview? _preview;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lyricTimer;
    private OnboardingEnding? _ending;
    private bool _active;
    private bool _closed;

    internal OnboardingWindow(AppHost host, bool firstRun, OnboardingStep step, bool capture,
                              Func<bool> isLaunchAtLoginOn, Func<bool, string?> setLaunchAtLogin)
    {
        _host = host;
        _capture = capture;
        _isLaunchAtLoginOn = isLaunchAtLoginOn;
        _setLaunchAtLogin = setLaunchAtLogin;
        _flow = new OnboardingFlow(firstRun, step, End);
        _flow.StepChanged += (_, change) => ShowStep(change.Forward, animate: true);

        Title = OnboardingCopy.WindowTitle;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Scaled(OnboardingCopy.WindowWidth), Scaled(OnboardingCopy.WindowHeight)));
        Centre();

        // ---- Title bar (see-through, the steps carry their own titles), the step, the footer ----
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titleBar = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        AutomationProperties.SetAccessibilityView(titleBar, AccessibilityView.Raw);
        _root.Children.Add(titleBar);
        SetTitleBar(titleBar);
        Grid.SetRow(_stepHost, 1);
        _root.Children.Add(_stepHost);

        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.Children.Add(_dots);
        Grid.SetColumn(_buttons, 1);
        footer.Children.Add(_buttons);
        var footerBand = new Border { Style = Resource<Style>("OnboardingFooter"), Padding = new Thickness(24, 16, 24, 16), Child = footer };
        Grid.SetRow(footerBand, 2);
        _root.Children.Add(footerBand);

        // ---- Keys ----
        _root.KeyDown += OnKeyDown;
        var back = new KeyboardAccelerator { Key = VirtualKey.Left, Modifiers = VirtualKeyModifiers.Menu };
        back.Invoked += (_, e) =>
        {
            e.Handled = true;
            _flow.Back();
        };
        _root.KeyboardAccelerators.Add(back);
        _root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        Content = _root;
        ApplyTheme();
        Activated += (_, e) => _active = e.WindowActivationState != WindowActivationState.Deactivated;
        // The look step's preview draws only while the window is visible.
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidVisibilityChange || e.DidPresenterChange) _preview?.UpdateVisibility();
        };
        Closed += (_, _) => OnClosed();
        ShowStep(forward: true, animate: false);
    }

    /// <summary>First run (Open Settings at the end) or shown again from Settings.</summary>
    public bool FirstRun => _flow.FirstRun;

    /// <summary>The window is open (shown, and not closed yet).</summary>
    public bool IsShown => !_closed && AppWindow.IsVisible;

    /// <summary>How it ended, raised once, after the window has closed (Closed when it was closed early).</summary>
    public event Action<OnboardingEnding>? Ended;

    private static int Scaled(double dips) => (int)Math.Round(dips * Interop.Win32.GetDpiForSystem() / 96.0);

    private void Centre()
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new Windows.Graphics.PointInt32(work.X + Math.Max(0, (work.Width - size.Width) / 2), work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
    }

    /// <summary>
    /// Shows the window. <paramref name="quiet"/> (<c>--quiet-windows</c>, captures): shown without being activated and
    /// under every other window, so it never takes the foreground or the keyboard.
    /// </summary>
    internal void Open(bool quiet)
    {
        if (quiet)
        {
            AppWindow.Show(activateWindow: false);
            Interop.Win32.SendToBottom(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        }
        else
        {
            AppWindow.Show(activateWindow: true);
            Activate();
        }
        _preview?.UpdateVisibility();
    }

    /// <summary>Opening it again (or Settings, while it is up) brings it forward; quietly, it is only shown.</summary>
    internal void BringForward(bool quiet) => Open(quiet);

    internal void ApplyTheme()
    {
        var theme = _host.ThemeOverride ?? _host.Settings.Current.AppTheme;
        _root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        AppWindow.TitleBar.PreferredTheme = theme switch { "light" => TitleBarTheme.Light, "dark" => TitleBarTheme.Dark, _ => TitleBarTheme.UseDefaultAppMode };
    }

    /// <summary>The song, the players or the settings changed elsewhere: the step on screen follows.</summary>
    internal void RefreshStatus() => _ctx?.QueueRefresh();

    // ---- Ending ----

    private void End(OnboardingEnding ending)
    {
        if (_closed) return;
        _ending = ending;
        Close();
    }

    private void OnClosed()
    {
        if (_closed) return;
        _closed = true;
        _lyricTimer?.Stop();
        _ctx?.Detach();
        _preview?.Dispose();
        _preview = null;
        UnwatchPlayers();
        Ended?.Invoke(_ending ?? OnboardingEnding.Closed);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == VirtualKey.Enter)
        {
            // A held Enter repeats: only a fresh press moves on, so holding it can't run through every step.
            if (e.KeyStatus.WasKeyDown) return;
            // Not taken by the focused control (a button presses itself): the primary button's.
            e.Handled = true;
            _flow.Press(_flow.DefaultButton);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    // ---- Steps ----

    /// <summary>Builds the step on screen, the dots and the buttons; slides it in from the side it came from.</summary>
    private void ShowStep(bool forward, bool animate)
    {
        var step = _flow.Step;
        _lyricTimer?.Stop();
        _lyricTimer = null;
        _ctx?.Detach();
        _preview?.SetActive(false);
        if (_preview?.View is FrameworkElement { Parent: Panel holder } previewView) holder.Children.Remove(previewView);

        _ctx = new SettingsContext(_host.Settings, DispatcherQueue, message => _host.Log.App.Info(message.Replace("in the window", "in the welcome", StringComparison.Ordinal)));
        var content = BuildStep(step, _ctx);

        // The step's frame: its content centred in the room between the title bar and the buttons, 48 from the sides,
        // scrolling only when larger text makes it taller than the window.
        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(48, 8, 48, 16) };
        column.Children.Add(content);
        var fill = new Grid();
        fill.Children.Add(column);
        var scroller = new ScrollViewer { Content = fill, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled };
        scroller.SizeChanged += (_, e) => fill.MinHeight = e.NewSize.Height;
        if (animate && !_capture && AnimationsEnabled())
            column.Transitions = new TransitionCollection { new EntranceThemeTransition { FromHorizontalOffset = forward ? 48 : -48, FromVerticalOffset = 0, IsStaggeringEnabled = false } };
        _stepHost.Children.Clear();
        _stepHost.Children.Add(scroller);

        BuildDots(step);
        BuildButtons();
        if (_active) _primary?.Focus(FocusState.Programmatic);
        if (animate) Announce(step);
    }

    /// <summary>Windows' "Animation effects" (read each time, never changed).</summary>
    private static bool AnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A dot per step, the current one a lit bar; one element to Narrator: "Step 2 of 6", with the step's name.</summary>
    private void BuildDots(OnboardingStep current)
    {
        _dots.Children.Clear();
        foreach (var step in OnboardingSteps.All)
        {
            var dot = new Border { Style = Resource<Style>(step == current ? "OnboardingDotCurrent" : "OnboardingDot") };
            AutomationProperties.SetAccessibilityView(dot, AccessibilityView.Raw);
            _dots.Children.Add(dot);
        }
        AutomationProperties.SetName(_dots, current.Position());
        AutomationProperties.SetHelpText(_dots, current.Title());
        AutomationProperties.SetAutomationControlType(_dots, AutomationControlType.Text);
        AutomationProperties.SetAccessibilityView(_dots, AccessibilityView.Content);
    }

    private void BuildButtons()
    {
        _buttons.Children.Clear();
        _primary = null;
        var all = _flow.Buttons;
        foreach (var kind in all)
        {
            var button = new Button { Content = OnboardingCopy.Label(kind), MinWidth = kind == OnboardingButton.Back ? 80 : 104 };
            if (kind == all[^1]) button.Style = Resource<Style>("AccentButtonStyle");
            var help = OnboardingCopy.Help(kind, _flow);
            AutomationProperties.SetName(button, OnboardingCopy.Label(kind));
            AutomationProperties.SetHelpText(button, help);
            ToolTipService.SetToolTip(button, help);
            button.Click += (_, _) => _flow.Press(kind);
            _buttons.Children.Add(button);
            if (kind == all[^1]) _primary = button;
        }
    }

    /// <summary>Narrator hears the new step's position and name, as VoiceOver moves to the Mac's new title.</summary>
    private void Announce(OnboardingStep step)
    {
        if (_stepTitle is null) return;
        var peer = FrameworkElementAutomationPeer.FromElement(_stepTitle) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_stepTitle);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent,
            OnboardingCopy.Announcement(step), "LunalyrWelcomeStep");
    }

    // ---- Capture (checks) ----

    /// <summary>
    /// <c>--onboarding-capture</c>: once the step has settled (and the look step's preview has drawn), writes the window as
    /// a PNG with PrintWindow, logs whether it ever had the foreground, then runs <paramref name="then"/> (quit).
    /// </summary>
    internal void CaptureWhenSettled(string path, Action then)
    {
        var started = DateTime.UtcNow;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.Tick += (_, _) =>
        {
            var waited = DateTime.UtcNow - started;
            // The look step's preview needs the wallpaper's controller and a few frames; the others only a layout pass.
            var ready = _flow.Step == OnboardingStep.Look
                ? (_host.Controller is not null && waited > TimeSpan.FromSeconds(4)) || waited > TimeSpan.FromSeconds(12)
                : (_host.Controller is not null && waited > TimeSpan.FromSeconds(2)) || waited > TimeSpan.FromSeconds(10);
            if (!ready) return;
            timer.Stop();
            _ctx?.Refresh();
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            try
            {
                var size = WindowCapture.WritePng(hwnd, path);
                _host.Log.App.Info(size is { } s
                    ? $"Welcome capture ({_flow.Step.Name()}) written to {path} ({s.Width}×{s.Height}); foreground: {WindowCapture.IsForeground(hwnd)}."
                    : $"Welcome capture ({_flow.Step.Name()}) couldn't be drawn.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _host.Log.App.Warn($"Couldn't write the welcome capture to {path}.", ex);
            }
            then();
        };
        timer.Start();
    }
}

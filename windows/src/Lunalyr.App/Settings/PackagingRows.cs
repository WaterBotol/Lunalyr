using Lunalyr.App.Hosting;
using Lunalyr.Shell.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>
/// Settings › General's Startup and Updates rows (milestone 8; the Mac's General page "Startup" and "Updates" cards).
/// Launch at login reads Windows' entry back each time the page refreshes; the update row follows the update service.
/// </summary>
internal static class PackagingRows
{
    public static void AddStartup(StackPanel page, SettingsContext ctx, PackagingHooks? hooks)
    {
        if (hooks is null)
        {
            page.Children.Add(Card("Launch at login", "Start Lunalyr in the tray when you sign in to Windows.", "", FixedToggle(false)));
            return;
        }
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0 };
        var card = Card("Launch at login", "", "", toggle);
        var warning = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };
        var openStartup = Button("Open Startup apps", hooks.OpenStartupApps);
        AutomationProperties.SetHelpText(openStartup, "Opens Windows Settings › Apps › Startup, where Windows' own switch for Lunalyr is.");
        warning.ActionButton = openStartup;
        var putting = false;

        void Show()
        {
            var view = hooks.LaunchAtLogin.View();
            putting = true;
            toggle.IsOn = view.IsOn;
            toggle.IsEnabled = view.Enabled;
            putting = false;
            card.Description = view.Note;
            warning.IsOpen = view.Warning is not null;
            warning.Title = view.Warning?.Title ?? "";
            warning.Message = view.Warning?.Message ?? "";
            openStartup.Visibility = view.OpenStartupApps ? Visibility.Visible : Visibility.Collapsed;
        }

        toggle.Toggled += (_, _) =>
        {
            if (putting) return;
            var error = hooks.LaunchAtLogin.Set(toggle.IsOn);
            Show();
            var said = error ?? (toggle.IsOn ? "Lunalyr will start when you sign in." : "Lunalyr won’t start when you sign in.");
            Announce(toggle, said);
        };
        ctx.OnRefresh(_ => Show());
        page.Children.Add(card);
        page.Children.Add(warning);
    }

    public static void AddUpdates(StackPanel page, SettingsContext ctx, string version, PackagingHooks? hooks)
    {
        var shortVersion = version.Split('+')[0];
        if (hooks is null)
        {
            page.Children.Add(Card($"Lunalyr {shortVersion}", UpdateCopy.Unconfigured, "", Button("Check for updates", () => { }, name: "Check for updates")));
            return;
        }
        // Named for what it does (the card would otherwise name it "Lunalyr 0.8.0"); the status is its help text.
        var button = Button("Check for updates", hooks.UpdateAction, name: "Check for updates");
        var card = Card($"Lunalyr {shortVersion}", "", "", button);
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = false, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
        AutomationProperties.SetName(progress, "Update download");
        UpdatePhase? said = null;

        void Show()
        {
            var state = hooks.Updates.State;
            card.Description = state.Status;
            button.Content = UpdateCopy.Button(state);
            AutomationProperties.SetName(button, UpdateCopy.Button(state));
            button.IsEnabled = UpdateCopy.ButtonEnabled(state);
            progress.Visibility = state.Phase is UpdatePhase.Downloading or UpdatePhase.Checking ? Visibility.Visible : Visibility.Collapsed;
            progress.IsIndeterminate = state.Phase == UpdatePhase.Checking;
            progress.Value = state.Progress;
            // Narrator hears where an update got to (not every percent).
            if (said != state.Phase && state.Phase is not (UpdatePhase.Downloading or UpdatePhase.Idle))
            {
                if (said is not null) Announce(button, state.Status);
                said = state.Phase;
            }
        }

        void Changed(UpdateState _) => ctx.QueueRefresh();
        hooks.Updates.Changed += Changed;
        ctx.Detached += () => hooks.Updates.Changed -= Changed;
        ctx.OnRefresh(_ => Show());
        page.Children.Add(card);
        page.Children.Add(progress);
        page.Children.Add(Card("Check for updates automatically",
            "Once a day. A new version downloads in the background and installs only when you choose Restart and update.", "",
            Toggle(ctx, s => s.AutomaticUpdateChecks, (s, v) => s with { AutomaticUpdateChecks = v })));
    }

    private static void Announce(UIElement source, string text)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(source) ?? FrameworkElementAutomationPeer.CreatePeerForElement(source);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent,
            text, "LunalyrPackaging");
    }
}

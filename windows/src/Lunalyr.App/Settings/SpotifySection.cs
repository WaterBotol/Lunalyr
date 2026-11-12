using Lunalyr.Core.Player.Spotify;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>
/// Settings › Music players › Spotify: the client ID of the owner's own developer app (<c>spotifyClientId</c>), the
/// account's state with Sign in and Sign out, and the one-time set-up with the exact redirect address. Off, and saying
/// why, when Spotify isn't part of this run (<c>--demo</c>, <c>--no-network</c>). Sign in opens the system browser
/// only when the user presses it; nothing is sent to Spotify from this page otherwise. Words: <see cref="SpotifyCopy"/>.
/// </summary>
internal static class SpotifySection
{
    private const string Glyph = "";

    public static void Add(StackPanel page, SettingsContext ctx, Func<SpotifyAccount?> account, Func<string?> offReason)
    {
        Section(page, "Spotify");
        page.Children.Add(Caption(SpotifyCopy.Summary));

        // Why it's off this run, when it is.
        var off = Card("Spotify is off for this run", null, Glyph);
        off.Visibility = Visibility.Collapsed;
        page.Children.Add(off);

        // The client ID: saved only when it is a whole one, or emptied (which turns Spotify off).
        var box = new TextBox { Width = 320, PlaceholderText = "32 letters and digits", IsSpellCheckEnabled = false };
        var clientId = Card("Client ID", SpotifyCopy.ClientIdCaption(""), "", box);
        AutomationProperties.SetName(box, "Spotify client ID");
        box.TextChanged += (_, _) =>
        {
            clientId.Description = SpotifyCopy.ClientIdCaption(box.Text);
            if (SpotifyCopy.IsSavable(box.Text)) ctx.Update(s => s with { SpotifyClientId = box.Text.Trim() });
        };
        ctx.OnRefresh(s =>
        {
            // Never overwrite what is being typed: only a saved value that differs from a box not in use.
            if (box.FocusState == FocusState.Unfocused && SpotifyOptions.NormalizeClientId(box.Text) != s.SpotifyClientId) box.Text = s.SpotifyClientId;
            clientId.Description = SpotifyCopy.ClientIdCaption(box.Text);
        });
        page.Children.Add(clientId);

        // The account: its state, Sign in (or Cancel while the browser is open), Sign out.
        var signIn = Button("Sign in", () => { }, "Sign in to Spotify");
        var cancel = Button("Cancel", () => { }, "Cancel the Spotify sign-in");
        var signOut = Button("Sign out", () => { }, "Sign out of Spotify");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(signIn);
        buttons.Children.Add(cancel);
        buttons.Children.Add(signOut);
        var state = Card(SpotifyCopy.State(SpotifyAccountState.NotSetUp), null, "", buttons);
        AutomationProperties.SetName(buttons, "Spotify account");
        page.Children.Add(state);
        var result = Caption("", selectable: true);
        result.Visibility = Visibility.Collapsed;
        page.Children.Add(result);

        CancellationTokenSource? pending = null;
        SpotifyAccount? followed = null;
        void StateChanged(object? sender, EventArgs e) => ctx.QueueRefresh();
        ctx.Detached += () =>
        {
            // The window closed: stop waiting for a browser that may never come back, and stop following the account.
            pending?.Cancel();
            if (followed is not null) followed.StateChanged -= StateChanged;
        };

        signIn.Click += async (_, _) =>
        {
            if (account() is not { } a || pending is not null) return;
            pending = new CancellationTokenSource();
            result.Visibility = Visibility.Collapsed;
            ctx.QueueRefresh();
            try
            {
                // Only ever for the user's own press: this opens Spotify's sign-in page in the system browser.
                var answer = await a.SignInAsync(pending.Token);
                result.Text = answer.Message;
                result.Visibility = Visibility.Visible;
            }
            finally
            {
                pending.Dispose();
                pending = null;
                ctx.QueueRefresh();
            }
        };
        cancel.Click += (_, _) => pending?.Cancel();
        signOut.Click += (_, _) =>
        {
            account()?.SignOut();
            result.Text = SpotifyCopy.SignOutNote;
            result.Visibility = Visibility.Visible;
        };

        // The one-time set-up, with the exact redirect address to copy.
        var steps = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 4) };
        for (var i = 0; i < SpotifyCopy.Steps.Count; i++)
        {
            var step = new TextBlock { Text = $"{i + 1}. {SpotifyCopy.Steps[i]}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            steps.Children.Add(step);
        }

        var redirect = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var address = new TextBox { Text = SpotifyCopy.RedirectUri, IsReadOnly = true, Width = 320 };
        AutomationProperties.SetName(address, "Redirect URI");
        var copy = Button("Copy", () =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(SpotifyCopy.RedirectUri);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        }, "Copy the redirect URI");
        redirect.Children.Add(address);
        redirect.Children.Add(copy);
        steps.Children.Add(redirect);
        var dashboard = new HyperlinkButton { Content = "Open the Spotify developer dashboard", NavigateUri = new Uri(SpotifyCopy.DashboardAddress) };
        steps.Children.Add(dashboard);
        steps.Children.Add(Caption(SpotifyCopy.Requirements));
        var setUp = Expander("Set up your Spotify developer app", "Once: an app of your own on Spotify’s dashboard, then its Client ID here.",
            "", steps);
        page.Children.Add(setUp);
        var openedForSetUp = false;

        ctx.OnRefresh(_ =>
        {
            var a = account();
            var reason = offReason();
            if (a is not null && !ReferenceEquals(a, followed))
            {
                if (followed is not null) followed.StateChanged -= StateChanged;
                followed = a;
                a.StateChanged += StateChanged;
            }

            var available = a is not null;
            off.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            off.Description = SpotifyCopy.Off(reason ?? "");
            box.IsEnabled = available;
            var current = a?.State ?? SpotifyAccountState.NotSetUp;
            state.Title = available ? SpotifyCopy.State(current) : "Not available in this run";
            state.Description = available ? SpotifyCopy.StateDetail(current) : "Sign in and Sign out work in a normal run of Lunalyr.";
            signIn.IsEnabled = available && current == SpotifyAccountState.SignedOut && pending is null;
            cancel.Visibility = current == SpotifyAccountState.SigningIn || pending is not null ? Visibility.Visible : Visibility.Collapsed;
            signOut.IsEnabled = available && current == SpotifyAccountState.SignedIn;
            AutomationProperties.SetHelpText(signIn, state.Description);
            // Not set up yet: the steps are open, once (the user can close them).
            if (available && current == SpotifyAccountState.NotSetUp && !openedForSetUp)
            {
                openedForSetUp = true;
                setUp.IsExpanded = true;
            }
        });
    }
}

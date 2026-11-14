using System.Globalization;
using Lunalyr.App.MusicVideo;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Platform.MusicVideo;
using Lunalyr.Platform.Wallpaper;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

// The wallpaper-types hook (WallpaperPage.cs): Settings › Wallpaper › Music video, ported from the Mac's
// MusicVideoSettings.swift (MusicVideoSection, PastedVideoLink, VideoTimingNudge, VideoTimingOrigin) and its Search
// YouTube… window (MusicVideoSearchWindow.swift), all Lunalyr's own lines; the window's two Verso-attributed lines are
// SwiftUI declarations. Windows' differences: the Music library becomes "Video file" (a file chosen for the song), search
// reads YouTube's site as on the Mac but can use the owner's YouTube Data API key instead ("Search YouTube with", and the
// key's row), and "Show the video on" isn't offered (one background draws on every display). The picker is its own window
// as on the Mac (MusicVideo/MusicVideoSearchWindow.cs), opened by Search… beside the link.
internal static partial class WallpaperPage
{
    // The welcome's music-video step says the same (OnboardingCopy.YouTubePrivacy, tested in Shell).
    public const string PrivacyCaption = Lunalyr.Shell.Onboarding.OnboardingCopy.YouTubePrivacy;

    public const string PrivacyDetail =
        "A video file you chose for a song is always tried first and needs nothing sent. With this on, Lunalyr searches YouTube for “artist title official music video” and picks the artist’s own official upload. The video plays muted in YouTube’s privacy-enhanced player, and each song’s answer is remembered on this PC so it is searched for once. A link you paste for a song plays even with this off; only that video is loaded from YouTube. Search YouTube… works with this off too: it sends only what is in its search box, when you search, and loads the results’ thumbnails.";

    public const string SearchWithDetail =
        "YouTube’s site needs no key: Lunalyr reads its search results page, as a browser would, without signing in or keeping cookies. If YouTube asks for consent or to confirm you’re not a bot, Lunalyr doesn’t get past it: with an API key of your own it searches YouTube’s Data API instead, otherwise it says the search isn’t available. “Use my API key” always searches the Data API.";

    public const string SiteChoice = "Search YouTube’s site (no key)";
    public const string KeyChoice = "Use my API key";

    /// <summary>The Mac's framingCaption (MusicVideoSettings.swift, Test Build 88).</summary>
    public const string FramingCaption =
        "Fill the screen crops the video’s edges. Show the whole video matches its width to the screen, with a blurred copy of the cover above and below.";

    /// <summary>The Mac's framingDetail.</summary>
    public const string FramingDetail =
        "Filled YouTube videos are zoomed a little further so YouTube’s title and logo stay off-screen. Shown whole, nothing is cut from the sides; a squarer video (4:3) loses a little of its top and bottom. To leave out YouTube’s own black bars, Lunalyr asks YouTube for the video’s shape (only its ID is sent). Shown whole, a YouTube video appears a few seconds after it starts playing, once YouTube’s title has faded; its still shows until then.";

    public const string TimingDetail =
        "Official videos often open with a scene, or are a different edit from the album track. A video longer than the song is guessed to have that much intro (Estimated); when searching, an official video as long as the song is preferred, as it needs no guess. Tap to sync: click it, then press any key (or Tap now) the moment the singing starts in the video; later in the song, tap when the next line is sung in the video. Tap again to refine: the taps are averaged. A tap, nudge or Align is kept for the video it was made on. Reset goes back to the estimate.";

    public const string KeySteps =
        "1. In Google Cloud Console (console.cloud.google.com) create a project, or pick one of yours.\n"
        + "2. APIs & Services › Library: enable “YouTube Data API v3”.\n"
        + "3. APIs & Services › Credentials › Create credentials › API key. Restrict it to YouTube Data API v3.\n"
        + "4. Paste the key here. It is kept encrypted for your Windows account and only ever sent to YouTube’s Data API.\n"
        + "Each automatic search uses about 101 of the key’s daily 10,000 quota units (Google’s default), so about 99 songs a day.";

    static partial void AddMusicVideoSections(StackPanel sections, SettingsContext ctx, ref bool added)
    {
        added = true;
        Section(sections, "Music video");
        if (MusicVideoUi.Controller is not { } video)
        {
            sections.Children.Add(Caption("Music video starts once Lunalyr is following now playing."));
            return;
        }

        var note = Caption("");
        AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Polite);
        sections.Children.Add(note);
        var open = new HyperlinkButton { Content = "Open video on YouTube", Padding = new Thickness(0) };
        ToolTipService.SetToolTip(open, "Opens the video in your browser at the song’s position");
        open.Click += (_, _) =>
        {
            if (video.CurrentWatchUrl is { } url) MusicVideoUi.OpenUrl?.Invoke(url);
        };
        sections.Children.Add(open);

        // ---- Search YouTube for videos, and the key it needs ----
        var online = Card("Search YouTube for videos", PrivacyCaption, "",
            Toggle(ctx, s => s.MusicVideoOnline, (s, v) => s with { MusicVideoOnline = v }));
        sections.Children.Add(online);
        sections.Children.Add(Caption(PrivacyDetail));
        sections.Children.Add(Card("Search YouTube with", null, "",
            Combo(ctx, [new SettingsChoice("site", SiteChoice), new SettingsChoice("apiKey", KeyChoice)],
                s => s.MusicVideoSearch, (s, v) => s with { MusicVideoSearch = v }, width: 240)));
        sections.Children.Add(Caption(SearchWithDetail));
        sections.Children.Add(KeyRow(ctx));
        if (MusicVideoUi.YouTubeOffReason is { } off)
            sections.Children.Add(Caption($"YouTube is off for this run ({off}): nothing is looked up or loaded from YouTube. Video files still play."));

        // ---- Without a video ----
        var fallbacks = AppSettings.MusicVideoFallbacks
            .Select(v => new SettingsChoice(v, SettingsCopy.WallpaperTypes.First(t => t.Value == v).Title)).ToList();
        sections.Children.Add(Card("Without a video", "What the wallpaper shows while the song has no video.", "",
            Combo(ctx, fallbacks, s => s.MusicVideoFallback, (s, v) => s with { MusicVideoFallback = v }, width: 220)));

        // ---- This song's video: a file, a link, or a pick from YouTube's results ----
        var fileName = Caption("");
        var choose = Button("Choose…", () => _ = ChooseFileAsync(sections, video), name: "Choose a video file for this song");
        var clearFile = Button("Clear", video.ClearVideoFile, name: "Stop using the video file for this song");
        var fileButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fileButtons.Children.Add(choose);
        fileButtons.Children.Add(clearFile);
        var fileCard = Card("Video file", "A music video on this PC for the playing song; tried first, and nothing is sent anywhere.", "", fileButtons);
        sections.Children.Add(fileCard);
        sections.Children.Add(fileName);

        var link = new TextBox { PlaceholderText = "Paste a link for this song", Width = 260 };
        AutomationProperties.SetName(link, "YouTube link for this song");
        var linkNote = Caption("");
        void UseLink()
        {
            var rejected = !video.UsePastedLink(link.Text);
            linkNote.Text = rejected ? "That isn’t a YouTube video link." : "";
            if (!rejected) link.Text = "";
        }
        link.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                UseLink();
                e.Handled = true;
            }
        };
        var use = Button("Use", UseLink);
        var clearLink = Button("Clear", () =>
        {
            video.ClearPastedLink();
            link.Text = "";
        }, name: "Stop using the pasted link for this song");
        ToolTipService.SetToolTip(clearLink, "Stop using the pasted link for this song");
        // Search…: the Search YouTube window, as on the Mac (MusicVideoSettings' PastedVideoLink row).
        var searchVideo = Button("Search…", () => App.CurrentHost?.OpenMusicVideoSearch(), name: "Search YouTube…");
        ToolTipService.SetToolTip(searchVideo, "Search YouTube for this song and pick the video to use");
        var linkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        linkRow.Children.Add(link);
        linkRow.Children.Add(use);
        linkRow.Children.Add(searchVideo);
        linkRow.Children.Add(clearLink);
        sections.Children.Add(Card("YouTube link", null, "", linkRow));
        sections.Children.Add(linkNote);

        // ---- Video timing ----
        var offset = new TextBlock { MinWidth = 64, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(offset, AccessibilityView.Raw);
        var nudges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        nudges.Children.Add(Nudge("−5", -5, "Video 5 seconds earlier", "The video five seconds earlier in the song"));
        nudges.Children.Add(Nudge("−0.5", -0.5, "Video half a second earlier", "The video half a second earlier in the song"));
        nudges.Children.Add(offset);
        nudges.Children.Add(Nudge("+0.5", 0.5, "Video half a second later", "Skip half a second more of the video’s intro"));
        nudges.Children.Add(Nudge("+5", 5, "Video 5 seconds later", "Skip five seconds more of the video’s intro"));
        var reset = Button("", video.ResetOffset, name: "Reset video timing");
        reset.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons");
        ToolTipService.SetToolTip(reset, "Drop your nudge: back to the estimate");
        nudges.Children.Add(reset);
        AutomationProperties.SetName(nudges, "Video timing for this song");
        var origin = new TextBlock { Style = Resource<Style>("SettingsCaption"), VerticalAlignment = VerticalAlignment.Center };
        var tap = Button("Tap to sync", video.TapSyncButton, name: "Tap to sync the video");
        ToolTipService.SetToolTip(tap, "Click, then press any key the moment the singing starts in the video");
        var originRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        originRow.Children.Add(origin);
        originRow.Children.Add(tap);
        var timing = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        timing.Children.Add(nudges);
        timing.Children.Add(originRow);
        sections.Children.Add(Card("Video timing", "For this song: how far into the video the song begins. Set it with Tap to sync, Align or the nudges.", "", timing));
        var tapNote = Caption("");
        AutomationProperties.SetLiveSetting(tapNote, AutomationLiveSetting.Polite);
        sections.Children.Add(tapNote);
        sections.Children.Add(Caption(TimingDetail));

        // ---- Look: fill or whole for each source (the Mac's framingChoice), then dimming and blur ----
        var framings = MusicVideoFramings.All.Select(f => new SettingsChoice(f.Value(), f.Title())).ToList();
        sections.Children.Add(Card("YouTube videos", null, "",
            Combo(ctx, framings, s => s.MusicVideoFraming, (s, v) => s with { MusicVideoFraming = v }, width: 220)));
        sections.Children.Add(Card("Video files", null, "",
            Combo(ctx, framings, s => s.LibraryVideoFraming, (s, v) => s with { LibraryVideoFraming = v }, width: 220)));
        sections.Children.Add(Caption(FramingCaption));
        sections.Children.Add(Caption(FramingDetail));
        sections.Children.Add(Card("Video dimming", null, "",
            Slider(ctx, 0, AppSettings.MusicVideoDimMax, 0.01, SettingsCopy.Percent, s => s.MusicVideoDim, (s, v) => s with { MusicVideoDim = v })));
        sections.Children.Add(Card("Video blur", null, "",
            Slider(ctx, 0, AppSettings.MusicVideoBlurMax, 0.5, v => v.ToString("0", CultureInfo.InvariantCulture) + " pt",
                s => s.MusicVideoBlur, (s, v) => s with { MusicVideoBlur = v })));
        sections.Children.Add(Caption("Video files play on every display; a YouTube video on the main display only, the others show the album’s colours."));

        Button Nudge(string text, double delta, string name, string help)
        {
            var button = Button(text, () => video.NudgeOffset(delta), name: name);
            ToolTipService.SetToolTip(button, help);
            return button;
        }

        void Refresh()
        {
            var settings = ctx.Current;
            var fallbackTitle = SettingsCopy.WallpaperTypes.First(t => t.Value == settings.MusicVideoFallback).Title;
            note.Text = video.Status is MusicVideoStatus.Showing { Source: MusicVideoSource.YouTube } shown && MusicVideoUi.YouTubeOffReason is { } off
                // A YouTube video was found (a pasted link), but its player isn't made this run: the fallback draws.
                ? $"The video for “{shown.Title}” is on YouTube, and YouTube's player is off for this run ({off}). Showing {fallbackTitle} instead."
                : MusicVideoController.Note(video.Status, settings.MusicVideoOnline, fallbackTitle, video.YouTubeSearchUnavailable) ?? "";
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            open.Visibility = video.ShowingYouTubeId is not null && MusicVideoUi.OpenUrl is not null ? Visibility.Visible : Visibility.Collapsed;
            var hasSong = video.HasSong;
            fileCard.IsEnabled = hasSong;
            clearFile.IsEnabled = video.VideoFile is not null;
            fileName.Text = video.VideoFile is { } path ? $"This song uses {Path.GetFileName(path)}." : "";
            linkRow.Opacity = hasSong ? 1 : 0.5;
            link.IsEnabled = use.IsEnabled = hasSong;
            clearLink.Visibility = video.PastedLink is null ? Visibility.Collapsed : Visibility.Visible;
            if (linkNote.Text != "That isn’t a YouTube video link.")
                linkNote.Text = video.PastedLink is { } pasted ? $"This song uses {pasted}." : "";
            var current = video.CurrentOffset;
            offset.Text = MusicVideoTiming.Format(current);
            AutomationProperties.SetItemStatus(nudges, MusicVideoTiming.Format(current));
            foreach (var child in nudges.Children.OfType<Button>()) child.IsEnabled = hasSong;
            reset.IsEnabled = hasSong && video.OffsetOrigin == MusicVideoOffsetOrigin.Manual;
            origin.Text = MusicVideoController.TimingLabel(video.OffsetOrigin) ?? "";
            tap.Content = MusicVideoTapSync.ButtonTitle(video.TapState);
            tap.IsEnabled = video.CanTapSync;
            tapNote.Text = MusicVideoTapSync.Note(video.TapState) ?? "";
            tapNote.Visibility = tapNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Changed() => sections.DispatcherQueue?.TryEnqueue(Refresh);
        video.Changed += Changed;
        ctx.Detached += () => video.Changed -= Changed;
        ctx.OnRefresh(_ => Refresh());

        // Tap to sync takes a plain key pressed anywhere in this window while it waits (the Mac watches its own windows).
        // Taken before any control sees it (PreviewKeyDown, as the Tap to sync panel does), so Space or Enter on the focused
        // Tap button never taps and then presses the button too.
        sections.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (video.TapState is not MusicVideoTapState.Armed) return;
            var typing = e.OriginalSource is TextBox or PasswordBox;
            var shortcut = IsDown(Windows.System.VirtualKey.Control) || IsDown(Windows.System.VirtualKey.Menu)
                || IsDown(Windows.System.VirtualKey.LeftWindows) || IsDown(Windows.System.VirtualKey.RightWindows);
            if (video.TapKey((int)e.Key, shortcut, e.KeyStatus.WasKeyDown, typing)) e.Handled = true;
        }), handledEventsToo: true);
    }

    private static bool IsDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>The YouTube Data API key: kept encrypted (DPAPI), never shown again once saved.</summary>
    private static Expander KeyRow(SettingsContext ctx)
    {
        var content = new StackPanel { Spacing = 8 };
        var status = Caption("");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var box = new PasswordBox { PlaceholderText = "Paste your API key", Width = 320 };
        AutomationProperties.SetName(box, "YouTube Data API key");
        var save = Button("Save", () => { }, name: "Save the API key");
        var remove = Button("Remove", () => { }, name: "Remove the API key");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(box);
        row.Children.Add(save);
        row.Children.Add(remove);
        content.Children.Add(row);
        content.Children.Add(status);
        content.Children.Add(Caption(KeySteps, selectable: true));
        var keys = MusicVideoUi.Keys;
        void Show()
        {
            if (keys is null)
            {
                status.Text = "YouTube is off for this run, so the key can’t be set.";
                box.IsEnabled = save.IsEnabled = remove.IsEnabled = false;
                return;
            }
            var rejected = MusicVideoUi.Controller?.Status is MusicVideoStatus.Missing { Miss: MusicVideoMiss.KeyRejected };
            status.Text = !keys.HasKey
                ? ctx.Current.MusicVideoSearch == "apiKey"
                    ? "No key yet: “Use my API key” can’t search until you add one, so choose YouTube’s site or add a key. Pasted links still play."
                    : "No key: YouTube’s site is searched without one. A key is used only when the site can’t be searched."
                : rejected ? "YouTube refused this key. Check it is a YouTube Data API v3 key and the API is enabled for its project."
                : "A key is saved (encrypted for your Windows account).";
            remove.IsEnabled = keys.HasKey;
        }
        save.Click += (_, _) =>
        {
            if (keys is null) return;
            if (!YouTubeApiKeyStore.LooksLikeKey(box.Password))
            {
                status.Text = "That doesn’t look like a Google API key (they start “AIza” and are 39 characters).";
                return;
            }
            keys.Set(box.Password);
            box.Password = "";
            Show();
        };
        remove.Click += (_, _) =>
        {
            keys?.Clear();
            Show();
        };
        Show();
        ctx.OnRefresh(_ => Show());
        return Expander("YouTube Data API key", "Optional: your own key, for “Use my API key”, or when YouTube’s site can’t be searched.", "", content,
            expanded: keys is not null && !keys.HasKey && ctx.Current.MusicVideoSearch == "apiKey");
    }

    /// <summary>Windows' file picker for a video, over this window; only ever opened by a click.</summary>
    private static async Task ChooseFileAsync(FrameworkElement anchor, MusicVideoController video)
    {
        try
        {
            if (anchor.XamlRoot?.ContentIslandEnvironment?.AppWindowId is not { } window) return;
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window)
            {
                CommitButtonText = "Use for This Song",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
            };
            foreach (var type in WallpaperPicture.VideoExtensions) picker.FileTypeFilter.Add(type);
            var file = await picker.PickSingleFileAsync();
            if (file?.Path is { } path) video.UseVideoFile(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The picker couldn't be shown; nothing changes.
        }
    }
}

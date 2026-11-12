using Lunalyr.Core.Compositions;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Flyout;

/// <summary>One transport button: whether it can be pressed, and what it is called.</summary>
public sealed record TransportButton(bool Enabled, string Title);

/// <summary>A wallpaper type the flyout offers as a tile.</summary>
/// <summary>A wallpaper-type tile: the stored value, the tile's name, the type's summary, its full name and glyph, and whether this build has it.</summary>
public sealed record BackgroundChoice(string Value, string ShortTitle, string Summary)
{
    public string Title { get; init; } = ShortTitle;

    public string Glyph { get; init; } = "";

    public bool Available { get; init; } = true;
}

/// <summary>A lyric layout the flyout's picker offers.</summary>
public sealed record LayoutChoice(string Value, string Title);

/// <summary>What feeds the flyout: the controller's state, the settings, and the run's switches.</summary>
public sealed record FlyoutInputs
{
    /// <summary>The controller's status, or null while it starts.</summary>
    public AppStatus? Status { get; init; }

    public AppSettings Settings { get; init; } = new();

    /// <summary>What the followed player takes from other apps.</summary>
    public PlayerCapabilities Capabilities { get; init; } = PlayerCapabilities.None;

    /// <summary>Why playback commands and sound are off for this run (<c>--no-playback</c>, <c>--demo</c>), or null.</summary>
    public string? PlaybackBlocked { get; init; }

    /// <summary>Whether a song's timing can be adjusted (its recording is known).</summary>
    public bool SongTimingAvailable { get; init; }

    /// <summary>This song's offset for the source on screen, seconds, positive earlier.</summary>
    public double SongOffset { get; init; }

    public LyricNow Lyric { get; init; } = LyricNow.None;

    /// <summary>A play/pause press the player hasn't confirmed yet (true: it was asked to play).</summary>
    public bool? PendingPlaying { get; init; }
}

/// <summary>
/// Everything the tray flyout shows, worked out from <see cref="FlyoutInputs"/> without any UI, so it is tested. The
/// order and wording follow the Mac's menu-bar panel: the song and its transport, the line being sung with its source,
/// this song's lyric timing, then (Windows) Search YouTube, the wallpaper's quick controls and the footer.
/// </summary>
public sealed record TrayFlyoutState
{
    // ---- Now playing ----

    public bool HasTrack { get; init; }

    public string Title { get; init; } = FlyoutText.NothingPlaying;

    public string Subtitle { get; init; } = FlyoutText.IdlePrompt;

    /// <summary>The player's name ("Spotify", "Lunalyr demo"), or null with nothing playing.</summary>
    public string? Player { get; init; }

    public TransportButton Previous { get; init; } = new(false, FlyoutText.PreviousTrack);

    public TransportButton PlayPause { get; init; } = new(false, FlyoutText.TransportTitle(false));

    public TransportButton Next { get; init; } = new(false, FlyoutText.NextTrack);

    /// <summary>The play/pause button shows pause (the music is playing, or was just asked to).</summary>
    public bool ShowsPause { get; init; }

    /// <summary>Why some or all of the transport can't be used, calmly; null when it all can (or nothing plays).</summary>
    public string? TransportNote { get; init; }

    // ---- The lyric line ----

    public LyricContent Lyric { get; init; } = new LyricContent.Placeholder();

    public string LyricPlaceholder { get; init; } = "";

    /// <summary>"LRCLIB · Word-by-word" while lyrics show, else the controller's status line when it says more.</summary>
    public string? LyricFooter { get; init; }

    /// <summary>What Narrator reads for the lyric block.</summary>
    public string LyricSpoken { get; init; } = "";

    // ---- Lyric timing ----

    public bool TimingEnabled { get; init; }

    public string OffsetText { get; init; } = FlyoutText.FormatOffset(0);

    public string OffsetSpoken { get; init; } = FlyoutText.SpokenOffset(0);

    public bool ResetEnabled { get; init; }

    // ---- Search YouTube ----

    public bool SearchEnabled { get; init; }

    /// <summary>Why Search YouTube can't be used now, or null.</summary>
    public string? SearchNote { get; init; }

    // ---- Wallpaper ----

    public bool WallpaperPaused { get; init; }

    public string PauseLabel { get; init; } = FlyoutText.Pause;

    public string PauseTitle { get; init; } = FlyoutText.PauseWallpaper;

    public string PauseHint { get; init; } = FlyoutText.PauseHint;

    public bool ReadingsOn { get; init; }

    public bool CreditsOn { get; init; }

    /// <summary>The value of the lit wallpaper-type tile (<see cref="Backgrounds"/>), or null when none of them is chosen.</summary>
    public string? Background { get; init; }

    /// <summary>The index in <see cref="Layouts"/> of the chosen layout.</summary>
    public int LayoutIndex { get; init; }

    // ---- Choices ----

    /// <summary>
    /// Every wallpaper type as a tile, as the Mac's menu-bar panel shows them (its <c>WallpaperModeGrid</c>: the five
    /// Lunalyr draws, then the three that start from something of yours), with the tiles' short names and the types'
    /// summaries. Music video isn't in this build, so its tile is off.
    /// </summary>
    public static IReadOnlyList<BackgroundChoice> Backgrounds { get; } =
        [.. SettingsCopy.WallpaperTypes.Select(t => new BackgroundChoice(t.Value, t.ShortTitle, t.Summary) { Glyph = t.Glyph, Available = t.Available, Title = t.Title })];

    /// <summary>"Match the music" and every composition, in the settings' order.</summary>
    public static IReadOnlyList<LayoutChoice> Layouts { get; } = BuildLayouts();

    private static LayoutChoice[] BuildLayouts()
    {
        var titles = Enum.GetValues<CompositionKind>().ToDictionary(k => k.Id(), k => k.Title(), StringComparer.Ordinal);
        return [.. AppSettings.Compositions.Select(id => new LayoutChoice(id,
            id == "automatic" ? FlyoutText.MatchTheMusic : titles.TryGetValue(id, out var title) ? title : id))];
    }

    /// <summary>The background value the tile for <paramref name="backgroundMode"/> stands for.</summary>
    public static string? BackgroundTile(string? backgroundMode) => SettingsCopy.WallpaperTypeFor(backgroundMode);

    /// <summary>Readings on means below the lyrics; off means off. "Replace" counts as on, and stays until switched off.</summary>
    public static string ReadingsValue(bool on, string current) =>
        on ? (current is "below" or "replace" ? current : "below") : "off";

    public static TrayFlyoutState From(FlyoutInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var status = inputs.Status;
        var settings = inputs.Settings.Normalized();
        var hasTrack = status?.Title is not null;
        var playing = status?.Playback == PlaybackStatus.Playing;
        var shownPlaying = FlyoutText.ShownPlaying(playing, inputs.PendingPlaying);
        var hasPlayer = status?.Player is not null && inputs.Capabilities.App is not null;

        // The transport: each button where the player takes it; a calm line saying why not. The run's switch first,
        // so the demo (whose player takes nothing) says playback is off for the run.
        var caps = inputs.Capabilities;
        bool canPlay = hasPlayer && caps.CanPlayPause, canPrevious = hasPlayer && caps.CanSkipPrevious, canNext = hasPlayer && caps.CanSkipNext;
        string? note = null;
        if (inputs.PlaybackBlocked is { } blocked)
        {
            note = hasPlayer ? FlyoutText.PlaybackOff(blocked) : null;
            canPlay = canPrevious = canNext = false;
        }
        else if (hasPlayer && !(canPlay && canPrevious && canNext))
        {
            note = !canPlay && !canPrevious && !canNext
                ? FlyoutText.NoControls(status!.Player!)
                : string.Join(" ", new[]
                {
                    canPrevious ? null : caps[PlayerControl.Previous].Reason,
                    canPlay ? null : caps[PlayerControl.PlayPause].Reason,
                    canNext ? null : caps[PlayerControl.Next].Reason,
                }.OfType<string>());
        }

        // The lyric block, with the source at its foot; without lyrics, the controller's status line when the
        // placeholder doesn't already say it.
        var lyric = LyricContent.Of(inputs.Lyric);
        var placeholder = FlyoutText.Placeholder(inputs.Lyric, hasTrack);
        var footer = FlyoutText.Source(inputs.Lyric);
        if (footer is null && status is not null && hasTrack
            && status.Status is not (StatusText.LookingUp or StatusText.NoLyrics or StatusText.Showing or StatusText.NothingPlaying))
            footer = status.Status;

        var offset = inputs.SongTimingAvailable ? inputs.SongOffset : 0;
        var tile = BackgroundTile(settings.BackgroundMode);
        var layout = Layouts.ToList().FindIndex(l => l.Value == settings.LyricComposition);

        return new TrayFlyoutState
        {
            HasTrack = hasTrack,
            Title = status?.Title ?? FlyoutText.NothingPlaying,
            Subtitle = FlyoutText.Subtitle(hasTrack, status?.Artist, status?.Status == StatusText.Stream),
            Player = status?.Player,
            Previous = new TransportButton(canPrevious, FlyoutText.PreviousTrack),
            PlayPause = new TransportButton(canPlay, FlyoutText.TransportTitle(shownPlaying)),
            Next = new TransportButton(canNext, FlyoutText.NextTrack),
            ShowsPause = shownPlaying,
            TransportNote = note,
            Lyric = lyric,
            LyricPlaceholder = placeholder,
            LyricFooter = footer,
            LyricSpoken = FlyoutText.Spoken(lyric, placeholder),
            TimingEnabled = hasTrack && inputs.SongTimingAvailable,
            OffsetText = FlyoutText.FormatOffset(offset),
            OffsetSpoken = FlyoutText.SpokenOffset(offset),
            ResetEnabled = hasTrack && inputs.SongTimingAvailable && offset != 0,
            SearchEnabled = hasTrack && inputs.PlaybackBlocked is null,
            SearchNote = inputs.PlaybackBlocked is { } off ? FlyoutText.SearchYouTubeOff(off)
                : hasTrack ? null : FlyoutText.SearchYouTubeNeedsSong,
            WallpaperPaused = settings.WallpaperPaused,
            PauseLabel = settings.WallpaperPaused ? FlyoutText.Resume : FlyoutText.Pause,
            PauseTitle = settings.WallpaperPaused ? FlyoutText.ResumeWallpaper : FlyoutText.PauseWallpaper,
            PauseHint = settings.WallpaperPaused ? FlyoutText.ResumeHint : FlyoutText.PauseHint,
            ReadingsOn = settings.RomanizationDisplay != "off",
            CreditsOn = settings.CreditsLayer,
            Background = tile,
            LayoutIndex = Math.Max(0, layout),
        };
    }
}

// The welcome's moving pictures and the Settings loader, as plain rules the window follows (the Mac's Test Build 88:
// OnboardingClip.swift, WelcomeGlassIntro.swift, OnboardingLayoutChoices.swift and Settings/SettingsLoader.swift, all
// Lunalyr's own lines apart from SwiftUI's reduce-motion declarations, which are not translated). Nothing here plays
// anything: the window makes a muted player only where these say so, and tests check the rules and the files.

using System.Numerics;
using Lunalyr.Core.Lyrics;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Demo;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Onboarding;

/// <summary>
/// A short screen recording of Lunalyr itself, shown in the welcome: the desktop with lyrics, readings, floating lyrics.
/// Each is an H.264 file with no audio track (<c>Assets\Onboarding</c>), with its first frame beside it as a JPEG poster.
/// It plays muted and looping only while its step is on screen and the window is active; with Windows' animation
/// effects off, in a quiet window and in captures, only the poster shows and no player is made.
/// <para>
/// The Mac's <c>OnboardingHero.mp4</c> shows a real album's cover and another app, so Windows ships no hero recording:
/// its poster is Lunalyr's own Broadside drawing of the invented demo song, held still (<see cref="HasVideo"/>). A
/// re-recorded clip can take its place later.
/// </para>
/// </summary>
public enum OnboardingClip
{
    Hero,
    Readings,
    Floating,
}

/// <summary>A point in a unit square (0…1 across and down), as SwiftUI's UnitPoint.</summary>
public readonly record struct UnitPoint(double X, double Y);

/// <summary>A rectangle in DIPs, top-left origin.</summary>
public readonly record struct BoxRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

public static class OnboardingClips
{
    public static IReadOnlyList<OnboardingClip> All { get; } = Enum.GetValues<OnboardingClip>();

    /// <summary>The recordings are 960 × 620.</summary>
    public const double Aspect = 960.0 / 620.0;

    /// <summary>Whether the clip has a recording to play (the hero is a still: see the type's summary).</summary>
    public static bool HasVideo(this OnboardingClip clip) => clip != OnboardingClip.Hero;

    /// <summary>The file's name without its extension: <c>OnboardingHero</c>, <c>OnboardingReadings</c>, <c>OnboardingFloating</c>.</summary>
    public static string Resource(this OnboardingClip clip) => "Onboarding" + clip;

    /// <summary>The recording, relative to the app's folder.</summary>
    public static string VideoPath(this OnboardingClip clip) => Path.Combine(OnboardingAssets.Folder, clip.Resource() + ".mp4");

    /// <summary>Its first frame, relative to the app's folder.</summary>
    public static string PosterPath(this OnboardingClip clip) => Path.Combine(OnboardingAssets.Folder, clip.Resource() + ".jpg");

    /// <summary>Where in the frame to look when the picture is smaller than the recording's detail: the lyrics, not the margins.</summary>
    public static UnitPoint Focus(this OnboardingClip clip) => clip switch
    {
        OnboardingClip.Readings => new(0.5, 0.53),
        OnboardingClip.Floating => new(0.47, 0.62),
        _ => new(0.5, 0.5),
    };

    /// <summary>How far in.</summary>
    public static double Zoom(this OnboardingClip clip) => clip == OnboardingClip.Readings ? 1.6 : 1;

    /// <summary>
    /// Whether a clip plays now rather than holding its poster: live (not a capture or a quiet window), with animation
    /// effects on, in an active window, and never in a test.
    /// </summary>
    public static bool Plays(bool live, bool reduceMotion, bool windowActive, bool isUnitTesting) =>
        live && !reduceMotion && windowActive && !isUnitTesting;

    /// <summary>
    /// The recording's rectangle in a box of <paramref name="width"/> × <paramref name="height"/>: filling it,
    /// <see cref="Zoom"/> times larger, moved so <see cref="Focus"/> sits as near the centre as the edges allow.
    /// </summary>
    public static BoxRect Frame(this OnboardingClip clip, double width, double height)
    {
        var fill = Math.Max(width / Aspect, height) * clip.Zoom();
        var contentWidth = fill * Aspect;
        var focus = clip.Focus();
        var x = Clamp(width / 2 - contentWidth * focus.X, width - contentWidth);
        var y = Clamp(height / 2 - fill * focus.Y, height - fill);
        return new BoxRect(x, y, contentWidth, fill);

        static double Clamp(double value, double lower) => Math.Min(0, Math.Max(lower, value));
    }
}

/// <summary>Where the welcome's media sit next to Lunalyr.exe.</summary>
public static class OnboardingAssets
{
    public static readonly string Folder = Path.Combine("Assets", "Onboarding");

    /// <summary>The glass logo, <c>WelcomeGlass.mp4</c>: silent, square, a few seconds.</summary>
    public static readonly string GlassVideo = Path.Combine(Folder, "WelcomeGlass.mp4");

    /// <summary>The Discord status recording (Discord's member list, the line changing as the song plays).</summary>
    public static readonly string DiscordStatus = Path.Combine(Folder, "DiscordStatus.gif");

    /// <summary>Lunalyr's icon for the welcome's picture (from Assets\Lunalyr.ico, 256 px).</summary>
    public static readonly string Icon = Path.Combine(Folder, "LunalyrIcon.png");
}

/// <summary>
/// The welcome's opening: Lunalyr's logo forming as a drop of liquid glass (<c>WelcomeGlass.mp4</c>, which has no sound
/// track and is played muted regardless), big and centred on a black window. It plays once per launch, then the window
/// cross-fades into the welcome step. A click or Enter skips it; animation effects off, quiet windows and captures skip
/// it altogether (a capture can ask for it with <c>--onboarding-step opening</c>).
/// </summary>
public static class WelcomeGlass
{
    /// <summary>After the video ends, its last frame (the plain logo) holds this long before the welcome fades in.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(0.35);

    /// <summary>The cross-fade into the welcome.</summary>
    public static readonly TimeSpan Fade = TimeSpan.FromSeconds(0.6);

    /// <summary>The longest the opening can stay up if the video never says it has ended, so it can never stick on black.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromSeconds(8);

    /// <summary>The logo's glass tile takes about this share of the 1080 × 1080 frame (the rest is black around it)…</summary>
    public const double TileShare = 0.55;

    /// <summary>…and is shown about this share of the window's height.</summary>
    public const double TileHeightShare = 0.5;

    /// <summary>Whether the opening plays now: once per launch, with motion allowed, outside tests, and only when the video is there.</summary>
    public static bool ShouldPlay(bool reduceMotion, bool alreadyPlayed, bool isUnitTesting, bool hasVideo) =>
        !reduceMotion && !alreadyPlayed && !isUnitTesting && hasVideo;

    /// <summary>Whether the window opens on the opening: on the welcome step, and only where <see cref="ShouldPlay"/> allows.</summary>
    public static bool OpensOnIntro(OnboardingStep step, bool reduceMotion, bool alreadyPlayed, bool isUnitTesting, bool hasVideo) =>
        step == OnboardingStep.Welcome && ShouldPlay(reduceMotion, alreadyPlayed, isUnitTesting, hasVideo);

    /// <summary>The video's side in a window of this size: big enough for the tile to fill half the window's height, never larger than the window.</summary>
    public static double VideoSide(double width, double height) =>
        Math.Min(height * TileHeightShare / TileShare, Math.Min(width, height));
}

/// <summary>
/// The first Settings window of a launch takes a moment to build. Rather than a blank window, it opens at once on
/// Lunalyr's glass logo (<c>WelcomeGlass.mp4</c>, silent, at double speed, the logo filling the window), builds Settings
/// behind it, and fades the logo away as soon as Settings is ready and the glass has formed: never a wait of its own
/// making, <see cref="MinimumShown"/> at most on a fast PC. Once per launch; animation effects off, tests, a missing
/// video and quiet windows (checks) skip it, unless <c>--settings-loader-capture</c> asks for it; a click skips it.
/// </summary>
public static class SettingsLoader
{
    /// <summary>The video plays this much faster than in the welcome.</summary>
    public const double Rate = 2;

    /// <summary>The least time on screen, so the glass has formed (about 1.5 s of the video at double speed) rather than flashing past.</summary>
    public const double MinimumShown = 0.75;

    /// <summary>The fade into Settings, in seconds.</summary>
    public const double Fade = 0.25;

    /// <summary>The video's logo sits in the middle of its square frame: this zoom makes the glass tile large in the window.</summary>
    public const double Zoom = 1.6;

    /// <summary>The Mac's rule: once per launch, with motion allowed, outside tests, and with the video there.</summary>
    public static bool ShouldShow(bool reduceMotion, bool alreadyShown, bool isUnitTesting, bool hasVideo) =>
        !reduceMotion && !alreadyShown && !isUnitTesting && hasVideo;

    /// <summary>
    /// Windows' rule: the Mac's, and never in a quiet window (a check while someone works at the PC) unless a capture asks
    /// for it; a capture also plays it with animation effects off, since it is what is being checked.
    /// </summary>
    public static bool ShouldShow(bool reduceMotion, bool alreadyShown, bool hasVideo, bool quiet, bool capture) =>
        capture ? !alreadyShown && hasVideo : !quiet && ShouldShow(reduceMotion, alreadyShown, isUnitTesting: false, hasVideo);

    /// <summary>How long to keep the logo up once Settings is ready, <paramref name="elapsed"/> seconds after the window opened.</summary>
    public static double Remaining(double elapsed) => Math.Max(0, MinimumShown - Math.Max(0, elapsed));

    /// <summary>The video's side in a window of this size: the shorter side, zoomed (the window clips it).</summary>
    public static double VideoSide(double width, double height) => Math.Min(width, height) * Zoom;
}

/// <summary>
/// The look step's lyric layouts (the Mac's <c>LayoutChoices</c>): a few of the reworked ones, the one in use always
/// among them; clicking one chooses it. The chosen one plays the sample line a few times, the others hold mid-line.
/// </summary>
public static class LayoutChoices
{
    /// <summary>A few of the reworked layouts; the one in use leads when it is another.</summary>
    public static IReadOnlyList<string> Featured { get; } = ["broadside", "portal", "mosaic", "cascade"];

    public static IReadOnlyList<string> Shown(string? current)
    {
        if (current is null or AppSettings.AllLayoutsKey || Featured.Contains(current) || !AppSettings.Compositions.Contains(current)) return Featured;
        return [current, .. Featured.Take(Featured.Count - 1)];
    }

    /// <summary>How many layouts Settings has (Custom Mix aside).</summary>
    public static int AllCount => AppSettings.Compositions.Count(id => id != AppSettings.AllLayoutsKey);
}

/// <summary>
/// One layout setting the sample line (the Mac's <c>LayoutThumbnail</c>): the welcome's sample song as one line of eight
/// words, a word every 0.42 s from one second in; the chosen tile plays it <see cref="Plays"/> times, then holds.
/// </summary>
public static class LayoutThumbnail
{
    /// <summary>The sample song's two lines (the Mac's <c>SampleLyric.lines</c>).</summary>
    public static readonly string[][] SampleLines = [["Static", "on", "the", "radio,"], ["gold", "across", "the", "bay"]];

    public const double Beat = 0.42;

    public static IReadOnlyList<LyricBuilder.Sung> Words { get; } =
        [.. SampleLines.SelectMany(line => line).Select((text, index) => new LyricBuilder.Sung(text, 1 + index * Beat, 1 + index * Beat + Beat * 0.9))];

    public static double LineEnd => 1 + Words.Count * Beat;

    public static double ExitStart => LineEnd + 1.2;

    /// <summary>One play, with a breath before the next.</summary>
    public static double Cycle => ExitStart + 1.4;

    public const int Plays = 3;

    /// <summary>Where a still picture holds: the line just sung in full.</summary>
    public static double Held => LineEnd + 0.9;

    /// <summary>
    /// Where Windows' still tiles hold: the moment the last word has been sung. The Mac's line view is told to keep the
    /// line up until <see cref="ExitStart"/>, so it holds at <see cref="Held"/>; the wallpaper's layers, which Windows'
    /// tiles are drawn with, begin a line's exit as its last word ends.
    /// </summary>
    public static double StillTime => LineEnd + 0.04;

    /// <summary>The height of the stage a tile is laid out on before being shrunk to it (the Mac's 360 × 210 stage).</summary>
    public const double StageHeight = 210;

    /// <summary>The time in the line <paramref name="elapsed"/> seconds after it started playing.</summary>
    public static double Time(double elapsed) => elapsed < Plays * Cycle ? Math.Max(0, elapsed) % Cycle : Held;

    /// <summary>
    /// The sample line as the wallpaper's layers take it (word-timed, through the real parser), over a palette in the
    /// welcome's sample-wallpaper colours, at the largest lyric size the layers allow.
    /// </summary>
    public static SceneSnapshot Scene()
    {
        var payload = LyricBuilder.Ttml("en", Words.ToArray());
        var lyrics = LyricBuilder.Parse(LyricFormat.Ttml, payload, Cycle + 1);
        return new SceneSnapshot
        {
            Lyrics = lyrics,
            Palette = SamplePalette,
            Style = LyricStyle.Default with { SizeFraction = LyricStyle.Default.SizeFraction * 1.5f },
            Duration = Cycle + 1,
        };
    }

    /// <summary>The sample wallpaper's colours (the Mac's <c>SampleWallpaper</c>: warm glows over a deep night).</summary>
    public static Palette SamplePalette { get; } = new(
        new Vector4(0.20f, 0.10f, 0.30f, 1),
        [new(0.99f, 0.56f, 0.32f, 1), new(0.88f, 0.27f, 0.52f, 1), new(0.40f, 0.30f, 0.82f, 1)],
        new Vector4(1, 1, 1, 1),
        new Vector4(1, 0.93f, 0.82f, 1));
}

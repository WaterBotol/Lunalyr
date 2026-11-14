using System.Globalization;
using Lunalyr.Core.Logging;

namespace Lunalyr.Shell;

/// <summary>
/// The command line.
/// <list type="table">
/// <item><term><c>--no-playback</c></term><description>Turns off every playback command to players and every sound
/// Lunalyr could make, for the whole process (the owner's test flag).</description></item>
/// <item><term><c>--data &lt;folder&gt;</c></term><description>Keep settings, logs and caches in this folder instead
/// of <c>%LOCALAPPDATA%\Lunalyr</c> (for testing). Instances with different data folders are separate.</description></item>
/// <item><term><c>--seconds &lt;N&gt;</c></term><description>Quit after N seconds (for checks).</description></item>
/// <item><term><c>--log-level &lt;debug|info|warn|error&gt;</c></term><description>Minimum log level (default info).</description></item>
/// <item><term><c>--settings</c></term><description>Open the settings window (also what a second launch does).</description></item>
/// <item><term><c>--no-network</c></term><description>Don't look lyrics up online (for checks; nothing is sent to any lyric service).</description></item>
/// <item><term><c>--demo</c></term><description>Follow a silent built-in demo player with invented songs and lyrics instead
/// of the system's players; implies <c>--no-network</c> and <c>--no-playback</c>, and keeps its own data folder.</description></item>
/// <item><term><c>--stats &lt;file&gt;</c></term><description>On quitting, write the renderer's frame statistics to this JSON
/// file (for measurements).</description></item>
/// <item><term><c>--settings-page &lt;page&gt;</c>, <c>--theme &lt;light|dark|system&gt;</c></term><description>Open a settings page; the look of
/// Lunalyr's windows (settings, the tray panel) for this run. Windows' own setting is never changed.</description></item>
/// <item><term><c>lunalyr://…</c></term><description>A link to act on (forwarded to the running instance).</description></item>
/// </list>
/// </summary>
public sealed record ShellOptions
{
    public bool NoPlayback { get; init; }

    public string? DataRoot { get; init; }

    public double? QuitAfterSeconds { get; init; }

    public LogLevel LogLevel { get; init; } = LogLevel.Info;

    public bool OpenSettings { get; init; }

    public bool NoNetwork { get; init; }

    /// <summary>The built-in demo player and lyrics instead of the system's players and the lyric services.</summary>
    public bool Demo { get; init; }

    /// <summary>Where to write the renderer's frame statistics on quitting, or null.</summary>
    public string? StatsPath { get; init; }

    /// <summary>
    /// <c>--settings-page &lt;page&gt;</c>: the settings page to show (general, lyric-style, readings, timing, wallpaper,
    /// covers, players, performance, about); implies <c>--settings</c>. A second launch forwards it, so checks and other
    /// surfaces can open a page.
    /// </summary>
    public string? SettingsPage { get; init; }

    /// <summary>
    /// <c>--theme &lt;light|dark|system&gt;</c>: the look of Lunalyr's own windows for this run only (for screenshots);
    /// nothing is saved and Windows' own setting is never touched.
    /// </summary>
    public string? Theme { get; init; }

    /// <summary>
    /// <c>--quiet-windows</c>: for checks while someone works at the PC. The settings window opens without being
    /// activated and at the bottom of the z-order, so it never takes the foreground or the keyboard; capture it with
    /// PrintWindow (tools/capture-window.ps1). Not forwarded to a running instance.
    /// </summary>
    public bool QuietWindows { get; init; }

    /// <summary>
    /// <c>--player-debug show,top,light,dark,reduce-motion,opaque,contrast,scrub=0.6</c>: checks of the desktop player
    /// without moving the pointer or changing Windows' settings. <c>show</c> keeps it up on the main display;
    /// the looks force one accessibility look; <c>scrub=x</c> holds a drag at that share of the seek bar (the
    /// wallpaper follows it, and nothing is ever sent to a player).
    /// </summary>
    public IReadOnlyList<string> PlayerDebug { get; init; } = [];

    /// <summary>
    /// <c>--floating-capture &lt;file&gt;</c>: on quitting, write the floating lyrics window's back buffer as a PNG
    /// (the engine draws a frame for it even while the displays are off), and log the window's styles. For checks.
    /// </summary>
    public string? FloatingCapturePath { get; init; }

    /// <summary>
    /// <c>--lyric-card</c>: open the lyric card window once Lunalyr is up (for checks; with <c>--quiet-windows</c> it opens
    /// unactivated at the bottom, like the settings window). Not forwarded to a running instance.
    /// </summary>
    public bool OpenLyricCard { get; init; }

    /// <summary>
    /// <c>--youtube-search</c>: open the Search YouTube window once Lunalyr is up (for checks, as <c>--lyric-card</c>).
    /// Not forwarded to a running instance.
    /// </summary>
    public bool OpenYouTubeSearch { get; init; }

    /// <summary>
    /// <c>--ticker-capture &lt;file&gt;</c>: on quitting, write the tray lyric strip's last frame as a PNG and log the
    /// strip's styles and whether it is the foreground window. For checks.
    /// </summary>
    public string? TickerCapturePath { get; init; }

    /// <summary>
    /// <c>--discord-pipe &lt;prefix&gt;</c>: Discord status dials <c>\\.\pipe\{prefix}0…9</c> instead of Discord's own
    /// pipes, and runs even under <c>--demo</c> and <c>--no-network</c>, for checks against a pipe of one's own. A prefix
    /// starting with "discord" is refused. Not forwarded to a running instance.
    /// </summary>
    public string? DiscordPipe { get; init; }

    /// <summary>
    /// <c>--hud-capture &lt;file&gt;</c>: each action HUD note's bitmap is also written as a PNG (the window itself is
    /// left out of screen captures), with the window's styles in the log. For checks.
    /// </summary>
    public string? HudCapturePath { get; init; }

    /// <summary>
    /// <c>--flyout-capture &lt;file&gt;</c>: open the tray flyout off screen, never activated, without a click on the icon;
    /// after <see cref="FlyoutCaptureAfter"/> seconds write it as a PNG (PrintWindow), log whether it was ever the foreground
    /// window, and quit. For checks (the sweep, 14 Nov 2026); not forwarded to a running instance.
    /// </summary>
    public string? FlyoutCapturePath { get; init; }

    /// <summary><c>--flyout-capture-after &lt;seconds&gt;</c>: how long the flyout is open before it is captured (default 6).</summary>
    public double FlyoutCaptureAfter { get; init; } = 6;

    /// <summary>
    /// <c>--onboarding-capture &lt;file&gt;</c>: open the welcome (as a first run sees it) without activating it, at the bottom
    /// of the z-order, at <see cref="OnboardingStep"/>; once it has settled, write the window as a PNG (PrintWindow) and quit.
    /// Nothing is recorded as seen. For checks; not forwarded to a running instance.
    /// </summary>
    public string? OnboardingCapturePath { get; init; }

    /// <summary>
    /// <c>--onboarding-step &lt;name&gt;</c>: the step <c>--onboarding-capture</c> shows (welcome, music, look, videos, lyrics,
    /// extras, tray, done; or 1–8), and <c>look-lyrics</c> or <c>opening</c> (<see cref="OnboardingPage"/>).
    /// </summary>
    public Onboarding.OnboardingStep? OnboardingStep { get; init; }

    /// <summary>The page <c>--onboarding-step</c> named: the step, the look step's half, or the glass logo opening.</summary>
    public Onboarding.OnboardingCapturePage? OnboardingPage { get; init; }

    /// <summary>
    /// <c>--settings-loader-capture &lt;file&gt;</c>: open Settings quietly with its loader (the glass logo while it builds,
    /// which quiet windows otherwise skip), write the window as a PNG while the logo is up, and quit. Implies
    /// <c>--settings</c> and <c>--quiet-windows</c>. For checks; not forwarded to a running instance.
    /// </summary>
    public string? SettingsLoaderCapturePath { get; init; }

    /// <summary>
    /// <c>--settings-capture &lt;file&gt;</c>: open Settings quietly (at <c>--settings-page</c>), let it settle, write the
    /// whole page from its title to its last row as a PNG (drawn off the screen, so a long page needs no scrolling), the
    /// window as <c>…-window.png</c> (PrintWindow) and, when the page has closed expanders, <c>…-expanded.png</c> with
    /// them open; log any text cut short; and quit. Implies <c>--settings</c> and <c>--quiet-windows</c>. For checks;
    /// not forwarded to a running instance.
    /// </summary>
    public string? SettingsCapturePath { get; init; }

    /// <summary>
    /// <c>--settings-capture-scale &lt;percent&gt;</c> (dev only, with <c>--settings-capture</c>): size the window as it
    /// would open at that display scale (100–400 %), so a capture shows the layout another scale gets. The pixels are
    /// still drawn at the display's own scale; Windows' setting is never changed.
    /// </summary>
    public double? SettingsCaptureScale { get; init; }

    /// <summary>
    /// <c>lunalyr://…</c> arguments, and every argument after <c>--link</c>: links to act on, each checked before
    /// anything is done (<c>Lunalyr.Shell.Extras.LunalyrLink</c>).
    /// </summary>
    /// <summary>
    /// <c>--login</c>: Windows started Lunalyr at sign-in (the Run value launch at login writes). It starts quietly in the
    /// tray: no settings window and no welcome, and a second launch with it opens nothing either.
    /// </summary>
    public bool Login { get; init; }

    /// <summary><c>--updated &lt;version&gt;</c>: the installer started this copy after updating it; says so once.</summary>
    public string? UpdatedTo { get; init; }

    public IReadOnlyList<string> Links { get; init; } = [];

    /// <summary>Arguments that couldn't be understood, as messages for the log.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public const string LinkScheme = "lunalyr:";

    /// <summary>Every argument after it is a link (see <see cref="Links"/>).</summary>
    public const string LinkFlag = "--link";

    public static ShellOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new ShellOptions();
        var links = new List<string>();
        var problems = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            // "--link": what follows is only ever a link, never a flag, however a page shaped it (the installer registers
            // the scheme as `Lunalyr.exe --link "%1"`, so a link with quotes in it can't add --data or --stats).
            if (string.Equals(arg, LinkFlag, StringComparison.OrdinalIgnoreCase))
            {
                for (i++; i < args.Count; i++) links.Add(args[i]);
                break;
            }

            string? Value()
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[++i];
                problems.Add($"{arg} needs a value.");
                return null;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--no-playback":
                    options = options with { NoPlayback = true };
                    break;
                case "--no-network":
                    options = options with { NoNetwork = true };
                    break;
                case "--settings":
                    options = options with { OpenSettings = true };
                    break;
                case "--login":
                    options = options with { Login = true };
                    break;
                case "--updated":
                    if (Value() is { } updated) options = options with { UpdatedTo = updated };
                    break;
                case "--quiet-windows":
                    options = options with { QuietWindows = true };
                    break;
                case "--lyric-card":
                    options = options with { OpenLyricCard = true };
                    break;
                case "--youtube-search":
                    options = options with { OpenYouTubeSearch = true };
                    break;
                case "--demo":
                    // Silent and offline by definition: no real player is followed and nothing is looked up.
                    options = options with { Demo = true, NoNetwork = true, NoPlayback = true };
                    break;
                case "--settings-page":
                    if (Value() is { } page)
                    {
                        if (page.Trim().Length == 0) problems.Add("--settings-page needs a page.");
                        else options = options with { SettingsPage = page.Trim(), OpenSettings = true };
                    }
                    break;
                case "--theme":
                    if (Value() is { } theme)
                    {
                        if (theme.Trim().ToLowerInvariant() is "light" or "dark" or "system") options = options with { Theme = theme.Trim().ToLowerInvariant() };
                        else problems.Add($"--theme {theme}: expected light, dark or system.");
                    }
                    break;
                case "--floating-capture":
                    if (Value() is { } capture)
                    {
                        if (capture.Trim().Length == 0) problems.Add("--floating-capture needs a file.");
                        else options = options with { FloatingCapturePath = Path.GetFullPath(capture.Trim()) };
                    }
                    break;
                case "--ticker-capture":
                    if (Value() is { } ticker)
                    {
                        if (ticker.Trim().Length == 0) problems.Add("--ticker-capture needs a file.");
                        else options = options with { TickerCapturePath = Path.GetFullPath(ticker.Trim()) };
                    }
                    break;
                case "--discord-pipe":
                    if (Value() is { } pipe)
                    {
                        var prefix = pipe.Trim();
                        if (prefix.Length == 0 || prefix.StartsWith("discord", StringComparison.OrdinalIgnoreCase)
                            || !prefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                            problems.Add($"--discord-pipe {pipe}: expected a pipe name prefix of your own (letters, digits, '-', '_', '.'), not Discord's.");
                        else options = options with { DiscordPipe = prefix };
                    }
                    break;
                case "--hud-capture":
                    if (Value() is { } hud)
                    {
                        if (hud.Trim().Length == 0) problems.Add("--hud-capture needs a file.");
                        else options = options with { HudCapturePath = Path.GetFullPath(hud.Trim()) };
                    }
                    break;
                case "--flyout-capture":
                    if (Value() is { } flyout)
                    {
                        if (flyout.Trim().Length == 0) problems.Add("--flyout-capture needs a file.");
                        else options = options with { FlyoutCapturePath = Path.GetFullPath(flyout.Trim()) };
                    }
                    break;
                case "--flyout-capture-after":
                    if (Value() is { } after)
                    {
                        if (double.TryParse(after, NumberStyles.Float, CultureInfo.InvariantCulture, out var wait) && wait >= 0 && double.IsFinite(wait))
                            options = options with { FlyoutCaptureAfter = wait };
                        else problems.Add($"--flyout-capture-after {after}: expected a number of seconds.");
                    }
                    break;
                case "--onboarding-capture":
                    if (Value() is { } welcome)
                    {
                        if (welcome.Trim().Length == 0) problems.Add("--onboarding-capture needs a file.");
                        else options = options with { OnboardingCapturePath = Path.GetFullPath(welcome.Trim()) };
                    }
                    break;
                case "--onboarding-step":
                    if (Value() is { } stepName)
                    {
                        if (Onboarding.OnboardingSteps.ParsePage(stepName) is { } welcomePage) options = options with { OnboardingStep = welcomePage.Step, OnboardingPage = welcomePage };
                        else problems.Add($"--onboarding-step {stepName}: expected welcome, music, look, look-lyrics, videos, lyrics, extras, tray, done or opening (or 1–8).");
                    }
                    break;
                case "--settings-loader-capture":
                    if (Value() is { } loader)
                    {
                        if (loader.Trim().Length == 0) problems.Add("--settings-loader-capture needs a file.");
                        else options = options with { SettingsLoaderCapturePath = Path.GetFullPath(loader.Trim()), OpenSettings = true, QuietWindows = true };
                    }
                    break;
                case "--settings-capture":
                    if (Value() is { } settingsCapture)
                    {
                        if (settingsCapture.Trim().Length == 0) problems.Add("--settings-capture needs a file.");
                        else options = options with { SettingsCapturePath = Path.GetFullPath(settingsCapture.Trim()), OpenSettings = true, QuietWindows = true };
                    }
                    break;
                case "--settings-capture-scale":
                    if (Value() is { } scaleText)
                    {
                        if (double.TryParse(scaleText.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                            && percent is >= 100 and <= 400)
                            options = options with { SettingsCaptureScale = percent / 100 };
                        else problems.Add($"--settings-capture-scale {scaleText}: expected a percentage from 100 to 400.");
                    }
                    break;
                case "--stats":
                    if (Value() is { } stats)
                    {
                        if (stats.Trim().Length == 0) problems.Add("--stats needs a file.");
                        else options = options with { StatsPath = Path.GetFullPath(stats.Trim()) };
                    }
                    break;
                case "--data":
                    if (Value() is { } folder)
                    {
                        if (folder.Trim().Length == 0) problems.Add("--data needs a folder.");
                        else options = options with { DataRoot = Path.GetFullPath(folder.Trim()) };
                    }
                    break;
                case "--seconds":
                    if (Value() is { } text)
                    {
                        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 && double.IsFinite(seconds))
                            options = options with { QuitAfterSeconds = seconds };
                        else problems.Add($"--seconds {text}: expected a positive number of seconds.");
                    }
                    break;
                case "--player-debug":
                    if (Value() is { } spec)
                        options = options with
                        {
                            PlayerDebug = [.. spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Select(s => s.ToLowerInvariant())],
                        };
                    break;
                case "--log-level":
                    if (Value() is { } level)
                    {
                        if (TryParseLevel(level, out var parsed)) options = options with { LogLevel = parsed };
                        else problems.Add($"--log-level {level}: expected debug, info, warn or error.");
                    }
                    break;
                default:
                    if (arg.StartsWith(LinkScheme, StringComparison.OrdinalIgnoreCase)) links.Add(arg);
                    else problems.Add($"Unknown argument {arg}.");
                    break;
            }
        }

        if (options.OnboardingStep is not null && options.OnboardingCapturePath is null)
            problems.Add("--onboarding-step goes with --onboarding-capture; on its own it does nothing.");
        if (options.SettingsCaptureScale is not null && options.SettingsCapturePath is null)
            problems.Add("--settings-capture-scale goes with --settings-capture; on its own it does nothing.");
        return options with { Links = links, Problems = problems };
    }

    public static bool TryParseLevel(string text, out LogLevel level)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "debug": level = LogLevel.Debug; return true;
            case "info": level = LogLevel.Info; return true;
            case "warn" or "warning": level = LogLevel.Warn; return true;
            case "error": level = LogLevel.Error; return true;
            default: level = LogLevel.Info; return false;
        }
    }
}

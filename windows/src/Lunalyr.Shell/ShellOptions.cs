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

    public IReadOnlyList<string> Links { get; init; } = [];

    /// <summary>Arguments that couldn't be understood, as messages for the log.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public const string LinkScheme = "lunalyr:";

    public static ShellOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new ShellOptions();
        var links = new List<string>();
        var problems = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
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
                case "--quiet-windows":
                    options = options with { QuietWindows = true };
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

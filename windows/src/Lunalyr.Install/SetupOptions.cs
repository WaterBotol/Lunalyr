namespace Lunalyr.Install;

/// <summary>What <c>Lunalyr Setup.exe</c> was asked to do.</summary>
public enum SetupMode
{
    Install,
    Uninstall,
}

/// <summary>
/// The installer's command line:
/// <list type="bullet">
/// <item>(nothing): install for this user, asking first.</item>
/// <item><c>--update</c>: started by Lunalyr to apply a downloaded update: no questions, and Lunalyr starts again
///   afterwards (with <c>--updated</c>).</item>
/// <item><c>--uninstall</c>: remove (Settings › Apps runs this).</item>
/// <item><c>--silent</c>: no windows at all; the exit code and the log say how it went.</item>
/// <item><c>--no-launch</c>: don't start Lunalyr afterwards.</item>
/// <item><c>--log &lt;file&gt;</c>: where to log (default <c>%TEMP%\Lunalyr Setup.log</c>).</item>
/// </list>
/// For checking the installer without touching this user's real install: <c>--root &lt;folder&gt;</c> (install
/// there), <c>--registry-base &lt;key&gt;</c> (write under <c>HKCU\Software\Lunalyr\…</c> instead of <c>HKCU</c>) and
/// <c>--start-menu &lt;folder&gt;</c> (put the shortcut there). Internal: <c>--wait-pid &lt;pid&gt;</c> (an uninstaller
/// copy waits for the one that started it).
/// </summary>
public sealed record SetupOptions
{
    public SetupMode Mode { get; init; } = SetupMode.Install;
    public bool Update { get; init; }
    public bool Silent { get; init; }
    public bool NoLaunch { get; init; }
    public string? Root { get; init; }
    public string? RegistryBase { get; init; }
    public string? StartMenu { get; init; }
    public string? LogPath { get; init; }
    public int? WaitPid { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>Whether this run changes this user's real install (not a checking run).</summary>
    public bool IsReal => Root is null && RegistryBase is null && StartMenu is null;

    public static SetupOptions Parse(IReadOnlyList<string> args)
    {
        var options = new SetupOptions();
        var problems = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            string? Value(string flag)
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[++i];
                problems.Add($"{flag} needs a value.");
                return null;
            }

            switch (args[i])
            {
                case "--uninstall": options = options with { Mode = SetupMode.Uninstall }; break;
                case "--update": options = options with { Update = true }; break;
                case "--silent": options = options with { Silent = true }; break;
                case "--no-launch": options = options with { NoLaunch = true }; break;
                case "--root": options = options with { Root = Value("--root") }; break;
                case "--start-menu": options = options with { StartMenu = Value("--start-menu") }; break;
                case "--log": options = options with { LogPath = Value("--log") }; break;
                case "--registry-base":
                    var key = Value("--registry-base");
                    if (key is not null && !key.StartsWith(@"Software\Lunalyr\", StringComparison.OrdinalIgnoreCase))
                        problems.Add(@"--registry-base must be a key under HKCU\Software\Lunalyr\.");
                    else options = options with { RegistryBase = key };
                    break;
                case "--wait-pid":
                    if (int.TryParse(Value("--wait-pid"), out var pid) && pid > 0) options = options with { WaitPid = pid };
                    else problems.Add("--wait-pid needs a process id.");
                    break;
                default:
                    problems.Add($"Unknown option {args[i]}.");
                    break;
            }
        }
        if (options.Update && options.Mode == SetupMode.Uninstall) problems.Add("--update and --uninstall don't go together.");
        return options with { Problems = problems };
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Lunalyr.Install;

namespace Lunalyr.Setup;

/// <summary>
/// <c>Lunalyr Setup.exe</c>. Exit codes: 0 done, 1 failed, 2 bad command line, 3 cancelled. See
/// <see cref="SetupOptions"/> for the command line and <c>docs/PACKAGING.md</c> for the whole story.
/// </summary>
internal static partial class Program
{
    private const int Done = 0, Failed = 1, BadArguments = 2, Cancelled = 3;
    private const string Title = "Lunalyr Setup";

    private static SetupOptions _options = new();
    private static StreamWriter? _log;

    [STAThread]
    public static int Main(string[] args)
    {
        _options = SetupOptions.Parse(args);
        OpenLog();
        Log($"Lunalyr Setup {string.Join(' ', args)}");
        try
        {
            if (_options.Problems.Count > 0)
            {
                Say(DialogIcon.Error, "Setup didn’t understand how it was started", string.Join("\n", _options.Problems));
                return BadArguments;
            }
            if (_options.WaitPid is { } pid) WaitFor(pid);
            return _options.Mode == SetupMode.Uninstall ? Uninstall() : Install();
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            Say(DialogIcon.Error, "Setup stopped unexpectedly", ex.Message);
            return Failed;
        }
        finally
        {
            _log?.Dispose();
        }
    }

    private static InstallTarget Target() => new(
        _options.Root is { } root ? new InstallLayout(root) : InstallLayout.Default(),
        DataRoot(),
        _options.StartMenu ?? WindowsShortcuts.StartMenuFolder());

    /// <summary>Lunalyr's data folder (settings, caches, logs): the install never writes or removes it.</summary>
    private static string DataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product.Name);

    private static InstallServices Services() => new()
    {
        Registry = new WindowsRegistryHive(_options.RegistryBase ?? ""),
        Copies = new WindowsRunningCopies(),
        Shortcuts = new WindowsShortcuts(),
        Log = Log,
        // Explorer only needs telling when the real lunalyr:// registration changed.
        AssociationsChanged = _options.RegistryBase is null ? () => SHChangeNotify(0x08000000, 0, 0, 0) : () => { },
    };

    // ---- Install ----

    private static int Install()
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Setup can't find its own file.");
        using var stream = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read);
        var payload = Payload.Locate(stream);
        if (payload is null)
        {
            // The copy inside an install (no payload): it removes Lunalyr.
            var (button, _) = Ask(new DialogSpec
            {
                Title = Title,
                Instruction = "This is Lunalyr’s uninstaller",
                Content = "To install or update Lunalyr, run the installer you downloaded. To remove Lunalyr, choose Uninstall.",
                Buttons = [new(100, "Uninstall Lunalyr…")],
                CommonButtons = TaskDialog.ButtonClose,
                DefaultButton = TaskDialog.IdClose,
            });
            return button == 100 ? Uninstall() : Cancelled;
        }

        string version;
        using (var zip = Payload.Open(stream, payload)) version = Payload.ReadManifest(zip).Version;
        var target = Target();
        var services = Services();
        var installed = services.Registry.GetString(Product.UninstallKey, "DisplayVersion");
        var others = Installer.OtherCopies(services.Copies, target.Layout);
        var interactive = !_options.Silent && !_options.Update;

        if (interactive)
        {
            var content = installed is null
                ? $"Lunalyr installs for you only, in {target.Layout.Root}. It doesn’t need administrator rights."
                : $"Lunalyr {installed} is installed. Setup replaces it with {version}; your settings and saved lyrics stay.";
            var other = others.FirstOrDefault();
            var (button, quitOther) = Ask(new DialogSpec
            {
                Title = Title,
                Instruction = installed is null ? $"Install Lunalyr {version}?" : $"Update Lunalyr to {version}?",
                Content = content,
                Expanded = other is null ? null
                    : $"Lunalyr is also running from {Path.GetDirectoryName(other.ExePath) ?? "another folder"}. Both use the same settings, so the installed Lunalyr can’t start while it runs. Tick the box to quit it first (as its tray menu’s Quit does).",
                Verification = other is null ? null : "Quit the Lunalyr that’s running from the other folder",
                Footer = "Lunalyr’s licence and the notices for the parts it includes are in THIRD-PARTY-NOTICES next to the app.",
                Buttons = [new(100, installed is null ? "Install" : "Update")],
                CommonButtons = TaskDialog.ButtonCancel,
                DefaultButton = 100,
            });
            if (button != 100) return Cancelled;
            if (quitOther)
            {
                foreach (var copy in others)
                {
                    Log($"Asking the Lunalyr in {copy.ExePath} to quit, as asked.");
                    services.Copies.Quit(copy, services.QuitTimeout);
                }
            }
        }

        var result = RunWithProgress($"Installing Lunalyr {version}", (progress, cancel) =>
            Installer.Install(stream, payload, target, services, progress, cancel));

        if (!result.Succeeded)
        {
            Say(DialogIcon.Error, _options.Update ? "Lunalyr couldn’t update" : "Lunalyr couldn’t be installed", result.Error!);
            // An update that failed leaves the old version: start it again, as the user had it.
            if (_options.Update && !_options.NoLaunch && File.Exists(target.Layout.AppExe)) Launch(target.Layout.AppExe, null);
            return result.Error!.Contains("cancelled", StringComparison.Ordinal) ? Cancelled : Failed;
        }

        if (_options.NoLaunch) return Done;
        if (_options.Update)
        {
            Launch(target.Layout.AppExe, $"--updated {version}");
            return Done;
        }
        if (_options.Silent) return Done;
        var stillRunning = Installer.OtherCopies(services.Copies, target.Layout).Count > 0;
        var (open, _) = Ask(new DialogSpec
        {
            Title = Title,
            Instruction = $"Lunalyr {version} is installed",
            Content = stillRunning
                ? "It’s in the Start menu. Lunalyr from the other folder is still running; quit it from its tray icon, then open the installed one."
                : "It’s in the Start menu, and lives in the notification area by the clock once it’s open.",
            Buttons = stillRunning ? [] : [new(100, "Open Lunalyr")],
            CommonButtons = TaskDialog.ButtonClose,
            DefaultButton = stillRunning ? TaskDialog.IdClose : 100,
        });
        if (open == 100) Launch(target.Layout.AppExe, null);
        return Done;
    }

    // ---- Uninstall ----

    private static int Uninstall()
    {
        var self = Environment.ProcessPath!;
        var root = _options.Root ?? (Path.GetFileName(self).Equals(Product.SetupName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(self) : null);
        var target = Target() with { Layout = root is null ? InstallLayout.Default() : new InstallLayout(root) };

        // Running from inside the install, Setup can't remove itself: continue from a copy in Temp.
        if (target.Layout.Contains(self) && _options.WaitPid is null)
        {
            var copy = Path.Combine(Path.GetTempPath(), $"Lunalyr-Uninstall-{Guid.NewGuid():N}.exe");
            File.Copy(self, copy);
            var args = new List<string> { "--uninstall", "--root", target.Layout.Root, "--wait-pid", Environment.ProcessId.ToString() };
            if (_options.Silent) args.Add("--silent");
            if (_options.RegistryBase is { } rb) args.AddRange(["--registry-base", rb]);
            if (_options.StartMenu is { } sm) args.AddRange(["--start-menu", sm]);
            if (_options.LogPath is { } lp) args.AddRange(["--log", lp]);
            var start = new ProcessStartInfo(copy) { UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            Process.Start(start)?.Dispose();
            return Done;
        }

        if (!_options.Silent)
        {
            var (button, _) = Ask(new DialogSpec
            {
                Title = Title,
                Instruction = "Remove Lunalyr?",
                Content = $"Lunalyr will be removed from {target.Layout.Root}, with its Start menu shortcut, lunalyr:// links and launch at login.",
                Expanded = $"Your settings, saved lyrics and logs stay in {target.DataRoot}. Delete that folder too if you want them gone.",
                Buttons = [new(100, "Remove")],
                CommonButtons = TaskDialog.ButtonCancel,
                DefaultButton = TaskDialog.IdCancel,
            });
            if (button != 100) return Cancelled;
        }

        var result = Uninstaller.Uninstall(target, Services(), runningSetup: self);
        ScheduleSelfDelete(self, target.Layout);
        if (!result.Succeeded)
        {
            Say(DialogIcon.Error, "Lunalyr couldn’t be removed", result.Error!);
            return Failed;
        }
        if (!_options.Silent)
        {
            Say(DialogIcon.Information, "Lunalyr was removed",
                result.Leftovers.Count == 0
                    ? $"Your settings and saved lyrics are still in {target.DataRoot}."
                    : $"A few files were in use and stay until you delete them: {string.Join(", ", result.Leftovers)}");
        }
        return Done;
    }

    /// <summary>The uninstaller's copy in Temp removes itself a moment after it exits.</summary>
    private static void ScheduleSelfDelete(string self, InstallLayout layout)
    {
        if (layout.Contains(self) || !Path.GetFileName(self).StartsWith("Lunalyr-Uninstall-", StringComparison.Ordinal)) return;
        // A raw command line: cmd doesn't read the backslash-escaped quotes ArgumentList would write.
        var start = new ProcessStartInfo("cmd.exe", $"/d /c ping -n 3 127.0.0.1 >nul & del /f /q \"{self}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log($"Couldn’t schedule removing {self}: {ex.Message}");
        }
    }

    // ---- Helpers ----

    private static T RunWithProgress<T>(string instruction, Func<IProgress<InstallProgress>, CancellationToken, T> work)
    {
        using var cancel = new CancellationTokenSource();
        var state = new ProgressState();
        var task = Task.Run(() =>
        {
            try
            {
                return work(state, cancel.Token);
            }
            finally
            {
                state.Done = true;
            }
        });
        if (!_options.Silent && !_options.Update)
        {
            TaskDialog.ShowProgress(new DialogSpec { Title = Title, Instruction = instruction, Content = "Starting…" },
                () => (state.Fraction, state.Step, state.Done), cancel.Cancel);
        }
        return task.GetAwaiter().GetResult();
    }

    private sealed class ProgressState : IProgress<InstallProgress>
    {
        public volatile bool Done;
        private InstallProgress _last = new("Starting…", 0);

        public double Fraction => Volatile.Read(ref _last).Fraction;
        public string Step => Volatile.Read(ref _last).Step;

        public void Report(InstallProgress value) => Volatile.Write(ref _last, value);
    }

    private static void Launch(string exe, string? arguments)
    {
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) };
            if (arguments is not null) start.Arguments = arguments;
            Process.Start(start)?.Dispose();
            Log($"Started {exe} {arguments}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log($"Couldn’t start {exe}: {ex.Message}");
        }
    }

    private static void WaitFor(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit(TimeSpan.FromSeconds(60));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private static (int, bool) Ask(DialogSpec spec)
    {
        Log($"Asked: {spec.Instruction}");
        var answer = TaskDialog.Show(spec);
        Log($"Answer: {answer}");
        return answer;
    }

    /// <summary>Tells the user (unless silent, when the log and exit code carry it).</summary>
    private static void Say(DialogIcon icon, string instruction, string content)
    {
        Log($"{instruction}: {content}");
        if (_options.Silent) return;
        TaskDialog.Show(new DialogSpec { Title = Title, Instruction = instruction, Content = content, Icon = icon, CommonButtons = TaskDialog.ButtonClose });
    }

    private static void OpenLog()
    {
        var path = _options.LogPath ?? Path.Combine(Path.GetTempPath(), "Lunalyr Setup.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _log = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log = null;
        }
    }

    private static void Log(string line) => _log?.WriteLine($"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz}\t{line}");

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}

using Lunalyr.App.Hosting;
using Lunalyr.App.Interop;
using Lunalyr.Core.Playback;
using Lunalyr.Platform;
using Lunalyr.Shell;

namespace Lunalyr.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = ShellOptions.Parse(args);
        // First thing: with --no-playback nothing in this process may ever send a playback command or make a sound.
        if (options.NoPlayback) PlaybackGuard.Process.Block(options.Demo ? "--demo" : "--no-playback");
        Win32.SetProcessDpiAwarenessContext(-4); // per-monitor v2 (also in the manifest)

        // The demo keeps its own settings and logs, so trying it never changes the real ones.
        var dataRoot = options.DataRoot
            ?? (options.Demo ? Path.Combine(AppPaths.DefaultDataRoot(), "Demo") : AppPaths.DefaultDataRoot());
        Directory.CreateDirectory(dataRoot);

        using var instance = SingleInstance.Acquire(dataRoot);
        if (!instance.IsFirst)
        {
            // Lunalyr is already running for this data folder: hand it our command line (a second launch opens settings).
            return instance.Forward(args, TimeSpan.FromSeconds(10)) ? 0 : 1;
        }

        var host = new AppHost(options, dataRoot, instance);
        return host.Run();
    }
}

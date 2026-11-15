using Lunalyr.Core.Logging;
using Lunalyr.Platform.Logging;

namespace Lunalyr.Platform.Windows.Tests.Logging;

public class LoggingHostTests
{
    [Fact]
    public async Task WiresTheFileSinkAndFlushesOnDispose()
    {
        using var temp = new TempFolder();
        var logs = AppPaths.LogsFolder(temp.Path);
        var time = new TimerTimeProvider(new DateTimeOffset(2026, 11, 12, 23, 30, 0, TimeSpan.Zero), TimeSpan.FromHours(2));

        await using (var host = LoggingHost.Create(logs, LogLevel.Debug, time))
        {
            host.Logger.Lyrics.Info("found 42 lines");
            host.Logger.Render.Debug("frame budget ok");
        }

        // 23:30 UTC is already the 13th at UTC+2: files follow the local day.
        var text = File.ReadAllText(Path.Combine(logs, "lunalyr-2026-11-13.log"));
        Assert.Contains("\tINFO\tlyrics\tfound 42 lines", text);
        Assert.Contains("\tDEBUG\trender\tframe budget ok", text);
    }

    [Fact]
    public void DefaultPathsPointAtLocalAppData()
    {
        var root = AppPaths.DefaultDataRoot();
        Assert.EndsWith(Path.DirectorySeparatorChar + "Lunalyr", root);
        Assert.Equal(Path.Combine(root, "Logs"), AppPaths.LogsFolder(root));
    }
}

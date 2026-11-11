using Lunalyr.Core.Logging;
using Windows.System.Power;

namespace Lunalyr.Platform.Desktop;

/// <summary>Windows Energy saver (battery saver before 24H2), read through <see cref="PowerManager"/>.</summary>
internal static class EnergySaver
{
    public static bool IsOn()
    {
        try
        {
            return PowerManager.EnergySaverStatus == EnergySaverStatus.On;
        }
        catch (Exception)
        {
            return false; // Not available (Server, odd SKUs): never constrains.
        }
    }

    /// <summary>A monitor that follows <see cref="PowerManager.EnergySaverStatusChanged"/> and calls <paramref name="changed"/> (on a pool thread) on each flip.</summary>
    public static PowerStateMonitor? CreateMonitor(Action changed, CategoryLogger log)
    {
        try
        {
            var monitor = new PowerStateMonitor(IsOn, refresh =>
            {
                EventHandler<object> handler = (_, _) => refresh();
                PowerManager.EnergySaverStatusChanged += handler;
                return new Subscription(handler);
            });
            monitor.Changed += (_, _) => changed();
            return monitor;
        }
        catch (Exception ex)
        {
            log.Warn("Energy saver status unavailable", ex);
            return null;
        }
    }

    private sealed class Subscription(EventHandler<object> handler) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                PowerManager.EnergySaverStatusChanged -= handler;
            }
            catch (Exception)
            {
            }
        }
    }
}

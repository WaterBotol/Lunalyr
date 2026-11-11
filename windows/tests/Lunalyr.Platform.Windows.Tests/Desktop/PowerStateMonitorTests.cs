using Lunalyr.Platform.Desktop;

namespace Lunalyr.Platform.Windows.Tests.Desktop;

public sealed class PowerStateMonitorTests
{
    private sealed class Source
    {
        public bool OnBattery;
    }

    private sealed class Token(Action onDispose) : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            onDispose();
        }
    }

    // Ported from PowerSavingTests.swift: testPowerSourceMonitorReportsOnlyRealFlips
    [Fact]
    public void PowerSourceMonitorReportsOnlyRealFlips()
    {
        var source = new Source();
        using var monitor = new PowerStateMonitor(() => source.OnBattery, _ => null);
        int flips = 0;
        monitor.Changed += (_, _) => flips++;

        Assert.False(monitor.Value);
        monitor.Refresh(); // unchanged: charge-level noise
        source.OnBattery = true;
        monitor.Refresh(); // unplugged
        Assert.True(monitor.Value);
        monitor.Refresh(); // unchanged
        source.OnBattery = false;
        monitor.Refresh(); // plugged back in
        Assert.False(monitor.Value);
        Assert.Equal(2, flips);
    }

    // Ported from PowerSavingTests.swift: testPowerSourceChangesReachTheMonitorThroughItsSubscription
    [Fact]
    public void PowerSourceChangesReachTheMonitorThroughItsSubscription()
    {
        var source = new Source();
        Action? changed = null;
        Token? held = null;
        var monitor = new PowerStateMonitor(() => source.OnBattery, handler =>
        {
            changed = handler;
            held = new Token(() => { });
            return held;
        });
        Assert.NotNull(changed); // the monitor subscribed to power-source changes
        Assert.NotNull(held);
        Assert.False(monitor.Value);

        int flips = 0;
        monitor.Changed += (_, _) => flips++;
        source.OnBattery = true;
        changed!(); // what the system's notification does
        Assert.True(monitor.Value);
        Assert.Equal(1, flips);

        monitor.Dispose();
        Assert.True(held!.Disposed); // releasing the monitor ends its subscription
        source.OnBattery = false;
        changed(); // a late callback is harmless
        Assert.Equal(1, flips);
    }

    [Fact]
    public void AFlipBetweenTheFirstReadAndTheSubscriptionIsNotLost()
    {
        var source = new Source();
        using var monitor = new PowerStateMonitor(() => source.OnBattery, _ =>
        {
            source.OnBattery = true; // unplugged while subscribing
            return null;
        });
        Assert.True(monitor.Value);
    }
}

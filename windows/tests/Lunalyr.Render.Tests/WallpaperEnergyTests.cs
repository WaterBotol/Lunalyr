using Lunalyr.Render.Devices;
using Lunalyr.Render.Pacing;

namespace Lunalyr.Render.Tests;

public sealed class WallpaperEnergyTests
{
    // Ported from WallpaperEnergyTests.swift: testFrameClockRunsWhileAnythingCanChange
    [Fact]
    public void FrameClockRunsWhileAnythingCanChange()
    {
        double now = 1000;
        Assert.Null(FrameClockPolicy.QuietAfter(new(BackgroundAnimates: true, LyricsAdvance: false, [])));
        Assert.Null(FrameClockPolicy.QuietAfter(new(BackgroundAnimates: false, LyricsAdvance: true, [])));
        Assert.False(FrameClockPolicy.IsIdle(null, now));
    }

    // Ported from WallpaperEnergyTests.swift: testFrameClockStopsAfterTheLastFadeAndResumesWithNewInput
    [Fact]
    public void FrameClockStopsAfterTheLastFadeAndResumesWithNewInput()
    {
        double now = 1000;
        double notice = now + 6, fade = now + 0.75;
        // "Nothing playing" fading on a still backdrop: wait for the notice.
        var quiet = FrameClockPolicy.QuietAfter(new(false, false, [fade, notice]));
        Assert.Equal(notice, quiet);
        Assert.False(FrameClockPolicy.IsIdle(quiet, now + 5.9));
        Assert.True(FrameClockPolicy.IsIdle(quiet, now + 6));
        // Nothing pending at all: idle at once.
        var settled = FrameClockPolicy.QuietAfter(new(false, false, []));
        Assert.True(FrameClockPolicy.IsIdle(settled, now));
        // A song starting is new input and the clock runs again immediately.
        Assert.Null(FrameClockPolicy.QuietAfter(new(false, true, [])));
    }

    // Ported in spirit from WallpaperEnergyTests.swift: testEffectsDrawOnTheGPUDrivingTheirDisplay. The Mac asks
    // Metal for the display's device; Windows matches the monitor against each DXGI adapter's outputs.
    [Fact]
    public void EachMonitorRendersOnTheGpuDrivingIt()
    {
        nint laptopPanel = 0x1001, external = 0x2002;
        var intel = new AdapterOutputs(11, "Intel Graphics", false, [laptopPanel]);
        var nvidia = new AdapterOutputs(22, "RTX 5060", false, [external]);
        var warp = new AdapterOutputs(33, "Microsoft Basic Render Driver", true, []);
        // DXGI lists the adapter driving the primary display first; that is the fallback, never a match's rival.
        AdapterOutputs[] adapters = [nvidia, intel, warp];
        Assert.Same(intel, AdapterSelection.For(laptopPanel, adapters));
        Assert.Same(nvidia, AdapterSelection.For(external, adapters));
        Assert.True(AdapterSelection.Drives(intel, laptopPanel));
    }

    [Fact]
    public void AMonitorNoAdapterDrivesFallsBackToTheFirstHardwareAdapter()
    {
        var warp = new AdapterOutputs(33, "Microsoft Basic Render Driver", true, [0x5005]);
        var nvidia = new AdapterOutputs(22, "RTX 5060", false, [0x2002]);
        Assert.Same(nvidia, AdapterSelection.For(0x9999, [warp, nvidia]));
        Assert.False(AdapterSelection.Drives(nvidia, 0x9999));
        // A software adapter is used only when nothing else exists, even if it "drives" the monitor.
        Assert.Same(nvidia, AdapterSelection.For(0x5005, [warp, nvidia]));
        Assert.Same(warp, AdapterSelection.For(0x5005, [warp]));
        Assert.Null(AdapterSelection.For(0x5005, []));
    }

    [Fact]
    public void SurfacesOnTheSameGpuShareItsDevice()
    {
        // In clone or extend mode two monitors on one GPU resolve to the same adapter (and so one device).
        var nvidia = new AdapterOutputs(22, "RTX 5060", false, [0x1001, 0x2002]);
        Assert.Same(AdapterSelection.For(0x1001, [nvidia]), AdapterSelection.For(0x2002, [nvidia]));
    }
}

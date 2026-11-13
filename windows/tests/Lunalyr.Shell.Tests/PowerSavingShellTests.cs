using Lunalyr.Core.Backgrounds;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Render.Pacing;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Shell.Wallpaper;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// Power saving in the app: Low power visuals reaches the renderer, and Low power visuals, Energy saver and the battery
/// draw what they allow instead of the chosen type (the Mac's effectiveBackgroundMode and its resolvedShader).
/// </summary>
public sealed class PowerSavingShellTests : IAsyncDisposable
{
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeRenderer _renderer = new();
    private AppController? _controller;

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
    }

    private AppController Start(AppSettings settings)
    {
        _controller = new AppController(_source, new FakeLyricLookup(), _renderer, settings, new SongOffsets(), ReadingServices.Basic,
            new PlaybackGuard(), Logger.Null, options: new AppControllerOptions { TimeProvider = new ManualTimeProvider(), ArtworkRetries = [] });
        _controller.Start();
        return _controller;
    }

    private static readonly string[] Modes = [.. AppSettings.BackgroundModes];

    // Ported from PowerSavingTests.swift: testEffectiveModeKeepsTheDesktopAndAppliesEachBehaviour
    [Fact]
    public void EffectiveModeKeepsTheDesktopAndAppliesEachBehaviour()
    {
        foreach (var chosen in Modes)
        {
            Assert.Equal(chosen, WallpaperPower.EffectiveMode(chosen, null));
            Assert.Equal(chosen, WallpaperPower.EffectiveMode(chosen, PowerConstraint.OnBattery(PowerFallback.LightEffects)));
            var patterns = WallpaperPower.EffectiveMode(chosen, PowerConstraint.EnergySaver);
            var colour = WallpaperPower.EffectiveMode(chosen, PowerConstraint.OnBattery(PowerFallback.ColourOnly));
            switch (chosen)
            {
                case "desktop":
                    Assert.Equal("desktop", patterns);
                    Assert.Equal("desktop", colour);
                    break;
                case "colourOnly":
                    Assert.Equal("colourOnly", patterns);
                    Assert.Equal("colourOnly", colour);
                    break;
                default:
                    Assert.Equal("pattern", patterns);
                    Assert.Equal("colourOnly", colour);
                    break;
            }
        }
    }

    // Ported from PowerSavingTests.swift: testPowerSavingNeverRaisesASlowerFrameRate (the Settings half; the pure half is
    // Lunalyr.Render.Tests' PowerSavingTests)
    [Fact]
    public void PowerSavingNeverRaisesASlowerFrameRate()
    {
        var settings = new AppSettings { LowPowerVisuals = true, BackgroundMode = "visualizer", AnimationRate = 15 };
        PowerConstraint? Constraint(AppSettings s) => PowerPolicy.Constraint(s.Performance(), energySaverOn: false, isOnBattery: false);
        Assert.Equal(FrameRateCap.Fps15, PowerPolicy.EffectiveRate(settings.Performance().FrameRate, Constraint(settings) is not null));
        settings = settings with { AnimationRate = 60 };
        Assert.Equal(FrameRateCap.Fps30, PowerPolicy.EffectiveRate(settings.Performance().FrameRate, Constraint(settings) is not null));
        Assert.Equal("pattern", WallpaperPower.EffectiveMode(settings.BackgroundMode, Constraint(settings)));
        settings = settings with { LowPowerVisuals = false };
        Assert.Equal(FrameRateCap.Fps60, PowerPolicy.EffectiveRate(settings.Performance().FrameRate, Constraint(settings) is not null));
        Assert.Equal("visualizer", WallpaperPower.EffectiveMode(settings.BackgroundMode, Constraint(settings)));
    }

    // Ported from MenuBarTests.swift: testPowerSavingIsRaisedOnlyWhenItReplacesTheChosenWallpaper (what is drawn; Windows
    // has no menu-bar banner)
    [Fact]
    public void PowerSavingReplacesOnlyWhatItMust()
    {
        bool Replaces(string chosen, PowerConstraint? power) => WallpaperPower.EffectiveMode(chosen, power) != chosen;
        Assert.True(Replaces("shader", PowerConstraint.LowPowerVisuals)); // Line patterns instead of GPU effects
        Assert.True(Replaces("visualizer", PowerConstraint.EnergySaver));
        Assert.False(Replaces("shader", null));
        Assert.False(Replaces("pattern", PowerConstraint.LowPowerVisuals)); // already the lighter look
        Assert.False(Replaces("desktop", PowerConstraint.LowPowerVisuals)); // Your wallpaper is never replaced
        Assert.False(Replaces("auto", PowerConstraint.OnBattery(PowerFallback.LightEffects))); // lighter effects keep the chosen wallpaper
    }

    [Fact]
    public void LowPowerVisualsReachesTheRenderer()
    {
        Assert.False(new AppSettings().Performance().LowPowerVisuals);
        var on = new AppSettings { LowPowerVisuals = true }.Performance();
        Assert.True(on.LowPowerVisuals);
        Assert.Equal(PowerConstraint.LowPowerVisuals, PowerPolicy.Constraint(on, energySaverOn: false, isOnBattery: false));
        // The rest of the Performance page as before.
        var rest = new AppSettings { AnimationRate = 0, PauseWhenCovered = false, PauseOnBatterySaver = false, WallpaperPaused = true }.Performance();
        Assert.Equal((FrameRateCap.Display, false, false, true), (rest.FrameRate, rest.PauseWhenCovered, rest.FollowEnergySaver, rest.PauseWallpaper));
        Assert.Equal(FrameRateCap.Fps30, new AppSettings { AnimationRate = 30 }.Performance().FrameRate);
    }

    [Fact]
    public void ThePerformancePageSaysWhatPowerSavingIsDoing()
    {
        Assert.Null(PowerCopy.InEffect(new AppSettings(), null));
        // Low power visuals' own switch already says so; only the held frame rate is news.
        Assert.Equal("Power saving is holding animation at 30 fps for now.", PowerCopy.InEffect(new AppSettings(), PowerConstraint.LowPowerVisuals));
        Assert.Null(PowerCopy.InEffect(new AppSettings { AnimationRate = 30 }, PowerConstraint.LowPowerVisuals));
        Assert.Equal("Energy saver is on now, so power saving is in effect.",
            PowerCopy.InEffect(new AppSettings { AnimationRate = 15 }, PowerConstraint.EnergySaver));
        Assert.Contains("This PC is on battery power now", PowerCopy.InEffect(new AppSettings(), PowerConstraint.OnBattery(PowerFallback.LightEffects)));
    }

    [Fact]
    public void LowPowerVisualsDrawsLinePatternsInsteadOfEffectsAndVisualisers()
    {
        var controller = Start(new AppSettings { BackgroundMode = "shader", ShaderStyle = "aurora" });
        Assert.Equal("shader.aurora", _renderer.Layers.Background);
        controller.SetPowerConstraint(PowerConstraint.LowPowerVisuals);
        Assert.StartsWith("pattern.", _renderer.Layers.Background, StringComparison.Ordinal);
        Assert.Equal("pattern", controller.EffectiveBackgroundMode);
        Assert.Equal("shader", controller.Settings.BackgroundMode); // the choice is kept

        controller.ApplySettings(controller.Settings with { BackgroundMode = "visualizer" });
        Assert.StartsWith("pattern.", _renderer.Layers.Background, StringComparison.Ordinal);
        controller.ApplySettings(controller.Settings with { BackgroundMode = "colourOnly" });
        Assert.Equal("colour", _renderer.Layers.Background);
        controller.ApplySettings(controller.Settings with { BackgroundMode = "desktop" });
        Assert.Equal("desktop", _renderer.Layers.Background);

        // Your full wallpaper returns when power saving ends.
        controller.ApplySettings(controller.Settings with { BackgroundMode = "visualizer" });
        controller.SetPowerConstraint(null);
        Assert.Equal("visualizer", _renderer.Layers.Background);
        controller.SetPowerConstraint(PowerConstraint.OnBattery(PowerFallback.ColourOnly));
        Assert.Equal("colour", _renderer.Layers.Background);
    }

    [Fact]
    public void InexpensiveEffectsSwapAPinnedHeavyEffectForALightOne()
    {
        var heavy = GpuEffectCatalog.All.First(e => e.Heavy).Id;
        var controller = Start(new AppSettings { BackgroundMode = "shader", ShaderStyle = heavy });
        Assert.Equal($"shader.{heavy}", _renderer.Layers.Background);
        controller.SetPowerConstraint(PowerConstraint.OnBattery(PowerFallback.LightEffects));
        var drawn = _renderer.Layers.Background;
        Assert.StartsWith("shader.", drawn, StringComparison.Ordinal);
        Assert.False(GpuEffectCatalog.All.First(e => "shader." + e.Id == drawn).Heavy, drawn);
        controller.SetPowerConstraint(null);
        Assert.Equal($"shader.{heavy}", _renderer.Layers.Background);
    }
}

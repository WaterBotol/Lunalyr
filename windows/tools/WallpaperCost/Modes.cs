using Lunalyr.Shell.Settings;

namespace Lunalyr.Tools.WallpaperCost;

/// <summary>Where a run's background layer comes from.</summary>
internal enum LayerSource
{
    /// <summary>The app's controller picks it from the settings, exactly as on the desktop.</summary>
    Controller,

    /// <summary>The imported picture ("custom"), made on the tool's test picture.</summary>
    Picture,

    /// <summary>The imported video ("custom"), made on the tool's test clip.</summary>
    Video,

    /// <summary>The animated cover ("motion"), made on the tool's test clip.</summary>
    Motion,

    /// <summary>The music video's file layer ("musicVideo"), fed the tool's test clip at the song's time.</summary>
    MusicVideo,
}

/// <summary>
/// One wallpaper mode as the Wallpaper page sets it. <paramref name="IdleAs"/>: with nothing playing the app draws
/// another mode's background (the animated cover and the music video belong to a song), so that idle run is not
/// repeated.
/// </summary>
internal sealed record Mode(string Id, string Title, Func<AppSettings, AppSettings> Configure, LayerSource Source = LayerSource.Controller,
    string? IdleAs = null, string? IdleWhy = null)
{
    /// <summary>The modes measured by default, in the Wallpaper page's order.</summary>
    public static IReadOnlyList<Mode> All { get; } =
    [
        new("auto", "Match the music (default)", s => s with { BackgroundMode = "auto" }),
        new("gradient", "Colour only, Album gradient off (album colour field)",
            s => s with { BackgroundMode = "colourOnly", AlbumGradientWallpaper = false }),
        new("colour", "Colour only (album gradient)", s => s with { BackgroundMode = "colourOnly", AlbumGradientWallpaper = true }),
        new("pattern-painter", "Line pattern: Orbits (Direct2D painter)", s => s with { BackgroundMode = "pattern", PatternStyle = "orbits" }),
        new("pattern-shader", "Line pattern: Low Poly (pixel shader)", s => s with { BackgroundMode = "pattern", PatternStyle = "lowPoly" }),
        new("effect-light", "GPU effect: Plasma (light)", s => s with { BackgroundMode = "shader", ShaderStyle = "plasma" }),
        new("effect-heavy", "GPU effect: Nebula (heavy)", s => s with { BackgroundMode = "shader", ShaderStyle = "nebula" }),
        new("picture", "Imported picture", s => s with { BackgroundMode = "custom" }, LayerSource.Picture),
        new("video", "Imported video", s => s with { BackgroundMode = "custom" }, LayerSource.Video),
        new("motion", "Animated cover (Match the music)", s => s with { BackgroundMode = "auto", UseMotionArtwork = true }, LayerSource.Motion,
            IdleAs: "auto", IdleWhy: "with nothing playing there is no album, so the app drops the cover and draws Match the music's background"),
        new("visualizer", "Visualiser: Silk (demo signal)", s => s with { BackgroundMode = "visualizer", VisualizerStyle = "silk" }),
        new("musicVideo", "Music video (local file, muted)", s => s with { BackgroundMode = "musicVideo" }, LayerSource.MusicVideo,
            IdleAs: "auto", IdleWhy: "with nothing playing there is no video, so the app draws the \"Without a video\" type (Match the music)"),
        new("desktop", "Your wallpaper (see-through)", s => s with { BackgroundMode = "desktop" }),
    ];

    /// <summary>A mode by id; <c>shader.&lt;id&gt;</c> and <c>pattern.&lt;id&gt;</c> pin any effect or pattern.</summary>
    public static Mode? Find(string id)
    {
        foreach (var mode in All)
            if (string.Equals(mode.Id, id, StringComparison.OrdinalIgnoreCase)) return mode;
        if (id.StartsWith("shader.", StringComparison.Ordinal))
            return new(id, $"GPU effect: {id[7..]}", s => s with { BackgroundMode = "shader", ShaderStyle = id[7..] });
        if (id.StartsWith("pattern.", StringComparison.Ordinal))
            return new(id, $"Line pattern: {id[8..]}", s => s with { BackgroundMode = "pattern", PatternStyle = id[8..] });
        return null;
    }

    /// <summary>The baseline: the controller and the tool, no device and no layer.</summary>
    public static Mode Baseline { get; } = new("baseline", "Baseline: controller only, no device, nothing drawn", s => s);
}

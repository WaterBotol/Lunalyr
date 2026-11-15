// The visualiser scenes, from Lunalyr for Mac's AudioVisualizerStyle (AudioVisualizerView.swift; Lunalyr's own lines).
// Windows draws the native scenes; the Mac's three web scenes (Wavefield Original, Wavecore, Light 3D) are not part of
// this port and read as the native scene the Mac itself stands in with on other displays.

namespace Lunalyr.Core.Visualisers;

/// <summary>A native visualiser scene. The raw values are the Mac's <c>custom.visualizerStyle</c> values.</summary>
public enum VisualizerStyle
{
    Silk,
    Aurora,
    Tunnel,
    CurtainAurora,
    FosforaAurora,
    Wavefield,
    WavefieldSphere,
    StarTrails,
    ColourStrata,
    Overprint,
}

/// <summary>Names, captions and shader choice per scene.</summary>
public static class VisualizerStyles
{
    /// <summary>The Mac's default scene.</summary>
    public const VisualizerStyle Default = VisualizerStyle.Silk;

    /// <summary>Every scene in the Mac's picker order.</summary>
    public static IReadOnlyList<VisualizerStyle> All { get; } =
    [
        VisualizerStyle.Silk, VisualizerStyle.Aurora, VisualizerStyle.Tunnel, VisualizerStyle.CurtainAurora,
        VisualizerStyle.FosforaAurora, VisualizerStyle.Wavefield, VisualizerStyle.WavefieldSphere, VisualizerStyle.StarTrails,
        VisualizerStyle.ColourStrata, VisualizerStyle.Overprint,
    ];

    public static bool IsWavefield(this VisualizerStyle style) =>
        style is VisualizerStyle.Wavefield or VisualizerStyle.WavefieldSphere;

    /// <summary>The setting's raw value (the Mac's).</summary>
    public static string RawValue(this VisualizerStyle style) => style switch
    {
        VisualizerStyle.Silk => "silk",
        VisualizerStyle.Aurora => "aurora",
        VisualizerStyle.Tunnel => "tunnel",
        VisualizerStyle.CurtainAurora => "curtainAurora",
        VisualizerStyle.FosforaAurora => "fosforaAurora",
        VisualizerStyle.Wavefield => "wavefield",
        VisualizerStyle.WavefieldSphere => "wavefieldSphere",
        VisualizerStyle.StarTrails => "starTrails",
        VisualizerStyle.ColourStrata => "colourStrata",
        _ => "overprint",
    };

    /// <summary>
    /// The scene for a stored value. The Mac's web scenes read as the native scene the Mac stands in with (Wavefield
    /// Original as Wavefield, Wavecore as Fosfora Aurora, Light 3D as Star Trails); anything unknown is the default.
    /// </summary>
    public static VisualizerStyle Parse(string? raw)
    {
        foreach (var style in All)
            if (string.Equals(style.RawValue(), raw, StringComparison.Ordinal)) return style;
        return raw switch
        {
            "wavefieldOriginal" => VisualizerStyle.Wavefield,
            "wavecore" => VisualizerStyle.FosforaAurora,
            "light3D" => VisualizerStyle.StarTrails,
            _ => Default,
        };
    }

    public static string Title(this VisualizerStyle style) => style switch
    {
        VisualizerStyle.Silk => "Silk Ribbons",
        VisualizerStyle.Aurora => "Spectral Aurora",
        VisualizerStyle.Tunnel => "Spectrum Tunnel",
        VisualizerStyle.CurtainAurora => "Aurora Curtains",
        VisualizerStyle.FosforaAurora => "Fosfora Aurora",
        VisualizerStyle.Wavefield => "Wavefield",
        VisualizerStyle.WavefieldSphere => "Wavefield Sphere",
        VisualizerStyle.StarTrails => "Star Trails",
        VisualizerStyle.ColourStrata => "Colour Strata",
        _ => "Overprint",
    };

    public static string Detail(this VisualizerStyle style) => style switch
    {
        VisualizerStyle.Silk => "Quiet folds of coloured light, shaped by the music.",
        VisualizerStyle.Aurora => "A flowing sky with a ribbon for each musical register.",
        VisualizerStyle.Tunnel => "An immersive passage carved from the live spectrum.",
        VisualizerStyle.CurtainAurora => "Bass on the left, treble on the right. Each curtain’s sections bend, widen and glow with their own frequencies.",
        VisualizerStyle.FosforaAurora => "Seven luminous frequency ribbons, adapted from Fosfora’s open-source Aurora.",
        VisualizerStyle.Wavefield => "Live spectral peaks redraw Wavefield’s heatmap interference patterns.",
        VisualizerStyle.WavefieldSphere => "Wavefield’s interference patterns wrap around a three-dimensional sphere.",
        VisualizerStyle.StarTrails => "Two craft weave contrasting neon trails through a nebula. Album colours can tint the sky and pick a vivid pair for the trails. Kicks, snares, hi-hats, bass, voice and pads each light their own stars.",
        VisualizerStyle.ColourStrata => "Soft album-coloured layers. Bass widens the lower field, mids bend the middle, and highs soften the upper edge.",
        _ => "Overlapping matte ink in album colours. Bass swells a plate, mids shift the overlap, and highs bring out fine dots.",
    };

    /// <summary>The compiled pixel shader a scene draws with (Silk Ribbons and Spectral Aurora share one, as on the Mac).</summary>
    public static string Shader(this VisualizerStyle style) => style switch
    {
        VisualizerStyle.Tunnel => "Tunnel",
        VisualizerStyle.StarTrails => "StarTrails",
        VisualizerStyle.ColourStrata => "ColourStrata",
        VisualizerStyle.Overprint => "Overprint",
        VisualizerStyle.CurtainAurora => "CurtainAurora",
        VisualizerStyle.FosforaAurora => "FosforaAurora",
        VisualizerStyle.Wavefield or VisualizerStyle.WavefieldSphere => "Wavefield",
        _ => "Aurora",
    };
}

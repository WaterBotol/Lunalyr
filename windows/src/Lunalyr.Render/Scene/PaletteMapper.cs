using System.Numerics;
using Lunalyr.Core.Colour;

namespace Lunalyr.Render.Scene;

/// <summary>
/// Turns a cover's colours (Core's <see cref="AlbumWallpaperPalette"/>) into the scene's <see cref="Palette"/>. Colours
/// are gamma-encoded sRGB, 0...1, alpha 1, as Direct2D takes them for an 8-bit surface.
/// </summary>
public static class PaletteMapper
{
    /// <summary>
    /// The scene palette for a cover:
    /// <list type="bullet">
    /// <item><see cref="Palette.Base"/>: the five stops' average at 45% brightness, the colour field's base.</item>
    /// <item><see cref="Palette.Accents"/>: the five sampled stops in the cover's own order (top-left, top-right,
    /// centre, bottom-left, bottom-right), which the backgrounds lay out as the cover has them.</item>
    /// <item><see cref="Palette.Text"/>: the lyric colour Album drift settles on at the middle of a line, made readable
    /// over the colour field (the Mac's default text mode).</item>
    /// <item><see cref="Palette.Highlight"/>: the highlight tile that complements it (the Mac's default highlight).</item>
    /// </list>
    /// Text and highlight drift and ease per frame on the Mac; the compositions run their own
    /// <see cref="LyricInkDirector"/> from <see cref="Palette.ArtworkStops"/> and <see cref="Palette.TextBackdrop"/>,
    /// which are filled in here. Takes a few milliseconds: call it once per cover, off the render thread.
    /// </summary>
    public static Palette FromAlbum(AlbumWallpaperPalette cover, BackgroundOptionsForText? text = null)
    {
        ArgumentNullException.ThrowIfNull(cover);
        var options = text ?? BackgroundOptionsForText.Default;
        var average = cover.Average;
        var accents = new Vector4[cover.Stops.Count];
        for (var i = 0; i < accents.Length; i++) accents[i] = Opaque(cover.Stops[i]);
        var backdrop = LyricBackdrop.Luminance(options.Mode, false, cover.ArtworkStops, options.ScrimStrength,
                                               pastelText: true, increaseContrast: options.IncreaseContrast);
        var ink = new LyricInkPreview().Ink(new LyricInkInputs(LyricColourMode.AlbumDrift)
        {
            Album = cover.ArtworkStops, Backdrop = backdrop, IncreaseContrast = options.IncreaseContrast,
        }, 0);
        return new Palette(Opaque(average * ColourField.BaseBrightness), accents, Opaque(ink.Sample(0.5).Rest), Opaque(ink.TileColour))
        {
            ArtworkStops = [.. cover.ArtworkStops],
            TextBackdrop = backdrop,
            // Before the automatic backing: what decides light or dark lyrics.
            TextUnbacked = LyricBackdrop.BeforeBacking(options.Mode, false, cover.ArtworkStops, options.ScrimStrength),
        };
    }

    /// <summary>The palette before artwork arrives, or for a track without any (the Mac's fallback colours).</summary>
    public static Palette Fallback { get; } = FromAlbum(AlbumWallpaperPalette.Fallback);

    private static Vector4 Opaque(Vector3 rgb) => new(rgb, 1);

    private static Vector4 Opaque(SrgbColour colour) => new((float)colour.R, (float)colour.G, (float)colour.B, 1);
}

/// <summary>What the text colour is judged against when a scene palette is made.</summary>
public sealed record BackgroundOptionsForText
{
    public static BackgroundOptionsForText Default { get; } = new();

    /// <summary>The background the lyrics sit on (the colour field by default).</summary>
    public WallpaperBackgroundMode Mode { get; init; } = WallpaperBackgroundMode.ColourOnly;

    public double ScrimStrength { get; init; } = 1;

    public bool IncreaseContrast { get; init; }
}

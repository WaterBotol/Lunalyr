using Lunalyr.Core.Colour;
using Lunalyr.Core.Player;
using Vortice.Mathematics;

namespace Lunalyr.App.Player;

/// <summary>
/// The card's colours for one album and one accessibility look. Dark mode is the Mac's Sleeve: white type on the album's
/// deep shade with its strongest colour washed across the top. Light mode keeps the album's hue but lightens the wash
/// and sets the type near-black. Transparency off makes the card solid. A contrast theme ignores the album entirely and
/// uses the theme's own colours, as Windows asks.
/// </summary>
internal sealed record CardTheme
{
    /// <summary>The card's base, under everything (alpha below 1 lets the wallpaper's colour through).</summary>
    public Color4 Base { get; init; }

    /// <summary>The wash's two ends, top-left to bottom-right (alpha included).</summary>
    public Color4 WashTop { get; init; }
    public Color4 WashBottom { get; init; }

    /// <summary>The light along the card's top edge (0 for none).</summary>
    public float Sheen { get; init; }

    /// <summary>Type, icons, the bar: drawn in this at the opacities the Mac uses for white.</summary>
    public Color4 Ink { get; init; }

    /// <summary>The colour behind the type: the play disc's glyph, the knob's shadow.</summary>
    public Color4 InkInverse { get; init; }

    /// <summary>The album's colour: the bar while dragged, the record's label, the capsules' rims.</summary>
    public Color4 Accent { get; init; }

    public Color4 Deep { get; init; }

    /// <summary>The capsules' backing (time and lyric over the bar, notes).</summary>
    public Color4 Capsule { get; init; }
    public Color4 CapsuleRim { get; init; }

    /// <summary>The window's 1 px DWM border (COLORREF, 0x00BBGGRR).</summary>
    public uint Border { get; init; }

    /// <summary>A contrast theme: system colours only, firm edges, no washes.</summary>
    public bool HighContrast { get; init; }

    /// <summary>Contrast theme only: the selection colour and the text on it, and greyed-out text.</summary>
    public Color4 Highlight { get; init; }
    public Color4 HighlightText { get; init; }
    public Color4 Disabled { get; init; }

    public static CardTheme For(ArtworkPalette palette, PlayerLook look)
    {
        if (look.HighContrast) return Contrast();
        var accent = Colour(palette.Accent);
        var deep = Colour(palette.Deep);
        if (look.Light)
        {
            // The album's hue, light: its accent mixed far towards white, then darkened only if dark type would not
            // reach AA on it.
            var light = palette.Accent.Mixed(new ArtworkPalette.Rgb(1, 1, 1), palette.FromArtwork ? 0.8 : 0.86);
            light = LightGuarded(light);
            var lower = palette.Accent.Mixed(new ArtworkPalette.Rgb(1, 1, 1), 0.62);
            var ink = new Color4(0.07f, 0.07f, 0.09f, 1);
            return new CardTheme
            {
                Base = WithAlpha(Colour(light), look.ReduceTransparency ? 1 : 0.95f),
                WashTop = WithAlpha(Colour(lower), look.ReduceTransparency ? 0 : 0.35f),
                WashBottom = WithAlpha(Colour(light), 0),
                Sheen = look.ReduceTransparency ? 0 : 0.35f,
                Ink = ink,
                InkInverse = new Color4(1, 1, 1, 1),
                Accent = Colour(ArtworkPalette.Guarded(palette.Accent, ArtworkPalette.AccentContrast)),
                Deep = deep,
                Capsule = WithAlpha(Colour(light.Mixed(new ArtworkPalette.Rgb(1, 1, 1), 0.5)), look.ReduceTransparency ? 1 : 0.97f),
                CapsuleRim = WithAlpha(accent, 0.6f),
                Border = ColorRef(Colour(palette.Accent.Mixed(new ArtworkPalette.Rgb(1, 1, 1), 0.45))),
            };
        }

        if (look.ReduceTransparency)
        {
            // The Mac's solid card under Reduce Transparency: the deep shade, a little greyer.
            var solid = palette.Deep.Mixed(new ArtworkPalette.Rgb(0.06, 0.06, 0.07), 0.35);
            return new CardTheme
            {
                Base = Colour(solid),
                WashTop = WithAlpha(accent, 0),
                WashBottom = WithAlpha(deep, 0),
                Sheen = 0,
                Ink = new Color4(1, 1, 1, 1),
                InkInverse = new Color4(0, 0, 0, 1),
                Accent = accent,
                Deep = deep,
                Capsule = new Color4(0.08f, 0.08f, 0.08f, 1),
                CapsuleRim = new Color4(1, 1, 1, 0.3f),
                Border = ColorRef(Colour(solid.Mixed(new ArtworkPalette.Rgb(1, 1, 1), 0.2))),
            };
        }

        // The Mac's glass without a system material behind it: the deep shade darkened as the base (the wallpaper shows
        // through a little), the album's colour washed from the top left into the deep shade, light along the top.
        var glass = palette.Deep.Mixed(new ArtworkPalette.Rgb(0, 0, 0), 0.24);
        return new CardTheme
        {
            Base = WithAlpha(Colour(glass), 0.93f),
            WashTop = WithAlpha(accent, palette.FromArtwork ? 0.42f : 0.3f),
            WashBottom = WithAlpha(deep, palette.FromArtwork ? 0.8f : 0.85f),
            Sheen = 0.10f,
            Ink = new Color4(1, 1, 1, 1),
            InkInverse = new Color4(0, 0, 0, 1),
            Accent = accent,
            Deep = deep,
            Capsule = WithAlpha(deep, 0.95f),
            CapsuleRim = WithAlpha(accent, 0.55f),
            Border = ColorRef(Colour(palette.Accent.Mixed(palette.Deep, 0.4))),
        };
    }

    private static CardTheme Contrast()
    {
        // COLOR_WINDOW 5, COLOR_WINDOWTEXT 8, COLOR_HIGHLIGHT 13, COLOR_HIGHLIGHTTEXT 14, COLOR_GRAYTEXT 17.
        var window = Sys(5);
        var text = Sys(8);
        return new CardTheme
        {
            Base = window,
            WashTop = WithAlpha(window, 0),
            WashBottom = WithAlpha(window, 0),
            Sheen = 0,
            Ink = text,
            InkInverse = window,
            Accent = Sys(13),
            Deep = window,
            Capsule = window,
            CapsuleRim = text,
            Border = PlayerNative.GetSysColor(8),
            HighContrast = true,
            Highlight = Sys(13),
            HighlightText = Sys(14),
            Disabled = Sys(17),
        };
    }

    private static ArtworkPalette.Rgb LightGuarded(ArtworkPalette.Rgb colour)
    {
        // Near-black type (luminance ~0.006) on it at 7:1 or better: lighten towards white until it is.
        var result = colour;
        for (var i = 0; i < 40 && (result.Luminance + 0.05) / (0.006 + 0.05) < 7; i++)
            result = result.Mixed(new ArtworkPalette.Rgb(1, 1, 1), 0.1);
        return result;
    }

    public static Color4 Colour(ArtworkPalette.Rgb c) =>
        new((float)Math.Clamp(c.Red, 0, 1), (float)Math.Clamp(c.Green, 0, 1), (float)Math.Clamp(c.Blue, 0, 1), 1);

    public static Color4 WithAlpha(Color4 c, float alpha) => new(c.R, c.G, c.B, alpha);

    private static Color4 Sys(int index)
    {
        var c = PlayerNative.GetSysColor(index);
        return new Color4((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, 1);
    }

    private static uint ColorRef(Color4 c) =>
        (uint)(Math.Clamp(c.R, 0, 1) * 255) | ((uint)(Math.Clamp(c.G, 0, 1) * 255) << 8) | ((uint)(Math.Clamp(c.B, 0, 1) * 255) << 16);
}

using Lunalyr.Core.Colour;
using Lunalyr.Core.Player;
using Vortice.Mathematics;

namespace Lunalyr.App.Player;

/// <summary>
/// The card's colours for one album and one accessibility look, from the player's tokens (docs/DESIGN.md, "Desktop
/// player"). Dark mode is the Mac's Sleeve: a black veil and the album's flat tint (<see cref="ArtworkPalette.CardTint"/>)
/// over what shows through, white ink. Light mode (Windows only) keeps the album's hue in a light flat tint over a white
/// veil, with near-black ink. Transparency off makes the card solid. A contrast theme ignores the album entirely and
/// uses the theme's own colours, as Windows asks, with no opacity on them. No gradients, sheens or tinted rims.
/// </summary>
internal sealed record CardTheme
{
    /// <summary>The veil, drawn first over the transparent window (alpha included; opaque when the card is solid).</summary>
    public Color4 Veil { get; init; }

    /// <summary>The album's flat tint over the veil (alpha included; 0 when the veil is the whole card).</summary>
    public Color4 Tint { get; init; }

    /// <summary>Type, icons, the bar: drawn in this at the ink levels' opacities (<see cref="PlayerTokens.Ink"/>).</summary>
    public Color4 Ink { get; init; }

    /// <summary>The colour behind the ink: the play disc's glyph, Done's text.</summary>
    public Color4 InkInverse { get; init; }

    /// <summary>The album's colour, for state only: the bar while dragged, the record's label, the video's span.</summary>
    public Color4 Accent { get; init; }

    public Color4 Deep { get; init; }

    /// <summary>The capsules' backing (time and lyric over the bar, notes), alpha included.</summary>
    public Color4 Capsule { get; init; }

    /// <summary>Edges: only in a contrast theme (transparent otherwise).</summary>
    public Color4 Hairline { get; init; }

    /// <summary>The window's 1 px DWM border (COLORREF, 0x00BBGGRR; <see cref="SystemBorder"/> for the system's own).</summary>
    public uint Border { get; init; }

    /// <summary>A contrast theme: system colours only, firm edges, no tint.</summary>
    public bool HighContrast { get; init; }

    /// <summary>Contrast theme only: the selection colour and the text on it, and greyed-out text.</summary>
    public Color4 Highlight { get; init; }
    public Color4 HighlightText { get; init; }
    public Color4 Disabled { get; init; }

    /// <summary><c>DWMWA_COLOR_DEFAULT</c>: the border every Windows 11 window has, in the system's colour.</summary>
    public const uint SystemBorder = 0xFFFFFFFF;

    public static CardTheme For(ArtworkPalette palette, PlayerLook look)
    {
        if (look.HighContrast) return Contrast();
        var accent = Colour(palette.Accent);
        var deep = Colour(palette.Deep);
        var veil = (float)PlayerTokens.Fill.Veil;
        var tint = (float)PlayerTokens.Fill.Tint;
        var capsule = (float)PlayerTokens.Fill.Capsule;
        var clear = new Color4(0, 0, 0, 0);
        if (look.Light)
        {
            var light = Colour(PlayerCardColours.LightTint(palette));
            var ink = Colour(PlayerCardColours.LightInk);
            return new CardTheme
            {
                Veil = look.ReduceTransparency ? light : new Color4(1, 1, 1, veil),
                Tint = WithAlpha(light, look.ReduceTransparency ? 0 : tint),
                Ink = ink,
                InkInverse = new Color4(1, 1, 1, 1),
                Accent = Colour(ArtworkPalette.Guarded(palette.Accent, ArtworkPalette.AccentContrast)),
                Deep = deep,
                Capsule = WithAlpha(light, look.ReduceTransparency ? 1 : capsule),
                Hairline = clear,
                Border = SystemBorder,
            };
        }

        if (look.ReduceTransparency)
        {
            // The Mac's solid card under Reduce Transparency: the deep shade, a little greyer; capsules near-black.
            var solid = Colour(palette.Deep.Mixed(new ArtworkPalette.Rgb(0.06, 0.06, 0.07), 0.35));
            return new CardTheme
            {
                Veil = solid,
                Tint = WithAlpha(solid, 0),
                Ink = new Color4(1, 1, 1, 1),
                InkInverse = new Color4(0, 0, 0, 1),
                Accent = accent,
                Deep = deep,
                Capsule = new Color4(0.08f, 0.08f, 0.08f, 1),
                Hairline = clear,
                Border = SystemBorder,
            };
        }

        // The Mac's card without a system material behind it: the veil and the flat tint over the wallpaper.
        return new CardTheme
        {
            Veil = new Color4(0, 0, 0, veil),
            Tint = WithAlpha(Colour(palette.CardTint), tint),
            Ink = new Color4(1, 1, 1, 1),
            InkInverse = new Color4(0, 0, 0, 1),
            Accent = accent,
            Deep = deep,
            Capsule = WithAlpha(deep, capsule),
            Hairline = clear,
            Border = SystemBorder,
        };
    }

    private static CardTheme Contrast()
    {
        // COLOR_WINDOW 5, COLOR_WINDOWTEXT 8, COLOR_HIGHLIGHT 13, COLOR_HIGHLIGHTTEXT 14, COLOR_GRAYTEXT 17.
        var window = Sys(5);
        var text = Sys(8);
        return new CardTheme
        {
            Veil = window,
            Tint = WithAlpha(window, 0),
            Ink = text,
            InkInverse = window,
            Accent = Sys(13),
            Deep = window,
            Capsule = window,
            Hairline = text,
            Border = PlayerNative.GetSysColor(8),
            HighContrast = true,
            Highlight = Sys(13),
            HighlightText = Sys(14),
            Disabled = Sys(17),
        };
    }

    public static Color4 Colour(ArtworkPalette.Rgb c) =>
        new((float)Math.Clamp(c.Red, 0, 1), (float)Math.Clamp(c.Green, 0, 1), (float)Math.Clamp(c.Blue, 0, 1), 1);

    public static Color4 WithAlpha(Color4 c, float alpha) => new(c.R, c.G, c.B, alpha);

    private static Color4 Sys(int index)
    {
        var c = PlayerNative.GetSysColor(index);
        return new Color4((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, 1);
    }
}

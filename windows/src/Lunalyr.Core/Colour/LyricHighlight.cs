// Ported from the Mac's Sources/UI/LyricHighlightColour.swift (Lunalyr's own code; git blame puts only an enum's
// boilerplate lines in the squashed Verso import). The preference store (LyricHighlightOptions' UserDefaults) belongs
// to the settings workstream; its pure parts (default colour, hex parsing) are here.

using System.Globalization;

namespace Lunalyr.Core.Colour;

/// <summary>
/// Where highlight tiles take their colour from: Cutout's labels, the tile "Cutout Â· Highlight" presses behind each
/// sung word, and the held words Cascade picks out.
/// </summary>
public enum LyricHighlightMode
{
    /// <summary>Opposite the lyric colour on the colour wheel, or beside its opposite. White lyrics take the album accent.</summary>
    Complement,
    /// <summary>The cover's richest colour, made vivid.</summary>
    Album,
    /// <summary>A colour of the user's own.</summary>
    Custom,
}

/// <summary>One highlight as the stage draws it: the tile, the text set on it, and the same hue as type for held words.</summary>
public readonly record struct LyricHighlightTone(OkLab Tile, OkLab Text, OkLab Held);

/// <summary>Colour preference helpers shared by the text colour and highlight settings.</summary>
public static class ColourHex
{
    /// <summary><c>#RRGGBB</c> (the hash optional, surrounding spaces ignored) as OKLab, or null.</summary>
    public static OkLab? ToOkLab(string? hex)
    {
        if (hex is null) return null;
        var text = hex.Trim(' ');
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length != 6 || !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            return null;
        var rgb = new Double3((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF) / 255;
        return Perceptual.ToOkLab(rgb);
    }

    /// <summary>An sRGB colour as <c>#RRGGBB</c>.</summary>
    public static string ToHex(SrgbColour colour)
    {
        static int Byte(double value) => (int)SwiftMath.Round(Math.Min(1, Math.Max(0, value)) * 255);
        return string.Create(CultureInfo.InvariantCulture, $"#{Byte(colour.R):X2}{Byte(colour.G):X2}{Byte(colour.B):X2}");
    }
}

/// <summary>
/// Highlight colours, worked out in OKLab/OKLCH. A tile is a flat, opaque piece of coloured paper with the word set on
/// it. Every tile here is vivid (chroma lifted from its source, short of neon), at a pleasing lightness (a deep tile,
/// L 0.38â€“0.62, with near-white type, or a light one, L 0.70â€“0.92, with deep ink of its own hue), readable (the text on
/// it reaches <see cref="TextContrast"/>), clear of the wallpaper by <see cref="Separation"/>, never muddy (olive,
/// khaki, mustard, brown, rust, brick and anything greyish are turned to the nearest clean hue), and a pure function of
/// its inputs.
/// </summary>
public static class LyricHighlight
{
    /// <summary>The user's custom highlight until one is picked.</summary>
    public const string DefaultCustomHex = "#FF92A3";

    /// <summary>The custom colour's fallback when nothing can be parsed.</summary>
    public static OkLab Coral { get; } = Perceptual.ToOkLab(new Double3(1, 0x6F / 255.0, 0x61 / 255.0));

    public const double TextContrast = 4.5;
    public const double IncreasedTextContrast = 7.0;

    /// <summary>How much lighter than the brightest backdrop a tile is, in OKLab L.</summary>
    public const double Separation = 0.15;

    /// <summary>No tile is closer to the backdrop than this.</summary>
    public const double MinimumSeparation = 0.06;

    public const double DeepBandLow = 0.38, DeepBandHigh = 0.62;
    public const double LightBandLow = 0.70, LightBandHigh = 0.92;

    public static double Degrees(double radians)
    {
        var value = radians * 180 / Math.PI % 360;
        return value < 0 ? value + 360 : value;
    }

    /// <summary>The angle between two hues, 0...Ï€.</summary>
    public static double HueDistance(double first, double second)
    {
        const double turn = 2 * Math.PI;
        var difference = (first - second) % turn;
        if (difference < 0) difference += turn;
        return Math.Min(difference, turn - difference);
    }

    /// <summary>Olive, khaki, citron, mustard, brown, rust, brick, tan, or anything greyish.</summary>
    public static bool IsMuddy(OkLab colour)
    {
        double hue = Degrees(colour.Hue), l = colour.L, c = colour.Chroma;
        // Yellow-green is lemon or lime only when light; otherwise citron, olive, khaki.
        if (hue >= 95 && hue <= 140) return l < OliveFloor(hue) || c < 0.12;
        // Green, deep only when vivid (emerald); a dull deep one is olive.
        if (hue >= 140 && hue < 165) return l < 0.62 && c < 0.15;
        // Gold, unless dark (goldenrod, mustard) or dull (khaki).
        if (hue >= 70 && hue < 95) return l < 0.74 || c < 0.11;
        // Orange, unless deep (brown, rust) or dull (tan).
        if (hue >= 30 && hue < 70) return l < 0.62 || c < 0.09;
        // Red-orange: vermilion when vivid, brick when deep and dull.
        if (hue >= 20 && hue < 30) return l < 0.62 && c < 0.15;
        return c < (l < 0.8 ? 0.07 : 0.045);
    }

    /// <summary>Lemon, lime and chartreuse read acid beside golden yellow or mint: a nudge towards either side.</summary>
    public static double Acidity(double hue) => 0.13 * Math.Max(0, 1 - Math.Abs(Degrees(hue) - 118) / 24);

    /// <summary>How light yellow-green must be not to read as olive: 0.8 for yellow, 0.88 through lime, 0.7 at green.</summary>
    public static double OliveFloor(double degrees)
    {
        if (degrees < 110) return 0.8 + 0.08 * Math.Max(0, degrees - 95) / 15;
        if (degrees <= 125) return 0.88;
        return 0.88 - 0.18 * Math.Min(1, (degrees - 125) / 15);
    }

    /// <summary>The lightness a light tile looks best at: yellows sit lighter.</summary>
    public static double LightSweet(double hue) => 0.8 + 0.06 * Math.Max(0, 1 - Math.Abs(Degrees(hue) - 95) / 35);

    /// <summary>The most chroma sRGB can show at this lightness and hue.</summary>
    public static double MaxChroma(double l, double hue)
    {
        double low = 0, high = 0.37;
        for (var i = 0; i < 14; i++)
        {
            var middle = (low + high) / 2;
            if (Perceptual.InGamut(OkLab.FromLch(l, middle, hue))) low = middle; else high = middle;
        }
        return low;
    }

    /// <summary>Light paper keeps a little short of neon, greens and cyans most.</summary>
    public static double LightCap(double hue)
    {
        var degrees = Degrees(hue);
        if (degrees >= 165 && degrees <= 230) return 0.115;
        if (degrees >= 140 && degrees < 165) return 0.13;
        if (degrees >= 75 && degrees < 140) return 0.155;
        return 0.16;
    }

    public const double DeepCap = 0.19;

    public static OkLab LightInk(OkLab tile) => OkLab.FromLch(0.985, 0.01, tile.Hue);

    public static OkLab DeepInk(OkLab tile) => OkLab.FromLch(0.25, Math.Min(0.05, tile.Chroma * 0.3), tile.Hue);

    /// <summary>
    /// The text for a tile: near-white when that reads better and well enough, otherwise deep ink of the tile's own hue,
    /// deepened as far as it must be to reach <paramref name="ratio"/>, or plain white.
    /// </summary>
    public static OkLab Text(OkLab tile, double ratio = TextContrast)
    {
        var background = Perceptual.Luminance(tile);
        double Contrast(OkLab ink) => Perceptual.Contrast(Perceptual.Luminance(ink), background);
        var light = LightInk(tile);
        var deep = DeepInk(tile);
        if (Contrast(light) >= ratio && Contrast(light) >= Contrast(deep)) return light;
        while (Contrast(deep) < ratio && deep.L > 0.03)
            deep = new OkLab(deep.L - 0.03, deep.A * 0.9, deep.B * 0.9);
        if (Contrast(deep) >= ratio) return deep;
        // The first of the best, as Swift's max(by:) keeps.
        OkLab best = light;
        foreach (var candidate in (ReadOnlySpan<OkLab>)[OkLab.White, deep])
            if (Contrast(best) < Contrast(candidate)) best = candidate;
        return best;
    }

    /// <summary>Readings on a tile are its text at this opacity.</summary>
    public const double ReadingOpacity = 0.85;

    /// <summary><paramref name="colour"/> drawn at <paramref name="opacity"/> over <paramref name="background"/>, blended in gamma-encoded sRGB.</summary>
    public static OkLab Blend(OkLab colour, OkLab background, double opacity)
    {
        var alpha = Math.Min(1, Math.Max(0, opacity));
        return Perceptual.ToOkLab(Perceptual.Srgb(colour) * alpha + Perceptual.Srgb(background) * (1 - alpha));
    }

    /// <summary>
    /// How clear of the backdrop a tile at <paramref name="l"/> is: lighter than the brightest backdrop, or over a
    /// backdrop so bright that nothing light would show, darker.
    /// </summary>
    public static double Clearance(double l, double backdrop)
    {
        var floor = Math.Cbrt(Math.Min(1, Math.Max(0, backdrop)));
        return floor >= 0.74 ? floor - l : l - floor;
    }

    /// <summary>A found tile and its score.</summary>
    public readonly record struct Scored(OkLab Tile, double Score);

    /// <summary>
    /// The best tile for one hue: lightness searched over both bands, chroma as high as asked for and the gamut and cap
    /// allow. Null when no tile of this hue can carry legible text clear of the backdrop.
    /// </summary>
    public static Scored? Tile(double hue, double chroma, double backdrop, double ratio = TextContrast)
    {
        Scored? best = null;
        foreach (var light in (ReadOnlySpan<bool>)[false, true])
        {
            double low = light ? LightBandLow : DeepBandLow, high = light ? LightBandHigh : DeepBandHigh;
            var sweet = light ? LightSweet(hue) : 0.50;
            var cap = light ? LightCap(hue) : DeepCap;
            for (var step = 0; step <= 24; step++)
            {
                var l = low + (high - low) * step / 24;
                var c = Math.Min(Math.Min(chroma, cap), MaxChroma(l, hue) * 0.96);
                var tile = OkLab.FromLch(l, Math.Max(0, c), hue);
                var ink = light ? DeepInk(tile) : LightInk(tile);
                if (!(Perceptual.Contrast(Perceptual.Luminance(ink), Perceptual.Luminance(tile)) >= ratio)) continue;
                var clear = Clearance(l, backdrop);
                if (!(clear >= MinimumSeparation)) continue;
                var score = c * 3 - Math.Abs(l - sweet) * 0.8 - Math.Max(0, Separation - clear) * 4 - Acidity(hue);
                if (light) score += 0.10;
                if (IsMuddy(tile)) score -= 1;
                if (best is not { } current || score > current.Score) best = new Scored(tile, score);
            }
        }
        return best;
    }

    /// <summary>A hue to try, how vivid to make it, and what straying to it costs.</summary>
    public readonly record struct Candidate(double Hue, double Chroma, double Penalty);

    /// <summary>The best-scoring tile among <paramref name="candidates"/>.</summary>
    public static Scored? Best(IEnumerable<Candidate> candidates, double backdrop, double ratio)
    {
        Scored? best = null;
        foreach (var candidate in candidates)
        {
            if (Tile(candidate.Hue, candidate.Chroma, backdrop, ratio) is not { } found) continue;
            var score = found.Score - candidate.Penalty;
            if (best is not { } current || score > current.Score) best = new Scored(found.Tile, score);
        }
        return best;
    }

    /// <summary>Small turns, so a muddy hue can step to the nearest clean one.</summary>
    public static IReadOnlyList<double> Turns { get; } = [0, -15, 15, -30, 30, -45, 45];

    /// <summary>The complement's turns: finer, so as the lyric colour drifts its complement moves in small steps.</summary>
    public static IReadOnlyList<double> FineTurns { get; } = MakeFineTurns();

    private static double[] MakeFineTurns()
    {
        var turns = new List<double>();
        for (var turn = -45.0; turn <= 45; turn += 5) turns.Add(turn);
        // Stable, by distance from zero: -5 before 5, as the Mac's sort leaves them.
        return [.. turns.OrderBy(Math.Abs)];
    }

    /// <summary>
    /// Opposite <paramref name="lyric"/> on the wheel, or up to 45Â° either side (split-complementary), keeping a little
    /// clear of <paramref name="stops"/>, the other colours the lyrics are drawn in. Null for a colour with no hue to
    /// oppose. <paramref name="previous"/>, the tile chosen a moment ago, is kept a little longer when two hues score
    /// alike, so the tile does not flicker between them.
    /// </summary>
    public static OkLab? Complement(OkLab lyric, double backdrop, IReadOnlyList<OkLab>? stops = null, OkLab? previous = null,
                                    double? backgroundHue = null, double ratio = TextContrast)
    {
        if (!(lyric.Chroma >= 0.035)) return null;
        var others = new List<OkLab>();
        if (stops is not null) foreach (var stop in stops) if (stop.Chroma >= 0.04) others.Add(stop);
        var candidates = new List<Candidate>(FineTurns.Count);
        foreach (var turn in FineTurns)
        {
            var hue = lyric.Hue + Math.PI + turn * Math.PI / 180;
            // A vivid lyric colour of the same hue would hide the tile; a pale one, much less.
            var crowding = 0.0;
            var anyOther = false;
            foreach (var other in others)
            {
                var value = Math.Max(0, 30 - Degrees(HueDistance(hue, other.Hue))) * Math.Min(1, other.Chroma / 0.14);
                crowding = anyOther ? Math.Max(crowding, value) : value;
                anyOther = true;
            }
            var staying = previous is { } kept ? (Degrees(HueDistance(hue, kept.Hue)) <= 8 ? 0.025 : 0) : 0;
            var background = BackgroundPenalty(hue, backgroundHue);
            candidates.Add(new Candidate(hue, 0.19, Math.Abs(turn) * 0.003 + crowding * 0.004 + background - staying));
        }
        return Best(candidates, backdrop, ratio)?.Tile;
    }

    /// <summary>The fallback hue for a cover with no colour at all: vermilion, the classic accent for black and white.</summary>
    public const double MonochromeHue = 25 * Math.PI / 180;

    /// <summary>
    /// The cover's richest colour as a tile. The most vivid cover colours are preferred, the accent the layouts used
    /// before breaks ties, and a muddy hue is turned to the nearest clean one. A cover of one hue that gives nothing
    /// clean fans out to its neighbours and its complement; a cover with no colour takes vermilion.
    /// </summary>
    public static OkLab Album(IReadOnlyList<OkLab> colours, double backdrop, OkLab? accent = null, double? backgroundHue = null,
                              double ratio = TextContrast)
    {
        var chromatic = new List<OkLab>();
        foreach (var colour in colours) if (colour.Chroma >= 0.04) chromatic.Add(colour);
        OkLab? richestFound = null;
        foreach (var colour in chromatic)
            if (richestFound is not { } r || r.Chroma < colour.Chroma) richestFound = colour;
        if (richestFound is not { } richest)
        {
            var vermilion = new List<Candidate>();
            foreach (var turn in (ReadOnlySpan<double>)[0.0, -20, 20])
                vermilion.Add(new Candidate(MonochromeHue + turn * Math.PI / 180, 0.19, Math.Abs(turn) * 0.004));
            return Best(vermilion, backdrop, ratio)?.Tile ?? OkLab.FromLch(0.5, 0.19, MonochromeHue);
        }
        var candidates = new List<Candidate>();
        foreach (var colour in chromatic)
        {
            var want = Math.Min(0.19, Math.Max(0.15, colour.Chroma * 1.5 + 0.04));
            var preference = (richest.Chroma - colour.Chroma) * 1.2;
            if (accent is { } a && a.Chroma >= 0.04 && HueDistance(colour.Hue, a.Hue) < 20 * Math.PI / 180) preference -= 0.03;
            foreach (var turn in Turns)
            {
                var hue = colour.Hue + turn * Math.PI / 180;
                candidates.Add(new Candidate(hue, want,
                    preference + Math.Abs(turn) * 0.004 + BackgroundPenalty(hue, backgroundHue)));
            }
        }
        var own = Best(candidates, backdrop, ratio);
        var spread = 0.0;
        foreach (var first in chromatic)
            foreach (var second in chromatic)
                spread = Math.Max(spread, HueDistance(first.Hue, second.Hue));
        if (spread < 40 * Math.PI / 180 && (own is not { } found || IsMuddy(found.Tile) || found.Tile.Chroma < 0.09))
        {
            // One hue, and nothing clean in it: a coordinated fan.
            foreach (var turn in (ReadOnlySpan<double>)[60.0, -60, 180])
                candidates.Add(new Candidate(richest.Hue + turn * Math.PI / 180, 0.17, 0.12 + Math.Abs(turn) * 0.0008));
            if (Best(candidates, backdrop, ratio) is { } fanned) return fanned.Tile;
        }
        return own?.Tile ?? OkLab.FromLch(0.5, 0.15, richest.Hue);
    }

    private static double BackgroundPenalty(double hue, double? backgroundHue) =>
        backgroundHue is { } background ? 0.50 * Math.Max(0, 1 - HueDistance(hue, background) / (Math.PI / 3)) : 0;

    /// <summary>The user's own colour as a tile: its hue and chroma kept, its lightness moved only as far as legibility and the backdrop need.</summary>
    public static OkLab Custom(OkLab colour, double backdrop, double ratio = TextContrast)
    {
        OkLab? best = null;
        var bestCost = 0.0;
        for (var step = 0; step <= 60; step++)
        {
            var l = 0.06 + 0.9 * step / 60;
            var tile = OkLab.FromLch(l, Math.Min(colour.Chroma, MaxChroma(l, colour.Hue)), colour.Hue);
            var ink = Text(tile, ratio);
            if (!(Perceptual.Contrast(Perceptual.Luminance(ink), Perceptual.Luminance(tile)) >= ratio)) continue;
            var clear = Clearance(l, backdrop);
            if (!(clear >= MinimumSeparation)) continue;
            var cost = Math.Abs(l - colour.L) + Math.Max(0, Separation - clear) * 2 + (colour.Chroma - tile.Chroma) * 2;
            if (best is null || cost < bestCost) { best = tile; bestCost = cost; }
        }
        return best ?? Perceptual.Clipped(colour);
    }

    /// <summary>The tile's hue as type of its own, for held words: light and clear, and readable against the backdrop.</summary>
    public static OkLab Held(OkLab tile, double backdrop, double ratio)
    {
        var tone = OkLab.FromLch(Math.Max(0.8, tile.L), Math.Min(0.15, tile.Chroma), tile.Hue);
        return Perceptual.Readable(tone, backdrop, ratio);
    }

    /// <summary>A highlight for ink no director made (previews, cards): vermilion.</summary>
    public static LyricHighlightTone Fallback { get; } =
        Tone(Album([], 0.08), 0.08, TextContrast, LyricInk.MinimumContrast);

    public static SrgbColour FallbackTile => Perceptual.ToColour(Fallback.Tile);

    public static SrgbColour FallbackText => Perceptual.ToColour(Fallback.Text);

    /// <summary>
    /// How opaque a fading (or arriving) tile is when the word on it should change between the colour set on the tile
    /// and its own lyric colour (a luminance): where the lyric colour over the tile on the brightest backdrop reads as
    /// well as the tile's ink over the tile on black. Only ink darker than the lyrics crosses over.
    /// </summary>
    public static double TextSwitch(OkLab tile, OkLab text, double backdrop, double lyric)
    {
        var ink = Perceptual.Luminance(text);
        if (!(ink < lyric)) return 0.5;
        double paper = LyricLegibility.Encoded(Perceptual.Luminance(tile)), bright = LyricLegibility.Encoded(backdrop);
        double Light(double opacity) => (lyric + 0.05) / (LyricLegibility.Linear(opacity * paper + (1 - opacity) * bright) + 0.05);
        double Deep(double opacity) => (LyricLegibility.Linear(opacity * paper) + 0.05) / (ink + 0.05);
        double low = 0, high = 1;
        for (var i = 0; i < 24; i++)
        {
            var middle = (low + high) / 2;
            if (Deep(middle) < Light(middle)) low = middle; else high = middle;
        }
        return (low + high) / 2;
    }

    /// <summary>The whole highlight for a tile.</summary>
    public static LyricHighlightTone Tone(OkLab tile, double backdrop, double textRatio, double lyricRatio) =>
        new(tile, Text(tile, textRatio), Held(tile, backdrop, lyricRatio));
}

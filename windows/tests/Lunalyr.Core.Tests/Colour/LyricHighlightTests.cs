using System.Globalization;
using System.Numerics;
using Lunalyr.Core.Colour;

namespace Lunalyr.Core.Tests.Colour;

/// <summary>
/// The colour half of the Mac's LyricHighlightTests: the highlight tile, its text, held words and the ink director's
/// highlight. Not ported here: the tile's press-and-fade timing, layout and rendering tests
/// (testTheTilePressesIn…, testRollingHighlights…, testReduceMotionOnlyFadesTheTile, testTheNewEntranceIsOffered…,
/// testEveryLayoutHighlights…, testTheTileNeverMoves…, testARunOfJapanese…) belong to the lyric compositions, and the
/// UserDefaults half of testHighlightSettingsPersistInIsolatedDefaults to the settings store.
/// </summary>
public class LyricHighlightTests
{
    private static Vector3 Srgb(string hex)
    {
        var value = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Vector3((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF) / 255;
    }

    /// <summary>Covers as the wallpaper passes them to the stage: five sRGB stops.</summary>
    private static readonly (string Name, Vector3[] Stops)[] Covers =
    [
        ("olive and khaki", new[] { "#5C4A2A", "#9E9457", "#75663A", "#383019", "#8F7A4D" }.Select(Srgb).ToArray()),
        ("brown leather", new[] { "#3B2414", "#6E4426", "#8B5A2B", "#A0522D", "#2A1A10" }.Select(Srgb).ToArray()),
        ("mustard", new[] { "#B8860B", "#C9A227", "#8B7500", "#DAA520", "#6B5A12" }.Select(Srgb).ToArray()),
        ("monochrome blue", new[] { "#1A2E6B", "#4D7AD1", "#3861B8", "#0F1A40", "#203A80" }.Select(Srgb).ToArray()),
        ("saturated", new[] { "#E61F33", "#FAC714", "#D933BF", "#FA730F", "#262629" }.Select(Srgb).ToArray()),
        ("pastel", new[] { "#FAD9E0", "#CCF2E5", "#E6DBFA", "#F5E0F0", "#FCF2D9" }.Select(Srgb).ToArray()),
        ("black and white", new[] { "#0D0D0D", "#808080", "#1F1F1F", "#4D4D4D", "#F2F2F2" }.Select(Srgb).ToArray()),
        ("all grey", new[] { "#777777", "#777777", "#777777", "#777777", "#777777" }.Select(Srgb).ToArray()),
        ("forest", new[] { "#1E3B1E", "#2E5A2E", "#556B2F", "#3C5A14", "#6B8E23" }.Select(Srgb).ToArray()),
    ];

    private static Vector3[] Cover(string name) => Covers.First(c => c.Name == name).Stops;

    /// <summary>Many more covers, deterministic: hues all round the wheel at every saturation and brightness.</summary>
    private static List<Vector3[]> RandomCovers(int count)
    {
        var state = 0x9E37_79B9_7F4A_7C15UL;
        float Next()
        {
            unchecked { state = state * 6364136223846793005UL + 1442695040888963407UL; }
            return (state >> 40) / (float)(1 << 24);
        }
        var covers = new List<Vector3[]>();
        for (var c = 0; c < count; c++)
        {
            float hue = Next(), spread = Next() * 0.3f, saturation = Next(), brightness = 0.15f + Next() * 0.85f;
            var stops = new Vector3[5];
            for (var index = 0; index < 5; index++)
            {
                var h = (hue + spread * index / 4) % 1f;
                var s = Math.Min(1f, saturation * (0.6f + Next() * 0.5f));
                var b = Math.Min(1f, brightness * (0.5f + Next() * 0.7f));
                var rgb = ColourSpaces.Rgb(new Double3(h, s, b));
                stops[index] = new Vector3((float)rgb.X, (float)rgb.Y, (float)rgb.Z);
            }
            covers.Add(stops);
        }
        return covers;
    }

    private static OkLab[] Lab(Vector3[] stops) => stops.Select(s => Perceptual.ToOkLab(Double3.From(s))).ToArray();

    /// <summary>The backdrops the stage is given: a black visualiser to a light picture only part shaded.</summary>
    private static readonly double[] Backdrops = [0.005, 0.03, 0.08, 0.155, 0.26, 0.36, 0.55];

    /// <summary>Colours people call muddy, worked out apart from LyricHighlight.</summary>
    private static readonly string[] MuddyNames =
        ["#808000", "#6B8E23", "#556B2F", "#BDB76B", "#C3B091", "#8B4513", "#A0522D", "#B8860B", "#8B7500", "#7B6B3A", "#9E9457", "#6E4426", "#967117", "#A89A6B"];

    private static readonly OkLab[] Muddy = MuddyNames.Select(n => Perceptual.ToOkLab(Double3.From(Srgb(n)))).ToArray();

    private static string Describe(OkLab colour)
    {
        var rgb = Perceptual.Srgb(colour);
        return string.Create(CultureInfo.InvariantCulture,
            $"#{(int)Math.Round(rgb.X * 255):X2}{(int)Math.Round(rgb.Y * 255):X2}{(int)Math.Round(rgb.Z * 255):X2} (L {colour.L:0.00} C {colour.Chroma:0.000} h {LyricHighlight.Degrees(colour.Hue):0})");
    }

    private static void AssertClean(OkLab tile, double backdrop, string context, double ratio = LyricHighlight.TextContrast)
    {
        var hue = LyricHighlight.Degrees(tile.Hue);
        // Not greyish, and no yellow-green, gold or orange darker than a clear yellow or orange.
        Assert.True(tile.Chroma >= 0.06, $"greyish tile {context}");
        if (hue is >= 70 and <= 125) Assert.True(tile.L >= 0.74, $"olive or mustard tile {context}");
        if (hue is >= 125 and <= 140) Assert.True(tile.L >= 0.68, $"olive-green tile {context}");
        if (hue is >= 30 and < 70) Assert.True(tile.L >= 0.6, $"brown or rust tile {context}");
        if (hue is >= 95 and < 165 && tile.L < 0.6) Assert.True(tile.Chroma >= 0.14, $"olive-green tile {context}");
        for (var index = 0; index < Muddy.Length; index++)
        {
            var colour = Muddy[index];
            var distance = Math.Sqrt((tile.L - colour.L) * (tile.L - colour.L) + (tile.A - colour.A) * (tile.A - colour.A)
                                     + (tile.B - colour.B) * (tile.B - colour.B));
            Assert.True(distance > 0.05, $"tile {Describe(tile)} close to muddy {MuddyNames[index]} {context}");
        }
        AssertReadable(tile, backdrop, context, ratio);
    }

    private static void AssertReadable(OkLab tile, double backdrop, string context, double ratio = LyricHighlight.TextContrast)
    {
        var text = LyricHighlight.Text(tile, ratio);
        var contrast = Perceptual.Contrast(Perceptual.Luminance(text), Perceptual.Luminance(tile));
        Assert.True(contrast >= ratio - 0.01, $"text on tile {context}");
        // Readings on a tile are the text at 85%, over the tile.
        var reading = LyricHighlight.Blend(text, tile, LyricHighlight.ReadingOpacity);
        Assert.True(Perceptual.Contrast(Perceptual.Luminance(reading), Perceptual.Luminance(tile)) >= 3, $"reading on tile {context}");
        Assert.True(LyricHighlight.Clearance(tile.L, backdrop) >= LyricHighlight.MinimumSeparation, $"tile lost in the backdrop {context}");
    }

    // Ported from LyricHighlightTests.swift: testAlbumTilesAreVividCleanAndReadableOnEveryCover
    [Fact]
    public void AlbumTilesAreVividCleanAndReadableOnEveryCover()
    {
        var covers = Covers.Select(c => c.Stops).Concat(RandomCovers(160)).ToList();
        for (var index = 0; index < covers.Count; index++)
        {
            var colours = Lab(covers[index]);
            foreach (var backdrop in Backdrops)
            foreach (var ratio in new[] { LyricHighlight.TextContrast, LyricHighlight.IncreasedTextContrast })
            {
                var tile = LyricHighlight.Album(colours, backdrop, ratio: ratio);
                AssertClean(tile, backdrop, $"cover {index} over {backdrop}", ratio);
                // Lighter than any backdrop up to the brightest it can be where the lyrics sit, by a clear step.
                if (backdrop <= 0.36)
                    Assert.True(LyricHighlight.Clearance(tile.L, backdrop) >= 0.1, $"cover {index} over {backdrop}");
            }
        }
    }

    // Ported from LyricHighlightTests.swift: testAMuddyCoverTurnsToACleanHueOfItsOwn
    [Fact]
    public void AMuddyCoverTurnsToACleanHueOfItsOwn()
    {
        foreach (var name in new[] { "olive and khaki", "brown leather", "mustard", "forest" })
        {
            var cover = Lab(Cover(name));
            foreach (var backdrop in Backdrops)
            {
                var tile = LyricHighlight.Album(cover, backdrop);
                var nearest = cover.Where(c => c.Chroma >= 0.04)
                    .Select(c => LyricHighlight.Degrees(LyricHighlight.HueDistance(c.Hue, tile.Hue))).DefaultIfEmpty(0).Min();
                Assert.True(nearest <= 75, $"{name} over {backdrop}");
                Assert.True(tile.Chroma >= 0.1, $"{name} over {backdrop}");
            }
        }
        // A cover with no colour at all takes vermilion.
        var grey = LyricHighlight.Album(Lab(Cover("all grey")), 0.05);
        Assert.True(LyricHighlight.Degrees(LyricHighlight.HueDistance(grey.Hue, LyricHighlight.MonochromeHue)) < 25);
        Assert.True(grey.Chroma > 0.12);
    }

    // Ported from LyricHighlightTests.swift: testTilesAreDeterministicPerCover
    [Fact]
    public void TilesAreDeterministicPerCover()
    {
        foreach (var (_, stops) in Covers)
        {
            var colours = Lab(stops);
            Assert.Equal(LyricHighlight.Album(colours, 0.1), LyricHighlight.Album(colours, 0.1));
        }
    }

    // Ported from LyricHighlightTests.swift: testComplementAvoidsAnAlbumBackgroundThatMatchesTheOppositeHue
    [Fact]
    public void ComplementAvoidsAnAlbumBackgroundThatMatchesTheOppositeHue()
    {
        const double background = 2 * Math.PI / 3;
        var lyric = OkLab.FromLch(0.84, 0.15, background + Math.PI);
        var tile = Assert.NotNull(LyricHighlight.Complement(lyric, 0.13, backgroundHue: background));
        Assert.True(LyricHighlight.HueDistance(tile.Hue, background) > Math.PI / 6);
        Assert.True(LyricHighlight.HueDistance(tile.Hue, lyric.Hue) >= Math.PI * 0.74);
        AssertReadable(tile, 0.13, "tile beside the album and lyric colours");
    }

    // Ported from LyricHighlightTests.swift: testComplementOpposesTheLyricColourAndStaysClean
    [Fact]
    public void ComplementOpposesTheLyricColourAndStaysClean()
    {
        for (var step = 0; step * 7.5 < 360; step++)
        {
            var hue = step * 7.5;
            foreach (var (l, c) in new[] { (0.78, 0.08), (0.85, 0.14), (0.7, 0.18) })
            {
                var lyric = OkLab.FromLch(l, c, hue * Math.PI / 180);
                foreach (var backdrop in Backdrops)
                {
                    var tile = LyricHighlight.Complement(lyric, backdrop);
                    Assert.True(tile is not null, $"no complement for hue {hue}");
                    // Complementary, or split-complementary: 135-180 degrees round.
                    Assert.True(LyricHighlight.Degrees(LyricHighlight.HueDistance(tile.Value.Hue, lyric.Hue)) >= 134.9, $"hue {hue} over {backdrop}");
                    AssertClean(tile.Value, backdrop, $"complement of hue {hue} over {backdrop}");
                }
            }
        }
        // White and grey have no hue to oppose.
        Assert.Null(LyricHighlight.Complement(OkLab.White, 0.05));
        Assert.Null(LyricHighlight.Complement(new OkLab(0.7, 0.01, -0.01), 0.05));
    }

    // Ported from LyricHighlightTests.swift: testComplementFollowsTheLyricColourAsItDrifts
    [Fact]
    public void ComplementFollowsTheLyricColourAsItDrifts()
    {
        // The default custom gradient drifting at speed 2, as the user has it.
        var custom = LyricColourOptions.DefaultCustom.Select(ColourHex.ToOkLab).OfType<OkLab>().ToArray();
        var director = new LyricInkDirector();
        var hues = new List<double>();
        int misses = 0, frames = 0;
        for (var frame = 0; frame < 60 * 30; frame++)
        {
            var ink = director.Ink(new LyricInkInputs(LyricColourMode.CustomGradient) { Speed = 2, Custom = custom, Backdrop = 0.1 }, frame / 30.0);
            if (frame < 90 || ink.Highlight is not { } tone) continue;
            frames += 1;
            var lyric = ink.Gradient(0.5);
            // The tile eases behind the drifting colour; it stays opposite it.
            if (LyricHighlight.Degrees(LyricHighlight.HueDistance(tone.Tile.Hue, lyric.Hue)) < 120) misses += 1;
            hues.Add(LyricHighlight.Degrees(tone.Tile.Hue));
            AssertReadable(tone.Tile, ink.Backdrop, $"frame {frame}");
        }
        Assert.True((double)misses / frames < 0.03, "the tile kept opposite the lyric colour");
        // It went round with the colour, not stayed put.
        var buckets = hues.Select(h => (int)(h / 30)).ToHashSet();
        Assert.True(buckets.Count >= 6, $"tile hues {string.Join(",", buckets.Order())}");
        // Between two stops of opposite hue the lyric colour passes through grey; the basis turns round the wheel.
        var drifting = new LyricInk
        {
            Mode = LyricColourMode.CustomGradient,
            Stops = new InkStops(OkLab.FromLch(0.85, 0.1, 1.3), OkLab.FromLch(0.85, 0.1, 1.3 + Math.PI), OkLab.White, OkLab.White),
            StopCount = 2,
        };
        for (var step = 0; step <= 20; step++)
        {
            drifting.Phase = step / 20.0;
            Assert.True(drifting.Gradient(0.5).Chroma > 0.09);
        }
    }

    // Ported from LyricHighlightTests.swift: testWhiteLyricsTakeTheAlbumAccent
    [Fact]
    public void WhiteLyricsTakeTheAlbumAccent()
    {
        var cover = Cover("saturated");
        var director = new LyricInkDirector();
        var white = director.Ink(new LyricInkInputs(LyricColourMode.Standard) { Album = cover, Backdrop = 0.05 }, 0);
        var album = new LyricInkDirector().Ink(new LyricInkInputs(LyricColourMode.Standard)
        {
            Album = cover, Backdrop = 0.05, Highlight = LyricHighlightMode.Album,
        }, 0);
        Assert.Equal(album.Highlight, white.Highlight);
        // Worked out for the backdrop as the director steps it (1/256).
        Assert.Equal(LyricHighlight.Album(Lab(cover), Math.Round(0.05 * 256, MidpointRounding.AwayFromZero) / 256), director.GoalTile);
        var drawn = Assert.NotNull(white.Highlight).Tile;
        Assert.Equal(director.GoalTile.L, drawn.L, 1.0 / 512);
        Assert.Equal(director.GoalTile.A, drawn.A, 1.0 / 512);
        Assert.Equal(director.GoalTile.B, drawn.B, 1.0 / 512);
    }

    // Ported from LyricHighlightTests.swift: testEachModeTakesItsOwnColour
    [Fact]
    public void EachModeTakesItsOwnColour()
    {
        var cover = Cover("monochrome blue");
        var teal = OkLab.FromLch(0.75, 0.12, 190 * Math.PI / 180);
        var coral = Assert.NotNull(ColourHex.ToOkLab(LyricHighlight.DefaultCustomHex));
        OkLab Tile(LyricHighlightMode mode, OkLab lyric)
        {
            var director = new LyricInkDirector();
            var ink = LyricInk.White;
            for (var frame = 0; frame < 120; frame++)
                ink = director.Ink(new LyricInkInputs(LyricColourMode.CustomGradient)
                {
                    Custom = [lyric], Album = cover, Backdrop = 0.05, Highlight = mode, HighlightCustom = coral,
                }, frame / 30.0);
            return Assert.NotNull(ink.Highlight).Tile;
        }
        var pink = OkLab.FromLch(0.8, 0.12, 0);
        // Album accent: the cover's own colour, whatever the lyrics.
        Assert.Equal(Tile(LyricHighlightMode.Album, teal).Hue, Tile(LyricHighlightMode.Album, pink).Hue, 0.02);
        // Custom: the user's own hue.
        Assert.True(LyricHighlight.HueDistance(Tile(LyricHighlightMode.Custom, teal).Hue, coral.Hue) < 0.03);
        // Complement: opposite the lyrics, so it changes with them.
        var opposite = Tile(LyricHighlightMode.Complement, teal);
        Assert.True(LyricHighlight.Degrees(LyricHighlight.HueDistance(opposite.Hue, teal.Hue)) > 134);
        Assert.True(LyricHighlight.Degrees(LyricHighlight.HueDistance(opposite.Hue, Tile(LyricHighlightMode.Complement, pink).Hue)) > 90);
    }

    // Ported from LyricHighlightTests.swift: testCustomColoursKeepTheirHueAndStayReadable
    [Fact]
    public void CustomColoursKeepTheirHueAndStayReadable()
    {
        foreach (var hex in new[] { "#FF6F61", "#FFFF00", "#000080", "#FFFFFF", "#000000", "#808080", "#808000", "#00FFAA", "#6A0DAD" })
        {
            var colour = Assert.NotNull(ColourHex.ToOkLab(hex));
            foreach (var backdrop in Backdrops)
            {
                var tile = LyricHighlight.Custom(colour, backdrop);
                AssertReadable(tile, backdrop, $"{hex} over {backdrop}");
                if (colour.Chroma > 0.05)
                    Assert.True(LyricHighlight.Degrees(LyricHighlight.HueDistance(tile.Hue, colour.Hue)) < 2, $"{hex} over {backdrop}");
            }
        }
    }

    // Ported from LyricHighlightTests.swift: testHighlightSettingsPersistInIsolatedDefaults (the store-free rules only)
    [Fact]
    public void HighlightSettingsPersistInIsolatedDefaults()
    {
        Assert.Equal(LyricHighlightMode.Complement, default(LyricHighlightMode));
        Assert.Equal("#FF92A3", LyricHighlight.DefaultCustomHex);
        Assert.Equal("#0080FF", ColourHex.ToHex(new SrgbColour(0, 0.5, 1)));
        Assert.Equal(ColourHex.ToOkLab("#0080FF"), ColourHex.ToOkLab(" 0080ff "));
        Assert.Null(ColourHex.ToOkLab("#12"));
        Assert.Null(ColourHex.ToOkLab("sepia"));
    }

    // Ported from LyricHighlightTests.swift: testTheStageInkCarriesTheHighlightInEveryTextMode
    [Fact]
    public void TheStageInkCarriesTheHighlightInEveryTextMode()
    {
        var custom = LyricColourOptions.DefaultCustom.Select(ColourHex.ToOkLab).OfType<OkLab>().ToArray();
        var covers = Covers.Where(c => c.Name is "olive and khaki" or "brown leather" or "monochrome blue" or "saturated" or "black and white")
            .Select(c => c.Stops).ToList();
        foreach (var mode in LyricColourModes.All)
        foreach (var cover in covers)
        foreach (var backdrop in new[] { 0.02, 0.3 })
        foreach (var increased in new[] { false, true })
        {
            var director = new LyricInkDirector();
            var ratio = increased ? LyricHighlight.IncreasedTextContrast : LyricHighlight.TextContrast;
            for (var frame = 0; frame < 45; frame++)
            {
                var audio = new AudioLevels(frame % 20 < 10 ? 0.4 : 0.05, 0.3, 0.2, frame % 30 < 15 ? 0.4 : 0, 0);
                var ink = director.Ink(new LyricInkInputs(mode)
                {
                    Custom = custom, Album = cover, Backdrop = backdrop, Audio = audio, IncreaseContrast = increased,
                }, frame / 20.0);
                var tone = Assert.NotNull(ink.Highlight);
                // Where the tile is heading is always clean and readable.
                AssertClean(director.GoalTile, ink.Backdrop, $"{mode} over {backdrop}", ratio);
                // The tile drawn eases there; whatever it is on the way, the text on it reaches AA.
                var onTile = Perceptual.Contrast(Perceptual.Luminance(tone.Text), Perceptual.Luminance(tone.Tile));
                Assert.True(onTile >= LyricHighlight.TextContrast - 0.01, $"{mode} frame {frame}");
                if (ink.Settled) Assert.True(onTile >= ratio - 0.01, $"{mode} frame {frame}");
                // Held words are type on the wallpaper: readable as the lyrics are.
                var held = Perceptual.Luminance(tone.Held);
                Assert.True(Perceptual.Contrast(held, ink.Backdrop) >= ink.Contrast - 0.02 || held > 0.9, $"{mode} frame {frame}");
            }
        }
    }

    /// <summary>The share of the tile's own text a word shows (LyricHighlightTiming.textOpacity, from the compositions).</summary>
    private static double TextOpacity(double opacity, double switchAt) =>
        Lunalyr.Core.Motion.Easing.Smoothstep((opacity - switchAt) / SwitchWidth + 0.5);

    private const double SwitchWidth = 0.04;

    // Ported from LyricHighlightTests.swift: testTheWordStaysReadableWhileItsTileFades
    [Fact]
    public void TheWordStaysReadableWhileItsTileFades()
    {
        foreach (var brightest in new[] { 0.01, 0.05, 0.12, 0.2 })
        for (var hue = 0.0; hue < 360; hue += 30)
        {
            if (LyricHighlight.Tile(hue * Math.PI / 180, 0.16, brightest) is not { } found) continue;
            var tile = found.Tile;
            var ink = LyricHighlight.Text(tile);
            foreach (var pastel in new[] { OkLab.White, OkLab.FromLch(0.86, 0.08, 1) })
            {
                // Lyric colours are lifted to 3:1 against the backdrop, as the stage lifts them.
                var lyric = Perceptual.Readable(pastel, brightest);
                var at = LyricHighlight.TextSwitch(tile, ink, brightest, Perceptual.Luminance(lyric));
                foreach (var backdrop in new[] { 0, brightest / 2, brightest })
                for (var step = 0; step <= 200; step++)
                {
                    var opacity = step / 200.0;
                    if (!(Math.Abs(opacity - at) > SwitchWidth / 2)) continue;
                    var shown = TextOpacity(opacity, at);
                    var behind = LyricLegibility.Linear(opacity * LyricLegibility.Encoded(Perceptual.Luminance(tile))
                        + (1 - opacity) * LyricLegibility.Encoded(backdrop));
                    var text = Perceptual.Luminance(LyricHighlight.Blend(ink, lyric, shown));
                    var floor = opacity <= 0.001 || opacity >= 0.98 ? 2.99 : 1.9;
                    Assert.True(Perceptual.Contrast(text, behind) >= floor, $"hue {hue}, {backdrop} of {brightest}, at {opacity}, switching at {at}");
                }
            }
        }
        // A whole tile carries its own text; none, the lyric colour.
        Assert.Equal(1, TextOpacity(1, 0.9));
        Assert.Equal(0, TextOpacity(0, 0.1));
    }
}

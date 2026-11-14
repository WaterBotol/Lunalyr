// Mosaic (the Mac's StageLyricView.mosaic and Star4 in StageLyricStyles.swift; Lunalyr's own code, ported as it is). A
// collage: each word is set a different way as it arrives -- huge and cut off by the edge of the stage with a star
// beside it, white on a colour bar across a pale panel, or letter by letter on jumping baselines -- while the words
// before it shrink into a small stack above.

using System.Globalization;
using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;
using Vortice.Direct2D1;

namespace Lunalyr.Render.Compositions.Stage;

internal sealed class MosaicLayer(IWordBreaker breaker, Logger? logger) : StageLayer(CompositionKind.Mosaic, breaker, logger)
{
    /// <summary>One letter of a word set letter by letter: its ink, place along the word, baseline jump and turn.</summary>
    private readonly record struct Letter(Ink Ink, double X, double Y, double Turn);

    private sealed class MosaicLine(StageSource source) : StageLine(source)
    {
        public int[] Kinds { get; } = new int[Math.Max(1, source.Runs.Count)];
        public StageWord?[] Small { get; } = new StageWord?[Math.Max(1, source.Runs.Count)];
        public Letter[][] Letters { get; } = new Letter[Math.Max(1, source.Runs.Count)][];
        /// <summary>The letter-by-letter kind's reading, and where it sits under the letters.</summary>
        public StageWord?[] LetterReadings { get; } = new StageWord?[Math.Max(1, source.Runs.Count)];
        public double[] LetterReadingY { get; } = new double[Math.Max(1, source.Runs.Count)];
        public double[] LetterScale { get; } = new double[Math.Max(1, source.Runs.Count)];
        public SizeD[] Bar { get; } = new SizeD[Math.Max(1, source.Runs.Count)];

        public override void Dispose()
        {
            base.Dispose();
            foreach (var word in Small) word?.Dispose();
            foreach (var word in LetterReadings) word?.Dispose();
            foreach (var letters in Letters)
                if (letters is not null) foreach (var letter in letters) letter.Ink.Dispose();
        }
    }

    private static readonly double[] BigSizes = [3.0, 2.3, 2.4];

    protected override StageLine Build(StageSource source)
    {
        var line = new MosaicLine(source);
        var u = Unit;
        var tileText = TileText(source.InkTime);
        var words = new StageWord[source.Runs.Count];
        for (var i = 0; i < words.Length; i++)
        {
            var run = source.Runs[i];
            var sample = Sample(line, run);
            var reading = source.Readings[i];
            var kind = (int)(StageShared.Chance(line.Seed, run.Id, 20) * 3) % 3;
            line.Kinds[i] = kind;
            var size = u * BigSizes[kind];
            if (kind == 0)
            {
                // Huge, running off the edge, with a star beside it.
                var spec = WithReading(new WordSpec(size, [Colour(sample.Sung)])
                {
                    MaxWidth = Stage.Width * 1.1, MinimumScale = 0.5, Shadow = Shadow(0.28, 8, 3), Raster = 1.1f,
                }, reading, size, [Colour(sample.Reading)], maxWidth: Stage.Width * 1.1);
                words[i] = Bake(run, run.Text, spec);
            }
            else if (kind == 1)
            {
                // White on a colour bar; a reading is printed on the bar under its word.
                var spec = WithReading(new WordSpec(size, [tileText])
                {
                    MaxWidth = Stage.Width * 0.7, MinimumScale = 0.45, Shadow = Shadow(0.25, 8, 3), Raster = 1.1f,
                }, reading, size, [Faded(tileText, LyricHighlight.ReadingOpacity)], shadow: false, maxWidth: Stage.Width * 0.7);
                words[i] = Bake(run, run.Text, spec);
                line.Bar[i] = new SizeD(words[i].Size.Width + size * 0.56, words[i].Size.Height + size * 0.12);
            }
            else
            {
                // Letter by letter, each on its own baseline: the word as a whole is only measured (no colour baked).
                words[i] = Bake(run, run.Text, new WordSpec(size, []) { MaxWidth = Stage.Width * 0.8, MinimumScale = 0.4 });
                BakeLetters(line, i, run, size, Colour(sample.Sung), reading, Colour(sample.Reading));
            }
            // The small version it becomes, taking over once the big one has shrunk to its size.
            var small = MosaicGeometry.SmallSize(u);
            line.Small[i] = Bake(run, run.Text, WithReading(new WordSpec(small, [Colour(sample.Rest, 0.75)])
            {
                MaxWidth = Stage.Width * 0.4, MinimumScale = 0.4, Shadow = Shadow(0.2, 6, 2),
            }, reading, small, [Colour(sample.Reading)], maxWidth: Stage.Width * 0.4));
        }
        line.Words = words;
        return line;
    }

    private void BakeLetters(MosaicLine line, int i, Core.Motion.StageRun run, double size, Vector4 colour, string? reading,
                             Vector4 readingColour)
    {
        var kit = Kit!;
        var letters = new List<(Ink Ink, double Width, double Y, double Turn)>();
        var enumerator = StringInfo.GetTextElementEnumerator(run.Text);
        var k = 0;
        double total = 0;
        while (enumerator.MoveNext())
        {
            var text = enumerator.GetTextElement();
            var salt = run.Id * 31 + k;
            var letterSize = size * (0.85 + 0.3 * StageShared.Chance(line.Seed, salt, 24));
            using var layout = kit.Layout(text, Face.Heavy, (float)letterSize, TextKit.LocaleFor(text), false);
            var ink = kit.Bake(layout, colour, 1.1f, Shadow(0.28, 8, 3));
            letters.Add((ink, layout.Metrics.WidthIncludingTrailingWhitespace, (StageShared.Chance(line.Seed, salt, 25) - 0.5) * size * 0.28,
                         (StageShared.Chance(line.Seed, salt, 26) - 0.5) * 22));
            total += layout.Metrics.WidthIncludingTrailingWhitespace;
            k++;
        }
        var spacing = size * 0.02;
        total += spacing * Math.Max(0, letters.Count - 1);
        line.LetterScale[i] = total > Stage.Width * 0.8 ? Math.Max(0.4, Stage.Width * 0.8 / total) : 1;
        var placed = new Letter[letters.Count];
        var x = -total / 2;
        for (var j = 0; j < letters.Count; j++)
        {
            placed[j] = new Letter(letters[j].Ink, x + letters[j].Width / 2, letters[j].Y, letters[j].Turn);
            x += letters[j].Width + spacing;
        }
        line.Letters[i] = placed;
        if (reading is not null)
        {
            var spec = WithReading(new WordSpec(size, [colour]), reading, size, [readingColour]);
            var readingSize = spec.ReadingSize;
            line.LetterReadings[i] = Bake(run, reading, new WordSpec(readingSize, [readingColour])
            {
                LabelIsReading = true, Face = Face.Quiet, MaxWidth = Stage.Width * 0.8, MinimumScale = 0.5, Shadow = spec.ReadingShadow,
            });
            line.LetterReadingY[i] = size * 0.6 + spec.ReadingGap + readingSize * 1.25 / 2;
        }
    }

    protected override void DrawLine(ID2D1DeviceContext dc, StageLine stageLine, double time, double exitStart, in Matrix4x4 stage,
                                     double alpha, float blur, int shown)
    {
        var line = (MosaicLine)stageLine;
        var runs = line.Runs;
        var n = shown;
        if (n == 0) return;
        var whole = (1 - StageMotion.Leave(time, exitStart)) * alpha;
        if (!(whole > 0.002)) return;
        var speed = line.Speed;
        var reduce = ReduceMotion;
        var seed = line.Seed;
        var u = Unit;
        var arrivals = line.A.AsSpan(0, n);
        var ranks = line.B.AsSpan(0, n);
        for (var i = 0; i < n; i++) arrivals[i] = StageMotion.Arrival(runs[i].Start, time, speed, reduce);
        StageMotion.Ranks(runs, n, time, speed, seed, reduce, ranks);
        // The stack's rows make room for readings when the line has any.
        var step = MosaicGeometry.StackStep(u, line.HasReadings, Pt);
        var flat = Homography.Flat(stage);
        var tile = Tile(line.InkTime);
        var white = new Vector4(1, 1, 1, 1);
        for (var id = 0; id < n; id++)
        {
            var run = runs[id];
            var a = arrivals[id];
            var rank = ranks[id];
            var settle = StageShared.Glide(Math.Min(rank, 1.2) / 1.2);
            var kind = line.Kinds[id];
            var morph = StageMotion.MosaicMorph(settle, 0.6 / BigSizes[kind]);
            var side = StageShared.Chance(seed, id, 23) < 0.5 ? 1.0 : -1.0;
            var place = MosaicGeometry.Place(id, rank, seed, step, Bounds);
            var at = Matrix3x2.CreateTranslation((float)place.X, (float)place.Y) * flat;
            var bigOpacity = Math.Min(1, a * 1.6) * morph.BigOpacity * whole;
            var size = u * BigSizes[kind];
            var word = line.Words[id];
            if (bigOpacity > 0.002)
            {
                // The big setting, shrinking and dimming as the next word takes over.
                var big = Matrix3x2.CreateScale((float)morph.BigScale) * at;
                if (kind == 0)
                {
                    var unit = Matrix3x2.CreateTranslation(reduce ? 0 : (float)((1 - Math.Min(a, 1)) * Stage.Width * 0.35 * side), 0) * big;
                    DrawLabel(dc, line, word, run, 0, time, Homography.Of(unit), bigOpacity, blur);
                    DrawReading(dc, line, word, run, 0, time, Homography.Of(unit), bigOpacity, blur);
                    var star = Matrix3x2.CreateRotation(Radians(reduce ? 0 : time * 40))
                        * Matrix3x2.CreateTranslation((float)(word.Size.Width / 2 - size * 0.16 + size * 0.1),
                                                      (float)(-word.Size.Height / 2 + size * 0.16 - size * 0.12)) * unit;
                    FillStar(dc, star, size * 0.32, tile, bigOpacity);
                }
                else if (kind == 1)
                {
                    var wipe = reduce ? 1 : StageShared.Glide((time - (run.Start - 0.04)) / (0.32 / speed));
                    var tilt = (StageShared.Chance(seed, id, 28) - 0.5) * 5;
                    var bar = line.Bar[id];
                    // The pale panel behind, a little off-square, arriving a beat after the bar.
                    var panelIn = StageShared.Glide((time - (run.Start + 0.05)) / 0.3);
                    var panel = Matrix3x2.CreateRotation(Radians(-tilt * 0.8))
                        * Matrix3x2.CreateTranslation((float)(size * 0.12), (float)(size * 0.1))
                        * Matrix3x2.CreateScale(reduce ? 1 : (float)(0.6 + 0.4 * panelIn), 1) * big;
                    SoftRounded(dc, Matrix3x2.CreateTranslation(0, Points(3)) * panel, bar.Width + size * 1.1, bar.Height + size * 0.68,
                                Points(4), Points(8), new Vector4(0, 0, 0, 1), 0.25 * panelIn * bigOpacity);
                    FillRounded(dc, panel, bar.Width + size * 1.1, bar.Height + size * 0.68, Points(4), white, 0.9 * panelIn * bigOpacity);
                    // The bar wipes across, then the word lands on it.
                    var turned = Matrix3x2.CreateRotation(Radians(tilt)) * big;
                    var wiped = bar.Width * wipe;
                    var barCentre = side > 0 ? -bar.Width / 2 + wiped / 2 : bar.Width / 2 - wiped / 2;
                    FillRounded(dc, Matrix3x2.CreateTranslation((float)barCentre, 0) * turned, wiped, bar.Height, 0, tile, bigOpacity);
                    var landed = StageShared.Glide((time - (run.Start + 0.1)) / 0.2) * bigOpacity;
                    var unit = Homography.Of(Matrix3x2.CreateRotation(Radians(tilt))
                        * Matrix3x2.CreateTranslation((float)((1 - wipe) * size * 0.5 * side), 0) * big);
                    DrawLabel(dc, line, word, run, 0, time, unit, landed, blur);
                    DrawReading(dc, line, word, run, 0, time, unit, landed, blur);
                    // A star above the word, which a reading under it lifts.
                    var lift = word.HasReading ? StageShared.ReadingSlot(size, point: Pt) / 2 : 0;
                    var star = Matrix3x2.CreateScale((float)wipe) * Matrix3x2.CreateRotation(Radians(reduce ? 0 : time * 50))
                        * Matrix3x2.CreateTranslation((float)(side * size * 0.1), (float)(-size * 0.62 - lift)) * big;
                    FillStar(dc, Matrix3x2.CreateTranslation(0, Points(1)) * star, size * 0.3, new Vector4(0, 0, 0, 1), 0.2 * bigOpacity);
                    FillStar(dc, star, size * 0.3, white, bigOpacity);
                }
                else
                {
                    var letters = line.Letters[id];
                    var fitted = Matrix3x2.CreateScale((float)line.LetterScale[id]) * big;
                    var readingRow = line.LetterReadings[id] is null ? 0 : line.LetterReadings[id]!.Size.Height + size * 0.1;
                    var lettersAt = Matrix3x2.CreateTranslation(0, (float)(-readingRow / 2)) * fitted;
                    for (var k = 0; k < letters.Length; k++)
                    {
                        var letter = letters[k];
                        var reveal = StageShared.Glide((time - (run.Start + k * 0.05)) / 0.25);
                        var local = Matrix3x2.CreateRotation(Radians(letter.Turn))
                            * Matrix3x2.CreateTranslation((float)letter.X, (float)(letter.Y + (reduce ? 0 : (1 - reveal) * size * 0.3))) * lettersAt;
                        DrawInk(dc, letter.Ink, Homography.Of(local), Vector2.Zero, reveal * bigOpacity, blur);
                    }
                    if (line.LetterReadings[id] is { } under)
                    {
                        var below = Matrix3x2.CreateTranslation(0, (float)(line.LetterReadingY[id] - readingRow / 2)) * fitted;
                        // Set on its own, the reading still fills in part by part as the run is sung.
                        var shares = line.ReadingOpacities(under, run, time);
                        DrawInk(dc, under.Labels[0], Homography.Of(below), Vector2.Zero, bigOpacity, blur, 1,
                                shares.IsEmpty ? default : under.ReadingSlices!.Boundaries, shares);
                    }
                }
            }
            if (morph.SmallOpacity > 0.002 && line.Small[id] is { } small)
            {
                var fade = Math.Max(0, 1 - (rank - 1.5) * 0.6);
                var o = morph.SmallOpacity * fade * whole;
                DrawLabel(dc, line, small, run, 0, time, Homography.Of(at), o, blur);
                DrawReading(dc, line, small, run, 0, time, Homography.Of(at), o, blur);
            }
        }
    }
}

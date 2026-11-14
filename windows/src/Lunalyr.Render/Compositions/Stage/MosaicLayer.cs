// Mosaic (the Mac's StageLayouts/MosaicLayout.swift, Build 88: mosaic, mosaicBeat, mosaicSetting, mosaicLetters,
// MosaicLetterRenderer; Lunalyr's own code, ported as it is). A collage: each beat is set a different way as it arrives
// -- huge, near the stage's width; white on a colour bar; or letter by letter, each letter rising onto one baseline --
// and then shrinks into a small, flush-left stack above, read top to bottom, the oldest words fading out at its top.
// Nothing else is drawn: no stars, no panels, and the bar is only ever the background of its own word. A quick run of
// words shares one beat (one treatment), each word appearing in its place as it is sung; a beat leaves for the stack just
// before the next one lands. Letters keep one size. The choreography is MosaicGeometry's.

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
    /// <summary>One letter of a beat set letter by letter: its ink, its centre along the beat, and which part of which run it is.</summary>
    private readonly record struct Letter(Ink Ink, double X, int Run, int Part, int Index);

    private sealed class Setting
    {
        public required MosaicBeat Beat { get; init; }
        /// <summary>The beat's box (its words side by side, and a single word's reading under it), at the treatment's size.</summary>
        public SizeD Size { get; set; }
        /// <summary>Each run's centre from the beat's centre (x), its words baked at the treatment's size.</summary>
        public double[] X { get; init; } = [];
        /// <summary>The beat's words over its height: shrunk to fit the stage's width (minimumScaleFactor).</summary>
        public double Fit { get; set; } = 1;
        public Letter[] Letters { get; set; } = [];
        public double LetterY { get; set; }
        public double Tilt { get; set; }
        public double Sway { get; set; }
    }

    private sealed class MosaicLine(StageSource source) : StageLine(source)
    {
        public Setting[] Settings { get; set; } = [];
        public MosaicBeat[] Beats { get; set; } = [];
        public bool RightToLeft { get; init; }

        public override void Dispose()
        {
            base.Dispose();
            foreach (var setting in Settings)
            foreach (var letter in setting.Letters) letter.Ink.Dispose();
        }
    }

    protected override StageLine Build(StageSource source)
    {
        var line = new MosaicLine(source) { RightToLeft = ScriptDirection.LineIsRightToLeft(source.Runs.Select(run => run.Text)) };
        var runs = source.Runs;
        var seed = line.Seed;
        var u = Unit;
        var tileText = TileText(source.InkTime);
        // A run with a reading under it makes a beat of its own.
        var alone = new HashSet<int>(Enumerable.Range(0, runs.Count).Where(i => source.Readings[i] is not null));
        var beats = MosaicGeometry.Beats(runs, alone);
        line.Beats = [.. beats];
        var words = new StageWord[runs.Count];
        var settings = new Setting[beats.Count];
        for (var g = 0; g < beats.Count; g++)
        {
            var beat = beats[g];
            var first = runs[beat.From];
            var sample = Sample(line, first);
            var size = u * MosaicGeometry.BigSize(beat.Kind);
            var width = Stage.Width * (beat.Kind == MosaicKind.Huge ? 0.9 : beat.Kind == MosaicKind.Bar ? 0.7 : 0.8);
            var bar = beat.Kind == MosaicKind.Bar;
            // The big colour, and the plain small word it becomes (laid exactly over it, then shrunk with it).
            Vector4 big = bar ? tileText : Colour(sample.Sung);
            Vector4 rest = Colour(sample.Rest, 0.8);
            var shadow = bar ? ShadowSpec.None : Shadow(0.28, 8, 3);
            var xs = new double[beat.Count];
            double total = 0, height = 0;
            for (var k = 0; k < beat.Count; k++)
            {
                var id = beat.From + k;
                var run = runs[id];
                var spec = new WordSpec(size, [big, rest]) { Shadow = shadow, Raster = 1.1f };
                if (k == 0)
                    spec = WithReading(spec, source.Readings[id], size,
                                       [bar ? Faded(tileText, LyricHighlight.ReadingOpacity) : Colour(sample.Reading)],
                                       shadow: !bar, maxWidth: width);
                words[id] = Bake(run, run.Text, spec);
                if (k > 0) total += Joiner(run, size);
                xs[k] = total + words[id].Size.Width / 2;
                total += words[id].Size.Width;
                height = Math.Max(height, words[id].Size.Height);
            }
            for (var k = 0; k < xs.Length; k++) xs[k] -= total / 2;
            if (line.RightToLeft) for (var k = 0; k < xs.Length; k++) xs[k] = -xs[k];
            var setting = new Setting
            {
                Beat = beat, Size = new SizeD(total, height), X = xs,
                Fit = total > width && total > 0 ? Math.Max(0.4, width / total) : 1,
                Tilt = (StageShared.Chance(seed, first.Id, 28) - 0.5) * 5,
                Sway = (StageShared.Chance(seed, g, 21) - 0.5) * 0.06,
            };
            if (beat.Kind == MosaicKind.Letters) BakeLetters(setting, line, runs, words, size, big);
            settings[g] = setting;
        }
        line.Words = words;
        line.Settings = settings;
        return line;
    }

    /// <summary>The width of the joiner before <paramref name="run"/> in a beat (a space, or nothing for an unspaced script).</summary>
    private double Joiner(StageRun run, double size)
    {
        if (MosaicGeometry.Joiner(run).Length == 0) return 0;
        using var spaced = Kit!.Layout("a a", Face.Heavy, (float)size, "en-us", false);
        using var tight = Kit.Layout("aa", Face.Heavy, (float)size, "en-us", false);
        return spaced.Metrics.WidthIncludingTrailingWhitespace - tight.Metrics.WidthIncludingTrailingWhitespace;
    }

    /// <summary>Letter by letter: each letter baked on its own, at its place along its word (one size, one baseline).</summary>
    private void BakeLetters(Setting setting, MosaicLine line, IReadOnlyList<StageRun> runs, StageWord[] words, double size, Vector4 colour)
    {
        var kit = Kit!;
        var letters = new List<Letter>();
        for (var k = 0; k < setting.Beat.Count; k++)
        {
            var id = setting.Beat.From + k;
            var run = runs[id];
            var word = words[id];
            var rtl = ScriptDirection.IsRightToLeft(run.Text);
            using var layout = kit.Layout(run.Text, Face.Heavy, (float)size, TextKit.LocaleFor(run.Text), rtl);
            var layoutWidth = layout.Metrics.Width;
            var offset = 0;
            for (var part = 0; part < run.Parts.Count; part++)
            {
                var text = run.Parts[part].Text;
                var enumerator = StringInfo.GetTextElementEnumerator(text);
                var index = 0;
                while (enumerator.MoveNext())
                {
                    var element = enumerator.GetTextElement();
                    var at = offset + enumerator.ElementIndex;
                    layout.HitTestTextPosition((uint)at, false, out var x, out _, out _);
                    layout.HitTestTextPosition((uint)(at + element.Length), false, out var x2, out _, out _);
                    var centre = (x + x2) / 2 - layout.Metrics.Left - layoutWidth / 2;
                    if (string.IsNullOrWhiteSpace(element)) { index++; continue; }
                    using var glyph = kit.Layout(element, Face.Heavy, (float)size, TextKit.LocaleFor(element), rtl);
                    var ink = kit.Bake(glyph, colour, 1.1f, Shadow(0.28, 8, 3));
                    // Along the word's own label, which the beat places by the word's centre.
                    letters.Add(new Letter(ink, setting.X[k] + word.LabelOffset.X + centre * word.Label.Width / Math.Max(1, layoutWidth),
                                           id, part, index));
                    index++;
                }
                offset += text.Length;
            }
        }
        setting.Letters = [.. letters];
        // The letters sit where the words' labels do: under them, a single word's reading.
        setting.LetterY = setting.Beat.Count > 0 ? words[setting.Beat.From].LabelOffset.Y : 0;
    }

    protected override void DrawLine(ID2D1DeviceContext dc, StageLine stageLine, double time, double exitStart, in Matrix4x4 stage,
                                     double alpha, float blur, int shown)
    {
        var line = (MosaicLine)stageLine;
        var runs = line.Runs;
        var beats = line.Beats;
        if (beats.Length == 0) return;
        var whole = (1 - StageMotion.Leave(time, exitStart)) * alpha;
        if (!(whole > 0.002)) return;
        var reduce = ReduceMotion;
        // Mosaic reads the words that have arrived only under Reduce Motion, for the shared dissolve's steps.
        var arrived = reduce ? shown : runs.Count;
        var speed = line.Speed;
        var u = Unit;
        var moves = line.A.AsSpan(0, beats.Length);
        var ranks = line.B.AsSpan(0, beats.Length);
        MosaicGeometry.Moves(beats, runs, time, speed, exitStart, reduce, arrived, moves);
        MosaicGeometry.Ranks(moves, ranks);
        // The stack's rows make room for readings when the line has any.
        var step = MosaicGeometry.StackStep(u, line.HasReadings, Pt);
        var flat = Homography.Flat(stage);
        var tile = Tile(line.InkTime);
        var bar = Desaturated(tile, MosaicGeometry.AccentSaturation);
        var rtl = line.RightToLeft;
        for (var g = 0; g < beats.Length; g++)
        {
            var beat = beats[g];
            var setting = line.Settings[g];
            var first = runs[beat.From];
            // A beat is drawn from just before its first word until it has faded off the top of the stack.
            var appear0 = MosaicGeometry.Appear(first, time, speed, reduce, arrived);
            var fade = MosaicGeometry.StackFade(ranks[g]);
            if (!(appear0 > 0.001 && fade > 0.001)) continue;
            var size = u * MosaicGeometry.BigSize(beat.Kind);
            var fit = setting.Fit;
            var measured = new SizeD(setting.Size.Width * fit, setting.Size.Height * fit);
            var overhang = beat.Kind == MosaicKind.Bar ? u * MosaicGeometry.BigSize(MosaicKind.Bar) * MosaicGeometry.BarPaddingY * 2 : 0;
            var centre = MosaicGeometry.Centre(measured, moves[g], ranks[g], setting.Sway, step, overhang, rtl, Bounds);
            var morph = StageMotion.MosaicMorph(moves[g], 0.6 / MosaicGeometry.BigSize(beat.Kind));
            // The whole beat shrinks towards its own edge as it leaves for the stack.
            var edge = (float)(rtl ? measured.Width / 2 : -measured.Width / 2);
            var beatAt = Matrix3x2.CreateScale((float)fit)
                * Matrix3x2.CreateScale((float)morph.BigScale, new Vector2(edge, 0))
                * Matrix3x2.CreateTranslation((float)centre.X, (float)centre.Y) * flat;
            var opacity = fade * whole;
            var bigOpacity = morph.BigOpacity * opacity;
            if (bigOpacity > 0.002)
            {
                switch (beat.Kind)
                {
                    case MosaicKind.Huge:
                    {
                        // Punches in from a little smaller, never past its own room on the stage.
                        var punch = Matrix3x2.CreateScale(reduce ? 1 : (float)(0.8 + 0.2 * Math.Min(1.05, appear0))) * beatAt;
                        DrawWords(dc, line, setting, time, punch, 0, bigOpacity, blur, speed, arrived, syllables: true);
                        break;
                    }
                    case MosaicKind.Bar:
                    {
                        // The bar and its word are one print: they come in together, and the bar never shows without the
                        // whole word on it.
                        var shown1 = Math.Min(1, appear0 * 1.6) * bigOpacity;
                        var leading = (float)(rtl ? setting.Size.Width / 2 : -setting.Size.Width / 2);
                        var print = Matrix3x2.CreateRotation(Radians(setting.Tilt))
                            * Matrix3x2.CreateScale(reduce ? 1 : (float)(0.7 + 0.3 * appear0), 1, new Vector2(leading, 0)) * beatAt;
                        // The bar is the background of the whole setting: its word, and the word's reading printed on it.
                        var word = line.Words[beat.From];
                        var barWidth = word.Size.Width + 2 * size * MosaicGeometry.BarPaddingX;
                        var barHeight = word.Size.Height + 2 * size * MosaicGeometry.BarPaddingY;
                        var barAt = print;
                        SoftRounded(dc, Matrix3x2.CreateTranslation(0, Points(3)) * barAt, barWidth, barHeight, 0, Points(8),
                                    new Vector4(0, 0, 0, 1), 0.28 * shown1);
                        FillRounded(dc, barAt, barWidth, barHeight, 0, bar, shown1);
                        DrawWords(dc, line, setting, time, print, 0, shown1, blur, speed, arrived, syllables: false, whole1: true);
                        break;
                    }
                    default:
                        DrawLetters(dc, line, setting, time, beatAt, size, bigOpacity, blur, arrived);
                        break;
                }
            }
            // The plain small word it becomes, laid exactly over it.
            if (morph.SmallOpacity > 0.002)
                DrawWords(dc, line, setting, time, beatAt, 1, morph.SmallOpacity * opacity, blur, speed, arrived,
                          syllables: beat.Kind != MosaicKind.Bar, shadow: 0);
        }
    }

    /// <summary>
    /// A beat's words side by side, each in its place from the start (unsung ones clear), so nothing shifts as they
    /// arrive; without <paramref name="syllables"/>, a word is printed whole (the bar's).
    /// </summary>
    private void DrawWords(ID2D1DeviceContext dc, MosaicLine line, Setting setting, double time, in Matrix3x2 at, int colour,
                           double opacity, float blur, double speed, int arrived, bool syllables, bool whole1 = false, float shadow = 1)
    {
        var reduce = ReduceMotion;
        for (var k = 0; k < setting.Beat.Count; k++)
        {
            var id = setting.Beat.From + k;
            var run = line.Runs[id];
            var word = line.Words[id];
            var appear = MosaicGeometry.Appear(run, time, speed, reduce, arrived);
            var shown = whole1 ? 1 : reduce ? (appear > 0 ? 1 : 0) : Math.Min(1, Math.Max(0, appear * 1.6));
            if (!(shown * opacity > 0.002)) continue;
            var unit = Homography.Of(Matrix3x2.CreateTranslation((float)setting.X[k], 0) * at);
            if (syllables) DrawLabel(dc, line, word, run, colour, time, unit, shown * opacity, blur, shadow);
            else DrawInk(dc, word.Labels[colour], unit, word.LabelOffset, shown * opacity, blur, shadow);
            DrawReading(dc, line, word, run, 0, time, unit, shown * opacity, blur, shadow);
        }
    }

    /// <summary>Letter by letter: each letter fades in rising onto the one baseline, in time with its syllable.</summary>
    private void DrawLetters(ID2D1DeviceContext dc, MosaicLine line, Setting setting, double time, in Matrix3x2 at, double size,
                             double opacity, float blur, int arrived)
    {
        var reduce = ReduceMotion;
        var drop = reduce ? 0 : size * 0.22;
        var runs = line.Runs;
        foreach (var letter in setting.Letters)
        {
            var run = runs[letter.Run];
            double? next = letter.Run + 1 < runs.Count ? runs[letter.Run + 1].Start : null;
            var reveal = reduce ? (run.Id < arrived ? 1 : 0) : MosaicGeometry.LetterReveal(letter.Part, letter.Index, run, next, time);
            if (!(reveal > 0.001)) continue;
            var lowered = 1 - Math.Min(1, Math.Max(0, reveal));
            var place = Matrix3x2.CreateTranslation((float)letter.X, (float)(setting.LetterY + lowered * drop)) * at;
            DrawInk(dc, letter.Ink, Homography.Of(place), Vector2.Zero, Math.Min(1, reveal) * opacity, blur);
        }
        // A single word's reading, under the letters.
        var first = setting.Beat.From;
        var word = line.Words[first];
        if (word.HasReading)
            DrawReading(dc, line, word, runs[first], 0, time, Homography.Of(Matrix3x2.CreateTranslation((float)setting.X[0], 0) * at),
                        opacity * (runs[first].Id < arrived || !reduce ? 1 : 0), blur);
    }

    /// <summary><paramref name="colour"/> with its saturation scaled (SwiftUI's saturation(), on luminance).</summary>
    private static Vector4 Desaturated(Vector4 colour, double amount)
    {
        var luma = 0.2126f * colour.X + 0.7152f * colour.Y + 0.0722f * colour.Z;
        var k = (float)amount;
        return new Vector4(luma + (colour.X - luma) * k, luma + (colour.Y - luma) * k, luma + (colour.Z - luma) * k, colour.W);
    }
}

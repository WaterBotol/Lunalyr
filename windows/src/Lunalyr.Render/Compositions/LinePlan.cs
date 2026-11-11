using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Compositions;

/// <summary>One run's baked ink: its label, the reading under it, and where it sits in the line.</summary>
internal sealed class RunInk : IDisposable
{
    public required Ink Label { get; init; }
    public Ink? Reading { get; init; }
    /// <summary>Label and reading centres relative to the unit's centre, layout pixels.</summary>
    public Vector2 LabelOffset { get; init; }
    public Vector2 ReadingOffset { get; init; }
    public SizeD Unit { get; init; }
    public SizeD Slot { get; init; }
    /// <summary>The glow of the run while it is held: its ink's glow colour.</summary>
    public Vector4 Glow { get; init; } = Vector4.One;
    /// <summary>The slot's centre in the line's block (top-left origin), layout pixels.</summary>
    public PointD Centre { get; set; }

    public void Dispose()
    {
        Label.Dispose();
        Reading?.Dispose();
    }
}

/// <summary>A backing-vocal group of a line, set as a small line of its own.</summary>
internal sealed class BackingPlan(BackingGroup group, LinePlan plan) : IDisposable
{
    public BackingGroup Group { get; } = group;
    public LinePlan Plan { get; } = plan;
    public void Dispose() => Plan.Dispose();
}

/// <summary>
/// One line set for drawing (Editorial's typography, or a line-timed phrase): its runs' baked ink, the row layout, the
/// line's reading, its fit to the stage, and scratch space for the per-frame states. Built when the line is first
/// needed and kept until the lyrics, size, scale or style change.
/// </summary>
internal sealed class LinePlan : IDisposable
{
    public required EditorialLine Typography { get; init; }
    public required RunInk[] Runs { get; init; }
    /// <summary>The words the plan was set from (the lead's, with backing vocals lifted out).</summary>
    public required IReadOnlyList<LyricWord> Words { get; init; }
    public required SizeD Block { get; init; }
    public Ink? LineReading { get; init; }
    public SizeD Content { get; init; }
    /// <summary>The line reading's centre in content coordinates.</summary>
    public PointD ReadingCentre { get; init; }
    public double Fit { get; init; }
    public BackingPlan[] Backing { get; init; } = [];

    // Per-frame scratch, sized once.
    public EditorialTokenFrame[] TokenFrames { get; init; } = [];
    public EditorialRunFrame[] RunFrames { get; init; } = [];
    public double[] TokenOpacities { get; init; } = [];
    public int[] Order { get; init; } = [];
    public long LastUsed { get; set; }

    public PointD BlockOrigin => new((Content.Width - Block.Width) / 2, 0);

    public void Dispose()
    {
        foreach (var run in Runs) run.Dispose();
        LineReading?.Dispose();
        foreach (var backing in Backing) backing.Dispose();
    }
}

/// <summary>Everything a plan is set with.</summary>
internal readonly record struct PlanSpec(
    double BaseSize, double MinimumSize, int LineIndex, bool WordTimed, bool Editorial, bool Heavy, double MaxWidth,
    double Spacing, double LineSpacing, LineFit Fit, double RasterBoost, ShadowSpec Shadow, Vector4 Colour,
    Vector4 ReadingColour, double LineReadingSize, double LineReadingWidth, ShadowSpec LineReadingShadow,
    LyricInk? Ink = null);

internal static class LinePlans
{
    /// <summary>Sets a line: runs and metrics, laid-out labels and readings, rows, the line reading, the fit; then bakes.</summary>
    public static LinePlan Build(TextKit kit, IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue, PlanSpec spec,
                                 IReadOnlyList<string?>? runReadings, string? lineReading)
    {
        var typography = EditorialSetting.Layout(words, glue, spec.BaseSize, spec.MinimumSize, spec.LineIndex, spec.WordTimed,
                                                 spec.Editorial, runReadings);
        var locale = TextKit.LocaleFor(string.Concat(words.Select(word => word.Text)));
        var count = typography.Runs.Count;
        var labels = new IDWriteTextLayout[count];
        var readings = new IDWriteTextLayout?[count];
        var units = new SizeD[count];
        var slots = new SizeD[count];
        try
        {
            for (var r = 0; r < count; r++)
            {
                var run = typography.Runs[r];
                var metrics = run.Metrics;
                var face = metrics.AlternateFace ? Face.Contrast : metrics.IsHero || spec.Heavy ? Face.Heavy : Face.Regular;
                labels[r] = kit.Layout(run.Label, face, (float)metrics.Size, locale, run.RightToLeft);
                var label = labels[r].Metrics;
                double width = label.Width, height = label.Height;
                if (!string.IsNullOrWhiteSpace(run.Reading))
                {
                    readings[r] = kit.Layout(run.Reading!, metrics.AlternateFace ? Face.Contrast : Face.Quiet,
                                             (float)(metrics.Size * 0.56), TextKit.LocaleFor(run.Reading!), false);
                    var reading = readings[r]!.Metrics;
                    width = Math.Max(width, reading.Width);
                    height += metrics.Size * 0.04 + reading.Height;
                }
                units[r] = new SizeD(width, height);
                slots[r] = EditorialFlow.Slot(units[r], metrics.Rotation, run.Inset, metrics.WaveOffset);
            }
            var centres = new PointD[count];
            var alignment = spec.WordTimed ? typography.Setting.Rows : RowAlignment.Center;
            var block = EditorialFlow.Place(slots, spec.MaxWidth, spec.Spacing, spec.LineSpacing, alignment,
                                            typography.RightToLeft, centres);

            IDWriteTextLayout? lineLayout = null;
            var content = block;
            var readingCentre = new PointD(block.Width / 2, block.Height);
            if (!string.IsNullOrWhiteSpace(lineReading))
            {
                lineLayout = kit.Layout(lineReading!, Face.Quiet, (float)spec.LineReadingSize, TextKit.LocaleFor(lineReading!),
                                        false, (float)spec.LineReadingWidth);
                var m = lineLayout.Metrics;
                var gap = spec.BaseSize * 0.2;
                content = new SizeD(Math.Max(block.Width, m.Width), block.Height + gap + m.Height);
                readingCentre = new PointD(content.Width / 2, block.Height + gap + m.Height / 2);
            }
            var fit = spec.Fit.ScaleFor(content);
            var raster = (float)Math.Clamp(fit * spec.RasterBoost, 0.2, 2.5);

            var runs = new RunInk[count];
            for (var r = 0; r < count; r++)
            {
                var run = typography.Runs[r];
                // Each run takes its own colour from the ink, by its place along the line (the Mac's ink.sample).
                var sample = spec.Ink?.Sample(run.Order, run.Seed);
                var colour = sample is { } rest ? LyricInks.Vector(rest.Rest) : spec.Colour;
                var readingColour = sample is { } read ? LyricInks.Vector(read.Reading) : spec.ReadingColour;
                var label = kit.Bake(labels[r], colour, raster, spec.Shadow);
                Ink? reading = readings[r] is { } layout ? kit.Bake(layout, readingColour, raster, spec.Shadow) : null;
                var unit = units[r];
                var labelHeight = labels[r].Metrics.Height;
                runs[r] = new RunInk
                {
                    Label = label,
                    Reading = reading,
                    LabelOffset = new Vector2(0, (float)(-unit.Height / 2 + labelHeight / 2)),
                    ReadingOffset = reading is null ? default : new Vector2(0, (float)(unit.Height / 2 - reading.Box.Y / 2)),
                    Unit = unit,
                    Slot = slots[r],
                    Centre = centres[r],
                    Glow = sample is { } glow ? LyricInks.Vector(glow.Glow) : colour,
                };
            }
            var lineInk = lineLayout is null ? null : kit.Bake(lineLayout, spec.ReadingColour, raster, spec.LineReadingShadow);
            lineLayout?.Dispose();
            var tokenCount = typography.Tokens.Count;
            return new LinePlan
            {
                Typography = typography,
                Runs = runs,
                Words = words,
                Block = block,
                LineReading = lineInk,
                Content = content,
                ReadingCentre = readingCentre,
                Fit = fit,
                TokenFrames = new EditorialTokenFrame[tokenCount],
                RunFrames = new EditorialRunFrame[count],
                TokenOpacities = new double[tokenCount],
                Order = new int[count],
            };
        }
        finally
        {
            foreach (var layout in labels) layout?.Dispose();
            foreach (var layout in readings) layout?.Dispose();
        }
    }
}

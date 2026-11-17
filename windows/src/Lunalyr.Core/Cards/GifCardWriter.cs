// Ported from the Mac's GIFWriter (GIFEncoder.swift, 7767ef6 and 52a4204; Lunalyr's own code): a size-budgeted GIF from
// rendered frames. It encodes frames as they come and, when a byte budget is set, keeps each frame's changes (compressed)
// so a GIF over budget can be made again with fewer frames per second first, then smaller, without drawing anything again.
// The Mac's LZ4 (Compression) becomes Deflate at its fastest, and vImage's high-quality scaling an area-averaged reduction.

using System.IO.Compression;

namespace Lunalyr.Core.Cards;

public sealed class GifCardWriter(int? byteBudget)
{
    public readonly record struct Summary(int Bytes, int Frames, double Scale, int Gap, int Passes, int Palettes);

    private sealed record Change(GifEncoder.Rect Rect, int Delay, byte[] Packed, int Count);

    private readonly object _gate = new();
    private bool _cancelled;
    private GifEncoder? _encoder;
    private int _firstFrameBytes;
    private readonly List<Change> _changes = [];
    private uint[] _pixels = [];

    public int? ByteBudget { get; } = byteBudget;

    /// <summary>What was written, once <see cref="Finish"/> has.</summary>
    public Summary? Written { get; private set; }

    public void Cancel()
    {
        lock (_gate) _cancelled = true;
    }

    public bool IsCancelled
    {
        get
        {
            lock (_gate) return _cancelled;
        }
    }

    /// <summary>Bytes encoded so far at full rate and size.</summary>
    public int ByteCount => _encoder?.ByteCount ?? 0;

    /// <summary>
    /// Encodes a <paramref name="width"/> × <paramref name="height"/> frame (opaque BGRA, rows top first, as the card's
    /// renderer reads it back), shown for <paramref name="delay"/> seconds. False once cancelled.
    /// </summary>
    public bool Add(ReadOnlySpan<byte> bgra, int width, int height, double delay)
    {
        if (IsCancelled) return false;
        if (_encoder is null)
        {
            _encoder = new GifEncoder(width, height) { IsCancelled = () => IsCancelled };
            _pixels = new uint[width * height];
        }
        var encoder = _encoder;
        Pack(bgra, _pixels, encoder.Width * encoder.Height);
        return AddPacked(_pixels, delay);
    }

    /// <summary>As <see cref="Add"/>, from packed sRGB pixels (r | g &lt;&lt; 8 | b &lt;&lt; 16).</summary>
    public bool AddPacked(ReadOnlySpan<uint> pixels, double delay, int width = 0, int height = 0)
    {
        if (IsCancelled) return false;
        _encoder ??= new GifEncoder(width, height) { IsCancelled = () => IsCancelled };
        var encoder = _encoder;
        var centiseconds = GifEncoder.Centiseconds(delay);
        if (encoder.Add(pixels, centiseconds) is not { } added) return false;
        if (encoder.FrameCount == 1 && _firstFrameBytes == 0) _firstFrameBytes = encoder.ByteCount;
        if (ByteBudget is not null) _changes.Add(new Change(added.Rect, centiseconds, Compress(added.Delta), added.Delta.Length));
        return !IsCancelled;
    }

    /// <summary>
    /// The GIF, made again smaller if it is over budget. <paramref name="progress"/> runs 0 to 1 over any extra passes. Null
    /// when cancelled or empty.
    /// </summary>
    public byte[]? Finish(Action<double>? progress = null)
    {
        if (_encoder is not { FrameCount: > 0 } encoder || IsCancelled) return null;
        encoder.ReleaseTables();
        if (ByteBudget is not { } budget || encoder.ByteCount <= budget)
        {
            Written = new Summary(encoder.ByteCount, encoder.FrameCount, 1, 0, 1, encoder.PalettesBuilt);
            progress?.Invoke(1);
            return encoder.Data();
        }
        var delays = _changes.Where(c => !c.Rect.IsEmpty).Select(c => c.Delay).Order().ToList();
        var cadence = Math.Max(2, delays.Count == 0 ? 4 : delays[delays.Count / 2]);
        double full = encoder.ByteCount, first = _firstFrameBytes;
        // Fewer frames per second first (20, 16.7, 12.5, then 10), then smaller, and only at the end below 10 fps. Gaps in
        // centiseconds.
        (int Gap, double Scale)[] ladder =
            [(5, 1), (6, 1), (8, 1), (10, 1), (10, 0.85), (10, 0.72), (10, 0.6), (10, 0.5), (12, 0.42), (14, 0.35)];
        var plans = new List<(int Gap, double Scale)>();
        foreach (var step in ladder)
        {
            var gap = Math.Max(step.Gap, cadence);
            if (gap > cadence || step.Scale < 1) plans.Add((gap, step.Scale));
        }
        double Estimate((int Gap, double Scale) plan) =>
            (first + (full - first) / Math.Pow((double)plan.Gap / cadence, 0.85)) * plan.Scale * plan.Scale;
        var candidates = plans.Where(p => Estimate(p) <= budget * 0.95).ToList();
        // At most four passes, the last always the smallest plan.
        var smallest = plans[^1];
        var firsts = candidates.Take(3).ToList();
        var tries = firsts.Contains(smallest) ? firsts : [.. firsts, smallest];
        var passes = 1;
        GifEncoder? best = null;
        var bestPlan = tries[0];
        for (var number = 0; number < tries.Count; number++)
        {
            var plan = tries[number];
            var index = number;
            if (Replay(plan.Gap, plan.Scale, fraction => progress?.Invoke((index + fraction) / tries.Count)) is not { } made) return null;
            passes++;
            if (best is null || made.ByteCount < best.ByteCount)
            {
                best = made;
                bestPlan = plan;
            }
            if (made.ByteCount <= budget) break;
        }
        if (best is null || IsCancelled) return null;
        Written = new Summary(best.ByteCount, best.FrameCount, bestPlan.Scale, bestPlan.Gap, passes, best.PalettesBuilt);
        progress?.Invoke(1);
        return best.Data();
    }

    /// <summary>The kept changes played back onto a canvas and encoded again, a frame every <paramref name="gap"/> centiseconds, scaled.</summary>
    private GifEncoder? Replay(double gap, double scale, Action<double> progress)
    {
        if (_encoder is not { } source) return null;
        int width = source.Width, height = source.Height;
        int outWidth = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero));
        int outHeight = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero));
        var made = new GifEncoder(outWidth, outHeight) { IsCancelled = () => IsCancelled };
        var canvas = new uint[width * height];
        var scaled = new uint[outWidth * outHeight];
        // Frames go out on an even schedule gap apart, each showing the latest frame by then, so the motion stays even.
        var total = _changes.Sum(c => c.Delay);
        int Due(int step) => Math.Min(total, (int)Math.Round(step * gap, MidpointRounding.AwayFromZero));
        int time = 0, at = 0;
        for (var index = 0; index < _changes.Count; index++)
        {
            if (IsCancelled) return null;
            var change = _changes[index];
            if (!change.Rect.IsEmpty)
            {
                var delta = Decompress(change.Packed, change.Count);
                for (var row = 0; row < change.Rect.Height; row++)
                {
                    int line = (change.Rect.Y + row) * width + change.Rect.X, from = row * change.Rect.Width;
                    for (var column = 0; column < change.Rect.Width; column++)
                    {
                        var value = delta[from + column];
                        if (value != 0) canvas[line + column] = value & 0xFF_FFFF;
                    }
                }
            }
            time += change.Delay;
            // Every scheduled moment before the next change shows this canvas.
            while (Due(at) < time)
            {
                var delay = Due(at + 1) - Due(at);
                at++;
                if (delay < 2 && made.FrameCount > 0)
                {
                    made.ExtendLastFrame(delay);
                    continue;
                }
                uint[] frame;
                if (scale < 1)
                {
                    Resize(canvas, width, height, scaled, outWidth, outHeight);
                    frame = scaled;
                }
                else
                {
                    frame = canvas;
                }
                if (made.Add(frame, delay) is null) return null;
            }
            progress((double)(index + 1) / _changes.Count);
        }
        made.ReleaseTables();
        return made;
    }

    // ---- Pixels ----

    /// <summary>Opaque BGRA to packed sRGB (r | g &lt;&lt; 8 | b &lt;&lt; 16).</summary>
    public static void Pack(ReadOnlySpan<byte> bgra, uint[] pixels, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var o = i * 4;
            pixels[i] = bgra[o + 2] | (uint)bgra[o + 1] << 8 | (uint)bgra[o] << 16;
        }
    }

    /// <summary>An area-averaged reduction of packed pixels (each target pixel the mean of the source pixels it covers).</summary>
    private static void Resize(uint[] source, int width, int height, uint[] target, int outWidth, int outHeight)
    {
        double sx = (double)width / outWidth, sy = (double)height / outHeight;
        Parallel.For(0, outHeight, y =>
        {
            var y0 = (int)Math.Floor(y * sy);
            var y1 = Math.Max(y0 + 1, Math.Min(height, (int)Math.Ceiling((y + 1) * sy)));
            for (var x = 0; x < outWidth; x++)
            {
                var x0 = (int)Math.Floor(x * sx);
                var x1 = Math.Max(x0 + 1, Math.Min(width, (int)Math.Ceiling((x + 1) * sx)));
                long r = 0, g = 0, b = 0;
                var count = 0;
                for (var yy = y0; yy < y1; yy++)
                {
                    var row = yy * width;
                    for (var xx = x0; xx < x1; xx++)
                    {
                        var value = source[row + xx];
                        r += value & 0xFF;
                        g += (value >> 8) & 0xFF;
                        b += (value >> 16) & 0xFF;
                        count++;
                    }
                }
                target[y * outWidth + x] = (uint)((r + count / 2) / count) | (uint)((g + count / 2) / count) << 8
                    | (uint)((b + count / 2) / count) << 16;
            }
        });
    }

    private static byte[] Compress(uint[] delta)
    {
        if (delta.Length == 0) return [];
        var raw = System.Runtime.InteropServices.MemoryMarshal.AsBytes(delta.AsSpan());
        using var memory = new MemoryStream(raw.Length / 4 + 64);
        using (var deflate = new DeflateStream(memory, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(raw);
        return memory.ToArray();
    }

    private static uint[] Decompress(byte[] packed, int count)
    {
        var delta = new uint[count];
        if (count == 0) return delta;
        using var deflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        deflate.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(delta.AsSpan()));
        return delta;
    }
}

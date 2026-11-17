// Ported from the Mac's GIFEncoder.swift (7767ef6, 52a4204; Lunalyr's own code): a GIF89a encoder made for lyric cards, a
// still, smooth backdrop with moving lyrics over it. Pure C#, so it is tested without the GPU or WIC.
//
// - Each frame gets its own palette of up to 255 colours (median cut in OKLab, then a k-means pass), plus one transparent
//   index. Most of it (the base) is made from a whole frame and kept while the backdrop stays, so whatever a word moves
//   off is drawn again exactly as it was; the rest holds the colours the base lacks, kept while it still fits.
// - Dithering is ordered (an 8 × 8 Bayer matrix fixed to pixel position), so a pixel whose colour does not change never
//   changes, and it mixes a pixel's nearest palette colour only with the partner whose mix comes closest, within reach:
//   gradients come out smooth while text edges stay crisp and flat fills get no stray dots.
// - Every frame after the first stores only the rectangle that changed, untouched pixels transparent over the previous
//   frame ("do not dispose"). A frame identical to the last one only lengthens it.
// - Delays are whole centiseconds, never below 2 (browsers turn 0 and 1 into 10).
//
// Pixels are packed sRGB, r | g << 8 | b << 16 (the top byte ignored), as on the Mac. One thread at a time feeds it; each
// frame's rows are worked on in parallel.

namespace Lunalyr.Core.Cards;

public sealed class GifEncoder
{
    public readonly record struct Rect(int X, int Y, int Width, int Height)
    {
        public bool IsEmpty => Width <= 0 || Height <= 0;
        public static Rect Empty => default;
    }

    /// <summary>One encoded frame, kept apart until <see cref="Data"/> so a later identical frame can lengthen its delay.</summary>
    private sealed class Frame(Rect rect, int delay, int? transparent, uint[] palette, int tableBits, byte[] lzw)
    {
        public Rect Rect { get; } = rect;
        public int Delay { get; set; } = delay;
        public int? Transparent { get; } = transparent;
        public uint[] Palette { get; } = palette;
        public int TableBits { get; } = tableBits;
        public byte[] Lzw { get; } = lzw;
        public int ByteCount => 8 + 10 + 3 * (1 << TableBits) + Lzw.Length;
    }

    private readonly List<Frame> _frames = [];
    /// <summary>The source value last stored for each pixel, to find what changed.</summary>
    private readonly uint[] _reference;
    /// <summary>The colour a decoder shows at each pixel.</summary>
    private readonly uint[] _shown;
    private uint[] _palette = [];
    private uint[] _base = [];
    private uint[] _extra = [];
    private readonly GifPaletteLookup _lookup = new();
    private readonly GifPaletteLookup _baseLookup = new();
    /// <summary>Counts per 24-bit colour while a palette is made; kept zeroed.</summary>
    private ushort[]? _tally;

    public GifEncoder(int width, int height, int loopCount = 0)
    {
        if (width <= 0 || height <= 0 || width > 65535 || height > 65535) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        LoopCount = loopCount;
        _reference = new uint[width * height];
        _shown = new uint[width * height];
    }

    public int Width { get; }
    public int Height { get; }
    public int LoopCount { get; }

    /// <summary>
    /// Changes of at most this much per channel from the value last stored for a pixel are left out (0 keeps every change).
    /// Measured against the stored value, so slow drifts are stored in small steps, never lost.
    /// </summary>
    public int Tolerance { get; set; } = 1;

    /// <summary>How far apart (sRGB levels, Euclidean) two palette colours may be and still be dithered together.</summary>
    public int DitherSpread { get; set; } = 56;

    /// <summary>Checked between rows; true stops the frame being added.</summary>
    public Func<bool> IsCancelled { get; set; } = static () => false;

    public int ByteCount { get; private set; } = 6 + 7 + 19 + 1;
    public int PalettesBuilt { get; private set; }
    public int FrameCount => _frames.Count;
    public IReadOnlyList<int> Delays => [.. _frames.Select(f => f.Delay)];
    public IReadOnlyList<Rect> Rects => [.. _frames.Select(f => f.Rect)];
    public IReadOnlyList<int> PaletteSizes => [.. _frames.Select(f => f.Palette.Length)];

    /// <summary>A delay in seconds as GIF centiseconds: rounded, at least 2.</summary>
    public static int Centiseconds(double seconds) =>
        !double.IsFinite(seconds) ? 2 : Math.Min(65535, Math.Max(2, (int)Math.Round(seconds * 100, MidpointRounding.AwayFromZero)));

    /// <summary>Lengthens the last frame, as if the next one were identical to it.</summary>
    public void ExtendLastFrame(int centiseconds)
    {
        if (_frames.Count == 0) return;
        var last = _frames[^1];
        last.Delay = Math.Min(65535, last.Delay + Math.Max(0, centiseconds));
    }

    /// <summary>
    /// Adds a frame of <see cref="Width"/> × <see cref="Height"/> packed pixels shown for <paramref name="delay"/>
    /// centiseconds. Returns the changed rectangle with each changed pixel's source (alpha byte set) and the rest 0, or null
    /// when cancelled.
    /// </summary>
    public (Rect Rect, uint[] Delta)? Add(ReadOnlySpan<uint> pixels, int delay)
    {
        if (pixels.Length < Width * Height) throw new ArgumentException("Too few pixels for the frame.", nameof(pixels));
        delay = Math.Max(2, Math.Min(65535, delay));
        var first = _frames.Count == 0;
        if (Difference(pixels, first) is not { } difference) return null;
        var (rect, delta) = difference;
        if (rect.IsEmpty)
        {
            ExtendLastFrame(delay);
            return (rect, []);
        }
        ChoosePalette(delta, first);
        if (Index(delta, rect, first) is not { } indices) return null;
        // The palette's colours plus the transparent index.
        var tableBits = 1;
        while ((1 << tableBits) < _palette.Length + 1) tableBits++;
        var lzw = GifLzw.Encode(indices, Math.Max(2, tableBits));
        if (IsCancelled()) return null;
        var frame = new Frame(rect, delay, first ? null : _palette.Length, _palette, tableBits, lzw);
        _frames.Add(frame);
        ByteCount += frame.ByteCount;
        return (rect, delta);
    }

    /// <summary>The whole file.</summary>
    public byte[] Data()
    {
        var output = new List<byte>(ByteCount);
        void Le16(int value)
        {
            output.Add((byte)(value & 0xFF));
            output.Add((byte)((value >> 8) & 0xFF));
        }
        output.AddRange("GIF89a"u8.ToArray());
        Le16(Width);
        Le16(Height);
        // No global colour table; 8 bits of colour resolution.
        output.AddRange([0x70, 0, 0]);
        // NETSCAPE2.0 looping.
        output.AddRange([0x21, 0xFF, 0x0B]);
        output.AddRange("NETSCAPE2.0"u8.ToArray());
        output.AddRange([0x03, 0x01]);
        Le16(LoopCount);
        output.Add(0);
        foreach (var frame in _frames)
        {
            // Graphic control: disposal 1 (do not dispose), the transparency flag.
            output.AddRange([0x21, 0xF9, 0x04, (byte)((1 << 2) | (frame.Transparent is null ? 0 : 1))]);
            Le16(frame.Delay);
            output.Add((byte)(frame.Transparent ?? 0));
            output.Add(0);
            output.Add(0x2C);
            Le16(frame.Rect.X);
            Le16(frame.Rect.Y);
            Le16(frame.Rect.Width);
            Le16(frame.Rect.Height);
            output.Add((byte)(0x80 | (frame.TableBits - 1)));
            for (var index = 0; index < (1 << frame.TableBits); index++)
            {
                var colour = index < frame.Palette.Length ? frame.Palette[index] : 0;
                output.Add((byte)(colour & 0xFF));
                output.Add((byte)((colour >> 8) & 0xFF));
                output.Add((byte)((colour >> 16) & 0xFF));
            }
            output.AddRange(frame.Lzw);
        }
        output.Add(0x3B);
        return [.. output];
    }

    /// <summary>Frees the lookup tables (about 64 MB) once no more frames will come.</summary>
    public void ReleaseTables()
    {
        _lookup.Release();
        _baseLookup.Release();
        _tally = null;
    }

    // ---- Differencing ----

    private static bool Differs(uint value, uint old, int tolerance)
    {
        value &= 0xFF_FFFF;
        if (value == old) return false;
        if (tolerance == 0) return true;
        var d = (int)(value & 0xFF) - (int)(old & 0xFF);
        if (d > tolerance || d < -tolerance) return true;
        d = (int)((value >> 8) & 0xFF) - (int)((old >> 8) & 0xFF);
        if (d > tolerance || d < -tolerance) return true;
        d = (int)((value >> 16) & 0xFF) - (int)((old >> 16) & 0xFF);
        return d > tolerance || d < -tolerance;
    }

    private (Rect, uint[])? Difference(ReadOnlySpan<uint> source, bool first)
    {
        int width = Width, height = Height;
        var reference = _reference;
        if (first)
        {
            var all = new uint[width * height];
            for (var i = 0; i < all.Length; i++)
            {
                var value = source[i] & 0xFF_FFFF;
                reference[i] = value;
                all[i] = value | 0xFF00_0000;
            }
            return (new Rect(0, 0, width, height), all);
        }
        var tolerance = Tolerance;
        var pixels = source[..(width * height)].ToArray();
        // Each row's first and last changed pixel, rows in parallel.
        var minX = new int[height];
        var maxX = new int[height];
        var chunks = GifRows.Chunks(height);
        Parallel.For(0, chunks, chunk =>
        {
            for (int y = height * chunk / chunks, end = height * (chunk + 1) / chunks; y < end; y++)
            {
                var row = y * width;
                var x = 0;
                while (x < width && !Differs(pixels[row + x], reference[row + x], tolerance)) x++;
                if (x < width)
                {
                    var last = width - 1;
                    while (last > x && !Differs(pixels[row + last], reference[row + last], tolerance)) last--;
                    minX[y] = x;
                    maxX[y] = last;
                }
                else
                {
                    minX[y] = -1;
                    maxX[y] = -1;
                }
            }
        });
        if (IsCancelled()) return null;
        int top = -1, bottom = -1, left = int.MaxValue, right = -1;
        for (var y = 0; y < height; y++)
        {
            if (maxX[y] < 0) continue;
            if (top < 0) top = y;
            bottom = y;
            if (minX[y] < left) left = minX[y];
            if (maxX[y] > right) right = maxX[y];
        }
        if (top < 0) return (Rect.Empty, []);
        var rect = new Rect(left, top, right - left + 1, bottom - top + 1);
        var delta = new uint[rect.Width * rect.Height];
        var rowChunks = GifRows.Chunks(rect.Height);
        Parallel.For(0, rowChunks, chunk =>
        {
            for (int row = rect.Height * chunk / rowChunks, end = rect.Height * (chunk + 1) / rowChunks; row < end; row++)
            {
                var y = rect.Y + row;
                if (maxX[y] < 0) continue;
                int line = y * width, target = row * rect.Width - rect.X;
                for (var x = minX[y]; x <= maxX[y]; x++)
                {
                    var value = pixels[line + x];
                    if (!Differs(value, reference[line + x], tolerance)) continue;
                    reference[line + x] = value & 0xFF_FFFF;
                    delta[target + x] = (value & 0xFF_FFFF) | 0xFF00_0000;
                }
            }
        });
        return (rect, delta);
    }

    // ---- Palette ----

    /// <summary>Counts the colours of <paramref name="values"/> into the tally, noting each new colour.</summary>
    private void Count(ReadOnlySpan<uint> values, bool changesOnly, List<uint> colours)
    {
        var tally = _tally ??= new ushort[1 << 24];
        foreach (var value in values)
        {
            if (changesOnly && value == 0) continue;
            var key = (int)(value & 0xFF_FFFF);
            int old = tally[key];
            if (old == 0) colours.Add((uint)key);
            tally[key] = (ushort)Math.Min(ushort.MaxValue, old + 1);
        }
    }

    /// <summary>The counts of <paramref name="colours"/>, leaving the tally zeroed.</summary>
    private int[] Collect(List<uint> colours)
    {
        if (_tally is not { } tally) return [];
        var counts = new int[colours.Count];
        for (var i = 0; i < colours.Count; i++)
        {
            var key = (int)colours[i];
            counts[i] = tally[key];
            tally[key] = 0;
        }
        return counts;
    }

    /// <summary>Colours this far (OKLab) from every palette colour are "not held".</summary>
    public const float HeldDistance = 0.02f;

    public const int BaseSize = 208;

    /// <summary>
    /// Sets the palette for a frame. The base is made from the whole frame for the first frame and whenever most of its
    /// pixels change (a moving background); otherwise it stays. The extra colours are made from the changed colours the
    /// base does not hold, and kept while the palette still holds them, so colours do not flicker from frame to frame.
    /// </summary>
    private void ChoosePalette(uint[] delta, bool first)
    {
        var colours = new List<uint>();
        Count(delta, changesOnly: true, colours);
        var counts = Collect(colours);
        var changedPalette = false;
        var changed = 0;
        foreach (var count in counts) changed += count;
        if (first || (long)changed * 10 >= (long)Width * Height * 6)
        {
            var all = new List<uint>();
            Count(_reference, changesOnly: false, all);
            // A frame of 255 colours or fewer keeps them all, exactly.
            _base = GifQuantizer.Palette(all, Collect(all), all.Count <= 255 ? 255 : BaseSize);
            if (_base.Length == 0) _base = [0];
            _baseLookup.Set(_base);
            _extra = [];
            changedPalette = true;
        }
        // The changed colours the base does not hold.
        var missing = new List<uint>();
        var missingCounts = new List<int>();
        for (var i = 0; i < colours.Count; i++)
        {
            var colour = (int)colours[i];
            if (_baseLookup.Distance(colour, _baseLookup.Index(colour)) > HeldDistance)
            {
                missing.Add(colours[i]);
                missingCounts.Add(counts[i]);
            }
        }
        if (missing.Count > 0 && !changedPalette)
        {
            // Keep the extra colours if, with them, the palette holds these too: all but a few pixels, and those not far off.
            var unheld = 0;
            var worst = 0f;
            for (var i = 0; i < missing.Count; i++)
            {
                var colour = (int)missing[i];
                var distance = _lookup.Distance(colour, _lookup.Index(colour));
                if (distance > HeldDistance) unheld += missingCounts[i];
                worst = Math.Max(worst, distance);
            }
            if ((long)unheld * 200 > changed || worst > 2 * HeldDistance)
            {
                _extra = GifQuantizer.Palette(missing, [.. missingCounts], 255 - _base.Length);
                changedPalette = true;
            }
        }
        else if (missing.Count > 0)
        {
            _extra = GifQuantizer.Palette(missing, [.. missingCounts], 255 - _base.Length);
        }
        if (changedPalette || _palette.Length == 0)
        {
            _palette = [.. _base, .. _extra];
            _lookup.Set(_palette);
            PalettesBuilt++;
        }
    }

    // ---- Indexing and dithering ----

    /// <summary>8 × 8 Bayer thresholds, 0…63, indexed (y &amp; 7) * 8 + (x &amp; 7).</summary>
    private static readonly int[] Bayer =
    [
        0, 32, 8, 40, 2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ];

    /// <summary>
    /// Each changed pixel's palette index: the nearest colour, or, by the Bayer threshold, its partner on the far side of
    /// the pixel's colour, in proportion to how far between the two it lies. Pixels that would show what is already shown,
    /// and unchanged ones, are transparent.
    /// </summary>
    private byte[]? Index(uint[] delta, Rect rect, bool first)
    {
        var transparent = (byte)_palette.Length;
        var spread = DitherSpread * DitherSpread;
        _lookup.Spread = spread;
        var width = Width;
        var shown = _shown;
        var lookup = _lookup;
        var colours = lookup.Rgb;
        var indices = new byte[rect.Width * rect.Height];
        Array.Fill(indices, transparent);
        var cancelled = false;
        var chunks = GifRows.Chunks(rect.Height);
        Parallel.For(0, chunks, chunk =>
        {
            if (IsCancelled())
            {
                cancelled = true;
                return;
            }
            for (int row = rect.Height * chunk / chunks, end = rect.Height * (chunk + 1) / chunks; row < end; row++)
            {
                var y = rect.Y + row;
                for (var column = 0; column < rect.Width; column++)
                {
                    var at = row * rect.Width + column;
                    var value = delta[at];
                    if (value == 0) continue;
                    var colour = (int)(value & 0xFF_FFFF);
                    int r = colour & 0xFF, g = (colour >> 8) & 0xFF, b = (colour >> 16) & 0xFF;
                    var chosen = lookup.Index(colour);
                    var near = colours[chosen];
                    int er = r - (near & 0xFF), eg = g - ((near >> 8) & 0xFF), eb = b - ((near >> 16) & 0xFF);
                    if (er > 1 || er < -1 || eg > 1 || eg < -1 || eb > 1 || eb < -1)
                    {
                        var other = lookup.Partner(colour, chosen);
                        if (other != chosen)
                        {
                            var far = colours[other];
                            int dr = (far & 0xFF) - (near & 0xFF), dg = ((far >> 8) & 0xFF) - ((near >> 8) & 0xFF);
                            var db = ((far >> 16) & 0xFF) - ((near >> 16) & 0xFF);
                            var length = dr * dr + dg * dg + db * db;
                            if (length > 0 && length <= spread)
                            {
                                // Far when the threshold, (t + ½) / 64, is below how far along near → far the colour lies.
                                var along = (er * dr + eg * dg + eb * db) * 128;
                                var x = rect.X + column;
                                if ((2 * Bayer[(y & 7) * 8 + (x & 7)] + 1) * length < along) chosen = other;
                            }
                        }
                    }
                    var i = y * width + rect.X + column;
                    var output = (uint)colours[chosen];
                    if (first || shown[i] != output)
                    {
                        shown[i] = output;
                        indices[at] = (byte)chosen;
                    }
                }
            }
        });
        return cancelled ? null : indices;
    }
}

/// <summary>Splits rows into chunks for the parallel loops.</summary>
internal static class GifRows
{
    public static int Chunks(int count) => Math.Max(1, Math.Min(count, Math.Min(Environment.ProcessorCount * 4, (count + 7) / 8)));

    public static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;
}

/// <summary>sRGB to OKLab in single precision, for the per-pixel work.</summary>
internal static class GifColour
{
    /// <summary>sRGB level to linear light.</summary>
    public static readonly float[] LinearTable = BuildLinear();

    private static float[] BuildLinear()
    {
        var table = new float[256];
        for (var value = 0; value < 256; value++) table[value] = Linear(value);
        return table;
    }

    public static float Linear(float level)
    {
        var c = level / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    public static (float L, float A, float B) Oklab(int colour) =>
        Oklab(LinearTable[colour & 0xFF], LinearTable[(colour >> 8) & 0xFF], LinearTable[(colour >> 16) & 0xFF]);

    public static (float L, float A, float B) Oklab(float r, float g, float b)
    {
        var l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        var m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        var s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
        return (0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }
}

/// <summary>The current palette, and the nearest entry for each 24-bit colour, looked up once per colour.</summary>
internal sealed class GifPaletteLookup
{
    /// <summary>Packed colours, as ints for the dithering arithmetic.</summary>
    public readonly int[] Rgb = new int[256];
    private readonly float[] _lab = new float[3 * 256];
    private byte[]? _cache;
    private byte[]? _partners;
    private int _count;

    /// <summary>Squared sRGB distance two colours may be apart and be mixed.</summary>
    public int Spread { get; set; } = 56 * 56;

    public void Release()
    {
        _cache = null;
        _partners = null;
    }

    public void Set(uint[] palette)
    {
        if (palette.Length > 255) throw new ArgumentException("At most 255 colours.", nameof(palette));
        _count = palette.Length;
        for (var index = 0; index < palette.Length; index++)
        {
            var colour = (int)(palette[index] & 0xFF_FFFF);
            Rgb[index] = colour;
            var (l, a, b) = GifColour.Oklab(colour);
            _lab[3 * index] = l;
            _lab[3 * index + 1] = a;
            _lab[3 * index + 2] = b;
        }
        _cache ??= new byte[1 << 24];
        Array.Fill(_cache, (byte)0xFF);
        if (_partners is not null) Array.Fill(_partners, (byte)0xFF);
    }

    /// <summary>The palette entry nearest <paramref name="colour"/> in OKLab.</summary>
    public int Index(int colour)
    {
        var cache = _cache ??= Filled();
        var cached = cache[colour];
        if (cached != 0xFF) return cached;
        var (l, a, b) = GifColour.Oklab(colour);
        var best = 0;
        var bestDistance = float.MaxValue;
        for (var entry = 0; entry < _count; entry++)
        {
            float dl = _lab[3 * entry] - l, da = _lab[3 * entry + 1] - a, db = _lab[3 * entry + 2] - b;
            var distance = dl * dl + da * da + db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = entry;
            }
        }
        // Rows run in parallel; a colour two rows meet at once gets the same answer written twice.
        cache[colour] = (byte)best;
        return best;
    }

    public float Distance(int colour, int entry)
    {
        var (l, a, b) = GifColour.Oklab(colour);
        float dl = _lab[3 * entry] - l, da = _lab[3 * entry + 1] - a, db = _lab[3 * entry + 2] - b;
        return MathF.Sqrt(dl * dl + da * da + db * db);
    }

    /// <summary>
    /// The palette entry to dither <paramref name="colour"/> with against its nearest, <paramref name="near"/>: of those
    /// within reach (<see cref="Spread"/>, and not much further than the error), on the colour's side, the one whose mix with
    /// <paramref name="near"/> comes closest to the colour. <paramref name="near"/> itself when there is none.
    /// </summary>
    public int Partner(int colour, int near)
    {
        var partners = _partners ??= Filled();
        var cached = partners[colour];
        if (cached != 0xFF) return cached;
        var n = Rgb[near];
        float er = (colour & 0xFF) - (n & 0xFF), eg = ((colour >> 8) & 0xFF) - ((n >> 8) & 0xFF);
        float eb = ((colour >> 16) & 0xFF) - ((n >> 16) & 0xFF);
        var best = near;
        var bestResidual = er * er + eg * eg + eb * eb;
        // A partner much further off than the error would sprinkle visible dots over a flat colour to fix an invisible
        // error: allowed are 12 levels, or four times the error.
        var reach = Math.Min(Spread, Math.Max(144f, 16 * bestResidual));
        for (var entry = 0; entry < _count; entry++)
        {
            var q = Rgb[entry];
            int dr = (q & 0xFF) - (n & 0xFF), dg = ((q >> 8) & 0xFF) - ((n >> 8) & 0xFF), db = ((q >> 16) & 0xFF) - ((n >> 16) & 0xFF);
            var length = dr * dr + dg * dg + db * db;
            if (length == 0 || length > reach) continue;
            var along = er * dr + eg * dg + eb * db;
            if (along <= 0) continue;
            var t = Math.Min(1f, along / length);
            float rr = er - t * dr, rg = eg - t * dg, rb = eb - t * db;
            var residual = rr * rr + rg * rg + rb * rb;
            if (residual < bestResidual)
            {
                bestResidual = residual;
                best = entry;
            }
        }
        partners[colour] = (byte)best;
        return best;
    }

    private static byte[] Filled()
    {
        var table = new byte[1 << 24];
        Array.Fill(table, (byte)0xFF);
        return table;
    }
}

/// <summary>Up to so many colours for a set of weighted colours (the Mac's GIFQuantizer).</summary>
internal static class GifQuantizer
{
    /// <summary>
    /// Median cut in OKLab, always splitting the box with the largest squared error where the split removes most of it,
    /// then a k-means pass. The output colours are the sRGB means of their members, so a dark gradient keeps its exact
    /// levels.
    /// </summary>
    public static uint[] Palette(List<uint> colours, int[] counts, int maximum)
    {
        if (colours.Count == 0 || maximum <= 0) return [];
        if (colours.Count <= maximum) return [.. colours];
        var points = new Points(colours, counts, 24_000);
        var centres = Refine(points, MedianCut(points, maximum));
        var seen = new HashSet<uint>();
        var result = new List<uint>(centres.Count);
        foreach (var (cr, cg, cb) in centres)
        {
            var r = (uint)GifRows.Clamp((int)MathF.Round(cr, MidpointRounding.AwayFromZero));
            var g = (uint)GifRows.Clamp((int)MathF.Round(cg, MidpointRounding.AwayFromZero));
            var b = (uint)GifRows.Clamp((int)MathF.Round(cb, MidpointRounding.AwayFromZero));
            var colour = r | g << 8 | b << 16;
            if (seen.Add(colour)) result.Add(colour);
        }
        return [.. result];
    }

    /// <summary>Weighted colours in flat arrays: OKLab (3 per point), sRGB (3), weight.</summary>
    private sealed class Points
    {
        public readonly int Count;
        public readonly float[] Lab;
        public readonly float[] Rgb;
        public readonly float[] Weight;

        /// <summary>Above <paramref name="limit"/> distinct colours, they are merged into 6-bit (then 5-bit) cells.</summary>
        public Points(List<uint> colours, int[] counts, int limit)
        {
            (float[] Rgb, float[] Weight)? merged = null;
            foreach (var bits in new[] { 6, 5 })
                if ((merged?.Weight.Length ?? colours.Count) > limit) merged = Merge(colours, counts, bits);
            Count = merged?.Weight.Length ?? colours.Count;
            Lab = new float[3 * Count];
            Rgb = new float[3 * Count];
            Weight = new float[Count];
            for (var index = 0; index < Count; index++)
            {
                if (merged is { } m)
                {
                    Rgb[3 * index] = m.Rgb[3 * index];
                    Rgb[3 * index + 1] = m.Rgb[3 * index + 1];
                    Rgb[3 * index + 2] = m.Rgb[3 * index + 2];
                    Weight[index] = m.Weight[index];
                }
                else
                {
                    var colour = colours[index];
                    Rgb[3 * index] = colour & 0xFF;
                    Rgb[3 * index + 1] = (colour >> 8) & 0xFF;
                    Rgb[3 * index + 2] = (colour >> 16) & 0xFF;
                    Weight[index] = counts[index];
                }
                var (l, a, b) = GifColour.Oklab(GifColour.Linear(Rgb[3 * index]), GifColour.Linear(Rgb[3 * index + 1]),
                    GifColour.Linear(Rgb[3 * index + 2]));
                Lab[3 * index] = l;
                Lab[3 * index + 1] = a;
                Lab[3 * index + 2] = b;
            }
        }

        private static (float[] Rgb, float[] Weight) Merge(List<uint> colours, int[] counts, int bits)
        {
            int shift = 8 - bits, mask = (1 << bits) - 1;
            var slot = new int[1 << (3 * bits)];
            Array.Fill(slot, -1);
            var rgb = new List<float>();
            var weight = new List<float>();
            // Each cell's heaviest colour, which stands for the cell when it is most of it (a flat fill keeps its exact colour).
            var top = new List<uint>();
            var topWeight = new List<float>();
            for (var index = 0; index < colours.Count; index++)
            {
                var colour = colours[index];
                var key = (int)((colour >> shift) & mask) | (int)((colour >> (8 + shift)) & mask) << bits
                    | (int)((colour >> (16 + shift)) & mask) << (2 * bits);
                var at = slot[key];
                if (at < 0)
                {
                    at = weight.Count;
                    slot[key] = at;
                    weight.Add(0);
                    rgb.AddRange([0, 0, 0]);
                    top.Add(colour);
                    topWeight.Add(0);
                }
                var w = (float)counts[index];
                weight[at] += w;
                rgb[3 * at] += (colour & 0xFF) * w;
                rgb[3 * at + 1] += ((colour >> 8) & 0xFF) * w;
                rgb[3 * at + 2] += ((colour >> 16) & 0xFF) * w;
                if (w > topWeight[at])
                {
                    topWeight[at] = w;
                    top[at] = colour;
                }
            }
            for (var at = 0; at < weight.Count; at++)
            {
                if (topWeight[at] * 2 >= weight[at])
                {
                    rgb[3 * at] = top[at] & 0xFF;
                    rgb[3 * at + 1] = (top[at] >> 8) & 0xFF;
                    rgb[3 * at + 2] = (top[at] >> 16) & 0xFF;
                }
                else
                {
                    rgb[3 * at] /= weight[at];
                    rgb[3 * at + 1] /= weight[at];
                    rgb[3 * at + 2] /= weight[at];
                }
            }
            return ([.. rgb], [.. weight]);
        }
    }

    private struct Box(int start, int end, float error)
    {
        public int Start = start;
        public int End = end;
        public float Error = error;
    }

    /// <summary>The squared error of points order[start..end) around their mean, and the axis along which they vary most.</summary>
    private static (float Error, int Axis) Spread(Points points, int[] order, int start, int end)
    {
        float w = 0, s0 = 0, s1 = 0, s2 = 0, q0 = 0, q1 = 0, q2 = 0;
        for (var i = start; i < end; i++)
        {
            var p = order[i];
            float pw = points.Weight[p], l = points.Lab[3 * p], a = points.Lab[3 * p + 1], b = points.Lab[3 * p + 2];
            w += pw;
            s0 += l * pw;
            s1 += a * pw;
            s2 += b * pw;
            q0 += l * l * pw;
            q1 += a * a * pw;
            q2 += b * b * pw;
        }
        if (!(w > 0)) return (0, 0);
        float v0 = Math.Max(0, q0 - s0 * s0 / w), v1 = Math.Max(0, q1 - s1 * s1 / w), v2 = Math.Max(0, q2 - s2 * s2 / w);
        return (v0 + v1 + v2, v0 >= v1 && v0 >= v2 ? 0 : v1 >= v2 ? 1 : 2);
    }

    private static List<(float R, float G, float B)> MedianCut(Points points, int maximum)
    {
        var order = new int[points.Count];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        var boxes = new List<Box> { new(0, points.Count, Spread(points, order, 0, points.Count).Error) };
        const int buckets = 128;
        var bucketWeight = new float[buckets];
        var bucketSum = new float[buckets];
        var bucketSquares = new float[buckets];
        while (boxes.Count < maximum)
        {
            var pick = -1;
            var largest = 0f;
            for (var index = 0; index < boxes.Count; index++)
            {
                var candidate = boxes[index];
                if (candidate.End - candidate.Start > 1 && candidate.Error > largest)
                {
                    largest = candidate.Error;
                    pick = index;
                }
            }
            if (pick < 0) break;
            var box = boxes[pick];
            var axis = Spread(points, order, box.Start, box.End).Axis;
            // Bucket the points along the axis and split between buckets where the two sides' error along it is least.
            float low = float.MaxValue, high = float.MinValue;
            for (var i = box.Start; i < box.End; i++)
            {
                var v = points.Lab[3 * order[i] + axis];
                if (v < low) low = v;
                if (v > high) high = v;
            }
            if (!(high > low))
            {
                boxes[pick] = box with { Error = 0 };
                continue;
            }
            var scale = buckets / (high - low) * 0.99999f;
            Array.Clear(bucketWeight);
            Array.Clear(bucketSum);
            Array.Clear(bucketSquares);
            for (var i = box.Start; i < box.End; i++)
            {
                var p = order[i];
                float v = points.Lab[3 * p + axis], w = points.Weight[p];
                var b = Math.Min(buckets - 1, (int)((v - low) * scale));
                bucketWeight[b] += w;
                bucketSum[b] += v * w;
                bucketSquares[b] += v * v * w;
            }
            float totalWeight = 0, totalSum = 0, totalSquares = 0;
            for (var b = 0; b < buckets; b++)
            {
                totalWeight += bucketWeight[b];
                totalSum += bucketSum[b];
                totalSquares += bucketSquares[b];
            }
            float leftWeight = 0, leftSum = 0, leftSquares = 0;
            var cut = -1;
            var bestError = float.MaxValue;
            for (var b = 0; b < buckets - 1; b++)
            {
                leftWeight += bucketWeight[b];
                leftSum += bucketSum[b];
                leftSquares += bucketSquares[b];
                var rightWeight = totalWeight - leftWeight;
                if (!(leftWeight > 0) || !(rightWeight > 0) || !(bucketWeight[b] > 0)) continue;
                float rightSum = totalSum - leftSum, rightSquares = totalSquares - leftSquares;
                var error = leftSquares - leftSum * leftSum / leftWeight + (rightSquares - rightSum * rightSum / rightWeight);
                if (error < bestError)
                {
                    bestError = error;
                    cut = b;
                }
            }
            if (cut < 0)
            {
                boxes[pick] = box with { Error = 0 };
                continue;
            }
            // Partition in place: buckets up to the cut to the left.
            int front = box.Start, back = box.End - 1;
            while (front <= back)
            {
                var v = points.Lab[3 * order[front] + axis];
                if (Math.Min(buckets - 1, (int)((v - low) * scale)) <= cut)
                {
                    front++;
                }
                else
                {
                    (order[front], order[back]) = (order[back], order[front]);
                    back--;
                }
            }
            if (front <= box.Start || front >= box.End)
            {
                boxes[pick] = box with { Error = 0 };
                continue;
            }
            boxes[pick] = new Box(box.Start, front, Spread(points, order, box.Start, front).Error);
            boxes.Add(new Box(front, box.End, Spread(points, order, front, box.End).Error));
        }
        var centres = new List<(float, float, float)>(boxes.Count);
        foreach (var box in boxes)
        {
            float w = 0, r = 0, g = 0, b = 0;
            for (var i = box.Start; i < box.End; i++)
            {
                var p = order[i];
                var pw = points.Weight[p];
                w += pw;
                r += points.Rgb[3 * p] * pw;
                g += points.Rgb[3 * p + 1] * pw;
                b += points.Rgb[3 * p + 2] * pw;
            }
            w = Math.Max(w, 1e-6f);
            centres.Add((r / w, g / w, b / w));
        }
        return centres;
    }

    /// <summary>One k-means pass: each point to its nearest centre (OKLab), each centre to its members' mean.</summary>
    private static List<(float R, float G, float B)> Refine(Points points, List<(float R, float G, float B)> centres)
    {
        var k = centres.Count;
        var lab = new float[3 * k];
        var sums = new float[4 * k];
        var heaviest = new float[k];
        var heaviestPoint = new int[k];
        for (var index = 0; index < k; index++)
        {
            var (cr, cg, cb) = centres[index];
            var (l, a, b) = GifColour.Oklab(GifColour.Linear(cr), GifColour.Linear(cg), GifColour.Linear(cb));
            lab[3 * index] = l;
            lab[3 * index + 1] = a;
            lab[3 * index + 2] = b;
        }
        for (var p = 0; p < points.Count; p++)
        {
            float l = points.Lab[3 * p], a = points.Lab[3 * p + 1], b = points.Lab[3 * p + 2];
            var best = 0;
            var bestDistance = float.MaxValue;
            for (var c = 0; c < k; c++)
            {
                float dl = lab[3 * c] - l, da = lab[3 * c + 1] - a, db = lab[3 * c + 2] - b;
                var distance = dl * dl + da * da + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = c;
                }
            }
            var w = points.Weight[p];
            sums[4 * best] += points.Rgb[3 * p] * w;
            sums[4 * best + 1] += points.Rgb[3 * p + 1] * w;
            sums[4 * best + 2] += points.Rgb[3 * p + 2] * w;
            sums[4 * best + 3] += w;
            if (w > heaviest[best])
            {
                heaviest[best] = w;
                heaviestPoint[best] = p;
            }
        }
        // A centre most of whose weight is one colour (a flat fill: a box, the text, white) takes that colour exactly, so it
        // needs no dither.
        var refined = new List<(float, float, float)>(k);
        for (var index = 0; index < k; index++)
        {
            var w = sums[4 * index + 3];
            if (!(w > 0))
            {
                refined.Add(centres[index]);
                continue;
            }
            if (heaviest[index] * 3 >= w)
            {
                var p = heaviestPoint[index];
                refined.Add((points.Rgb[3 * p], points.Rgb[3 * p + 1], points.Rgb[3 * p + 2]));
                continue;
            }
            refined.Add((sums[4 * index] / w, sums[4 * index + 1] / w, sums[4 * index + 2] / w));
        }
        return refined;
    }
}

/// <summary>GIF's LZW with variable code size, clear codes and a 12-bit cap (the Mac's GIFLZW).</summary>
public static class GifLzw
{
    /// <summary>Packs codes LSB first into sub-blocks of at most 255 bytes.</summary>
    private sealed class BitSink(byte[] output, int offset)
    {
        public int Length { get; private set; } = offset + 1;
        private int _blockStart = offset;
        private ulong _bits;
        private int _bitCount;

        public void Emit(int code, int width)
        {
            _bits |= (ulong)code << _bitCount;
            _bitCount += width;
            while (_bitCount >= 8)
            {
                Byte((byte)_bits);
                _bits >>= 8;
                _bitCount -= 8;
            }
        }

        private void Byte(byte value)
        {
            output[Length++] = value;
            if (Length - _blockStart - 1 == 255)
            {
                output[_blockStart] = 255;
                _blockStart = Length;
                output[Length++] = 0;
            }
        }

        /// <summary>Flushes the last bits, closes the sub-block and adds the terminator.</summary>
        public void Finish()
        {
            if (_bitCount > 0)
            {
                Byte((byte)_bits);
                _bits = 0;
                _bitCount = 0;
            }
            var pending = Length - _blockStart - 1;
            if (pending > 0)
            {
                output[_blockStart] = (byte)pending;
                output[Length++] = 0;
            }
            else
            {
                // The empty block just opened is the terminator.
                output[_blockStart] = 0;
            }
        }
    }

    /// <summary>
    /// The minimum code size, then the LZW stream of <paramref name="indices"/> in sub-blocks, then the block terminator.
    /// Codes grow from minimum + 1 bits to 12; a clear code restarts the table when it fills.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> indices, int minCodeSize)
    {
        if (minCodeSize is < 2 or > 8) throw new ArgumentOutOfRangeException(nameof(minCodeSize));
        // At most 12 bits a pixel, plus clear codes, sub-block lengths and slack.
        var buffer = new byte[indices.Length * 2 + 64 + 1];
        buffer[0] = (byte)minCodeSize;
        var sink = new BitSink(buffer, 1);
        int clear = 1 << minCodeSize, end = clear + 1;
        var width = minCodeSize + 1;
        var next = end + 1;
        // Open-addressed table of (prefix << 8 | byte) → code.
        const int tableSize = 1 << 14, tableMask = tableSize - 1;
        var keys = new int[tableSize];
        var codes = new ushort[tableSize];
        Array.Fill(keys, -1);
        sink.Emit(clear, width);
        if (indices.Length > 0)
        {
            int prefix = indices[0];
            for (var position = 1; position < indices.Length; position++)
            {
                int value = indices[position];
                var key = prefix << 8 | value;
                var slot = (int)(((uint)key * 0x9E37_79B1u) >> 7) & tableMask;
                var found = -1;
                while (keys[slot] >= 0)
                {
                    if (keys[slot] == key)
                    {
                        found = codes[slot];
                        break;
                    }
                    slot = (slot + 1) & tableMask;
                }
                if (found >= 0)
                {
                    prefix = found;
                    continue;
                }
                sink.Emit(prefix, width);
                if (next < 4096)
                {
                    keys[slot] = key;
                    codes[slot] = (ushort)next;
                    next++;
                    // The decoder adds this entry one code later, so it widens once next passes the current width.
                    if (next > (1 << width) && width < 12) width++;
                }
                else
                {
                    sink.Emit(clear, width);
                    Array.Fill(keys, -1);
                    width = minCodeSize + 1;
                    next = end + 1;
                }
                prefix = value;
            }
            sink.Emit(prefix, width);
            // The decoder adds an entry for that last code, and may widen.
            if (next < 4096 && next == (1 << width) && width < 12) width++;
        }
        sink.Emit(end, width);
        sink.Finish();
        return buffer.AsSpan(0, sink.Length).ToArray();
    }
}

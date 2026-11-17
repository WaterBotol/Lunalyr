using Lunalyr.Core.Cards;

namespace Lunalyr.Core.Tests.Cards;

/// <summary>
/// Ported from GIFEncoderTests.swift (the Mac's 7767ef6 and 52a4204): the hand-made GIF encoder. Every file is decoded by
/// <see cref="GifDecoder"/>, which composites partial frames the way browsers do (the Mac decodes with ImageIO).
/// </summary>
public sealed class GifEncoderTests
{
    private struct Random(ulong state)
    {
        private ulong _state = state;

        public ulong Next()
        {
            _state = unchecked(_state * 6364136223846793005UL + 1442695040888963407UL);
            return _state >> 33;
        }
    }

    private static uint Pack(int r, int g, int b) => (uint)r | (uint)g << 8 | (uint)b << 16;

    private static (int R, int G, int B) Channels(uint value) => ((int)(value & 0xFF), (int)((value >> 8) & 0xFF), (int)((value >> 16) & 0xFF));

    /// <summary>A dark, smooth two-way gradient, the case that bands: well over 255 colours, so it is quantised and dithered.</summary>
    private static uint[] Gradient(int width, int height)
    {
        var pixels = new uint[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            double x = (double)(i % width) / width, y = (double)(i / width) / height;
            pixels[i] = Pack((int)(10 + 60 * x), (int)(8 + 30 * y), (int)(30 + 50 * (x + y) / 2));
        }
        return pixels;
    }

    private static (byte[] Data, GifEncoder Encoder) Encode(IEnumerable<uint[]> frames, int width, int height, int delay = 4)
    {
        var encoder = new GifEncoder(width, height);
        foreach (var frame in frames) Assert.NotNull(encoder.Add(frame, delay));
        return (encoder.Data(), encoder);
    }

    /// <summary>Mean and largest per-channel difference.</summary>
    private static (double Mean, int Max) Error(uint[] a, uint[] b)
    {
        long sum = 0;
        var largest = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var p = Channels(a[i]);
            var q = Channels(b[i]);
            var e = Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B)));
            sum += e;
            largest = Math.Max(largest, e);
        }
        return ((double)sum / Math.Max(1, a.Length), largest);
    }

    // Ported from GIFEncoderTests.swift: testExactColoursRoundTripThroughImageIO
    [Fact]
    public void ExactColoursRoundTrip()
    {
        // Noise fills the code table again and again (clear codes); runs make long codes; table sizes from 2 colours to 255.
        var random = new Random(7);
        foreach (var (colours, width, height) in new[] { (2, 37, 23), (3, 64, 64), (4, 1, 1), (5, 50, 3), (16, 120, 90), (64, 200, 150), (200, 256, 256), (255, 301, 177) })
        {
            var palette = new uint[colours];
            for (var index = 0; index < colours; index++)
                palette[index] = Pack(index * 37 % 256, index * 101 % 256, (index * 13 + (int)(random.Next() % 7)) % 256);
            var noise = new uint[width * height];
            for (var i = 0; i < noise.Length; i++) noise[i] = palette[(int)(random.Next() % (ulong)colours)];
            var runs = new uint[width * height];
            for (var i = 0; i < runs.Length; i++) runs[i] = palette[i / 97 % colours];
            foreach (var pixels in new[] { noise, runs })
            {
                var (data, encoder) = Encode([pixels], width, height);
                Assert.True(encoder.PaletteSizes[0] <= 255);
                Assert.Equal(pixels, GifDecoder.Decode(data).Frames[0]);
            }
        }
    }

    // Ported from GIFEncoderTests.swift: testLZWStreamHasValidSubBlocks
    [Fact]
    public void LzwStreamHasValidSubBlocks()
    {
        var random = new Random(3);
        var indices = new byte[100_000];
        for (var i = 0; i < indices.Length; i++) indices[i] = (byte)(random.Next() % 200);
        var output = GifLzw.Encode(indices, 8);
        Assert.Equal(8, output[0]);
        var position = 1;
        while (position < output.Length && output[position] != 0) position += output[position] + 1;
        Assert.Equal(output.Length - 1, position); // sub-blocks end with a single terminator
    }

    // Ported from GIFEncoderTests.swift: testAPaletteHoldsAtMost255ColoursAndSpendsThemWell
    [Fact]
    public void APaletteHoldsAtMost255ColoursAndSpendsThemWell()
    {
        const int width = 320, height = 240;
        var pixels = new uint[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            int x = i % width, y = i / width;
            pixels[i] = Pack(x * 255 / width, y * 255 / height, (x + y) % 256);
        }
        Assert.True(pixels.Distinct().Count() > 255);
        var (data, encoder) = Encode([pixels], width, height);
        Assert.True(encoder.PaletteSizes[0] <= 255);
        Assert.True(encoder.PaletteSizes[0] > 200, "the colours are used, not wasted");
        Assert.True(Error(GifDecoder.Decode(data).Frames[0], pixels).Mean < 11, "a whole rainbow in 255 colours stays close on average");
    }

    // Ported from GIFEncoderTests.swift: testADarkGradientIsDitheredNotBanded
    [Fact]
    public void ADarkGradientIsDitheredNotBanded()
    {
        const int width = 400, height = 300;
        var source = Gradient(width, height);
        Assert.True(source.Distinct().Count() > 1000, "more colours than a palette holds");
        var (data, _) = Encode([source], width, height);
        var decoded = GifDecoder.Decode(data).Frames[0];
        Assert.True(Error(decoded, source).Mean < 1.5);
        // Averaged over 8 × 8 tiles (what the eye sees of dithering), the gradient is reproduced within about a level.
        double worst = 0;
        int tiles = 0, off = 0;
        for (var tileY = 0; tileY < height - 8; tileY += 8)
        for (var tileX = 0; tileX < width - 8; tileX += 8)
        {
            double dr = 0, dg = 0, db = 0;
            for (var y = tileY; y < tileY + 8; y++)
            for (var x = tileX; x < tileX + 8; x++)
            {
                var p = Channels(source[y * width + x]);
                var q = Channels(decoded[y * width + x]);
                dr += p.R - q.R;
                dg += p.G - q.G;
                db += p.B - q.B;
            }
            var error = Math.Max(Math.Abs(dr / 64), Math.Max(Math.Abs(dg / 64), Math.Abs(db / 64)));
            worst = Math.Max(worst, error);
            tiles++;
            if (error > 1.5) off++;
        }
        Assert.True(worst < 2.5, $"{worst}");
        Assert.True((double)off / tiles < 0.03, $"{off} of {tiles} tiles off by more than 1.5 levels");
    }

    // Ported from GIFEncoderTests.swift: testLaterFramesStoreOnlyWhatChangedAndDecodeToTheSource
    [Fact]
    public void LaterFramesStoreOnlyWhatChangedAndDecodeToTheSource()
    {
        const int width = 240, height = 300;
        var backdrop = Gradient(width, height);
        var frames = new List<uint[]>();
        for (var step = 0; step < 6; step++)
        {
            var frame = (uint[])backdrop.Clone();
            // A "word": a white block with a soft coloured edge, moving right.
            for (var y = 120; y < 160; y++)
            for (var x = 20 + step * 20; x < 80 + step * 20; x++)
            {
                var edge = y == 120 || y == 159 || x == 20 + step * 20;
                frame[y * width + x] = edge ? Pack(200, 150, 220) : Pack(255, 255, 255);
            }
            frames.Add(frame);
        }
        frames.Add(frames[5]); // an identical frame lengthens the one before
        var (data, encoder) = Encode(frames, width, height);
        Assert.Equal(6, encoder.FrameCount);
        Assert.Equal([4, 4, 4, 4, 4, 8], encoder.Delays);
        Assert.Equal(new GifEncoder.Rect(0, 0, width, height), encoder.Rects[0]);
        foreach (var rect in encoder.Rects.Skip(1))
        {
            Assert.Equal(120, rect.Y);
            Assert.Equal(40, rect.Height);
            Assert.True(rect.Width <= 81, "only the word's old and new place");
        }
        var decoded = GifDecoder.Decode(data);
        Assert.Equal(6, decoded.Frames.Count);
        uint[]? previous = null;
        for (var index = 0; index < 6; index++)
        {
            var frame = decoded.Frames[index];
            Assert.True(Error(frame, frames[index]).Mean < 1.5, $"frame {index}");
            if (previous is not null)
            {
                // The untouched backdrop is bit for bit what it was: no shimmer.
                for (var i = 0; i < width * height; i++)
                    if (i / width < 115 || i / width > 165) Assert.Equal(previous[i], frame[i]);
            }
            previous = frame;
        }
    }

    // Ported from GIFEncoderTests.swift: testABackdropAWordMovesOffComesBackExactly
    [Fact]
    public void ABackdropAWordMovesOffComesBackExactly()
    {
        const int width = 240, height = 200;
        var backdrop = Gradient(width, height);
        var word = (uint[])backdrop.Clone();
        // A white block with a soft, coloured glow, unlike anything in the backdrop.
        for (var y = 60; y < 140; y++)
        for (var x = 40; x < 200; x++)
        {
            var edge = Math.Min(Math.Min(x - 40, 199 - x), Math.Min(y - 60, 139 - y));
            word[y * width + x] = edge < 6 ? Pack(150 + edge * 15, 90 + edge * 20, 200) : Pack(255, 255, 255);
        }
        var (data, encoder) = Encode([backdrop, word, backdrop], width, height);
        Assert.Equal(3, encoder.FrameCount);
        var decoded = GifDecoder.Decode(data);
        Assert.True(Error(decoded.Frames[1], word).Mean < 2);
        Assert.Equal(decoded.Frames[0], decoded.Frames[2]); // nothing of the word is left behind
    }

    // Ported from GIFEncoderTests.swift: testAToleranceLeavesOutInvisibleChangesWithoutDrifting
    [Fact]
    public void AToleranceLeavesOutInvisibleChangesWithoutDrifting()
    {
        const int width = 64, height = 64;
        var encoder = new GifEncoder(width, height) { Tolerance = 2 };
        var frame = Enumerable.Repeat(Pack(40, 40, 40), width * height).ToArray();
        encoder.Add(frame, 4);
        // Creeping up a level a frame: stored every third frame, never left behind.
        for (var level = 41; level <= 52; level++)
        {
            frame = Enumerable.Repeat(Pack(level, level, level), width * height).ToArray();
            encoder.Add(frame, 4);
        }
        Assert.Equal(5, encoder.FrameCount);
        Assert.Equal(0, Error(GifDecoder.Decode(encoder.Data()).Frames[4], frame).Max);
    }

    // Ported from GIFEncoderTests.swift: testDelaysAreWholeCentisecondsOfAtLeastTwoAndTheGIFLoops
    [Fact]
    public void DelaysAreWholeCentisecondsOfAtLeastTwoAndTheGifLoops()
    {
        Assert.Equal(2, GifEncoder.Centiseconds(0));
        Assert.Equal(2, GifEncoder.Centiseconds(0.004));
        Assert.Equal(2, GifEncoder.Centiseconds(0.01)); // 1 cs would be slowed to 10 by browsers
        Assert.Equal(4, GifEncoder.Centiseconds(0.04));
        Assert.Equal(5, GifEncoder.Centiseconds(0.045));
        Assert.Equal(180, GifEncoder.Centiseconds(1.8));
        Assert.Equal(65535, GifEncoder.Centiseconds(1000));
        const int width = 16, height = 16;
        var writer = new GifCardWriter(byteBudget: null);
        double[] delays = [0.004, 0.04, 0.05, 1.8];
        for (var index = 0; index < delays.Length; index++)
        {
            var frame = Enumerable.Repeat(Pack(index * 60, 20, 200 - index * 40), width * height).ToArray();
            Assert.True(writer.AddPacked(frame, delays[index], width, height));
        }
        var decoded = GifDecoder.Decode(writer.Finish()!);
        Assert.Equal(4, decoded.Frames.Count);
        Assert.Equal([2, 4, 5, 180], decoded.Delays);
        Assert.Equal(0, decoded.LoopCount); // loops for ever
    }

    /// <summary>Two seconds at 25 fps of noise sweeping over a gradient: a lot of bytes.</summary>
    private static List<uint[]> BusyFrames(int width, int height)
    {
        var backdrop = Gradient(width, height);
        var random = new Random(11);
        var frames = new List<uint[]>();
        for (var step = 0; step < 50; step++)
        {
            var frame = (uint[])backdrop.Clone();
            for (var y = 40; y < height - 40; y++)
            for (var x = 0; x < width / 3; x++)
            {
                var value = (int)(random.Next() % 256);
                frame[y * width + (x + step * 4) % width] = Pack(value, 255 - value, value * 7 % 256);
            }
            frames.Add(frame);
        }
        return frames;
    }

    // Ported from GIFEncoderTests.swift: testAGIFOverBudgetDropsFramesFirstThenShrinks
    [Fact]
    public void AGifOverBudgetDropsFramesFirstThenShrinks()
    {
        const int width = 200, height = 160;
        var frames = BusyFrames(width, height);
        (byte[] Data, GifCardWriter.Summary Summary) Make(int? budget)
        {
            var writer = new GifCardWriter(budget);
            foreach (var frame in frames) Assert.True(writer.AddPacked(frame, 0.04, width, height));
            var data = writer.Finish()!;
            return (data, writer.Written!.Value);
        }
        var (full, unlimited) = Make(null);
        Assert.Equal(50, unlimited.Frames);
        Assert.Equal(1, unlimited.Scale);
        Assert.Equal(full.Length, unlimited.Bytes);

        // A little over: fewer frames per second, the same size.
        var (lighter, fewer) = Make(full.Length * 3 / 4);
        Assert.True(lighter.Length <= full.Length * 3 / 4);
        Assert.Equal(lighter.Length, fewer.Bytes);
        Assert.True(fewer.Frames < 50);
        Assert.True(fewer.Gap > 4);
        Assert.Equal(1, fewer.Scale);
        var decoded = GifDecoder.Decode(lighter);
        Assert.Equal(200, decoded.Delays.Sum()); // the GIF lasts as long as before
        Assert.Equal(width, decoded.Width);

        // Far over: smaller too.
        var (smallest, shrunk) = Make(full.Length / 5);
        Assert.True(smallest.Length <= full.Length / 5);
        Assert.True(shrunk.Scale < 1);
        Assert.True(GifDecoder.Decode(smallest).Width < width);
    }

    // Ported from GIFEncoderTests.swift: testACancelledWriterStopsAndGivesNothing
    [Fact]
    public void ACancelledWriterStopsAndGivesNothing()
    {
        const int width = 32, height = 32;
        var frame = Gradient(width, height);
        var writer = new GifCardWriter(byteBudget: null);
        Assert.True(writer.AddPacked(frame, 0.04, width, height));
        writer.Cancel();
        Assert.False(writer.AddPacked(frame, 0.04, width, height));
        Assert.Null(writer.Finish());
    }

    [Fact]
    public void BgraFramesArePackedAsSrgb()
    {
        var packed = new uint[2];
        GifCardWriter.Pack([10, 20, 30, 255, 1, 2, 3, 255], packed, 2);
        Assert.Equal(Pack(30, 20, 10), packed[0]); // BGRA: blue first
        Assert.Equal(Pack(3, 2, 1), packed[1]);
    }
}

/// <summary>
/// A small GIF89a decoder for the tests: each frame composited over the last as browsers do (partial rectangles,
/// transparency, "do not dispose"), as packed sRGB.
/// </summary>
internal static class GifDecoder
{
    public sealed record Gif(int Width, int Height, int? LoopCount, List<uint[]> Frames, List<int> Delays);

    public static Gif Decode(byte[] data)
    {
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(data, 0, 6));
        int width = data[6] | data[7] << 8, height = data[8] | data[9] << 8;
        var flags = data[10];
        var position = 13;
        uint[]? global = null;
        if ((flags & 0x80) != 0) global = Table(data, ref position, flags & 7);
        var canvas = new uint[width * height];
        var frames = new List<uint[]>();
        var delays = new List<int>();
        int? loop = null;
        int delay = 0;
        int? transparent = null;
        while (true)
        {
            var block = data[position++];
            if (block == 0x3B) break;
            if (block == 0x21)
            {
                var label = data[position++];
                if (label == 0xF9)
                {
                    var size = data[position];
                    var packed = data[position + 1];
                    delay = data[position + 2] | data[position + 3] << 8;
                    transparent = (packed & 1) != 0 ? data[position + 4] : null;
                    position += size + 1;
                    Assert.Equal(0, data[position++]);
                }
                else if (label == 0xFF)
                {
                    var size = data[position];
                    var name = System.Text.Encoding.ASCII.GetString(data, position + 1, size);
                    position += size + 1;
                    var sub = SubBlocks(data, ref position);
                    if (name == "NETSCAPE2.0" && sub.Length >= 3) loop = sub[1] | sub[2] << 8;
                }
                else
                {
                    position++;
                    SubBlocks(data, ref position);
                }
                continue;
            }
            Assert.Equal(0x2C, block);
            int left = data[position] | data[position + 1] << 8, top = data[position + 2] | data[position + 3] << 8;
            int w = data[position + 4] | data[position + 5] << 8, h = data[position + 6] | data[position + 7] << 8;
            var local = data[position + 8];
            position += 9;
            var table = (local & 0x80) != 0 ? Table(data, ref position, local & 7) : global!;
            var minCode = data[position++];
            var indices = Lzw(SubBlocks(data, ref position), minCode, w * h);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var index = indices[y * w + x];
                if (transparent == index) continue;
                canvas[(top + y) * width + left + x] = table[index];
            }
            frames.Add((uint[])canvas.Clone());
            delays.Add(delay);
            transparent = null;
        }
        return new Gif(width, height, loop, frames, delays);
    }

    private static uint[] Table(byte[] data, ref int position, int bits)
    {
        var count = 1 << (bits + 1);
        var table = new uint[count];
        for (var i = 0; i < count; i++) table[i] = data[position + 3 * i] | (uint)data[position + 3 * i + 1] << 8 | (uint)data[position + 3 * i + 2] << 16;
        position += 3 * count;
        return table;
    }

    private static byte[] SubBlocks(byte[] data, ref int position)
    {
        var output = new List<byte>();
        while (true)
        {
            var size = data[position++];
            if (size == 0) break;
            output.AddRange(data.AsSpan(position, size).ToArray());
            position += size;
        }
        return [.. output];
    }

    private static byte[] Lzw(byte[] stream, int minCode, int count)
    {
        int clear = 1 << minCode, end = clear + 1;
        var prefixes = new int[4096];
        var suffixes = new byte[4096];
        var lengths = new int[4096];
        var output = new List<byte>(count);
        int width = minCode + 1, next = end + 1, previous = -1;
        int bits = 0, bitCount = 0, position = 0;
        for (var i = 0; i < clear; i++)
        {
            prefixes[i] = -1;
            suffixes[i] = (byte)i;
            lengths[i] = 1;
        }
        byte FirstOf(int code)
        {
            while (prefixes[code] >= 0) code = prefixes[code];
            return suffixes[code];
        }
        void Write(int code)
        {
            var start = output.Count;
            var length = lengths[code];
            for (var i = 0; i < length; i++) output.Add(0);
            for (var i = length - 1; i >= 0; i--)
            {
                output[start + i] = suffixes[code];
                code = prefixes[code];
            }
        }
        while (true)
        {
            while (bitCount < width)
            {
                if (position >= stream.Length) return [.. output];
                bits |= stream[position++] << bitCount;
                bitCount += 8;
            }
            var code = bits & ((1 << width) - 1);
            bits >>= width;
            bitCount -= width;
            if (code == clear)
            {
                width = minCode + 1;
                next = end + 1;
                previous = -1;
                continue;
            }
            if (code == end) break;
            if (previous < 0)
            {
                Write(code);
                previous = code;
                continue;
            }
            if (code < next)
            {
                Write(code);
                if (next < 4096)
                {
                    prefixes[next] = previous;
                    suffixes[next] = FirstOf(code);
                    lengths[next] = lengths[previous] + 1;
                    next++;
                }
            }
            else
            {
                Assert.Equal(next, code);
                prefixes[next] = previous;
                suffixes[next] = FirstOf(previous);
                lengths[next] = lengths[previous] + 1;
                next++;
                Write(code);
            }
            if (next == (1 << width) && width < 12) width++;
            previous = code;
        }
        Assert.Equal(count, output.Count);
        return [.. output];
    }
}

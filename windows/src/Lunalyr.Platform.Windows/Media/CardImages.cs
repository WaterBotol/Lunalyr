// A lyric card's PNG, written with WIC (the Mac's stillPNG, LyricCard.swift), its GIF, written by the card's own encoder
// (Lunalyr.Core.Cards.GifEncoder, the Mac's 7767ef6), and an animated GIF read back frame by frame for a card's background
// (the Mac's LyricCardBackground). GIF metadata (frame delays, the endless loop) is read through
// IWICMetadataQueryReader's vtable with PROPVARIANTs made here, so it stays Native AOT safe.

using System.Runtime.InteropServices;
using Lunalyr.Core.Colour;
using Vortice.WIC;

namespace Lunalyr.Platform.Media;

/// <summary>Writes a card's still as a PNG and its frames as a looping GIF.</summary>
public static unsafe class CardImages
{
    /// <summary>Opaque BGRA (rows top first, width × 4 bytes a row) as PNG bytes.</summary>
    public static byte[] EncodePng(ReadOnlySpan<byte> bgra, int width, int height)
    {
        using var wic = new IWICImagingFactory();
        using var memory = new MemoryStream();
        using (var encoder = wic.CreateEncoder(ContainerFormat.Png, memory))
        {
            using var frame = encoder.CreateNewFrame(out var properties);
            frame.Initialize(properties);
            frame.SetSize((uint)width, (uint)height);
            var format = PixelFormat.Format32bppBGRA;
            frame.SetPixelFormat(ref format);
            fixed (byte* data = bgra) frame.WritePixels((uint)height, (uint)(width * 4), (uint)(width * height * 4), data);
            frame.Commit();
            encoder.Commit();
            properties?.Dispose();
        }
        return memory.ToArray();
    }

    /// <summary>
    /// A GIF of <paramref name="frames"/> (opaque BGRA, each shown for its delay in seconds) that loops for ever.
    /// <paramref name="cancellationToken"/> stops it between frames.
    /// </summary>
    public static byte[]? EncodeGif(IReadOnlyList<(byte[] Bgra, double Delay)> frames, int width, int height,
                                    CancellationToken cancellationToken = default)
    {
        if (frames.Count == 0) return null;
        // The card's own encoder (the Mac's GIFEncoder, 7767ef6), with no size budget.
        var gif = new Lunalyr.Core.Cards.GifCardWriter(byteBudget: null);
        using var registration = cancellationToken.Register(gif.Cancel);
        foreach (var (bgra, delay) in frames)
            if (!gif.Add(bgra, width, height, delay)) return null;
        return gif.Finish();
    }

    /// <summary>What a GIF file holds: its frames' sizes and delays and its loop count (for checks; nothing is shown).</summary>
    public static GifInfo? ReadGif(string path)
    {
        try
        {
            using var wic = new IWICImagingFactory();
            using var decoder = wic.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnDemand);
            if (decoder.ContainerFormat != ContainerFormatGuids.Gif) return null;
            int? loops = null;
            using (var global = decoder.MetadataQueryReader)
            {
                // The NETSCAPE2.0 sub-block: its size (3), its id (1), then the loop count, low byte first.
                if (GetBytes(global.NativePointer, "/appext/Data") is { Length: >= 4 } data && data[0] >= 3 && data[1] == 1) loops = data[2] | (data[3] << 8);
            }
            var frames = new List<(int Width, int Height, double Delay)>();
            for (var i = 0u; i < decoder.FrameCount; i++)
            {
                using var frame = decoder.GetFrame(i);
                using var reader = frame.MetadataQueryReader;
                var delay = GetUnsigned(reader.NativePointer, "/grctlext/Delay") is { } d ? d / 100.0 : 0;
                frames.Add(((int)frame.Size.Width, (int)frame.Size.Height, delay));
            }
            return new GifInfo(frames, loops);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return null;
        }
    }

    /// <summary>A byte vector from a metadata query reader, or null.</summary>
    private static byte[]? GetBytes(nint reader, string name)
    {
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        fixed (char* key = name)
        {
            if (((delegate* unmanaged[Stdcall]<nint, char*, byte*, int>)MfInterop.Slot(reader, 5))(reader, key, variant) < 0) return null;
        }
        try
        {
            if (*(ushort*)variant != 0x1011) return null;
            var count = *(uint*)(variant + 8);
            var data = *(byte**)(variant + 16);
            return new ReadOnlySpan<byte>(data, (int)count).ToArray();
        }
        finally
        {
            MfInterop.PropVariantClear(variant);
        }
    }

    /// <summary>An unsigned value from a metadata query reader (IWICMetadataQueryReader: 5 GetMetadataByName), or null.</summary>
    internal static uint? GetUnsigned(nint reader, string name)
    {
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        fixed (char* key = name)
        {
            if (((delegate* unmanaged[Stdcall]<nint, char*, byte*, int>)MfInterop.Slot(reader, 5))(reader, key, variant) < 0) return null;
        }
        try
        {
            return *(ushort*)variant switch
            {
                17 => variant[8],          // VT_UI1
                18 => *(ushort*)(variant + 8), // VT_UI2
                19 => *(uint*)(variant + 8),   // VT_UI4
                11 => *(short*)(variant + 8) != 0 ? 1u : 0u, // VT_BOOL
                _ => null,
            };
        }
        finally
        {
            MfInterop.PropVariantClear(variant);
        }
    }
}

/// <summary>A GIF's frames (size and delay in seconds) and its loop count (0 = for ever; null when it doesn't loop).</summary>
public sealed record GifInfo(IReadOnlyList<(int Width, int Height, double Delay)> Frames, int? LoopCount);

/// <summary>
/// An animated GIF read frame by frame: each frame composed over the ones before as the GIF says (its place, its
/// transparency, its disposal), scaled to at most <see cref="MaxSide"/>, as premultiplied BGRA.
/// </summary>
public sealed unsafe class AnimatedPicture : IDisposable
{
    public const int MaxSide = 1920;

    private readonly IWICImagingFactory _wic;
    private readonly IWICBitmapDecoder _decoder;
    private readonly int _canvasWidth, _canvasHeight;
    private readonly uint[] _canvas;
    private uint[]? _saved;
    private int _next;
    private (int Index, CoverArt Art)? _last;

    private AnimatedPicture(IWICImagingFactory wic, IWICBitmapDecoder decoder, int width, int height, double[] delays)
    {
        _wic = wic;
        _decoder = decoder;
        _canvasWidth = width;
        _canvasHeight = height;
        _canvas = new uint[width * height];
        Delays = delays;
    }

    /// <summary>Each frame's delay in seconds (at least 0.02; 0.1 when the GIF doesn't say).</summary>
    public IReadOnlyList<double> Delays { get; }

    public int FrameCount => Delays.Count;

    /// <summary>Seconds one pass lasts.</summary>
    public double Duration => Delays.Sum();

    /// <summary>The GIF at <paramref name="path"/> when it has more than one frame; null for a still or a file WIC can't read.</summary>
    public static AnimatedPicture? Open(string path)
    {
        IWICImagingFactory? wic = null;
        IWICBitmapDecoder? decoder = null;
        try
        {
            wic = new IWICImagingFactory();
            decoder = wic.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnDemand);
            if (decoder.ContainerFormat != ContainerFormatGuids.Gif || decoder.FrameCount < 2)
            {
                decoder.Dispose();
                wic.Dispose();
                return null;
            }
            int width = 0, height = 0;
            using (var global = decoder.MetadataQueryReader)
            {
                width = (int)(CardImages.GetUnsigned(global.NativePointer, "/logscrdesc/Width") ?? 0);
                height = (int)(CardImages.GetUnsigned(global.NativePointer, "/logscrdesc/Height") ?? 0);
            }
            var delays = new double[decoder.FrameCount];
            for (var i = 0; i < delays.Length; i++)
            {
                using var frame = decoder.GetFrame((uint)i);
                if (width <= 0 || height <= 0)
                {
                    width = (int)frame.Size.Width;
                    height = (int)frame.Size.Height;
                }
                using var reader = frame.MetadataQueryReader;
                var hundredths = CardImages.GetUnsigned(reader.NativePointer, "/grctlext/Delay");
                delays[i] = Math.Max(0.02, hundredths is { } d && d > 0 ? d / 100.0 : 0.1);
            }
            return new AnimatedPicture(wic, decoder, width, height, delays);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            decoder?.Dispose();
            wic?.Dispose();
            return null;
        }
    }

    /// <summary>The frame showing <paramref name="time"/> seconds in, looping.</summary>
    public CoverArt FrameAt(double time)
    {
        var duration = Duration;
        var position = duration > 0 ? ((time % duration) + duration) % duration : 0;
        var index = 0;
        double elapsed = 0;
        for (var i = 0; i < Delays.Count; i++)
        {
            elapsed += Delays[i];
            if (position < elapsed)
            {
                index = i;
                break;
            }
        }
        return Frame(index);
    }

    /// <summary>Frame <paramref name="index"/> composed, as premultiplied BGRA at most <see cref="MaxSide"/> on its longer side.</summary>
    public CoverArt Frame(int index)
    {
        index = Math.Clamp(index, 0, FrameCount - 1);
        if (_last is { } last && last.Index == index) return last.Art;
        if (index < _next)
        {
            Array.Clear(_canvas);
            _saved = null;
            _pendingDisposal = null;
            _next = 0;
        }
        while (_next <= index) Compose(_next++);
        var art = Scaled();
        _last = (index, art);
        return art;
    }

    private void Compose(int index)
    {
        using var frame = _decoder.GetFrame((uint)index);
        uint left, top, disposal;
        using (var reader = frame.MetadataQueryReader)
        {
            left = CardImages.GetUnsigned(reader.NativePointer, "/imgdesc/Left") ?? 0;
            top = CardImages.GetUnsigned(reader.NativePointer, "/imgdesc/Top") ?? 0;
            disposal = CardImages.GetUnsigned(reader.NativePointer, "/grctlext/Disposal") ?? 0;
        }
        // The previous frame's disposal clears the way for this one.
        if (_pendingDisposal is { } dispose)
        {
            if (dispose.Kind == 2)
            {
                for (var y = Math.Max(0, dispose.Top); y < Math.Min(_canvasHeight, dispose.Top + dispose.Height); y++)
                    for (var x = Math.Max(0, dispose.Left); x < Math.Min(_canvasWidth, dispose.Left + dispose.Width); x++)
                        _canvas[y * _canvasWidth + x] = 0;
            }
            else if (dispose.Kind == 3 && _saved is not null)
            {
                Array.Copy(_saved, _canvas, _canvas.Length);
            }
            _pendingDisposal = null;
        }
        if (disposal == 3) _saved = (uint[])_canvas.Clone();
        using var converter = _wic.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
        int w = (int)frame.Size.Width, h = (int)frame.Size.Height;
        var pixels = new uint[w * h];
        fixed (uint* data = pixels) converter.CopyPixels((uint)(w * 4), (uint)(pixels.Length * 4), (nint)data);
        for (var y = 0; y < h; y++)
        {
            var cy = (int)top + y;
            if (cy < 0 || cy >= _canvasHeight) continue;
            for (var x = 0; x < w; x++)
            {
                var cx = (int)left + x;
                if (cx < 0 || cx >= _canvasWidth) continue;
                var source = pixels[y * w + x];
                if (source >> 24 == 0) continue; // transparent: what was there shows through
                _canvas[cy * _canvasWidth + cx] = source;
            }
        }
        _pendingDisposal = (disposal, (int)left, (int)top, w, h);
    }

    // A frame's disposal takes effect before the next frame is drawn.
    private (uint Kind, int Left, int Top, int Width, int Height)? _pendingDisposal;

    private CoverArt Scaled()
    {
        var fit = Math.Min(1, MaxSide / (double)Math.Max(_canvasWidth, _canvasHeight));
        int w = Math.Max(1, (int)Math.Round(_canvasWidth * fit)), h = Math.Max(1, (int)Math.Round(_canvasHeight * fit));
        var bytes = new byte[w * h * 4];
        fixed (uint* canvas = _canvas)
        {
            using var bitmap = _wic.CreateBitmapFromMemory((uint)_canvasWidth, (uint)_canvasHeight, PixelFormat.Format32bppPBGRA,
                (uint)(_canvasWidth * 4), (uint)(_canvas.Length * 4), canvas);
            if (w == _canvasWidth && h == _canvasHeight)
            {
                fixed (byte* data = bytes) bitmap.CopyPixels((uint)(w * 4), (uint)bytes.Length, (nint)data);
            }
            else
            {
                using var scaler = _wic.CreateBitmapScaler();
                scaler.Initialize(bitmap, (uint)w, (uint)h, BitmapInterpolationMode.HighQualityCubic);
                fixed (byte* data = bytes) scaler.CopyPixels((uint)(w * 4), (uint)bytes.Length, (nint)data);
            }
        }
        return new CoverArt(w, h, bytes);
    }

    public void Dispose()
    {
        _decoder.Dispose();
        _wic.Dispose();
    }
}

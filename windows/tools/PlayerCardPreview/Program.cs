// PlayerCardPreview [out-folder] [-warp] [-prefix name-]: the desktop player's card drawn offscreen by the app's own
// CardPainter, for looking at it without showing a window: every look (dark, light, Windows' contrast colours,
// transparency off, animation effects off) and the states that draw differently (scrubbing with the capsules, search,
// the queue, the music video's align mode, a note, a live stream, long names, keyboard focus). Each card sits over an
// invented wallpaper that is bright on the left and dark on the right, clipped to the 8 px corners DWM gives the
// window, with its 1 px border drawn as DWM would (approximated for the system's default colour). Writes into
// docs/previews/player by default. Uses the GPU (WARP with -warp).
// The contrast card takes the system's current colours (a contrast theme is never switched on for a preview).

using System.Globalization;
using System.Numerics;
using Lunalyr.App.Player;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Core.Player;
using Lunalyr.Platform.Media;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var warp = args.Contains("-warp");
var prefixAt = Array.IndexOf(args, "-prefix");
var prefix = prefixAt >= 0 && prefixAt + 1 < args.Length ? args[prefixAt + 1] : "card-";
var output = Path.GetFullPath(args.Where((a, i) => !a.StartsWith('-') && (prefixAt < 0 || i != prefixAt + 1)).FirstOrDefault()
                              ?? Path.Combine("docs", "previews", "player"));
Directory.CreateDirectory(output);

const float Scale = 2, Margin = 40;
using var stage = new Stage(warp);
Console.WriteLine($"Adapter: {stage.AdapterName}{(stage.IsWarp ? " (WARP)" : "")}");

var pink = Covers.Paint((0.93, 0.36, 0.56), (0.98, 0.62, 0.42), (0.42, 0.16, 0.48), (0.86, 0.30, 0.40), (1.0, 0.86, 0.70));
var teal = Covers.Paint((0.10, 0.52, 0.56), (0.36, 0.78, 0.70), (0.04, 0.20, 0.32), (0.12, 0.40, 0.52), (0.80, 0.96, 0.90));
var pale = Covers.Paint((0.96, 0.94, 0.86), (0.99, 0.97, 0.90), (0.88, 0.86, 0.74), (0.95, 0.90, 0.78), (1.0, 1.0, 0.96));
const double Duration = 213, Elapsed = 83;
var lines = Lyrics.Lines(Duration);
var context = new ScrubController.Context(Duration, "demo", lines, canSeek: true);

CardModel Song(CoverArt? cover, PlayerLook look, Action<CardModel>? more = null)
{
    var palette = cover is null ? ArtworkPalette.Neutral : ArtworkPalette.Of(Covers.Pixels(cover));
    var m = new CardModel
    {
        Look = look,
        Theme = CardTheme.For(palette, look),
        HasSong = true,
        Title = "Evening Tide",
        Artist = "Mira Sol",
        Album = "Harbour Lights",
        PlayerName = "Spotify",
        Cover = cover,
        DisplayedPlaying = true,
        RowKind = ScrubRowKind.Seekable,
        Elapsed = Elapsed,
        Duration = Duration,
        Scrub = context,
        RecordOut = 1,
        Thickness = 4,
        FocusPart = CardPart.PlayPause,
    };
    more?.Invoke(m);
    return m;
}

void Scrubbing(CardModel m)
{
    var at = 30.0;
    m.Active = 1;
    m.Thickness = 7;
    m.Capsules = 1;
    m.CapsuleTime = at;
    m.CapsuleX = ScrubMath.X(at, Duration);
    m.TitleAlpha = (float)PlayerCard.TitleOpacity(true);
    m.Hover = new ScrubController.Hover(m.CapsuleX, 0, at, null);
}

void Searching(CardModel m)
{
    m.Pane = PlayerPane.Search;
    m.Pickers = ["Spotify", "Apple Music"];
    m.PickerSelected = 0;
    m.Query = "evening";
    m.FieldFocused = true;
    m.SearchPlaceholder = "Search Spotify";
    var target = new PlayerSearchTarget("demo:1");
    m.Results =
    [
        new("1", PlayerSearchResultKind.Song, "Evening Tide", "Mira Sol", "Harbour Lights", null, target, InLibrary: true),
        new("2", PlayerSearchResultKind.Album, "Evening Songs", "The Low Hours", "Evening Songs", null, target),
        new("3", PlayerSearchResultKind.Song, "Evening Bell", "Kai Arden", "Northbound", null, target),
        new("4", PlayerSearchResultKind.Song, "Every Evening", "Paper Moons", "Paper Moons", null, target),
    ];
    m.Selection = 0;
    m.HoverPart = CardParts.Result(2);
}

void Queueing(CardModel m)
{
    m.Pane = PlayerPane.Queue;
    m.ShuffleOn = true;
    m.Queue =
    [
        new("a", "Low Light", "Mira Sol", 1, PersistentId: "a"),
        new("b", "Evening Tide", "Mira Sol", 2, IsCurrent: true, PersistentId: "b"),
        new("c", "Northbound", "Kai Arden", 3, PersistentId: "c"),
        new("d", "Paper Moons", "Paper Moons", 4, PersistentId: "d"),
        new("e", "Radio Hours", "The Low Hours", 5),
        new("f", "Harbour Lights", "Mira Sol", 6, PersistentId: "f"),
    ];
    m.QueueRows = m.Queue.Count;
    m.HoverPart = CardParts.QueueRow(2);
}

var dark = new PlayerLook();
var light = new PlayerLook(Light: true);
var shots = new (string Name, CardModel Model)[]
{
    ("dark", Song(pink, dark)),
    ("light", Song(pink, light)),
    ("contrast", Song(pink, new PlayerLook(HighContrast: true))),
    ("opaque", Song(pink, new PlayerLook(ReduceTransparency: true))),
    ("reduce-motion", Song(teal, new PlayerLook(ReduceMotion: true), m => { m.DisplayedPlaying = false; m.Active = 1; m.Thickness = 7; m.HoverPart = CardPart.SearchButton; })),
    ("paused", Song(teal, dark, m => { m.DisplayedPlaying = false; m.RecordOut = 0; })),
    ("pale-cover", Song(pale, dark)),
    ("pale-cover-light", Song(pale, light)),
    ("no-cover", Song(null, dark, m => { m.Title = "Nothing playing"; m.Artist = null; m.Album = null; m.HasSong = false; m.RowKind = ScrubRowKind.Hidden; m.PlayerName = ""; })),
    ("scrub-dark", Song(pink, dark, Scrubbing)),
    ("scrub-light", Song(pink, light, Scrubbing)),
    ("scrub-contrast", Song(pink, new PlayerLook(HighContrast: true), Scrubbing)),
    ("search-dark", Song(pink, dark, Searching)),
    ("search-light", Song(pink, light, Searching)),
    ("search-contrast", Song(pink, new PlayerLook(HighContrast: true), Searching)),
    ("queue-dark", Song(teal, dark, Queueing)),
    ("queue-light", Song(teal, light, Queueing)),
    ("align-dark", Song(pink, dark, m =>
    {
        m.MusicVideoOffered = true;
        m.Align = new MusicVideoAlignDisplay(240, Duration, 3.4, 62, MusicVideoOffsetOrigin.Estimated, Touched: true);
        m.TitleAlpha = (float)PlayerCard.TitleOpacity(true); // as DesktopPlayer.UpdateAlign steps the song aside
    })),
    ("commands-off", Song(pink, dark, m =>
    {
        m.CommandsOff = true;
        m.Note = "Controls are off for this run; scrubbing only previews.";
        m.NoteAlpha = 1;
    })),
    ("live", Song(null, dark, m =>
    {
        m.Title = "Late Night Radio";
        m.Artist = "Harbour FM";
        m.Album = null;
        m.IsStream = true;
        m.RowKind = ScrubRowKind.Live;
    })),
    ("elapsed-only", Song(teal, dark, m => m.RowKind = ScrubRowKind.ElapsedOnly)),
    ("long-names", Song(teal, dark, m =>
    {
        m.Title = "The Song With the Very Long Title That Will Not Fit on One Line";
        m.Artist = "An Artist Whose Name Also Runs On, featuring Several Friends";
        m.Album = "A Deluxe Edition of an Album With an Unusually Long Name (Remastered)";
        m.MusicVideoOffered = true;
    })),
    ("keyboard-focus", Song(pink, dark, m => { m.FocusVisible = true; m.FocusPart = CardPart.SearchButton; m.MusicVideoOffered = true; })),
};

using var painter = new CardPainter();
foreach (var (name, model) in shots)
{
    var height = model.Pane == PlayerPane.Song ? CardPainter.Height : CardPainter.HeightFor(model.Pane, model.QueueRows);
    var path = Path.Combine(output, $"{prefix}{name}.png");
    stage.Render(painter, model, (float)PlayerCard.Width, height, Scale, Margin, path);
    Console.WriteLine($"{path}");
}

/// <summary>A hardware (or WARP) Direct2D context, the card drawn into its own bitmap and set over a wallpaper.</summary>
internal sealed class Stage : IDisposable
{
    private readonly ID3D11Device _d3d;
    private readonly ID3D11DeviceContext _context;
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _device;
    private readonly ID2D1DeviceContext _dc;

    public Stage(bool warp)
    {
        Vortice.Direct3D.FeatureLevel[] levels = [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1];
        ID3D11Device? d3d = null;
        ID3D11DeviceContext? context = null;
        if (warp || D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out d3d, out context).Failure)
        {
            D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out d3d, out context).CheckError();
            IsWarp = true;
        }

        _d3d = d3d!;
        _context = context!;
        using var dxgi = _d3d.QueryInterface<IDXGIDevice>();
        using (var adapter = dxgi.GetAdapter()) AdapterName = adapter.Description.Description;
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);
        _device = _factory.CreateDevice(dxgi);
        _dc = _device.CreateDeviceContext(DeviceContextOptions.None);
    }

    public bool IsWarp { get; }

    public string AdapterName { get; } = "";

    public unsafe void Render(CardPainter painter, CardModel model, float width, float height, float scale, float margin, string path)
    {
        var dpi = 96 * scale;
        var format = new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied);
        var cardPixels = new SizeI((int)MathF.Ceiling(width * scale), (int)MathF.Ceiling(height * scale));
        var fullW = width + 2 * margin;
        var fullH = height + 2 * margin;
        var full = new SizeI((int)MathF.Ceiling(fullW * scale), (int)MathF.Ceiling(fullH * scale));
        using var card = _dc.CreateBitmap(cardPixels, IntPtr.Zero, 0, new BitmapProperties1(format, dpi, dpi, BitmapOptions.Target));
        using var target = _dc.CreateBitmap(full, IntPtr.Zero, 0, new BitmapProperties1(format, dpi, dpi, BitmapOptions.Target));
        using var readback = _dc.CreateBitmap(full, IntPtr.Zero, 0, new BitmapProperties1(format, dpi, dpi, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        _dc.SetDpi(dpi, dpi);

        // The card, as the window's swap chain gets it.
        _dc.Target = card;
        _dc.BeginDraw();
        painter.Paint(_dc, _factory, model, width, height);
        _dc.EndDraw().CheckError();

        // The wallpaper, then the card over it in DWM's 8 px corners, then the window's 1 px border.
        _dc.Target = target;
        _dc.BeginDraw();
        _dc.Transform = Matrix3x2.Identity;
        DrawWallpaper(fullW, fullH);
        using var corners = _factory.CreateRoundedRectangleGeometry(new RoundedRectangle(new RawRectF(margin, margin, margin + width, margin + height), 8, 8));
        _dc.PushLayer(new LayerParameters1
        {
            ContentBounds = new RawRectF(margin, margin, margin + width, margin + height), GeometricMask = corners,
            MaskAntialiasMode = AntialiasMode.PerPrimitive, MaskTransform = Matrix3x2.Identity, Opacity = 1,
        }, null!);
        _dc.DrawBitmap(card, new RawRectF(margin, margin, margin + width, margin + height), 1, Vortice.Direct2D1.InterpolationMode.NearestNeighbor, null, null);
        _dc.PopLayer();
        if (BorderColour(model) is { } border)
        {
            using var brush = _dc.CreateSolidColorBrush(border);
            _dc.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(margin + 0.5f, margin + 0.5f, margin + width - 0.5f, margin + height - 0.5f), 8, 8), brush, 1);
        }

        _dc.EndDraw().CheckError();
        _dc.Target = null;

        readback.CopyFromBitmap(target).CheckError();
        var mapped = readback.Map(MapOptions.Read);
        var pixels = new byte[full.Width * full.Height * 4];
        try
        {
            for (var y = 0; y < full.Height; y++)
                new ReadOnlySpan<byte>((byte*)mapped.Bits + (long)y * mapped.Pitch, full.Width * 4).CopyTo(pixels.AsSpan(y * full.Width * 4));
        }
        finally
        {
            readback.Unmap();
        }

        File.WriteAllBytes(path, CardImages.EncodePng(pixels, full.Width, full.Height));
    }

    /// <summary>DWM's border as the card asks for it: none, the system's default (approximated) or a colour.</summary>
    private static Color4? BorderColour(CardModel model)
    {
        var border = model.Theme.Border;
        if (border == 0xFFFFFFFE) return null;
        if (border == 0xFFFFFFFF) return model.Look.Light ? new Color4(0, 0, 0, 0.16f) : new Color4(1, 1, 1, 0.14f);
        return new Color4((border & 0xFF) / 255f, ((border >> 8) & 0xFF) / 255f, ((border >> 16) & 0xFF) / 255f, 1);
    }

    /// <summary>An invented wallpaper: a bright half and a dark half, each with a lyric line across it.</summary>
    private void DrawWallpaper(float width, float height)
    {
        _dc.Clear(new Color4(0, 0, 0, 1));
        var half = width / 2;
        using var brightStops = _dc.CreateGradientStopCollection([new GradientStop(0, new Color4(1, 1, 1, 1)), new GradientStop(1, new Color4(0.86f, 0.90f, 0.97f, 1))]);
        using var bright = _dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, new Vector2(0, height)), brightStops);
        _dc.FillRectangle(new RawRectF(0, 0, half, height), bright);
        using var darkStops = _dc.CreateGradientStopCollection([new GradientStop(0, new Color4(0.10f, 0.08f, 0.20f, 1)), new GradientStop(1, new Color4(0.02f, 0.02f, 0.05f, 1))]);
        using var night = _dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(half, 0), new Vector2(width, height)), darkStops);
        _dc.FillRectangle(new RawRectF(half, 0, width, height), night);
        using var dwrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<Vortice.DirectWrite.IDWriteFactory>();
        using var format = dwrite.CreateTextFormat("Segoe UI Variable Display", null, Vortice.DirectWrite.FontWeight.SemiBold,
            Vortice.DirectWrite.FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, 64, "");
        using var ink = _dc.CreateSolidColorBrush(new Color4(0.15f, 0.15f, 0.2f, 0.85f));
        _dc.DrawText("hold me through", format, new RawRectF(8, height * 0.22f, half, height), ink);
        ink.Color = new Color4(1, 1, 1, 0.85f);
        _dc.DrawText("the night", format, new RawRectF(half + 24, height * 0.52f, width, height), ink);
    }

    public void Dispose()
    {
        _dc.Dispose();
        _device.Dispose();
        _factory.Dispose();
        _context.Dispose();
        _d3d.Dispose();
    }
}

/// <summary>Invented covers painted in code: four corner colours blended across the square and a soft glowing disc.</summary>
internal static class Covers
{
    public static CoverArt Paint((double R, double G, double B) topLeft, (double R, double G, double B) topRight,
        (double R, double G, double B) bottomLeft, (double R, double G, double B) bottomRight, (double R, double G, double B) glow, int side = 240)
    {
        var pixels = new byte[side * side * 4];
        for (var row = 0; row < side; row++)
        {
            var y = (row + 0.5) / side;
            for (var column = 0; column < side; column++)
            {
                var x = (column + 0.5) / side;
                double Mix(double a, double b, double c, double d) => (a * (1 - x) + b * x) * (1 - y) + (c * (1 - x) + d * x) * y;
                var r = Mix(topLeft.R, topRight.R, bottomLeft.R, bottomRight.R);
                var g = Mix(topLeft.G, topRight.G, bottomLeft.G, bottomRight.G);
                var b = Mix(topLeft.B, topRight.B, bottomLeft.B, bottomRight.B);
                var dx = x - 0.62;
                var dy = y - 0.4;
                var disc = Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy) / 0.28, 0, 1);
                disc *= disc;
                r += (glow.R - r) * disc;
                g += (glow.G - g) * disc;
                b += (glow.B - b) * disc;
                var i = (row * side + column) * 4;
                pixels[i] = (byte)Math.Round(Math.Clamp(b, 0, 1) * 255);
                pixels[i + 1] = (byte)Math.Round(Math.Clamp(g, 0, 1) * 255);
                pixels[i + 2] = (byte)Math.Round(Math.Clamp(r, 0, 1) * 255);
                pixels[i + 3] = 255;
            }
        }

        return new CoverArt(side, side, pixels);
    }

    /// <summary>The cover as straight RGBA, as the player reads it for its colours.</summary>
    public static ArtworkPixels Pixels(CoverArt cover)
    {
        var rgba = new byte[cover.Width * cover.Height * 4];
        var source = cover.Pixels;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = source[i + 2];
            rgba[i + 1] = source[i + 1];
            rgba[i + 2] = source[i];
            rgba[i + 3] = source[i + 3];
        }

        return new ArtworkPixels(cover.Width, cover.Height, rgba);
    }
}

/// <summary>An invented synced lyric for the seek bar's ticks, sections and capsules.</summary>
internal static class Lyrics
{
    public static ScrubLine[] Lines(double duration)
    {
        string[] words =
        [
            "Hold me through the night", "The harbour lights are low", "Every evening tide", "Carries me back home",
            "Hold me through the night", "The harbour lights are low", "And the water knows", "Where the evening goes",
        ];
        var lines = new List<ScrubLine>();
        var t = 14.0;
        for (var verse = 0; verse < 2; verse++)
        {
            foreach (var text in words)
            {
                lines.Add(new ScrubLine(t, t + 4.2, text, null, false));
                t += 4.6;
            }

            lines.Add(new ScrubLine(t, t + 18, "", null, true));
            t += 24;
        }

        return lines.Where(l => l.End < duration).ToArray();
    }
}

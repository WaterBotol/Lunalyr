// The contract between the render engine and what it draws. The engine owns devices, swap chains, monitors and
// frame pacing; layers (one background, one lyric composition, later visualisers, credits, interludes) only draw.
// Everything a layer needs each frame arrives in FrameContext and a SceneSnapshot; layers never read settings,
// players or the network themselves, so they can be tested and previewed with made-up scenes.

using System.Numerics;
using Lunalyr.Core.Lyrics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Scene;

/// <summary>Album colours for the current track (or the default palette when there's no artwork).</summary>
/// <param name="Base">Darkest, dominant background colour, linear-light premultiplied-friendly sRGB 0..1.</param>
/// <param name="Accents">2–5 accent colours from the artwork, most prominent first.</param>
/// <param name="Text">Lyric text colour chosen for contrast against the palette.</param>
/// <param name="Highlight">Colour for the sung/active word highlight.</param>
public sealed record Palette(Vector4 Base, IReadOnlyList<Vector4> Accents, Vector4 Text, Vector4 Highlight)
{
    /// <summary>
    /// The cover's nine colours (3×3 grid, row by row, gamma-encoded sRGB), which the compositions' lyric ink drifts
    /// through (the Mac's Album drift). Empty for a palette not made from a cover: the ink then uses its own pastels.
    /// </summary>
    public IReadOnlyList<Vector3> ArtworkStops { get; init; } = [];

    /// <summary>
    /// The relative luminance the lyric ink is made readable against: the background behind the stage, its scrim and
    /// the text backing (see <c>LyricBackdrop</c>).
    /// </summary>
    public double TextBackdrop { get; init; } = 0.08;

    public static Palette Default { get; } = new(
        new Vector4(0.106f, 0.063f, 0.208f, 1),
        [new(0.482f, 0.247f, 0.894f, 1), new(0.894f, 0.341f, 0.180f, 1), new(0.180f, 0.525f, 0.671f, 1), new(0.949f, 0.757f, 0.306f, 1)],
        new Vector4(1, 1, 1, 1),
        new Vector4(1, 1, 1, 1));
}

/// <summary>What is playing and what to show, as one immutable snapshot. The app swaps in a new one on change.</summary>
public sealed record SceneSnapshot
{
    public string? Title { get; init; }
    public string? Artist { get; init; }

    /// <summary>The chosen lyrics, or null while looking up / when none were found.</summary>
    public LyricResult? Lyrics { get; init; }

    /// <summary>
    /// Readings per line index (romanisation), one string per line, when shown; empty when off. Kept for layers that
    /// draw a reading per line; <see cref="WordReadings"/> has the same readings word by word.
    /// </summary>
    public IReadOnlyDictionary<int, string> Readings { get; init; } = new Dictionary<int, string>();

    /// <summary>
    /// Readings per line index as <c>LyricRomanizer.Readings</c> works them out (the Mac's <c>romanized</c>), made for
    /// <see cref="LyricStyle.Readings"/>: with <see cref="ReadingPlacement.Below"/> one reading per word of
    /// <c>line.Words</c> (or a single word spanning the line, for a reading that cannot be split by word); with
    /// <see cref="ReadingPlacement.Instead"/> the words to show in the line's place. Empty when off or not worked out
    /// yet; a line missing here falls back to <see cref="Readings"/>. <c>Lunalyr.Core.Compositions.LineReadings</c>
    /// and <c>RollingSong</c> turn an entry into what a stage draws.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<LyricWord>> WordReadings { get; init; } = new Dictionary<int, IReadOnlyList<LyricWord>>();

    public Palette Palette { get; init; } = Palette.Default;

    /// <summary>The composition's own settings (font, size, reveal style…), opaque to the engine.</summary>
    public LyricStyle Style { get; init; } = LyricStyle.Default;

    /// <summary>Calm status text to show instead of lyrics ("No lyrics found", "Offline"…), or null.</summary>
    public string? Status { get; init; }

    /// <summary>
    /// The track's length in seconds, when the player gives one: an instrumental break after the last line then shows
    /// how far it has to go, as other breaks do. Null when unknown (the break is open-ended).
    /// </summary>
    public double? Duration { get; init; }

    /// <summary>
    /// The system asks for less motion (Windows' "Animation effects" turned off; the Mac's Reduce Motion). Every layer
    /// honours it as the Mac does: entrances, swells, drifts and travel become plain fades, layout steps short
    /// dissolves, the lyric colours drift ten times slower, the colour field's ambient motion runs at 15% and the
    /// colour-only gradient holds still. Read from the system by the app, never changed by it; false by default.
    /// </summary>
    public bool ReduceMotion { get; init; }
}

/// <summary>Settings shared by the lyric compositions. Each composition reads what applies to it.</summary>
public sealed record LyricStyle
{
    public static LyricStyle Default { get; } = new();

    /// <summary>Font family for lyrics (a system family name).</summary>
    public string FontFamily { get; init; } = "Segoe UI Variable Display";

    /// <summary>Lyric size relative to the monitor's height (the Mac's slider maps onto this).</summary>
    public float SizeFraction { get; init; } = 0.075f;

    /// <summary>Reveal style, as the Mac's `WordArrival` raw values: "automatic", "fade" (Soft focus), "lift" (Float up), "pop", "orbit", "glide", "unfold", "cutoutFold", "cutoutSlide", "cutoutHighlight", "satinRibbon", "splitInk", "mix".</summary>
    public string Reveal { get; init; } = "fade";

    /// <summary>Show readings below the lyric, instead of it, or not at all.</summary>
    public ReadingPlacement Readings { get; init; } = ReadingPlacement.Below;

    /// <summary>
    /// Show the song's credits on the wallpaper: its "Title - Artist" as it starts, then any production credits that
    /// came with the lyrics, one after another in a band at the top or foot of the stage (the Mac's credits layer,
    /// <c>custom.creditsLayer</c>). Off by default here so scenes made without it draw as before; the app's setting
    /// defaults to on, as the Mac's does.
    /// </summary>
    public bool Credits { get; init; }

    /// <summary>Three dots during instrumental breaks (the Mac's <c>custom.showInterludes</c>).</summary>
    public bool Interludes { get; init; } = true;

    /// <summary>
    /// Where highlight tiles take their colour from, as the Mac's <c>LyricHighlightMode</c> raw values: "complement"
    /// (opposite the lyric colour; the default), "album" (the cover's richest colour) or "custom"
    /// (<see cref="HighlightCustom"/>). Tiles are drawn with the "cutoutHighlight" reveal and behind the artist's name in
    /// the opening credit.
    /// </summary>
    public string HighlightMode { get; init; } = "complement";

    /// <summary>The custom highlight colour as <c>#RRGGBB</c> (the Mac's <c>custom.lyricHighlightCustom</c>).</summary>
    public string HighlightCustom { get; init; } = "#FF92A3";
}

public enum ReadingPlacement { Off, Below, Instead }

/// <summary>Per-frame inputs for a layer. Valid only during <see cref="IRenderLayer.Render"/>.</summary>
public readonly struct FrameContext
{
    public required ID2D1DeviceContext Dc { get; init; }
    public required IDWriteFactory DWrite { get; init; }
    public required ID2D1Factory1 D2DFactory { get; init; }

    /// <summary>Surface size in physical pixels (the device context draws at 96 DPI = 1 unit per pixel).</summary>
    public required Vector2 Size { get; init; }

    /// <summary>Monitor DPI scale (1.0 at 96 DPI). Use it for anything specified in "points" (blur radii, strokes).</summary>
    public required float Scale { get; init; }

    /// <summary>Monotonic wall-clock seconds since the engine started (for ambient motion).</summary>
    public required double WallSeconds { get; init; }

    /// <summary>Song position in seconds, offsets already applied (global, per-song). NaN when nothing is playing.</summary>
    public required double SongSeconds { get; init; }

    public required bool IsPlaying { get; init; }

    public required SceneSnapshot Scene { get; init; }

    /// <summary>Seconds since the previous frame on this surface (clamped to 0..0.25).</summary>
    public required double DeltaSeconds { get; init; }

    /// <summary>
    /// The engine's allowance for work done ahead of time (one piece per render pass across all surfaces). Null when
    /// rendering offscreen (previews, tests): then ahead-of-time work is always allowed.
    /// </summary>
    public Pacing.AheadWork? AheadWork { get; init; }
}

/// <summary>
/// Something drawn on every wallpaper surface each frame. One instance per surface (per monitor), created by an
/// <see cref="IRenderLayerFactory"/>. All calls come from the render thread.
/// </summary>
public interface IRenderLayer : IDisposable
{
    /// <summary>Create device resources (brushes, text formats). Called after device creation or loss.</summary>
    void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale);

    /// <summary>The surface changed size or DPI.</summary>
    void Resize(Vector2 size, float scale);

    /// <summary>Draw one frame. Must not throw for bad data; log and draw nothing instead.</summary>
    void Render(in FrameContext frame);

    /// <summary>Release device resources (device lost or surface going away). Dispose also calls this.</summary>
    void ReleaseResources();

    /// <summary>True while the layer is animating and needs frames even when nothing else changes.</summary>
    bool IsAnimating { get; }
}

/// <summary>Creates a layer for one surface. Registered by id, e.g. "gradient", "editorial", "spoken".</summary>
public interface IRenderLayerFactory
{
    string Id { get; }
    IRenderLayer Create();
}

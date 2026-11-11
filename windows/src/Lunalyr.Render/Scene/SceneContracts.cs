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

    /// <summary>Readings per line index (romanisation), when shown; empty when off.</summary>
    public IReadOnlyDictionary<int, string> Readings { get; init; } = new Dictionary<int, string>();

    public Palette Palette { get; init; } = Palette.Default;

    /// <summary>The composition's own settings (font, size, reveal style…), opaque to the engine.</summary>
    public LyricStyle Style { get; init; } = LyricStyle.Default;

    /// <summary>Calm status text to show instead of lyrics ("No lyrics found", "Offline"…), or null.</summary>
    public string? Status { get; init; }
}

/// <summary>Settings shared by the lyric compositions. Each composition reads what applies to it.</summary>
public sealed record LyricStyle
{
    public static LyricStyle Default { get; } = new();

    /// <summary>Font family for lyrics (a system family name).</summary>
    public string FontFamily { get; init; } = "Segoe UI Variable Display";

    /// <summary>Lyric size relative to the monitor's height (the Mac's slider maps onto this).</summary>
    public float SizeFraction { get; init; } = 0.075f;

    /// <summary>Reveal style id, e.g. "softFocus", "floatUp", "pop"… (the Mac's raw values).</summary>
    public string Reveal { get; init; } = "softFocus";

    /// <summary>Show readings below the lyric, instead of it, or not at all.</summary>
    public ReadingPlacement Readings { get; init; } = ReadingPlacement.Below;
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

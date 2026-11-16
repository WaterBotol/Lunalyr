// The floating lyrics window over a stand-in desktop (an app window and Windows' taskbar), for each preset place on
// this PC's two displays, drawn by the engine's own FloatingFrameDrawer and lyric layers (the Mac's
// FloatingLyricsPreviewTests). Nothing plays, nothing is fetched, no window opens.

using System.Globalization;
using System.Numerics;
using Lunalyr.Core.Floating;
using Lunalyr.Render.Floating;
using Lunalyr.Render.Scene;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Box = Lunalyr.Core.Motion.Box;

namespace Lunalyr.Tools.CompositionPreview;

public static class FloatingShots
{
    /// <summary>A display: its pixels, scale and a bottom taskbar of 48 points.</summary>
    public sealed record Screen(string Name, int Width, int Height, float Scale)
    {
        public FloatingDisplay Display => new(new Box(0, 0, Width, Height), new Box(0, 0, Width, Height - Math.Round(48 * Scale)), Scale);
    }

    public sealed record Shot(string Name, string Fixture, double Time, double TextSize = 44, FloatingPlacement Placement = FloatingPlacement.Bottom,
                              bool Dark = false, bool Editing = false, bool Reduce = false, double Backdrop = 0.6, bool Placeholder = false);

    public static void Run(string output, IRenderLayerFactory lyricFactory, IReadOnlyDictionary<string, Func<SceneSnapshot>> fixtures,
                           bool warp, string? onlyScreen)
    {
        // The external display, the built-in panel at the 200% the brief names, and at the 125% this PC runs it at.
        Screen[] screens = [new("2560x1440-125", 2560, 1440, 1.25f), new("3200x2000-200", 3200, 2000, 2f), new("3200x2000-125", 3200, 2000, 1.25f)];
        var shots = new List<Shot>();
        // Every preset place, light app behind, medium text.
        foreach (var placement in FloatingPlacements.Presets)
            shots.Add(new Shot($"place-{placement.Raw()}", "english", 12.9, Placement: placement, Dark: placement is FloatingPlacement.TopRight or FloatingPlacement.Centre));
        shots.Add(new Shot("small-bottom-light", "english", 12.9, TextSize: 30));
        shots.Add(new Shot("large-bottom-light", "english", 12.9, TextSize: 64));
        shots.Add(new Shot("medium-reading", "japanesewords", 12.8));
        shots.Add(new Shot("medium-centre-edit", "english", 12.9, Placement: FloatingPlacement.Centre, Editing: true));
        shots.Add(new Shot("medium-edit-nothing-playing", "nolyrics", 0, Editing: true, Placeholder: true, Dark: true));
        shots.Add(new Shot("medium-reduce-transparency", "english", 12.9, Reduce: true));
        shots.Add(new Shot("medium-no-shade", "english", 12.9, Backdrop: 0));
        // The opening title through its moments, over a dark app (the Mac's OpeningTitlePreviewTests f-floating-* frames).
        foreach (var (label, time) in OpeningFixtures.CardMoments(OpeningFixtures.HumanNature()))
            shots.Add(new Shot($"opening-{label}", "opening", time, Dark: true));
        // Long after: only a kept title (-style keep=on) is still there, a row above the lyrics.
        shots.Add(new Shot("opening-6-late", "opening", 15.2, Dark: true));
        shots.Add(new Shot("opening-6-late-light", "opening", 15.2));

        foreach (var screen in screens)
        {
            if (onlyScreen is not null && !screen.Name.StartsWith(onlyScreen, StringComparison.Ordinal)) continue;
            using var stage = new Offscreen(screen.Width, screen.Height, warp);
            foreach (var shot in shots)
            {
                if (!fixtures.TryGetValue(shot.Fixture, out var make)) { Console.WriteLine($"No fixture {shot.Fixture}"); continue; }
                Render(stage, screen, shot, make(), lyricFactory);
                var path = Path.Combine(output, $"{screen.Name}-{shot.Name}.png");
                stage.SavePng(path);
                Console.WriteLine(path);
            }
        }
    }

    private static void Render(Offscreen stage, Screen screen, Shot shot, SceneSnapshot scene, IRenderLayerFactory factory)
    {
        var dc = stage.Dc;
        var display = screen.Display;
        var rect = display.Place(shot.Placement, null, null, shot.TextSize);
        var size = new Vector2((float)rect.Width, (float)rect.Height);
        var format = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var window = dc.CreateBitmap(new SizeI((int)rect.Width, (int)rect.Height), IntPtr.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
        using var layer = factory.Create();
        layer.CreateResources(dc, stage.DWrite, size, screen.Scale);
        using var drawer = new FloatingFrameDrawer();
        drawer.Prepare(dc, stage.DWrite, size, screen.Scale);
        var floatingScene = FloatingFrameDrawer.SceneFor(scene, shot.TextSize, shot.Backdrop, shot.Reduce);
        var accent = new Vector4(0, 0.47f, 0.83f, 1); // Windows' default accent

        // Let entrances and colours settle as the clock runs (the Mac's 24 steps of 0.05 s).
        var start = shot.Time - 1.2;
        for (var step = 0; step <= 24; step++)
        {
            var time = start + step * 0.05;
            if (step == 24) time = shot.Time;
            dc.Target = stage.Target;
            dc.BeginDraw();
            dc.Transform = Matrix3x2.Identity;
            FakeDesktop(dc, stage, screen, shot.Dark);
            var frame = new FrameContext
            {
                Dc = dc, DWrite = stage.DWrite, D2DFactory = stage.Factory, Size = size, Scale = screen.Scale, WallSeconds = 10 + time,
                SongSeconds = time, IsPlaying = scene.Title is not null, Scene = floatingScene, DeltaSeconds = 0.05,
            };
            drawer.BeginStage(dc);
            if (!shot.Placeholder) layer.Render(frame);
            FloatingFrameDrawer.EndStage(dc, window);
            dc.Clear(new Color4(0, 0, 0, 0));
            drawer.Compose(dc, stage.DWrite, 1, shot.Backdrop, shot.Reduce, hasStage: !shot.Placeholder, shot.Editing, shot.Placeholder, accent);
            dc.Target = stage.Target;
            dc.Transform = Matrix3x2.Identity;
            dc.DrawBitmap(window, new RawRectF((float)rect.X, (float)rect.Y, (float)(rect.X + rect.Width), (float)(rect.Y + rect.Height)), 1,
                InterpolationMode.NearestNeighbor, null, null);
            Caption(dc, stage, screen, $"{shot.Name} · {screen.Name} · window {rect.Width:0}×{rect.Height:0} at {rect.X:0},{rect.Y:0}");
            dc.EndDraw().CheckError();
            dc.Target = null;
        }
    }

    /// <summary>A stand-in desktop: a wallpaper, a document app's window and Windows' taskbar.</summary>
    private static void FakeDesktop(ID2D1DeviceContext dc, Offscreen stage, Screen screen, bool dark)
    {
        float w = screen.Width, h = screen.Height, s = screen.Scale;
        var taskbar = (float)Math.Round(48 * s);
        dc.Clear(new Color4(0.25f, 0.35f, 0.55f, 1));
        using var brush = dc.CreateSolidColorBrush(new Color4(0.55f, 0.4f, 0.5f, 0.6f));
        dc.FillEllipse(new Ellipse(new Vector2(w * 0.8f, h * 0.2f), w * 0.4f, h * 0.5f), brush);
        var page = dark ? new Color4(0.13f, 0.13f, 0.13f, 1) : new Color4(0.985f, 0.985f, 0.985f, 1);
        var chrome = dark ? new Color4(0.2f, 0.2f, 0.2f, 1) : new Color4(0.93f, 0.93f, 0.93f, 1);
        var ink = dark ? new Color4(0.75f, 0.75f, 0.75f, 0.3f) : new Color4(0.25f, 0.25f, 0.25f, 0.3f);
        // The app fills the work area (maximised).
        var app = new RawRectF(0, 0, w, h - taskbar);
        brush.Color = page;
        dc.FillRectangle(app, brush);
        brush.Color = chrome;
        dc.FillRectangle(new RawRectF(0, 0, w, 40 * s), brush);
        dc.FillRectangle(new RawRectF(0, 40 * s, 260 * s, h - taskbar), brush);
        brush.Color = ink;
        for (var row = 0; row < 30; row++)
        {
            var y = 110 * s + row * 26 * s;
            if (y > h - taskbar - 40 * s) break;
            if (row % 6 == 5) continue;
            dc.FillRectangle(new RawRectF(300 * s, y, 300 * s + (520 + (row * 131) % 560) * s, y + 11 * s), brush);
        }
        for (var row = 0; row < 14; row++)
            dc.FillRectangle(new RawRectF(24 * s, 70 * s + row * 30 * s, (24 + 90 + (row * 37) % 70) * s, 80 * s + row * 30 * s), brush);
        // The taskbar, with its icons in the middle.
        brush.Color = dark ? new Color4(0.12f, 0.12f, 0.14f, 1) : new Color4(0.9f, 0.92f, 0.95f, 1);
        dc.FillRectangle(new RawRectF(0, h - taskbar, w, h), brush);
        for (var i = 0; i < 9; i++)
        {
            var hue = i / 9f;
            brush.Color = new Color4(0.4f + 0.5f * MathF.Abs(MathF.Sin(hue * 6.28f)), 0.5f + 0.3f * MathF.Cos(hue * 6.28f), 0.7f, 1);
            var x = w / 2 + (i - 4) * 44 * s - 16 * s;
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x, h - taskbar + 8 * s, x + 32 * s, h - 8 * s), 6 * s, 6 * s), brush);
        }
    }

    private static void Caption(ID2D1DeviceContext dc, Offscreen stage, Screen screen, string text)
    {
        var s = screen.Scale;
        using var format = stage.DWrite.CreateTextFormat("Segoe UI Variable Text", null, Vortice.DirectWrite.FontWeight.SemiBold,
            Vortice.DirectWrite.FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, 13 * s, CultureInfo.InvariantCulture.Name);
        using var layout = stage.DWrite.CreateTextLayout(text, format, 2000 * s, 40 * s);
        using var brush = dc.CreateSolidColorBrush(new Color4(0, 0, 0, 0.7f));
        var width = layout.Metrics.WidthIncludingTrailingWhitespace + 20 * s;
        // Left of the middle: clear of every preset place.
        var top = screen.Height * 0.42f;
        var left = 300 * s;
        dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(left, top, left + width, top + 26 * s), 13 * s, 13 * s), brush);
        brush.Color = new Color4(1, 1, 1, 1);
        dc.DrawTextLayout(new Vector2(left + 10 * s, top + 4 * s), layout, brush, Vortice.Direct2D1.DrawTextOptions.None);
    }
}

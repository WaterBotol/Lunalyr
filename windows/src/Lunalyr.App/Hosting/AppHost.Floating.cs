using System.Runtime.InteropServices;
using Lunalyr.Platform.Desktop;
using Lunalyr.Shell;
using Lunalyr.Shell.Rendering;

namespace Lunalyr.App.Hosting;

/// <summary>The floating lyrics' app side: a drag places them for good, Done in the window ends edit position.</summary>
internal sealed partial class AppHost
{
    /// <summary>The displays the floating lyrics can go on, main display first (for the settings page).</summary>
    public static IReadOnlyList<DisplayChoice> FloatingDisplays()
    {
        try
        {
            return DisplayIdentity.Connected();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Edit position on or off (the settings page), only while the lyrics float.</summary>
    public void SetFloatingEditing(bool editing) => _controller?.SetFloatingEditing(editing);

    public bool FloatingEditing => _controller?.FloatingEditing ?? false;

    private void WireFloating(EngineWallpaperRenderer wallpaper, AppController controller)
    {
        var engine = wallpaper.Engine;
        // A drag ended: the frame is kept for that display and the place becomes Custom (the Mac's storeCustomFrame).
        // A resize belongs to the layout showing (tester feedback on 0.9.0): it keeps that size, its text scaled with the
        // height, while the place every layout shares moves to the window's new centre at its own size.
        engine.FloatingPlaced += (_, e) => PlaceFloating(e.DisplayKey, e.Relative, e.HeightRatio);
        engine.FloatingEditDone += (_, _) => controller.SetFloatingEditing(false);
    }

    /// <summary>
    /// The floating lyrics were moved or resized, in their window or by the handles in the Settings preview: the place is
    /// kept for the display (Custom), and a resize is kept for the layout showing, its text scaled with the height from
    /// <paramref name="baseText"/> (the drag's start; null for the text now).
    /// </summary>
    internal void PlaceFloating(string displayKey, Core.Floating.FloatingRect relative, double? heightRatio, double? baseText = null,
                                bool quiet = false)
    {
        if (_controller is not { } controller) return;
        _settings.Update(s =>
        {
            if (heightRatio is not { } ratio || controller.ShownComposition is not { Length: > 0 } layout)
                return s.WithCustomFrame(relative, displayKey);
            var shared = s.CustomFrame(displayKey) is { } frame
                ? new Core.Floating.FloatingLayoutSize(new Core.Compositions.SizeD(frame.Width, frame.Height), 0).Centred(relative)
                : relative;
            if (!quiet) _log.Info($"Floating lyrics: {layout} keeps {relative.Width:0}x{relative.Height:0} pt, text ×{ratio:0.00}.");
            return s.WithCustomFrame(shared, displayKey)
                .WithLayoutResized(layout, new Core.Compositions.SizeD(relative.Width, relative.Height), ratio, baseText);
        });
    }

    /// <summary><c>--floating-capture</c>: the window's back buffer as a PNG, and its styles in the log (read only).</summary>
    private void WriteFloatingCapture(string path)
    {
        if (_wallpaper?.Engine is not { } engine) return;
        nint hwnd = engine.FloatingWindow;
        if (hwnd != 0)
        {
            long ex = GetWindowLongPtr(hwnd, -20), style = GetWindowLongPtr(hwnd, -16);
            _log.Info($"Floating lyrics window {hwnd:X}: style 0x{style:X8}, ex-style 0x{ex:X8}, visible {IsWindowVisible(hwnd)}, "
                + $"foreground is it: {GetForegroundWindow() == hwnd}.");
        }
        try
        {
            var png = engine.CaptureFloatingAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, png);
            _log.Info($"Floating lyrics captured to {path}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"Couldn't capture the floating lyrics to {path}.", ex);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
}

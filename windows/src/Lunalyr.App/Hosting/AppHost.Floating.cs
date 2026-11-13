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
        engine.FloatingPlaced += (_, e) => _settings.Update(s => s.WithCustomFrame(e.Relative, e.DisplayKey));
        engine.FloatingEditDone += (_, _) => controller.SetFloatingEditing(false);
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

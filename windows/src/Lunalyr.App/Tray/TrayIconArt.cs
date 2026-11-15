using Lunalyr.App.Interop;
using Microsoft.Win32;

namespace Lunalyr.App.Tray;

/// <summary>
/// The placeholder tray icon, drawn in code: a monochrome crescent moon with a short lyric line beside it, white on a
/// dark taskbar and near-black on a light one. (Not from any other app's assets; the real icon comes later.)
/// </summary>
internal static class TrayIconArt
{
    /// <summary>Whether the taskbar is light (Windows' "SystemUsesLightTheme"; read only).</summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int light && light != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Creates an icon <paramref name="size"/> pixels square. The caller destroys it with <c>DestroyIcon</c>.</summary>
    public static unsafe nint Create(int size, bool lightTaskbar)
    {
        size = Math.Clamp(size, 16, 256);
        var pixels = Render(size, lightTaskbar ? (byte)0x1C : (byte)0xFF);
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Win32.BITMAPINFOHEADER),
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = Win32.CreateDIBSection(0, header, 0, out var bits, 0, 0);
        if (color == 0) return 0;
        fixed (uint* source = pixels) Buffer.MemoryCopy(source, (void*)bits, (long)size * size * 4, (long)size * size * 4);
        var maskBytes = new byte[((size + 15) / 16) * 2 * size]; // all zero: the alpha channel decides
        nint mask;
        fixed (byte* m = maskBytes) mask = Win32.CreateBitmap(size, size, 1, 1, (nint)m);
        var icon = Win32.CreateIconIndirect(new Win32.ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color });
        Win32.DeleteObject(color);
        Win32.DeleteObject(mask);
        return icon;
    }

    // Premultiplied BGRA, 4×4 supersampled coverage.
    private static uint[] Render(int size, byte grey)
    {
        var result = new uint[size * size];
        double s = size;
        // Crescent: a disc with a second disc taken out of its upper right.
        var cx = s * 0.40; var cy = s * 0.50; var r = s * 0.38;
        var ox = cx + r * 0.52; var oy = cy - r * 0.30; var or = r * 0.86;
        // A short horizontal bar, lower right: a line of lyrics.
        double barX0 = s * 0.62, barX1 = s * 0.94, barY0 = s * 0.72, barY1 = s * 0.82;
        const int n = 4;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var hits = 0;
                for (var j = 0; j < n; j++)
                {
                    for (var i = 0; i < n; i++)
                    {
                        var px = x + (i + 0.5) / n;
                        var py = y + (j + 0.5) / n;
                        var inMoon = Sq(px - cx) + Sq(py - cy) <= r * r && Sq(px - ox) + Sq(py - oy) > or * or;
                        var inBar = px >= barX0 && px <= barX1 && py >= barY0 && py <= barY1;
                        if (inMoon || inBar) hits++;
                    }
                }

                var alpha = (uint)Math.Round(255.0 * hits / (n * n));
                var c = (uint)Math.Round(grey * alpha / 255.0);
                result[y * size + x] = (alpha << 24) | (c << 16) | (c << 8) | c;
            }
        }

        return result;
    }

    private static double Sq(double v) => v * v;
}

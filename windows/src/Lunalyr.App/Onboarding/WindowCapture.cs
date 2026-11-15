using System.Runtime.InteropServices;
using Lunalyr.App.Interop;

namespace Lunalyr.App.Onboarding;

/// <summary>
/// <c>--onboarding-capture</c>: a window drawn into a bitmap with <c>PrintWindow(PW_RENDERFULLCONTENT)</c>, as
/// tools/capture-window.ps1 does from outside, so it works while the window sits unactivated under every other window.
/// Cropped to the visible frame (DWM's extended frame bounds) and written as an opaque PNG. For checks only.
/// </summary>
internal static partial class WindowCapture
{
    private const uint PwRenderFullContent = 2;
    private const int DwmwaExtendedFrameBounds = 9;

    /// <summary>Writes <paramref name="hwnd"/> to <paramref name="path"/>; returns its size, or null when it couldn't.</summary>
    public static unsafe (int Width, int Height)? WritePng(nint hwnd, string path)
    {
        if (!GetWindowRect(hwnd, out var window)) return null;
        var frame = DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var bounds, sizeof(Win32.RECT)) == 0 ? bounds : window;
        int width = window.Right - window.Left, height = window.Bottom - window.Top;
        if (width <= 0 || height <= 0) return null;

        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Win32.BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
        };
        var bitmap = Win32.CreateDIBSection(dc, header, 0, out var bits, 0, 0);
        try
        {
            if (bitmap == 0) return null;
            var old = SelectObject(dc, bitmap);
            var drawn = PrintWindow(hwnd, dc, PwRenderFullContent);
            GdiFlush();
            SelectObject(dc, old);
            if (!drawn) return null;

            // The visible frame inside the window's rectangle (Windows 11's invisible resize borders are left out).
            int left = Math.Clamp(frame.Left - window.Left, 0, width - 1), top = Math.Clamp(frame.Top - window.Top, 0, height - 1);
            int right = Math.Clamp(frame.Right - window.Left, left + 1, width), bottom = Math.Clamp(frame.Bottom - window.Top, top + 1, height);
            int w = right - left, h = bottom - top;
            var source = new ReadOnlySpan<byte>((void*)bits, width * height * 4);
            var pixels = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                source.Slice(((top + y) * width + left) * 4, w * 4).CopyTo(pixels.AsSpan(y * w * 4));
            }
            // PrintWindow leaves alpha undefined for GDI-drawn parts: the capture is of an opaque window.
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Ticker.TickerPng.FromBgra(pixels, w, h));
            return (w, h);
        }
        finally
        {
            if (bitmap != 0) Win32.DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>Whether <paramref name="hwnd"/> is the foreground window (it never should be under a capture).</summary>
    public static bool IsForeground(nint hwnd) => GetForegroundWindow() == hwnd;

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Win32.RECT rect);

    [LibraryImport("dwmapi")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Win32.RECT value, int size);

    [LibraryImport("user32")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("user32")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("gdi32")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GdiFlush();

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint hwnd, nint dc, uint flags);
}

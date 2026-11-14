// --settings-capture (checks): the whole Settings page written as a PNG without scrolling anything, the window beside it,
// and a log of text that doesn't fit. Nothing is activated, focused or clicked; the window sits under every other window.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Lunalyr.App.Onboarding;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Lunalyr.App;

public sealed partial class SettingsWindow
{
    /// <summary>The window's size in physical pixels when it opens.</summary>
    private const int OpeningWidth = 1400, OpeningHeight = 1040;

    /// <summary>
    /// Sizes the window as it opens. <c>--settings-capture-scale</c> (dev only) gives it the size in DIPs it would have at
    /// that display scale, drawn at this display's own scale.
    /// </summary>
    private void SizeWindow()
    {
        if (_host.SettingsCaptureScale is { } emulated)
        {
            var native = GetDpiForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Round(OpeningWidth / emulated * native),
                (int)Math.Round(OpeningHeight / emulated * native)));
            return;
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(OpeningWidth, OpeningHeight));
    }

    /// <summary><c>--settings-capture</c>: once the page has settled, writes it (and the window), logs what doesn't fit, quits.</summary>
    private async void CaptureSettings(string path)
    {
        var log = _host.Log.App;
        try
        {
            // The page's refreshes, the preview's first frames and the status line arrive in the first second or two.
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            var emulated = _host.SettingsCaptureScale is { } s ? $" (window sized for {s * 100:0} %)" : "";
            log.Info($"Settings capture: {PageTitle.Text}, display scale {GetDpiForWindow(hwnd) / 96.0:0.##}{emulated}, "
                + $"window {Root.ActualWidth:0}×{Root.ActualHeight:0} DIP, page {PageStack.ActualWidth:0}×{PageStack.ActualHeight:0} DIP, "
                + $"theme {Root.ActualTheme}.");
            var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
            var window = WindowCapture.WritePng(hwnd, stem + "-window.png");
            log.Info(window is { } w ? $"Settings capture: window written ({w.Width}×{w.Height})." : "Settings capture: the window couldn't be drawn.");
            Audit("as opened");
            await RenderPage(path);

            var closed = Descendants(PageStack).OfType<Expander>().Where(e => !e.IsExpanded && Shown(e)).ToList();
            if (closed.Count > 0)
            {
                foreach (var expander in closed) expander.IsExpanded = true;
                await Task.Delay(TimeSpan.FromSeconds(1));
                Audit("expanders open");
                await RenderPage(stem + "-expanded.png");
            }
            log.Info($"Settings capture: foreground: {WindowCapture.IsForeground(hwnd)}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Warn($"Settings capture to {path} failed.", ex);
        }
        _host.Quit();
    }

    /// <summary>Draws the page from its title to its last row (off the screen too) over the window's base colour.</summary>
    private async Task RenderPage(string path)
    {
        // RenderTargetBitmap draws at most 4096 pixels a side (it shrinks anything bigger), so a long page is drawn in
        // blocks no taller than that, each put back where it sits on the page.
        var scale = PageStack.XamlRoot.RasterizationScale;
        int width = (int)Math.Ceiling(PageStack.ActualWidth * scale), height = (int)Math.Ceiling(PageStack.ActualHeight * scale);
        var pixels = new byte[width * height * 4];
        foreach (var block in Blocks(PageStack, 4000 / scale))
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(block);
            if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) continue;
            var drawn = (await bitmap.GetPixelsAsync()).ToArray();
            var at = block.TransformToVisual(PageStack).TransformPoint(new Windows.Foundation.Point(0, 0));
            int left = (int)Math.Round(at.X * scale), top = (int)Math.Round(at.Y * scale);
            for (var y = 0; y < bitmap.PixelHeight; y++)
            {
                var row = top + y;
                if (row < 0 || row >= height) continue;
                var from = Math.Max(0, -left);
                var count = Math.Min(bitmap.PixelWidth, width - left) - from;
                if (count <= 0) continue;
                for (var x = from; x < from + count; x++)
                {
                    // Over what is already there (a block's own background is drawn with it).
                    int s = (y * bitmap.PixelWidth + x) * 4, d = (row * width + left + x) * 4;
                    var under = 255 - drawn[s + 3];
                    for (var c = 0; c < 4; c++) pixels[d + c] = (byte)(drawn[s + c] + pixels[d + c] * under / 255);
                }
            }
        }
        // Mica isn't drawn off the screen: the page goes over the theme's solid base colour (Fluent's
        // SolidBackgroundFillColorBase), as Mica looks with transparency effects off.
        var (r, g, b) = Root.ActualTheme == ElementTheme.Dark ? (0x20, 0x20, 0x20) : (0xF3, 0xF3, 0xF3);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var keep = 255 - pixels[i + 3];
            pixels[i] = (byte)(pixels[i] + b * keep / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] + g * keep / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] + r * keep / 255);
            pixels[i + 3] = 255;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Ticker.TickerPng.FromBgra(pixels, width, height));
        _host.Log.App.Info($"Settings capture: page written to {path} ({width}×{height}).");
    }

    /// <summary>Logs text cut short, menus whose longest choice doesn't fit, and anything reaching past the page's edge.</summary>
    private void Audit(string when)
    {
        var log = _host.Log.App;
        var found = 0;
        var pageWidth = PageStack.ActualWidth;
        foreach (var element in Descendants(PageStack).OfType<FrameworkElement>())
        {
            if (element.ActualWidth <= 0 || !Shown(element)) continue;
            if (element is TextBlock { IsTextTrimmed: true } trimmed)
            {
                log.Info($"Settings capture ({when}): text cut short: \"{trimmed.Text}\" ({trimmed.ActualWidth:0} DIP).");
                found++;
            }
            if (element is ComboBox combo)
            {
                foreach (var item in combo.Items.OfType<ComboBoxItem>())
                {
                    if (item.Content is not string text) continue;
                    var probe = new TextBlock { Text = text, FontSize = combo.FontSize };
                    probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    // The closed box: 12 DIPs before the text, the chevron's 38 after it.
                    if (probe.DesiredSize.Width + 50 > combo.ActualWidth + 0.5)
                    {
                        log.Info($"Settings capture ({when}): menu \"{AutomationProperties.GetName(combo)}\" ({combo.ActualWidth:0} DIP) "
                            + $"can't show \"{text}\" ({probe.DesiredSize.Width:0} DIP).");
                        found++;
                    }
                }
            }
            if (element is Control or TextBlock or Image)
            {
                var right = element.TransformToVisual(PageStack).TransformPoint(new Windows.Foundation.Point(element.ActualWidth, 0)).X;
                if (right > pageWidth + 1)
                {
                    log.Info($"Settings capture ({when}): {element.GetType().Name} {Describe(element)} reaches {right - pageWidth:0} DIP past the page's edge.");
                    found++;
                }
            }
        }
        log.Info($"Settings capture ({when}): {found} layout problem(s).");

        static string Describe(FrameworkElement e) => e switch
        {
            TextBlock t => $"\"{t.Text}\"",
            _ => AutomationProperties.GetName(e) is { Length: > 0 } name ? $"\"{name}\"" : e.Name,
        };
    }

    /// <summary>
    /// The page in pieces no taller than <paramref name="tallest"/> DIPs: an element that fits as itself, else each of its
    /// children (a transparent panel loses nothing by being drawn child by child).
    /// </summary>
    private static IEnumerable<UIElement> Blocks(UIElement element, double tallest)
    {
        if (element.Visibility == Visibility.Collapsed || element.ActualSize.Y <= 0) yield break;
        var children = element is Panel or ContentControl or ContentPresenter or Border ? VisualTreeHelper.GetChildrenCount(element) : 0;
        if (element.ActualSize.Y <= tallest || children == 0)
        {
            yield return element;
            yield break;
        }
        for (var i = 0; i < children; i++)
        {
            if (VisualTreeHelper.GetChild(element, i) is UIElement child)
                foreach (var block in Blocks(child, tallest)) yield return block;
        }
    }

    /// <summary>Whether <paramref name="element"/> and everything above it inside the page is visible.</summary>
    private bool Shown(UIElement element)
    {
        for (DependencyObject? at = element; at is not null && !ReferenceEquals(at, PageStack); at = VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement { Visibility: Visibility.Collapsed }) return false;
        }
        return true;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }

    [LibraryImport("user32")]
    private static partial uint GetDpiForWindow(nint hwnd);
}

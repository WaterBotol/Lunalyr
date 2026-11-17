using System.Text;
using Lunalyr.Core.Logging;
using Windows.ApplicationModel.DataTransfer;

namespace Lunalyr.App.Hosting;

// Tester feedback on 0.9.0: a tester's wallpaper failed on their PC and their log was weeks away. Settings › General ›
// Copy diagnostics puts what is needed on the clipboard, and a display whose wallpaper couldn't start says so once.
internal sealed partial class AppHost
{
    private int _wallpaperProblemShown;

    /// <summary>Copies the diagnostics to the clipboard (UI thread). False when the clipboard refused.</summary>
    internal async Task<bool> CopyDiagnosticsAsync()
    {
        var text = await DiagnosticsAsync().ConfigureAwait(true);
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            _log.Info("Diagnostics copied to the clipboard.");
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _log.Warn("Couldn't copy the diagnostics to the clipboard.", ex);
            return false;
        }
    }

    /// <summary>The diagnostics as text: nothing personal beyond the hardware (no song, no paths, no account).</summary>
    internal async Task<string> DiagnosticsAsync()
    {
        var text = new StringBuilder();
        var settings = _settings.Current;
        text.AppendLine($"Lunalyr {Version} on Windows {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        text.AppendLine($"Wallpaper: {settings.BackgroundMode}{(settings.WallpaperPaused ? " (paused)" : "")}; editing the {settings.LyricsSurface} style, layout {settings.LyricComposition}; "
            + $"displays {(DisplayKeysNow is { Count: > 0 } keys ? string.Join(", ", keys.Select((k, i) => $"{i + 1}: {Core.Floating.DisplayLyricsChoices.Raw(settings.DisplayChoice(k))}")) : "unknown")}; "
            + $"Animation effects {(SystemReducesMotion ? "off" : "on")}{(SystemReducesMotion && !ReduceMotion ? " (animating anyway)" : "")}; "
            + $"attach {_wallpaper?.Engine.AttachMode?.ToString() ?? "none"}");
        string webView;
        try
        {
            webView = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "not installed";
        }
        catch (Exception)
        {
            webView = "not installed";
        }
        text.AppendLine($"WebView2 (music video): {webView}");
        text.AppendLine();
        text.Append(_wallpaper?.Engine is { } engine ? await engine.DescribeAsync().ConfigureAwait(true) : "Renderer: not started.\n");
        text.AppendLine();
        var problems = _logging.Problems.Entries;
        text.AppendLine(problems.Count == 0 ? "Recent problems: none." : $"Recent problems ({problems.Count}):");
        foreach (var entry in problems)
        {
            text.Append("  ").Append(entry.Timestamp.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                .Append(' ').Append(LogNames.Of(entry.Level)).Append(' ').Append(LogNames.Of(entry.Category)).Append(": ")
                .AppendLine(entry.Message.ReplaceLineEndings(" "));
            if (entry.Exception is { Length: > 0 } ex) text.Append("     ").AppendLine(ex.Split('\n')[0].Trim());
        }
        return text.ToString();
    }

    /// <summary>
    /// Says once a session, in the action HUD, that a display's wallpaper couldn't start (its swap chain or a layer
    /// failed), so a tester knows it isn't them and where to get the details.
    /// </summary>
    private void WatchWallpaperProblems()
    {
        _logging.Problems.Logged += entry =>
        {
            if (entry.Category != LogCategory.Render || entry.Level < LogLevel.Error) return;
            if (!entry.Message.Contains("swap chain", StringComparison.OrdinalIgnoreCase)
                && !entry.Message.Contains("couldn't create its resources", StringComparison.OrdinalIgnoreCase)
                && !entry.Message.Contains("failed to render", StringComparison.OrdinalIgnoreCase)
                && !entry.Message.Contains("Creating layer", StringComparison.OrdinalIgnoreCase)) return;
            if (Interlocked.Exchange(ref _wallpaperProblemShown, 1) != 0) return;
            _tray.Post(() => ShowHud("The wallpaper couldn’t start on a display. Settings › Performance › Copy diagnostics has the details.",
                "exclamationmark.triangle"));
        };
    }
}

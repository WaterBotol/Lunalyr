// Choosing a picture or video for "Imported image or video": the copy Lunalyr keeps and switching the wallpaper to it.
// Ported from the Mac's WallpaperImporter.swift (Lunalyr's own lines; its four Verso-attributed lines are imports and
// declarations, nothing to port). The file picker itself is the settings window's (the Windows App SDK's).

using Lunalyr.Platform.Wallpaper;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Wallpaper;

/// <summary>
/// Imports a chosen file: <see cref="CustomWallpaperStore"/> checks and copies it into <c>{data}\Wallpapers</c>, then
/// the settings point at the copy, the wallpaper becomes "Imported image or video", and the copy it replaced is deleted.
/// A file that can't be used leaves everything as it was and says why in <see cref="Message"/>.
/// </summary>
public sealed class WallpaperImporter(string dataRoot, Func<AppSettings> current, Action<Func<AppSettings, AppSettings>> update,
    TimeProvider? time = null)
{
    /// <summary>How long an import runs before a spinner shows (the Mac's spinner delay); a quick copy shows none.</summary>
    public static readonly TimeSpan SpinnerDelay = TimeSpan.FromSeconds(0.4);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>An import is running.</summary>
    public bool Importing { get; private set; }

    /// <summary>The import has run past <see cref="SpinnerDelay"/>: show progress.</summary>
    public bool ShowsProgress { get; private set; }

    /// <summary>Why the last import failed, or null.</summary>
    public string? Message { get; private set; }

    /// <summary>Raised (on the caller's context) when <see cref="Importing"/>, <see cref="ShowsProgress"/> or <see cref="Message"/> change.</summary>
    public event Action? Changed;

    /// <summary>Where imported copies go.</summary>
    public string Folder => CustomWallpaperStore.DefaultFolder(dataRoot);

    public async Task<bool> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Importing) return false;
        Importing = true;
        Message = null;
        Changed?.Invoke();
        using var spinner = _time.CreateTimer(_ =>
        {
            if (!Importing) return;
            ShowsProgress = true;
        }, null, SpinnerDelay, Timeout.InfiniteTimeSpan);
        try
        {
            var imported = await CustomWallpaperStore.ImportAsync(path, Folder, cancellationToken);
            var replaced = current().WallpaperPath;
            update(s => s with { WallpaperPath = imported.Path, WallpaperName = imported.OriginalName, BackgroundMode = "custom" });
            CustomWallpaperStore.DiscardReplaced(replaced, imported.Path, Folder);
            return true;
        }
        catch (WallpaperImportException ex)
        {
            Message = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "Lunalyr couldn’t keep a copy of that file. " + ex.Message;
            return false;
        }
        finally
        {
            Importing = false;
            ShowsProgress = false;
            Changed?.Invoke();
        }
    }

    /// <summary>The imported file's name under File, or why there is none to show (the Mac's <c>importedFileLabel</c>).</summary>
    public static string FileLabel(string name, string path, Func<string, bool>? available = null)
    {
        if (string.IsNullOrEmpty(path)) return "None chosen";
        if (!(available ?? CustomWallpaperStore.IsAvailable)(path))
            return string.IsNullOrEmpty(name) ? "The imported file is missing. Choose it again." : $"“{name}” is missing. Choose it again.";
        return string.IsNullOrEmpty(name) ? "Imported file" : name;
    }

    /// <summary>The caption under Placement (the Mac's <c>importedWallpaperDetail</c>, with Windows' name for Reduce Motion).</summary>
    public static string Detail(bool reduceMotion)
    {
        var videos = reduceMotion ? "Animation effects are off in Windows, so videos hold on a still frame." : "Videos loop silently.";
        return $"Images stay still. {videos} Lunalyr keeps a copy so moving the original won’t break your wallpaper.";
    }
}

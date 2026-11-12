using Lunalyr.Platform.Wallpaper;
using Lunalyr.Shell.Wallpaper;

namespace Lunalyr.App;

public sealed partial class SettingsWindow
{
    private WallpaperImporter? _importer;

    /// <summary>The Wallpaper page's importer: copies go to <c>{data}\Wallpapers</c>.</summary>
    private WallpaperImporter Importer() =>
        _importer ??= new WallpaperImporter(_host.DataRoot,
            () => _host.Settings.Current, change => _ctx.Update(change));

    /// <summary>
    /// Windows' file picker for a wallpaper picture or video, over this window (the Windows App SDK's, as for lyric
    /// files); null when cancelled or when it can't be shown (logged). Only ever opened by a click.
    /// </summary>
    private async Task<string?> ChooseWallpaperFileAsync()
    {
        try
        {
            _host.Log.App.Info("Choosing a picture or video for the wallpaper.");
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)
            {
                CommitButtonText = "Choose",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            };
            foreach (var type in WallpaperPicture.Extensions.Concat(WallpaperPicture.VideoExtensions)) picker.FileTypeFilter.Add(type);
            var file = await picker.PickSingleFileAsync();
            _host.Log.App.Info(file is null ? "No wallpaper file chosen." : $"A wallpaper file was chosen ({Path.GetExtension(file.Path)}).");
            return file?.Path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn("Windows' file picker couldn't be shown (wallpaper).", ex);
            return null;
        }
    }
}

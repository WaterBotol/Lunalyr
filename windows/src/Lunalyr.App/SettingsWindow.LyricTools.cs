using Lunalyr.Shell.LyricTools;

namespace Lunalyr.App;

public sealed partial class SettingsWindow
{
    /// <summary>
    /// Windows' file picker for a lyric file (LRC or TTML) for the playing song, over this window; null when cancelled or
    /// when it can't be shown (logged). The Windows App SDK's picker, as for the Lyrimuse folder.
    /// </summary>
    private async Task<string?> ChooseLyricsFileAsync()
    {
        try
        {
            _host.Log.App.Info("Choosing a lyric file to import.");
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)
            {
                CommitButtonText = "Import",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            foreach (var type in new[] { ".lrc", ".ttml", ".xml", ".txt" }) picker.FileTypeFilter.Add(type);
            var file = await picker.PickSingleFileAsync();
            _host.Log.App.Info(file is null ? "No lyric file chosen." : $"A lyric file was chosen ({Path.GetExtension(file.Path)}).");
            return file?.Path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn($"Windows' file picker couldn't be shown ({LyricToolsText.ImportPickerTitle}).", ex);
            return null;
        }
    }

    /// <summary>Windows' file picker for a reading LRC (Settings › Readings › Import reading LRC); null when cancelled.</summary>
    private async Task<string?> ChooseReadingFileAsync()
    {
        try
        {
            _host.Log.App.Info("Choosing a reading file to import.");
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)
            {
                CommitButtonText = "Import",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            foreach (var type in new[] { ".lrc", ".txt" }) picker.FileTypeFilter.Add(type);
            var file = await picker.PickSingleFileAsync();
            _host.Log.App.Info(file is null ? "No reading file chosen." : $"A reading file was chosen ({Path.GetExtension(file.Path)}).");
            return file?.Path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn($"Windows' file picker couldn't be shown ({LyricToolsText.ReadingPickerTitle}).", ex);
            return null;
        }
    }
}

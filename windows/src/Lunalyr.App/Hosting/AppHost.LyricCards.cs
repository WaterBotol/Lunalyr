using System.Diagnostics;
using Lunalyr.Core.Cards;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Audio;
using Lunalyr.Platform.Media;
using Lunalyr.Render.Cards;
using Lunalyr.Shell;
using Lunalyr.Shell.Cards;
using Lunalyr.Shell.Rendering;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Lunalyr.App.Hosting;

// Lyric cards (milestone 7): what the card window asks of the host. The hotkey and link work can open the window with
// OpenLyricCard, as the Mac's shortcut, lunalyr://card and menu-bar ticker do.
internal sealed partial class AppHost
{
    private LyricCardExporter? _cardExporter;

    /// <summary>Opens the lyric card window for the song playing (the tray flyout, Settings › General, and later shortcuts and links).</summary>
    public void OpenLyricCard()
    {
        if (_quitting || _app is null) return;
        _log.Info("Opening the lyric card window.");
        _app.ShowLyricCard();
    }

    /// <summary>
    /// Why the song's sound can't be recorded in this run, or null when it can. Under <c>--demo</c> and
    /// <c>--no-playback</c> nothing is ever captured, so checks never record the PC's sound.
    /// </summary>
    internal string? CardSongRecordingOff => _options.Demo ? LyricCardCopy.SongOff("--demo")
        : PlaybackGuard.Process.IsBlocked ? LyricCardCopy.SongOff("--no-playback") : null;

    private string CardFolder(string name) => Path.Combine(DataRoot, "LyricCards", name);

    internal LyricCardWindowModel CreateLyricCardModel()
    {
        var dispatcher = _uiDispatcher ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var readings = _readings;
        _cardExporter ??= new LyricCardExporter(WallpaperLayers.Factories(readings?.WordBreaker
            ?? Lunalyr.Core.Lyrics.Readings.ReadingServices.Basic.WordBreaker, Log), AudioFileDecoder.Instance, hardware: true, Log);
        var exporter = _cardExporter;
        var recordings = CardFolder("Recordings");
        // Recordings left by an earlier run (quit mid-card) are deleted; only Lunalyr's own, in its own folder.
        _ = Task.Run(() => SongRecordings.DiscardLeftovers(recordings));
        Func<double, double, Action<RecordingUpdate>, CancellationToken, Task<SongAudioClip>>? record = null;
        if (CardSongRecordingOff is null)
        {
            var recorder = new SongRecorder(new LoopbackSongCaptureSource());
            record = (from, to, update, ct) =>
            {
                _log.Info("Lyric card: recording the song's sound for the selected lines as they play (loopback, kept in memory, then a file).");
                return recorder.RecordAsync(from, to, () => _controller?.ReadSongPosition() ?? (double.NaN, false), update, recordings, ct);
            };
        }
        var source = new LyricCardSource
        {
            TrackKey = () => _controller?.Status is { } s ? $"{s.Title}|{s.Artist}" : "",
            Title = () => _controller?.Status.Title ?? "",
            Artist = () => _controller?.Status.Artist ?? "",
            HasSong = () => _controller?.Status.Title is not null,
            Rows = () => _controller is { } c ? LyricCardBuilder.Rows(c.Scene) : [],
            ActiveRow = () => _controller is { } c ? LyricCardBuilder.ActiveRow(c.Scene, c.ReadClock().Seconds) : null,
            Content = (rows, layout, aspect) =>
            {
                if (_controller is not { } controller) return null;
                var settings = _settings.Current;
                var wallpaper = settings.LyricComposition == AppController.AutomaticComposition
                    ? controller.MatchedComposition ?? "editorial" : settings.LyricComposition;
                var scene = controller.Scene;
                return LyricCardBuilder.Build(scene, rows, layout ?? wallpaper, aspect, scene.Style, ReduceMotion);
            },
            Preview = job =>
            {
                var (width, height, pixels) = exporter.Still(job, LyricCardExporter.PreviewScale);
                return new CardPicture(width, height, pixels);
            },
            Export = (job, formats, directory, notes, phase, progress, ct) =>
            {
                _log.Info($"Lyric card: making {string.Join(", ", CardFormats.All.Where(formats.Contains).Select(f => f.Title()))} "
                    + $"({job.Content.Aspect.Raw()}, {job.Content.Pages.Count} line(s){(job.Content.SongAudio is null ? "" : ", with the song's sound")}).");
                return exporter.ExportAsync(job, formats, directory, notes, phase, progress, ct);
            },
            Settings = () => _settings.Current,
            UpdateSettings = update => _settings.Update(update),
            Publish = files => Publish(dispatcher, files),
            ShowFiles = ShowInExplorer,
            BackgroundPalette = background => CardLook.PaletteFor(background.First),
            RecordSong = record,
            RecordingsFolder = recordings,
            Post = action =>
            {
                if (dispatcher.HasThreadAccess) action();
                else dispatcher.TryEnqueue(() => action());
            },
        };
        return new LyricCardWindowModel(source);
    }

    /// <summary>Puts the card's files on the clipboard (a PNG also as a picture), only from the window's Copy.</summary>
    private Task<bool> Publish(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, IReadOnlyList<(CardFormat Format, string Path)> files)
    {
        var done = new TaskCompletionSource<bool>();
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                var items = new List<IStorageItem>();
                foreach (var (_, path) in files) items.Add(await StorageFile.GetFileFromPathAsync(path));
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetStorageItems(items);
                if (files.FirstOrDefault(f => f.Format == CardFormat.Png) is { Path: { } png })
                    package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(png)));
                Clipboard.SetContent(package);
                Clipboard.Flush();
                _log.Info($"Lyric card: copied {files.Count} file(s) to the clipboard.");
                done.TrySetResult(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("Lyric card: couldn't put the card on the clipboard.", ex);
                done.TrySetResult(false);
            }
        })) done.TrySetResult(false);
        return done.Task;
    }

    /// <summary>Opens Explorer at the saved cards, only from the window (after Save, or Show in Explorer).</summary>
    private void ShowInExplorer(IReadOnlyList<string> files)
    {
        if (files.Count == 0 || !File.Exists(files[0])) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{files[0]}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Warn("Lyric card: couldn't show the files in Explorer.", ex);
        }
    }
}

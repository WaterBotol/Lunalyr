// The lyric card window's model: the Mac's LyricCardPopoutModel and LyricCardSource (LyricCardPopout.swift, Lunalyr's
// own code, no Verso lines). The song's lines to pick from, the card's options, its preview, making it (Copy or Save),
// and getting the song's sound. Everything outside (the song, the settings, the exporter, the clipboard, Explorer, the
// recorder) comes through LyricCardSource, so tests run it without a player, a clipboard or real settings.

using Lunalyr.Core.Cards;
using Lunalyr.Platform.Media;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Cards;

/// <summary>A preview image: premultiplied BGRA.</summary>
public sealed record CardPicture(int Width, int Height, byte[] Pixels);

/// <summary>What the card window reads about the song, and where it makes, copies and shows cards.</summary>
public sealed class LyricCardSource
{
    public required Func<string> TrackKey { get; init; }
    public required Func<string> Title { get; init; }
    public required Func<string> Artist { get; init; }
    public required Func<bool> HasSong { get; init; }

    /// <summary>The lines to pick from.</summary>
    public required Func<IReadOnlyList<CardRow>> Rows { get; init; }

    /// <summary>The row being sung now, if any.</summary>
    public required Func<int?> ActiveRow { get; init; }

    /// <summary>The card for these rows, in this layout (null follows the wallpaper) and shape; null without a song.</summary>
    public required Func<IReadOnlyList<int>, string?, CardAspect, LyricCardJob?> Content { get; init; }

    /// <summary>Draws the preview of a card (its still).</summary>
    public required Func<LyricCardJob, CardPicture?> Preview { get; init; }

    public required Func<LyricCardJob, IReadOnlySet<CardFormat>, string, Action<string>, Action<ExportPhase>, Action<double>, CancellationToken,
        Task<IReadOnlyList<(CardFormat Format, string Path)>>> Export { get; init; }

    /// <summary>The settings the window keeps (the card's layout, shape, formats, background, song and text size).</summary>
    public required Func<AppSettings> Settings { get; init; }

    public required Action<Func<AppSettings, AppSettings>> UpdateSettings { get; init; }

    /// <summary>Puts made files on the clipboard (a PNG also as a picture); false when it couldn't.</summary>
    public Func<IReadOnlyList<(CardFormat Format, string Path)>, Task<bool>> Publish { get; init; } = _ => Task.FromResult(false);

    /// <summary>Shows made files in Explorer.</summary>
    public Action<IReadOnlyList<string>> ShowFiles { get; init; } = _ => { };

    /// <summary>Opens a picture, GIF or video for the background (null when it can't be read).</summary>
    public Func<string, Task<CardBackgroundSource?>> LoadBackground { get; init; } = path => CardBackgroundSource.LoadAsync(path);

    /// <summary>The palette a background's first frame gives.</summary>
    public Func<CardBackgroundSource, Palette?> BackgroundPalette { get; init; } = _ => null;

    /// <summary>Records the song from one song time to another as it plays (see <see cref="SongRecorder"/>).</summary>
    public Func<double, double, Action<RecordingUpdate>, CancellationToken, Task<SongAudioClip>>? RecordSong { get; init; }

    /// <summary>Where cards made to be copied are written (a fresh folder for each, under this one).</summary>
    public string CopyFolder { get; init; } = Path.Combine(Path.GetTempPath(), "Lunalyr Lyric Cards");

    /// <summary>Where song recordings are kept.</summary>
    public string RecordingsFolder { get; init; } = Path.Combine(Path.GetTempPath(), "Lunalyr Lyric Cards", "Recordings");

    /// <summary>Runs an update on the window's thread (inline by default: tests).</summary>
    public Action<Action> Post { get; init; } = action => action();
}

/// <summary>How the status line reads: a plain note, a card made, or a failure.</summary>
public enum CardStatusTone { Plain, Success, Failure }

/// <summary>An audio file mixed under the MP4, and how loud (0–1.5).</summary>
public sealed class CardSoundFile(string path)
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Path { get; } = path;
    public double Volume { get; set; } = 1;
}

public sealed class LyricCardWindowModel
{
    private readonly LyricCardSource _source;
    private readonly object _gate = new();
    private IReadOnlySet<int> _selection = new HashSet<int>();
    private string? _layout;
    private CardAspect _aspect;
    private IReadOnlySet<CardFormat> _formats;
    private double _textScale;
    private bool _useSong;
    private CardBackgroundSource? _background;
    private Palette? _backgroundPalette;
    private ClipLength _clipLength = ClipLength.MatchBackground;
    private bool _capsLength;
    private SongAudioClip? _songClip;
    private CancellationTokenSource? _previewCancel;
    private CancellationTokenSource? _staleCancel;
    private CancellationTokenSource? _exportCancel;
    private CancellationTokenSource? _songCancel;
    private Task _exportTask = Task.CompletedTask;
    private Task _songTask = Task.CompletedTask;
    private Guid? _exportId;

    public LyricCardWindowModel(LyricCardSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        var settings = source.Settings();
        _aspect = settings.CardAspect;
        _layout = settings.CardComposition;
        _formats = settings.CardFormats;
        _useSong = settings.LyricCardUseSong;
        _textScale = settings.CardTextScale;
    }

    /// <summary>Anything shown changed (on the window's thread when the source posts there).</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<CardRow> Rows { get; private set; } = [];
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public bool HasSong => _source.HasSong();

    /// <summary>The song whose lines were last loaded.</summary>
    public string? LoadedTrackKey { get; private set; }

    public IReadOnlySet<int> Selection
    {
        get => _selection;
        set
        {
            _selection = value ?? new HashSet<int>();
            OptionChanged();
        }
    }

    /// <summary>The card's layout; null follows the wallpaper. Shared with Settings › General › Card layout.</summary>
    public string? Layout
    {
        get => _layout;
        set
        {
            if (_layout == value) return;
            _layout = value;
            _source.UpdateSettings(s => s with { LyricCardComposition = value ?? "" });
            OptionChanged();
        }
    }

    public CardAspect Aspect
    {
        get => _aspect;
        set
        {
            _aspect = value;
            _source.UpdateSettings(s => s with { LyricCardAspect = value.Raw() });
            OptionChanged();
        }
    }

    public IReadOnlySet<CardFormat> Formats
    {
        get => _formats;
        set
        {
            _formats = value;
            _source.UpdateSettings(s => s with { LyricCardFormats = AppSettings.StoredCardFormats(value) });
            ClearResult();
            Raise();
        }
    }

    /// <summary>How big the lyrics are on the card, 50 to 110% of the room they have.</summary>
    public double TextScale
    {
        get => _textScale;
        set
        {
            _textScale = Math.Clamp(value, AppSettings.LyricCardTextScaleMin, AppSettings.LyricCardTextScaleMax);
            _source.UpdateSettings(s => s with { LyricCardTextScale = _textScale });
            OptionChanged();
        }
    }

    /// <summary>Whether the song's own sound goes under the MP4.</summary>
    public bool UseSong
    {
        get => _useSong;
        set
        {
            _useSong = value;
            _source.UpdateSettings(s => s with { LyricCardUseSong = value });
            OptionChanged();
        }
    }

    /// <summary>How loud the song is in the MP4: a slider position from 0 to 1.5, shown in dB.</summary>
    public double SoundVolume { get; set; } = 1;

    /// <summary>How loud the background video's own sound is.</summary>
    public double BackgroundVolume { get; set; } = 1;

    /// <summary>Lowers the song while the background video's own sound plays.</summary>
    public bool DuckSong { get; set; }

    public ClipLength ClipLength
    {
        get => _clipLength;
        set
        {
            _clipLength = value;
            OptionChanged();
        }
    }

    /// <summary>Keeps the MP4 to 60 seconds.</summary>
    public bool CapsLength
    {
        get => _capsLength;
        set
        {
            _capsLength = value;
            OptionChanged();
        }
    }

    /// <summary>Audio files to go under the MP4, mixed together.</summary>
    public List<CardSoundFile> SoundFiles { get; } = [];

    /// <summary>A picture, GIF or video behind the lyrics; null keeps the album look.</summary>
    public CardBackgroundSource? Background => _background;

    public CardPicture? Preview { get; private set; }

    /// <summary>Counts the previews drawn, so each new one can fade in.</summary>
    public int PreviewGeneration { get; private set; }

    /// <summary>The preview shown is out of date and a new one hasn't come yet.</summary>
    public bool PreviewStale { get; private set; }

    /// <summary>The bar while a card is made (0–1), else null.</summary>
    public double? Progress { get; private set; }

    public string Status { get; set; } = "";
    public CardStatusTone StatusTone { get; private set; } = CardStatusTone.Plain;

    /// <summary>What the last export noted, shown under Details.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>The files the last export made, for Show in Explorer.</summary>
    public IReadOnlyList<string> MadeFiles { get; private set; } = [];

    public int CardsMade { get; private set; }

    /// <summary>The MP4's length for the current choices.</summary>
    public ClipPlan? ClipPlan { get; private set; }

    /// <summary>The song's own sound for the selected lines, once recorded.</summary>
    public SongAudioClip? SongClip
    {
        get => _songClip;
        private set
        {
            // A replaced recording is deleted (the song's own file never is).
            if (_songClip is { } old && old.Path != value?.Path) SongRecordings.Discard(old, _source.RecordingsFolder);
            _songClip = value;
        }
    }

    /// <summary>The stretch of the song the selected lines need.</summary>
    public (double From, double To)? SongRange { get; private set; }

    public string SongStatus { get; private set; } = "";

    /// <summary>Getting the song's sound. Settable for tests, as the Mac's is.</summary>
    public bool SongBusy { get; set; }

    public RecordingUpdate? Recording { get; private set; }

    public bool SongNeedsFetching => SongRange is not { } range || SongClip is not { } clip || !clip.Covers(range.From, range.To);

    /// <summary>Making a card, or getting the song's sound: what the card is made from can't change underneath either.</summary>
    public bool IsBusy => Progress is not null || SongBusy;

    public bool CanMake => Selection.Count > 0 && Formats.Count > 0 && !IsBusy;

    /// <summary>The PNG shows only the first three lines.</summary>
    public bool StillLimitApplies => Formats.Contains(CardFormat.Png) && Selection.Count > LyricCardLayout.StillLineLimit;

    /// <summary>Waits for the export in progress, if any (tests).</summary>
    public Task WaitForExportAsync() => _exportTask;

    /// <summary>Waits for the song's recording in progress, if any (tests).</summary>
    public Task WaitForSongAsync() => _songTask;

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private void Post(Action action) => _source.Post(() =>
    {
        lock (_gate) action();
        Raise();
    });

    // ---- Opening and following the song ----

    /// <summary>
    /// The window is opening again. A new song starts afresh; the same song keeps the selection and the recording,
    /// refreshing only lines that changed meanwhile. Nothing changes while a card or recording is being made.
    /// </summary>
    public void PrepareToShow()
    {
        if (IsBusy) return;
        Reload(LoadedTrackKey != _source.TrackKey());
    }

    /// <summary>The song or its lyrics changed while the window is open (never mid-export).</summary>
    public void SongChanged()
    {
        if (Progress is not null) return;
        var key = _source.TrackKey();
        if (key != LoadedTrackKey) Reload(trackChanged: true);
        else if (!_source.Rows().SequenceEqual(Rows)) Reload(trackChanged: false);
    }

    /// <summary>
    /// Reads the playing song's lyrics. A new track selects the line being sung and drops the old song's recording; new
    /// lyrics for the same track keep the lines picked where they are still there.
    /// </summary>
    public void Reload(bool trackChanged = true)
    {
        if (trackChanged)
        {
            _exportCancel?.Cancel();
            _exportId = null;
            Progress = null;
            Show("");
            _songCancel?.Cancel();
            SongClip = null;
            SongStatus = "";
            SongRange = null;
        }
        var composition = _source.Settings().CardComposition;
        if (_layout != composition) _layout = composition;
        LoadedTrackKey = _source.TrackKey();
        Title = _source.Title();
        Artist = _source.Artist();
        var fresh = _source.Rows();
        if (!trackChanged)
        {
            if (fresh.SequenceEqual(Rows))
            {
                Raise();
                return;
            }
            var kept = CarrySelection(Selection, Rows, fresh);
            var hadSelection = Selection.Count > 0;
            Rows = fresh;
            if (kept.Count > 0)
            {
                Selection = kept;
                if (hadSelection) Show("Lyrics updated, selection kept");
                Raise();
                return;
            }
        }
        Rows = fresh;
        if (_source.ActiveRow() is { } active && Rows.Any(r => r.Id == active)) Selection = new HashSet<int> { active };
        else if (Rows.Count > 0) Selection = new HashSet<int> { Rows[0].Id };
        else Selection = new HashSet<int>();
        Raise();
    }

    /// <summary>The picked lines in <paramref name="fresh"/>, matched by their words: the second of two identical lines stays the second.</summary>
    public static IReadOnlySet<int> CarrySelection(IReadOnlySet<int> selection, IReadOnlyList<CardRow> old, IReadOnlyList<CardRow> fresh)
    {
        var occurrence = new Dictionary<string, int>(StringComparer.Ordinal);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in old)
        {
            var seen = occurrence.GetValueOrDefault(row.Text);
            occurrence[row.Text] = seen + 1;
            if (selection.Contains(row.Id)) wanted.Add($"{seen}|{row.Text}");
        }
        occurrence.Clear();
        var kept = new HashSet<int>();
        foreach (var row in fresh)
        {
            var seen = occurrence.GetValueOrDefault(row.Text);
            occurrence[row.Text] = seen + 1;
            if (wanted.Contains($"{seen}|{row.Text}")) kept.Add(row.Id);
        }
        return kept;
    }

    public void SelectAll() => Selection = Rows.Select(r => r.Id).ToHashSet();

    public void Toggle(CardFormat format)
    {
        var formats = Formats.ToHashSet();
        if (!formats.Remove(format)) formats.Add(format);
        Formats = formats;
    }

    // ---- Background and sounds ----

    public async Task SetBackgroundAsync(string path)
    {
        Show("Loading background…");
        Raise();
        var loaded = await _source.LoadBackground(path).ConfigureAwait(false);
        Post(() =>
        {
            if (loaded is null)
            {
                Show("Couldn’t open that file as a background", CardStatusTone.Failure);
                return;
            }
            _background?.Dispose();
            _background = loaded;
            // The lyrics' colours follow the background's own colours, not the album's.
            _backgroundPalette = _source.BackgroundPalette(loaded);
            _source.UpdateSettings(s => s with { LyricCardBackgroundPath = path });
            Show("");
            OptionChanged();
        });
    }

    /// <summary>Opens the background chosen last time, if it is still there.</summary>
    public async Task RestoreBackgroundAsync()
    {
        var path = _source.Settings().LyricCardBackgroundPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || _background is not null) return;
        var loaded = await _source.LoadBackground(path).ConfigureAwait(false);
        if (loaded is null) return;
        Post(() =>
        {
            _background ??= loaded;
            _backgroundPalette = _source.BackgroundPalette(_background);
            OptionChanged();
        });
    }

    public void ClearBackground()
    {
        _background?.Dispose();
        _background = null;
        _backgroundPalette = null;
        _source.UpdateSettings(s => s with { LyricCardBackgroundPath = "" });
        OptionChanged();
    }

    public void AddSound(string path)
    {
        if (SoundFiles.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        SoundFiles.Add(new CardSoundFile(path));
        Raise();
    }

    public void RemoveSound(Guid id)
    {
        SoundFiles.RemoveAll(s => s.Id == id);
        Raise();
    }

    /// <summary>Dropped files: audio goes under the card, anything else behind it.</summary>
    public Task AcceptAsync(string path)
    {
        if (IsAudio(path))
        {
            AddSound(path);
            return Task.CompletedTask;
        }
        return SetBackgroundAsync(path);
    }

    public static IReadOnlyList<string> AudioExtensions { get; } = [".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac", ".aif", ".aiff"];

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ---- The card ----

    /// <summary>The card for the current choices, or null with no lines picked.</summary>
    public LyricCardJob? Job()
    {
        if (Selection.Count == 0) return null;
        if (_source.Content([.. Selection.Order()], _layout, _aspect) is not { } job) return null;
        if (_background is not null) job = job.WithBackground(_background, _backgroundPalette);
        return job with
        {
            Content = job.Content with
            {
                KeepBackgroundSound = true,
                SoundVolume = SoundVolume,
                TextScale = _textScale,
                BackgroundVolume = BackgroundVolume,
                ExtraSounds = [.. SoundFiles.Select(s => new CardSoundTrack(s.Path, s.Volume))],
                // A recording that covers only some of the lines leaves the rest silent (the export says so).
                SongAudio = _useSong ? SongClip : null,
                DuckSong = DuckSong,
                ClipLength = _clipLength,
                CapsLength = _capsLength,
            },
        };
    }

    /// <summary>Something the card is made from changed: the last result no longer describes it, and the preview is redrawn.</summary>
    private void OptionChanged()
    {
        ClearResult();
        SchedulePreview();
        Raise();
    }

    /// <summary>Clears the last card's result (never a card being made).</summary>
    private void ClearResult()
    {
        if (Progress is not null) return;
        if (StatusTone == CardStatusTone.Plain && Warnings.Count == 0 && MadeFiles.Count == 0) return;
        Show("");
    }

    /// <summary>Sets the status line, dropping the last card's details.</summary>
    public void Show(string text, CardStatusTone tone = CardStatusTone.Plain)
    {
        Status = text;
        StatusTone = tone;
        Warnings = [];
        MadeFiles = [];
    }

    private void SchedulePreview()
    {
        _previewCancel?.Cancel();
        var cancel = _previewCancel = new CancellationTokenSource();
        // A preview still not redrawn after a moment dims.
        if (_staleCancel is null)
        {
            var stale = _staleCancel = new CancellationTokenSource();
            _ = Task.Delay(250, stale.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) Post(() => PreviewStale = true);
            }, TaskScheduler.Default);
        }
        // The card as the options now stand (made on the window's thread; the drawing happens off it).
        _ = DrawPreviewAsync(Job(), cancel.Token);
    }

    private async Task DrawPreviewAsync(LyricCardJob? job, CancellationToken token)
    {
        try
        {
            await Task.Delay(120, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer change: that one draws the preview. Clearing it here would flash the empty
            // placeholder on every slider tick.
            return;
        }
        CardPicture? picture = null;
        ClipPlan? plan = null;
        (double, double)? range = null;
        if (job is not null)
        {
            plan = LyricCardTiming.Plan(job.Content);
            range = LyricCardSound.SongRange(job.Content);
            picture = await Task.Run(() => _source.Preview(job), CancellationToken.None).ConfigureAwait(false);
        }
        if (token.IsCancellationRequested) return;
        Post(() =>
        {
            if (token.IsCancellationRequested) return;
            Preview = job is null ? null : picture;
            ClipPlan = plan;
            SongRange = range;
            _staleCancel?.Cancel();
            _staleCancel = null;
            PreviewStale = false;
            PreviewGeneration++;
        });
    }

    // ---- Making it ----

    /// <summary>Copies the chosen formats to the clipboard as files (a PNG also as a picture).</summary>
    public void Copy() => Make(null);

    /// <summary>Writes the chosen formats into <paramref name="folder"/> (chosen with the folder picker) and shows them.</summary>
    public void Save(string folder) => Make(folder);

    private void Make(string? folder)
    {
        if (!CanMake || Job() is not { } job) return;
        var chosen = Formats;
        var id = Guid.NewGuid();
        _exportId = id;
        Progress = 0;
        Show("Making your card…");
        var directory = folder ?? LyricCardExporter.ExportDirectory(_source.CopyFolder);
        var cancel = _exportCancel = new CancellationTokenSource();
        Raise();
        _exportTask = RunExportAsync(job, chosen, directory, folder, id, cancel.Token);
    }

    private async Task RunExportAsync(LyricCardJob job, IReadOnlySet<CardFormat> chosen, string directory, string? folder, Guid id, CancellationToken token)
    {
        // Starts after Copy or Save returns, as the Mac's export task does: the bar shows first.
        await Task.Yield();
        var warnings = new List<string>();
        IReadOnlyList<(CardFormat Format, string Path)> files;
        try
        {
            files = await _source.Export(job, chosen, directory, note => { lock (warnings) warnings.Add(note); },
                phase => Post(() => ReportPhase(phase, id)), value => Post(() => ReportProgress(value, id)), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            files = [];
        }
        if (token.IsCancellationRequested || _exportId != id)
        {
            // Cancelled: nothing half-made is left behind (an MP4 may have lost its sound).
            LyricCardExportPlan.DiscardExport(files, directory, ownsDirectory: folder is null);
            return;
        }
        // Copy: the files go on the clipboard (on the window's thread, which the source's Publish goes to).
        var published = folder is null && files.Count > 0 && await _source.Publish(files).ConfigureAwait(false);
        var done = new TaskCompletionSource();
        _source.Post(() =>
        {
            lock (_gate) Finish(files, warnings, directory, folder, published);
            Raise();
            done.TrySetResult();
        });
        await done.Task.ConfigureAwait(false);
    }

    private void Finish(IReadOnlyList<(CardFormat Format, string Path)> files, List<string> warnings, string directory, string? folder, bool published)
    {
        _exportId = null;
        Progress = null;
        if (files.Count == 0 && folder is null) LyricCardExportPlan.DiscardExport([], directory, ownsDirectory: true);
        if (files.Count == 0)
        {
            Show("Couldn’t make the card", CardStatusTone.Failure);
        }
        else
        {
            var names = string.Join(", ", files.Select(f => f.Format.Title()));
            if (folder is null)
            {
                if (published)
                {
                    LyricCardExportPlan.PruneExpiredExports(Path.GetDirectoryName(directory)!, directory, DateTime.Now);
                    Succeeded($"Copied {names}", files);
                }
                else
                {
                    Show("Couldn’t copy the card", CardStatusTone.Failure);
                }
            }
            else
            {
                Succeeded($"Saved {names}", files);
                _source.ShowFiles(MadeFiles);
            }
        }
        lock (warnings) Warnings = [.. warnings];
    }

    private void Succeeded(string text, IReadOnlyList<(CardFormat Format, string Path)> files)
    {
        Show(text, CardStatusTone.Success);
        MadeFiles = [.. files.Select(f => f.Path)];
        CardsMade++;
    }

    /// <summary>
    /// How far the export <paramref name="id"/> has got. Only the running export moves the bar, and only forward: a
    /// report arriving after Cancel or the finish would otherwise bring back a bar with nothing behind it.
    /// </summary>
    public void ReportProgress(double value, Guid id)
    {
        if (id != _exportId || Progress is not { } current || !double.IsFinite(value)) return;
        Progress = Math.Max(current, Math.Min(1, value));
    }

    /// <summary>What the export <paramref name="id"/> is doing now, shown under the bar.</summary>
    public void ReportPhase(ExportPhase phase, Guid id)
    {
        if (id != _exportId || Progress is null) return;
        Status = phase.Title;
    }

    public void Cancel()
    {
        _exportCancel?.Cancel();
        _exportId = null;
        Progress = null;
        Show("Cancelled");
        Raise();
    }

    public void ShowMadeFiles()
    {
        if (MadeFiles.Count > 0) _source.ShowFiles(MadeFiles);
    }

    // ---- The song's sound ----

    /// <summary>Whether the song's sound can be recorded in this run (a recorder is there).</summary>
    public bool CanRecordSong => _source.RecordSong is not null;

    /// <summary>
    /// Gets the song's own sound for the selected lines: Lunalyr records them while the song plays them (it never plays,
    /// pauses or moves the player itself), and only from this button.
    /// </summary>
    public void FetchSong()
    {
        // Not mid-export: the card being made may be reading the current recording.
        if (SongBusy || Progress is not null || SongRange is not { } range || _source.RecordSong is not { } record) return;
        SongBusy = true;
        SongStatus = $"Play the song from just before {Clock(range.From)}; Lunalyr records these lines as they play.";
        var cancel = _songCancel = new CancellationTokenSource();
        Raise();
        _songTask = Task.Run(async () =>
        {
            string status;
            SongAudioClip? clip = null;
            try
            {
                clip = await record(range.From, range.To, update => Post(() =>
                {
                    Recording = update;
                    if (update is RecordingUpdate.Recording) SongStatus = "Recording the song…";
                }), cancel.Token).ConfigureAwait(false);
                status = "Recorded the song.";
            }
            catch (OperationCanceledException)
            {
                status = "Cancelled.";
            }
            catch (SongRecordingException error)
            {
                status = error.Message;
            }
            Post(() =>
            {
                if (clip is not null) SongClip = clip;
                SongStatus = status;
                Recording = null;
                SongBusy = false;
            });
        });
    }

    public void CancelSong() => _songCancel?.Cancel();

    private static string Clock(double seconds)
    {
        var whole = (int)Math.Max(0, Math.Floor(seconds));
        return $"{whole / 60}:{whole % 60:00}";
    }

    /// <summary>The window closed for good: let the background and any recording go.</summary>
    public void Close()
    {
        _songCancel?.Cancel();
        _previewCancel?.Cancel();
    }
}

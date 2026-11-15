using Lunalyr.Core.Cards;
using Lunalyr.Render.Cards;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Cards;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests.Cards;

/// <summary>
/// The lyric card window's model, against a song, lyrics and export of the test's own: no player, clipboard or real
/// settings (ported from LyricCardPopoutTests.swift).
/// </summary>
public class LyricCardWindowModelTests
{
    /// <summary>What the fake player and lyrics say; tests change it as they go.</summary>
    private sealed class Song
    {
        public string Key = "Song|Artist";
        public IReadOnlyList<CardRow> Rows = Make("One", "Two", "Three", "Two", "Five");
        public int? Active = 2;
        public AppSettings Settings = new();

        public Func<LyricCardJob, IReadOnlySet<CardFormat>, string, Action<string>, Action<ExportPhase>, Action<double>, CancellationToken,
            Task<IReadOnlyList<(CardFormat, string)>>> Export = (_, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<(CardFormat, string)>>([]);

        public static IReadOnlyList<CardRow> Make(params string[] texts) => [.. texts.Select((t, i) => new CardRow(i, t))];
    }

    private static LyricCardJob Content(IReadOnlyList<int> rows) => new()
    {
        Content = new LyricCardContent { Pages = [.. rows.Select(r => new CardPage(["Line", $"{r}"]))], Title = "Song", Artist = "Artist", ReduceMotion = true },
        Look = CardLook.ForCard(PaletteMapper.Fallback, null, LyricStyle.Default),
    };

    private static LyricCardWindowModel MakeModel(Song song)
    {
        var source = new LyricCardSource
        {
            TrackKey = () => song.Key, Title = () => "Song", Artist = () => "Artist", HasSong = () => true,
            Rows = () => song.Rows, ActiveRow = () => song.Active,
            Content = (rows, _, _) => Content(rows),
            Preview = _ => new CardPicture(4, 4, new byte[64]),
            Export = (job, formats, directory, notes, phase, progress, ct) => song.Export(job, formats, directory, notes, phase, progress, ct),
            Settings = () => song.Settings,
            UpdateSettings = update => song.Settings = update(song.Settings),
            CopyFolder = Path.Combine(Path.GetTempPath(), "lunalyr-card-model-tests"),
        };
        return new LyricCardWindowModel(source) { Formats = new HashSet<CardFormat> { CardFormat.Png } };
    }

    private static Task Settle(int milliseconds = 60) => Task.Delay(milliseconds, TestContext.Current.CancellationToken);

    // Ported from LyricCardPopoutTests.swift: testAFinishedExportLeavesNoProgressBarBehind
    [Fact]
    public async Task AFinishedExportLeavesNoProgressBarBehind()
    {
        var song = new Song();
        // Like the real export, the last report comes right before it returns.
        song.Export = (_, _, _, _, _, progress, _) =>
        {
            progress(0.4);
            progress(1);
            return Task.FromResult<IReadOnlyList<(CardFormat, string)>>([]);
        };
        var model = MakeModel(song);
        model.PrepareToShow();
        model.Copy();
        Assert.NotNull(model.Progress);
        await model.WaitForExportAsync();
        await Settle();
        Assert.Null(model.Progress); // a report queued before the finish must not bring the bar back
        Assert.Equal("Couldn’t make the card", model.Status);
        Assert.True(model.CanMake);
    }

    // Ported from LyricCardPopoutTests.swift: testCancelLeavesNoProgressBarBehind
    [Fact]
    public async Task CancelLeavesNoProgressBarBehind()
    {
        var song = new Song();
        song.Export = async (_, _, _, _, _, progress, ct) =>
        {
            progress(0.2);
            while (!ct.IsCancellationRequested) await Task.Delay(5, CancellationToken.None);
            progress(1); // the real export reports its last share even when cancelled
            return [];
        };
        var model = MakeModel(song);
        model.PrepareToShow();
        model.Copy();
        await Settle(30);
        Assert.Equal(0.2, model.Progress ?? -1, 3);
        model.Cancel();
        await model.WaitForExportAsync();
        await Settle();
        Assert.Null(model.Progress);
        Assert.Equal("Cancelled", model.Status);
        Assert.True(model.CanMake); // Copy and Save work again after Cancel
    }

    // Ported from LyricCardPopoutTests.swift: testProgressOnlyMovesForwardAndOnlyForTheRunningExport
    [Fact]
    public void ProgressOnlyMovesForwardAndOnlyForTheRunningExport()
    {
        var model = MakeModel(new Song());
        model.ReportProgress(0.5, Guid.NewGuid());
        Assert.Null(model.Progress); // no export running: nothing to show
    }

    // Ported from LyricCardPopoutTests.swift: testThePreviewNeverBlanksWhileAnOptionChanges
    [Fact]
    public async Task ThePreviewNeverBlanksWhileAnOptionChanges()
    {
        var model = MakeModel(new Song());
        model.PrepareToShow();
        await Settle(300);
        Assert.NotNull(model.Preview);
        var seen = new List<bool>();
        model.Changed += (_, _) => { lock (seen) seen.Add(model.Preview is not null); };
        for (var scale = 0.6; scale <= 1.0001; scale += 0.1)
        {
            model.TextScale = scale;
            await Settle(20);
        }
        await Settle(300);
        Assert.NotEmpty(seen); // the last change is drawn
        Assert.DoesNotContain(false, seen); // a superseded change must not clear the preview
    }

    // Ported from LyricCardPopoutTests.swift: testThePreviewClearsWhenNothingIsPicked
    [Fact]
    public async Task ThePreviewClearsWhenNothingIsPicked()
    {
        var model = MakeModel(new Song());
        model.PrepareToShow();
        await Settle(300);
        model.Selection = new HashSet<int>();
        await Settle(300);
        Assert.Null(model.Preview);
    }

    // Ported from LyricCardPopoutTests.swift: testOpeningAgainForTheSameSongKeepsTheWork
    [Fact]
    public void OpeningAgainForTheSameSongKeepsTheWork()
    {
        var song = new Song();
        var model = MakeModel(song);
        model.PrepareToShow();
        Assert.Equal([2], model.Selection); // the line being sung
        model.Selection = new HashSet<int> { 0, 1 };
        model.Status = "Copied PNG";
        song.Active = 4;
        model.PrepareToShow();
        Assert.Equal([0, 1], model.Selection.Order());
        Assert.Equal("Copied PNG", model.Status);
    }

    // Ported from LyricCardPopoutTests.swift: testOpeningAgainForAnotherSongStartsAfresh
    [Fact]
    public void OpeningAgainForAnotherSongStartsAfresh()
    {
        var song = new Song();
        var model = MakeModel(song);
        model.PrepareToShow();
        model.Selection = new HashSet<int> { 0, 1 };
        song.Key = "Other|Artist";
        song.Active = 4;
        model.PrepareToShow();
        Assert.Equal([4], model.Selection);
    }

    // Ported from LyricCardPopoutTests.swift: testOpeningAgainWhileBusyChangesNothing
    [Fact]
    public void OpeningAgainWhileBusyChangesNothing()
    {
        var song = new Song();
        var model = MakeModel(song);
        model.PrepareToShow();
        model.Selection = new HashSet<int> { 0, 1 };
        model.SongBusy = true;
        song.Key = "Other|Artist";
        model.PrepareToShow();
        Assert.Equal([0, 1], model.Selection.Order()); // a recording in progress keeps its lines
    }

    // Ported from LyricCardPopoutTests.swift: testNewLyricsForTheSameSongKeepTheLinesPicked
    [Fact]
    public void NewLyricsForTheSameSongKeepTheLinesPicked()
    {
        var song = new Song();
        var model = MakeModel(song);
        model.PrepareToShow();
        model.Selection = new HashSet<int> { 3, 4 }; // the second "Two", and "Five"
        song.Rows = Song.Make("Intro", "One", "Two", "Three", "Two", "Five");
        model.Reload(trackChanged: false);
        Assert.Equal([4, 5], model.Selection.Order());
        Assert.Equal("Lyrics updated, selection kept", model.Status);
    }

    // Ported from LyricCardPopoutTests.swift: testNewLyricsWithNoneOfThePickedLinesSelectTheLineBeingSung
    [Fact]
    public void NewLyricsWithNoneOfThePickedLinesSelectTheLineBeingSung()
    {
        var song = new Song();
        var model = MakeModel(song);
        model.PrepareToShow();
        model.Selection = new HashSet<int> { 0 };
        song.Rows = Song.Make("Uno", "Dos", "Tres");
        model.Reload(trackChanged: false);
        Assert.Equal([2], model.Selection);
        Assert.Equal("", model.Status);
    }

    // Ported from LyricCardPopoutTests.swift: testCarryingTheSelectionMatchesRepeatedLinesInOrder
    [Fact]
    public void CarryingTheSelectionMatchesRepeatedLinesInOrder()
    {
        CardRow[] old = [new(0, "A"), new(1, "B"), new(2, "A")];
        CardRow[] fresh = [new(5, "A"), new(6, "A"), new(7, "C")];
        Assert.Equal([6], LyricCardWindowModel.CarrySelection(new HashSet<int> { 2 }, old, fresh));
        Assert.Equal([5], LyricCardWindowModel.CarrySelection(new HashSet<int> { 0, 1 }, old, fresh));
        Assert.Empty(LyricCardWindowModel.CarrySelection(new HashSet<int>(), old, fresh));
    }

    // Ported from LyricCardPopoutTests.swift: testNothingCanBeMadeWhileTheSongIsBeingRecorded
    [Fact]
    public void NothingCanBeMadeWhileTheSongIsBeingRecorded()
    {
        var model = MakeModel(new Song());
        model.PrepareToShow();
        Assert.True(model.CanMake);
        model.SongBusy = true;
        Assert.False(model.CanMake);
        Assert.True(model.IsBusy);
    }

    // Ported from LyricCardPopoutTests.swift: testAFailedCardSaysSoWithItsNotesAndAnOptionChangeClearsIt
    [Fact]
    public async Task AFailedCardSaysSoWithItsNotesAndAnOptionChangeClearsIt()
    {
        var song = new Song();
        song.Export = (_, _, _, notes, phase, _, _) =>
        {
            phase(new ExportPhase.Drawing(CardFormat.Png));
            notes("Couldn’t read the audio in a.wav");
            return Task.FromResult<IReadOnlyList<(CardFormat, string)>>([]);
        };
        var model = MakeModel(song);
        model.PrepareToShow();
        model.Copy();
        await model.WaitForExportAsync();
        Assert.Equal(CardStatusTone.Failure, model.StatusTone);
        Assert.Equal(["Couldn’t read the audio in a.wav"], model.Warnings);
        model.TextScale = 0.7;
        Assert.Equal("", model.Status);
        Assert.Equal(CardStatusTone.Plain, model.StatusTone);
        Assert.Empty(model.Warnings);
    }

    // Ported from LyricCardPopoutTests.swift: testTheStatusSaysWhatTheExportIsDoing
    [Fact]
    public async Task TheStatusSaysWhatTheExportIsDoing()
    {
        var song = new Song();
        var seen = new List<string>();
        LyricCardWindowModel? model = null;
        song.Export = (_, _, _, _, phase, _, _) =>
        {
            phase(new ExportPhase.MixingSound());
            seen.Add(model?.Status ?? "");
            return Task.FromResult<IReadOnlyList<(CardFormat, string)>>([]);
        };
        model = MakeModel(song);
        model.PrepareToShow();
        model.Copy();
        await model.WaitForExportAsync();
        Assert.Equal(["Mixing the sound…"], seen);
    }

    // Ported from LyricCardPopoutTests.swift: testEachNewPreviewIsCounted
    [Fact]
    public async Task EachNewPreviewIsCounted()
    {
        var model = MakeModel(new Song());
        model.PrepareToShow();
        await Settle(300);
        var first = model.PreviewGeneration;
        model.Aspect = CardAspect.Square;
        await Settle(300);
        Assert.Equal(first + 1, model.PreviewGeneration);
        Assert.False(model.PreviewStale);
    }

    // Ported from LyricExtrasTests.swift: testCardControlsPersistAndCountOnlyVocalRowsAroundTheCurrentLine (the settings'
    // half: the card's layout and line counts are kept, and read back clamped)
    [Fact]
    public void CardControlsPersist()
    {
        var settings = new AppSettings();
        Assert.Equal(0, settings.CardLinesBefore);
        Assert.Equal(0, settings.CardLinesAfter);
        Assert.Null(settings.CardComposition);
        settings = settings with { LyricCardLinesBefore = 1, LyricCardLinesAfter = 2, LyricCardComposition = "cutout" };
        var json = System.Text.Json.JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(1, reloaded.CardLinesBefore);
        Assert.Equal(2, reloaded.CardLinesAfter);
        Assert.Equal("cutout", reloaded.CardComposition);
        Assert.Contains("\"lyricCardComposition\": \"cutout\"", json, StringComparison.Ordinal);
        // Out of range or unknown reads as the default.
        var odd = reloaded with { LyricCardLinesAfter = 40, LyricCardComposition = "nonsense", LyricCardAspect = "round", LyricCardFormats = ["tiff"], LyricCardTextScale = 9 };
        Assert.Equal(5, odd.CardLinesAfter);
        Assert.Null(odd.CardComposition);
        Assert.Equal(CardAspect.Portrait, odd.CardAspect);
        Assert.Equal([CardFormat.Mp4, CardFormat.Png], odd.CardFormats.Order());
        Assert.Equal(1.1, odd.CardTextScale);
    }

    // Windows' own: the window's choices are kept in the settings, as the Mac keeps them in its defaults.
    [Fact]
    public void TheWindowsChoicesAreKept()
    {
        var song = new Song();
        var model = MakeModel(song);
        model.Aspect = CardAspect.Story;
        model.Formats = new HashSet<CardFormat> { CardFormat.Gif, CardFormat.Mp4 };
        model.TextScale = 0.6;
        model.UseSong = true;
        model.Layout = "spoken";
        Assert.Equal("story", song.Settings.LyricCardAspect);
        Assert.Equal(["gif", "mp4"], song.Settings.LyricCardFormats);
        var again = MakeModel(song);
        Assert.Equal(CardAspect.Story, again.Aspect);
        Assert.Equal(0.6, again.TextScale, 9);
        Assert.True(again.UseSong);
        Assert.Equal("spoken", again.Layout);
    }
}

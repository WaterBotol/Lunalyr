using System.Text.Json;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Desktop;
using Lunalyr.Shell.Extras;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests.Extras;

/// <summary>
/// Hiding the lyrics while a display is mirrored (Windows: duplicated), ported from LyricExtrasTests.swift over settings
/// in memory and a manual clock. The Mac's titles are in Windows' sentence case ("Show lyrics anyway"); its
/// visibility.target is what the controller is told (<see cref="ExtrasHooks.HiddenByMirroringChanged"/>), and its
/// previewVisibility (the Settings preview, which Windows doesn't fade) is left out. Real display changes can't be made
/// here: <see cref="DisplayMirroring.AnyMirrored"/> is the pure part of reading them.
/// </summary>
public sealed class MirroringTests
{
    private AppSettings _settings = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly List<string> _spoken = [];
    private readonly List<bool> _sent = [];

    private bool Update(Func<AppSettings, AppSettings> change)
    {
        _settings = change(_settings).Normalized();
        return true;
    }

    private LunalyrExtras Extras(List<string>? spoken = null) =>
        new(() => _settings, Update, new ExtrasHooks { HiddenByMirroringChanged = _sent.Add }, _clock) { Announce = (spoken ?? _spoken).Add };

    // Ported from LyricExtrasTests.swift: testMirroringIsDetectedOnAnyDisplay (a source shown on two monitors stands in
    // for a display in a mirror set).
    [Fact]
    public void MirroringIsDetectedOnAnyDisplay()
    {
        Assert.False(DisplayMirroring.AnyMirrored([(1, 0), (1, 1)]));
        Assert.True(DisplayMirroring.AnyMirrored([(1, 0), (1, 1), (1, 1)]));
        Assert.False(DisplayMirroring.AnyMirrored([]));
        Assert.False(DisplayMirroring.AnyMirrored([(1, 0), (2, 0)])); // two adapters' first sources are two pictures
    }

    // Ported from LyricExtrasTests.swift: testFeaturePreferencesPersistInIsolatedDefaults (the mirroring part).
    [Fact]
    public async Task HidingWhileMirroringIsOnUntilChosenAndIsKept()
    {
        Assert.True(Extras().HidesWhileMirroring);
        Assert.False(Extras().MirroringChoiceMade);
        using var temp = new TempFolder();
        await using (var store = AppSettingsStore.Open(temp.Path, Logger.Null))
        {
            store.Update(s => s with { LyricOffset = 0.5 });
            Assert.True(await store.FlushAsync());
            var fresh = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "settings.json"), TestContext.Current.CancellationToken)).RootElement;
            Assert.False(fresh.TryGetProperty("hideLyricsWhileMirroring", out _)); // "not yet chosen" is the key's absence
            store.Update(s => s with { HideLyricsWhileMirroring = false });
            Assert.True(await store.FlushAsync());
        }
        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "settings.json"), TestContext.Current.CancellationToken)).RootElement;
        Assert.False(json.GetProperty("hideLyricsWhileMirroring").GetBoolean());
        await using var reopened = AppSettingsStore.Open(temp.Path, Logger.Null);
        Assert.False(reopened.Current.HidesWhileMirroring);
        Assert.Equal(false, reopened.Current.HideLyricsWhileMirroring);
    }

    // Ported from LyricExtrasTests.swift: testEveryHideAndShowControlFollowsWhatIsOnScreenWhileMirroring.
    [Fact]
    public void EveryHideAndShowControlFollowsWhatIsOnScreenWhileMirroring()
    {
        var extras = Extras();
        Assert.Equal("Hide lyrics", extras.LyricsActionTitle);

        // Presenting on a projector: mirroring hides the lyrics, says so, and the control offers to show them, not to
        // hide them again.
        extras.UpdateMirroring(true);
        Assert.False(extras.LyricsVisible);
        Assert.True(extras.HiddenByMirroring);
        Assert.False(_settings.LyricsHidden); // the user's own Hide is untouched
        Assert.Equal("Lyrics hidden while a display is mirrored", extras.Note?.Text);
        Assert.Equal("Show lyrics anyway", extras.LyricsActionTitle);
        Assert.Equal("eye", extras.LyricsActionSymbol);
        Assert.Equal([true], _sent);

        // Pressing it shows them, and the confirmation is true.
        extras.ToggleLyrics();
        Assert.True(extras.LyricsVisible);
        Assert.Equal("Lyrics shown while mirroring", extras.Note?.Text);
        Assert.Equal("Hide lyrics", extras.LyricsActionTitle);
        extras.ToggleLyrics();
        Assert.False(extras.LyricsVisible);
        Assert.True(_settings.LyricsHidden);
        Assert.Equal("Show lyrics", extras.LyricsActionTitle);
        extras.ToggleLyrics();
        Assert.True(extras.LyricsVisible); // still shown anyway for this mirroring

        // The next mirroring hides them again.
        extras.UpdateMirroring(false);
        Assert.True(extras.LyricsVisible);
        _clock.AdvanceSeconds(60);
        extras.UpdateMirroring(true);
        Assert.False(extras.LyricsVisible);
        Assert.True(_sent[^1]); // the wallpaper fades with it
        // Hidden by the user too, showing them still overrides mirroring, and the control says so.
        extras.HideLyrics();
        Assert.Equal("Show lyrics anyway", extras.LyricsActionTitle);
        extras.ToggleLyrics();
        Assert.True(extras.LyricsVisible);
        Assert.False(_settings.LyricsHidden);
        extras.UpdateMirroring(false);
        _clock.AdvanceSeconds(60);
        extras.UpdateMirroring(true);
        Assert.False(extras.LyricsVisible);
        // What the Settings preview would follow: the user's own Hide, but not mirroring.
        Assert.True(extras.LyricsVisibleInPreview);
        extras.HideLyrics();
        Assert.False(extras.LyricsVisibleInPreview); // the user's own Hide reaches it
        extras.ShowLyrics();

        // Not hiding while mirroring: nothing changes, and nothing is said.
        Update(s => s with { HideLyricsWhileMirroring = false });
        extras.SettingsChanged();
        Assert.True(extras.LyricsVisible);
        extras.UpdateMirroring(false);
        var before = _spoken.Count;
        extras.UpdateMirroring(true);
        Assert.True(extras.LyricsVisible);
        Assert.Equal(before, _spoken.Count);
        Assert.Equal("Lyrics hidden while a display is mirrored", _spoken[0]); // notes are said to Narrator too
        Assert.False(_sent[^1]);
    }

    // Ported from LyricExtrasTests.swift: testTheFirstMirroringAsksOnceWhetherToKeepHiding.
    [Fact]
    public void TheFirstMirroringAsksOnceWhetherToKeepHiding()
    {
        var extras = Extras();
        Assert.False(extras.AsksAboutMirroring); // nothing to ask until it happens
        extras.UpdateMirroring(true);
        Assert.True(extras.AsksAboutMirroring);
        extras.KeepHidingWhileMirroring();
        Assert.False(extras.AsksAboutMirroring);
        Assert.True(extras.HiddenByMirroring);
        Assert.True(Extras().MirroringChoiceMade); // the answer is kept

        _settings = new AppSettings();
        var declining = Extras();
        declining.UpdateMirroring(true);
        declining.StopHidingWhileMirroring();
        Assert.True(declining.LyricsVisible);
        Assert.False(declining.AsksAboutMirroring);
        Assert.False(Extras().HidesWhileMirroring);

        // Someone who already chose in Settings is never asked.
        _settings = new AppSettings { HideLyricsWhileMirroring = true };
        var settled = Extras();
        settled.UpdateMirroring(true);
        Assert.True(settled.HiddenByMirroring);
        Assert.False(settled.AsksAboutMirroring);
    }

    // Ported from LyricExtrasTests.swift: testABriefLapseInMirroringKeepsShowAnywayAndSaysNothingAgain.
    [Fact]
    public void ABriefLapseInMirroringKeepsShowAnywayAndSaysNothingAgain()
    {
        var extras = Extras();
        extras.UpdateMirroring(true);
        Assert.Equal(["Lyrics hidden while a display is mirrored"], _spoken);
        extras.ShowLyrics();
        Assert.True(extras.LyricsVisible);
        _spoken.Clear();

        // A flaky cable or a projector waking: mirroring drops for a moment.
        extras.UpdateMirroring(false);
        _clock.Advance(LunalyrExtras.MirroringLapse - TimeSpan.FromSeconds(1));
        extras.UpdateMirroring(true);
        Assert.True(extras.LyricsVisible); // still shown anyway mid-presentation
        Assert.Equal("Hide lyrics", extras.LyricsActionTitle);
        Assert.Empty(_spoken); // nothing said again on the shared screen

        // Without Show anyway, a lapse hides them again, but quietly.
        var heard = new List<string>();
        var quiet = Extras(heard);
        quiet.UpdateMirroring(true);
        quiet.UpdateMirroring(false);
        Assert.True(quiet.LyricsVisible);
        _clock.AdvanceSeconds(2);
        quiet.UpdateMirroring(true);
        Assert.False(quiet.LyricsVisible);
        Assert.Equal(["Lyrics hidden while a display is mirrored"], heard); // said once for the one mirroring

        // Mirroring again after a real break is a new presentation.
        extras.UpdateMirroring(false);
        _clock.Advance(LunalyrExtras.MirroringLapse + TimeSpan.FromSeconds(1));
        extras.UpdateMirroring(true);
        Assert.False(extras.LyricsVisible);
        Assert.Equal("Show lyrics anyway", extras.LyricsActionTitle);
        Assert.Equal(["Lyrics hidden while a display is mirrored"], _spoken);
    }

    // The Mac's ExtrasInspector.mirroringNote, with "duplicating" for Windows' word for it.
    [Fact]
    public void TheSettingSaysWhatMirroringIsDoingNow()
    {
        Assert.Equal("For presenting: duplicating a display to a projector or TV hides the lyrics until it ends.",
            LunalyrExtras.MirroringNote(false, true, false));
        Assert.Equal("A display is mirrored now; the lyrics stay on every display.", LunalyrExtras.MirroringNote(true, false, false));
        Assert.Equal("A display is mirrored now, and the lyrics are shown anyway until mirroring ends.", LunalyrExtras.MirroringNote(true, true, true));
        Assert.Equal("A display is mirrored now, so the lyrics are hidden on every display until mirroring ends.",
            LunalyrExtras.MirroringNote(true, true, false));
    }
}

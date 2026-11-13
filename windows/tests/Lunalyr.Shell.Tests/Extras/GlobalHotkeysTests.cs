using System.Text.Json;
using Lunalyr.Shell.Extras;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests.Extras;

/// <summary>
/// The global shortcuts against a stand-in registrar: nothing here ever holds a key system-wide. "Reloaded" means a new
/// <see cref="GlobalHotkeys"/> over the settings the first one saved, as the Mac's tests reload from their defaults.
/// </summary>
public sealed class GlobalHotkeysTests
{
    /// <summary>Settings as the store keeps them: read and updated, normalised like the real store.</summary>
    private sealed class Store
    {
        public AppSettings Current { get; private set; } = new();

        public int Updates { get; private set; }

        public bool Update(Func<AppSettings, AppSettings> change)
        {
            var next = change(Current).Normalized();
            Updates++;
            Current = next;
            return true;
        }
    }

    /// <summary>Records what would be registered, refusing whatever "another app" holds.</summary>
    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public HashSet<int> Held { get; } = [];

        public HashSet<string> Taken { get; set; } = [];

        public List<(uint ScanCode, uint Modifiers)> Attempts { get; } = [];

        public bool Register(uint scanCode, uint modifiers, int id)
        {
            Attempts.Add((scanCode, modifiers));
            if (Taken.Contains($"{scanCode}/{modifiers}")) return false;
            Held.Add(id);
            return true;
        }

        public void Unregister(int id) => Held.Remove(id);

        public void Take(HotkeyAction action, HotkeyModifiers modifiers) =>
            Taken.Add($"{GlobalHotkeys.ShortcutFor(action)!.ScanCode}/{modifiers.Flags()}");
    }

    private static GlobalHotkeys Hotkeys(Store store, IHotkeyRegistrar registrar, Func<uint, string?>? keyName = null) =>
        new(() => store.Current, store.Update, registrar, keyName);

    private static readonly HashSet<HotkeyAction> OnByDefault = [.. HotkeyActions.All.Where(a => a.IsOnByDefault())];

    // Ported from LyricExtrasTests.swift: testShortcutsAreDistinctAndLeaveWindowManagerArrowsAlone. The arrows are
    // Windows' own with Win (snapping) and Ctrl+Win (virtual desktops), as they are Rectangle's and Magnet's on the Mac.
    [Fact]
    public void TestShortcutsAreDistinctAndLeaveWindowManagerArrowsAlone()
    {
        var shortcuts = GlobalHotkeys.Shortcuts;
        Assert.Equal(shortcuts.Count, shortcuts.Select(s => s.ScanCode).Distinct().Count());
        Assert.Equal(HotkeyActions.All.ToHashSet(), shortcuts.Select(s => s.Action).ToHashSet());
        uint[] arrows = [0x4B, 0x4D, 0x48, 0x50]; // left, right, up, down
        Assert.All(shortcuts, s => Assert.DoesNotContain(s.ScanCode, arrows));
        Assert.Equal("[", GlobalHotkeys.ShortcutFor(HotkeyAction.SyncEarlier)?.Key);
        Assert.Equal("]", GlobalHotkeys.ShortcutFor(HotkeyAction.SyncLater)?.Key);
        Assert.False(new AppSettings().GlobalShortcuts, "nothing is registered system-wide by default");
        // The second set has no Alt (the Mac's has no Option, VoiceOver's keys).
        Assert.Equal(0u, HotkeyModifiers.ControlShiftCommand.Flags() & HotkeyModifiersInfo.ModAlt);
        Assert.NotEqual(HotkeyModifiers.ControlOptionCommand.Flags(), HotkeyModifiers.ControlShiftCommand.Flags());
    }

    /// <summary>
    /// Windows' own: neither set is Ctrl+Alt without Win, which layouts with AltGr type characters with (ł, €, [ on a
    /// German keyboard): registering it would take keys people type.
    /// </summary>
    [Fact]
    public void NeitherKeySetTakesAltGrCharacters()
    {
        foreach (var modifiers in HotkeyModifiersInfo.All)
            Assert.Equal(HotkeyModifiersInfo.ModWin, modifiers.Flags() & HotkeyModifiersInfo.ModWin);
    }

    // Ported from LyricExtrasTests.swift: testAShortcutAnotherAppHoldsIsReportedAndTheRestStillRegister. The Mac's menu
    // key equivalents are Windows' ActiveLabel (menus and tooltips name only a key that works).
    [Fact]
    public void TestAShortcutAnotherAppHoldsIsReportedAndTheRestStillRegister()
    {
        var store = new Store();
        var fake = new FakeRegistrar();
        fake.Take(HotkeyAction.ToggleLyrics, HotkeyModifiers.ControlOptionCommand);
        var hotkeys = Hotkeys(store, fake);
        hotkeys.SetEnabled(true);
        Assert.Equal(new[] { HotkeyAction.ToggleLyrics }, hotkeys.Unavailable.ToArray()); // an exclusive registration that fails is said, not hidden
        Assert.Equal(OnByDefault.Except([HotkeyAction.ToggleLyrics]).ToHashSet(), hotkeys.Active.ToHashSet());
        Assert.All(fake.Attempts, a => Assert.Equal(HotkeyModifiers.ControlOptionCommand.Flags(), a.Modifiers));
        Assert.Null(hotkeys.ActiveLabel(HotkeyAction.ToggleLyrics)); // the menus don't advertise a key that won't work
        Assert.Equal("Win+Ctrl+Alt+P", hotkeys.ActiveLabel(HotkeyAction.PauseWallpaper));

        // The other app quits: the combination is taken up.
        fake.Taken = [];
        hotkeys.RetryUnavailable();
        Assert.Empty(hotkeys.Unavailable);
        Assert.Contains(HotkeyAction.ToggleLyrics, hotkeys.Active);

        hotkeys.SetEnabled(false);
        Assert.Empty(fake.Held); // switching off releases every combination
        Assert.Empty(hotkeys.Active);
        Assert.False(hotkeys.Registered);
    }

    // Ported from LyricExtrasTests.swift: testEachShortcutSwitchesOffOnItsOwnAndTheKeysCanChange.
    [Fact]
    public void TestEachShortcutSwitchesOffOnItsOwnAndTheKeysCanChange()
    {
        var store = new Store();
        var fake = new FakeRegistrar();
        var hotkeys = Hotkeys(store, fake);
        hotkeys.SetSwitchedOn(false, HotkeyAction.SyncEarlier);
        hotkeys.SetEnabled(true);
        Assert.DoesNotContain(HotkeyAction.SyncEarlier, hotkeys.Active);
        Assert.DoesNotContain((int)HotkeyAction.SyncEarlier, fake.Held);
        hotkeys.SetSwitchedOn(false, HotkeyAction.SyncLater);
        Assert.DoesNotContain((int)HotkeyAction.SyncLater, fake.Held); // released at once
        hotkeys.SetSwitchedOn(true, HotkeyAction.SyncEarlier);
        Assert.Contains(HotkeyAction.SyncEarlier, hotkeys.Active); // taken up at once

        hotkeys.Modifiers = HotkeyModifiers.ControlShiftCommand;
        Assert.Equal(HotkeyModifiers.ControlShiftCommand.Flags(), fake.Attempts[^1].Modifiers);
        Assert.Equal(OnByDefault.Count - 1, hotkeys.Active.Count);
        Assert.StartsWith("Win+Ctrl+Shift+", hotkeys.Label(HotkeyAction.ToggleLyrics));

        var reloaded = Hotkeys(store, new FakeRegistrar());
        Assert.Equal(HotkeyModifiers.ControlShiftCommand, reloaded.Modifiers);
        Assert.False(reloaded.IsSwitchedOn(HotkeyAction.SyncLater));
        Assert.True(reloaded.IsSwitchedOn(HotkeyAction.SyncEarlier));
        hotkeys.SetEnabled(false);
    }

    // Ported from LyricExtrasTests.swift: testShortcutLabelsNameTheKeyOnTheLayoutInUse. Windows' labels spell the
    // modifiers out, so the key that follows them is what is upper-case.
    [Fact]
    public void TestShortcutLabelsNameTheKeyOnTheLayoutInUse()
    {
        var hotkeys = Hotkeys(new Store(), new FakeRegistrar());
        foreach (var shortcut in GlobalHotkeys.Shortcuts)
        {
            var label = hotkeys.Label(shortcut.Action);
            Assert.NotNull(label);
            Assert.StartsWith("Win+Ctrl+Alt+", label);
            var key = label["Win+Ctrl+Alt+".Length..];
            Assert.NotEmpty(key); // a key follows the modifiers
            Assert.Equal(key.ToUpperInvariant(), key);
        }
        // The caption under those rows never names the sync keys as a US keyboard prints them, which would contradict
        // the rows elsewhere.
        foreach (var key in new[] { "[", "]" }) Assert.DoesNotContain(key, ShortcutsCopy.Caption, StringComparison.Ordinal);
        Assert.Contains("arrow keys", ShortcutsCopy.Caption, StringComparison.Ordinal);
    }

    /// <summary>Windows' own: a German layout's key at the [ position names the label (Ü), as the Mac's doc says.</summary>
    [Fact]
    public void LabelsUseTheCharacterTheLayoutPutsAtThatPosition()
    {
        var german = new Dictionary<uint, string> { [0x1A] = "ü", [0x1B] = "+" };
        var hotkeys = Hotkeys(new Store(), new FakeRegistrar(), scan => german.GetValueOrDefault(scan));
        Assert.Equal("Win+Ctrl+Alt+Ü", hotkeys.Label(HotkeyAction.SyncEarlier));
        Assert.Equal("Win+Ctrl+Alt++", hotkeys.Label(HotkeyAction.SyncLater));
        Assert.Equal("Win+Ctrl+Alt+L", hotkeys.Label(HotkeyAction.ToggleLyrics)); // a key the layout doesn't name: the US one
    }

    // Ported from LyricTapSyncTests.swift: testNewShortcutsWaitToBeSwitchedOnAndRemembered.
    [Fact]
    public void TestNewShortcutsWaitToBeSwitchedOnAndRemembered()
    {
        var store = new Store();
        var registrar = new FakeRegistrar();
        var hotkeys = Hotkeys(store, registrar);
        foreach (var action in new[] { HotkeyAction.TapSyncLyrics, HotkeyAction.ToggleMusicVideo, HotkeyAction.NextVisualizer })
        {
            Assert.False(action.IsOnByDefault());
            Assert.False(hotkeys.IsSwitchedOn(action)); // off until switched on
        }
        Assert.True(hotkeys.IsSwitchedOn(HotkeyAction.ToggleLyrics));
        hotkeys.SetEnabled(true);
        Assert.DoesNotContain(HotkeyAction.TapSyncLyrics, hotkeys.Active);
        hotkeys.SetSwitchedOn(true, HotkeyAction.TapSyncLyrics);
        Assert.Contains(HotkeyAction.TapSyncLyrics, hotkeys.Active);
        hotkeys.SetSwitchedOn(false, HotkeyAction.CopyCard);
        var reloaded = Hotkeys(store, registrar);
        Assert.True(reloaded.IsSwitchedOn(HotkeyAction.TapSyncLyrics));
        Assert.False(reloaded.IsSwitchedOn(HotkeyAction.ToggleMusicVideo));
        Assert.False(reloaded.IsSwitchedOn(HotkeyAction.CopyCard));
        Assert.True(reloaded.IsSwitchedOn(HotkeyAction.ToggleLyrics));
        hotkeys.SetEnabled(false);
    }

    /// <summary>Windows' own: with the settings a new install has, following them registers nothing at all.</summary>
    [Fact]
    public void DefaultSettingsTakeNoKeys()
    {
        var store = new Store();
        var fake = new FakeRegistrar();
        var hotkeys = Hotkeys(store, fake);
        hotkeys.Sync(store.Current);
        Assert.Empty(fake.Attempts);
        Assert.False(hotkeys.Registered);
        Assert.Null(hotkeys.Pressed((nuint)HotkeyAction.ToggleLyrics));
        Assert.Equal("Global shortcuts off: none registered.", hotkeys.Describe());
    }

    /// <summary>
    /// Windows' own: the settings page writes the store and the shortcuts follow it (the master switch, the keys, a
    /// row's switch), registering, swapping and releasing exactly what changed.
    /// </summary>
    [Fact]
    public void FollowingTheSettingsRegistersSwapsAndReleases()
    {
        var store = new Store();
        var fake = new FakeRegistrar();
        var hotkeys = Hotkeys(store, fake);
        var changes = 0;
        hotkeys.Changed += () => changes++;

        store.Update(s => s with { GlobalShortcuts = true });
        hotkeys.Sync(store.Current);
        Assert.True(hotkeys.Registered);
        Assert.Equal(OnByDefault.Select(a => (int)a).ToHashSet(), fake.Held);
        Assert.True(changes > 0);

        store.Update(s => s with { GlobalShortcutModifiers = "controlShiftCommand" });
        hotkeys.Sync(store.Current);
        Assert.Equal(HotkeyModifiers.ControlShiftCommand, hotkeys.Modifiers);
        Assert.Equal(HotkeyModifiers.ControlShiftCommand.Flags(), fake.Attempts[^1].Modifiers);

        store.Update(s => s with { GlobalShortcutsOff = ["pauseWallpaper"], GlobalShortcutsOn = ["nextVisualizer"] });
        hotkeys.Sync(store.Current);
        Assert.DoesNotContain((int)HotkeyAction.PauseWallpaper, fake.Held);
        Assert.Contains((int)HotkeyAction.NextVisualizer, fake.Held);

        var attempts = fake.Attempts.Count;
        hotkeys.Sync(store.Current); // nothing changed: nothing is registered again
        Assert.Equal(attempts, fake.Attempts.Count);

        store.Update(s => s with { GlobalShortcuts = false });
        hotkeys.Sync(store.Current);
        Assert.Empty(fake.Held);
        Assert.False(hotkeys.Registered);
    }

    /// <summary>Windows' own: a press runs only a live shortcut of ours; another id, or a switched-off one, runs nothing.</summary>
    [Fact]
    public void OnlyLiveShortcutsAnswerAPress()
    {
        var store = new Store();
        var fake = new FakeRegistrar();
        var hotkeys = Hotkeys(store, fake);
        Assert.Null(hotkeys.Pressed((nuint)HotkeyAction.ToggleLyrics)); // not registered
        hotkeys.SetEnabled(true);
        Assert.Equal(HotkeyAction.ToggleLyrics, hotkeys.Pressed((nuint)HotkeyAction.ToggleLyrics));
        Assert.Null(hotkeys.Pressed((nuint)HotkeyAction.NextVisualizer)); // waits to be switched on
        Assert.Null(hotkeys.Pressed(0));
        Assert.Null(hotkeys.Pressed(99));
        hotkeys.SetSwitchedOn(false, HotkeyAction.ToggleLyrics);
        Assert.Null(hotkeys.Pressed((nuint)HotkeyAction.ToggleLyrics));
    }

    // Ported from LyricExtrasTests.swift: testFeaturePreferencesPersistInIsolatedDefaults (the parts Windows has: the
    // shortcuts are opt-in and Hide lyrics is kept), with the Mac's keys without "custom.".
    [Fact]
    public void TestFeaturePreferencesPersistInIsolatedDefaults()
    {
        var settings = new AppSettings();
        Assert.False(settings.GlobalShortcuts); // global shortcuts are opt-in
        Assert.False(settings.LyricsHidden);
        var json = JsonSerializer.Serialize(settings with
        {
            LyricsHidden = true,
            GlobalShortcuts = true,
            GlobalShortcutModifiers = "controlShiftCommand",
            GlobalShortcutsOff = ["copyCard"],
            GlobalShortcutsOn = ["tapSyncLyrics"],
        }, AppSettingsJsonContext.Default.AppSettings);
        foreach (var key in new[] { "\"lyricsHidden\": true", "\"globalShortcuts\": true", "\"globalShortcutModifiers\": \"controlShiftCommand\"",
                                    "\"globalShortcutsOff\"", "\"globalShortcutsOn\"" })
            Assert.Contains(key, json, StringComparison.Ordinal);
        var reloaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!.Normalized();
        Assert.True(reloaded.LyricsHidden);
        Assert.True(reloaded.GlobalShortcuts);
        Assert.Equal(new[] { "copyCard" }, reloaded.GlobalShortcutsOff);
    }

    /// <summary>Windows' own: unknown keys and names read from disk are repaired, never kept.</summary>
    [Fact]
    public void UnknownValuesAreRepaired()
    {
        var repaired = new AppSettings
        {
            GlobalShortcutModifiers = "hyper",
            GlobalShortcutsOff = ["copyCard", "copyCard", "quit"],
            GlobalShortcutsOn = ["nextVisualizer", "launchRockets"],
        }.Normalized();
        Assert.Equal("controlOptionCommand", repaired.GlobalShortcutModifiers);
        Assert.Equal(new[] { "copyCard" }, repaired.GlobalShortcutsOff);
        Assert.Equal(new[] { "nextVisualizer" }, repaired.GlobalShortcutsOn);
    }

    // Ported from SettingsFollowupTests.swift: testShortcutCaptionNamesTheCardShortcutAsItsRowDoes (Windows has no Share
    // page for the caption to name; it names Timing, where the song offset is).
    [Fact]
    public void TestShortcutCaptionNamesTheCardShortcutAsItsRowDoes()
    {
        Assert.Contains(HotkeyAction.CopyCard.Title(), ShortcutsCopy.Caption, StringComparison.Ordinal);
        Assert.DoesNotContain("copy a lyric card", ShortcutsCopy.Caption, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Timing", ShortcutsCopy.Caption, StringComparison.Ordinal);
    }

    // Ported from ActionHUDTests.swift: testTheCardShortcutSaysItOpensTheCard.
    [Fact]
    public void TestTheCardShortcutSaysItOpensTheCard() => Assert.Equal("Open lyric card", HotkeyAction.CopyCard.Title());
}

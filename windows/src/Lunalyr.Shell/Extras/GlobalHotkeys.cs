// System-wide shortcuts, ported from the Mac's GlobalHotkeys (GlobalHotkeys.swift, Lunalyr's own lines; blame-checked
// against a99fd65: none). The Mac registers Carbon hot keys; Windows registers RegisterHotKey on the app's hidden window
// (Lunalyr.App's Win32HotkeyRegistrar). Both take only these exact combinations, never anything else typed, and need no
// permission. What is registered, what another app holds and what was switched off is worked out here, against a
// registrar the tests replace, so no test ever holds a key system-wide.

using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Extras;

/// <summary>What a global shortcut does (the Mac's <c>GlobalHotkeys.Action</c>, its raw values as the hot key ids).</summary>
public enum HotkeyAction
{
    ToggleLyrics = 1,
    SyncEarlier,
    SyncLater,
    CycleLayout,
    CopyCard,
    PauseWallpaper,
    TapSyncLyrics,
    ToggleMusicVideo,
    NextVisualizer,
}

public static class HotkeyActions
{
    /// <summary>Every action, in the Mac's order (the settings rows' order).</summary>
    public static IReadOnlyList<HotkeyAction> All { get; } = Enum.GetValues<HotkeyAction>();

    public static string Title(this HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleLyrics => "Show or hide lyrics",
        HotkeyAction.SyncEarlier => "Lyrics 0.1 s earlier for this song",
        HotkeyAction.SyncLater => "Lyrics 0.1 s later for this song",
        HotkeyAction.CycleLayout => "Next layout",
        HotkeyAction.CopyCard => "Open lyric card",
        HotkeyAction.PauseWallpaper => "Pause or resume the wallpaper",
        HotkeyAction.TapSyncLyrics => "Tap to sync lyrics",
        HotkeyAction.ToggleMusicVideo => "Music video on or off",
        HotkeyAction.NextVisualizer => "Next visualiser",
        _ => action.ToString(),
    };

    /// <summary>How a switched-off (or switched-on) shortcut is remembered.</summary>
    public static string StorageName(this HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleLyrics => "toggleLyrics",
        HotkeyAction.SyncEarlier => "syncEarlier",
        HotkeyAction.SyncLater => "syncLater",
        HotkeyAction.CycleLayout => "cycleLayout",
        HotkeyAction.CopyCard => "copyCard",
        HotkeyAction.PauseWallpaper => "pauseWallpaper",
        HotkeyAction.TapSyncLyrics => "tapSyncLyrics",
        HotkeyAction.ToggleMusicVideo => "toggleMusicVideo",
        HotkeyAction.NextVisualizer => "nextVisualizer",
        _ => action.ToString(),
    };

    public static HotkeyAction? FromStorageName(string name)
    {
        foreach (var action in All)
            if (action.StorageName() == name) return action;
        return null;
    }

    /// <summary>
    /// The first six are on (once the shortcuts are) unless switched off; later ones wait to be switched on, so an
    /// update takes no new keys from other apps.
    /// </summary>
    public static bool IsOnByDefault(this HotkeyAction action) =>
        action is not (HotkeyAction.TapSyncLyrics or HotkeyAction.ToggleMusicVideo or HotkeyAction.NextVisualizer);
}

/// <summary>
/// The modifier keys every shortcut shares. The Mac's ⌃⌥⌘ and ⌃⇧⌘ by key position: Windows' Win key sits where the
/// Mac's Command does. Neither set is Ctrl+Alt on its own, which many keyboard layouts type as AltGr (registering it
/// would take characters people type, such as ł or €).
/// </summary>
public enum HotkeyModifiers
{
    /// <summary>Win+Ctrl+Alt (the Mac's Control-Option-Command).</summary>
    ControlOptionCommand,

    /// <summary>Win+Ctrl+Shift (the Mac's Control-Shift-Command): no Alt, for when another app holds the first set.</summary>
    ControlShiftCommand,
}

public static class HotkeyModifiersInfo
{
    // RegisterHotKey's modifier flags.
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    public static IReadOnlyList<HotkeyModifiers> All { get; } = Enum.GetValues<HotkeyModifiers>();

    public static string RawValue(this HotkeyModifiers modifiers) =>
        modifiers == HotkeyModifiers.ControlOptionCommand ? "controlOptionCommand" : "controlShiftCommand";

    public static HotkeyModifiers? Parse(string? raw) => raw switch
    {
        "controlOptionCommand" => HotkeyModifiers.ControlOptionCommand,
        "controlShiftCommand" => HotkeyModifiers.ControlShiftCommand,
        _ => null,
    };

    /// <summary>What a label starts with: "Win+Ctrl+Alt+", Windows' own order.</summary>
    public static string Prefix(this HotkeyModifiers modifiers) =>
        modifiers == HotkeyModifiers.ControlOptionCommand ? "Win+Ctrl+Alt+" : "Win+Ctrl+Shift+";

    /// <summary>The Keys menu's titles.</summary>
    public static string Title(this HotkeyModifiers modifiers) =>
        modifiers == HotkeyModifiers.ControlOptionCommand ? "Win+Ctrl+Alt" : "Win+Ctrl+Shift";

    /// <summary>RegisterHotKey's flags, without <see cref="ModNoRepeat"/> (the registrar adds it).</summary>
    public static uint Flags(this HotkeyModifiers modifiers) =>
        modifiers == HotkeyModifiers.ControlOptionCommand ? ModWin | ModControl | ModAlt : ModWin | ModControl | ModShift;
}

/// <summary>
/// One shortcut: its action, the key by position (a set-1 scan code, as the Mac keeps a key code) and the key as a US
/// keyboard prints it. Labels use the keyboard layout in use, so on a German one Win+Ctrl+Alt+[ shows as Win+Ctrl+Alt+Ü.
/// </summary>
public sealed record HotkeyShortcut(HotkeyAction Action, uint ScanCode, string Key);

/// <summary>Takes and releases combinations. The app's registers with Windows; tests use a stand-in.</summary>
public interface IHotkeyRegistrar
{
    /// <summary>
    /// Registers the key at <paramref name="scanCode"/> with <paramref name="modifiers"/> (RegisterHotKey flags) as
    /// <paramref name="id"/>. False when another app already holds the combination (or the key isn't on this keyboard).
    /// </summary>
    bool Register(uint scanCode, uint modifiers, int id);

    void Unregister(int id);
}

/// <summary>
/// The global shortcuts: off until switched on in Settings, and each can be switched off on its own. They keep clear of
/// the arrow keys, which Windows uses with Win and Ctrl+Win for snapping and virtual desktops, so the sync nudges use
/// [ and ]. RegisterHotKey is exclusive: a combination another app holds fails to register, and the settings page says
/// so, instead of both apps acting on one press. Call it on the thread that owns the registrar's window.
/// </summary>
public sealed class GlobalHotkeys
{
    /// <summary>The shortcuts, by key position (scan codes of a PC keyboard).</summary>
    public static IReadOnlyList<HotkeyShortcut> Shortcuts { get; } =
    [
        new(HotkeyAction.ToggleLyrics, 0x26, "L"),
        new(HotkeyAction.SyncEarlier, 0x1A, "["),
        new(HotkeyAction.SyncLater, 0x1B, "]"),
        new(HotkeyAction.CycleLayout, 0x25, "K"),
        new(HotkeyAction.CopyCard, 0x2E, "C"),
        new(HotkeyAction.PauseWallpaper, 0x19, "P"),
        new(HotkeyAction.TapSyncLyrics, 0x14, "T"),
        new(HotkeyAction.ToggleMusicVideo, 0x2F, "V"),
        new(HotkeyAction.NextVisualizer, 0x31, "N"),
    ];

    public static HotkeyShortcut? ShortcutFor(HotkeyAction action) => Shortcuts.FirstOrDefault(s => s.Action == action);

    private readonly Func<AppSettings> _read;
    private readonly Func<Func<AppSettings, AppSettings>, bool> _update;
    private readonly IHotkeyRegistrar _registrar;
    private readonly Func<uint, string?> _keyName;
    private readonly Action<string>? _log;
    private HotkeyModifiers _modifiers;
    private HashSet<HotkeyAction> _switchedOff;
    private HashSet<HotkeyAction> _unavailable = [];
    private HashSet<HotkeyAction> _active = [];

    /// <param name="read">The settings now.</param>
    /// <param name="update">Changes the settings (the store's <c>Update</c>).</param>
    /// <param name="registrar">Windows' registration in the app; a stand-in in tests.</param>
    /// <param name="keyName">The character a key (by scan code) types on the layout in use, or null to use the US key.</param>
    /// <param name="log">Where registration results go.</param>
    public GlobalHotkeys(Func<AppSettings> read, Func<Func<AppSettings, AppSettings>, bool> update, IHotkeyRegistrar registrar,
                         Func<uint, string?>? keyName = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(registrar);
        _read = read;
        _update = update;
        _registrar = registrar;
        _keyName = keyName ?? (_ => null);
        _log = log;
        var settings = read();
        _modifiers = HotkeyModifiersInfo.Parse(settings.GlobalShortcutModifiers) ?? HotkeyModifiers.ControlOptionCommand;
        _switchedOff = SwitchedOffIn(settings);
    }

    /// <summary>Something the settings page shows changed: what is registered, taken or switched off.</summary>
    public event Action? Changed;

    /// <summary>Global shortcuts are switched on and registration has run.</summary>
    public bool Registered { get; private set; }

    /// <summary>Shortcuts the user switched off one by one (and those still waiting to be switched on).</summary>
    public IReadOnlySet<HotkeyAction> SwitchedOff => _switchedOff;

    /// <summary>Combinations another app already holds, so they are not registered.</summary>
    public IReadOnlySet<HotkeyAction> Unavailable => _unavailable;

    /// <summary>Combinations registered now.</summary>
    public IReadOnlySet<HotkeyAction> Active => _active;

    /// <summary>The modifier keys. Setting it saves the choice and registers every shortcut again with the new keys.</summary>
    public HotkeyModifiers Modifiers
    {
        get => _modifiers;
        set
        {
            if (value == _modifiers) return;
            _modifiers = value;
            _update(s => s with { GlobalShortcutModifiers = value.RawValue() });
            Reregister();
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (Registered) return;
            Registered = true;
            RegisterMissing();
        }
        else if (Registered)
        {
            Unregister();
        }
    }

    public bool IsSwitchedOn(HotkeyAction action) => !_switchedOff.Contains(action);

    public void SetSwitchedOn(bool on, HotkeyAction action)
    {
        if (on) _switchedOff.Remove(action);
        else _switchedOff.Add(action);
        string[] off = [.. _switchedOff.Where(a => a.IsOnByDefault()).Select(a => a.StorageName()).Order(StringComparer.Ordinal)];
        string[] onList = [.. HotkeyActions.All.Where(a => !a.IsOnByDefault() && !_switchedOff.Contains(a)).Select(a => a.StorageName())];
        _update(s => s with { GlobalShortcutsOff = off, GlobalShortcutsOn = onList });
        if (!Registered)
        {
            Changed?.Invoke();
            return;
        }
        if (!on)
        {
            if (_active.Remove(action)) _registrar.Unregister((int)action);
            _unavailable.Remove(action);
        }
        RegisterMissing(force: true);
    }

    /// <summary>
    /// Follows settings changed elsewhere (the settings page writes the store; another window, a restore): the master
    /// switch, the keys and the switched-off lists. Doing nothing when nothing changed, it is safe to call on every change.
    /// </summary>
    public void Sync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var modifiers = HotkeyModifiersInfo.Parse(settings.GlobalShortcutModifiers) ?? HotkeyModifiers.ControlOptionCommand;
        var off = SwitchedOffIn(settings);
        if (!settings.GlobalShortcuts) SetEnabled(false);
        if (modifiers != _modifiers)
        {
            _modifiers = modifiers;
            Reregister();
        }
        if (!off.SetEquals(_switchedOff))
        {
            foreach (var action in off.Except(_switchedOff).ToList())
            {
                if (_active.Remove(action)) _registrar.Unregister((int)action);
                _unavailable.Remove(action);
            }
            _switchedOff = off;
            if (Registered) RegisterMissing(force: true);
            else Changed?.Invoke();
        }
        if (settings.GlobalShortcuts) SetEnabled(true);
    }

    /// <summary>A combination another app held may be free again (that app quit): try those once more.</summary>
    public void RetryUnavailable()
    {
        if (!Registered || _unavailable.Count == 0) return;
        RegisterMissing();
    }

    /// <summary>A press of hot key <paramref name="id"/>: the action to run, or null when it isn't one of ours that is live.</summary>
    public HotkeyAction? Pressed(nuint id)
    {
        if (!Registered || id == 0 || id > (nuint)HotkeyActions.All.Count) return null;
        var action = (HotkeyAction)(int)id;
        return _active.Contains(action) ? action : null;
    }

    // ---- Labels ----

    /// <summary>"Win+Ctrl+Alt+L", with the key as the keyboard layout in use types it.</summary>
    public string? Label(HotkeyAction action) => ShortcutFor(action) is { } shortcut ? _modifiers.Prefix() + KeyName(shortcut) : null;

    /// <summary>The combination while it is registered, for menus and tooltips that offer the action.</summary>
    public string? ActiveLabel(HotkeyAction action) => _active.Contains(action) ? Label(action) : null;

    public string KeyName(HotkeyShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        var name = _keyName(shortcut.ScanCode);
        return string.IsNullOrWhiteSpace(name) ? shortcut.Key : name.Trim().ToUpperInvariant();
    }

    // ---- Registration ----

    /// <summary>Registers every switched-on shortcut that is not registered yet.</summary>
    private void RegisterMissing(bool force = false)
    {
        var live = new HashSet<HotkeyAction>(_active);
        var missing = new HashSet<HotkeyAction>();
        foreach (var shortcut in Shortcuts)
        {
            if (!IsSwitchedOn(shortcut.Action) || live.Contains(shortcut.Action)) continue;
            if (_registrar.Register(shortcut.ScanCode, _modifiers.Flags(), (int)shortcut.Action)) live.Add(shortcut.Action);
            else missing.Add(shortcut.Action);
        }
        var changed = !live.SetEquals(_active) || !missing.SetEquals(_unavailable);
        _active = live;
        _unavailable = missing;
        if (changed)
        {
            _log?.Invoke(Describe());
        }
        if (changed || force) Changed?.Invoke();
    }

    private void Reregister()
    {
        if (!Registered)
        {
            Changed?.Invoke();
            return;
        }
        foreach (var action in _active) _registrar.Unregister((int)action);
        _active = [];
        _unavailable = [];
        RegisterMissing(force: true);
    }

    private void Unregister()
    {
        foreach (var action in _active) _registrar.Unregister((int)action);
        _active = [];
        _unavailable = [];
        Registered = false;
        _log?.Invoke("Global shortcuts off: none registered.");
        Changed?.Invoke();
    }

    /// <summary>The log's line: which combinations are live and which another app holds.</summary>
    public string Describe()
    {
        if (!Registered) return "Global shortcuts off: none registered.";
        var live = Shortcuts.Where(s => _active.Contains(s.Action)).Select(s => Label(s.Action)).ToList();
        var taken = Shortcuts.Where(s => _unavailable.Contains(s.Action)).Select(s => Label(s.Action)).ToList();
        var text = live.Count == 0 ? "Global shortcuts on: none registered" : $"Global shortcuts on: {string.Join(", ", live)}";
        return taken.Count == 0 ? text + "." : $"{text}; already used by another app: {string.Join(", ", taken)}.";
    }

    private static HashSet<HotkeyAction> SwitchedOffIn(AppSettings settings)
    {
        var off = new HashSet<string>(settings.GlobalShortcutsOff ?? [], StringComparer.Ordinal);
        var on = new HashSet<string>(settings.GlobalShortcutsOn ?? [], StringComparer.Ordinal);
        return [.. HotkeyActions.All.Where(a => a.IsOnByDefault() ? off.Contains(a.StorageName()) : !on.Contains(a.StorageName()))];
    }
}

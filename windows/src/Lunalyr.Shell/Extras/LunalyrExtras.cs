// What a global shortcut or a lunalyr:// link does, and the brief note that confirms it: ported from the Mac's
// LunalyrExtras (its actions, notes and note origins), LunalyrExtrasShortcuts.swift (music video, next visualiser) and
// LyricExtrasOptions' LyricComposition.cycled (Lunalyr's own lines; blame-checked against a99fd65: the two Verso lines
// in LunalyrExtras.swift are a screen observer for mirroring, not used here). Windows has no mirroring, no per-song style
// memory and no wallpaper note of its own (the Mac draws one on the wallpaper): the action HUD is where notes are seen.
// Nothing here sends a playback command: none of these actions touches the player.

using Lunalyr.Core.Visualisers;
using Lunalyr.Shell.Flyout;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Extras;

/// <summary>Where an action came from, which decides whether its note goes to the action HUD.</summary>
public enum NoteOrigin
{
    /// <summary>A control in a Lunalyr window or menu the user is looking at.</summary>
    Window,

    /// <summary>A global shortcut, a <c>lunalyr://</c> link, or something that happened on its own.</summary>
    Elsewhere,
}

/// <summary>A short confirmation of what just happened.</summary>
/// <param name="InHud">The action HUD showed it.</param>
public sealed record ExtrasNote(string Text, string Symbol, DateTimeOffset At, bool InHud);

/// <summary>
/// What the extras need from the rest of the app, each optional so tests give only what they use. The app wires the
/// controller, the windows and the HUD.
/// </summary>
public sealed record ExtrasHooks
{
    /// <summary>Moves this song's lyrics by the delta (positive: earlier); the saved offset, or null with no song.</summary>
    public Func<double, double?>? NudgeSync { get; init; }

    /// <summary>A song is playing (on screen).</summary>
    public Func<bool>? HasSong { get; init; }

    /// <summary>The song playing, once its length is known (its remembered style is changed in place), or null.</summary>
    public Func<Core.Lyrics.LyricQuery?>? PlayingSong { get; init; }

    /// <summary>Opens the lyric card window for the line playing; false when this build has none.</summary>
    public Func<bool>? OpenLyricCard { get; init; }

    /// <summary>Starts Tap to sync for the song playing (its panel takes the keyboard), or stops it.</summary>
    public Action? ToggleTapSync { get; init; }

    /// <summary>Opens the settings window.</summary>
    public Action? OpenSettings { get; init; }

    /// <summary>The wallpaper was just paused (true) or resumed (false) here.</summary>
    public Action<bool>? PauseChanged { get; init; }

    /// <summary>Where every note is written down.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The actions global shortcuts and links run, each saying what it did. Every action changes the settings through the
/// store (so the wallpaper, the floating lyrics and the settings window follow) or asks the controller. Call it on the
/// app's UI thread.
/// </summary>
public sealed class LunalyrExtras
{
    private readonly Func<AppSettings> _read;
    private readonly Func<Func<AppSettings, AppSettings>, bool> _update;
    private readonly ExtrasHooks _hooks;
    private readonly TimeProvider _time;

    public LunalyrExtras(Func<AppSettings> read, Func<Func<AppSettings, AppSettings>, bool> update, ExtrasHooks? hooks = null,
                         TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(update);
        _read = read;
        _update = update;
        _hooks = hooks ?? new ExtrasHooks();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Shows a note in the action HUD. Nothing until the app sets it (so unit tests never open a window).</summary>
    public Action<string, string>? PresentHud { get; set; }

    /// <summary>Says each note to assistive technology. Nothing until the app sets one.</summary>
    public Action<string>? Announce { get; set; }

    /// <summary>Whether the wallpaper where the user is looking is covered by windows. False until the app sets it.</summary>
    public Func<bool> WallpaperCovered { get; set; } = static () => false;

    /// <summary>The last note posted.</summary>
    public ExtrasNote? Note { get; private set; }

    /// <summary>The origin of the action running now; see <see cref="Performing{T}"/>.</summary>
    public NoteOrigin ActionOrigin { get; private set; } = NoteOrigin.Window;

    /// <summary>The user's own Hide lyrics is off (Windows has no mirroring, which also hides them on the Mac).</summary>
    public bool LyricsVisible => !_read().LyricsHidden;

    // ---- Notes ----

    /// <summary>
    /// Runs <paramref name="body"/> as an action from <paramref name="origin"/>: a global shortcut or link runs its action
    /// through here, so the note it posts shows in the action HUD.
    /// </summary>
    public T Performing<T>(NoteOrigin origin, Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var outer = ActionOrigin;
        ActionOrigin = origin;
        try
        {
            return body();
        }
        finally
        {
            ActionOrigin = outer;
        }
    }

    public void Performing(NoteOrigin origin, Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Performing(origin, () =>
        {
            body();
            return 0;
        });
    }

    /// <summary>
    /// Whether a note belongs in the action HUD: the action came from outside Lunalyr's windows, or the wallpaper is
    /// paused (it draws nothing new) or covered where the user is looking.
    /// </summary>
    public static bool ShowsInHud(NoteOrigin origin, bool wallpaperPaused, bool wallpaperCovered) =>
        origin == NoteOrigin.Elsewhere || wallpaperPaused || wallpaperCovered;

    /// <summary>
    /// Confirms what just happened: to assistive technology, in the log, and in the action HUD when the wallpaper is
    /// unlikely to be seen. <paramref name="origin"/> defaults to that of the action running.
    /// </summary>
    public void Post(string text, string symbol, NoteOrigin? origin = null)
    {
        var inHud = ShowsInHud(origin ?? ActionOrigin, _read().WallpaperPaused, WallpaperCovered());
        if (inHud) PresentHud?.Invoke(text, symbol);
        Note = new ExtrasNote(text, symbol, _time.GetUtcNow(), inHud && PresentHud is not null);
        Announce?.Invoke(text);
        _hooks.Log?.Invoke($"{text}.");
    }

    // ---- Running commands ----

    /// <summary>Runs a link's command, as its global shortcut does; its note goes to the HUD.</summary>
    public void Run(LunalyrCommand command) => Performing(NoteOrigin.Elsewhere, () =>
    {
        switch (command)
        {
            case LunalyrCommand.ToggleLyrics: ToggleLyrics(); break;
            case LunalyrCommand.SyncEarlier: NudgeSync(AppController.NudgeStep); break;
            case LunalyrCommand.SyncLater: NudgeSync(-AppController.NudgeStep); break;
            case LunalyrCommand.CycleLayout: CycleLayout(); break;
            case LunalyrCommand.LyricCard: CopyLyricCard(); break;
            case LunalyrCommand.PauseWallpaper: ToggleWallpaperPause(); break;
            case LunalyrCommand.OpenSettings: OpenSettings(); break;
            case LunalyrCommand.ToggleFloating: ToggleFloating(); break;
        }
    });

    /// <summary>A global shortcut was pressed in another app: its note goes to the HUD.</summary>
    public void Perform(HotkeyAction action) => Performing(NoteOrigin.Elsewhere, () =>
    {
        switch (action)
        {
            case HotkeyAction.ToggleLyrics: ToggleLyrics(); break;
            case HotkeyAction.SyncEarlier: NudgeSync(AppController.NudgeStep); break;
            case HotkeyAction.SyncLater: NudgeSync(-AppController.NudgeStep); break;
            case HotkeyAction.CycleLayout: CycleLayout(); break;
            case HotkeyAction.CopyCard: CopyLyricCard(); break;
            case HotkeyAction.PauseWallpaper: ToggleWallpaperPause(); break;
            case HotkeyAction.TapSyncLyrics: _hooks.ToggleTapSync?.Invoke(); break;
            case HotkeyAction.ToggleMusicVideo: ToggleMusicVideo(); break;
            case HotkeyAction.NextVisualizer: NextVisualizer(); break;
        }
    });

    // ---- Actions ----

    /// <summary>Hides the lyrics if they are showing, and otherwise shows them.</summary>
    public void ToggleLyrics()
    {
        if (LyricsVisible) HideLyrics();
        else ShowLyrics();
    }

    public void HideLyrics()
    {
        _update(s => s with { LyricsHidden = true });
        Post("Lyrics hidden", "eye.slash");
    }

    public void ShowLyrics()
    {
        _update(s => s with { LyricsHidden = false });
        Post("Lyrics shown", "eye");
    }

    /// <summary>
    /// Pauses or resumes every wallpaper. A paused desktop draws nothing new, so its note shows in the HUD even from a
    /// Lunalyr window; the note shows the state just reached, not the action that would undo it.
    /// </summary>
    public void ToggleWallpaperPause()
    {
        _update(s => s with { WallpaperPaused = !s.WallpaperPaused });
        var paused = _read().WallpaperPaused;
        _hooks.PauseChanged?.Invoke(paused);
        Post(paused ? "Wallpaper paused" : "Wallpaper resumed", PauseStateSymbol(paused));
    }

    /// <summary>The symbol for the state the wallpaper is in (a paused screen, unlike the music transport's pause glyph).</summary>
    public static string PauseStateSymbol(bool paused) => paused ? "pause.rectangle.fill" : "play.rectangle.fill";

    /// <summary>
    /// Moves the playing recording's lyrics by <paramref name="delta"/> seconds, saved with that recording and its lyric
    /// source, exactly like the flyout's Earlier and Later: positive brings the lyrics earlier.
    /// </summary>
    public double? NudgeSync(double delta)
    {
        if (_hooks.NudgeSync?.Invoke(delta) is not { } saved)
        {
            Post(FlyoutText.NudgeNeedsSong, "clock");
            return null;
        }
        Post(FlyoutText.NudgeNote(delta, saved), "clock.arrow.circlepath");
        return saved;
    }

    /// <summary>The next layout: Match the music, then each layout in turn, then Match the music again.</summary>
    public void CycleLayout()
    {
        // A layout remembered for this song or artist is cycled in place, so the change shows and stays with the music.
        var song = _hooks.PlayingSong?.Invoke();
        (string Layout, StyleScope Scope)? remembered = null;
        _update(s =>
        {
            if (StyleMemory.CycledRemembered(s, song, CycledLayout) is { } cycled)
            {
                remembered = (cycled.Layout, cycled.Scope);
                return cycled.Settings;
            }
            remembered = null;
            return s with { LyricComposition = CycledLayout(s.LyricComposition) };
        });
        if (remembered is { } kept)
        {
            Post($"Layout for this {StyleMemory.ScopeName(kept.Scope)}: {LayoutTitle(kept.Layout)}", "square.grid.3x3");
            return;
        }
        var id = _read().LyricComposition;
        Post($"Layout: {LayoutTitle(id)}", "square.grid.3x3");
    }

    /// <summary>The layout after <paramref name="current"/> (the Mac's <c>LyricComposition.cycled(after:)</c>).</summary>
    public static string CycledLayout(string? current)
    {
        var layouts = AppSettings.Compositions.Where(id => id != AppController.AutomaticComposition).ToList();
        if (current is null || current == AppController.AutomaticComposition) return layouts[0];
        var index = layouts.IndexOf(current);
        return index < 0 || index + 1 >= layouts.Count ? AppController.AutomaticComposition : layouts[index + 1];
    }

    private static string LayoutTitle(string id) => SettingsCopy.Layouts.FirstOrDefault(l => l.Value == id)?.Title ?? id;

    /// <summary>Opens the lyric card window for the song that is playing.</summary>
    public void CopyLyricCard()
    {
        if (_hooks.HasSong?.Invoke() != true)
        {
            Post("Play a song to make a lyric card", "photo");
            return;
        }
        if (_hooks.OpenLyricCard?.Invoke() != true) Post(LyricCardMissing, "photo");
    }

    /// <summary>Until the lyric card window is wired in (another part of milestone 7).</summary>
    public const string LyricCardMissing = "Lyric cards aren’t in this build yet";

    /// <summary>Music video on, or back to the type it replaced.</summary>
    public void ToggleMusicVideo()
    {
        if (_read().BackgroundMode == "musicVideo")
        {
            _update(s => s with { BackgroundMode = s.MusicVideoFallback });
            Post($"Music video off · {WallpaperTitle(_read().BackgroundMode)}", "film");
        }
        else
        {
            _update(s => AppSettings.RememberMusicVideoFallback(s, s with { BackgroundMode = "musicVideo" }));
            Post("Music video on", "film");
        }
    }

    private static string WallpaperTitle(string mode) => SettingsCopy.WallpaperTypes.FirstOrDefault(t => t.Value == mode)?.Title ?? mode;

    /// <summary>The next visualiser. With another background showing, the first press shows the visualiser chosen last.</summary>
    public void NextVisualizer()
    {
        if (_read().BackgroundMode != "visualizer") _update(s => s with { BackgroundMode = "visualizer" });
        else _update(s => s with { VisualizerStyle = VisualizerAfter(s.Visualizer()).RawValue() });
        Post($"Visualiser: {_read().Visualizer().Title()}", "waveform");
    }

    public static VisualizerStyle VisualizerAfter(VisualizerStyle style)
    {
        var all = VisualizerStyles.All;
        var index = -1;
        for (var i = 0; i < all.Count; i++) if (all[i] == style) index = i;
        return all[(index + 1) % all.Count];
    }

    /// <summary>
    /// Moves the lyrics between the desktop and a window over every app, each with its own style. The Mac posts no
    /// note here (its Shortcuts action answers instead); Windows says where the lyrics went, as links have no other answer.
    /// </summary>
    public void ToggleFloating()
    {
        _update(s => s.ToggleSurface());
        Post(_read().IsFloating ? "Lyrics floating over apps" : "Lyrics on the desktop", "macwindow");
    }

    public void OpenSettings() => _hooks.OpenSettings?.Invoke();
}

// Ported from the Mac's Sources/Player/DesktopPlayerView.swift (card geometry, title opacity, PlayerMotion),
// ScrubController.swift (OptimisticToggle, PlayerHoverFocus.look), DesktopScrubber.swift (ScrubRowKind, PlayerLook) and
// SleeveParts.swift (PlayerNoteToast.noteSeconds). Lunalyr's own code; the one Verso-attributed line in
// DesktopPlayerView.swift (an observed HoverState property) has no counterpart here.

namespace Lunalyr.Core.Player;

/// <summary>What the card's right-hand column shows: the song, the search, or the queue (search and queue are a later round).</summary>
public enum PlayerPane { Song, Search, Queue }

/// <summary>
/// The "Sleeve" card's geometry in DIPs: the cover on the left with its record peeking out, the song's column on the
/// right. The card is centred on its display at every size, so a taller pane grows it up and down alike.
/// </summary>
public static class PlayerCard
{
    public const double Width = 640;
    public const double Cover = 176;
    public const double Column = 364;
    public const double ArtWidth = 216;
    public const double Padding = 20;

    /// <summary>The Mac's corner radius. Windows draws the card in a window DWM rounds at its own 8 px (see APP-SHELL.md).</summary>
    public const double MacRadius = PlayerTokens.Radius.Card;

    /// <summary>The card at rest.</summary>
    public const double RestHeight = Cover + 2 * Padding;

    /// <summary>The cover with the song's title and the small transport under it (search or queue open).</summary>
    public const double CompactArtHeight = Cover + 96;

    /// <summary>The card at its tallest (the queue open).</summary>
    public static double TallestHeight => ColumnHeight(PlayerPane.Queue) + 2 * Padding;

    /// <summary>
    /// Where the card's top sits in a panel <paramref name="panelHeight"/> tall (centred on the screen): the card is
    /// centred at every size.
    /// </summary>
    public static double CardTop(double panelHeight, double cardHeight = RestHeight) => Math.Max(0, (panelHeight - cardHeight) / 2);

    /// <summary>
    /// The card's height for each pane: the taller of the cover column (the cover alone, or with the song and transport
    /// under it while search or the queue is open) and the right-hand column, with the padding.
    /// </summary>
    public static double CardHeight(PlayerPane pane, int queueRows = int.MaxValue) =>
        Math.Max(pane == PlayerPane.Song ? Cover : CompactArtHeight, ColumnHeight(pane, queueRows)) + 2 * Padding;

    /// <summary>
    /// The right-hand column's height for each pane. A queue of a song or two keeps to the height of the cover with the
    /// song and transport under it.
    /// </summary>
    public static double ColumnHeight(PlayerPane pane, int queueRows = int.MaxValue) => pane switch
    {
        PlayerPane.Song => Cover,
        PlayerPane.Search => 300,
        _ => queueRows <= 2 ? CompactArtHeight : 334,
    };

    /// <summary>The song's titles while the seek bar's capsules are up over them: all but gone, so the capsules read.</summary>
    public static double TitleOpacity(bool dimmed) => dimmed ? 0.08 : 1;

    /// <summary>Reveal and dismiss in seconds: in with the standard settle, out quicker as a fade; under Reduce Motion, short fades.</summary>
    public static double RevealSeconds(bool shown, bool reduceMotion) => PlayerTokens.Motion.Reveal(shown, reduceMotion);

    /// <summary>The rise the card makes as it appears (none under Reduce Motion), in DIPs.</summary>
    public static double RevealRise(bool reduceMotion) => reduceMotion ? 0 : PlayerTokens.Motion.Rise;

    /// <summary>Long enough to read a note over the card: 2.5 s for a short one, up to 7 s.</summary>
    public static double NoteSeconds(string? note) => Math.Min(7, Math.Max(2.5, (note?.Length ?? 0) / 14.0));
}

/// <summary>
/// The system's accessibility settings as the player draws them: on Windows "Animation effects" off (Reduce Motion),
/// "Transparency effects" off (Reduce Transparency), a contrast theme (the Mac's Increase Contrast, drawn here in the
/// theme's own colours) and light or dark app mode.
/// </summary>
public readonly record struct PlayerLook(bool ReduceMotion = false, bool ReduceTransparency = false, bool HighContrast = false,
    bool Light = false);

/// <summary>
/// How the wallpaper's lyrics sit under the player: faint while it is up, so the player reads; clearer while its seek
/// bar is dragged, so the lyric under the hand can be seen moving with it. The Mac also blurs them (22 pt, 4 pt while
/// dragging); the Windows wallpaper only fades them, as the Mac does under Reduce Motion.
/// </summary>
public static class PlayerHoverFocus
{
    public static (double Blur, double Opacity) Look(bool hovered, bool previewing, bool reduceMotion = false)
    {
        if (previewing) return (reduceMotion ? 0 : 4, 0.8);
        if (!hovered) return (0, 1);
        return reduceMotion ? (0, 0.15) : (22, 0.3);
    }

    /// <summary>The wallpaper lyrics' opacity on Windows, where there is no blur: the Reduce Motion look always.</summary>
    public static double WallpaperOpacity(bool hovered, bool previewing) => Look(hovered, previewing, reduceMotion: true).Opacity;
}

/// <summary>
/// Play/pause that answers at once: the glyph flips on the press and the player's own answer settles it. If the player
/// has not changed within <see cref="Timeout"/>, the glyph goes back to what is really happening.
/// </summary>
public struct OptimisticToggle
{
    public const double Timeout = 1.5;

    public bool? Promised { get; private set; }

    public double Since { get; private set; }

    public readonly bool Displayed(bool actual, double now) =>
        Promised is { } promised && now - Since < Timeout ? promised : actual;

    public void Press(bool actual, double now)
    {
        Promised = !Displayed(actual, now);
        Since = now;
    }

    /// <summary>The player reported <paramref name="actual"/>.</summary>
    public void Reconcile(bool actual, double now)
    {
        if (Promised == actual || now - Since >= Timeout) Promised = null;
    }

    /// <summary>The player refused the command: the glyph goes back to the truth at once.</summary>
    public void Withdraw() => Promised = null;
}

/// <summary>What the row under the song can be for the song playing.</summary>
public enum ScrubRowKind
{
    /// <summary>A song that can be scrubbed.</summary>
    Seekable,

    /// <summary>Shows the song's progress but cannot seek it.</summary>
    ReadOnly,

    /// <summary>Only the elapsed time (no known length).</summary>
    ElapsedOnly,

    Live,
    Advert,

    /// <summary>Nothing at all (nothing playing, or searching).</summary>
    Hidden,
}

public static class ScrubRowKinds
{
    public static ScrubRowKind Of(bool hasTrack, bool isAdvert, bool isStream, double duration, bool canSeek)
    {
        if (!hasTrack) return ScrubRowKind.Hidden;
        if (isAdvert) return ScrubRowKind.Advert;
        if (isStream) return ScrubRowKind.Live;
        if (!(duration > 0)) return ScrubRowKind.ElapsedOnly;
        return canSeek ? ScrubRowKind.Seekable : ScrubRowKind.ReadOnly;
    }
}

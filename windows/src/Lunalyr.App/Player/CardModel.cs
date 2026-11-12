using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Player;

namespace Lunalyr.App.Player;

/// <summary>The card's parts that take clicks, and what Narrator lists (in reading order).</summary>
internal enum CardPart
{
    None = 0,
    Cover,
    Previous,
    PlayPause,
    Next,
    SeekBar,
    TimeLabel,
}

/// <summary>
/// Where everything sits on the card, in DIPs from its top left (the Mac's Sleeve layout: the cover on the left with its
/// record, the song's column on the right with the seek bar and the transport at its foot).
/// </summary>
internal static class CardLayout
{
    public const float Pad = (float)PlayerCard.Padding;
    public const float CoverSize = (float)PlayerCard.Cover;
    public const float ColumnLeft = Pad + (float)PlayerCard.ArtWidth + Pad;
    public const float ColumnWidth = (float)PlayerCard.Column;
    public const float ColumnTop = Pad;
    public const float RecordSize = CoverSize - 6;

    /// <summary>The record's left edge: peeking out by this much while playing, mostly in while paused.</summary>
    public const float RecordOut = 40, RecordIn = 14;

    public const float LabelHeight = 16;
    public const float TitleTop = ColumnTop + LabelHeight + 8;

    public const float ControlsHeight = 44;
    public const float ControlsTop = ColumnTop + CoverSize - ControlsHeight;
    public const float SeekRowHeight = 34, BarBand = 20, TimesHeight = 14;
    public const float SeekRowTop = ControlsTop - 8 - SeekRowHeight;
    public const float BarCentre = SeekRowTop + BarBand / 2;

    /// <summary>The seek bar takes presses this far beyond its band, so a click between a hairline's pixels never misses.</summary>
    public const float BarSlop = 8;

    public const float Cell = 34, Disc = 44, TransportGap = 6;
    public static readonly RectF PreviousRect = new(ColumnLeft, ControlsTop + (ControlsHeight - Cell) / 2, Cell, Cell);
    public static readonly RectF PlayRect = new(ColumnLeft + Cell + TransportGap, ControlsTop, Disc, Disc);
    public static readonly RectF NextRect = new(ColumnLeft + Cell + TransportGap + Disc + TransportGap, ControlsTop + (ControlsHeight - Cell) / 2, Cell, Cell);

    /// <summary>
    /// The row's right-hand end, kept clear for the search, queue and music-video buttons (a later round): nothing is
    /// drawn there yet, and nothing takes clicks.
    /// </summary>
    public static readonly RectF ReservedRect = new(ColumnLeft + ColumnWidth - 3 * 34, ControlsTop + 7, 3 * 34, 30);

    public static readonly RectF CoverRect = new(Pad, Pad, CoverSize, CoverSize);

    public static RectF BarHitRect => new(ColumnLeft - BarSlop, SeekRowTop - BarSlop, ColumnWidth + 2 * BarSlop, BarBand + 2 * BarSlop);

    public static RectF TimesRect => new(ColumnLeft, SeekRowTop + BarBand, ColumnWidth, TimesHeight);
}

/// <summary>A rectangle in DIPs.</summary>
internal readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float CentreX => X + Width / 2;
    public float CentreY => Y + Height / 2;

    public bool Contains(float x, float y) => x >= X && x < Right && y >= Y && y < Bottom;

    public RectF Inflate(float d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
}

/// <summary>
/// Everything the painter draws, as the controller last set it: plain fields, reused frame to frame so drawing
/// allocates nothing. Written and read on the UI thread.
/// </summary>
internal sealed class CardModel
{
    public PlayerLook Look;
    public CardTheme Theme = CardTheme.For(ArtworkPalette.Neutral, default);

    // The song.
    public bool HasSong;
    public string Title = "Nothing playing";
    public string? Artist;
    public string? Album;
    public string PlayerName = "";
    public CoverArt? Cover;
    public bool IsStream;
    public bool DisplayedPlaying;
    public bool CommandsOff;

    // The seek row.
    public ScrubRowKind RowKind = ScrubRowKind.Hidden;
    public double Elapsed;
    public double Duration;
    public bool ShowsRemaining = true;
    public ScrubController.Context Scrub = ScrubController.Context.Empty;
    public ScrubSession? Session;
    public ScrubController.Hover? Hover;
    public double? Flash;
    public RomanizationDisplay Romanization;

    // Animated values, 0–1 unless said otherwise (set by the controller's tweens each frame).
    public float Reveal = 1;
    public float RecordOut = 1;
    public float Thickness = 4;
    public float Active;
    public float Capsules;
    public float TitleAlpha = 1;
    public float NoteAlpha;
    public string? Note;

    // What the pointer is over and pressing (for the faint halo and the press).
    public CardPart HoverPart;
    public CardPart PressedPart;

    /// <summary>The control with the keyboard's focus, and whether to ring it (only after Tab, as Windows does).</summary>
    public CardPart FocusPart = CardPart.PlayPause;
    public bool FocusVisible;

    /// <summary>The capsules' time and x while they show (kept as they fade out).</summary>
    public double CapsuleTime;
    public double CapsuleX;
}

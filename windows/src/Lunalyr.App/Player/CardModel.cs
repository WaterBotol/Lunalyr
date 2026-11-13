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

    // Search and the queue (CardParts has the rows: a picker item, a result and a queue row carry their index).
    SearchButton,
    QueueButton,
    PlayerLabel,
    PaneClose,
    ClearSearch,
    PromptAction,
    SearchField,

    // The music video's Align (the Mac's VideoAlignRow): the film button, and in align mode the video's bar and its row.
    AlignButton,
    AlignBar,
    AlignReset,
    AlignCancel,
    AlignDone,
}

/// <summary>The card's parts that come in numbered rows: the picker's players, the results and the queue.</summary>
internal static class CardParts
{
    private const int PickerBase = 1000, ResultBase = 2000, QueueBase = 3000, Span = 1000;

    public static CardPart Picker(int index) => (CardPart)(PickerBase + index);

    public static CardPart Result(int index) => (CardPart)(ResultBase + index);

    public static CardPart QueueRow(int index) => (CardPart)(QueueBase + index);

    public static bool IsPicker(CardPart part, out int index) => Is(part, PickerBase, out index);

    public static bool IsResult(CardPart part, out int index) => Is(part, ResultBase, out index);

    public static bool IsQueueRow(CardPart part, out int index) => Is(part, QueueBase, out index);

    /// <summary>A part that is a round button (the focus ring is a circle).</summary>
    public static bool IsRound(CardPart part) =>
        part is CardPart.PlayPause or CardPart.Previous or CardPart.Next or CardPart.SearchButton or CardPart.QueueButton or CardPart.PaneClose
            or CardPart.ClearSearch;

    private static bool Is(CardPart part, int start, out int index)
    {
        index = (int)part - start;
        if (index is >= 0 and < Span) return true;
        index = -1;
        return false;
    }
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
    /// The row's right-hand end: search and the queue (the Mac's order; its film button for the music video is
    /// milestone 6 and keeps its place on the right).
    /// </summary>
    public static readonly RectF ReservedRect = new(ColumnLeft + ColumnWidth - 3 * 34, ControlsTop + 7, 3 * 34, 30);

    public const float IconButton = 30, IconGap = 4;
    public static readonly RectF QueueButtonRect = new(ColumnLeft + ColumnWidth - IconButton, ControlsTop + 7, IconButton, IconButton);
    public static readonly RectF SearchButtonRect = new(QueueButtonRect.X - IconGap - IconButton, ControlsTop + 7, IconButton, IconButton);

    /// <summary>The film button (Align video), left of search: the place the row kept for it.</summary>
    public static readonly RectF AlignButtonRect = new(SearchButtonRect.X - IconGap - IconButton, ControlsTop + 7, IconButton, IconButton);

    /// <summary>Align mode's row in place of the transport: Reset on the left, Cancel and Done on the right.</summary>
    public static readonly RectF AlignResetRect = new(ColumnLeft, ControlsTop + 7, 76, 30);
    public static readonly RectF AlignDoneRect = new(ColumnLeft + ColumnWidth - 72, ControlsTop + 6, 72, 32);
    public static readonly RectF AlignCancelRect = new(AlignDoneRect.X - 14 - 64, ControlsTop + 6, 64, 32);

    public static readonly RectF CoverRect = new(Pad, Pad, CoverSize, CoverSize);

    public static RectF BarHitRect => new(ColumnLeft - BarSlop, SeekRowTop - BarSlop, ColumnWidth + 2 * BarSlop, BarBand + 2 * BarSlop);

    public static RectF TimesRect => new(ColumnLeft, SeekRowTop + BarBand, ColumnWidth, TimesHeight);

    // ---- Search or the queue open: the song and a small transport under the cover ----

    public const float CompactTitleTop = Pad + CoverSize + 12;
    public const float SmallCell = 30, SmallDisc = 34, SmallGap = 4;
    public const float SmallTransportTop = CompactTitleTop + 44;
    private const float SmallLeft = Pad + (CoverSize - (SmallCell * 2 + SmallDisc + SmallGap * 2)) / 2;
    public static readonly RectF SmallPreviousRect = new(SmallLeft, SmallTransportTop + (SmallDisc - SmallCell) / 2, SmallCell, SmallCell);
    public static readonly RectF SmallPlayRect = new(SmallLeft + SmallCell + SmallGap, SmallTransportTop, SmallDisc, SmallDisc);
    public static readonly RectF SmallNextRect = new(SmallLeft + SmallCell + SmallGap + SmallDisc + SmallGap, SmallTransportTop + (SmallDisc - SmallCell) / 2, SmallCell, SmallCell);

    public static RectF PreviousFor(PlayerPane pane) => pane == PlayerPane.Song ? PreviousRect : SmallPreviousRect;

    public static RectF PlayFor(PlayerPane pane) => pane == PlayerPane.Song ? PlayRect : SmallPlayRect;

    public static RectF NextFor(PlayerPane pane) => pane == PlayerPane.Song ? NextRect : SmallNextRect;

    // ---- The search column ----

    public const float PickerHeight = 30;
    public static readonly RectF PaneCloseRect = new(ColumnLeft + ColumnWidth - 26, ColumnTop + 3, 24, 24);
    public static readonly RectF FieldRect = new(ColumnLeft, ColumnTop + PickerHeight + 10, ColumnWidth, 36);
    public static readonly RectF ClearRect = new(FieldRect.Right - 32, FieldRect.Y + 6, 24, 24);

    /// <summary>The text inside the field: after the magnifier, before the clear button; one line of 15 DIP type.</summary>
    public const float FieldTextHeight = 21;
    public static readonly RectF FieldTextRect = new(FieldRect.X + 34, FieldRect.Y + (36 - FieldTextHeight) / 2, FieldRect.Width - 34 - 38, FieldTextHeight);

    public const float ResultsTop = ColumnTop + PickerHeight + 10 + 36 + 10;
    public const float ResultRow = 48, RowGap = 2;

    public static float ColumnBottom(PlayerPane pane, int queueRows) => ColumnTop + (float)PlayerCard.ColumnHeight(pane, queueRows);

    // ---- The queue column ----

    public const float QueueHeader = 24;
    public const float QueueTop = ColumnTop + QueueHeader + 6;
    public const float QueueRow = 38, QueueSection = 20;
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

    // Search and the queue (from DesktopPlayerModel; see DesktopPlayer.Search.cs).

    /// <summary>The pane the right-hand column shows; the art column is compact for search and the queue.</summary>
    public PlayerPane Pane;

    /// <summary>The queue's rows, for the card's height.</summary>
    public int QueueRows;

    /// <summary>The players the picker offers, and the one the search talks to.</summary>
    public IReadOnlyList<string> Pickers = [];
    public int PickerSelected;

    /// <summary>Instead of the search box: what to do (set up, sign in, play in Spotify), or null when search works.</summary>
    public PlayerSearchPrompt? Prompt;

    public string Query = "";
    public bool FieldFocused;
    public string SearchPlaceholder = "Search";
    public IReadOnlyList<PlayerSearchResult> Results = [];
    public int Selection;
    public PlayerSearchStatus Status;
    public float ResultsScroll;

    public IReadOnlyList<PlayerQueueItem> Queue = [];
    public string? QueueMessage;
    public bool ShuffleOn;
    public float QueueScroll;

    /// <summary>A row's cover by its address, once loaded (null while it loads, or when there is none).</summary>
    public Func<Uri?, CoverArt?> RowCover = _ => null;

    // The music video (CardPainter.Align.cs, DesktopPlayer.Align.cs).

    /// <summary>The film button shows: Music video is the wallpaper and the song could have a video.</summary>
    public bool MusicVideoOffered;

    /// <summary>Why the video can't be lined up now (the button is dimmed), or null.</summary>
    public string? AlignReason;

    /// <summary>Align mode's bar, or null outside it: the seek row and transport give way to it.</summary>
    public Lunalyr.Core.MusicVideo.MusicVideoAlignDisplay? Align;
}

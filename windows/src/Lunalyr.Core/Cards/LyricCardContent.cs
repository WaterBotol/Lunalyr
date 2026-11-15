// A lyric card's portable description: its shape, its pages and their timing, what sits behind it and what sound goes
// under its MP4. Ported from the Mac's LyricCard.swift (CardAspect, CardFormat, LyricCard.Page/Content/Animation and
// the layout constants) and LunalyrExtras.swift (the card line counts): Lunalyr's own code, the blame check found only
// imports from the Verso commit. How a card is drawn (colours, cover, composition layers) is the renderer's; what the
// card's MP4, GIF and PNG are made of is decided here, so it is tested without drawing.

using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Cards;

/// <summary>The shape of a lyric card. Every card is 1080 points on its short side.</summary>
public enum CardAspect { Portrait, Square, Story, Landscape }

/// <summary>What a lyric card can be saved or copied as, in the Mac's order.</summary>
public enum CardFormat { Mp4, Gif, Png }

/// <summary>A size in card points.</summary>
public readonly record struct CardSize(double Width, double Height);

/// <summary>A rectangle in card points, top-left origin.</summary>
public readonly record struct CardRect(double X, double Y, double Width, double Height)
{
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(CardRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right + 1e-9 && other.Bottom <= Bottom + 1e-9;
}

public static class CardAspects
{
    public static IReadOnlyList<CardAspect> All { get; } = [CardAspect.Portrait, CardAspect.Square, CardAspect.Story, CardAspect.Landscape];

    /// <summary>The Mac's raw value (<c>custom.lyricCardAspect</c>).</summary>
    public static string Raw(this CardAspect aspect) => aspect switch
    {
        CardAspect.Square => "square",
        CardAspect.Story => "story",
        CardAspect.Landscape => "landscape",
        _ => "portrait",
    };

    public static CardAspect? Parse(string? raw) => raw switch
    {
        "portrait" => CardAspect.Portrait,
        "square" => CardAspect.Square,
        "story" => CardAspect.Story,
        "landscape" => CardAspect.Landscape,
        _ => null,
    };

    public static string Title(this CardAspect aspect) => aspect switch
    {
        CardAspect.Square => "1:1",
        CardAspect.Story => "9:16",
        CardAspect.Landscape => "16:9",
        _ => "4:5",
    };

    public static string Detail(this CardAspect aspect) => aspect switch
    {
        CardAspect.Square => "Square",
        CardAspect.Story => "Story",
        CardAspect.Landscape => "Wide",
        _ => "Portrait",
    };

    public static CardSize Size(this CardAspect aspect) => aspect switch
    {
        CardAspect.Square => new(1080, 1080),
        CardAspect.Story => new(1080, 1920),
        CardAspect.Landscape => new(1920, 1080),
        _ => new(1080, 1350),
    };

    /// <summary>Wide cards put the cover and credits in a column beside the words.</summary>
    public static bool HasSidebar(this CardAspect aspect) => aspect == CardAspect.Landscape;
}

public static class CardFormats
{
    /// <summary>Every format, in the order an export makes them (MP4, GIF, PNG).</summary>
    public static IReadOnlyList<CardFormat> All { get; } = [CardFormat.Mp4, CardFormat.Gif, CardFormat.Png];

    /// <summary>The Mac's raw value, which is also the file extension.</summary>
    public static string Raw(this CardFormat format) => format switch
    {
        CardFormat.Gif => "gif",
        CardFormat.Png => "png",
        _ => "mp4",
    };

    public static CardFormat? Parse(string? raw) => raw switch
    {
        "mp4" => CardFormat.Mp4,
        "gif" => CardFormat.Gif,
        "png" => CardFormat.Png,
        _ => null,
    };

    public static string Title(this CardFormat format) => format.Raw().ToUpperInvariant();

    public static string Detail(this CardFormat format) => format switch
    {
        CardFormat.Gif => "Loops everywhere",
        CardFormat.Png => "Crisp still image",
        _ => "Smooth video, full colour",
    };
}

/// <summary>What sits behind a card instead of the album look: a picture, an animated image or a video.</summary>
public enum CardBackgroundKind { Image, AnimatedImage, Video }

/// <summary>A chosen background as the card's timing sees it (the platform reads its frames).</summary>
/// <param name="Path">The file.</param>
/// <param name="Kind">Picture, animated image or video.</param>
/// <param name="Duration">Seconds one pass lasts; 0 for a picture.</param>
/// <param name="HasAudio">A video background that carries sound.</param>
public sealed record CardBackgroundInfo(string Path, CardBackgroundKind Kind, double Duration, bool HasAudio = false)
{
    public bool IsAnimated => Kind != CardBackgroundKind.Image;

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>An extra sound mixed under the MP4, at its own slider level (0–1.5).</summary>
public sealed record CardSoundTrack(string Path, double Volume = 1);

/// <summary>
/// One timed line as a card plays it (the Mac's <c>LyricCard.Animation</c>): the line, the layout drawing it, the words
/// shown (the readings with "Instead"), its end, when its first ink appears and the card's type size for it.
/// </summary>
public sealed record CardAnimation
{
    public required LyricLine Line { get; init; }
    public required CompositionKind Composition { get; init; }
    public int Seed { get; init; }

    /// <summary>The words shown: the line's own, or the readings shown in their place.</summary>
    public required IReadOnlyList<LyricWord> Words { get; init; }

    /// <summary>The card's type size for this line, in card points.</summary>
    public required double CardTypeSize { get; init; }

    /// <summary>A word-timed line in a layout that rolls word by word (Spoken, Cascade, Cutout).</summary>
    public bool Rolls => Line.WordTimed && Composition.ArrivalOnly();

    /// <summary>Where the line's highlight releases: its end, or its last word's if later.</summary>
    public double End => LyricHighlightTiming.LineEnd(Line.End, Words);

    /// <summary>When the first visible word has started to appear, or null when the line has none.</summary>
    public double? FirstInk
    {
        get
        {
            LyricWord? first = null;
            foreach (var word in Words)
            {
                if (!LyricRuns.IsVisible(word.Text)) continue;
                first = word;
                break;
            }
            if (first is null) return null;
            if (!Line.WordTimed) return Line.Start + Math.Min(0.09, (End - Line.Start) / 2);
            var following = End;
            foreach (var word in Words)
            {
                if (word.Start > first.Start && LyricRuns.IsVisible(word.Text))
                {
                    following = word.Start;
                    break;
                }
            }
            return first.Start + Math.Min(0.12, Math.Max(0.02, (following - first.Start) * 0.75));
        }
    }

    /// <summary>
    /// The card's animation of <paramref name="line"/> in <paramref name="composition"/>: the words shown (the readings
    /// with "Instead") and the type size their weight asks for (the Mac's <c>max(126, 224·√(5 / max(5, weight)))</c>).
    /// </summary>
    public static CardAnimation Create(LyricLine line, CompositionKind composition, int seed,
                                       IReadOnlyList<LyricWord>? readings = null,
                                       RomanizationDisplay display = RomanizationDisplay.Off)
    {
        ArgumentNullException.ThrowIfNull(line);
        var source = display == RomanizationDisplay.Replace && readings is { Count: > 0 } ? readings : line.Words;
        var glue = LyricRuns.Glue(line, source);
        var labels = LyricRuns.Labels(source, glue);
        var weight = LyricCardLayout.Weight(labels);
        return new CardAnimation
        {
            Line = line, Composition = composition, Seed = seed, Words = source,
            CardTypeSize = Math.Max(126, 224 * Math.Sqrt(5 / Math.Max(5, weight))),
        };
    }
}

/// <summary>One page of a card: a line's words (each glued run is one entry), timed when it has an animation.</summary>
public sealed record CardPage(IReadOnlyList<string> Words, CardAnimation? Animation = null)
{
    public bool RightToLeft => ScriptDirection.LineIsRightToLeft(Words);
}

/// <summary>
/// What a card shows and how its MP4 is made (the Mac's <c>LyricCard.Content</c>, less the colours and the cover, which
/// the renderer holds).
/// </summary>
public sealed record LyricCardContent
{
    public required IReadOnlyList<CardPage> Pages { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";

    /// <summary>The card shows the song's cover.</summary>
    public bool HasArtwork { get; init; }

    public CardAspect Aspect { get; init; } = CardAspect.Portrait;

    /// <summary>A picture, GIF or video chosen to sit behind the lyrics instead of the album colours.</summary>
    public CardBackgroundInfo? Background { get; init; }

    /// <summary>Carries a video background's own sound into the MP4.</summary>
    public bool KeepBackgroundSound { get; init; } = true;

    /// <summary>A sound to put under the MP4 (any audio file, or a video whose sound is taken), at <see cref="SoundVolume"/>.</summary>
    public string? SoundFile { get; init; }

    /// <summary>More sounds mixed under the MP4, each at its own level.</summary>
    public IReadOnlyList<CardSoundTrack> ExtraSounds { get; init; } = [];

    /// <summary>The song's own sound for these lines, which wins over any other.</summary>
    public SongAudioClip? SongAudio { get; init; }

    /// <summary>How loud the song or chosen file is in the MP4, as a slider position 0–1.5 (<see cref="LyricCardMix.Decibels"/>).</summary>
    public double SoundVolume { get; init; } = 1;

    /// <summary>How big the lyrics are on the card, as a share of the room they have (1 fills it).</summary>
    public double TextScale { get; init; } = 1;

    /// <summary>How loud a video background's own sound is.</summary>
    public double BackgroundVolume { get; init; } = 1;

    /// <summary>Lowers the song by 12 dB while the background video's own sound plays.</summary>
    public bool DuckSong { get; init; }

    /// <summary>What decides the MP4's length; it applies to a video background over two seconds.</summary>
    public ClipLength ClipLength { get; init; } = ClipLength.MatchBackground;

    /// <summary>Keeps the MP4 to 60 seconds, for sharing.</summary>
    public bool CapsLength { get; init; }

    public bool ReduceMotion { get; init; }

    public CardSize Size => Aspect.Size();

    /// <summary>The page drawn for a still image: the first few selected lines, one under another.</summary>
    public CardPage StillPage => LyricCardLayout.StillPage(Pages);

    public bool HasLyrics => Pages.Any(page => page.Words.Count > 0 || page.Animation is not null);

    public bool HasTrackDetails => HasArtwork || Title.Length > 0 || Artist.Length > 0;

    /// <summary>Where the lyrics are set, in card points.</summary>
    public CardRect LyricStage => LyricCardLayout.LyricStage(Aspect, HasTrackDetails);

    /// <summary>The background video's own sound, when it has some and it's wanted.</summary>
    public string? BackgroundSoundSource =>
        KeepBackgroundSound && Background is { Kind: CardBackgroundKind.Video, HasAudio: true } background ? background.Path : null;

    /// <summary>Where the MP4's sound comes from, if anywhere.</summary>
    public string? SoundSource => SoundFile ?? BackgroundSoundSource;
}

/// <summary>The card's layout constants and the rules that pick and weigh its lines.</summary>
public static class LyricCardLayout
{
    public const double Inset = 52;
    public const double TrackFooterHeight = 224;
    public const double SidebarWidth = 440;
    public const double SignatureHeight = 44;

    /// <summary>The lightest the card's scrim gets (its top-left corner), which the lyric ink is judged against.</summary>
    public const double LightestScrim = 0.48;

    public const double RollingHeightInTypeSizes = 2.35;

    /// <summary>A still image holds at most this many lines: more would shrink the words until they are hard to read.</summary>
    public const int StillLineLimit = 3;

    /// <summary>A word that is really a line break (see <see cref="StillPage"/>).</summary>
    public const string LineBreak = "\n";

    /// <summary>The lines before and after the one sung that a quick card takes (Mac <c>lyricCardLineRange</c>).</summary>
    public const int MinimumLineCount = 0, MaximumLineCount = 5;

    public static int ClampedLineCount(int count) => Math.Min(MaximumLineCount, Math.Max(MinimumLineCount, count));

    /// <summary>Where the lyrics are set on a card of <paramref name="aspect"/>, in card points.</summary>
    public static CardRect LyricStage(CardAspect aspect, bool hasTrackDetails)
    {
        var size = aspect.Size();
        if (aspect.HasSidebar() && hasTrackDetails)
        {
            var left = Inset + SidebarWidth + 56;
            return new CardRect(left, Inset, size.Width - left - Inset, size.Height - 2 * Inset - SignatureHeight - 20);
        }
        var top = aspect == CardAspect.Landscape ? Inset : 84;
        var footer = hasTrackDetails ? TrackFooterHeight : SignatureHeight;
        return new CardRect(Inset, top, size.Width - 2 * Inset, size.Height - top - Inset - footer - 28);
    }

    /// <summary>The first few selected lines, one under another (at most <see cref="StillLineLimit"/>), for a still image.</summary>
    public static CardPage StillPage(IReadOnlyList<CardPage> pages)
    {
        var words = new List<string>();
        foreach (var page in pages.Take(StillLineLimit))
        {
            if (page.Words.Count == 0) continue;
            if (words.Count > 0) words.Add(LineBreak);
            words.AddRange(page.Words);
        }
        return new CardPage(words);
    }

    /// <summary>How much room a line takes, counted in words: a run written without spaces counts by its length, about
    /// three characters to a word.</summary>
    public static double Weight(IEnumerable<string> words)
    {
        double total = 0;
        foreach (var word in words)
        {
            var characters = 0;
            foreach (var rune in word.EnumerateRunes())
                if (Lyrics.Readings.JapaneseReading.IsJapanese(rune)) characters++;
            total += Math.Max(1, characters / 3.0);
        }
        return total;
    }

    /// <summary>
    /// Counts vocal lines on each side of the lyric on screen (<paramref name="active"/>): <paramref name="before"/>
    /// and <paramref name="after"/> of them (0–5). During a break the previous lyric remains the reference point.
    /// </summary>
    public static IReadOnlyList<int> SelectedRows(IReadOnlyList<int> vocalRows, int? active, int before, int after)
    {
        if (vocalRows.Count == 0) return [];
        var pivot = 0;
        if (active is { } index)
        {
            for (var i = vocalRows.Count - 1; i >= 0; i--)
            {
                if (vocalRows[i] > index) continue;
                pivot = i;
                break;
            }
        }
        var first = Math.Max(0, pivot - ClampedLineCount(before));
        var last = Math.Min(vocalRows.Count - 1, pivot + ClampedLineCount(after));
        return [.. vocalRows.Skip(first).Take(last - first + 1)];
    }

    /// <summary>The composition with id <paramref name="id"/> ("editorial", "cutout"…), or null.</summary>
    public static CompositionKind? Composition(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var kind in Enum.GetValues<CompositionKind>())
            if (kind.Id() == id) return kind;
        return null;
    }
}

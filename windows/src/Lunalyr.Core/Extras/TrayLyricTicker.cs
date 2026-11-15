// The tray lyric's timing and text rules, ported from the Mac's MenuBarLyricTicker.swift (Sources/Extras, Lunalyr's own
// lines; its one Verso line, the workspace notification centre behind the screens-asleep observer, is not translated:
// Windows reads the displays' power through its own monitor). The status item, its menu and the roll are the Mac's
// AppKit; on Windows the line shows in a strip beside the taskbar (docs/APP-SHELL.md, "Tray lyric").

using System.Globalization;
using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.Extras;

/// <summary>
/// The line being sung, beside the tray. Its width is set once per song, from the widest line it will show, so it stays
/// put while lines change; a line too wide for it is shown in two halves, one after the other. While a song with timed
/// lyrics plays it wakes once per change (the next line, the second half of a long one, or the moment a finished line
/// gives way to ♪) and at least every two seconds to catch a scrub in the player. Paused, every two seconds. Otherwise
/// nothing can change by itself, so it waits for the song, playback, the lyrics or the settings to change.
/// </summary>
public static class TrayLyricTicker
{
    /// <summary>Shown instead of the line while the wallpaper is paused, beside a pause symbol (never colour emoji).</summary>
    public const string PausedText = "Wallpaper paused";

    public const string HiddenText = "Lyrics hidden";

    /// <summary>The longest wait while a song plays: a scrub in the player is caught within this.</summary>
    public const double MaxWait = 2;

    /// <summary>The shortest wait, so a line start just missed is not spun on.</summary>
    public const double MinWait = 0.05;

    /// <summary>While the song is paused.</summary>
    public const double PausedTickInterval = 2;

    /// <summary>How late a line start may show: barely noticeable, yet lets the system fold the wake-up in with others.</summary>
    public const double LineTolerance = 0.03;

    /// <summary>After a line, a note reads better than a stale line.</summary>
    public const double FinishedAfter = 1.5;

    /// <summary>Lines that change faster than this swap without the roll, so the roll never stutters.</summary>
    public const double MinTransitionGap = 0.3;

    /// <summary>With nothing playing the line waits this long before it leaves, so a skip never pops it away and back.</summary>
    public const double HideDelay = 2;

    /// <summary>Padding beside the widest line, in points.</summary>
    public const double WidthPadding = 8;

    /// <summary>The "Longest line" setting's range and default, in characters (the Mac's <c>menuBarLengthRange</c>).</summary>
    public const double LengthMin = 16, LengthMax = 64, DefaultLength = 36;

    /// <summary>The roll's length (the Mac's <c>LunalyrDesign.Motion.CA.line</c>) and Reduce Motion's cross-fade (<c>.reduced</c>).</summary>
    public const double RollSeconds = 0.32, ReducedSeconds = 0.18;

    // ---- What shows ----

    /// <summary>What the line shows, before the line itself is read.</summary>
    public abstract record Display
    {
        /// <summary>Nothing: nothing is playing.</summary>
        public sealed record None : Display;

        public sealed record Note(string Text) : Display;

        /// <summary>The line being sung.</summary>
        public sealed record Line : Display;
    }

    /// <summary>A paused wallpaper says so even with nothing playing; hidden lyrics never show here either.</summary>
    public static Display DisplayFor(bool paused, bool hasTrack, bool lyricsVisible)
    {
        if (paused) return new Display.Note(PausedText);
        if (!hasTrack) return new Display.None();
        return lyricsVisible ? new Display.Line() : new Display.Note(HiddenText);
    }

    /// <summary>Which line (null: ♪) and which half of it shows, and when that next changes (playback time).</summary>
    public readonly record struct LineState(int? Line, int Page, double? NextEvent);

    public static LineState LineStateAt(IReadOnlyList<LyricLine> lines, int? index, IReadOnlyList<double?> pageSwitches, double time)
    {
        if (index is not { } i || i < 0 || i >= lines.Count)
            return new LineState(null, 0, lines.FirstOrDefault(l => l.Start > time)?.Start);
        var line = lines[i];
        var hasNext = i + 1 < lines.Count;
        var events = new List<double>(3);
        if (hasNext) events.Add(lines[i + 1].Start);
        var finishedAt = line.End + FinishedAfter;
        var finished = hasNext && time > finishedAt;
        if (hasNext && !finished) events.Add(finishedAt);
        var page = 0;
        if (i < pageSwitches.Count && pageSwitches[i] is { } flip)
        {
            if (time >= flip) page = 1;
            else events.Add(flip);
        }
        double? next = null;
        foreach (var e in events) if (e > time && (next is null || e < next)) next = e;
        if (line.IsInstrumental || finished) return new LineState(null, 0, next);
        return new LineState(i, page, next);
    }

    // ---- When it wakes ----

    /// <summary>
    /// The longest wait between checks, or null when time alone cannot change the line: a song with timed lyrics whose
    /// lyrics are on screen, a moving wallpaper and a display awake to show it. Paused, only a scrub moves it.
    /// </summary>
    public static double? TickInterval(bool hasTrack, bool playing, bool hasTimedLines, bool lyricsVisible, bool paused, bool screensAsleep)
    {
        if (!hasTrack || !hasTimedLines || !lyricsVisible || paused || screensAsleep) return null;
        return playing ? MaxWait : PausedTickInterval;
    }

    /// <summary>How long to sleep while playing: until the next change, kept between <see cref="MinWait"/> and <paramref name="ceiling"/>.</summary>
    public static double Wait(double? untilEventIn, double ceiling)
    {
        if (untilEventIn is not { } delay || !double.IsFinite(delay)) return ceiling;
        return Math.Min(ceiling, Math.Max(MinWait, delay + 0.01));
    }

    /// <summary>Line starts are kept close; a plain re-read may wait a little.</summary>
    public static double Tolerance(double wait) => wait < MaxWait ? LineTolerance : wait * 0.1;

    // ---- Lines, pages and width ----

    /// <summary>Each line's text as shown (its reading when readings replace the lyrics), with the words that time it.</summary>
    public sealed record LineText(string Text, IReadOnlyList<LyricWord> Words);

    /// <summary>A song's lines measured once: each line's one or two pages, when the second takes over, and the width.</summary>
    public sealed record Layout(IReadOnlyList<IReadOnlyList<string>> Pages, IReadOnlyList<double?> PageSwitches, double Width)
    {
        public bool Equals(Layout? other) => other is not null && Width == other.Width
            && PageSwitches.SequenceEqual(other.PageSwitches)
            && Pages.Count == other.Pages.Count && Pages.Zip(other.Pages).All(p => p.First.SequenceEqual(p.Second));

        public override int GetHashCode() => HashCode.Combine(Width, Pages.Count);
    }

    public static Layout LayoutFor(IReadOnlyList<LyricLine> lines, IReadOnlyList<LineText> readings, double maxWidth, Func<string, double> measure)
    {
        var pages = new List<IReadOnlyList<string>>(lines.Count);
        var switches = new List<double?>(lines.Count);
        var widest = measure("♪");
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var reading = index < readings.Count ? readings[index] : new LineText(line.Text, line.Words);
            var split = Pages(reading.Text, maxWidth, measure);
            pages.Add(split);
            if (!line.IsInstrumental) widest = Math.Max(widest, split.Count == 0 ? 0 : split.Max(measure));
            switches.Add(split.Count > 1 ? PageSwitch(line, reading.Words, reading.Text, split) : null);
        }
        return new Layout(pages, switches, Math.Ceiling(Math.Min(maxWidth, widest) + WidthPadding));
    }

    /// <summary>The "Longest line" setting counts characters; an average character of the font turns it into points.</summary>
    public static double MaxWidth(int characters, Func<string, double> measure)
    {
        const string sample = "The quick brown fox jumps over the lazy dog";
        return Math.Round(measure(sample) / sample.Length * Math.Max(4, characters), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// A line as one page when it fits, else two halves split at the space nearest its middle (or the middle itself, for
    /// writing without spaces), each shortened to fit if it still does not.
    /// </summary>
    public static IReadOnlyList<string> Pages(string line, double maxWidth, Func<string, double> measure)
    {
        var trimmed = (line ?? "").Trim();
        if (trimmed.Length == 0) return ["♪"];
        var characters = Elements(trimmed);
        if (measure(trimmed) <= maxWidth || characters.Count <= 1) return [trimmed];
        var middle = characters.Count / 2;
        int? cut = null;
        for (var i = 0; i < characters.Count; i++)
            if (IsWhitespace(characters[i]) && (cut is null || Math.Abs(i - middle) < Math.Abs(cut.Value - middle))) cut = i;
        var at = cut ?? middle;
        var first = string.Concat(characters.Take(at)).Trim();
        var second = string.Concat(characters.Skip(at)).Trim();
        if (first.Length == 0 || second.Length == 0) return [Fitted(trimmed, maxWidth, measure)];
        return [Fitted(first, maxWidth, measure), Fitted(second, maxWidth, measure)];
    }

    /// <summary>
    /// When the second half of a paged line takes over: when its first word is sung, if the words are timed one by one,
    /// else a share of the line in proportion to its characters.
    /// </summary>
    public static double PageSwitch(LyricLine line, IReadOnlyList<LyricWord> words, string text, IReadOnlyList<string> pages)
    {
        var tokens = Tokens(text);
        var firstCount = Tokens(pages[0]);
        if (words.Count == tokens && firstCount < words.Count && !pages[0].EndsWith('…')) return words[firstCount].Start;
        var total = pages.Sum(p => Elements(p).Count);
        var share = total > 0 ? (double)Elements(pages[0]).Count / total : 0.5;
        // A line timed only at its start ends where the next begins, which may be long after the singing; assume a
        // brisk 11 characters a second.
        var sungEnd = words.Count > 0 ? words[^1].End
            : line.EndFromNextLine == true ? Math.Min(line.End, line.Start + total / 11.0) : line.End;
        return line.Start + Math.Max(0, sungEnd - line.Start) * share;
    }

    /// <summary>
    /// A text shortened to <paramref name="maxWidth"/> with "…", at a word boundary where one is close, by whole
    /// characters (never splitting a cluster or an emoji).
    /// </summary>
    public static string Fitted(string text, double maxWidth, Func<string, double> measure)
    {
        if (measure(text) <= maxWidth) return text;
        var characters = Elements(text);
        if (characters.Count <= 1) return text;
        int low = 1, high = characters.Count - 1, best = 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (measure(string.Concat(characters.Take(middle)) + "…") <= maxWidth)
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        var cut = characters.Take(best).ToList();
        // Cut exactly at a space: the last word is whole, keep it.
        if (IsWhitespace(characters[best])) return string.Concat(cut).Trim() + "…";
        var space = cut.FindLastIndex(IsWhitespace);
        if (space >= 0 && space > best / 2) return string.Concat(cut.Take(space)).Trim() + "…";
        return string.Concat(cut).Trim() + "…";
    }

    // ---- Where the strip sits (Windows) ----

    /// <summary>Which edge of its display the taskbar is on.</summary>
    public enum TaskbarEdge
    {
        Left,
        Top,
        Right,
        Bottom,
    }

    /// <summary>The gap between the strip and the taskbar or the display's edge, in points.</summary>
    public const double PlaceMargin = 8;

    /// <summary>
    /// Where the strip goes, in the same units as the area: in the corner of the usable area (the work area, less an
    /// auto-hidden taskbar's reserve) nearest the clock, <paramref name="margin"/> clear of the taskbar and the edge.
    /// The clock is at the right end of a bottom or top taskbar and the bottom of a side one.
    /// </summary>
    public static (double X, double Y) Place(double areaX, double areaY, double areaWidth, double areaHeight, TaskbarEdge edge,
        double width, double height, double margin)
    {
        var right = edge != TaskbarEdge.Left;
        var bottom = edge != TaskbarEdge.Top;
        var x = right ? areaX + areaWidth - margin - width : areaX + margin;
        var y = bottom ? areaY + areaHeight - margin - height : areaY + margin;
        return (Math.Round(x, MidpointRounding.AwayFromZero), Math.Round(y, MidpointRounding.AwayFromZero));
    }

    /// <summary>The roll's curve: a fast start that settles (the Mac's <c>easeOut</c>, cubic Bézier 0.22, 1, 0.36, 1).</summary>
    public static double EaseOut(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        const double x1 = 0.22, y1 = 1, x2 = 0.36, y2 = 1;
        // Solve x(u) = t by bisection (monotonic in u), then read y(u).
        double low = 0, high = 1, u = t;
        for (var i = 0; i < 40; i++)
        {
            u = (low + high) / 2;
            var x = Bezier(u, x1, x2);
            if (x < t) low = u;
            else high = u;
        }
        return Bezier(u, y1, y2);

        static double Bezier(double u, double p1, double p2)
        {
            var v = 1 - u;
            return 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u;
        }
    }

    /// <summary>The user-perceived characters of a text (Swift's <c>Array(string)</c>).</summary>
    internal static List<string> Elements(string text)
    {
        var elements = new List<string>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        return elements;
    }

    private static bool IsWhitespace(string element) => element.Length > 0 && element.All(char.IsWhiteSpace);

    private static int Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

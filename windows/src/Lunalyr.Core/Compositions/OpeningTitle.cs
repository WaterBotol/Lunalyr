// Ported from the Mac app's OpeningTitle.swift (OpeningTitle, OpeningTitleMemory): Lunalyr's own code. Git blame puts
// one line of that file in the squashed Verso import, the card view's `Settings.shared` property, which has no
// counterpart here. The card itself is drawn by Lunalyr.Render (LyricCompositionLayer.OpeningTitle.cs).

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>
/// The song's opening title. When a song starts, its title, artist and album are set on the wallpaper like a music
/// video's title card: low on the left, clear of the lyric, in the lyric's own typeface. It arrives with the song, holds
/// through the intro and has gone a second before the first line is sung. Everything is a pure function of the song's
/// time, so a seek or a pause shows the same frame for the same moment.
/// </summary>
public static class OpeningTitle
{
    // Type sizes, as shares of the title's own size (before a long title is set smaller), so the hierarchy holds at
    // every size.
    public const double ArtistShare = 0.32;

    /// <summary>
    /// How strongly the card glows in the album's colour: fully over a dark picture, gently when the type is dark on a
    /// light one (<paramref name="dark"/>), where a glow would only muddy the words.
    /// </summary>
    public static double GlowStrength(bool dark) => dark ? 0.35 : 1;
    public const double ReadingShare = 0.32;
    public const double AlbumShare = 0.26;

    // Timing (seconds of song time).

    /// <summary>The beat after the song starts before the card does.</summary>
    public const double Lead = 0.5;
    /// <summary>A song first seen later than this was joined part-way.</summary>
    public const double MidSong = 2.5;
    /// <summary>Joined part-way: the card starts this long after.</summary>
    public const double JoinLead = 0.3;
    /// <summary>Entrances: the full card's reveal is staged over this long.</summary>
    public const double FullEntrance = 1.6;
    public const double CompactEntrance = 0.8;
    /// <summary>Exits are quicker than entrances.</summary>
    public const double FullExit = 0.7;
    public const double CompactExit = 0.45;
    /// <summary>The shortest hold worth arriving for.</summary>
    public const double FullMinimumHold = 1.2;
    public const double CompactMinimumHold = 0.7;
    /// <summary>The longest the card holds through a long intro, and with no words.</summary>
    public const double LyricHold = 8.0;
    public const double WordlessHold = 11.0;
    /// <summary>A song joined part-way, or with words already showing.</summary>
    public const double BriefHold = 2.6;
    /// <summary>Gone at least this long before the first line starts to appear.</summary>
    public const double Clearance = 1.0;

    /// <summary>
    /// Whether the cover is drawn beside the title: the setting is on and the song has artwork with a size. Without
    /// it the words stay where they are.
    /// </summary>
    public static bool ShowsCover(bool enabled, int width, int height) => enabled && width > 0 && height > 0;

    /// <summary>When the first line may first be drawn: its own start, less the next-word preview lead and the line's attack.</summary>
    public static double Deadline(double firstLine, double previewLead) => firstLine - Math.Max(Clearance, previewLead + 0.6);

    /// <summary>The card's timing for a song first seen at <paramref name="joined"/> seconds in, or null when there is no room for it.</summary>
    public static OpeningSchedule? Schedule(OpeningWords words, double joined = 0, double previewLead = 0.45)
    {
        var partWay = joined > MidSong;
        var start = partWay ? joined + JoinLead : Lead;
        OpeningSchedule Card(OpeningSize size, double leave)
        {
            var entrance = size == OpeningSize.Full ? FullEntrance : CompactEntrance;
            var exit = size == OpeningSize.Full ? FullExit : CompactExit;
            return new OpeningSchedule(start, start + entrance, leave, leave + exit, size);
        }
        var brief = Card(OpeningSize.Compact, start + CompactEntrance + BriefHold);
        switch (words.Kind)
        {
            case OpeningWordsKind.Showing:
                return brief;
            case OpeningWordsKind.Absent:
                // No lyrics for this song: the card stays for the rest of it (the owner, 15 Nov 2026), compact when joined
                // part-way. It still gives way if lyrics arrive late (Yielding).
                return Card(partWay ? OpeningSize.Compact : OpeningSize.Full, double.PositiveInfinity);
            case OpeningWordsKind.None:
            case OpeningWordsKind.Timed when words.FirstLine is null:
                return partWay ? brief : Card(OpeningSize.Full, start + FullEntrance + WordlessHold);
            default:
                var first = words.FirstLine!.Value;
                var gone = Deadline(first, previewLead);
                var room = gone - start;
                if (room >= FullEntrance + FullMinimumHold + FullExit)
                    return Card(OpeningSize.Full, Math.Min(gone - FullExit, start + FullEntrance + LyricHold));
                if (room >= CompactEntrance + CompactMinimumHold + CompactExit)
                    return Card(OpeningSize.Compact, gone - CompactExit);
                // The words are already under way: joined part-way, a moment's reminder; at the very start, nothing.
                return partWay && first < start ? brief : null;
        }
    }

    /// <summary>A card that stays held this long (seconds after it settled) eases to its resting state.</summary>
    public const double RestAfter = 30;

    /// <summary>How long the ease to the resting state takes.</summary>
    public const double RestFade = 2;

    /// <summary>The resting state's opacity: still there, quieter.</summary>
    public const double RestOpacity = 0.55;

    /// <summary>Whether <paramref name="schedule"/> stays for the rest of the song (no lyrics).</summary>
    public static bool Stays(OpeningSchedule? schedule) => schedule is { } s && double.IsPositiveInfinity(s.Leave);

    /// <summary>
    /// A card that was staying (<paramref name="staying"/>), now that lyrics arrived at <paramref name="at"/>: it leaves
    /// from then, with its usual exit, rather than vanishing.
    /// </summary>
    public static OpeningSchedule Yielding(OpeningSchedule staying, double at)
    {
        var leave = Math.Max(at, staying.Settled);
        var exit = staying.Size == OpeningSize.Full ? FullExit : CompactExit;
        return staying with { Leave = leave, End = leave + exit };
    }

    /// <summary>A staying card entering again from <paramref name="at"/>: the holding card had already left when the lookup ended.</summary>
    public static OpeningSchedule Reentering(OpeningSchedule staying, double at)
    {
        var entrance = staying.Size == OpeningSize.Full ? FullEntrance : CompactEntrance;
        return staying with { Start = at, Settled = at + entrance };
    }

    /// <summary>The opacity a staying card is drawn at: 1, then easing to <see cref="RestOpacity"/> once held a while.</summary>
    public static double Resting(OpeningSchedule? schedule, double time)
    {
        if (!Stays(schedule) || !double.IsFinite(time)) return 1;
        var t = (time - schedule!.Value.Settled - RestAfter) / RestFade;
        return 1 - (1 - RestOpacity) * Easing.Smoothstep(Math.Clamp(t, 0, 1));
    }

    /// <summary>The start of the first line that is sung (credits and breaks are not).</summary>
    public static double? FirstSung(IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
            if (!line.IsInstrumental && line.Credit != true) return line.Start;
        return null;
    }

    /// <summary>Where the card is at <paramref name="time"/>: null before it starts and once it has gone.</summary>
    public static OpeningPhase? Phase(OpeningSchedule? schedule, double time)
    {
        if (schedule is not { } s || !(time >= s.Start) || !(time < s.End)) return null;
        var exit = time <= s.Leave ? 0 : (time - s.Leave) / (s.End - s.Leave);
        return new OpeningPhase(time - s.Start, Math.Min(1, Math.Max(0, exit)), s.Size, time < s.Settled || time > s.Leave);
    }

    /// <summary>0…1: how much of something else naming the song (the loading notice) may show: none while the card is up, fading back after.</summary>
    public static double Yields(OpeningSchedule? schedule, double time) =>
        schedule is not { } s ? 1 : Easing.Smoothstep((time - s.End) / 0.5);

    /// <summary>The card's phase for the stage's clock: as <see cref="Phase"/>, but a card due within the second counts as moving, so a resting clock is awake for it.</summary>
    public static OpeningPhase? Upcoming(OpeningSchedule? schedule, double time)
    {
        if (schedule is not { } s || !(time < s.Start)) return Phase(schedule, time);
        if (!(time >= s.Start - 1)) return null;
        return new OpeningPhase(0, 0, s.Size, true);
    }

    /// <summary>The stage's clock while the card is up: every frame while it moves, and never stopped while it is held, so its exit is drawn.</summary>
    public static LyricPace.Level Pace(LyricPace.Level level, OpeningPhase? phase, bool playing)
    {
        if (phase is not { } p) return level;
        if (p.Moving) return playing ? LyricPace.Level.Active : (level == LyricPace.Level.Resting ? LyricPace.Level.Waiting : level);
        return level == LyricPace.Level.Resting ? (playing ? LyricPace.Level.Still : LyricPace.Level.Waiting) : level;
    }

    /// <summary>1 for a title of up to 24 characters, down to 0.7 for a long one.</summary>
    public static double LengthScale(string title) =>
        Math.Min(1, Math.Max(0.7, 24.0 / Math.Max(1, TextElements.Count(title ?? ""))));

    /// <summary>The title's line on its own: the album, unless it only repeats the title ("Song - Single").</summary>
    public static string? AlbumLine(string? album, string? title)
    {
        var trimmed = (album ?? "").Trim();
        var name = trimmed;
        foreach (var suffix in Suffixes)
            if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
        if (name.Length == 0 || LyricQuery.Normalize(name) == LyricQuery.Normalize(title ?? "")) return null;
        return trimmed;
    }

    private static readonly string[] Suffixes = [" - Single", " - EP", " – Single", " – EP"];
}

/// <summary>How much of the card is drawn.</summary>
public enum OpeningSize
{
    /// <summary>Title, its reading, artist and album.</summary>
    Full,
    /// <summary>A smaller title and the artist: a short intro, or a song joined part-way.</summary>
    Compact,
}

public enum OpeningWordsKind { Timed, None, Showing, Absent }

/// <summary>What the stage knows about the song's words.</summary>
public readonly record struct OpeningWords(OpeningWordsKind Kind, double? FirstLine = null)
{
    /// <summary>Timed lyrics; the start of the first sung line, null if none is sung.</summary>
    public static OpeningWords Timed(double? firstLine) => new(OpeningWordsKind.Timed, firstLine);

    /// <summary>Nothing to sing along to (yet): looking lyrics up, none found, an instrumental. The card holds longer.</summary>
    public static OpeningWords None => new(OpeningWordsKind.None);

    /// <summary>Words already on screen from the start (unsynced lyrics): only a brief, compact card.</summary>
    public static OpeningWords Showing => new(OpeningWordsKind.Showing);

    /// <summary>The lookup ended with no lyrics: the card stays for the rest of the song.</summary>
    public static OpeningWords Absent => new(OpeningWordsKind.Absent);
}

/// <summary>When the card enters, settles, starts leaving and is gone, in song time.</summary>
public readonly record struct OpeningSchedule(double Start, double Settled, double Leave, double End, OpeningSize Size);

/// <summary>Where the card is.</summary>
/// <param name="Age">Seconds since the card started entering.</param>
/// <param name="Exit">0 while held, rising to 1 as it leaves.</param>
/// <param name="Moving">Entering or leaving. Held, nothing on it changes.</param>
public readonly record struct OpeningPhase(double Age, double Exit, OpeningSize Size, bool Moving);

/// <summary>How far each part of the card has arrived, 0…1.</summary>
/// <param name="Cover">The cover: arrives a beat before the title's wipe.</param>
/// <param name="Wipe">How far the soft wipe has uncovered the title, left to right.</param>
public readonly record struct OpeningReveal(double Rule, double Cover, double Title, double Wipe, double Reading, double Artist,
                                            double Album)
{
    /// <summary>The card's parts <paramref name="age"/> seconds in. Reduce Motion fades the whole card in at once.</summary>
    public static OpeningReveal At(double age, OpeningSize size, bool reduceMotion)
    {
        if (reduceMotion)
        {
            var fade = Easing.Smoothstep(age / 0.6);
            return new OpeningReveal(fade, fade, fade, 1, fade, fade, fade);
        }
        // Staged: the title is wiped on, then the reading, the artist and the album follow. Rule is the card's first
        // beat (the corner shade comes in with it); no bar is drawn.
        var scale = size == OpeningSize.Full ? 1.0 : OpeningTitle.CompactEntrance / OpeningTitle.FullEntrance;
        double Part(double from, double span) => Easing.Settle(age - from * scale, span * scale);
        // The wipe travels at an even, eased pace; everything else settles.
        var wipe = Easing.Smoothstep((age - 0.1 * scale) / (1.2 * scale));
        return new OpeningReveal(Part(0, 0.9), Part(0, 0.55), Part(0.12, 1.15), wipe, Part(0.5, 0.9), Part(0.45, 1.1), Part(0.75, 0.85));
    }
}

/// <summary>
/// Per song: when Lunalyr first saw it (so a song joined part-way gets only a brief card), and the title's reading,
/// worked out once. Not thread-safe: each owner keeps its own (the renderer the join, the app the reading).
/// </summary>
public sealed class OpeningTitleMemory
{
    private string? _track;
    private double _joinedAt;
    private string? _readingKey;
    private string? _reading;

    /// <summary>
    /// The song time at which <paramref name="track"/> was first seen: 0 for a song seen from its start, or once
    /// playback is back at its start. A time that is not a number (no position yet) is not a sighting.
    /// </summary>
    public double Joined(string? track, double time)
    {
        if (!double.IsFinite(time)) return track == _track ? _joinedAt : 0;
        if (track != _track)
        {
            _track = track;
            _joinedAt = time;
        }
        if (time < OpeningTitle.MidSong) _joinedAt = 0;
        return _joinedAt;
    }

    /// <summary>The title's romanisation when readings are shown and it needs one.</summary>
    public string? Reading(string title, RomanizationOptions options, LyricRomanizer romanizer)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(romanizer);
        if (options.Display == RomanizationDisplay.Off || !LyricRomanizer.ContainsNonLatin(title)) return null;
        var key = $"{title}\u0001{options.Language}\u0001{options.ToneMarks}";
        if (key != _readingKey)
        {
            _readingKey = key;
            var text = romanizer.Romanize(title, options.Language, options.ToneMarks);
            _reading = text == title || string.IsNullOrEmpty(text) ? null : Capitalised(text);
        }
        return _reading;
    }

    /// <summary>"yoru ni kakeru" as a title: "Yoru ni Kakeru". Short particles stay lower case.</summary>
    public static string Capitalised(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var lower = words[i].ToLowerInvariant();
            if (i > 0 && SmallWords.Contains(lower)) { words[i] = lower; continue; }
            words[i] = lower.Length == 0 ? lower : char.ToUpperInvariant(lower[0]) + lower[1..];
        }
        return string.Join(' ', words);
    }

    private static readonly HashSet<string> SmallWords =
        new(["ni", "no", "wo", "o", "wa", "ga", "to", "de", "e", "he", "mo", "ka", "ya", "yo", "ne"], StringComparer.Ordinal);
}

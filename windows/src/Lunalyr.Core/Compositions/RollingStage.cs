// The rolling stage (Spoken). Ported from the Mac app's SpokenTypographyView.swift (cursor, stacks, RollingTokenState,
// RollingStack) and LyricLayerView.swift (LyricLayerCache.rollingWindow, wordOffsets): Lunalyr's own code. Preparing
// a word (SpokenTypography.prepare) is partly Verso-derived and is re-implemented from docs/compositions/spoken.md.

using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>One word as a rolling stage sets it (a glued run counts as one word).</summary>
public sealed record RollingWord
{
    public required int Index { get; init; }
    public required LyricWord Source { get; init; }
    /// <summary>FNV-1a of the text: picks the entrance's direction.</summary>
    public required ulong Hash { get; init; }
    /// <summary>Per timed word, so repeats of one word still move differently.</summary>
    public required ulong Seed { get; init; }
    /// <summary>Time to the next onset: how briskly the word moves and hands over.</summary>
    public required double Cadence { get; init; }
    public required bool RightToLeft { get; init; }
    public required bool Hero { get; init; }
    /// <summary>The word's type size, in the caller's units.</summary>
    public required double PointSize { get; init; }
    public string? Reading { get; init; }
    /// <summary>Where along the ink's colours the word sits, 0…1, keyed by its place in the song.</summary>
    public double Slot { get; init; }
    /// <summary>The syllables or characters a glued run is made of; empty for a single token.</summary>
    public IReadOnlyList<LyricWord> Parts { get; init; } = [];
    /// <summary>Where each part falls along the word, 0…1 by character (parts + 1 values); empty for a single token.</summary>
    public IReadOnlyList<double> Boundaries { get; init; } = [];
    /// <summary>The largest entrance turn this word may make, degrees.</summary>
    public double TurnLimit { get; init; } = 12;
    /// <summary>Set in the heavy cut: every word in Spoken, only heroes in Cascade and Cutout.</summary>
    public bool Heavy { get; init; } = true;
    /// <summary>Set on an album-colour label (Cutout's heroes and about a quarter of its other words).</summary>
    public bool Tile { get; init; }

    /// <summary>A colour slot from the word's place in the whole song: consecutive words take well-separated places.</summary>
    public static double SlotOf(int ordinal) => (ordinal * 0.618_034) % 1.0;

    /// <summary>The direction an entrance travels from: +1 or −1.</summary>
    public double Direction => Hash % 2 == 0 ? 1 : -1;
}

/// <summary>Preparing a rolling stage's words (Spoken).</summary>
public static class RollingWords
{
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "i", "in", "of", "to", "and", "or", "it", "is", "my", "me", "you",
    };

    /// <summary>A short function word, lightly set (its letters, lower-cased).</summary>
    public static bool IsFiller(string text)
    {
        Span<char> buffer = stackalloc char[16];
        var length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            var lower = Rune.ToLowerInvariant(rune);
            if (lower.Utf16SequenceLength + length > buffer.Length) return false;
            length += lower.EncodeToUtf16(buffer[length..]);
        }
        return length > 0 && Fillers.GetAlternateLookup<ReadOnlySpan<char>>().Contains(buffer[..length]);
    }

    /// <summary>64-bit FNV-1a over the text's UTF-8 bytes.</summary>
    public static ulong Fnv1a(string text)
    {
        var hash = 1469598103934665603UL;
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in text.EnumerateRunes())
        {
            var length = rune.EncodeToUtf8(bytes);
            for (var i = 0; i < length; i++) hash = unchecked((hash ^ bytes[i]) * 1099511628211UL);
        }
        return hash;
    }

    /// <summary>A hero carries the line: not a filler, and five characters or more, or held longer than 0.65 s.</summary>
    public static bool IsHero(LyricWord word) =>
        !IsFiller(word.Text) && (TextElements.Count(word.Text) >= 5 || word.End - word.Start > 0.65);

    /// <summary>
    /// A rolling stage's words, timed at the pace of the next onset (<paramref name="following"/> for the window's last
    /// word, so its pace never changes when the next word joins) and coloured by their place in the song. Spoken sets
    /// every word at 1.10 × <paramref name="baseSize"/>; Cascade and Cutout set heroes at 0.88, fillers at 0.62 and
    /// the rest at 0.74 of it, and only heroes in the heavy cut. A word with a reading under it is set at 0.88 of its
    /// size. Cutout puts its heroes and about a quarter of its other words on labels.
    /// </summary>
    public static RollingWord[] Prepare(IReadOnlyList<LyricWord> words, double baseSize,
                                        IReadOnlyDictionary<LyricWord, string>? readings = null, double? following = null,
                                        IReadOnlyDictionary<LyricWord, int>? ordinals = null,
                                        IReadOnlyDictionary<LyricWord, IReadOnlyList<LyricWord>>? parts = null,
                                        CompositionKind composition = CompositionKind.Spoken)
    {
        ArgumentNullException.ThrowIfNull(words);
        var result = new RollingWord[words.Count];
        var spoken = composition == CompositionKind.Spoken;
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            string? reading = null;
            readings?.TryGetValue(word, out reading);
            var hash = Fnv1a(word.Text);
            var hero = IsHero(word);
            var columnSize = baseSize * (hero ? 0.88 : IsFiller(word.Text) ? 0.62 : 0.74);
            // A word with its reading under it is set a little smaller, so the pair reads as one unit.
            var compact = reading is null ? 1 : 0.88;
            var pointSize = (spoken ? baseSize * 1.10 : columnSize) * compact;
            var tile = composition == CompositionKind.Cutout && (hero || hash % 4 == 1);
            var isLast = index == words.Count - 1;
            var cadence = isLast && (following ?? double.NegativeInfinity) > word.Start
                ? following!.Value - word.Start : OrganicMotion.Cadence(words, index);
            IReadOnlyList<LyricWord>? own = null;
            parts?.TryGetValue(word, out own);
            var ordinal = index;
            if (ordinals is not null && ordinals.TryGetValue(word, out var placed)) ordinal = placed;
            result[index] = new RollingWord
            {
                Index = index,
                Source = word,
                Hash = hash,
                Seed = TimedWordKey.SeedOf(word.Text, word.Start),
                Cadence = cadence,
                RightToLeft = ScriptDirection.IsRightToLeft(word.Text),
                Hero = hero,
                PointSize = pointSize,
                Reading = reading,
                Slot = RollingWord.SlotOf(ordinal),
                Parts = own ?? [],
                Boundaries = own is null ? [] : LyricRunParts.Boundaries(own),
                TurnLimit = RollingStack.TurnLimit(word.Text, reading, pointSize, tile),
                Heavy = hero || spoken,
                Tile = tile,
            };
        }
        return result;
    }

    /// <summary>
    /// How far the stage has rolled: one step per onset, each a seeded spring paced by the gap to the next word, so
    /// quick passages move briskly and overlapping steps add their velocities. Under Reduce Motion each step is a short
    /// dissolve.
    /// </summary>
    public static double Cursor(ReadOnlySpan<RollingWord> prepared, double time, double intensity, bool reduceMotion)
    {
        double total = 0;
        foreach (var word in prepared)
        {
            var age = time - word.Source.Start;
            total += reduceMotion ? Easing.Smoothstep(age / WallpaperMotion.ReducedDissolve)
                : LyricMotion.RollingHandoff(age, word.Cadence, word.Seed, intensity);
        }
        return total;
    }

    /// <summary>
    /// Where the stack is drawn and how strongly: once at the cursor; under Reduce Motion at settled places only,
    /// dissolving from one to the next.
    /// </summary>
    public static StackDraws Stacks(double cursor, bool reduceMotion)
    {
        if (!reduceMotion) return new StackDraws(cursor, 1, double.NaN, 0);
        var low = Math.Floor(cursor);
        var share = cursor - low;
        if (share < 0.001) return new StackDraws(low, 1, double.NaN, 0);
        if (share > 0.999) return new StackDraws(low + 1, 1, double.NaN, 0);
        return new StackDraws(low, 1 - share, low + 1, share);
    }
}

/// <summary>One or two places the stack is drawn at, with their opacities (the second is NaN when unused).</summary>
public readonly record struct StackDraws(double Position, double Opacity, double SecondPosition, double SecondOpacity)
{
    public int Count => double.IsNaN(SecondPosition) ? 1 : 2;
}

/// <summary>Everything one rolling word does on one frame, worked out in one place so the stage and its tests agree.</summary>
public readonly record struct RollingTokenState(
    WordPresentation Presentation, WordPose Pose, double Shown, double Focus, double Scale, double Angle, double MaxTurn,
    RollingStack.Slot Slot)
{
    /// <summary>Spoken's curves (the fan).</summary>
    public const RollingStyle Style = RollingStyle.Fan;

    /// <param name="composition">Spoken (the fan), Cascade (the column) or Cutout (the collage).</param>
    public static RollingTokenState Make(RollingWord token, int? next, double rank, double time, double baseSize,
                                         double stageWidth, double intensity, bool reduceMotion, WordArrival arrival,
                                         WordRevealOptions options, double eligibleSince = double.NegativeInfinity,
                                         double highlightOpacity = 0, CompositionKind composition = CompositionKind.Spoken)
    {
        var word = token.Source;
        var motion = LyricMotion.ForWord(time, word.Start, word.End, token.Cadence, double.PositiveInfinity, 0, token.Seed,
                                         intensity, reduceMotion);
        var presentation = WordPresentation.Make(word, token.Index, next, time, options, motion, eligibleSince);
        var style = composition.RollingStyle();
        var pose = RollingMotion.Pose(style, rank, time - word.Start, word.End - word.Start, token.Seed, intensity, reduceMotion);
        var focus = Math.Exp(-rank * rank * 2.4);
        // Held against the time-based fade while a highlight tile is lit; the stage still fades words off the top.
        var visible = Math.Max(pose.Opacity, highlightOpacity * RollingMotion.RankOpacity(rank));
        var shown = presentation.Opacity * (presentation.Preview ? 1 : visible);
        // Settled words keep a slow float; the word in focus breathes instead.
        var drift = LyricMotion.IdleDrift(token.Seed, time, reduceMotion ? 0 : 0.55 * (1 - focus));
        // Cascade's and Cutout's column runs left of the stage's centre.
        var columnShift = composition == CompositionKind.Spoken ? 0 : -stageWidth * 0.13;
        var scale = pose.Scale * drift.Scale;
        var angle = pose.Angle + drift.Angle;
        var travel = EntranceTransform.Make(presentation.Progress, presentation.Arrival, arrival, token.PointSize,
                                            token.Direction, reduceMotion, token.TurnLimit);
        var slot = new RollingStack.Slot
        {
            Rank = rank,
            Scale = scale,
            Angle = angle,
            Dx = columnShift + (pose.X + drift.X) * baseSize,
            Lift = (pose.Y - RollingMotion.Baseline(style, rank) + drift.Y) * baseSize,
            EntranceX = travel.OffsetX,
            EntranceY = travel.OffsetY,
            Turn = travel.Turn,
            EntranceScale = travel.Scale,
            Tilt = token.Tile && !reduceMotion ? RollingLabel.Tilt : 0,
            Shown = shown,
        };
        return new RollingTokenState(presentation, pose, shown, focus, scale, angle, token.TurnLimit, slot);
    }

    /// <summary>
    /// A glued run's syllables or characters arriving one by one: each part's opacity relative to the run's own, into
    /// <paramref name="reveal"/> (one per part). False for a single token (nothing to reveal by part).
    /// </summary>
    public static bool PartReveal(RollingWord token, double time, double runOpacity, double intensity, bool reduceMotion,
                                  WordRevealOptions options, Span<double> reveal)
    {
        var parts = token.Parts;
        if (parts.Count <= 1 || token.Boundaries.Count != parts.Count + 1 || reveal.Length < parts.Count) return false;
        int? next = null;
        for (var i = 0; i < parts.Count; i++)
            if (parts[i].Start > time) { next = i; break; }
        reveal[0] = 1;
        for (var offset = 1; offset < parts.Count; offset++)
        {
            var part = parts[offset];
            var motion = LyricMotion.ForWord(time, part.Start, part.End, OrganicMotion.Cadence(parts, offset),
                double.PositiveInfinity, 0, TimedWordKey.SeedOf(part.Text, part.Start), intensity, reduceMotion);
            var opacity = WordPresentation.Make(part, offset, next, time, options, motion, parts[offset - 1].Start).Opacity;
            reveal[offset] = Math.Min(1, opacity / Math.Max(runOpacity, 0.001));
        }
        return true;
    }

    /// <summary>How far a sweeping colour has run through the word: through a glued run, each part over its own letters.</summary>
    public static double Sweep(RollingWord token, double time)
    {
        static double Progress(LyricWord word, double time) =>
            Math.Min(Math.Max((time - word.Start) / Math.Max(word.End - word.Start, 0.001), 0), 1);
        var parts = token.Parts;
        if (parts.Count <= 1 || token.Boundaries.Count != parts.Count + 1) return Progress(token.Source, time);
        double total = 0;
        for (var i = 0; i < parts.Count; i++)
            total += (token.Boundaries[i + 1] - token.Boundaries[i]) * Progress(parts[i], time);
        return Math.Min(1, total);
    }
}

/// <summary>
/// Where each word of a rolling stage sits, stacked by the size it takes: every word is measured (reading, scale and
/// tilt included) and neighbours are spaced by their real extents plus a gap, so no two can meet at any size.
/// </summary>
public static class RollingStack
{
    public record struct Slot
    {
        public Slot() { }
        /// <summary>Continuous. 0 is the word in focus; older words (positive) stack above it, words to come below.</summary>
        public double Rank { get; set; }
        public double Scale { get; set; } = 1;
        /// <summary>Degrees.</summary>
        public double Angle { get; set; }
        /// <summary>Horizontal place relative to the stage's anchor.</summary>
        public double Dx { get; set; }
        /// <summary>Breath and float: small, and well inside the gap.</summary>
        public double Lift { get; set; }
        /// <summary>The entrance's travel still to unwind.</summary>
        public double EntranceX { get; set; }
        public double EntranceY { get; set; }
        /// <summary>The entrance's turn still to unwind, degrees (signed).</summary>
        public double Turn { get; set; }
        public double EntranceScale { get; set; } = 1;
        /// <summary>A label's own tilt, degrees.</summary>
        public double Tilt { get; set; }
        /// <summary>How visible the unit is; not used for placement.</summary>
        public double Shown { get; set; } = 1;
    }

    /// <summary>
    /// The largest entrance turn for a unit, degrees: the ends of the label may swing by at most about a third of its
    /// height. Estimated from the text, so it is known before layout.
    /// </summary>
    public static double TurnLimit(string text, string? reading, double pointSize, bool tile)
    {
        static double Width(string text, double size)
        {
            double total = 0;
            foreach (var rune in text.EnumerateRunes()) total += size * (rune.Value >= 0x1100 ? 1.0 : 0.6);
            return total;
        }
        var readingWidth = reading is null ? 0 : Width(reading, pointSize * 0.56);
        var unitWidth = Math.Max(Width(text, pointSize), readingWidth) + (tile ? 22 : 4);
        var unitHeight = pointSize * 1.3 + (reading is null ? 0 : pointSize * 0.75);
        var ratio = Math.Min(1, 0.6 * unitHeight / Math.Max(1, unitWidth));
        return Math.Min(12, Math.Asin(ratio) * 180 / Math.PI);
    }

    /// <summary>The height a unit can take: its scale and tilt, the entrance's turn and scale, and room on both sides for the travel still to come.</summary>
    public static double Extent(SizeD size, in Slot slot)
    {
        var radians = Math.Min(90, Math.Abs(slot.Angle) + Math.Abs(slot.Turn) + slot.Tilt) * Math.PI / 180;
        var rotated = size.Height * Math.Abs(Math.Cos(radians)) + size.Width * Math.Abs(Math.Sin(radians));
        return rotated * Math.Max(0, slot.Scale) * Math.Max(1, slot.EntranceScale) + 2 * Math.Abs(slot.EntranceY);
    }

    /// <summary>
    /// Centres for every unit, into <paramref name="centres"/>. Units are ordered by rank and spaced by half of each
    /// neighbour's extent plus <paramref name="gap"/>; the stack is shifted so rank 0 falls on <paramref name="anchor"/>.
    /// Continuous in rank. <paramref name="order"/> and <paramref name="stack"/> are scratch space of the same length.
    /// </summary>
    public static void Centres(ReadOnlySpan<SizeD> sizes, ReadOnlySpan<Slot> slots, PointD anchor, double gap,
                               Span<PointD> centres, Span<int> order, Span<double> stack)
    {
        var count = slots.Length;
        for (var i = 0; i < count; i++) { centres[i] = anchor; order[i] = i; }
        if (count == 0) return;
        // Insertion sort by rank, highest first (stable, allocation-free; a stage holds a handful of words).
        for (var i = 1; i < count; i++)
        {
            var member = order[i];
            var j = i - 1;
            while (j >= 0 && slots[order[j]].Rank < slots[member].Rank) { order[j + 1] = order[j]; j--; }
            order[j + 1] = member;
        }
        stack[0] = 0;
        for (var k = 1; k < count; k++)
        {
            stack[k] = stack[k - 1] + (Extent(sizes[order[k - 1]], slots[order[k - 1]]) + Extent(sizes[order[k]], slots[order[k]])) / 2 + gap;
        }
        int first = order[0], last = order[count - 1];
        double origin;
        var found = -1;
        for (var k = 0; k + 1 < count; k++)
        {
            if (slots[order[k]].Rank >= 0 && slots[order[k + 1]].Rank <= 0) { found = k; break; }
        }
        if (found >= 0)
        {
            double upper = slots[order[found]].Rank, lower = slots[order[found + 1]].Rank;
            var share = upper - lower > 1e-9 ? upper / (upper - lower) : 0;
            origin = stack[found] + share * (stack[found + 1] - stack[found]);
        }
        else if (slots[first].Rank < 0)
        {
            // Nothing sung yet: the first word waits below the anchor at its plain height.
            var plain = slots[first] with { EntranceX = 0, EntranceY = 0, Turn = 0, EntranceScale = 1, Tilt = 0 };
            origin = slots[first].Rank * (Extent(sizes[first], plain) + gap);
        }
        else
        {
            // Only older words: the anchor is below the last of them.
            origin = stack[count - 1] + slots[last].Rank * (Extent(sizes[last], slots[last]) + gap);
        }
        for (var k = 0; k < count; k++)
        {
            var member = order[k];
            centres[member] = new PointD(anchor.X + slots[member].Dx, anchor.Y + stack[k] - origin + slots[member].Lift);
        }
    }

    /// <summary><see cref="Centres(ReadOnlySpan{SizeD}, ReadOnlySpan{Slot}, PointD, double, Span{PointD}, Span{int}, Span{double})"/>, allocating its own result (tests).</summary>
    public static PointD[] Centres(IReadOnlyList<SizeD> sizes, IReadOnlyList<Slot> slots, PointD anchor, double gap)
    {
        var count = slots.Count;
        var result = new PointD[count];
        Centres([.. sizes], [.. slots], anchor, gap, result, new int[count], new double[count]);
        return result;
    }

    /// <summary>The fit the stage applies to a placed unit (<see cref="WordStageFit"/>), with the inputs the drawing uses.</summary>
    public static double Fit(SizeD size, in Slot slot, PointD centre, SizeD stage, double margin)
    {
        var envelope = new SizeD(size.Width + 2 * Math.Abs(slot.EntranceX), size.Height + 2 * Math.Abs(slot.EntranceY));
        return WordStageFit.Scale(envelope, stage, centre, slot.Scale * Math.Max(1, slot.EntranceScale),
                                  Math.Abs(slot.Angle) + Math.Abs(slot.Turn) + slot.Tilt, margin);
    }

    /// <summary>A unit as drawn: turned, scaled by its own, the entrance's and the stage's scale, and moved by the entrance's remaining travel.</summary>
    public static Box DrawnBox(SizeD size, in Slot slot, PointD centre, double fit = 1)
    {
        var radians = Math.Min(90, Math.Abs(slot.Angle + slot.Turn) + slot.Tilt) * Math.PI / 180;
        double cosine = Math.Abs(Math.Cos(radians)), sine = Math.Abs(Math.Sin(radians));
        var scale = slot.Scale * slot.EntranceScale * fit;
        var width = (size.Width * cosine + size.Height * sine) * scale;
        var height = (size.Height * cosine + size.Width * sine) * scale;
        var x = centre.X + slot.EntranceX * slot.Scale * fit;
        var y = centre.Y + slot.EntranceY * slot.Scale * fit;
        return new Box(x - width / 2, y - height / 2, width, height);
    }
}

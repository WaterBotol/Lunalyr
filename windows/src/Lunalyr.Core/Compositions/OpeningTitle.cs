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

    /// <summary>
    /// The cover's side, as a share of the title's nominal size: the same for every title, so the card keeps its size
    /// whether the title takes one line or two (the Mac's 297b2f2).
    /// </summary>
    public static double CoverShare(bool compact) => compact ? 1.65 : 2.4;

    /// <summary>The title's sizes beside a cover, largest first: it takes the largest whose two lines fit.</summary>
    public static ReadOnlySpan<double> TitleSteps => [1, 0.84, 0.7, 0.58];

    /// <summary>Between the title's last baseline and the artist's capitals, as a share of the cover's side.</summary>
    public const double BlockGap = 0.16;

    /// <summary>How much shorter than the cover a block may be and still be spread from its top to its foot, as a share of its side.</summary>
    public const double CoverStretch = 0.14;

    /// <summary>How long a card keeps an empty place for a cover still being fetched; after that, the words take the room.</summary>
    public const double CoverWait = 8.0;

    public static bool ReservesCover(double age) => age < CoverWait;

    /// <summary>
    /// The card's spacing, one scale in steps of the title's nominal size (the Mac's 11f1228): <c>Line</c> between lines
    /// that belong together (the artist and album), <c>Reading</c> under the title, <c>Group</c> between the title and
    /// the details, and between the cover and the words.
    /// </summary>
    public readonly record struct Spacing(double Nominal)
    {
        public double Line => Nominal * 0.08;
        public double Reading => Nominal * 0.12;
        public double Group => Nominal * 0.24;
    }

    // Where it sits.

    /// <summary>Kept clear of the screen's edges (and, at the top, of what sits there), in points.</summary>
    public const double EdgeMargin = 16, TopMargin = 40;

    /// <summary>
    /// The card's box on a desktop <paramref name="width"/> × <paramref name="height"/> (origin top left): as wide as a
    /// title may run and as tall as a full card with a cover, at its scale; its lower-left corner low on the left, or
    /// where the user put it (<paramref name="corner"/>: its left edge and lift from the bottom as shares of the screen),
    /// kept on screen (the Mac's <c>OpeningTitle.box</c>, 3276613).
    /// </summary>
    /// <param name="points">Pixels per point: the margins and the default place are in points.</param>
    public static (double X, double Y, double Width, double Height) Box(double width, double height, double baseSize, double points = 1,
                                                                       (double Left, double Lift)? corner = null, double scale = 1)
    {
        scale = CardScale(scale);
        var edge = EdgeMargin * points;
        var boxWidth = Math.Min(width * 0.46 * scale, Math.Max(1, width - 2 * edge));
        var boxHeight = Math.Min(baseSize * scale * 2.3, Math.Max(1, height - edge - TopMargin * points));
        var leading = corner is { } c ? c.Left * width : Math.Max(40 * points, width * 0.058);
        var bottom = corner is { } d ? d.Lift * height : Math.Max(96 * points, height * 0.13);
        var x = Math.Min(Math.Max(edge, leading), Math.Max(edge, width - boxWidth - edge));
        var lift = Math.Min(Math.Max(edge, bottom), Math.Max(edge, height - boxHeight - TopMargin * points));
        return (x, height - lift - boxHeight, boxWidth, boxHeight);
    }

    /// <summary>
    /// Which of the title's sizes to set (<see cref="TitleSteps"/>, then the last on one line): the first that fits its
    /// width on at most two lines with no word broken (<see cref="CardBlock.Fits"/>) and, given a
    /// <paramref name="room"/>, its height; the last whatever (the Mac's <c>FirstThatFits</c>).
    /// </summary>
    public static int FirstThatFits(ReadOnlySpan<CardBlock> titles, double? room)
    {
        for (var i = 0; i < titles.Length; i++)
        {
            if (i == titles.Length - 1) return i;
            if (!titles[i].Fits) continue;
            if (room is not { } r || titles[i].Height <= r + 0.5) return i;
        }
        return Math.Max(0, titles.Length - 1);
    }

    /// <summary>
    /// The title card's grid beside a cover of <paramref name="side"/> (the Mac's <c>CoverBesideWords</c>, 11f1228 and
    /// 79088c3): the words are one block, measured by their ink, from the title's capitals to the details' last baseline,
    /// the details' capitals <paramref name="spacing"/> under the title's last baseline. A block about as tall as the cover
    /// runs from its top to its foot; a shorter one (a one-line title) is centred on it; a taller one steps the title down
    /// to fit, and as a last resort runs on below the cover's foot.
    /// </summary>
    /// <param name="titles">The title (with its reading) at each of its sizes, largest first.</param>
    /// <param name="details">The artist and album, or null for none.</param>
    public static CardGrid BesideCover(double side, double gap, double column, double spacing, ReadOnlySpan<CardBlock> titles,
                                       CardBlock? details)
    {
        if (titles.Length == 0) return default;
        var hasDetails = details is { Height: > 0.5 };
        var d = details ?? default;
        var detailsCap = hasDetails ? d.Cap : 0;
        // The details from their capitals to their last baseline.
        var detailsInk = hasDetails ? d.LastBaseline - detailsCap : 0;
        var between = hasDetails ? spacing : 0;
        var chosen = FirstThatFits(titles, side);
        for (var pass = 0; pass < 3; pass++)
        {
            var t = titles[chosen];
            var below = t.Height - t.LastBaseline;
            var room = Math.Max(1, t.Cap + side - between - detailsInk + below);
            chosen = FirstThatFits(titles, room);
        }
        var title = titles[chosen];
        var block = title.LastBaseline - title.Cap + between + detailsInk;
        // Where the block's top (the title's capitals) sits below the cover's top.
        var inset = block >= side * (1 - CoverStretch) ? 0 : (side - block) / 2;
        var coverTop = Math.Max(0, title.Cap - inset);
        var titleTop = coverTop + inset - title.Cap;
        var detailsTop = titleTop + title.LastBaseline + between - detailsCap;
        // Spread: the last baseline on the cover's foot.
        if (inset == 0 && block <= side && hasDetails) detailsTop = coverTop + side - d.LastBaseline;
        var height = Math.Max(coverTop + side, Math.Max(titleTop + title.Height, hasDetails ? detailsTop + d.Height : 0));
        var width = side + gap + Math.Min(column, Math.Max(title.Width, hasDetails ? d.Width : 0));
        return new CardGrid(chosen, coverTop, titleTop, detailsTop, width, height);
    }

    /// <summary>The card without a cover: the title (the first size that fits), then the details <paramref name="gap"/> below.</summary>
    public static CardGrid Stacked(ReadOnlySpan<CardBlock> titles, CardBlock? details, double gap)
    {
        if (titles.Length == 0) return default;
        var chosen = FirstThatFits(titles, null);
        var title = titles[chosen];
        var hasDetails = details is { Height: > 0.5 };
        var detailsTop = title.Height + (hasDetails ? gap : 0);
        var height = hasDetails ? detailsTop + details!.Value.Height : title.Height;
        var width = Math.Max(title.Width, hasDetails ? details!.Value.Width : 0);
        return new CardGrid(chosen, 0, 0, detailsTop, width, height);
    }

    /// <summary>The card's scale, kept between 60% and 160%.</summary>
    public static double CardScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, 0.6, 1.6) : 1;

    /// <summary>The card's box where <paramref name="placement"/> puts it.</summary>
    public static (double X, double Y, double Width, double Height) Box(double width, double height, double baseSize, double points,
                                                                       OpeningTitlePlacement placement) =>
        Box(width, height, baseSize, points, placement.Corner, placement.Scale);

    /// <summary>The placement that puts the card's box at <paramref name="box"/> on a desktop <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static OpeningTitlePlacement PlacementFor((double X, double Y, double Width, double Height) box, double width, double height,
                                                     double scale) =>
        new((box.X / Math.Max(1, width), (height - (box.Y + box.Height)) / Math.Max(1, height)), CardScale(scale));

    /// <summary>
    /// The placement after the card's box <paramref name="box"/> (desktop points, origin top left) has been dragged on the
    /// Settings picture by (<paramref name="dx"/>, <paramref name="dy"/>): moved, kept on screen as the card itself is,
    /// or (<paramref name="resizing"/>, its top-right corner) scaled from its lower-left corner along its diagonal (the
    /// Mac's <c>TitleCardHandles.dragged</c>, 3276613).
    /// </summary>
    public static OpeningTitlePlacement Dragged((double X, double Y, double Width, double Height) box, OpeningTitlePlacement placement,
                                                double dx, double dy, bool resizing, double width, double height, double baseSize,
                                                double points = 1)
    {
        if (resizing)
        {
            double diagonalX = box.Width, diagonalY = -box.Height;
            var length = diagonalX * diagonalX + diagonalY * diagonalY;
            if (!(length > 1)) return placement;
            var along = ((diagonalX + dx) * diagonalX + (diagonalY + dy) * diagonalY) / length;
            // The lower-left corner stays put.
            return PlacementFor(box, width, height, placement.Scale * Math.Max(0.1, along));
        }
        var moved = (box.X + dx, box.Y + dy, box.Width, box.Height);
        var wanted = PlacementFor(moved, width, height, placement.Scale);
        var kept = Box(width, height, baseSize, points, wanted);
        return PlacementFor(kept, width, height, placement.Scale);
    }

    /// <summary>
    /// The floating title row's placement after a drag on the Settings picture that has reached
    /// <paramref name="locationY"/> having moved <paramref name="translationX"/> across, from <paramref name="start"/>,
    /// in the window's box on the picture: along the window, and at its foot when dropped in its lower half (the Mac's
    /// <c>FloatingTitleHandles.moved</c>, dece022).
    /// </summary>
    public static OpeningTitleRow RowMoved(OpeningTitleRow start, double translationX, double locationY,
                                           (double X, double Y, double Width, double Height) window) =>
        start with
        {
            Centre = Math.Clamp(start.Centre + translationX / Math.Max(1, window.Width), 0, 1),
            Bottom = locationY > window.Y + window.Height / 2,
        };

    /// <summary>The row's placement after the corner of a row <paramref name="width"/> wide moved by <paramref name="dx"/>.</summary>
    public static OpeningTitleRow RowResized(OpeningTitleRow start, double width, double dx) =>
        start with { Scale = RowScale(start.Scale * Math.Max(0.1, (width + dx) / Math.Max(1, width))) };

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
    /// <param name="keep">
    /// "Keep the song title on screen" (the Mac's <c>custom.openingTitleAlways</c>, d849c49): the full card, from the
    /// usual moment, for the whole song, whatever the lookup finds. It rests from where the ordinary card would have
    /// left, and what waits for the card (the credits) waits only as long as for the ordinary one.
    /// </param>
    public static OpeningSchedule? Schedule(OpeningWords words, double joined = 0, double previewLead = 0.45, bool keep = false)
    {
        var partWay = joined > MidSong;
        var start = partWay ? joined + JoinLead : Lead;
        if (keep)
        {
            // The ordinary card this one stands in for: none found counts as nothing to sing along to (the Mac has no
            // Absent words; its kept card skips the staying path).
            var ordinary = Schedule(words.Kind == OpeningWordsKind.Absent ? OpeningWords.None : words, joined, previewLead);
            var settled = start + FullEntrance;
            return new OpeningSchedule(start, settled, double.PositiveInfinity, double.PositiveInfinity, OpeningSize.Full)
            {
                Stays = true,
                Kept = true,
                OrdinaryLeave = Math.Max(settled, ordinary?.Leave ?? settled),
                OrdinaryEnd = ordinary?.End ?? start,
            };
        }
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
                return Card(partWay ? OpeningSize.Compact : OpeningSize.Full, double.PositiveInfinity) with { Stays = true };
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

    /// <summary>
    /// "Title strength" (the Mac's e30fbb2, <c>custom.openingTitleStrength</c>): how strong a kept card stays once it has
    /// rested, 30% to 100% (solid); <see cref="RestOpacity"/> by default.
    /// </summary>
    public const double StrengthMin = 0.3, StrengthMax = 1;

    /// <summary>A stored Title strength, kept in range; one that is not a number is the default.</summary>
    public static double KeptStrength(double stored) =>
        double.IsFinite(stored) ? Math.Clamp(stored, StrengthMin, StrengthMax) : RestOpacity;

    /// <summary>Whether <paramref name="schedule"/> stays with no leave due: no lyrics found, or kept for the whole song.</summary>
    public static bool Stays(OpeningSchedule? schedule) => schedule is { } s && double.IsPositiveInfinity(s.Leave);

    /// <summary>
    /// A card that was staying (<paramref name="staying"/>), now that lyrics arrived at <paramref name="at"/>: it leaves
    /// from then, with its usual exit, rather than vanishing. A kept card never leaves.
    /// </summary>
    public static OpeningSchedule Yielding(OpeningSchedule staying, double at)
    {
        if (staying.Kept) return staying;
        var leave = Math.Max(at, staying.Settled);
        var exit = staying.Size == OpeningSize.Full ? FullExit : CompactExit;
        return staying with { Leave = leave, End = leave + exit };
    }

    /// <summary>
    /// The card that entered while the lyrics were looked up (<paramref name="loading"/>, on the no-words schedule), now
    /// that they arrived at <paramref name="at"/> (the Mac's 5fb4a15): one card for the rest of its life, never a second.
    /// It becomes the lyrics' own card when that is the same card (same start and size); otherwise it keeps its size and
    /// leaves in time for the first line (at once when there is no room). A card already leaving keeps its exit.
    /// </summary>
    /// <param name="lyrics">The lyrics' own schedule, or null when they leave it no room.</param>
    /// <param name="firstLine">When the first line is sung, if known.</param>
    public static OpeningSchedule Handover(OpeningSchedule loading, OpeningSchedule? lyrics, double? firstLine,
                                           double previewLead, double at)
    {
        if (!(at < loading.Leave)) return loading;
        var exit = loading.Size == OpeningSize.Full ? FullExit : CompactExit;
        double leave;
        if (lyrics is { } own && own.Start == loading.Start && own.Size == loading.Size) leave = own.Leave;
        else
        {
            leave = loading.Leave;
            if (firstLine is { } first) leave = Math.Min(leave, Deadline(first, previewLead) - exit);
        }
        leave = Math.Max(leave, at);
        return loading with { Leave = leave, End = leave + exit };
    }

    /// <summary>A staying card entering again from <paramref name="at"/>: the holding card had already left when the lookup ended.</summary>
    public static OpeningSchedule Reentering(OpeningSchedule staying, double at)
    {
        var entrance = staying.Size == OpeningSize.Full ? FullEntrance : CompactEntrance;
        return staying with { Start = at, Settled = at + entrance };
    }

    /// <summary>How far a staying card has eased to rest at <paramref name="time"/>, 0…1 (the Mac's <c>rest</c>).</summary>
    public static double Rest(OpeningSchedule? schedule, double time)
    {
        if (schedule is not { Stays: true } s || !double.IsFinite(time)) return 0;
        return Easing.Smoothstep(Math.Clamp((time - s.RestsFrom) / RestFade, 0, 1));
    }

    /// <summary>
    /// The opacity a staying card is drawn at: 1, then easing to its rest once held a while: a kept card's to
    /// <paramref name="keptStrength"/> (Title strength), one staying for want of lyrics to <see cref="RestOpacity"/>. A
    /// staying card that yields to late lyrics leaves from where it rested.
    /// </summary>
    public static double Resting(OpeningSchedule? schedule, double time, double keptStrength = RestOpacity)
    {
        var floor = schedule is { Kept: true } ? KeptStrength(keptStrength) : RestOpacity;
        return 1 - (1 - floor) * Rest(schedule, time);
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
    /// <param name="keptStrength">Title strength, where a kept card rests.</param>
    public static OpeningPhase? Phase(OpeningSchedule? schedule, double time, double keptStrength = RestOpacity)
    {
        if (schedule is not { } s || !(time >= s.Start) || !(time < s.End)) return null;
        var exit = time <= s.Leave ? 0 : (time - s.Leave) / (s.End - s.Leave);
        // Easing to rest counts as moving; held at rest, nothing on it changes.
        var resting = s.Stays && time >= s.RestsFrom && time <= s.RestsFrom + RestFade;
        return new OpeningPhase(time - s.Start, Math.Min(1, Math.Max(0, exit)), s.Size, time < s.Settled || time > s.Leave || resting)
        {
            Rest = Rest(s, time),
            Stays = double.IsPositiveInfinity(s.Leave),
            Kept = s.Kept,
            Opacity = Resting(s, time, keptStrength),
        };
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

    /// <summary>
    /// The stage's clock while the card is up: every frame while it moves, and never stopped while it is held, so its
    /// exit is drawn. A card that stays with no exit due is only looked at, for its ease to rest.
    /// </summary>
    public static LyricPace.Level Pace(LyricPace.Level level, OpeningPhase? phase, bool playing)
    {
        if (phase is not { } p) return level;
        if (p.Moving) return playing ? LyricPace.Level.Active : (level == LyricPace.Level.Resting ? LyricPace.Level.Waiting : level);
        return level == LyricPace.Level.Resting ? (playing && !p.Stays ? LyricPace.Level.Still : LyricPace.Level.Waiting) : level;
    }

    // The floating window's kept title: one row above the lyrics (the Mac's d849c49). Its margin from the window's
    // edge, the cover's side and its type, as shares of the lyric size.
    public const double RowMargin = 0.18;
    public const double RowCover = 0.62;
    public const double RowTitle = 0.38;
    public const double RowArtist = 0.3;

    /// <summary>The height kept clear for the row: its margin, the cover, and a gap before the lyrics.</summary>
    public static double RowBand(double baseSize, double scale = 1) => baseSize * (RowMargin + (RowCover + 0.16) * RowScale(scale));

    /// <summary>The row's scale, kept between 60% and 160% (the Mac's <c>RowPlacement.clamped</c>).</summary>
    public static double RowScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, 0.6, 1.6) : 1;

    /// <summary>
    /// The title row's frame in a window <paramref name="windowWidth"/> × <paramref name="windowHeight"/> (origin top
    /// left) for a row <paramref name="rowWidth"/> across: centred at <paramref name="centre"/> (a share of the window's
    /// width), kept inside the window, <see cref="RowMargin"/> in from its top or (<paramref name="bottom"/>) its foot.
    /// </summary>
    public static (double X, double Y, double Width, double Height) RowFrame(double windowWidth, double windowHeight, double rowWidth,
                                                                            double rowHeight, double baseSize, double centre = 0.5,
                                                                            bool bottom = false)
    {
        var edge = baseSize * 0.2;
        var width = Math.Min(rowWidth, Math.Max(1, windowWidth - 2 * edge));
        var x = Math.Min(Math.Max(edge, centre * windowWidth - width / 2), Math.Max(edge, windowWidth - width - edge));
        var margin = baseSize * RowMargin;
        var y = bottom ? windowHeight - margin - rowHeight : margin;
        return (x, y, width, rowHeight);
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

/// <summary>
/// Where the card sits on the desktop and how large, as set by dragging it on the Settings picture (the Mac's
/// <c>OpeningTitle.Placement</c>, <c>custom.openingTitleLeft/Lift/Scale</c>): its lower-left corner as shares of the
/// screen, so it lands in the same place on every display, and a scale. No corner: the default place, low on the left.
/// </summary>
public readonly record struct OpeningTitlePlacement((double Left, double Lift)? Corner = null, double Scale = 1)
{
    public static OpeningTitlePlacement Standard => new(null, 1);
    public bool IsStandard => Corner is null && Math.Abs(Scale - 1) < 0.001;

    /// <summary>As stored: a corner only when both shares are numbers; the scale kept in range.</summary>
    public static OpeningTitlePlacement From(double? left, double? lift, double scale) =>
        new(left is { } l && lift is { } t && double.IsFinite(l) && double.IsFinite(t) ? (l, t) : null, OpeningTitle.CardScale(scale));
}

/// <summary>
/// Where the floating window's title row sits (the Mac's <c>OpeningTitle.RowPlacement</c>,
/// <c>custom.floatingTitleCentre/Edge/Scale</c>): across the window (its centre, a share of the window's width), at the
/// window's top or foot (the lyrics make room on that side), and a scale.
/// </summary>
public readonly record struct OpeningTitleRow(double Centre = 0.5, bool Bottom = false, double Scale = 1)
{
    public static OpeningTitleRow Standard => new(0.5, false, 1);
    public bool IsStandard => Math.Abs(Centre - 0.5) < 0.001 && !Bottom && Math.Abs(Scale - 1) < 0.001;

    /// <summary>As stored: the centre within the window, the edge "bottom" or the top, the scale in range.</summary>
    public static OpeningTitleRow From(double centre, string? edge, double scale) =>
        new(double.IsFinite(centre) ? Math.Clamp(centre, 0, 1) : 0.5, edge == "bottom", OpeningTitle.RowScale(scale));
}

/// <summary>A block of the title card as set (pixels, down from its top): its size, baselines and where its ink starts.</summary>
/// <param name="Cap">Where the first line's ink (its capitals) starts.</param>
/// <param name="Fits">The title at this size fits the column on at most two lines with no word broken.</param>
public readonly record struct CardBlock(double Width, double Height, double FirstBaseline, double LastBaseline, double Cap, bool Fits = true);

/// <summary>Where the card's parts go, down from its top: the title's size (an index into its sizes), the cover, the title block and the details.</summary>
public readonly record struct CardGrid(int Title, double CoverTop, double TitleTop, double DetailsTop, double Width, double Height);

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
public readonly record struct OpeningSchedule(double Start, double Settled, double Leave, double End, OpeningSize Size)
{
    /// <summary>
    /// The card stays and rests: no lyrics were found (its leave is infinite until lyrics arrive, and a card yielding to
    /// them leaves from where it rested), or it is kept.
    /// </summary>
    public bool Stays { get; init; }

    /// <summary>Kept on screen for the whole song ("Keep the song title on screen"): only a new song ends it.</summary>
    public bool Kept { get; init; }

    /// <summary>
    /// A kept card: where the ordinary card would have left and been gone (its start when there would have been none). It
    /// rests from <see cref="OrdinaryLeave"/>, and what waits for the card waits for <see cref="OrdinaryEnd"/>.
    /// </summary>
    public double? OrdinaryLeave { get; init; }
    public double? OrdinaryEnd { get; init; }

    /// <summary>When what waits for the card to go (the credits) may start.</summary>
    public double Clears => OrdinaryEnd ?? End;

    /// <summary>When a staying card starts to ease to rest.</summary>
    public double RestsFrom => OrdinaryLeave ?? Settled + OpeningTitle.RestAfter;
}

/// <summary>Where the card is.</summary>
/// <param name="Age">Seconds since the card started entering.</param>
/// <param name="Exit">0 while held, rising to 1 as it leaves.</param>
/// <param name="Moving">Entering, easing to rest or leaving. Held, nothing on it changes.</param>
public readonly record struct OpeningPhase(double Age, double Exit, OpeningSize Size, bool Moving)
{
    /// <summary>0 at full strength, 1 rested (a staying card).</summary>
    public double Rest { get; init; }

    /// <summary>Staying with no leave due: only an input (lyrics arriving, a new song) ends it.</summary>
    public bool Stays { get; init; }

    /// <summary>Kept for the whole song.</summary>
    public bool Kept { get; init; }

    /// <summary>The card's strength before its exit: a kept card rests at Title strength, one staying for want of lyrics at <see cref="OpeningTitle.RestOpacity"/>.</summary>
    public double Opacity { get; init; } = 1;
}

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
    /// playback is back at its start. Playback sought back before that moment joins the song there instead, so a card (a
    /// kept one above all) is not missing until the old moment returns (the Mac's d849c49). A time that is not a number
    /// (no position yet) is not a sighting.
    /// </summary>
    public double Joined(string? track, double time)
    {
        if (!double.IsFinite(time)) return track == _track ? _joinedAt : 0;
        if (track != _track)
        {
            _track = track;
            _joinedAt = time;
        }
        if (time < _joinedAt) _joinedAt = time;
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

using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;
using T = Lunalyr.Core.Compositions.OpeningTitle;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>The Mac's OpeningTitleTests: the card's timing, motion and lines (the reading of a Japanese title, which needs
/// the Windows readings, is in Lunalyr.Platform.Windows.Tests).</summary>
public class OpeningTitleTests
{
    private static OpeningSchedule Some(OpeningSchedule? schedule) => Assert.NotNull(schedule);

    // Ported from OpeningTitleTests.swift: testALongIntroGetsTheFullCardGoneBeforeTheFirstLine
    [Fact]
    public void ALongIntroGetsTheFullCardGoneBeforeTheFirstLine()
    {
        var schedule = Some(T.Schedule(OpeningWords.Timed(11), previewLead: 0.45));
        Assert.Equal(OpeningSize.Full, schedule.Size);
        Assert.Equal(T.Lead, schedule.Start, 9);
        Assert.Equal(T.Lead + T.FullEntrance, schedule.Settled, 9);
        // Gone a second before the first line can first be drawn.
        Assert.True(schedule.End <= 11 - 0.45 - 0.6 + 1e-9);
        Assert.True(schedule.End <= 11 - T.Clearance);
        // A quicker exit than entrance.
        Assert.True(schedule.End - schedule.Leave < schedule.Settled - schedule.Start);
        Assert.Null(T.Phase(schedule, 0.2));
        Assert.NotNull(T.Phase(schedule, 3));
        Assert.Null(T.Phase(schedule, schedule.End));
        Assert.Null(T.Phase(schedule, 11));
    }

    // Ported from OpeningTitleTests.swift: testAVeryLongIntroHoldsThenLeavesWellBeforeTheVoice
    [Fact]
    public void AVeryLongIntroHoldsThenLeavesWellBeforeTheVoice()
    {
        var schedule = Some(T.Schedule(OpeningWords.Timed(60)));
        Assert.Equal(T.LyricHold, schedule.Leave - schedule.Settled, 9);
        Assert.True(schedule.End < 15);
    }

    // Ported from OpeningTitleTests.swift: testAFirstLineSoonGetsTheCompactCardOrNone
    [Fact]
    public void AFirstLineSoonGetsTheCompactCardOrNone()
    {
        var compact = Some(T.Schedule(OpeningWords.Timed(4.2), previewLead: 0.45));
        Assert.Equal(OpeningSize.Compact, compact.Size);
        Assert.True(compact.End <= 4.2 - T.Clearance + 1e-9);
        Assert.True(compact.Leave - compact.Settled >= T.CompactMinimumHold - 1e-9);
        // Under about three seconds: no card at all.
        Assert.Null(T.Schedule(OpeningWords.Timed(2.8), previewLead: 0.45));
        Assert.Null(T.Schedule(OpeningWords.Timed(0.4)));
        // A longer preview lead keeps it further from the line.
        var early = Some(T.Schedule(OpeningWords.Timed(9), previewLead: 1.5));
        Assert.True(early.End <= 9 - 1.5 - 0.6 + 1e-9);
    }

    // Ported from OpeningTitleTests.swift: testNeverOverlapsTheFirstLineAcrossManyStarts
    [Fact]
    public void NeverOverlapsTheFirstLineAcrossManyStarts()
    {
        for (var tenth = 0; tenth <= 400; tenth++)
        {
            var first = tenth / 10.0;
            foreach (var lead in new[] { 0.0, 0.45, 1.0, 2.0 })
            {
                if (T.Schedule(OpeningWords.Timed(first), previewLead: lead) is not { } schedule) continue;
                Assert.True(schedule.End <= first - Math.Max(T.Clearance, lead + 0.6) + 1e-9, $"{first} {lead}");
                Assert.True(schedule.Start < schedule.Settled);
                Assert.True(schedule.Settled <= schedule.Leave);
                Assert.True(schedule.Leave < schedule.End);
            }
        }
    }

    // Ported from OpeningTitleTests.swift: testWithoutWordsTheCardHoldsLongerThenFades
    [Fact]
    public void WithoutWordsTheCardHoldsLongerThenFades()
    {
        var none = Some(T.Schedule(OpeningWords.None));
        var instrumental = Some(T.Schedule(OpeningWords.Timed(null)));
        Assert.Equal(none, instrumental);
        Assert.Equal(OpeningSize.Full, none.Size);
        Assert.Equal(T.WordlessHold, none.Leave - none.Settled, 9);
        Assert.True(T.WordlessHold > T.LyricHold);
        Assert.Null(T.FirstSung([]));
        LyricLine[] lines =
        [
            new() { Start = 0, End = 0.01, Text = "Produced by: A", IsInstrumental = true, Credit = true },
            new() { Start = 0, End = 6, Text = "", IsInstrumental = true },
            new() { Start = 6, End = 9, Text = "Hello", IsInstrumental = false },
        ];
        Assert.Equal(6, T.FirstSung(lines));
    }

    // Ported from OpeningTitleTests.swift: testASongJoinedPartWayGetsABriefCompactCard
    [Fact]
    public void ASongJoinedPartWayGetsABriefCompactCard()
    {
        // Mid-song, the words already going: a brief reminder from where we joined.
        var brief = Some(T.Schedule(OpeningWords.Timed(10), joined: 95));
        Assert.Equal(OpeningSize.Compact, brief.Size);
        Assert.Equal(95 + T.JoinLead, brief.Start, 9);
        Assert.Equal(T.BriefHold, brief.Leave - brief.Settled, 9);
        // Joined during a long intro: the ordinary rules, from the join.
        var intro = Some(T.Schedule(OpeningWords.Timed(30), joined: 6));
        Assert.Equal(OpeningSize.Full, intro.Size);
        Assert.Equal(6.3, intro.Start, 9);
        Assert.True(intro.End <= 29);
        // Joined just before the first line: nothing would fit before it, and the words have not started -- no card.
        Assert.Null(T.Schedule(OpeningWords.Timed(6), joined: 5));
        // Unsynced words are already on screen: always brief.
        Assert.Equal(OpeningSize.Compact, T.Schedule(OpeningWords.Showing)?.Size);
    }

    // Ported from OpeningTitleTests.swift: testTheSongIsJoinedFromTheStartUnlessFirstSeenPartWay (tracks by key)
    [Fact]
    public void TheSongIsJoinedFromTheStartUnlessFirstSeenPartWay()
    {
        var memory = new OpeningTitleMemory();
        Assert.Equal(0, memory.Joined("1", 0.3));
        Assert.Equal(0, memory.Joined("1", 50)); // same song playing on
        Assert.Equal(120, memory.Joined("2", 120)); // a song first seen part-way
        Assert.Equal(120, memory.Joined("2", 140));
        Assert.Equal(0, memory.Joined("2", 0.5)); // back to the start: the full card again
        Assert.Equal(0, memory.Joined("2", 140));
        // The old song's time read once as the new song starts does not count as joining it part-way.
        Assert.Equal(210, memory.Joined("3", 210));
        Assert.Equal(0, memory.Joined("3", 0.1));
    }

    /// <summary>
    /// Windows: before the player gives a position the clock reads NaN; that is not a sighting, so a song whose
    /// position arrives late, mid-song, still counts as joined part-way.
    /// </summary>
    [Fact]
    public void NoPositionYetIsNotASighting()
    {
        var memory = new OpeningTitleMemory();
        Assert.Equal(0, memory.Joined("1", double.NaN));
        Assert.Equal(95, memory.Joined("1", 95));
        Assert.Equal(95, memory.Joined("1", double.NaN));
    }

    // Ported from OpeningTitleTests.swift: testPhaseIsAPureFunctionOfTime
    [Fact]
    public void PhaseIsAPureFunctionOfTime()
    {
        var schedule = Some(T.Schedule(OpeningWords.Timed(12)));
        for (var time = 0.0; time <= 12; time += 0.25) Assert.Equal(T.Phase(schedule, time), T.Phase(schedule, time));
        var entering = Assert.NotNull(T.Phase(schedule, schedule.Start + 0.5));
        Assert.True(entering.Moving);
        Assert.Equal(0, entering.Exit);
        var held = Assert.NotNull(T.Phase(schedule, schedule.Settled + 0.5));
        Assert.False(held.Moving);
        Assert.Equal(0, held.Exit);
        var leaving = Assert.NotNull(T.Phase(schedule, (schedule.Leave + schedule.End) / 2));
        Assert.True(leaving.Moving);
        Assert.Equal(0.5, leaving.Exit, 9);
    }

    // Ported from OpeningTitleTests.swift: testRevealIsStagedAndReduceMotionOnlyFades
    [Fact]
    public void RevealIsStagedAndReduceMotionOnlyFades()
    {
        var early = OpeningReveal.At(0.3, OpeningSize.Full, reduceMotion: false);
        Assert.True(early.Rule > early.Title, "The rule draws in first");
        Assert.Equal(0, early.Album); // the album comes last
        var done = OpeningReveal.At(T.FullEntrance, OpeningSize.Full, reduceMotion: false);
        foreach (var part in new[] { done.Rule, done.Title, done.Wipe, done.Reading, done.Artist, done.Album }) Assert.Equal(1, part, 9);
        var compact = OpeningReveal.At(T.CompactEntrance, OpeningSize.Compact, reduceMotion: false);
        foreach (var part in new[] { compact.Rule, compact.Title, compact.Wipe, compact.Artist }) Assert.Equal(1, part, 9);
        // Reduce Motion: one fade for the whole card, nothing staged.
        var reduced = OpeningReveal.At(0.3, OpeningSize.Full, reduceMotion: true);
        Assert.Single(new HashSet<double> { reduced.Rule, reduced.Title, reduced.Reading, reduced.Artist, reduced.Album });
        Assert.Equal(1, reduced.Wipe); // no wipe: the title fades
        Assert.Equal(1, OpeningReveal.At(0.6, OpeningSize.Full, reduceMotion: true).Title, 9);
    }

    // Ported from OpeningTitleTests.swift: testTheClockRunsWhileTheCardMovesAndNeverRestsWhileItIsUp
    [Fact]
    public void TheClockRunsWhileTheCardMovesAndNeverRestsWhileItIsUp()
    {
        var schedule = Some(T.Schedule(OpeningWords.None));
        var entering = T.Phase(schedule, schedule.Start + 0.2);
        var held = T.Phase(schedule, schedule.Settled + 1);
        Assert.Equal(LyricPace.Level.Active, T.Pace(LyricPace.Level.Still, entering, playing: true));
        Assert.Equal(LyricPace.Level.Still, T.Pace(LyricPace.Level.Resting, held, playing: true));
        Assert.Equal(LyricPace.Level.Waiting, T.Pace(LyricPace.Level.Resting, held, playing: false));
        Assert.Equal(LyricPace.Level.Ambient, T.Pace(LyricPace.Level.Ambient, held, playing: true));
        Assert.Equal(LyricPace.Level.Resting, T.Pace(LyricPace.Level.Resting, null, playing: true));
        // Still to come: the clock is kept awake for it.
        Assert.Equal(LyricPace.Level.Active, T.Pace(LyricPace.Level.Resting, T.Upcoming(schedule, 0.1), playing: true));
        Assert.Null(T.Upcoming(schedule, schedule.End + 0.1));
        var later = Some(T.Schedule(OpeningWords.Timed(10), joined: 95));
        Assert.Null(T.Upcoming(later, 30)); // long before a card, the clock may rest
    }

    // Ported from OpeningTitleTests.swift: testTheLoadingNoticeYieldsToTheCard
    [Fact]
    public void TheLoadingNoticeYieldsToTheCard()
    {
        var schedule = Some(T.Schedule(OpeningWords.None));
        Assert.Equal(0, T.Yields(schedule, 3));
        Assert.Equal(1, T.Yields(schedule, schedule.End + 1));
        Assert.Equal(1, T.Yields(null, 3));
    }

    // Ported from OpeningTitleTests.swift: testAlbumLineLeavesOutASingleNamedAfterTheSong
    [Fact]
    public void AlbumLineLeavesOutASingleNamedAfterTheSong()
    {
        Assert.Equal("Rare Hymns", T.AlbumLine("Rare Hymns", "Human Nature"));
        Assert.Null(T.AlbumLine("Fast Start - Single", "Fast Start"));
        Assert.Null(T.AlbumLine("Human Nature", "Human Nature"));
        Assert.Null(T.AlbumLine("  ", "x"));
    }

    // Ported from OpeningTitleTests.swift: testAJapaneseTitleGetsItsReadingWhenReadingsShow (the capitalisation; the
    // reading itself needs the Windows readings: WindowsReadingTests.AJapaneseTitleGetsItsReadingWhenReadingsShow)
    [Fact]
    public void ReadingsAreCapitalisedAsATitle() =>
        Assert.Equal("Yoru ni Kakeru", OpeningTitleMemory.Capitalised("yoru ni kakeru"));

    // Ported from OpeningTitleTests.swift: testTheCoverIsOnByDefaultAndOnlyWithArtwork (the artwork half; the setting's
    // default is in Lunalyr.Shell.Tests: ExtrasSettingsTests.TheOpeningTitleSettingsAreOnByDefault)
    [Fact]
    public void TheCoverIsDrawnOnlyWithArtwork()
    {
        Assert.True(T.ShowsCover(enabled: true, 4, 4));
        Assert.False(T.ShowsCover(enabled: false, 4, 4)); // setting off: no cover
        Assert.False(T.ShowsCover(enabled: true, 0, 0)); // no artwork: no cover, the words stay put
    }

    // Ported from OpeningTitleTests.swift: testSecondaryLinesKeepTheHierarchy
    [Fact]
    public void SecondaryLinesKeepTheHierarchy()
    {
        Assert.InRange(T.ArtistShare, 0.30, 0.34);
        Assert.Equal(0.26, T.AlbumShare, 2);
        Assert.True(T.AlbumShare < T.ArtistShare);
        Assert.True(OpeningReveal.At(0.15, OpeningSize.Full, false).Cover > OpeningReveal.At(0.15, OpeningSize.Full, false).Wipe,
                    "The cover leads the wipe");
    }

    [Fact]
    public void ALongTitleIsSetSmaller()
    {
        Assert.Equal(1, T.LengthScale("Human Nature"));
        Assert.Equal(0.7, T.LengthScale("Everything You Said Before the Summer Ended (Midnight Version)"));
        Assert.Equal(1, T.LengthScale("夜に駆ける"));
    }

    // ---- A song without lyrics keeps its card (the owner, 15 Nov 2026) ----

    [Fact]
    public void ACardForASongWithoutLyricsStaysThenRests()
    {
        var card = OpeningTitle.Schedule(OpeningWords.Absent)!.Value;
        Assert.True(OpeningTitle.Stays(card));
        Assert.Equal(OpeningSize.Full, card.Size);
        Assert.NotNull(OpeningTitle.Phase(card, 600)); // still up ten minutes in
        Assert.Equal(1, OpeningTitle.Resting(card, card.Settled + 5));
        Assert.Equal(OpeningTitle.RestOpacity, OpeningTitle.Resting(card, card.Settled + OpeningTitle.RestAfter + OpeningTitle.RestFade + 1), 6);
        Assert.Equal(OpeningSize.Compact, OpeningTitle.Schedule(OpeningWords.Absent, joined: 90)!.Value.Size);
    }

    [Fact]
    public void LyricsArrivingLateTakeOverWithTheUsualExit()
    {
        var card = OpeningTitle.Schedule(OpeningWords.Absent)!.Value;
        var yielding = OpeningTitle.Yielding(card, 40);
        Assert.False(OpeningTitle.Stays(yielding));
        Assert.Equal(40, yielding.Leave);
        Assert.Equal(40 + OpeningTitle.FullExit, yielding.End, 6);
        Assert.Equal(1, OpeningTitle.Resting(yielding, 20));
        // Rested before the lyrics came, it leaves from its rest rather than flaring back to full strength (the Mac's
        // leaving keeps the card's stays).
        Assert.Equal(OpeningTitle.RestOpacity, OpeningTitle.Resting(yielding, 39), 6);
    }

    [Fact]
    public void ACardComingBackAfterTheHoldingOneLeftEntersFromThen()
    {
        var staying = OpeningTitle.Schedule(OpeningWords.Absent)!.Value;
        var again = OpeningTitle.Reentering(staying, 20);
        Assert.Equal(20, again.Start);
        Assert.Equal(20 + OpeningTitle.FullEntrance, again.Settled);
        Assert.True(OpeningTitle.Stays(again));
        Assert.Equal(0, OpeningTitle.Phase(again, 20)!.Value.Age);
    }

    // The Mac's 5fb4a15: the card that entered while the lyrics were looked up is the one card, across their arrival.
    [Fact]
    public void A_loading_card_becomes_the_lyrics_card_when_it_is_the_same_card()
    {
        var loading = OpeningTitle.Schedule(OpeningWords.None)!.Value;
        var lyrics = OpeningTitle.Schedule(OpeningWords.Timed(12))!.Value;
        Assert.Equal(lyrics, OpeningTitle.Handover(loading, lyrics, 12, 0.45, 3));
    }

    [Fact]
    public void A_loading_card_keeps_its_size_and_leaves_in_time_for_an_early_first_line()
    {
        var loading = OpeningTitle.Schedule(OpeningWords.None)!.Value;
        // First line at 4.5 s: the lyrics' own card would be compact; the full card already up stays full.
        var lyrics = OpeningTitle.Schedule(OpeningWords.Timed(4.5));
        Assert.Equal(OpeningSize.Compact, lyrics!.Value.Size);
        var handed = OpeningTitle.Handover(loading, lyrics, 4.5, 0.45, 2.5);
        Assert.Equal(OpeningSize.Full, handed.Size);
        Assert.Equal(loading.Start, handed.Start);
        Assert.Equal(OpeningTitle.Deadline(4.5, 0.45), handed.End, 6);
    }

    [Fact]
    public void With_no_room_it_leaves_at_once_and_one_already_leaving_keeps_its_exit()
    {
        var loading = OpeningTitle.Schedule(OpeningWords.None)!.Value;
        var handed = OpeningTitle.Handover(loading, null, 2, 0.45, 3);
        Assert.Equal(3, handed.Leave, 6);
        Assert.Equal(3 + OpeningTitle.FullExit, handed.End, 6);
        var leaving = OpeningTitle.Handover(loading, null, 2, 0.45, loading.Leave + 0.1);
        Assert.Equal(loading, leaving);
    }

    // ---- Kept on screen for the whole song (the Mac's d849c49, e30fbb2) ----

    // Ported from OpeningTitleTests.swift: testAKeptCardEntersAsUsualAndNeverLeaves
    [Fact]
    public void A_kept_card_enters_as_usual_and_never_leaves()
    {
        var ordinary = Some(T.Schedule(OpeningWords.Timed(14)));
        var kept = Some(T.Schedule(OpeningWords.Timed(14), keep: true));
        Assert.Equal(ordinary.Start, kept.Start, 9);
        Assert.Equal(OpeningSize.Full, kept.Size);
        Assert.True(kept.Kept);
        Assert.True(kept.Stays);
        Assert.Equal(double.PositiveInfinity, kept.Leave);
        Assert.Equal(double.PositiveInfinity, kept.End);
        foreach (var time in new[] { 3.0, 30.0, 200.0, 1_000.0 }) Assert.NotNull(T.Phase(kept, time));
        // Lyrics arriving after a lookup found none do not end it.
        Assert.Equal(kept, T.Yielding(kept, 40));
    }

    // Ported from OpeningTitleTests.swift: testAKeptCardRestsWhenTheOrdinaryOneWouldHaveLeft
    [Fact]
    public void A_kept_card_rests_when_the_ordinary_one_would_have_left()
    {
        var ordinary = Some(T.Schedule(OpeningWords.Timed(14)));
        var kept = Some(T.Schedule(OpeningWords.Timed(14), keep: true));
        Assert.Equal(ordinary.Leave, kept.RestsFrom, 9);
        Assert.Equal(0, T.Rest(kept, ordinary.Leave - 0.1), 9);
        Assert.Equal(1, T.Rest(kept, ordinary.Leave + T.RestFade), 9);
        var phase = Assert.NotNull(T.Phase(kept, 60));
        Assert.Equal(T.RestOpacity, phase.Opacity, 9);
        Assert.False(phase.Moving); // held at rest, nothing on it changes
        Assert.True(phase.Kept);
        Assert.True(phase.Stays);
    }

    // Ported from OpeningTitleTests.swift: testWhatWaitsForAKeptCardWaitsOnlyForTheOrdinaryOne
    [Fact]
    public void What_waits_for_a_kept_card_waits_only_for_the_ordinary_one()
    {
        var ordinary = Some(T.Schedule(OpeningWords.Timed(14)));
        var kept = Some(T.Schedule(OpeningWords.Timed(14), keep: true));
        Assert.Equal(ordinary.End, kept.Clears, 9); // credits start where they always did
        Assert.Equal(ordinary.End, ordinary.Clears);
        // Singing at once: no ordinary card, so nothing is held up.
        var soon = Some(T.Schedule(OpeningWords.Timed(1), keep: true));
        Assert.Equal(soon.Start, soon.Clears, 9);
        Assert.Equal(soon.Settled, soon.RestsFrom, 9); // quiet as soon as it has arrived
    }

    // Windows' own: none found is a staying card of its own on Windows (Absent); kept, it is the ordinary no-words card's
    // rest and clearance that count, never an endless one.
    [Fact]
    public void A_kept_card_for_a_song_without_lyrics_still_rests_and_clears()
    {
        var kept = Some(T.Schedule(OpeningWords.Absent, keep: true));
        var none = Some(T.Schedule(OpeningWords.None));
        Assert.True(kept.Kept);
        Assert.Equal(none.Leave, kept.RestsFrom, 9);
        Assert.Equal(none.End, kept.Clears, 9);
        Assert.True(double.IsFinite(kept.RestsFrom) && double.IsFinite(kept.Clears));
    }

    // Ported from OpeningTitleTests.swift: testAKeptCardShowsWhenLunalyrOpensMidSong
    [Fact]
    public void A_kept_card_shows_when_Lunalyr_opens_mid_song()
    {
        var memory = new OpeningTitleMemory();
        var joined = memory.Joined("7", 120);
        Assert.Equal(120, joined);
        var kept = Some(T.Schedule(OpeningWords.Timed(12), joined, keep: true));
        Assert.Equal(120 + T.JoinLead, kept.Start, 9);
        Assert.Equal(OpeningSize.Full, kept.Size);
        Assert.NotNull(T.Phase(kept, 125));
        Assert.NotNull(T.Phase(kept, 200));
        // Even when the song was first seen before its position was known.
        Assert.NotNull(T.Phase(Some(T.Schedule(OpeningWords.Timed(12), 0, keep: true)), 120));
    }

    // Ported from OpeningTitleTests.swift: testSeekingBackBeforeTheJoinJoinsThere
    [Fact]
    public void Seeking_back_before_the_join_joins_there()
    {
        var memory = new OpeningTitleMemory();
        Assert.Equal(120, memory.Joined("7", 120));
        Assert.Equal(120, memory.Joined("7", 130)); // playing on
        Assert.Equal(60, memory.Joined("7", 60)); // sought back
        var kept = Some(T.Schedule(OpeningWords.Timed(12), 60, keep: true));
        Assert.NotNull(T.Phase(kept, 61)); // not missing until 120 s comes round again
        Assert.Equal(0, memory.Joined("7", 1)); // back at the start
    }

    // Ported from OpeningTitleTests.swift: testAKeptCardIsNotKeptWithoutTheSetting
    [Fact]
    public void A_kept_card_is_not_kept_without_the_setting()
    {
        Assert.False(Some(T.Schedule(OpeningWords.Timed(14))).Kept);
        Assert.Null(T.Schedule(OpeningWords.Timed(1)));
        Assert.Equal(OpeningSize.Compact, Some(T.Schedule(OpeningWords.Absent, joined: 60)).Size);
    }

    // Ported from OpeningTitleTests.swift: testTheFloatingRowHasRoomAboveTheLyrics
    [Fact]
    public void The_floating_row_has_room_above_the_lyrics()
    {
        const double width = 836, height = 202;
        var plain = ViewportMetrics.ForText(width, height, 1, 44);
        var row = ViewportMetrics.ForText(width, height, 1, 44, titleRow: true);
        Assert.Equal(0, plain.TitleBand);
        Assert.Equal(T.RowBand(44), row.TitleBand);
        Assert.Equal(plain.Stage.Height - row.TitleBand, row.Stage.Height, 3);
        // The lyrics' top (centred under the band) stays below the row.
        var lyricsTop = row.TitleBand + (height - row.TitleBand - row.Stage.Height) / 2;
        Assert.Equal(height / 2 + row.CentreShift - row.Stage.Height / 2, lyricsTop, 6);
        Assert.True(lyricsTop > 44 * (T.RowMargin + T.RowCover));
        Assert.Equal(0, ViewportMetrics.For(1512, 982, 1).TitleBand); // the desktop card sits beside the lyrics
    }

    // Ported from OpeningTitleTests.swift: testTheRowStaysInsideTheWindowAtItsEdge
    [Fact]
    public void The_row_stays_inside_the_window_at_its_edge()
    {
        const double width = 836, height = 202;
        var standard = T.RowFrame(width, height, 260, 27, 44);
        Assert.Equal(width / 2, standard.X + standard.Width / 2, 0.5); // centred at the top by default
        Assert.Equal(44 * T.RowMargin, standard.Y, 3);
        foreach (var centre in new[] { -1.0, 0, 0.1, 0.9, 1, 3 })
            foreach (var bottom in new[] { false, true })
            {
                var frame = T.RowFrame(width, height, 260, 27, 44, centre, bottom);
                Assert.True(frame.X >= 0 && frame.X + frame.Width <= width);
                Assert.True(frame.Y >= 0 && frame.Y + frame.Height <= height);
            }
        var foot = T.RowFrame(width, height, 260, 27, 44, 0.5, bottom: true);
        Assert.Equal(44 * T.RowMargin, height - (foot.Y + foot.Height), 3);
    }

    // Ported from OpeningTitleTests.swift: testTitleStrengthSetsWhereAKeptCardRests
    [Fact]
    public void Title_strength_sets_where_a_kept_card_rests()
    {
        var kept = Some(T.Schedule(OpeningWords.Timed(14), keep: true));
        var rested = kept.RestsFrom + T.RestFade + 1;
        Assert.Equal(T.RestOpacity, Assert.NotNull(T.Phase(kept, rested)).Opacity, 9); // 55% by default, as before
        foreach (var (set, expected) in new[] { (1.0, 1.0), (0.3, 0.3), (0.8, 0.8), (0.05, 0.3), (4.0, 1.0), (double.NaN, T.RestOpacity) })
        {
            Assert.Equal(expected, Assert.NotNull(T.Phase(kept, rested, set)).Opacity, 9);
            Assert.Equal(1, Assert.NotNull(T.Phase(kept, kept.Settled + 0.1, set)).Opacity, 9); // full strength before it rests
        }
        // A card staying for want of lyrics keeps its own rest.
        var staying = Some(T.Schedule(OpeningWords.Absent));
        Assert.Equal(T.RestOpacity, Assert.NotNull(T.Phase(staying, staying.RestsFrom + T.RestFade + 1, 1.0)).Opacity, 9);
    }

    // ---- The card's grid and its faces (the Mac's 11f1228, 297b2f2, 79088c3) ----

    [Fact]
    public void A_short_block_is_centred_on_the_cover_and_a_tall_one_spans_it()
    {
        // A one-line title (cap at 20 px of an 80 px line) and two lines of details, beside a 240 px cover.
        CardBlock[] oneLine = [new(300, 80, 70, 70, 20), new(250, 70, 60, 60, 18), new(200, 60, 52, 52, 15), new(150, 50, 44, 44, 12), new(150, 50, 44, 44, 12)];
        var details = new CardBlock(200, 60, 20, 52, 6);
        var grid = T.BesideCover(240, 30, 600, 240 * T.BlockGap, oneLine, details);
        Assert.Equal(0, grid.Title);
        var inkTop = grid.TitleTop + 20;
        var inkFoot = grid.DetailsTop + 52;
        Assert.Equal(inkTop - grid.CoverTop, grid.CoverTop + 240 - inkFoot, 6); // centred: as much room above as below
        Assert.True(inkTop - grid.CoverTop > 10);
        Assert.Equal(240 * T.BlockGap, grid.DetailsTop + 6 - (grid.TitleTop + 70), 6); // the step under the title
        // Two lines, as tall as the cover: capitals on its top, the last baseline on its foot.
        CardBlock[] twoLines = [new(300, 180, 70, 160, 20), new(250, 150, 60, 135, 18), new(200, 125, 52, 112, 15), new(150, 100, 44, 90, 12), new(150, 50, 44, 44, 12)];
        var spread = T.BesideCover(240, 30, 600, 240 * T.BlockGap, twoLines, details);
        Assert.Equal(spread.CoverTop, spread.TitleTop + twoLines[spread.Title].Cap, 6);
        Assert.Equal(spread.CoverTop + 240, spread.DetailsTop + 52, 6);
    }

    [Fact]
    public void A_title_steps_down_to_fit_and_never_breaks_a_word()
    {
        // The first two sizes would break a word; the third is too tall for the room; the fourth fits.
        CardBlock[] titles = [new(300, 80, 70, 70, 20, Fits: false), new(250, 70, 60, 60, 18, Fits: false), new(200, 400, 52, 380, 15),
                              new(150, 100, 44, 90, 12), new(150, 50, 44, 44, 12)];
        Assert.Equal(2, T.FirstThatFits(titles, null));
        Assert.Equal(3, T.FirstThatFits(titles, 240));
        // Nothing fits: the last, the smallest on one line, whatever.
        Assert.Equal(2, T.FirstThatFits([new(1, 900, 1, 1, 1), new(1, 900, 1, 1, 1, Fits: false), new(1, 900, 1, 1, 1)], 100));
    }

    [Fact]
    public void The_type_metrics_even_out_faces()
    {
        static double Advance(string s) => s.Length * 0.6;
        // As tall as the reference face: kept at its size, no room added.
        var reference = TitleTypeMetrics.Measure("MARVELOUS", 0.95, 0.3, 0.7, 0.55, _ => 0.7, s => s.Length * 0.67);
        Assert.Equal(1, reference.Scale, 2);
        Assert.Equal(0, reference.PadTop, 6);
        // A script whose lower case is small is set larger.
        var script = TitleTypeMetrics.Measure("wave to earth", 0.95, 0.3, 0.6, 0.35, _ => 0.6, Advance);
        Assert.True(script.Scale > 1.1, $"{script.Scale}");
        // A pixel face whose line box ends at its capitals gets room above them.
        var pixel = TitleTypeMetrics.Measure("MARVELOUS", 0.72, 0.05, 0.7, 0.7, _ => 0.7, s => s.Length * 0.67);
        Assert.True(pixel.PadTop > 0.2, $"{pixel.PadTop}");
        Assert.True(pixel.PadBottom > 0.2, $"{pixel.PadBottom}");
        // Kana measured by its own ink: as tall as the reference's fallback, kept at its size.
        var kana = TitleTypeMetrics.Measure("夜に駆ける", 0.95, 0.3, 0.7, 0.5, _ => 0.84, Advance);
        Assert.Equal(1, kana.Scale, 2);
        Assert.Equal(0.84, kana.InkTop, 6);
        // Everything stays in range.
        Assert.Equal(1.3, TitleTypeMetrics.Measure("tiny", 0.95, 0.3, 0.2, 0.1, _ => 0.2, Advance).Scale, 6);
        Assert.Equal(0.85, TitleTypeMetrics.Measure("HUGE", 0.95, 0.3, 1.5, 1.2, _ => 1.5, Advance).Scale, 6);
    }

    [Fact]
    public void The_box_sits_low_on_the_left_by_default()
    {
        foreach (var (w, h) in new[] { (1512.0, 982.0), (1280.0, 800.0), (2560.0, 1440.0) })
        {
            var baseSize = ViewportMetrics.For(w, h, 1).BaseSize;
            var box = T.Box(w, h, baseSize);
            Assert.Equal(Math.Max(40, w * 0.058), box.X, 3);
            Assert.Equal(Math.Max(96, h * 0.13), h - (box.Y + box.Height), 3);
            Assert.Equal(w * 0.46, box.Width, 3);
        }
    }

    // The Mac's pace: a card that stays with no exit due is only looked at, never kept on a running clock.
    [Fact]
    public void A_kept_card_at_rest_does_not_keep_the_clock_running()
    {
        var kept = Some(T.Schedule(OpeningWords.Timed(14), keep: true));
        var held = T.Phase(kept, 60);
        Assert.Equal(LyricPace.Level.Waiting, T.Pace(LyricPace.Level.Resting, held, playing: true));
        Assert.Equal(LyricPace.Level.Active, T.Pace(LyricPace.Level.Resting, T.Phase(kept, kept.RestsFrom + 0.5), playing: true));
    }
}

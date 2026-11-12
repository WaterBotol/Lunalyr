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
}

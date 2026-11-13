using Lunalyr.Core.Cards;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Core.Tests.Cards;

/// <summary>
/// How long a card's MP4 lasts, what its frames do at the ends and how the song's sound is cut. The Mac loads small
/// videos for its backgrounds; here a background is its length and kind, which is all the plan reads.
/// </summary>
public class LyricCardLengthTests
{
    private static readonly (double, double)[] ThreeLines = [(10, 11.6), (12, 13.6), (14, 15.6)];

    private static CardBackgroundInfo Video(double seconds, bool sound = false) =>
        new("bg.mp4", CardBackgroundKind.Video, seconds, sound);

    private static LyricCardContent Card(CardBackgroundInfo? background = null, params (double, double)[] lines) =>
        CardProbe.Content(lines) with { Background = background };

    private static double LyricsLength(LyricCardContent content) => LyricCardTiming.PageClips(content).Sum(c => c.Duration);

    // Ported from LyricCardLengthTests.swift: testWithoutAVideoTheCardFollowsTheLines
    [Fact]
    public void WithoutAVideoTheCardFollowsTheLines()
    {
        var content = Card();
        var plan = LyricCardTiming.Plan(content);
        Assert.Equal(ClipLength.MatchLyrics, plan.Length);
        Assert.False(plan.OffersChoice);
        Assert.Equal(LyricsLength(content), plan.Duration, 9);
        Assert.True(plan.Duration > 1.8);
        Assert.Null(plan.BackgroundLoop);
        Assert.False(plan.HoldsLastLine);
        Assert.Null(plan.Note);

        // A picture behaves the same, whatever the choice says.
        var withPicture = Card(new CardBackgroundInfo("still.png", CardBackgroundKind.Image, 0)) with { ClipLength = ClipLength.MatchBackground };
        var still = LyricCardTiming.Plan(withPicture);
        Assert.Equal(ClipLength.MatchLyrics, still.Length);
        Assert.False(still.OffersChoice);
        Assert.Equal(plan.Duration, still.Duration, 9);
    }

    // Ported from LyricCardLengthTests.swift: testAShortVideoAlwaysFollowsTheLinesAndLoops
    [Fact]
    public void AShortVideoAlwaysFollowsTheLinesAndLoops()
    {
        var content = Card(Video(1.5)) with { ClipLength = ClipLength.MatchBackground };
        var plan = LyricCardTiming.Plan(content);
        Assert.False(plan.OffersChoice);
        Assert.Equal(ClipLength.MatchLyrics, plan.Length);
        Assert.Equal(LyricsLength(content), plan.Duration, 9);
        Assert.Equal(new LoopTiming(1.5, 0.3), plan.BackgroundLoop);
    }

    // Ported from LyricCardLengthTests.swift: testALongerVideoSetsTheLengthAndTheLastLineHoldsThenFades
    [Fact]
    public void ALongerVideoSetsTheLengthAndTheLastLineHoldsThenFades()
    {
        var plan = LyricCardTiming.Plan(Card(Video(5)));
        Assert.True(plan.OffersChoice);
        Assert.Equal(ClipLength.MatchBackground, plan.Length);
        Assert.InRange(plan.Duration, 4.99, 5.01);
        Assert.True(plan.HoldsLastLine);
        Assert.True(plan.FadesLastLine); // more than 3 s are left after the lines
        Assert.Equal(1, plan.LyricOpacity(2));
        Assert.InRange(plan.LyricOpacity(4.5), 1 - 1e-6, 1 + 1e-6);
        Assert.InRange(plan.LyricOpacity(4.75), 0.47, 0.53);
        Assert.InRange(plan.LyricOpacity(5), -0.03, 0.03);
        Assert.Equal(0, plan.BackgroundLoop?.Crossfade); // the video plays once, so it never loops
        Assert.Contains("fades out", plan.Summary, StringComparison.Ordinal);

        // Less than 3 s left: held to the end without fading.
        var held = LyricCardTiming.Plan(Card(Video(3.5)));
        Assert.True(held.HoldsLastLine);
        Assert.False(held.FadesLastLine);
        Assert.Equal(1, held.LyricOpacity(3.49));
        Assert.Contains("stays up", held.Summary, StringComparison.Ordinal);
    }

    // Ported from LyricCardLengthTests.swift: testMatchLyricsWithALongVideoKeepsTheOldLength
    [Fact]
    public void MatchLyricsWithALongVideoKeepsTheOldLength()
    {
        var content = Card(Video(5)) with { ClipLength = ClipLength.MatchLyrics };
        var plan = LyricCardTiming.Plan(content);
        Assert.True(plan.OffersChoice);
        Assert.Equal(ClipLength.MatchLyrics, plan.Length);
        Assert.Equal(LyricsLength(content), plan.Duration, 9);
        Assert.False(plan.HoldsLastLine);

        // Lines longer than the video: it loops, crossfaded.
        var looping = Card(Video(2.5), ThreeLines) with { ClipLength = ClipLength.MatchLyrics };
        var loopPlan = LyricCardTiming.Plan(looping);
        Assert.Equal(0.3, loopPlan.BackgroundLoop?.Crossfade);
        Assert.Equal(0, loopPlan.LinesCut);
    }

    // Ported from LyricCardLengthTests.swift: testLinesThatRunPastTheVideoAreCutWithANote
    [Fact]
    public void LinesThatRunPastTheVideoAreCutWithANote()
    {
        var content = Card(Video(2.5), ThreeLines);
        Assert.True(LyricsLength(content) > 4.5);
        var plan = LyricCardTiming.Plan(content);
        Assert.Equal(ClipLength.MatchBackground, plan.Length);
        Assert.InRange(plan.Duration, 2.49, 2.51);
        Assert.Equal(2, plan.LinesCut);
        Assert.False(plan.HoldsLastLine);
        Assert.Equal("2 lines won’t fit; pick fewer lines or Match lyrics", plan.Note);
        Assert.Equal(plan.Note, plan.Summary);
    }

    // Ported from LyricCardLengthTests.swift: testMaxSixtySecondsCapsALongCard
    [Fact]
    public void MaxSixtySecondsCapsALongCard()
    {
        var content = Card(null, (10, 80));
        Assert.Equal(LyricsLength(content), LyricCardTiming.Plan(content).Duration, 9);
        Assert.Null(LyricCardTiming.Plan(content).Note);
        var capped = content with { CapsLength = true };
        var plan = LyricCardTiming.Plan(capped);
        Assert.Equal(60, plan.Duration);
        Assert.Equal(1, plan.LinesCut);
        Assert.Equal("1 line won’t fit in 60 s; pick fewer lines or turn off Max 60 s", plan.Note);
        // A short card is untouched by the cap.
        var shortCard = Card() with { CapsLength = true };
        Assert.Equal(LyricsLength(shortCard), LyricCardTiming.Plan(shortCard).Duration, 9);
    }

    // Ported from LyricCardLengthTests.swift: testLoopTimingCrossfadesAtEachSeam
    [Fact]
    public void LoopTimingCrossfadesAtEachSeam()
    {
        var loop = new LoopTiming(2, 0.3);
        Assert.Equal(1.7, loop.Period, 9);
        Assert.Equal(2, loop.Seams(5).Count);
        Assert.Equal(1.7, loop.Seams(5)[0], 9);
        Assert.Equal(3.4, loop.Seams(5)[1], 9);
        var first = loop.Sample(1.0);
        Assert.Equal(1.0, first.Position, 9);
        Assert.Null(first.Previous);
        var seam = loop.Sample(1.8);
        Assert.Equal(0.1, seam.Position, 9);
        Assert.Equal(1.8, seam.Previous!.Value, 9);
        Assert.Equal(1.0 / 3, seam.Weight, 9);
        var after = loop.Sample(2.1);
        Assert.Equal(0.4, after.Position, 9);
        Assert.Null(after.Previous);
        Assert.Equal(0.5, loop.Sample(3.4 + 0.15).Weight, 9);
        // Without a crossfade it is a plain repeat.
        var plain = new LoopTiming(2, 0);
        Assert.Equal(0.5, plain.Sample(4.5).Position, 9);
        Assert.Null(plain.Sample(4.5).Previous);
        Assert.Empty(plain.Seams(10));
    }

    private static (List<CardFrame> Frames, double End) Frames(LyricCardContent content, ClipPlan? plan)
    {
        var frames = LyricCardFrames.Schedule(content, 0.1, null, plan).ToList();
        return (frames, frames.Sum(f => f.Delay));
    }

    // Ported from LyricCardLengthTests.swift: testFramesStopAtTheVideosEndWhenTheLinesRunOver
    [Fact]
    public void FramesStopAtTheVideosEndWhenTheLinesRunOver()
    {
        var content = Card(Video(2.5), ThreeLines);
        var (_, end) = Frames(content, LyricCardTiming.Plan(content));
        Assert.InRange(end, 2.47, 2.53);
    }

    // Ported from LyricCardLengthTests.swift: testGIFFramesKeepTheirLength
    [Fact]
    public void GifFramesKeepTheirLength()
    {
        var content = Card(Video(5));
        var (_, end) = Frames(content, null);
        Assert.InRange(end, LyricsLength(content) - 0.05, LyricsLength(content) + 0.05);
    }

    // Ported from LyricCardLengthTests.swift: testFramesHoldTheLastLineThenFadeItToTheVideosEnd (the schedule's half:
    // the pixels are checked in the Shell tests)
    [Fact]
    public void FramesHoldTheLastLineThenFadeItToTheVideosEnd()
    {
        var content = Card(Video(5));
        var (frames, end) = Frames(content, LyricCardTiming.Plan(content));
        Assert.InRange(end, 4.97, 5.03);
        CardFrame Near(double time) => frames.MinBy(f => Math.Abs(f.CardTime - time));
        Assert.Equal(1, Near(3.5).LyricOpacity);
        Assert.InRange(Near(4.75).LyricOpacity, 0.4, 0.6);
        Assert.True(Near(4.95).LyricOpacity < Near(4.75).LyricOpacity);
        // The held frames show the line as it ended: the same song time from 2.5 s to 4.4 s.
        Assert.Equal(Near(2.5).SongTime, Near(4.4).SongTime);
    }

    // Ported from LyricExtrasTests.swift: testAnExcerptIncludesTimedAndUntimedPagesInOneGIF (the frames and their delays;
    // the GIF file itself is checked in the Shell tests)
    [Fact]
    public void AnExcerptIncludesTimedAndUntimedPagesInOneGif()
    {
        var word = new LyricWord("Middle", 10, 10.7);
        var line = new LyricLine { Start = 10, End = 10.8, Text = word.Text, Words = [word], WordTimed = true };
        var content = new LyricCardContent
        {
            Pages = [new CardPage(["Before"]), new CardPage([word.Text], CardAnimation.Create(line, CompositionKind.Cutout, 0)), new CardPage(["After"])],
            Title = "A Song", Artist = "An Artist", ReduceMotion = true,
        };
        var frames = LyricCardFrames.Schedule(content, LyricCardFrames.GifCadence, LyricCardFrames.GifFrameBudget(3)).ToList();
        Assert.Equal(12, frames.Count);
        Assert.Equal(1.8, frames[0].Delay, 2);
        Assert.Equal(0.08, frames[5].Delay, 2);
        Assert.Equal(1.8, frames[^1].Delay, 2);
    }

    // Ported from LyricCardLengthTests.swift: testTheSongsWindowsAreLaidEndToEnd
    [Fact]
    public async Task TheSongsWindowsAreLaidEndToEnd()
    {
        var decoder = new MemoryAudioDecoder().Add("twopart.wav", CardProbe.TwoPartTone());
        var clip = new SongAudioClip("twopart.wav", 0, 3);
        var layer = LyricCardSound.SongLayer(decoder, clip, [(0.2, 0.5), (1.5, 0.5)]);
        var rendered = await layer!.Render(1.0, TestContext.Current.CancellationToken);
        Assert.Equal(96000, rendered!.Samples.Length);
        var mono = CardProbe.Left(rendered.Samples);
        Assert.True(CardProbe.Amplitude(300, mono, 2400, 21600) > 0.25);
        Assert.True(CardProbe.Amplitude(1200, mono, 2400, 21600) < 0.02);
        Assert.True(CardProbe.Amplitude(1200, mono, 26400, 45600) > 0.25);
        Assert.True(CardProbe.Amplitude(300, mono, 26400, 45600) < 0.02);
        Assert.Null(rendered.FadeOut); // it ends with the video, which fades everything
    }

    // Ported from LyricCardLengthTests.swift: testTheSongFadesWhereItIsCutAndWhereItStops
    [Fact]
    public async Task TheSongFadesWhereItIsCutAndWhereItStops()
    {
        var decoder = new MemoryAudioDecoder().Add("twopart.wav", CardProbe.TwoPartTone());
        var clip = new SongAudioClip("twopart.wav", 0, 3);
        var cut = await LyricCardSound.SongLayer(decoder, clip, [(0, 2.0)])!.Render(1.0, TestContext.Current.CancellationToken);
        Assert.Equal(48000, cut!.FadeOut!.Value.End);
        Assert.Equal(0.4, cut.FadeOut!.Value.Seconds);

        var earlyLayer = LyricCardSound.SongLayer(decoder, clip, [(0, 0.5)])!;
        var early = await earlyLayer.Render(1.0, TestContext.Current.CancellationToken);
        Assert.Equal(24000, early!.FadeOut!.Value.End);

        // In the mix: full level before the fade, silence after it.
        var mixed = await LyricCardSound.MixdownAsync([earlyLayer], 1.0, duck: false, cancellationToken: TestContext.Current.CancellationToken);
        var mono = CardProbe.Left(mixed!.Samples!);
        var full = CardProbe.RmsDb(mono, 2400, 4800);
        Assert.True(CardProbe.RmsDb(mono, 23000, 24000) < full - 20);
        Assert.True(CardProbe.RmsDb(mono, 24000, 48000) < -100);
    }

    // Ported from LyricCardLengthTests.swift: testPartsOfTheSongTheRecordingMissesAreSilent
    [Fact]
    public async Task PartsOfTheSongTheRecordingMissesAreSilent()
    {
        var decoder = new MemoryAudioDecoder().Add("twopart.wav", CardProbe.TwoPartTone());
        var clip = new SongAudioClip("twopart.wav", 1.0, 4.0);
        var rendered = await LyricCardSound.SongLayer(decoder, clip, [(0.5, 1.0)])!.Render(1.0, TestContext.Current.CancellationToken);
        var mono = CardProbe.Left(rendered!.Samples);
        Assert.True(CardProbe.RmsDb(mono, 0, 23000) < -100);
        // From 0.5 s on: the recording's start (300 Hz), lined up with the song.
        Assert.True(CardProbe.Amplitude(300, mono, 26400, 45600) > 0.25);
    }

    // Ported from LyricCardBackgroundTests.swift: testTheSongsSoundIsCutToTheStretchesTheCardPlays (the windows, the
    // range and what a recording covers; the MP4 is checked in the Shell tests)
    [Fact]
    public void TheSongsSoundIsCutToTheStretchesTheCardPlays()
    {
        var card = CardProbe.SpacedContent();
        var windows = LyricCardSound.SongWindows(card);
        Assert.Single(windows);
        var range = LyricCardSound.SongRange(card)!.Value;
        Assert.Equal(windows[0].Start, range.From, 3);
        Assert.True(range.To - range.From > 1.5);
        // A recording of the song that begins at 9.0 s and runs 5 s.
        var clip = new SongAudioClip("tone.m4a", 9.0, 14.0);
        Assert.True(clip.Covers(range.From, range.To));
        Assert.False(new SongAudioClip(clip.Path, 11, 14).Covers(range.From, range.To));
    }
}

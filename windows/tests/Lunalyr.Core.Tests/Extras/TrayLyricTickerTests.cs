using Lunalyr.Core.Extras;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Tests.Extras;

/// <summary>
/// The tray lyric's rules, ported from the Mac's MenuBarTickerTests.swift with its inputs: a width fixed per song, long
/// lines in two halves, and one wake-up per change instead of four a second. Pure functions only; no window is made.
/// </summary>
public class TrayLyricTickerTests
{
    private static readonly Func<string, double> TenPoints = text => TrayLyricTicker.Elements(text).Count * 10;

    private static LyricLine Line(double start, double end, string text, IReadOnlyList<LyricWord>? words = null, bool instrumental = false) =>
        new() { Start = start, End = end, Text = text, Words = words ?? [], IsInstrumental = instrumental };

    /// <summary>Forty lines over about three minutes, each sung for 3 s of its 4.5 s.</summary>
    private static IReadOnlyList<LyricLine> Song =>
        [.. Enumerable.Range(0, 40).Select(index => Line(10 + index * 4.5, 10 + index * 4.5 + 3, $"Line number {index} of the song"))];

    private static IReadOnlyList<TrayLyricTicker.LineText> Plain(IEnumerable<LyricLine> lines) =>
        [.. lines.Select(l => new TrayLyricTicker.LineText(l.Text, []))];

    // Ported from MenuBarTickerTests.swift: testTheLineChangesAtItsStartAndGivesWayToANoteAfterIt
    [Fact]
    public void TheLineChangesAtItsStartAndGivesWayToANoteAfterIt()
    {
        IReadOnlyList<LyricLine> lines = [Line(10, 12, "First"), Line(20, 22, "Second"), Line(30, 31, "...", instrumental: true), Line(40, 42, "Last")];
        // Before the first line, it waits for it.
        Assert.Equal(new TrayLyricTicker.LineState(null, 0, 10), TrayLyricTicker.LineStateAt(lines, null, [], 5));
        Assert.Equal(new TrayLyricTicker.LineState(0, 0, 12 + TrayLyricTicker.FinishedAfter), TrayLyricTicker.LineStateAt(lines, 0, [], 11));
        // A finished line gives way to ♪.
        Assert.Equal(new TrayLyricTicker.LineState(null, 0, 20), TrayLyricTicker.LineStateAt(lines, 0, [], 14));
        // An instrumental break shows ♪.
        Assert.Null(TrayLyricTicker.LineStateAt(lines, 2, [], 30.5).Line);
        // The last line stays.
        Assert.Equal(new TrayLyricTicker.LineState(3, 0, null), TrayLyricTicker.LineStateAt(lines, 3, [], 50));
    }

    // Ported from MenuBarTickerTests.swift: testALongLinesSecondHalfTakesOverOnTime
    [Fact]
    public void ALongLinesSecondHalfTakesOverOnTime()
    {
        IReadOnlyList<LyricLine> lines = [Line(10, 16, "A long line"), Line(20, 22, "Next")];
        Assert.Equal(new TrayLyricTicker.LineState(0, 0, 13), TrayLyricTicker.LineStateAt(lines, 0, [13], 11));
        Assert.Equal(new TrayLyricTicker.LineState(0, 1, 16 + TrayLyricTicker.FinishedAfter), TrayLyricTicker.LineStateAt(lines, 0, [13], 13.2));
    }

    // Ported from MenuBarTickerTests.swift: testItWakesOncePerChangeNotFourTimesASecond
    [Fact]
    public void ItWakesOncePerChangeNotFourTimesASecond()
    {
        var lines = Song;
        var time = 0.0;
        var wakes = 0;
        var shownStarts = new HashSet<int>();
        var end = lines[^1].End + 10;
        var index = new LyricLineIndex(lines);
        while (time < end)
        {
            var state = TrayLyricTicker.LineStateAt(lines, index.LineAt(time), [], time);
            if (state.Line is { } shown && Math.Abs(time - lines[shown].Start) < 0.1) shownStarts.Add(shown);
            time += TrayLyricTicker.Wait(state.NextEvent - time, TrayLyricTicker.MaxWait);
            wakes++;
        }
        Assert.Equal(lines.Count, shownStarts.Count); // every line appears within 0.1 s of its start
        var seconds = end;
        Assert.True(wakes / seconds < 1.0, $"at most about one wake-up a second, was {wakes} in {seconds} s");
        Assert.True(wakes / seconds > 0.3);
    }

    // Ported from MenuBarTickerTests.swift: testWaitsStayBetweenTheFloorAndTheCeiling
    [Fact]
    public void WaitsStayBetweenTheFloorAndTheCeiling()
    {
        Assert.Equal(2, TrayLyricTicker.Wait(null, 2));
        Assert.Equal(2, TrayLyricTicker.Wait(30, 2)); // a scrub is still caught within 2 s
        Assert.Equal(TrayLyricTicker.MinWait, TrayLyricTicker.Wait(-1, 2));
        Assert.Equal(0.51, TrayLyricTicker.Wait(0.5, 2), 9); // just after the change, so the new line is read
    }

    // Ported from MenuBarTickerTests.swift: testTheWidthIsSetOncePerSongFromItsWidestLine
    [Fact]
    public void TheWidthIsSetOncePerSongFromItsWidestLine()
    {
        IReadOnlyList<LyricLine> lines = [Line(1, 2, "Short"), Line(3, 4, "A rather longer line"), Line(5, 6, "..", instrumental: true)];
        var layout = TrayLyricTicker.LayoutFor(lines, Plain(lines), 400, TenPoints);
        Assert.Equal(200 + TrayLyricTicker.WidthPadding, layout.Width); // the widest line, not each line's own width
        Assert.Equal([["Short"], ["A rather longer line"], [".."]], layout.Pages.Select(p => p.ToArray()));

        var capped = TrayLyricTicker.LayoutFor(lines, Plain(lines), 120, TenPoints);
        Assert.True(capped.Width <= 120 + TrayLyricTicker.WidthPadding); // never wider than the length setting allows
        Assert.Equal(2, capped.Pages[1].Count); // the line too wide for it is shown in halves
    }

    // Ported from MenuBarTickerTests.swift: testALongLineIsShownInTwoHalvesSplitNearItsMiddle
    [Fact]
    public void ALongLineIsShownInTwoHalvesSplitNearItsMiddle()
    {
        var pages = TrayLyricTicker.Pages("I could stay awake just to hear you breathing", 260, TenPoints);
        Assert.Equal(["I could stay awake just", "to hear you breathing"], pages);
        Assert.All(pages, p => Assert.True(TenPoints(p) <= 260));

        var kana = TrayLyricTicker.Pages("あいうえおかきくけこさしすせそ", 100, TenPoints);
        Assert.Equal(2, kana.Count); // writing without spaces splits at its middle
        Assert.Equal("あいうえおかきくけこさしすせそ", string.Concat(kana));

        var huge = TrayLyricTicker.Pages(string.Concat(Enumerable.Repeat("word ", 40)), 100, TenPoints);
        Assert.Equal(2, huge.Count); // never more than two halves
        Assert.All(huge, p => Assert.True(TenPoints(p) <= 100 && p.EndsWith('…'))); // each half shortened to fit

        Assert.Equal(["Fits"], TrayLyricTicker.Pages("Fits", 100, TenPoints));
    }

    // Ported from MenuBarTickerTests.swift: testTheSecondHalfStartsWithItsFirstSungWord
    [Fact]
    public void TheSecondHalfStartsWithItsFirstSungWord()
    {
        const string text = "one two three four";
        IReadOnlyList<LyricWord> words = [new("one", 10, 10.5), new("two", 10.5, 11), new("three", 12, 12.5), new("four", 12.5, 13)];
        var timed = Line(10, 13, text, words);
        IReadOnlyList<string> pages = ["one two", "three four"];
        Assert.Equal(12, TrayLyricTicker.PageSwitch(timed, words, text, pages));

        // Line timing only: a share of the line by characters.
        var untimed = Line(10, 20, text);
        Assert.Equal(10 + 10 * 7.0 / 17.0, TrayLyricTicker.PageSwitch(untimed, [], text, pages), 9);

        // A start-only line whose end is the next line's start: the singing is assumed to finish well before then.
        var startOnly = Line(10, 60, text) with { EndFromNextLine = true };
        Assert.True(TrayLyricTicker.PageSwitch(startOnly, [], text, pages) < 12);

        var layout = TrayLyricTicker.LayoutFor([timed], [new TrayLyricTicker.LineText(text, words)], 100, TenPoints);
        Assert.Equal([12.0], layout.PageSwitches.Select(s => s!.Value));
    }

    // Ported from MenuBarTickerTests.swift: testTheRollUsesTheMotionTokens (adapted: the roll is drawn by the strip, so
    // the tokens and the curve are checked rather than a CATransition)
    [Fact]
    public void TheRollUsesTheMotionTokens()
    {
        Assert.Equal(0.32, TrayLyricTicker.RollSeconds); // LunalyrDesign.Motion.CA.line
        Assert.Equal(0.18, TrayLyricTicker.ReducedSeconds); // LunalyrDesign.Motion.CA.reduced
        Assert.Equal(0, TrayLyricTicker.EaseOut(0));
        Assert.Equal(1, TrayLyricTicker.EaseOut(1));
        // A fast start that settles: well past halfway at a quarter of the time, and never back.
        Assert.True(TrayLyricTicker.EaseOut(0.25) > 0.6);
        var previous = 0.0;
        for (var t = 0.0; t <= 1.0; t += 0.05)
        {
            var e = TrayLyricTicker.EaseOut(t);
            Assert.True(e >= previous - 1e-9);
            previous = e;
        }
    }

    // Ported from MenuBarTickerTests.swift: testThePausedNoteUsesASymbolNotAnEmoji (adapted: the symbol is Segoe Fluent
    // Icons' Pause glyph, drawn by the strip, not an SF Symbol)
    [Fact]
    public void ThePausedNoteUsesASymbolNotAnEmoji()
    {
        Assert.DoesNotContain(TrayLyricTicker.PausedText.EnumerateRunes(), r => r.Value == 0x23F8 || r.Value >= 0x1F000);
        Assert.True(TrayLyricTicker.HideDelay >= 1.5); // a skip between songs does not pop the line away and back
    }

    // ---- Windows' own ----

    [Fact]
    public void ThePausedWallpaperSaysSoEvenWithNothingPlaying()
    {
        Assert.Equal(new TrayLyricTicker.Display.Note(TrayLyricTicker.PausedText), TrayLyricTicker.DisplayFor(paused: true, hasTrack: false, lyricsVisible: true));
        Assert.Equal(new TrayLyricTicker.Display.None(), TrayLyricTicker.DisplayFor(paused: false, hasTrack: false, lyricsVisible: true));
        Assert.Equal(new TrayLyricTicker.Display.Line(), TrayLyricTicker.DisplayFor(paused: false, hasTrack: true, lyricsVisible: true));
        Assert.Equal(new TrayLyricTicker.Display.Note(TrayLyricTicker.HiddenText), TrayLyricTicker.DisplayFor(paused: false, hasTrack: true, lyricsVisible: false));
    }

    [Fact]
    public void NothingWakesItWhenTimeAloneCannotChangeTheLine()
    {
        Assert.Equal(TrayLyricTicker.MaxWait, TrayLyricTicker.TickInterval(true, true, true, true, false, false));
        Assert.Equal(TrayLyricTicker.PausedTickInterval, TrayLyricTicker.TickInterval(true, false, true, true, false, false));
        Assert.Null(TrayLyricTicker.TickInterval(false, true, true, true, false, false)); // no song
        Assert.Null(TrayLyricTicker.TickInterval(true, true, false, true, false, false)); // no timed lyrics
        Assert.Null(TrayLyricTicker.TickInterval(true, true, true, true, true, false)); // a paused wallpaper
        Assert.Null(TrayLyricTicker.TickInterval(true, true, true, true, false, true)); // the displays asleep
        Assert.Equal(TrayLyricTicker.LineTolerance, TrayLyricTicker.Tolerance(0.5));
        Assert.Equal(0.2, TrayLyricTicker.Tolerance(2), 9);
    }

    [Fact]
    public void TheStripSitsInTheCornerBesideTheClock()
    {
        // A 1920 x 1080 display with a 48-pixel taskbar at the bottom: the work area ends at 1032.
        Assert.Equal((1612.0, 994.0), TrayLyricTicker.Place(0, 0, 1920, 1032, TrayLyricTicker.TaskbarEdge.Bottom, 300, 30, 8));
        // At the top: under it, at the right.
        Assert.Equal((1612.0, 56.0), TrayLyricTicker.Place(0, 48, 1920, 1032, TrayLyricTicker.TaskbarEdge.Top, 300, 30, 8));
        // On the left: beside it, at the bottom (where its clock is).
        Assert.Equal((56.0, 1042.0), TrayLyricTicker.Place(48, 0, 1872, 1080, TrayLyricTicker.TaskbarEdge.Left, 300, 30, 8));
        // On the right: beside it, at the bottom.
        Assert.Equal((1564.0, 1042.0), TrayLyricTicker.Place(0, 0, 1872, 1080, TrayLyricTicker.TaskbarEdge.Right, 300, 30, 8));
    }

    [Fact]
    public void TheLongestLineSettingTurnsCharactersIntoPoints()
    {
        Assert.Equal(360, TrayLyricTicker.MaxWidth(36, TenPoints));
        Assert.Equal(40, TrayLyricTicker.MaxWidth(1, TenPoints)); // four characters at least
    }
}

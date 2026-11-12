using Lunalyr.Core.Player;

namespace Lunalyr.Core.Tests.Player;

public sealed class ScrubControllerTests
{
    private readonly List<double> _seeks = [];
    private readonly PlayerInteraction _interaction = new();
    private int _felt;
    private double _clock = 100.0;
    private double _position = 30.0;

    private ScrubController Controller(ScrubLine[]? lines = null)
    {
        var controller = new ScrubController(1, _seeks.Add, () => _position, () => _clock, _interaction)
        {
            Feel = _ => _felt++,
        };
        controller.Update(new ScrubController.Context(200, "song", lines ?? [], canSeek: true));
        return controller;
    }

    // Ported from DesktopScrubberTests.swift: testOneSeekOnReleaseNoneWhileDragging
    [Fact]
    public void OneSeekOnReleaseNoneWhileDragging()
    {
        var scrub = Controller();
        scrub.MouseDown(100, 0);
        Assert.True(_interaction.Dragging); // holds the player up
        for (var step = 1; step <= 20; step++)
        {
            _clock += 0.016;
            scrub.MouseDragged(100 + step * 5, 0);
        }

        Assert.Empty(_seeks); // nothing reaches the player while the hand moves
        _clock += 0.016;
        scrub.MouseUp(ScrubMath.BarWidth / 2, 0);
        Assert.Single(_seeks);
        Assert.Equal(100, _seeks[0], 1e-9);
        Assert.False(_interaction.Dragging);
        Assert.Equal(100, scrub.DisplayedTime(30)); // holds the target over a stale redraw
        _clock += 0.7;
        Assert.Equal(30, scrub.DisplayedTime(30));
    }

    // Ported from DesktopScrubberTests.swift: testEscapeCancelsWithoutSeeking
    [Fact]
    public void EscapeCancelsWithoutSeeking()
    {
        var scrub = Controller();
        scrub.MouseDown(300, 0);
        scrub.MouseDragged(350, 0);
        scrub.Cancel();
        Assert.Empty(_seeks);
        Assert.Null(scrub.Session);
        Assert.Equal(30, scrub.DisplayedTime(30));
    }

    // Ported from DesktopScrubberTests.swift: testANewSongCancelsTheDragSilently
    [Fact]
    public void ANewSongCancelsTheDragSilently()
    {
        var scrub = Controller();
        scrub.MouseDown(300, 0);
        scrub.Update(new ScrubController.Context(180, "other", [], canSeek: true));
        Assert.Null(scrub.Session);
        scrub.MouseUp(300, 0);
        Assert.Empty(_seeks); // never seeks the new song to the old song's place
    }

    // Ported from DesktopScrubberTests.swift: testHapticsOnlyForEndsAndSnaps
    [Fact]
    public void HapticsOnlyForEndsAndSnaps()
    {
        var scrub = Controller([new ScrubLine(50, 55, "a", null, false)]);
        var fifty = ScrubMath.X(50, 200);
        scrub.MouseDown(fifty + 86, 0);
        _clock += 0.1;
        scrub.MouseDragged(fifty + 36, 0); // fast: no snap
        for (var step = 1; step <= 20; step++)
        {
            _clock += 0.1;
            scrub.MouseDragged(fifty + 36 - step * 2, 0);
        }

        Assert.Equal(1, _felt); // snapped onto the line once
        _clock += 0.1;
        scrub.MouseDragged(-20, 0);
        Assert.Equal(2, _felt); // reached the start
        scrub.MouseUp(-20, 0);
        Assert.Equal([0], _seeks);
    }

    // Ported from DesktopScrubberTests.swift: testKeysSeekByLineAndStep
    [Fact]
    public void KeysSeekByLineAndStep()
    {
        var lines = new[] { 10.0, 20, 30 }.Select(s => new ScrubLine(s, s + 2, "l", null, false)).ToArray();
        var scrub = Controller(lines);
        _position = 12;
        scrub.SeekLine(forward: true);
        Assert.Equal(19.8, _seeks[^1], 1e-9);
        Assert.Equal(19.8, scrub.Flash ?? 0, 1e-9); // the time capsule shows
        // The next press starts from the held target, not a stale clock.
        scrub.SeekLine(forward: true);
        Assert.Equal(29.8, _seeks[^1], 1e-9);
        scrub.SeekBy(-5);
        Assert.Equal(24.8, _seeks[^1], 1e-9);
        scrub.Jump(5);
        Assert.Equal(100, _seeks[^1]);
        var plain = Controller();
        _position = 50;
        plain.SeekLine(forward: false);
        Assert.Equal(40, _seeks[^1]); // without lyrics, 10 s
    }

    // Ported from DesktopScrubberTests.swift: testTitlesMakeWayForTheCapsules
    [Fact]
    public void TitlesMakeWayForTheCapsules()
    {
        // The time and lyric capsules rise over the song's titles on the card: the titles all but go while they are
        // up (hover, drag, or a key's capsule).
        Assert.True(PlayerCard.TitleOpacity(dimmed: true) < 0.1);
        Assert.Equal(1, PlayerCard.TitleOpacity(dimmed: false));
        var scrub = Controller();
        Assert.False(scrub.CapsulesUp);
        scrub.SeekBy(5);
        Assert.True(scrub.Flashing); // a key press shows the time capsule
        Assert.True(scrub.CapsulesUp);
        _clock += ScrubController.FlashSeconds + 0.01;
        Assert.False(scrub.CapsulesUp); // for a second
        var hovered = Controller();
        hovered.MouseDown(100, 0);
        Assert.True(hovered.CapsulesUp);
        hovered.MouseUp(100, 0);
    }

    // Ported from DesktopScrubberTests.swift: testCannotSeekDoesNothing
    [Fact]
    public void CannotSeekDoesNothing()
    {
        var scrub = Controller();
        scrub.Update(new ScrubController.Context(200, "song", [], canSeek: false));
        scrub.MouseDown(100, 0);
        Assert.Null(scrub.Session);
        scrub.SeekBy(5);
        Assert.Empty(_seeks);
    }

    // Ported from DesktopScrubberTests.swift: testHoverSnapsAsAClickWould
    [Fact]
    public void HoverSnapsAsAClickWould()
    {
        var scrub = Controller([new ScrubLine(50, 55, "a", null, false)]);
        scrub.PointerMoved(ScrubMath.X(50.9, 200), 0);
        Assert.Equal(50, scrub.HoverState?.Time);
        Assert.True(scrub.Hovering);
        scrub.PointerExited();
        Assert.Null(scrub.HoverState);
        Assert.False(scrub.Hovering);
    }

    // Ported from DesktopScrubberTests.swift: testRowKinds
    [Fact]
    public void RowKinds()
    {
        Assert.Equal(ScrubRowKind.Seekable, ScrubRowKinds.Of(hasTrack: true, isAdvert: false, isStream: false, duration: 200, canSeek: true));
        Assert.Equal(ScrubRowKind.ReadOnly, ScrubRowKinds.Of(true, false, false, 200, canSeek: false));
        Assert.Equal(ScrubRowKind.ElapsedOnly, ScrubRowKinds.Of(true, false, false, 0, true));
        Assert.Equal(ScrubRowKind.Live, ScrubRowKinds.Of(true, false, isStream: true, 0, true));
        Assert.Equal(ScrubRowKind.Advert, ScrubRowKinds.Of(true, isAdvert: true, true, 0, true));
        Assert.Equal(ScrubRowKind.Hidden, ScrubRowKinds.Of(hasTrack: false, false, false, 200, true));
    }

    // Ported from DesktopScrubberTests.swift: testWallpaperLyricsClearUnderADrag
    [Fact]
    public void WallpaperLyricsClearUnderADrag()
    {
        Assert.Equal(1, PlayerHoverFocus.Look(hovered: false, previewing: false).Opacity);
        Assert.Equal(22, PlayerHoverFocus.Look(hovered: true, previewing: false).Blur);
        Assert.True(PlayerHoverFocus.Look(hovered: true, previewing: true).Blur < 22);
        Assert.True(PlayerHoverFocus.Look(hovered: true, previewing: true).Opacity > 0.3);
    }

    // Ported from DesktopScrubberTests.swift: testWallpaperFocusUnderReduceMotionOnlyFades (the look; the Mac's
    // animation curves have no counterpart: the Windows wallpaper fades by a fixed time)
    [Fact]
    public void WallpaperFocusUnderReduceMotionOnlyFades()
    {
        foreach (var (hovered, previewing) in new[] { (true, false), (true, true), (false, false) })
            Assert.Equal(0, PlayerHoverFocus.Look(hovered, previewing, reduceMotion: true).Blur);
        Assert.True(PlayerHoverFocus.Look(hovered: true, previewing: false, reduceMotion: true).Opacity < 0.3); // fainter, making up for the missing blur
        Assert.Equal(0.15, PlayerHoverFocus.WallpaperOpacity(hovered: true, previewing: false));
        Assert.Equal(0.8, PlayerHoverFocus.WallpaperOpacity(hovered: true, previewing: true));
        Assert.Equal(1, PlayerHoverFocus.WallpaperOpacity(hovered: false, previewing: false));
    }

    // Ported from DesktopScrubberTests.swift: testScrubPreviewIsPerDisplay
    [Fact]
    public void ScrubPreviewIsPerDisplay()
    {
        var preview = new ScrubPreview();
        var held = true;
        preview.ButtonsHeld = () => held;
        preview.Show(42, 7);
        Assert.Equal(42, preview.SongTime(7));
        Assert.Null(preview.SongTime(8));
        Assert.Null(preview.SongTime(null));
        preview.Clear(8);
        Assert.Equal(42, preview.SongTime(7));
        held = false;
        Assert.Null(preview.SongTime(7)); // never outlives the press
        Assert.Null(preview.AnySongTime());
        held = true;
        preview.Clear(7);
        Assert.Null(preview.SongTime(7));
    }

    [Fact]
    public void ADragPreviewsItsTargetAndClearsOnRelease()
    {
        var preview = new ScrubPreview();
        var scrub = new ScrubController(3, _seeks.Add, () => _position, () => _clock, _interaction, preview);
        scrub.Update(new ScrubController.Context(200, "song", [], canSeek: true));
        scrub.MouseDown(ScrubMath.BarWidth / 4, 0);
        Assert.Equal(50, preview.SongTime(3) ?? 0, 1e-9);
        _clock += 0.1;
        scrub.MouseDragged(ScrubMath.BarWidth / 2, 0);
        Assert.Equal(100, preview.AnySongTime() ?? 0, 1e-9);
        Assert.Empty(_seeks);
        scrub.Cancel();
        Assert.Null(preview.AnySongTime());
        Assert.Empty(_seeks);
    }

    [Fact]
    public void ARefusedSeekLetsTheBarGoBackAtOnce()
    {
        var scrub = Controller();
        scrub.SeekNow(120);
        Assert.Equal(120, scrub.DisplayedTime(30));
        scrub.Release();
        Assert.Equal(30, scrub.DisplayedTime(30));
    }
}

public sealed class PlayerOptimismTests
{
    // Ported from DesktopScrubberTests.swift: testPlayPauseFlipsAtOnceAndSettles
    [Fact]
    public void PlayPauseFlipsAtOnceAndSettles()
    {
        var toggle = new OptimisticToggle();
        Assert.True(toggle.Displayed(actual: true, now: 0));
        toggle.Press(actual: true, now: 0);
        Assert.False(toggle.Displayed(actual: true, now: 0.1)); // shows paused before the player answers
        toggle.Reconcile(actual: false, now: 0.4);
        Assert.Null(toggle.Promised);
        Assert.False(toggle.Displayed(actual: false, now: 0.5));
    }

    // Ported from DesktopScrubberTests.swift: testPlayPauseFallsBackWhenThePlayerDoesNotChange
    [Fact]
    public void PlayPauseFallsBackWhenThePlayerDoesNotChange()
    {
        var toggle = new OptimisticToggle();
        toggle.Press(actual: false, now: 0);
        Assert.True(toggle.Displayed(actual: false, now: 1.4));
        Assert.False(toggle.Displayed(actual: false, now: 1.6)); // back to the truth after 1.5 s
        toggle.Reconcile(actual: false, now: 1.6);
        Assert.Null(toggle.Promised);
    }

    // Ported from DesktopScrubberTests.swift: testDoublePressCancelsOut
    [Fact]
    public void DoublePressCancelsOut()
    {
        var toggle = new OptimisticToggle();
        toggle.Press(actual: true, now: 0);
        toggle.Press(actual: true, now: 0.1);
        Assert.True(toggle.Displayed(actual: true, now: 0.2));
    }

    [Fact]
    public void ARefusedPressGoesBackToTheTruthAtOnce()
    {
        var toggle = new OptimisticToggle();
        toggle.Press(actual: true, now: 0);
        toggle.Withdraw();
        Assert.True(toggle.Displayed(actual: true, now: 0.1));
    }
}

public sealed class PlayerCardTests
{
    // Ported from PlayerSleeveTests.swift: testCardIsCentredAtEverySizeAndFitsThePanel
    [Fact]
    public void CardIsCentredAtEverySizeAndFitsThePanel()
    {
        // At rest and with search or the queue open, the card's middle is the panel's (the screen's) middle.
        foreach (var pane in new[] { PlayerPane.Song, PlayerPane.Search, PlayerPane.Queue })
        {
            var card = PlayerCard.CardHeight(pane, queueRows: 10);
            var top = PlayerCard.CardTop(panelHeight: 780, cardHeight: card);
            Assert.Equal(390, top + card / 2, 0.5);
        }

        Assert.Equal(PlayerCard.RestHeight, PlayerCard.CardHeight(PlayerPane.Song));
        Assert.Equal(PlayerCard.TallestHeight, PlayerCard.CardHeight(PlayerPane.Queue));
        foreach (var height in new[] { 780.0, 700, 500, 300 })
        {
            var top = PlayerCard.CardTop(panelHeight: height, cardHeight: PlayerCard.TallestHeight);
            Assert.True(top >= 0);
            if (height >= PlayerCard.TallestHeight) Assert.True(top + PlayerCard.TallestHeight <= height, $"the open queue fits, {height}");
        }

        Assert.Equal(ScrubMath.BarWidth, PlayerCard.Column); // the bar spans the column
        Assert.Equal(PlayerCard.Width, PlayerCard.ArtWidth + PlayerCard.Column + 3 * PlayerCard.Padding);
    }

    [Fact]
    public void NotesStayLongEnoughToRead()
    {
        Assert.Equal(2.5, PlayerCard.NoteSeconds(null));
        Assert.Equal(2.5, PlayerCard.NoteSeconds("Paused"));
        Assert.Equal(7, PlayerCard.NoteSeconds(new string('x', 200)));
    }
}

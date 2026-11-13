using Lunalyr.Shell.Extras;

namespace Lunalyr.Shell.Tests.Extras;

// lunalyr:// links: strict parsing (a link can come from any page) and routing over fake actions. No settings, network
// or app state. The Mac's inputs, unchanged.
public sealed class LunalyrLinkParsingTests
{
    private static LunalyrLink? Link(string? text) => LunalyrLink.Parse(text);

    private static LunalyrLink Command(LunalyrCommand command) => new LunalyrLink.Command(command);

    // Ported from LunalyrLinkTests.swift: testKnownLinks.
    [Fact]
    public void TestKnownLinks()
    {
        Assert.Equal(new LunalyrLink.Join("abc123defg"), Link("lunalyr://join/abc123defg"));
        Assert.Equal(new LunalyrLink.Join("abc123defg"), Link("lunalyr://join/abc123defg/"));
        Assert.Equal(Command(LunalyrCommand.ToggleLyrics), Link("lunalyr://toggle"));
        Assert.Equal(Command(LunalyrCommand.ToggleLyrics), Link("lunalyr://toggle/"));
        Assert.Equal(Command(LunalyrCommand.LyricCard), Link("lunalyr://card"));
        Assert.Equal(Command(LunalyrCommand.OpenSettings), Link("lunalyr://settings"));
        Assert.Equal(Command(LunalyrCommand.ToggleLyrics), Link("LUNALYR://Toggle"));
    }

    // Ported from LunalyrLinkTests.swift: testRoomMustBeExactlyTenLowerCaseLettersOrDigits.
    [Theory]
    [InlineData("abc123def")]
    [InlineData("abc123defgh")]
    [InlineData("ABC123DEFG")]
    [InlineData("abc123def-")]
    [InlineData("abc123def_")]
    [InlineData("abc%3123defg")]
    [InlineData("abc123defg%00")]
    [InlineData("%61bc123defg")]
    [InlineData("abc 123def")]
    [InlineData("ábc123defg")]
    public void TestRoomMustBeExactlyTenLowerCaseLettersOrDigits(string bad) => Assert.Null(Link($"lunalyr://join/{bad}"));

    // Ported from LunalyrLinkTests.swift: testRoomMustBeExactlyTenLowerCaseLettersOrDigits (its path cases).
    [Theory]
    [InlineData("lunalyr://join")]
    [InlineData("lunalyr://join/")]
    [InlineData("lunalyr://join/abc123defg/extra")]
    [InlineData("lunalyr://join//abc123defg")]
    [InlineData("lunalyr://join/../abc123defg")]
    public void TestRoomPathsThatAreNotExactlyOneRoom(string bad) => Assert.Null(Link(bad));

    // Ported from LunalyrLinkTests.swift: testAnythingElseIsIgnored.
    [Theory]
    [InlineData("lunalyr://")]
    [InlineData("lunalyr:toggle")]
    [InlineData("lunalyr:///toggle")]
    [InlineData("lunalyr://unknown")]
    [InlineData("lunalyr://toggle/now")]
    [InlineData("lunalyr://toggle?x=1")]
    [InlineData("lunalyr://toggle#x")]
    [InlineData("lunalyr://user@toggle")]
    [InlineData("lunalyr://toggle:80")]
    [InlineData("lunalyr://join/abc123defg?relay=https://evil.example")]
    [InlineData("https://join/abc123defg")]
    [InlineData("verso://toggle")]
    [InlineData("lunalyr://settings/../toggle")]
    [InlineData("lunalyr://quit")]
    [InlineData("lunalyr://share")]
    public void TestAnythingElseIsIgnored(string bad) => Assert.Null(Link(bad));

    /// <summary>
    /// Windows' own: what a command line can carry that a URL parser on the Mac would never hand over, and what
    /// System.Uri would quietly repair.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("lunalyr://toggle\" --data \"C:\\x")]
    [InlineData("lunalyr://toggle --settings")]
    [InlineData("lunalyr://toggle\t")]
    [InlineData("lunalyr://toggle\0")]
    [InlineData("lunalyr://%74oggle")]
    [InlineData("lunalyr://tog%67le")]
    [InlineData("lunalyr://toggle?")]
    [InlineData("lunalyr://toggle#")]
    [InlineData("lunalyr://toggle:")]
    [InlineData("lunalyr://@toggle")]
    [InlineData("lunalyr://toggle.")]
    [InlineData("lunalyr://toggle//")]
    [InlineData("lunalyr://toggle\\")]
    [InlineData("lunalyr:\\\\toggle")]
    [InlineData("lunalyr://ｔoggle")]
    [InlineData(" lunalyr://toggle")]
    [InlineData("--data")]
    public void WindowsCommandLineOddities(string bad) => Assert.Null(Link(bad));

    [Fact]
    public void AnythingLongerThanALinkIsNotRead()
    {
        Assert.Null(Link("lunalyr://toggle/" + new string('a', LunalyrLink.MaximumLength)));
        Assert.Null(Link(null));
    }

    // Ported from LunalyrLinkTests.swift: testJoinURLRoundTrips.
    [Fact]
    public void TestJoinURLRoundTrips()
    {
        var url = LunalyrLink.JoinUrl("abc123defg");
        Assert.Equal("lunalyr://join/abc123defg", url);
        Assert.Equal(new LunalyrLink.Join("abc123defg"), Link(url!));
        Assert.Null(LunalyrLink.JoinUrl("nope"));
    }

    // Ported from FloatingLyricsTests.swift: testFloatingLinkSwitchesSurface.
    [Fact]
    public void TestFloatingLinkSwitchesSurface()
    {
        Assert.Equal(Command(LunalyrCommand.ToggleFloating), Link("lunalyr://floating"));
        Assert.Null(Link("lunalyr://floating/on"));
        Assert.False(string.IsNullOrEmpty(LunalyrCommand.ToggleFloating.DoneText()));
    }

    // Ported from ActionHUDTests.swift: testAShortcutsActionSaysWhatHappened (its doneText part; Windows has no
    // Shortcuts app to answer, see LunalyrIntents.swift in docs/milestones/M7.md).
    [Fact]
    public void EveryCommandSaysWhatItDoes()
    {
        Assert.Equal("Opening the lyric card", LunalyrCommand.LyricCard.DoneText());
        foreach (var command in LunalyrCommands.All) Assert.False(string.IsNullOrEmpty(command.DoneText()));
    }

    /// <summary>Windows' own: what goes to the log from a link is cut short and never carries control characters.</summary>
    [Fact]
    public void LinksAreLoggedSafely()
    {
        Assert.Equal("lunalyr://toggle\\u000A\\u001B[2J", LunalyrLink.ForLog("lunalyr://toggle\n\u001b[2J"));
        Assert.EndsWith("…", LunalyrLink.ForLog(new string('x', 500)));
        Assert.Equal("(none)", LunalyrLink.ForLog(null));
    }

    /// <summary>
    /// Windows' own: after --link every argument is a link, never a flag, so a link with quotes in it can't add
    /// --data, --stats or --settings to the command line the installer registers (Lunalyr.exe --link "%1").
    /// </summary>
    [Fact]
    public void AfterLinkNothingIsAFlag()
    {
        var options = ShellOptions.Parse(["--data", @"C:\temp\lunalyr", "--link", "lunalyr://toggle", "--stats", @"C:\x.json", "--settings"]);
        Assert.Equal(["lunalyr://toggle", "--stats", @"C:\x.json", "--settings"], options.Links);
        Assert.Null(options.StatsPath);
        Assert.False(options.OpenSettings);
        Assert.Equal(@"C:\temp\lunalyr", options.DataRoot);
        Assert.Empty(options.Problems);
        Assert.Equal(["lunalyr://card"], ShellOptions.Parse(["lunalyr://card"]).Links);
        Assert.Equal(@"C:\temp\hud.png", ShellOptions.Parse(["--hud-capture", @"C:\temp\hud.png"]).HudCapturePath);
    }
}

public sealed class LunalyrLinkRouterTests
{
    private readonly List<string> _joined = [];
    private readonly List<LunalyrCommand> _ran = [];
    private readonly List<string> _notes = [];
    private bool _relaySet = true;

    private LunalyrLinkRouter Router(bool ready = true) => new(new LunalyrLinkRouter.Actions(
        Join: room =>
        {
            if (!_relaySet) return false;
            _joined.Add(room);
            return true;
        },
        Run: _ran.Add,
        Note: (text, _) => _notes.Add(text)), ready);

    // Ported from LunalyrLinkTests.swift: testRoutesKnownLinksAndIgnoresTheRest.
    [Fact]
    public void TestRoutesKnownLinksAndIgnoresTheRest()
    {
        var router = Router();
        router.Open(["lunalyr://toggle", "lunalyr://nope", "lunalyr://join/abc123defg", "lunalyr://card", "lunalyr://settings",
            "lunalyr://join/BAD"]);
        Assert.Equal([LunalyrCommand.ToggleLyrics, LunalyrCommand.LyricCard, LunalyrCommand.OpenSettings], _ran);
        Assert.Equal(["abc123defg"], _joined);
        Assert.Empty(_notes);
    }

    // Ported from LunalyrLinkTests.swift: testJoinWithoutARelaySaysWhereToAddOne. Windows has no relay setting at all
    // yet, so its note says so instead of where to add one.
    [Fact]
    public void TestJoinWithoutARelaySaysWhereToAddOne()
    {
        _relaySet = false;
        Router().Open(["lunalyr://join/abc123defg"]);
        Assert.Empty(_joined);
        Assert.Single(_notes);
        Assert.Contains("relay", _notes[0], StringComparison.Ordinal);
    }

    // Ported from LunalyrLinkTests.swift: testShortcutsActionsThatLaunchTheAppWaitUntilItHasStarted (commands from
    // outside: on Windows a link's, or a forwarded one's).
    [Fact]
    public void TestShortcutsActionsThatLaunchTheAppWaitUntilItHasStarted()
    {
        var router = Router(ready: false);
        router.Run(LunalyrCommand.ToggleLyrics);
        router.Open(["lunalyr://card"]);
        router.Run(LunalyrCommand.OpenSettings);
        Assert.Empty(_ran);
        router.BecameReady();
        Assert.Equal([LunalyrCommand.ToggleLyrics, LunalyrCommand.LyricCard, LunalyrCommand.OpenSettings], _ran); // in the order they came
        router.Run(LunalyrCommand.SyncLater);
        Assert.Equal(LunalyrCommand.SyncLater, _ran[^1]); // at once once started
    }

    // Ported from LunalyrLinkTests.swift: testLinksThatLaunchTheAppWaitUntilItHasStarted.
    [Fact]
    public void TestLinksThatLaunchTheAppWaitUntilItHasStarted()
    {
        var router = Router(ready: false);
        router.Open(["lunalyr://join/abc123defg", "lunalyr://toggle"]);
        Assert.Empty(_joined);
        Assert.Empty(_ran);
        router.BecameReady();
        Assert.Equal(["abc123defg"], _joined);
        Assert.Equal([LunalyrCommand.ToggleLyrics], _ran);
        router.BecameReady();
        Assert.Equal([LunalyrCommand.ToggleLyrics], _ran);
    }

    /// <summary>Windows' own: every link is logged, the ones turned away too, and nothing unsafe reaches an action.</summary>
    [Fact]
    public void EveryLinkIsLoggedAndOnlyGoodOnesAct()
    {
        var log = new List<string>();
        var router = new LunalyrLinkRouter(new LunalyrLinkRouter.Actions(_ => true, _ran.Add, (_, _) => { }), ready: true, log: log.Add);
        router.Open(["lunalyr://toggle", "lunalyr://toggle\" --data x", "javascript:alert(1)"]);
        Assert.Equal([LunalyrCommand.ToggleLyrics], _ran);
        Assert.Equal(3, log.Count);
        Assert.Equal(2, log.Count(l => l.StartsWith("Ignored", StringComparison.Ordinal)));
    }
}

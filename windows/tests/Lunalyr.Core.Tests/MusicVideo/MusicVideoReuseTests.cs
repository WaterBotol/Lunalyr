using Lunalyr.Core.MusicVideo;
using Kind = Lunalyr.Core.MusicVideo.YouTubeEmbedSession.EffectKind;

namespace Lunalyr.Core.Tests.MusicVideo;

/// <summary>
/// Ported from MusicVideoReuseTests.swift (the Mac's e7e50f7 and 92992f0): the next YouTube video switches inside the
/// page's ready player rather than loading a new page. The Mac's parking of a layer's page has no Windows counterpart (one
/// player host serves every song).
/// </summary>
public sealed class MusicVideoReuseTests
{
    private static YouTubeEmbedSession Session() => new(sleep: (_, ct) => Task.Delay(Timeout.Infinite, ct));

    // Ported from MusicVideoReuseTests.swift: testOnlyAReadyPlayerSwitchesVideos
    [Fact]
    public void OnlyAReadyPlayerSwitchesVideos()
    {
        Assert.Equal(YouTubePlayerReuse.Load.SwitchVideo, YouTubePlayerReuse.LoadFor(hasView: true, pageReady: true));
        Assert.Equal(YouTubePlayerReuse.Load.NewPage, YouTubePlayerReuse.LoadFor(hasView: true, pageReady: false));
        Assert.Equal(YouTubePlayerReuse.Load.NewPage, YouTubePlayerReuse.LoadFor(hasView: false, pageReady: true));
    }

    // Windows' own: the session switches videos in a ready page, renumbered so the last video's messages are told apart.
    [Fact]
    public void TheNextVideoSwitchesInsideTheReadyPage()
    {
        var session = Session();
        var first = session.Attach("4NRXx6U8ABQ");
        Assert.Equal(Kind.Load, first.Kind); // the first video: a new page
        session.Receive(new(YouTubeEmbedSession.MessageKind.Ready, 0, "4NRXx6U8ABQ", first.Page), true);
        Assert.True(session.HasPlayer);

        var next = session.Attach("dQw4w9WgXcQ");
        Assert.Equal(Kind.Switch, next.Kind);
        Assert.Equal("dQw4w9WgXcQ", next.VideoId);
        Assert.True(next.Page > first.Page);
        Assert.False(session.HasPlayer); // not ready until the new video is cued
        // The last video's messages, still on their way, are ignored.
        Assert.Equal(YouTubeEmbedSession.Effect.None,
            session.Receive(new(YouTubeEmbedSession.MessageKind.State, 1, "4NRXx6U8ABQ", first.Page), true));
        Assert.Equal(YouTubeEmbedSession.Effect.Ready,
            session.Receive(new(YouTubeEmbedSession.MessageKind.Ready, 0, "dQw4w9WgXcQ", next.Page), true));

        // A page that can't switch (its player went away): a new page.
        var again = session.Attach("4NRXx6U8ABQ");
        Assert.Equal(Kind.Switch, again.Kind);
        var refused = session.SwitchRefused();
        Assert.Equal(Kind.Load, refused.Kind);
        Assert.True(refused.Page > again.Page);

        // A failed player never switches: the next video loads a new page.
        session.Receive(new(YouTubeEmbedSession.MessageKind.Ready, 0, "4NRXx6U8ABQ", refused.Page), true);
        session.Receive(new(YouTubeEmbedSession.MessageKind.Error, 150, "4NRXx6U8ABQ", refused.Page), true);
        Assert.Equal(Kind.Load, session.Attach("dQw4w9WgXcQ").Kind);
        // Nor a closed one.
        session.Stop();
        Assert.Equal(Kind.Load, session.Attach("dQw4w9WgXcQ").Kind);
    }

    // Ported from MusicVideoReuseTests.swift: testTheSwitchScriptIsCheckedAndThePageCanSwitch
    [Fact]
    public void TheSwitchScriptIsCheckedAndThePageCanSwitch()
    {
        Assert.Equal("!!(window.lunalyrLoad && window.lunalyrLoad('dQw4w9WgXcQ', 12, 7, null))",
            YouTubeEmbed.SwitchScript("dQw4w9WgXcQ", 12.8, 7, captions: null));
        Assert.Contains("'en'))", YouTubeEmbed.SwitchScript("dQw4w9WgXcQ", 0, 1, captions: "en-GB"));
        Assert.Contains("''))", YouTubeEmbed.SwitchScript("dQw4w9WgXcQ", 0, 1, captions: "")); // the video's own track
        Assert.Contains(", 0, 1,", YouTubeEmbed.SwitchScript("dQw4w9WgXcQ", double.NaN, 1, captions: null));
        Assert.Throws<ArgumentException>(() => YouTubeEmbed.SwitchScript("x'); alert(1); ('", 0, 1, null));
        var html = YouTubeEmbed.Html("4NRXx6U8ABQ", 0, page: 3);
        Assert.Contains("window.lunalyrLoad = function (id, start, pageNo, captions)", html);
        Assert.Contains("player.cueVideoById({videoId: id", html);
        Assert.Contains("if (state === 5 && awaitingCue) { awaitingCue = false; post('ready', 0); }", html);
        Assert.Contains("video: currentVideo, page: currentPage", html); // messages carry the video switched to
        Assert.Contains("var currentVideo = '4NRXx6U8ABQ', currentPage = 3", html);
        Assert.Equal(2, YouTubeEmbed.CueWait);
    }
}

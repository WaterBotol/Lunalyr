using System.Net.Sockets;
using Lunalyr.Core.Player.Spotify;

namespace Lunalyr.Core.Tests.Player;

/// <summary>The sign-in's loopback redirect, driven by a local fake browser on a port Windows chooses.</summary>
public sealed class LoopbackRedirectListenerTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task OnlyTheCallbackWithThisSignInsStateEndsTheWait()
    {
        using var listener = LoopbackRedirectListener.Start(0);
        Assert.Equal($"http://127.0.0.1:{listener.Port}/callback", listener.RedirectUri.ToString());
        var waiting = listener.WaitAsync("STATE-1", TestContext.Current.CancellationToken);

        var icon = await LocalBrowser.GetAsync(new Uri(listener.RedirectUri, "/favicon.ico"));
        Assert.Equal(404, icon.Status);
        var stale = await LocalBrowser.GetAsync(new Uri($"{listener.RedirectUri}?code=OLD&state=%3Cscript%3Eold%3C%2Fscript%3E"));
        Assert.Equal(400, stale.Status);
        Assert.DoesNotContain("<script>", stale.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD", stale.Body, StringComparison.Ordinal);
        var raw = await LocalBrowser.RawAsync(listener.Port, "POST /callback?code=X&state=STATE-1 HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", raw, StringComparison.Ordinal);
        Assert.False(waiting.IsCompleted);

        var done = await LocalBrowser.GetAsync(new Uri($"{listener.RedirectUri}?code=AQB%2Bcode&state=STATE-1"));
        Assert.Equal(200, done.Status);
        Assert.DoesNotContain("AQB", done.Body, StringComparison.Ordinal);
        var callback = await waiting.WaitAsync(Bounded, TestContext.Current.CancellationToken);
        Assert.Equal(new LoopbackCallback("AQB+code", null), callback);
    }

    [Fact]
    public async Task SpotifysErrorComesBack()
    {
        using var listener = LoopbackRedirectListener.Start(0);
        var waiting = listener.WaitAsync("S", TestContext.Current.CancellationToken);
        var page = await LocalBrowser.GetAsync(new Uri($"{listener.RedirectUri}?error=access_denied&state=S"));
        Assert.Contains("cancelled", page.Body, StringComparison.Ordinal);
        Assert.Equal(new LoopbackCallback(null, "access_denied"), await waiting.WaitAsync(Bounded, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATakenPortFailsAtOnceAndCancellingEndsTheWait()
    {
        using var first = LoopbackRedirectListener.Start(0);
        Assert.Throws<SocketException>(() => LoopbackRedirectListener.Start(first.Port));

        using var cancel = new CancellationTokenSource();
        var waiting = first.WaitAsync("S", cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Bounded, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void QueriesAreDecoded()
    {
        var query = LoopbackRedirectListener.ParseQuery("code=a%2Bb+c&state=x&state=y&flag");
        Assert.Equal("a+b c", query["code"]);
        Assert.Equal("x", query["state"]);
        Assert.Equal("", query["flag"]);
    }
}

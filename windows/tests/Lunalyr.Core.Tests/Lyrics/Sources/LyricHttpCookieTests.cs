using System.Net.Http;
using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Core.Tests.Lyrics.Pipeline;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

/// <summary>
/// The Mac found that NetEase answers with unrelated songs once its NMTID cookie is sent back (next/integration
/// 1cdde21). Windows' shared client never keeps cookies; these tests pin that on the real handler the app uses, with
/// its connections answered in memory (no socket, no network).
/// </summary>
public sealed class LyricHttpCookieTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly NetEaseOptions Plain = new()
    {
        // Plain HTTP, so the in-memory connection needs no TLS; the host is NetEase's, so a cookie it sets would apply.
        SearchAddress = "http://music.163.com/api/search/get",
        LyricAddress = "http://music.163.com/api/song/lyric",
        Referer = "http://music.163.com/",
    };

    private static readonly SungLine[] Lines =
    [
        new("Invented first line here", 11, 13), new("Second made up line", 14, 16), new("A third line we wrote", 17, 19),
        new("Fourth line for testing", 20, 22),
    ];

    [Fact]
    public async Task NetEaseNeverGetsItsCookieBack()
    {
        var server = new CookieSettingServer();
        var handler = LyricHttpClient.CreateDefaultHandler();
        Assert.False(handler.UseCookies);
        handler.ConnectCallback = server.ConnectAsync;
        var found = await Ask(handler);
        Assert.NotNull(found);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, request => Assert.DoesNotContain("\r\ncookie:", request, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The check above can fail: the same handler with cookies on sends NMTID back on the lyric request.</summary>
    [Fact]
    public async Task WithCookiesOnTheHandlerWouldSendItBack()
    {
        var server = new CookieSettingServer();
        var handler = LyricHttpClient.CreateDefaultHandler();
        handler.UseCookies = true;
        handler.ConnectCallback = server.ConnectAsync;
        _ = await Ask(handler);
        Assert.Contains("NMTID=00Otest", server.Requests[^1], StringComparison.Ordinal);
    }

    private static async Task<RawLyrics?> Ask(SocketsHttpHandler handler)
    {
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var http = new LyricHttpClient(client, new LyricHttpOptions { MinimumHostInterval = TimeSpan.Zero, IsNetworkAvailable = () => true });
        return await new NetEaseSource(http, Plain).FetchAsync(new LyricQuery("Shape of You", "Ed Sheeran", null, 233.7), Ct);
    }

    /// <summary>
    /// Serves NetEase's search and lyric replies over in-memory connections, setting NMTID as NetEase does, and keeps
    /// every request's head.
    /// </summary>
    private sealed class CookieSettingServer
    {
        private readonly List<string> _requests = [];

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new Connection(this));

        private byte[] Respond(string head)
        {
            lock (_requests)
            {
                _requests.Add(head);
            }
            var path = head[..head.IndexOf("\r\n", StringComparison.Ordinal)];
            var body = path.Contains("/api/search/get", StringComparison.Ordinal)
                ? ServiceReplies.NetEaseSearch((1, "Shape of You", "Ed Sheeran", 233.7))
                : ServiceReplies.NetEaseLyric(ServiceReplies.Lrc(Lines));
            var bytes = Encoding.UTF8.GetBytes(body);
            var response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nSet-Cookie: NMTID=00Otest; Path=/\r\n"
                + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            return [.. Encoding.ASCII.GetBytes(response), .. bytes];
        }

        /// <summary>One connection: collects the request head, then reads back the reply and closes.</summary>
        private sealed class Connection(CookieSettingServer server) : Stream
        {
            private readonly MemoryStream _request = new();
            private readonly TaskCompletionSource<byte[]> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _read;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                _request.Write(buffer);
                var text = Encoding.ASCII.GetString(_request.ToArray());
                if (!_reply.Task.IsCompleted && text.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    _reply.TrySetResult(server.Respond(text[..text.IndexOf("\r\n\r\n", StringComparison.Ordinal)]));
                }
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Write(buffer.Span);
                return ValueTask.CompletedTask;
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var reply = await _reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                var count = Math.Min(buffer.Length, reply.Length - _read);
                reply.AsSpan(_read, count).CopyTo(buffer.Span);
                _read += count;
                return count;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _request.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}

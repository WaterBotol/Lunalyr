using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lunalyr.Core.Player.Spotify;

/// <summary>What the browser brought back to the redirect address: a code, or Spotify's error (e.g. "access_denied").</summary>
public sealed record LoopbackCallback(string? Code, string? Error);

/// <summary>
/// Waits for the sign-in redirect on <c>http://127.0.0.1:{port}/callback</c>: a plain socket on the IPv4 loopback
/// address (no http.sys, no URL reservation, nothing reachable from another machine) that reads one request line per
/// connection. Only <c>/callback</c> with the expected state ends the wait; anything else (a favicon request, a stale
/// tab from an earlier sign-in) gets a short answer and the wait goes on.
/// </summary>
/// <remarks>The pages it serves are fixed text: nothing from the request is echoed back.</remarks>
public sealed class LoopbackRedirectListener : IDisposable
{
    private const int MaxRequestBytes = 16 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private bool _disposed;

    private LoopbackRedirectListener(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        RedirectUri = new Uri($"http://127.0.0.1:{Port}/callback");
    }

    /// <summary>The port listened on (the one asked for, or the one Windows chose for 0).</summary>
    public int Port { get; }

    /// <summary><c>http://127.0.0.1:{Port}/callback</c>, exactly as it must be registered with Spotify.</summary>
    public Uri RedirectUri { get; }

    /// <summary>Starts listening. Throws <see cref="SocketException"/> when the port is taken.</summary>
    public static LoopbackRedirectListener Start(int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start(backlog: 4);
        return new LoopbackRedirectListener(listener);
    }

    /// <summary>
    /// Waits until the browser arrives at <c>/callback</c> with <paramref name="expectedState"/>, answers it with a page
    /// saying it can be closed, and returns its code or error. Cancelling ends the wait.
    /// </summary>
    public async Task<LoopbackCallback> WaitAsync(string expectedState, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedState);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var result = await HandleAsync(client, expectedState, cancellationToken).ConfigureAwait(false);
            if (result is not null) return result;
        }
    }

    private static async Task<LoopbackCallback?> HandleAsync(TcpClient client, string expectedState, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReadTimeout);
        var stream = client.GetStream();
        string? target;
        try
        {
            target = await ReadRequestTargetAsync(stream, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // a connection that never sent a request: drop it, keep waiting
        }
        catch (IOException)
        {
            return null;
        }

        if (target is null)
        {
            await RespondAsync(stream, 400, Pages.Unexpected, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var query = "";
        var path = target;
        var mark = target.IndexOf('?', StringComparison.Ordinal);
        if (mark >= 0)
        {
            path = target[..mark];
            query = target[(mark + 1)..];
        }

        if (!string.Equals(path, "/callback", StringComparison.Ordinal))
        {
            await RespondAsync(stream, 404, Pages.Unexpected, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var parameters = ParseQuery(query);
        parameters.TryGetValue("state", out var state);
        if (!string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            // Not this sign-in's (an old tab, or someone else's request): ignored.
            await RespondAsync(stream, 400, Pages.Unexpected, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (parameters.TryGetValue("error", out var error) && error.Length > 0)
        {
            await RespondAsync(stream, 200, Pages.Cancelled, cancellationToken).ConfigureAwait(false);
            return new LoopbackCallback(null, error);
        }

        if (parameters.TryGetValue("code", out var code) && code.Length > 0)
        {
            await RespondAsync(stream, 200, Pages.SignedIn, cancellationToken).ConfigureAwait(false);
            return new LoopbackCallback(code, null);
        }

        await RespondAsync(stream, 400, Pages.Unexpected, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>The request target of a <c>GET</c> request line, or null for anything else.</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
        }

        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
        var line = lineEnd >= 0 ? head[..lineEnd] : head;
        var parts = line.Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !parts[1].StartsWith('/'))
            return null;
        return parts[1];
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Decode(pair[0]);
            if (!result.ContainsKey(key)) result[key] = pair.Length > 1 ? Decode(pair[1]) : "";
        }
        return result;

        static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string page, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(page);
        var reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Bad Request" };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The browser went away first; nothing to tell it.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Stop();
        _listener.Dispose();
    }

    private static class Pages
    {
        private const string Head = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Lunalyr</title>"
            + "<style>body{font-family:'Segoe UI Variable','Segoe UI',sans-serif;margin:15vh auto;max-width:28rem;padding:0 1rem;"
            + "color:#1b1b1b;background:#f6f6f6}@media(prefers-color-scheme:dark){body{color:#eee;background:#1f1f1f}}</style></head><body>";

        public const string SignedIn = Head + "<h1>Signed in to Spotify</h1><p>You can close this tab and go back to Lunalyr.</p></body></html>";

        public const string Cancelled = Head + "<h1>Sign-in cancelled</h1><p>Nothing was changed. You can close this tab.</p></body></html>";

        public const string Unexpected = Head + "<h1>Nothing to do here</h1><p>This page is only for signing in to Spotify from Lunalyr.</p></body></html>";
    }
}

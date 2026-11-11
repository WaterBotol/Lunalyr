using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

/// <summary>A canned reply: status, body, optional headers; <see cref="Hang"/> never answers (for timeouts).</summary>
internal sealed record Reply(int Status, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null, bool Hang = false, bool Fail = false)
{
    public static Reply Text(int status, string body, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, Encoding.UTF8.GetBytes(body), headers);

    public static Reply Fixture(string relativePath, int status = 200) => new(status, Fixtures.Bytes(relativePath));

    public static Reply Never { get; } = new(200, [], Hang: true);

    /// <summary>The connection fails (as when a host is down or the PC is offline).</summary>
    public static Reply Broken { get; } = new(0, [], Fail: true);
}

/// <summary>
/// Replays replies by host + path (e.g. "lrclib.net/api/get"), never touching the network, and records every request.
/// Unknown addresses answer <see cref="Fallback"/>.
/// </summary>
internal sealed class FixtureHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Reply> _replies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<HttpRequestMessage> _requests = new();

    public Reply Fallback { get; set; } = Reply.Text(500, "");

    /// <summary>Called for every request; returning a reply overrides the table.</summary>
    public Func<HttpRequestMessage, Reply?>? Responder { get; set; }

    public FixtureHandler Set(string address, Reply reply)
    {
        _replies[address] = reply;
        return this;
    }

    public IReadOnlyList<HttpRequestMessage> Requests => [.. _requests];

    public IReadOnlyList<string> Addresses => [.. _requests.Select(r => r.RequestUri!.Host + r.RequestUri.AbsolutePath)];

    public int Count(string host) => _requests.Count(r => string.Equals(r.RequestUri!.Host, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>A decoded query parameter of the <paramref name="index"/>th request.</summary>
    public string? Query(int index, string name) => QueryOf(Requests[index].RequestUri!, name);

    public static string? QueryOf(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) == name)
            {
                return pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "";
            }
        }
        return null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        var key = request.RequestUri!.Host + request.RequestUri.AbsolutePath;
        var reply = Responder?.Invoke(request) ?? (_replies.TryGetValue(key, out var found) ? found : Fallback);
        if (reply.Fail)
        {
            throw new HttpRequestException("connection failed");
        }
        if (reply.Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        var response = new HttpResponseMessage((HttpStatusCode)reply.Status) { Content = new ByteArrayContent(reply.Body), RequestMessage = request };
        foreach (var (name, value) in reply.Headers ?? new Dictionary<string, string>())
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    /// <summary>A client over this handler with politeness spacing off and the network said to be up.</summary>
    public LyricHttpClient Client(Func<bool>? networkUp = null, TimeProvider? time = null) =>
        new(new HttpClient(this) { Timeout = Timeout.InfiniteTimeSpan }, new LyricHttpOptions
        {
            MinimumHostInterval = TimeSpan.Zero,
            IsNetworkAvailable = networkUp ?? (() => true),
            TimeProvider = time ?? TimeProvider.System,
        });
}

/// <summary>Reads files under Fixtures/LyricSources, copied beside the test assembly.</summary>
internal static class Fixtures
{
    public static string PathOf(string relativePath) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "LyricSources", relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static byte[] Bytes(string relativePath) => File.ReadAllBytes(PathOf(relativePath));

    public static string Text(string relativePath) => File.ReadAllText(PathOf(relativePath), Encoding.UTF8);
}

/// <summary>A fresh folder under the temp directory, deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lunalyr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Write(string name, string text) => System.IO.File.WriteAllText(File(name), text, new UTF8Encoding(false));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

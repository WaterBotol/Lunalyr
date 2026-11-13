using System.IO.Pipes;
using Lunalyr.Core.Extras;

namespace Lunalyr.Platform.Discord;

/// <summary>
/// Dials Discord's IPC on Windows: the named pipes <c>\\.\pipe\{prefix}0</c> … <c>{prefix}9</c>, first answering wins
/// (the Mac dials <c>$TMPDIR/discord-ipc-N</c>). The prefix has no default: only the app names Discord's own
/// (<c>discord-ipc-</c>), so tests can only ever reach a pipe of their own.
/// </summary>
public sealed class DiscordPipeDialer(string prefix, TimeSpan? connectTimeout = null)
{
    /// <summary>How long a listed pipe has to accept the connection.</summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(500);

    public string Prefix { get; } = !string.IsNullOrWhiteSpace(prefix) && prefix.IndexOfAny(['\\', '/']) < 0
        ? prefix
        : throw new ArgumentException("A pipe name prefix, without a path.", nameof(prefix));

    public DiscordDialer Dialer => DialAsync;

    /// <summary>
    /// The pipes there are, read from the pipe namespace's listing (which opens none of them), in index order. Asking
    /// for a pipe that isn't there would wait out the whole connect time-out, ten times over.
    /// </summary>
    public IReadOnlyList<string> Listed()
    {
        var found = new List<string>();
        try
        {
            var names = new HashSet<string>(Directory.EnumerateFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>(),
                StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < 10; index++)
            {
                var name = Prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (names.Contains(name)) found.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The listing failed: nothing is dialled this time; the transport looks again later.
        }
        return found;
    }

    public async Task<Stream?> DialAsync(CancellationToken cancellationToken)
    {
        foreach (var name in Listed())
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(connectTimeout ?? DefaultConnectTimeout);
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) return null;
            }
        }
        return null;
    }
}

// The connection to Discord's local IPC, ported from the Mac's DiscordTransport (DiscordPresence.swift, Lunalyr's own
// lines): connect on demand, remember the last activity and show it again after Discord starts or restarts, look for a
// missing Discord every retry interval, and look again at once when a write finds it gone. The Mac reads a Unix socket
// with poll(); here a stream (Windows' named pipe) is read by one reader per connection, which does the Mac's drain:
// it answers PING, passes on READY and ERROR, and closes on CLOSE or a broken frame. No events are subscribed to
// (they are listen-along's joins, which Windows doesn't have).

using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Lunalyr.Core.Logging;

namespace Lunalyr.Core.Extras;

/// <summary>Opens a stream to Discord's IPC, or returns null when no Discord answers.</summary>
public delegate Task<Stream?> DiscordDialer(CancellationToken cancellationToken);

/// <summary>
/// Discord's IPC connection, on its own serial queue. Never dials anything but what its <see cref="DiscordDialer"/> opens:
/// it has no default, so only the app (which names Discord's pipes) can reach the real Discord.
/// </summary>
public sealed class DiscordTransport : IAsyncDisposable
{
    /// <summary>How long Discord has to answer the handshake.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);

    /// <summary>A Discord that hangs without reading can't block the queue for good.</summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);

    /// <summary>A header without its body this long leaves the stream mid-packet: the connection starts again.</summary>
    public static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(1);

    /// <summary>How often a listening transport looks for a missing Discord (each look waits out the retry interval).</summary>
    public static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(1);

    private readonly DiscordDialer _dial;
    private readonly string _clientId;
    private readonly TimeSpan _retryInterval;
    private readonly TimeSpan _readyTimeout;
    private readonly TimeProvider _time;
    private readonly CategoryLogger _log;
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;
    private readonly CancellationTokenSource _disposing = new();

    // Queue state: touched only by work on the queue.
    private Connection? _connection;
    private long _lastFailure = long.MinValue;
    private byte[]? _lastActivity;
    private bool _retryScheduled;
    private bool _listening;
    private int _generation;
    private int _connections;
    private bool _disposed;

    public DiscordTransport(DiscordDialer dial, string clientId, TimeSpan? retryInterval = null, TimeProvider? timeProvider = null,
        Logger? logger = null, TimeSpan? readyTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(dial);
        _readyTimeout = readyTimeout ?? ReadyTimeout;
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        _dial = dial;
        _clientId = clientId;
        _retryInterval = retryInterval ?? DiscordPresence.ReconnectInterval;
        _time = timeProvider ?? TimeProvider.System;
        _log = (logger ?? Logger.Null).App;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Called on the transport's queue for each event Discord sends, and when the connection goes.</summary>
    public Action<DiscordEvent>? OnEvent { get; set; }

    /// <summary>Connections made since this transport started (handshake answered).</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Keeps Discord looked for while nothing new is sent, so a Discord that starts later shows the presence.</summary>
    public void StartListening() => Enqueue(() =>
    {
        if (_listening) return Task.CompletedTask;
        _listening = true;
        var token = ++_generation;
        ScheduleListen(token, TimeSpan.Zero);
        return Task.CompletedTask;
    });

    public void StopListening() => Enqueue(() =>
    {
        _listening = false;
        _generation++;
        return Task.CompletedTask;
    });

    /// <summary>
    /// Sends a payload. If Discord isn't running, an activity is kept and sent when Discord is found, looked for every
    /// retry interval; if Discord went away since the last write, it is looked for at once. <paramref name="thenDisconnect"/>
    /// closes the connection after the write (the clear sent when the presence is switched off).
    /// </summary>
    public void Send(JsonObject payload, bool thenDisconnect = false)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var data = DiscordPresence.Frame(DiscordPresence.Opcode.Frame, payload);
        var isActivity = payload["cmd"]?.GetValue<string>() == "SET_ACTIVITY";
        var shows = isActivity && payload["args"] is JsonObject args && args["activity"] is not null;
        Enqueue(async () =>
        {
            if (isActivity) _lastActivity = shows && !thenDisconnect ? data : null;
            if (_connection is null)
            {
                if (!RetryDue())
                {
                    ScheduleRetry();
                    return;
                }
                if (!await ConnectAsync(replay: false).ConfigureAwait(false))
                {
                    _lastFailure = _time.GetTimestamp();
                    ScheduleRetry();
                    return;
                }
            }

            if (!await WriteAsync(data).ConfigureAwait(false))
            {
                // Discord quit or restarted since the last write: find it again now.
                Disconnect();
                if (!await ConnectAsync(replay: false).ConfigureAwait(false) || !await WriteAsync(data).ConfigureAwait(false))
                {
                    Disconnect();
                    _lastFailure = _time.GetTimestamp();
                    ScheduleRetry();
                    return;
                }
            }
            if (thenDisconnect) Disconnect();
        });
    }

    /// <summary>Completes once everything queued so far has run (or <paramref name="timeout"/> passed).</summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            done.TrySetResult();
            return Task.CompletedTask;
        });
        try
        {
            await done.Task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Enqueue(() =>
        {
            _listening = false;
            _generation++;
            Disconnect();
            return Task.CompletedTask;
        });
        _queue.Writer.TryComplete();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Warn("Discord: the connection didn't close in time.");
        }
        await _disposing.CancelAsync().ConfigureAwait(false);
        _disposing.Dispose();
    }

    // ---- The queue ----

    private void Enqueue(Func<Task> work) => _queue.Writer.TryWrite(work);

    private async Task RunAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("Discord: a request failed.", ex);
                Disconnect();
            }
        }
    }

    private bool RetryDue() => _lastFailure == long.MinValue || _time.GetElapsedTime(_lastFailure) >= _retryInterval;

    private void After(TimeSpan delay, Func<Task> work)
    {
        if (_disposed) return;
        _ = Task.Delay(delay, _time, _disposing.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Enqueue(work);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ScheduleListen(int token, TimeSpan delay) => After(delay, async () =>
    {
        if (!_listening || token != _generation) return;
        if (_connection is null && RetryDue() && !await ConnectAsync(replay: true).ConfigureAwait(false))
            _lastFailure = _time.GetTimestamp();
        ScheduleListen(token, ReadInterval);
    });

    /// <summary>Looks for Discord again later while there is an activity it hasn't got.</summary>
    private void ScheduleRetry()
    {
        if (_retryScheduled || _lastActivity is null) return;
        _retryScheduled = true;
        After(_retryInterval, async () =>
        {
            _retryScheduled = false;
            if (_connection is not null || _lastActivity is null) return;
            if (!await ConnectAsync(replay: true).ConfigureAwait(false))
            {
                _lastFailure = _time.GetTimestamp();
                ScheduleRetry();
            }
        });
    }

    // ---- The connection ----

    private sealed class Connection(Stream stream)
    {
        public Stream Stream { get; } = stream;
        public CancellationTokenSource Reading { get; } = new();
    }

    /// <summary>Dials, says hello and waits for READY; then, with <paramref name="replay"/>, shows the last activity again.</summary>
    private async Task<bool> ConnectAsync(bool replay)
    {
        Stream? stream;
        try
        {
            stream = await _dial(_disposing.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
        {
            stream = null;
        }
        if (stream is null) return false;

        var connection = new Connection(stream);
        var hello = DiscordPresence.Frame(DiscordPresence.Opcode.Handshake, DiscordPresence.HandshakePayload(_clientId));
        if (await WriteAsync(connection, hello).ConfigureAwait(false)
            && await ReadPacketAsync(connection, _readyTimeout).ConfigureAwait(false) is { } reply
            && reply.Opcode == (uint)DiscordPresence.Opcode.Frame
            && DiscordPresence.EventFromFrame(reply.Body) is DiscordEvent.Ready ready)
        {
            _connection = connection;
            Interlocked.Increment(ref _connections);
            _log.Info("Discord answered the handshake.");
            Raise(ready);
            _ = Task.Run(() => ReadLoopAsync(connection));
            if (replay && _lastActivity is { } last) await WriteAsync(last).ConfigureAwait(false);
            return true;
        }

        Close(connection);
        return false;
    }

    private Task<bool> WriteAsync(byte[] data) =>
        _connection is { } connection ? WriteAsync(connection, data) : Task.FromResult(false);

    private static async Task<bool> WriteAsync(Connection connection, byte[] data)
    {
        using var timeout = new CancellationTokenSource(WriteTimeout);
        try
        {
            await connection.Stream.WriteAsync(data, timeout.Token).ConfigureAwait(false);
            await connection.Stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException
            or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Reads Discord's packets for as long as the connection lasts, so the pipe never fills.</summary>
    private async Task ReadLoopAsync(Connection connection)
    {
        while (!connection.Reading.IsCancellationRequested)
        {
            var packet = await ReadPacketAsync(connection, null).ConfigureAwait(false);
            if (packet is not { } p)
            {
                Enqueue(() =>
                {
                    if (ReferenceEquals(_connection, connection)) Disconnect();
                    return Task.CompletedTask;
                });
                return;
            }

            Enqueue(async () =>
            {
                if (!ReferenceEquals(_connection, connection)) return;
                switch ((DiscordPresence.Opcode)p.Opcode)
                {
                    case DiscordPresence.Opcode.Close:
                        Disconnect();
                        break;
                    case DiscordPresence.Opcode.Frame:
                        if (DiscordPresence.EventFromFrame(p.Body) is { } evt)
                        {
                            if (evt is DiscordEvent.Error error) _log.Info($"Discord refused a request ({error.Code}): {error.Message}");
                            Raise(evt);
                        }
                        break;
                    case DiscordPresence.Opcode.Ping:
                        var pong = new byte[8 + p.Body.Length];
                        BinaryPrimitives.WriteUInt32LittleEndian(pong, (uint)DiscordPresence.Opcode.Pong);
                        BinaryPrimitives.WriteUInt32LittleEndian(pong.AsSpan(4), (uint)p.Body.Length);
                        p.Body.CopyTo(pong, 8);
                        await WriteAsync(connection, pong).ConfigureAwait(false);
                        break;
                }
            });
        }
    }

    /// <summary>
    /// One packet, or null when the connection ended, its framing was lost, or (with a <paramref name="timeout"/>)
    /// nothing came in time.
    /// </summary>
    private static async Task<(uint Opcode, byte[] Body)?> ReadPacketAsync(Connection connection, TimeSpan? timeout)
    {
        var header = new byte[8];
        if (!await ReadExactlyAsync(connection, header, timeout).ConfigureAwait(false)) return null;
        var opcode = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (length >= 1 << 20) return null;
        var body = new byte[length];
        if (length > 0 && !await ReadExactlyAsync(connection, body, BodyTimeout).ConfigureAwait(false)) return null;
        return (opcode, body);
    }

    private static async Task<bool> ReadExactlyAsync(Connection connection, byte[] buffer, TimeSpan? timeout)
    {
        using var limit = timeout is { } t ? new CancellationTokenSource(t) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(connection.Reading.Token, limit?.Token ?? CancellationToken.None);
        var read = 0;
        try
        {
            while (read < buffer.Length)
            {
                var n = await connection.Stream.ReadAsync(buffer.AsMemory(read), linked.Token).ConfigureAwait(false);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private void Disconnect()
    {
        if (_connection is not { } connection) return;
        _connection = null;
        Close(connection);
        _log.Info("Discord connection closed.");
        Raise(new DiscordEvent.Disconnected());
    }

    private static void Close(Connection connection)
    {
        try
        {
            connection.Reading.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            connection.Stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private void Raise(DiscordEvent evt)
    {
        try
        {
            OnEvent?.Invoke(evt);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("Discord: an event handler failed.", ex);
        }
    }
}

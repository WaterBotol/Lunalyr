using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Lunalyr.Tests.Shared;

/// <summary>
/// A stand-in for Discord's IPC: a named pipe of the tests' own (<c>lunalyr-test-{guid}-0</c>, never Discord's
/// <c>discord-ipc-N</c>) that answers the handshake with READY, the way Discord does, and records every packet it is
/// sent. <see cref="Up"/> opens and closes it, as Discord starting and quitting would; <see cref="HangUp"/> drops the
/// current connection, as a Discord restart does.
/// </summary>
internal sealed class FakeDiscordPipe : IAsyncDisposable
{
    private readonly Channel<(uint Opcode, JsonObject Body)> _received = Channel.CreateUnbounded<(uint, JsonObject)>();
    private readonly Lock _gate = new();
    private CancellationTokenSource? _accepting;
    private NamedPipeServerStream? _current;
    private int _connections;

    /// <summary>The prefix to dial: the pipe is <c>{Prefix}0</c>.</summary>
    public string Prefix { get; } = $"lunalyr-test-{Guid.NewGuid():N}-";

    public string UserName { get; init; } = "tester";

    /// <summary>Connections accepted so far.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Whether "Discord" is running: the pipe exists and takes connections.</summary>
    public bool Up
    {
        get
        {
            lock (_gate) return _accepting is not null;
        }
        set
        {
            lock (_gate)
            {
                if (value == (_accepting is not null)) return;
                if (value)
                {
                    _accepting = new CancellationTokenSource();
                    var token = _accepting.Token;
                    _ = Task.Run(() => AcceptAsync(token));
                }
                else
                {
                    _accepting!.Cancel();
                    _accepting = null;
                    _current?.Dispose();
                    _current = null;
                }
            }
        }
    }

    /// <summary>The next packet "Discord" received, within <paramref name="timeout"/> (3 s by default), or null.</summary>
    public async Task<(uint Opcode, JsonObject Body)?> NextAsync(TimeSpan? timeout = null)
    {
        using var limit = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
        try
        {
            return await _received.Reader.ReadAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Every packet received so far that hasn't been read with <see cref="NextAsync"/>.</summary>
    public List<(uint Opcode, JsonObject Body)> Drain()
    {
        var packets = new List<(uint, JsonObject)>();
        while (_received.Reader.TryRead(out var packet)) packets.Add(packet);
        return packets;
    }

    /// <summary>Drops the current connection (Discord restarting); the pipe keeps taking new ones while up.</summary>
    public void HangUp()
    {
        lock (_gate)
        {
            _current?.Dispose();
            _current = null;
        }
    }

    public async Task WaitForConnectionsAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (Connections < count && DateTime.UtcNow < deadline) await Task.Delay(25);
    }

    public ValueTask DisposeAsync()
    {
        Up = false;
        return ValueTask.CompletedTask;
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(Prefix + "0", PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                await server.DisposeAsync();
                return;
            }

            lock (_gate)
            {
                if (token.IsCancellationRequested)
                {
                    server.Dispose();
                    return;
                }
                _current?.Dispose();
                _current = server;
            }
            _ = Task.Run(() => ServeAsync(server));
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server)
    {
        try
        {
            var first = true;
            while (await ReadPacketAsync(server) is { } packet)
            {
                await _received.Writer.WriteAsync(packet);
                if (first && packet.Opcode == 0)
                {
                    first = false;
                    Interlocked.Increment(ref _connections);
                    var ready = new JsonObject
                    {
                        ["cmd"] = "DISPATCH",
                        ["evt"] = "READY",
                        ["data"] = new JsonObject { ["v"] = 1, ["user"] = new JsonObject { ["id"] = "1", ["username"] = UserName } },
                    };
                    await server.WriteAsync(Frame(1, ready));
                    await server.FlushAsync();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static async Task<(uint Opcode, JsonObject Body)?> ReadPacketAsync(Stream stream)
    {
        var header = new byte[8];
        if (!await ReadExactlyAsync(stream, header)) return null;
        var opcode = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var body = new byte[length];
        if (length > 0 && !await ReadExactlyAsync(stream, body)) return null;
        return (opcode, JsonNode.Parse(Encoding.UTF8.GetString(body)) as JsonObject ?? new JsonObject());
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    public static byte[] Frame(uint opcode, JsonObject payload)
    {
        var json = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var data = new byte[8 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), json.Length);
        json.CopyTo(data, 8);
        return data;
    }
}

namespace Lunalyr.Core.Playback;

/// <summary>
/// A one-way switch that turns off, for the whole process, every playback command Lunalyr could send to a player
/// (GSMTC's <c>Try…Async</c>: play, pause, skip, seek, rate, shuffle, repeat) and every sound it could make itself
/// (a music video's audio, later). The app blocks it for <c>--no-playback</c>, the owner's test flag; once blocked it
/// stays blocked.
/// </summary>
/// <remarks>
/// Every command path consults <see cref="TryAllow"/> immediately before it would reach a player, and every audio
/// path consults <see cref="AllowsSound"/>. The app uses <see cref="Process"/>; tests make their own instances so they
/// never change the process-wide one. Thread-safe.
/// </remarks>
public sealed class PlaybackGuard
{
    private string? _reason;
    private int _refused;

    /// <summary>The process-wide guard the app wires into every player adapter.</summary>
    public static PlaybackGuard Process { get; } = new();

    /// <summary>True once <see cref="Block"/> has been called: no playback command or sound is allowed.</summary>
    public bool IsBlocked => Volatile.Read(ref _reason) is not null;

    /// <summary>Why commands are blocked (e.g. "--no-playback"), or null while they are allowed.</summary>
    public string? Reason => Volatile.Read(ref _reason);

    /// <summary>False while blocked: nothing Lunalyr plays itself (video audio) may make a sound.</summary>
    public bool AllowsSound => !IsBlocked;

    /// <summary>How many commands have been refused so far (for diagnostics and tests).</summary>
    public int RefusedCount => Volatile.Read(ref _refused);

    /// <summary>Blocks every playback command and sound from now on. The first reason given is kept.</summary>
    public void Block(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Interlocked.CompareExchange(ref _reason, reason, null);
    }

    /// <summary>
    /// Asks to send <paramref name="command"/> to <paramref name="target"/>. Returns true when it may be sent; false
    /// when commands are blocked, in which case the refusal is counted and logged through <paramref name="log"/>.
    /// </summary>
    public bool TryAllow(string command, string target, Action<string>? log = null)
    {
        var reason = Volatile.Read(ref _reason);
        if (reason is null) return true;
        Interlocked.Increment(ref _refused);
        log?.Invoke($"Refused the {command} command to {(target.Length > 0 ? target : "a player")}: playback commands are off ({reason}).");
        return false;
    }
}

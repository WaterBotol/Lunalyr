using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.NowPlaying;

namespace Lunalyr.Shell.Flyout;

/// <summary>The three transport buttons the flyout offers.</summary>
public enum TransportCommand
{
    PlayPause,
    Previous,
    Next,
}

/// <summary>What became of a transport press.</summary>
public enum TransportOutcome
{
    /// <summary>The player took it.</summary>
    Sent,

    /// <summary>Playback commands are off for this run (<c>--no-playback</c>, <c>--demo</c>): nothing was sent.</summary>
    Refused,

    /// <summary>The player doesn't take this command from other apps: nothing was sent.</summary>
    Unsupported,

    /// <summary>Nothing is playing: nothing was sent.</summary>
    NoPlayer,

    /// <summary>Sent, but the player said no (or went away).</summary>
    Failed,
}

/// <summary>
/// The flyout's transport: play/pause, previous and next for the followed player. Called only from a click on the
/// flyout's own buttons. <see cref="PlaybackGuard"/> is asked first, before the session is even looked at, so with
/// <c>--no-playback</c> (and <c>--demo</c>) nothing ever reaches a player, whatever the session is.
/// </summary>
public sealed class PlaybackCommands
{
    private readonly PlaybackGuard _guard;
    private readonly Action<string>? _log;

    public PlaybackCommands(PlaybackGuard guard, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(guard);
        _guard = guard;
        _log = log;
    }

    /// <param name="session">The followed player, or null.</param>
    /// <param name="capabilities">What it takes from other apps (<see cref="PlayerCapabilities"/>).</param>
    /// <param name="command">The button pressed.</param>
    /// <param name="playing">Whether the music is playing, so play/pause knows which way to go.</param>
    public async Task<TransportOutcome> SendAsync(IMediaSession? session, PlayerCapabilities capabilities, TransportCommand command, bool playing)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var name = Name(command, playing);
        if (!_guard.TryAllow(name, session?.SourceAppUserModelId ?? "", _log)) return TransportOutcome.Refused;
        if (session is null) return TransportOutcome.NoPlayer;
        var control = command switch
        {
            TransportCommand.Previous => PlayerControl.Previous,
            TransportCommand.Next => PlayerControl.Next,
            _ => PlayerControl.PlayPause,
        };
        if (!capabilities[control].IsSupported)
        {
            _log?.Invoke($"Not sending {name} to {session.SourceAppUserModelId}: {capabilities[control].Reason}");
            return TransportOutcome.Unsupported;
        }

        bool sent;
        try
        {
            sent = command switch
            {
                TransportCommand.Previous => await session.TrySkipPreviousAsync().ConfigureAwait(false),
                TransportCommand.Next => await session.TrySkipNextAsync().ConfigureAwait(false),
                _ => await PlayPauseAsync(session, capabilities.Controls, playing).ConfigureAwait(false),
            };
        }
        catch (Exception ex)
        {
            _log?.Invoke($"The {name} command to {session.SourceAppUserModelId} failed: {ex.Message}");
            return TransportOutcome.Failed;
        }

        _log?.Invoke(sent ? $"Sent {name} to {session.SourceAppUserModelId} (a click in the tray panel)."
            : $"{session.SourceAppUserModelId} didn't take the {name} command.");
        return sent ? TransportOutcome.Sent : TransportOutcome.Failed;
    }

    // The direct command when the player offers it, else its toggle.
    private static Task<bool> PlayPauseAsync(IMediaSession session, MediaControls controls, bool playing)
    {
        if (playing) return controls.HasFlag(MediaControls.Pause) ? session.TryPauseAsync() : session.TryTogglePlayPauseAsync();
        return controls.HasFlag(MediaControls.Play) ? session.TryPlayAsync() : session.TryTogglePlayPauseAsync();
    }

    private static string Name(TransportCommand command, bool playing) => command switch
    {
        TransportCommand.Previous => "previous",
        TransportCommand.Next => "next",
        _ => playing ? "pause" : "play",
    };
}

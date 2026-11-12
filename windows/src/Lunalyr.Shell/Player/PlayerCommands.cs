using Lunalyr.Core.Logging;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.NowPlaying;

namespace Lunalyr.Shell.Player;

/// <summary>How a command from the desktop player went.</summary>
public enum CommandOutcome
{
    /// <summary>Sent, and the player took it.</summary>
    Sent,

    /// <summary>Playback commands are off for this run (<c>--no-playback</c>, <c>--demo</c>): nothing was sent.</summary>
    Off,

    /// <summary>Nothing is playing, or this player doesn't take that command from other apps: nothing was sent.</summary>
    Unsupported,

    /// <summary>Sent, but the player didn't take it.</summary>
    Refused,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Note">A calm sentence for the card when it didn't work, or null.</param>
public readonly record struct CommandResult(CommandOutcome Outcome, string? Note)
{
    public bool Ok => Outcome == CommandOutcome.Sent;
}

/// <summary>
/// The desktop player's only way to a player: play/pause, next, previous and seek, each sent once in direct response to
/// a click or key on the card. The playback guard is asked first, before the session is even looked at, so with
/// <c>--no-playback</c> or <c>--demo</c> nothing reaches any player; the session's own commands ask it again.
/// </summary>
public sealed class PlayerCommands
{
    /// <summary>Under the card when the guard is on: the scrub still previews on the wallpaper.</summary>
    public const string OffNote = "Controls are off for this run; scrubbing only previews.";

    private readonly Func<IMediaSession?> _session;
    private readonly Func<PlayerCapabilities> _capabilities;
    private readonly PlaybackGuard _guard;
    private readonly CategoryLogger _log;

    public PlayerCommands(Func<IMediaSession?> session, Func<PlayerCapabilities> capabilities, PlaybackGuard guard, Logger logger)
    {
        _session = session;
        _capabilities = capabilities;
        _guard = guard;
        _log = logger.Player;
    }

    /// <summary>Commands are off for this whole run (the card says so, calmly, and the scrub only previews).</summary>
    public bool AreOff => _guard.IsBlocked;

    public Task<CommandResult> PlayPauseAsync(bool playing) =>
        Send(playing ? "pause" : "play", PlayerControl.PlayPause, s =>
        {
            var controls = _capabilities().Controls;
            if (controls.HasFlag(MediaControls.PlayPauseToggle)) return s.TryTogglePlayPauseAsync();
            return playing ? s.TryPauseAsync() : s.TryPlayAsync();
        });

    public Task<CommandResult> SkipAsync(bool forward) =>
        Send(forward ? "next" : "previous", forward ? PlayerControl.Next : PlayerControl.Previous,
            s => forward ? s.TrySkipNextAsync() : s.TrySkipPreviousAsync());

    /// <summary>Seeks to <paramref name="songSeconds"/> (song time, no lyric offsets).</summary>
    public Task<CommandResult> SeekAsync(double songSeconds) =>
        Send("seek", PlayerControl.Seek, s => s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(Math.Max(0, songSeconds))));

    private async Task<CommandResult> Send(string name, PlayerControl control, Func<IMediaSession, Task<bool>> command)
    {
        var session = _session();
        // Asked first, whatever else is true: with the guard on nothing below runs.
        if (!_guard.TryAllow(name, session?.SourceAppUserModelId ?? "", message => _log.Info(message)))
            return new CommandResult(CommandOutcome.Off, OffNote);
        if (session is null) return new CommandResult(CommandOutcome.Unsupported, "Nothing is playing.");
        var support = _capabilities()[control];
        if (!support.IsSupported) return new CommandResult(CommandOutcome.Unsupported, support.Reason);
        try
        {
            if (await command(session).ConfigureAwait(false))
            {
                _log.Info($"Desktop player: {name} sent to {session.SourceApp.DisplayName}.");
                return new CommandResult(CommandOutcome.Sent, null);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Desktop player: the {name} command failed.", ex);
        }

        var player = string.IsNullOrWhiteSpace(session.SourceApp.DisplayName) ? "The player" : session.SourceApp.DisplayName;
        return new CommandResult(CommandOutcome.Refused, $"{player} didn't take that. Try it in {player} itself.");
    }
}

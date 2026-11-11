namespace Lunalyr.Core.NowPlaying;

/// <summary>The player controls Lunalyr's UI can offer.</summary>
public enum PlayerControl
{
    PlayPause = 0,
    Next,
    Previous,
    Seek,
    PlaybackRate,
    Shuffle,
    Repeat,
}

/// <summary>Whether one control works, and if not, a sentence the UI can show as is.</summary>
public readonly record struct ControlSupport(bool IsSupported, string? Reason)
{
    public static ControlSupport Supported { get; } = new(true, null);

    public static ControlSupport Unsupported(string reason) => new(false, reason);
}

/// <summary>
/// Which controls the current player accepts from other apps, from GSMTC's <c>Is…Enabled</c> flags, with a reason
/// for each one it doesn't so the UI can say so instead of showing a button that does nothing.
/// </summary>
public sealed class PlayerCapabilities
{
    private const int ControlCount = (int)PlayerControl.Repeat + 1;

    private readonly ControlSupport[] _support;

    private PlayerCapabilities(MediaControls controls, MediaSourceApp? app, ControlSupport[] support)
    {
        Controls = controls;
        App = app;
        _support = support;
    }

    /// <summary>No player: every control is unavailable.</summary>
    public static PlayerCapabilities None { get; } = new(
        MediaControls.None, null, Enumerable.Repeat(ControlSupport.Unsupported("Nothing is playing."), ControlCount).ToArray());

    public MediaControls Controls { get; }

    /// <summary>The player these capabilities describe, or null for <see cref="None"/>.</summary>
    public MediaSourceApp? App { get; }

    public bool CanPlayPause => _support[(int)PlayerControl.PlayPause].IsSupported;

    public bool CanSkipNext => _support[(int)PlayerControl.Next].IsSupported;

    public bool CanSkipPrevious => _support[(int)PlayerControl.Previous].IsSupported;

    public bool CanSeek => _support[(int)PlayerControl.Seek].IsSupported;

    public bool CanChangePlaybackRate => _support[(int)PlayerControl.PlaybackRate].IsSupported;

    public bool CanShuffle => _support[(int)PlayerControl.Shuffle].IsSupported;

    public bool CanRepeat => _support[(int)PlayerControl.Repeat].IsSupported;

    public ControlSupport this[PlayerControl control] => _support[(int)control];

    /// <param name="controls">The player's enabled controls.</param>
    /// <param name="status">Play/pause depends on it: while playing it needs Pause (or the toggle), otherwise Play.</param>
    /// <param name="app">The player, for the reason text.</param>
    public static PlayerCapabilities From(MediaControls controls, PlaybackStatus status, MediaSourceApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var name = string.IsNullOrWhiteSpace(app.DisplayName) ? "This player" : app.DisplayName;
        var support = new ControlSupport[ControlCount];

        var toggle = controls.HasFlag(MediaControls.PlayPauseToggle);
        support[(int)PlayerControl.PlayPause] = status == PlaybackStatus.Playing
            ? Check(toggle || controls.HasFlag(MediaControls.Pause), $"{name} doesn't allow pausing from other apps.")
            : Check(toggle || controls.HasFlag(MediaControls.Play), $"{name} doesn't allow starting playback from other apps.");
        support[(int)PlayerControl.Next] = Check(controls.HasFlag(MediaControls.Next), $"{name} doesn't allow skipping to the next track from other apps.");
        support[(int)PlayerControl.Previous] = Check(controls.HasFlag(MediaControls.Previous), $"{name} doesn't allow going back a track from other apps.");
        support[(int)PlayerControl.Seek] = Check(controls.HasFlag(MediaControls.Seek), $"{name} doesn't allow seeking from other apps.");
        support[(int)PlayerControl.PlaybackRate] = Check(controls.HasFlag(MediaControls.PlaybackRate), $"{name} doesn't allow changing the playback speed from other apps.");
        support[(int)PlayerControl.Shuffle] = Check(controls.HasFlag(MediaControls.Shuffle), $"{name} doesn't allow changing shuffle from other apps.");
        support[(int)PlayerControl.Repeat] = Check(controls.HasFlag(MediaControls.Repeat), $"{name} doesn't allow changing repeat from other apps.");
        return new PlayerCapabilities(controls, app, support);
    }

    /// <inheritdoc cref="From(MediaControls, PlaybackStatus, MediaSourceApp)"/>
    public static PlayerCapabilities From(MediaPlaybackInfo info, MediaSourceApp app)
    {
        ArgumentNullException.ThrowIfNull(info);
        return From(info.Controls, info.Status, app);
    }

    private static ControlSupport Check(bool supported, string reason) =>
        supported ? ControlSupport.Supported : ControlSupport.Unsupported(reason);
}

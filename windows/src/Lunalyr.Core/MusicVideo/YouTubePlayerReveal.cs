// Ported from the Mac's MusicVideoView.swift (YouTubeMusicVideoCoordinator.setVisible, reveal, cancelPendingHide and
// cancelPendingReveal; Lunalyr's own code, Test Build 88), apart from WebView2 so it can be tested with hand-fired timers.
// The Mac's frozen frame is not kept on Windows, so a hide never waits for one (HideDelay's hasFrame is always false).

namespace Lunalyr.Core.MusicVideo;

/// <summary>
/// When YouTube's player is uncovered. It shows only while it plays; a playing video that starts buffering stays up for a
/// short grace when filled (<see cref="YouTubeEmbed.HideDelay"/>), and is covered at once when shown whole. Shown whole, a
/// player that starts playing stays covered (behind the video's still) for <see cref="MusicVideoFramings.TitleOverlayHold"/>
/// (<see cref="YouTubeEmbed.RevealDelay"/>), so YouTube's start-of-play title overlay is never seen: after a start, a
/// resume and a seek alike.
/// </summary>
public sealed class YouTubePlayerReveal
{
    private readonly Func<double, Action, Action> _schedule;
    private Action? _pendingHide;
    private Action? _pendingReveal;

    /// <param name="schedule">Runs the work after a delay in seconds (on the caller's thread); returns a cancel.</param>
    public YouTubePlayerReveal(Func<double, Action, Action> schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        _schedule = schedule;
    }

    /// <summary>Fill or whole; whole holds each reveal.</summary>
    public MusicVideoFraming Framing { get; set; }

    /// <summary>Why the video is moving or not, as of the sync loop's last check.</summary>
    public MusicVideoHold Hold { get; set; } = MusicVideoHold.Other;

    /// <summary>The player is uncovered (or uncovering), as last asked.</summary>
    public bool Showing { get; private set; }

    /// <summary>A reveal waits for YouTube's title overlay to fade.</summary>
    public bool RevealPending => _pendingReveal is not null;

    /// <summary>A buffering player's hide waits out the grace.</summary>
    public bool HidePending => _pendingHide is not null;

    /// <summary>Uncovers (true) or covers (false) the player.</summary>
    public Action<bool>? OnShow { get; set; }

    /// <summary>
    /// The player plays (<paramref name="visible"/>) or not. <paramref name="buffering"/>: it is buffering (state 3, or a
    /// sample that says so).
    /// </summary>
    public void Set(bool visible, bool buffering = false)
    {
        if (visible)
        {
            CancelHide();
            var hold = YouTubeEmbed.RevealDelay(Framing, Showing);
            if (hold <= 0)
            {
                Reveal();
                return;
            }
            if (_pendingReveal is not null) return;
            Action? cancel = null;
            var fired = false;
            cancel = _schedule(hold, () =>
            {
                fired = true;
                if (_pendingReveal is null || !ReferenceEquals(_pendingReveal, cancel)) return;
                _pendingReveal = null;
                Reveal();
            });
            // A scheduler that ran the work at once has nothing left to cancel.
            if (!fired) _pendingReveal = cancel;
            return;
        }
        CancelReveal();
        var delay = YouTubeEmbed.HideDelay(buffering, Hold, hasFrame: false, Framing);
        if (delay > 0 && Showing)
        {
            if (_pendingHide is not null) return;
            Action? cancelHide = null;
            var done = false;
            cancelHide = _schedule(delay, () =>
            {
                done = true;
                if (_pendingHide is null || !ReferenceEquals(_pendingHide, cancelHide)) return;
                _pendingHide = null;
                Fade(false);
            });
            if (!done) _pendingHide = cancelHide;
            return;
        }
        CancelHide();
        Fade(false);
    }

    /// <summary>The player closed or another page loads: nothing pending, covered (no call to <see cref="OnShow"/>).</summary>
    public void Reset()
    {
        CancelHide();
        CancelReveal();
        Showing = false;
    }

    private void Reveal()
    {
        CancelReveal();
        Fade(true);
    }

    private void Fade(bool visible)
    {
        Showing = visible;
        OnShow?.Invoke(visible);
    }

    private void CancelHide()
    {
        var cancel = _pendingHide;
        _pendingHide = null;
        cancel?.Invoke();
    }

    private void CancelReveal()
    {
        var cancel = _pendingReveal;
        _pendingReveal = null;
        cancel?.Invoke();
    }
}

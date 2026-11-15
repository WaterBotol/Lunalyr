// The action HUD's placement and motion, ported from the Mac's ActionHUD (ActionHUD.swift, Lunalyr's own lines;
// blame-checked against a99fd65: none) and its timings (MotionTokens.swift's Hold.hud, CA.quick, CA.standard). The Mac
// places it just under the menu bar of the screen with the pointer; Windows has no menu bar, so it sits as far down
// from the top of that display's work area (clear of a taskbar docked at the top), in Windows' y-down pixels.

namespace Lunalyr.Shell.Extras;

/// <summary>A rectangle in screen pixels, y down.</summary>
public readonly record struct HudRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double MidX => X + Width / 2;

    public bool Contains(HudRect other) =>
        other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;
}

public static class ActionHudLayout
{
    /// <summary>Room around the capsule inside the clear window, for its shadow (points).</summary>
    public const double Margin = 10;

    /// <summary>The capsule's height (points); it sizes to its text and is centred.</summary>
    public const double CapsuleHeight = 36;

    /// <summary>The window's size in points; the capsule inside is centred.</summary>
    public const double PanelWidth = 560, PanelHeight = CapsuleHeight + 2 * Margin;

    /// <summary>Gap between the top of the work area (the Mac's menu bar) and the capsule's top (points).</summary>
    public const double GapBelowTop = 6;

    /// <summary>
    /// Kept clear of the display's top edge even when the work area reaches it (points): where the Mac keeps clear of a
    /// hidden menu bar dropping in, Windows keeps clear of an auto-hidden taskbar docked at the top.
    /// </summary>
    public const double TopClearance = 24;

    /// <summary>How far the HUD settles downwards as it appears (points); none under Reduce Motion.</summary>
    public const double Settle = 6;

    /// <summary>
    /// The window's frame, centred on the display, just under the top of its work area. <paramref name="panelWidth"/> and
    /// <paramref name="panelHeight"/> are in pixels; <paramref name="scale"/> turns the points above into pixels.
    /// </summary>
    public static HudRect Frame(double panelWidth, double panelHeight, HudRect monitor, HudRect work, double scale = 1)
    {
        var capsuleTop = Math.Max(work.Top, monitor.Top + TopClearance * scale) + GapBelowTop * scale;
        var width = Math.Min(panelWidth, monitor.Width);
        return new HudRect(Math.Round(monitor.MidX - width / 2), Math.Round(capsuleTop - Margin * scale), width, panelHeight);
    }

    /// <summary>The index of the frame holding <paramref name="x"/>, <paramref name="y"/>, edges included; null when none does.</summary>
    public static int? IndexOf(double x, double y, IReadOnlyList<HudRect> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (x >= f.Left && x <= f.Right && y >= f.Top && y <= f.Bottom) return i;
        }
        return null;
    }
}

/// <summary>
/// The HUD's life: fade in (0.16 s, settling 6 points down unless Reduce Motion is on), hold for 1.4 s, fade out
/// (0.28 s), gone. A second note while one shows replaces the text in place and restarts only the hold; a note while it
/// fades out turns it back from where it is. Times are seconds on any steady clock.
/// </summary>
public sealed class ActionHudMotion
{
    public const double FadeIn = 0.16, Hold = 1.4, FadeOut = 0.28;

    private double _alphaFrom, _alphaTo, _alphaStart, _alphaSeconds;
    private double _settleFrom, _settleStart;
    private double _hideAt = double.PositiveInfinity;

    /// <summary>Fading in or holding (as against fading out or gone).</summary>
    public bool IsShowing { get; private set; }

    /// <summary>On screen at all (fading out included).</summary>
    public bool IsVisible { get; private set; }

    /// <summary>
    /// A note to show at <paramref name="now"/>. True when the window has to be put in place and shown (it was gone),
    /// false when the note changes in place.
    /// </summary>
    public bool Show(double now, bool reduceMotion)
    {
        var appears = !IsVisible;
        if (appears)
        {
            _alphaFrom = 0;
            _settleFrom = reduceMotion ? 0 : -ActionHudLayout.Settle;
            _settleStart = now;
            StartFade(now, 0, 1, FadeIn);
        }
        else if (!IsShowing)
        {
            // Fading out: back in from wherever it is.
            StartFade(now, Alpha(now), 1, FadeIn);
        }
        IsShowing = true;
        IsVisible = true;
        _hideAt = now + Hold;
        return appears;
    }

    /// <summary>Moves on to <paramref name="now"/>: starts the fade out once the hold is over. False once it has gone.</summary>
    public bool Advance(double now)
    {
        if (IsShowing && now >= _hideAt)
        {
            IsShowing = false;
            StartFade(now, Alpha(now), 0, FadeOut);
        }
        if (IsVisible && !IsShowing && now >= _alphaStart + _alphaSeconds) IsVisible = false;
        return IsVisible;
    }

    /// <summary>Opacity at <paramref name="now"/>, 0–1.</summary>
    public double Alpha(double now)
    {
        if (!IsVisible) return 0;
        if (!(_alphaSeconds > 0) || now >= _alphaStart + _alphaSeconds) return _alphaTo;
        var t = Math.Clamp((now - _alphaStart) / _alphaSeconds, 0, 1);
        var eased = _alphaTo > _alphaFrom ? 1 - Math.Pow(1 - t, 3) : t * t * (3 - 2 * t); // ease out in, smooth out
        return _alphaFrom + (_alphaTo - _alphaFrom) * eased;
    }

    /// <summary>How far above its place it is at <paramref name="now"/>, in points (negative: above), settling to 0.</summary>
    public double SettleOffset(double now)
    {
        if (_settleFrom == 0) return 0;
        var t = Math.Clamp((now - _settleStart) / FadeIn, 0, 1);
        return _settleFrom * Math.Pow(1 - t, 3);
    }

    /// <summary>Something moves at <paramref name="now"/> (a fade or the settle): the window needs frames.</summary>
    public bool Animating(double now) =>
        IsVisible && (now < _alphaStart + _alphaSeconds || (_settleFrom != 0 && now < _settleStart + FadeIn));

    /// <summary>When the hold ends (the fade out starts), while showing.</summary>
    public double? HideAt => IsShowing ? _hideAt : null;

    private void StartFade(double now, double from, double to, double seconds)
    {
        _alphaFrom = from;
        _alphaTo = to;
        _alphaStart = now;
        _alphaSeconds = seconds;
    }
}

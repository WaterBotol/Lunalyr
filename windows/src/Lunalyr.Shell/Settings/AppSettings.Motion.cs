// Windows' "Animation effects" and the wallpaper's motion (tester feedback on 0.9.0, 14 Nov 2026). Off in Windows, it is
// the Mac's Reduce Motion: lyrics settle in place, layouts don't animate and the music video shows its still. Testers took
// that for the app being broken, so Lunalyr says why once (MotionNotice) and lets the owner of the PC keep the motion
// anyway. Windows' own setting is only read, never changed.

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    /// <summary>
    /// "Follow Windows' Animation effects" (on by default): with Animation effects off, the wallpaper moves less. Off, the
    /// wallpaper animates whatever Windows says. Windows' own key.
    /// </summary>
    public bool MotionFollowsWindows { get; init; } = true;

    /// <summary>The notice explaining why nothing moves has been seen and answered (shown once).</summary>
    public bool MotionNoticeShown { get; init; }

    /// <summary>Whether the wallpaper should move less, given Windows' Animation effects off (<paramref name="systemReduces"/>).</summary>
    public bool ReducesMotion(bool systemReduces) => systemReduces && MotionFollowsWindows;
}

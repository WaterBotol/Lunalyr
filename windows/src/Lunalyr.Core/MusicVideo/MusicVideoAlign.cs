// Ported from the Mac's MusicVideoAlign.swift (Lunalyr's own code). Lining the music video up by hand: in align mode the
// desktop player's seek row becomes the video's own bar, and dragging it moves only the video while the song plays on.
// The rules live here as plain values; the controller keeps the state and the sync loop does the seeking. Windows keys
// stand in for the Mac's key codes (PlayerKeyMap.KeyCode: the virtual keys), Alt for ⌥ and Ctrl for ⌘.

using System.Globalization;
using Lunalyr.Core.Player;

namespace Lunalyr.Core.MusicVideo;

/// <summary>Whether the video can be lined up by hand just now, and if not, why.</summary>
public readonly record struct MusicVideoAlignAvailability(string? Reason)
{
    public static MusicVideoAlignAvailability Available => new(null);

    public bool IsAvailable => Reason is null;

    public const string NoVideo = "No music video is showing for this song";
    public const string ReduceMotion = "Animation effects are off, so the wallpaper shows a still of the video and it can’t be lined up";
    public const string NotPlaying = "Play the song to line up its video";
    public const string NotResponding = "The video isn’t playing yet";

    public static MusicVideoAlignAvailability Of(bool showing, bool reduceMotion, bool songPlaying, bool responding)
    {
        if (!showing) return new(NoVideo);
        if (reduceMotion) return new(ReduceMotion);
        if (!songPlaying) return new(NotPlaying);
        if (!responding) return new(NotResponding);
        return Available;
    }
}

/// <summary>
/// The video's bar: its whole length, with the song's span laid over it where the offset puts the song inside the video,
/// and a knob for where the video is now. DIPs from the bar's leading edge.
/// </summary>
public static class MusicVideoAlignBar
{
    public readonly record struct Layout(double SpanStart, double SpanEnd, double Knob);

    /// <summary>The knob never goes quite to the video's end: there it holds still.</summary>
    public const double EndGuard = 0.5;

    /// <summary>The video's length as drawn: its player's report, else just long enough for the song at its offset.</summary>
    public static double VideoLength(double? reported, double songDuration, double offset)
    {
        if (reported is { } r && double.IsFinite(r) && r > 0) return r;
        var song = double.IsFinite(songDuration) && songDuration > 0 ? songDuration : 0;
        var ahead = double.IsFinite(offset) ? Math.Max(0, offset) : 0;
        return Math.Max(1, song + ahead);
    }

    public static double X(double time, double length, double width)
    {
        if (!(length > 0) || !(width > 0) || !double.IsFinite(time)) return 0;
        return Math.Clamp(time / length, 0, 1) * width;
    }

    public static Layout LayoutOf(double videoLength, double songDuration, double offset, double songTime, double width)
    {
        offset = double.IsFinite(offset) ? offset : 0;
        var song = double.IsFinite(songDuration) ? Math.Max(0, songDuration) : 0;
        return new Layout(X(offset, videoLength, width), X(offset + song, videoLength, width),
            X((double.IsFinite(songTime) ? songTime : 0) + offset, videoLength, width));
    }

    /// <summary>Seconds of video per DIP of the bar at full speed.</summary>
    public static double SecondsPerPoint(double videoLength, double width) =>
        double.IsFinite(videoLength) && videoLength > 0 && width > 0 ? videoLength / width : 0;

    /// <summary>
    /// The offsets a drag may reach: the knob stays inside the video (from its start to just before its end), and an offset
    /// already outside that is kept, never pulled in by the press.
    /// </summary>
    public static (double Lower, double Upper) Bounds(double videoLength, double songTime, double current)
    {
        var song = double.IsFinite(songTime) ? songTime : 0;
        var lower = Math.Max(MusicVideoTiming.OffsetMin, -song);
        var upper = Math.Min(MusicVideoTiming.OffsetMax, Math.Max(lower, videoLength - EndGuard - song));
        if (double.IsFinite(current))
        {
            lower = Math.Min(lower, current);
            upper = Math.Max(upper, current);
        }
        return (lower, upper);
    }

    /// <summary>"Video +3.4 s".</summary>
    public static string Title(double offset) => "Video " + MusicVideoTiming.Format(offset);

    /// <summary>For Narrator: "The video runs 3.4 s ahead of the song".</summary>
    public static string Spoken(double offset)
    {
        if (!(Math.Abs(offset) >= 0.05)) return "The video runs with the song";
        var amount = Math.Abs(offset).ToString("0.0", CultureInfo.InvariantCulture);
        return offset > 0 ? $"The video runs {amount} s ahead of the song" : $"The video runs {amount} s behind the song";
    }
}

/// <summary>
/// One press on the video's bar, from button-down to release. The video keeps playing under the hand: a drag moves the
/// offset by the pointer's travel (times the fine-scrub gain), so a hand held still keeps the offset. A press away from the
/// knob first brings the knob there.
/// </summary>
public sealed class MusicVideoAlignDrag
{
    /// <summary>A press this close to the knob grabs it where it is.</summary>
    public const double GrabRadius = 10;

    private double _lastX, _lastY;
    private bool _atEnd;

    public MusicVideoAlignDrag(double x, double y, double knobX, double offset, double secondsPerPoint,
        (double Lower, double Upper) bounds, bool fine = false)
    {
        OffsetAtPress = offset;
        SecondsPerPoint = double.IsFinite(secondsPerPoint) ? Math.Max(0, secondsPerPoint) : 0;
        Bounds = bounds;
        Gain = ScrubMath.Gain(y, fine);
        _lastX = double.IsFinite(x) ? x : knobX;
        _lastY = double.IsFinite(y) ? y : 0;
        var start = offset;
        if (double.IsFinite(x) && Math.Abs(x - knobX) > GrabRadius) start = offset + (x - knobX) * SecondsPerPoint;
        Offset = Math.Clamp(start, bounds.Lower, bounds.Upper);
    }

    public double OffsetAtPress { get; }
    public double SecondsPerPoint { get; }
    public (double Lower, double Upper) Bounds { get; }
    public double Offset { get; private set; }
    public double Gain { get; private set; }
    public double Travel { get; private set; }

    /// <summary>The pointer moved. True when the knob has just reached an end of the video.</summary>
    public bool Move(double x, double y, bool fine = false)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        var dx = x - _lastX;
        Travel += Math.Abs(dx) + Math.Abs(y - _lastY);
        Gain = ScrubMath.Gain(y, fine);
        Offset = Math.Clamp(Offset + dx * Gain * SecondsPerPoint, Bounds.Lower, Bounds.Upper);
        _lastX = x;
        _lastY = y;
        var end = Offset <= Bounds.Lower || Offset >= Bounds.Upper;
        var reached = end && !_atEnd && dx != 0;
        _atEnd = end;
        return reached;
    }

    /// <summary>The fine-scrub modifier went down or up without the pointer moving.</summary>
    public void ModifiersChanged(bool fine) => Gain = ScrubMath.Gain(_lastY, fine);

    /// <summary>"Half speed", "Quarter speed", "Fine", or null at full speed.</summary>
    public string? RateLabel => ScrubMath.RateLabel(Gain);

    /// <summary>Whether the release has anything to keep.</summary>
    public bool Changed => Math.Abs(Offset - OffsetAtPress) >= 0.005;
}

/// <summary>
/// Seeks the video while a drag moves it: as soon as the offset has moved, but no more often than its player handles well.
/// A move that comes too soon is sent by a later check, so the last position is always reached. No drift correction
/// meanwhile.
/// </summary>
public sealed class MusicVideoAlignSeeker(MusicVideoCorrectionStyle style, double offset, double lastSeek = double.NegativeInfinity)
{
    /// <summary>An offset change smaller than this is not worth a seek.</summary>
    public const double MinimumChange = 0.03;

    public static double Interval(MusicVideoCorrectionStyle style) => style switch
    {
        MusicVideoCorrectionStyle.Rate => 0.08,
        MusicVideoCorrectionStyle.Seek => 0.2,
        _ => 0.4,
    };

    public MusicVideoCorrectionStyle Style { get; } = style;
    public double SeekedOffset { get; private set; } = offset;
    public double LastSeek { get; private set; } = lastSeek;

    public MusicVideoAction Decide(double offset, double? target, MusicVideoDrift.Sample sample, bool shouldPlay, double now, double lead)
    {
        if (target is not { } goal) return sample.VideoPlaying ? MusicVideoAction.Pause : MusicVideoAction.None;
        if (Math.Abs(offset - SeekedOffset) >= MinimumChange)
        {
            if (now - LastSeek < Interval(Style)) return MusicVideoAction.None;
            SeekedOffset = offset;
            LastSeek = now;
            return MusicVideoAction.Seek(Math.Max(0, goal + (shouldPlay ? lead : 0)), shouldPlay);
        }
        if (shouldPlay) return sample.VideoPlaying || sample.Buffering ? MusicVideoAction.None : MusicVideoAction.Play;
        return sample.VideoPlaying ? MusicVideoAction.Pause : MusicVideoAction.None;
    }
}

/// <summary>What a key does while the video is being lined up (the player card has the keyboard).</summary>
public readonly record struct MusicVideoAlignKey(MusicVideoAlignKey.KeyKind Kind, double Delta = 0)
{
    public enum KeyKind { Nudge, Done, Cancel }

    public const double Step = 0.1;
    public const double BigStep = 1.0;

    public static MusicVideoAlignKey Nudge(double delta) => new(KeyKind.Nudge, delta);
    public static MusicVideoAlignKey Done => new(KeyKind.Done);
    public static MusicVideoAlignKey Cancel => new(KeyKind.Cancel);

    /// <summary>
    /// ← → move the video a tenth of a second (a second with Shift), Enter keeps it, Esc cancels. Anything else is the
    /// card's as usual (Alt+← / Alt+→ step lines, Ctrl+← / Ctrl+→ change songs).
    /// </summary>
    /// <param name="alt">Alt (the Mac's ⌥).</param>
    /// <param name="ctrl">Ctrl or the Windows key (the Mac's ⌘ and Control).</param>
    public static MusicVideoAlignKey? Action(ushort keyCode, bool shift, bool alt, bool ctrl, bool editingText)
    {
        if (keyCode == PlayerKeyMap.KeyCode.Escape) return Cancel;
        if (ctrl || editingText) return null;
        return keyCode switch
        {
            PlayerKeyMap.KeyCode.Left when !alt => Nudge(shift ? -BigStep : -Step),
            PlayerKeyMap.KeyCode.Right when !alt => Nudge(shift ? BigStep : Step),
            PlayerKeyMap.KeyCode.Return => alt ? null : Done,
            _ => null,
        };
    }
}

/// <summary>Align mode, as the controller keeps it: the song and video it is for, and what to put back if it is cancelled.</summary>
public sealed record MusicVideoAlignSession(string SongKey, string Video, MusicVideoOffsets.ManualEntry? Restore);

/// <summary>What the video's bar draws at one moment.</summary>
public sealed record MusicVideoAlignDisplay(double VideoLength, double SongDuration, double Offset, double SongTime,
    MusicVideoOffsetOrigin Origin, bool Dragging = false, string? RateLabel = null, bool Touched = false, bool Available = true)
{
    public double VideoTime => Math.Clamp(SongTime + Offset, 0, VideoLength);

    public MusicVideoAlignBar.Layout Layout(double width) =>
        MusicVideoAlignBar.LayoutOf(VideoLength, SongDuration, Offset, SongTime, width);

    public string Title => MusicVideoAlignBar.Title(Offset);

    /// <summary>The small line under the title: the drag's speed, else a hint before anything is moved, else where the offset came from.</summary>
    public string Detail
    {
        get
        {
            if (Dragging) return RateLabel ?? "Release to keep";
            if (!Touched) return Origin == MusicVideoOffsetOrigin.Manual ? "Set by you · drag to adjust" : "Drag until the picture matches the music";
            return Origin switch
            {
                MusicVideoOffsetOrigin.Manual => "Kept for this video",
                MusicVideoOffsetOrigin.Estimated => "Estimated",
                MusicVideoOffsetOrigin.Captions => "From captions",
                _ => "No offset",
            };
        }
    }

    /// <summary>The hint under the bar (Windows keys).</summary>
    public const string Hint = "Drag down for fine steps · ← → 0.1 s · Shift 1 s";
}

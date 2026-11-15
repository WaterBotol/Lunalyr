// Ported from the Mac's MusicVideoTapSync.swift (Lunalyr's own code). Tap to sync for the video: the user taps when the
// singing starts in the (muted) video, and the offset is worked out from the song's timed lyrics. Windows' virtual keys
// stand in for the Mac's key codes (as TapSyncKeys does for the lyric Tap to sync).

using System.Globalization;

namespace Lunalyr.Core.MusicVideo;

/// <summary>A timed lyric line in the song's own time.</summary>
public readonly record struct MusicVideoLyricLine(double Start, string Text);

public enum MusicVideoTapFailure
{
    /// <summary>The song's timed lyrics have not loaded (or it has none).</summary>
    NoLyrics,
    /// <summary>The song is paused, so there is nothing to time the tap against.</summary>
    NotPlaying,
}

/// <summary>Where the tap-to-sync flow is.</summary>
public abstract record MusicVideoTapState
{
    private MusicVideoTapState()
    {
    }

    public static MusicVideoTapState Idle { get; } = new IdleState();

    public sealed record IdleState : MusicVideoTapState;

    /// <summary>Waiting for the tap. <paramref name="FirstLine"/>: armed before the song's first sung line.</summary>
    public sealed record Armed(bool FirstLine) : MusicVideoTapState;

    /// <summary>The offset now in force (the user's), from <paramref name="Taps"/> taps averaged.</summary>
    public sealed record Done(double Offset, int Taps, bool FirstLine) : MusicVideoTapState;

    public sealed record Failed(MusicVideoTapFailure Failure) : MusicVideoTapState;
}

public static class MusicVideoTapSync
{
    /// <summary>
    /// How late a tap lands after what it marks: reaction time. Longer than the lyric Tap to sync's 0.15 on purpose: this is
    /// one tap reacting to a line seen on screen.
    /// </summary>
    public const double ReactionTime = 0.2;

    /// <summary>The tap is waited for this long before it is given up (seconds).</summary>
    public const double ArmedLimit = 120;

    public static bool ArmedBeforeFirstLine(double songTime, IReadOnlyList<MusicVideoLyricLine> lines) =>
        lines.Count > 0 && songTime < lines.Min(l => l.Start);

    /// <summary>
    /// The line the tap marks. Armed before the first line: the first line. Otherwise the line starting nearest
    /// <paramref name="reference"/>, the next one or the one just passed.
    /// </summary>
    public static MusicVideoLyricLine? Line(IReadOnlyList<MusicVideoLyricLine> lines, double reference, bool firstLine)
    {
        var sorted = lines.OrderBy(l => l.Start).ToList();
        if (sorted.Count == 0) return null;
        var first = sorted[0];
        if (firstLine || reference <= first.Start) return first;
        var passedIndex = sorted.FindLastIndex(l => l.Start <= reference);
        var upcomingIndex = sorted.FindIndex(l => l.Start > reference);
        MusicVideoLyricLine? passed = passedIndex >= 0 ? sorted[passedIndex] : null;
        MusicVideoLyricLine? upcoming = upcomingIndex >= 0 ? sorted[upcomingIndex] : null;
        return (passed, upcoming) switch
        {
            ({ } a, { } b) => b.Start - reference < reference - a.Start ? b : a,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null,
        };
    }

    /// <summary>The offset (video time − song time) one tap gives.</summary>
    public static double Offset(double videoTimeAtTap, double lineStart, double reaction = ReactionTime) =>
        videoTimeAtTap - reaction - lineStart;

    /// <summary>The taps so far, averaged, to a tenth of a second.</summary>
    public static double? Averaged(IReadOnlyList<double> taps) =>
        taps.Count == 0 ? null : Math.Round(taps.Average() * 10, MidpointRounding.AwayFromZero) / 10;

    /// <summary>"Video starts 8.4 s ahead", "Video starts 2.0 s behind".</summary>
    public static string Describe(double offset)
    {
        if (Math.Abs(offset) < 0.05) return "Video starts with the song";
        return $"Video starts {Math.Abs(offset).ToString("0.0", CultureInfo.InvariantCulture)} s {(offset > 0 ? "ahead" : "behind")}";
    }

    public enum KeyAction
    {
        /// <summary>Not for Tap to sync: the key goes where it was going.</summary>
        Pass,
        Tap,
        Cancel,
    }

    public const int Escape = 0x1B;

    /// <summary>Shift, Ctrl, Alt (either side), the Windows keys and Caps Lock.</summary>
    private static readonly HashSet<int> ModifierKeys = [0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0x14];

    /// <summary>
    /// What a key press does while armed. Only plain keys count, and never while a text box has focus: Ctrl, Alt and Windows
    /// key shortcuts, modifier keys alone and repeats pass through. Esc cancels.
    /// </summary>
    /// <param name="shortcut">Ctrl, Alt or a Windows key is held (the Mac's Command, Control or Option).</param>
    public static KeyAction KeyActionFor(int virtualKey, bool shortcut, bool isRepeat, bool textInputFocused)
    {
        if (textInputFocused || isRepeat) return KeyAction.Pass;
        if (shortcut && virtualKey != Escape) return KeyAction.Pass;
        if (ModifierKeys.Contains(virtualKey)) return KeyAction.Pass;
        return virtualKey == Escape ? KeyAction.Cancel : KeyAction.Tap;
    }

    /// <summary>The button's title for each state.</summary>
    public static string ButtonTitle(MusicVideoTapState state) => state switch
    {
        MusicVideoTapState.Armed => "Tap now",
        MusicVideoTapState.Done => "Tap again",
        _ => "Tap to sync",
    };

    /// <summary>The line under the button, if any.</summary>
    public static string? Note(MusicVideoTapState state) => state switch
    {
        MusicVideoTapState.Armed { FirstLine: true } =>
            "Watch the video. Press any key, or Tap now, the moment the singing starts in the video. Esc cancels.",
        MusicVideoTapState.Armed =>
            "Watch the video. Press any key, or Tap now, when the next line is sung in the video. Esc cancels.",
        MusicVideoTapState.Done done =>
            $"{Describe(done.Offset)}, {(done.FirstLine ? "from where the singing starts" : "from the next line sung in the video")}." +
            (done.Taps == 1 ? " Tap again to refine." : $" Averaged over {done.Taps} taps."),
        MusicVideoTapState.Failed { Failure: MusicVideoTapFailure.NoLyrics } =>
            "Tap to sync needs this song’s timed lyrics, and they haven’t loaded.",
        MusicVideoTapState.Failed => "Play the song first: the tap is timed against it.",
        _ => null,
    };
}

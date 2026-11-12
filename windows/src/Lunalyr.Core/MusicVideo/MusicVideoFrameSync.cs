// Windows' own. The Mac plays a library video in AVPlayer, a clock of its own, and keeps it in step with rate nudges and
// seeks (MusicVideoCorrector's Rate style). Windows decodes the file frame by frame (Media Foundation's Source Reader), so
// the frame shown is simply the one the song's clock asks for: song time plus the offset (MusicVideoTiming.VideoTime).
// Nothing drifts; the only decision is when decoding forward is too slow and the reader should seek instead.

namespace Lunalyr.Core.MusicVideo;

public static class MusicVideoFrameSync
{
    /// <summary>A target further than this past the decoded frames is reached by a seek rather than by decoding forward.</summary>
    public const double SeekAhead = 1.5;

    /// <summary>A target this far before the frame shown is a step back (a scrub, a nudge): sought.</summary>
    public const double BackTolerance = 0.1;

    public enum StepKind
    {
        /// <summary>Show the newest decoded frame at or before the target (or keep the one shown).</summary>
        Show,
        /// <summary>Outside the video (before its start, past its end): keep the frame shown, decode nothing more.</summary>
        Hold,
        /// <summary>Drop what is decoded and seek the reader to <see cref="Step.To"/>.</summary>
        Seek,
    }

    public readonly record struct Step(StepKind Kind, double To = 0);

    /// <param name="target">Where the video should be (<see cref="MusicVideoTiming.VideoTime"/>), null outside it.</param>
    /// <param name="shown">The time of the frame on screen, null before the first.</param>
    /// <param name="decodedFrom">The earliest frame decoded and waiting, null when none waits.</param>
    /// <param name="decodedTo">The latest frame decoded (waiting or shown), null before any.</param>
    /// <param name="seekingTo">A seek sent and not landed yet, or null.</param>
    public static Step Decide(double? target, double? shown, double? decodedFrom, double? decodedTo, double? seekingTo)
    {
        if (target is not { } goal) return new Step(StepKind.Hold);
        if (seekingTo is { } pending)
        {
            // A seek in flight lands near its target; another is sent only for a target that moved well away from it.
            return Math.Abs(goal - pending) > SeekAhead ? new Step(StepKind.Seek, goal) : new Step(StepKind.Show);
        }
        var earliest = decodedFrom ?? shown;
        if (earliest is { } first && goal < first - BackTolerance && (shown is not { } s || goal < s - BackTolerance))
            return new Step(StepKind.Seek, goal);
        var latest = decodedTo ?? shown;
        if (latest is { } last ? goal > last + SeekAhead : goal > SeekAhead) return new Step(StepKind.Seek, goal);
        return new Step(StepKind.Show);
    }
}

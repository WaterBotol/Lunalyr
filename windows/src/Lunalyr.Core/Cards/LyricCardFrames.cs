// Which frames a lyric card's MP4 or GIF is made of, and how long each is shown: the scheduling half of the Mac's
// LyricCard.renderFrames (LyricCard.swift, Lunalyr's own code). The renderer draws each frame this hands it; keeping
// the schedule here lets its timing be tested without drawing anything.

namespace Lunalyr.Core.Cards;

/// <summary>How a frame shows its page.</summary>
public enum CardFrameKind
{
    /// <summary>A timed line drawn by its composition at <see cref="CardFrame.SongTime"/>.</summary>
    Timed,

    /// <summary>A line without timing drawn as a still paragraph over a moving background.</summary>
    UntimedOverMovingBackground,

    /// <summary>A line without timing over a still background: one frame for the page.</summary>
    Still,
}

/// <summary>One frame of a card's animation.</summary>
/// <param name="Page">Which page (index into the content's pages).</param>
/// <param name="Kind">How it is drawn.</param>
/// <param name="SongTime">For a timed page, the song time drawn.</param>
/// <param name="CardTime">Seconds into the card: where a moving background is, and the lyric fade's clock.</param>
/// <param name="Delay">How long the frame is shown, in seconds.</param>
/// <param name="LyricOpacity">How much of the lyrics shows (1 except while a held line fades).</param>
public readonly record struct CardFrame(int Page, CardFrameKind Kind, double SongTime, double CardTime, double Delay, double LyricOpacity);

public static class LyricCardFrames
{
    public const double VideoFramesPerSecond = 30;

    /// <summary>MP4s are 720 pixels on their short side (a third smaller than the card): sharp on a phone, small to send.</summary>
    public const double VideoScale = 2.0 / 3.0;

    /// <summary>GIFs are drawn at the card's full size (1080 on the short side).</summary>
    public const double GifScale = 1;

    /// <summary>
    /// Seconds between GIF frames: 25 fps, so the lyrics move as smoothly as a GIF allows (4 cs; browsers slow anything
    /// under 2 cs to 10). The Mac's 7767ef6; before, 12.5 fps capped at 90 or 120 frames.
    /// </summary>
    public const double GifCadence = 0.04;

    /// <summary>A moving background changes every pixel of every frame, so it is drawn at 10 fps to stay in budget.</summary>
    public const double GifMovingBackgroundCadence = 0.1;

    /// <summary>
    /// The most a GIF should weigh: under Discord's 10 MB upload limit with room to spare. Over it, the GIF is made again with
    /// fewer frames per second, then smaller (<see cref="GifCardWriter"/>).
    /// </summary>
    public const int GifByteBudget = 8_000_000;

    /// <summary>The share of a GIF's progress its frames take (encoded as they are drawn); finishing the file takes the rest.</summary>
    public const double GifDrawingShare = 0.85;

    /// <summary>How many frames <see cref="Schedule"/> expects to hand out (for the progress bar).</summary>
    public static int Expected(LyricCardContent content, double minimumCadence, int? budget, ClipPlan? plan)
    {
        var clips = LyricCardTiming.PageClips(content);
        var cadence = Cadence(clips, minimumCadence, budget);
        var total = clips.Where(c => c.FirstInk is not null).Sum(c => c.Duration);
        return Math.Max(1, (int)Math.Ceiling((plan?.Duration ?? total) / cadence) + clips.Count);
    }

    private static double Cadence(IReadOnlyList<PageClip> clips, double minimumCadence, int? budget)
    {
        // Reserve frames for every clip, even very short lines. Share the remaining budget across the timed clips in
        // proportion to duration.
        var total = clips.Where(c => c.FirstInk is not null).Sum(c => c.Duration);
        return budget is { } frames ? Math.Max(minimumCadence, total / Math.Max(1, frames - 3 * clips.Count)) : minimumCadence;
    }

    private static double Hundredths(double seconds) => Math.Round(seconds * 100, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The card's frames, one at a time. <paramref name="budget"/> caps the frame count by stretching the cadence (the
    /// GIF's way); null takes a frame every <paramref name="minimumCadence"/>, whatever the length. With a
    /// <paramref name="plan"/> (the MP4's way) the frames last exactly <see cref="ClipPlan.Duration"/>: the last line is
    /// held (and faded) or the lines are cut.
    /// </summary>
    public static IEnumerable<CardFrame> Schedule(LyricCardContent content, double minimumCadence, int? budget, ClipPlan? plan = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var clips = LyricCardTiming.PageClips(content);
        if (clips.Count == 0) yield break;
        var movingBackground = content.Background?.IsAnimated == true;
        var anyTimed = clips.Any(c => c.FirstInk is not null);
        var cadence = Cadence(clips, minimumCadence, budget);
        var end = plan?.Duration ?? double.PositiveInfinity;
        double Opacity(double time) => plan?.LyricOpacity(time) ?? 1;

        double elapsed = 0;
        for (var clipIndex = 0; clipIndex < clips.Count; clipIndex++)
        {
            var clip = clips[clipIndex];
            if (elapsed >= end - 0.0005) yield break;
            // Under a plan the last page stays up until the end.
            var shown = plan is not null && clipIndex == clips.Count - 1 ? Math.Max(clip.Duration, end - elapsed) : clip.Duration;
            if (clip.Page.Animation is { } animation && clip.FirstInk is { } firstInk && anyTimed)
            {
                var natural = Math.Max(3, (int)Math.Ceiling(clip.Duration / cadence - 1e-9));
                var step = clip.Duration / natural;
                var count = Math.Max(natural, (int)Math.Ceiling(shown / step - 1e-9));
                // Begin after the first word or phrase has started to appear. Keep the final rolling word in view
                // through any trailing rest.
                var lastInk = animation.Rolls
                    ? Math.Max(firstInk, (animation.Words.Count > 0 ? animation.Words.Max(w => w.End) : animation.End) + 0.44)
                    : double.PositiveInfinity;
                var lastTime = clip.Start + (natural - 1) * step;
                for (var index = 0; index < count; index++)
                {
                    var offset = index * step;
                    var time = Math.Min(lastInk, Math.Max(firstInk, Math.Min(lastTime, clip.Start + offset)));
                    // Round successive boundaries so errors do not accumulate.
                    var delay = (Hundredths(offset + step) - Hundredths(offset)) / 100;
                    var at = elapsed + offset;
                    var (frame, more) = Emit(clipIndex, CardFrameKind.Timed, time, at, delay, end, Opacity(at));
                    yield return frame;
                    if (!more) yield break;
                }
            }
            else if (movingBackground)
            {
                // A line with no timing, held over a moving background: its frames follow the background's.
                var count = Math.Max(1, (int)Math.Ceiling(shown / Math.Max(cadence, 0.1) - 1e-9));
                var step = shown / count;
                for (var index = 0; index < count; index++)
                {
                    var offset = index * step;
                    var delay = (Hundredths(offset + step) - Hundredths(offset)) / 100;
                    var at = elapsed + offset;
                    var (frame, more) = Emit(clipIndex, CardFrameKind.UntimedOverMovingBackground, double.NaN, at, delay, end, Opacity(at));
                    yield return frame;
                    if (!more) yield break;
                }
            }
            else
            {
                var (frame, more) = Emit(clipIndex, CardFrameKind.Still, double.NaN, elapsed, shown, end, 1);
                yield return frame;
                if (!more) yield break;
            }
            elapsed += shown;
        }
    }

    /// <summary>A frame cut short at the plan's end, and whether more may follow.</summary>
    private static (CardFrame Frame, bool More) Emit(int page, CardFrameKind kind, double songTime, double at, double delay, double end,
                                                     double opacity)
    {
        delay = Math.Min(delay, end - at);
        var frame = new CardFrame(page, kind, songTime, at, Math.Max(0.02, delay), opacity);
        return (frame, at + delay < end - 0.0005);
    }
}

/// <summary>What an export is doing, for the status under the bar.</summary>
public abstract record ExportPhase
{
    public sealed record Drawing(CardFormat Format) : ExportPhase;

    public sealed record MixingSound : ExportPhase;

    public sealed record EncodingGif : ExportPhase;

    public string Title => this switch
    {
        Drawing { Format: CardFormat.Mp4 } => "Making the MP4…",
        Drawing { Format: CardFormat.Gif } => "Making the GIF…",
        Drawing => "Making the PNG…",
        MixingSound => "Mixing the sound…",
        _ => "Encoding the GIF…",
    };
}

/// <summary>The export's bookkeeping: its progress bar, its files and what a cancelled export leaves.</summary>
public static class LyricCardExportPlan
{
    /// <summary>The share of the MP4's part of the bar its sound mix takes, when it has sound.</summary>
    public const double MixShare = 0.15;

    /// <summary>The name every card file starts from.</summary>
    public const string FileName = "Lunalyr Lyric Card";

    /// <summary>Whether the MP4 will carry any sound.</summary>
    public static bool HasSound(LyricCardContent content) =>
        content.SongAudio is not null || content.SoundFile is not null || content.ExtraSounds.Count > 0
        || (content.BackgroundSoundSource is not null && content.BackgroundVolume > 0);

    /// <summary>
    /// Each format's stretch of the progress bar, in <paramref name="order"/>, sized by roughly how long it takes: the
    /// frames it draws times their size, plus the MP4's sound mix and the GIF's encoding. A long MP4 gets most of the
    /// bar, a PNG a sliver, so the bar moves at an even pace.
    /// </summary>
    public static IReadOnlyDictionary<CardFormat, (double From, double To)> ProgressShares(IReadOnlyList<CardFormat> order, double duration,
                                                                                          int pages, bool hasSound)
    {
        var seconds = double.IsFinite(duration) ? Math.Max(0.5, duration) : 5;
        double Cost(CardFormat format)
        {
            switch (format)
            {
                case CardFormat.Mp4:
                    var frames = seconds * LyricCardFrames.VideoFramesPerSecond + pages;
                    var drawing = frames * LyricCardFrames.VideoScale * LyricCardFrames.VideoScale;
                    return hasSound ? drawing / (1 - MixShare) : drawing;
                case CardFormat.Gif:
                    // Drawn and encoded together, at most GifByteBudget deep.
                    var gifFrames = seconds / LyricCardFrames.GifCadence + pages;
                    return gifFrames * LyricCardFrames.GifScale * LyricCardFrames.GifScale / LyricCardFrames.GifDrawingShare;
                default:
                    // One frame at twice the card's size.
                    return 4;
            }
        }
        var costs = order.Select(Cost).ToList();
        var total = costs.Sum();
        var shares = new Dictionary<CardFormat, (double, double)>();
        if (!(total > 0)) return shares;
        double start = 0;
        for (var i = 0; i < order.Count; i++)
        {
            var finish = i == order.Count - 1 ? 1 : start + costs[i] / total;
            shares[order[i]] = (start, finish);
            start = finish;
        }
        return shares;
    }

    /// <summary><c>name.ext</c> in <paramref name="directory"/>, or <c>name 2.ext</c>, <c>name 3.ext</c>… when that is taken.</summary>
    public static string AvailableFile(string name, string extension, string directory)
    {
        var candidate = Path.Combine(directory, $"{name}.{extension}");
        var number = 2;
        while (File.Exists(candidate) && number < 10_000)
        {
            candidate = Path.Combine(directory, $"{name} {number}.{extension}");
            number++;
        }
        return candidate;
    }

    /// <summary>Removes what a cancelled export made: the whole folder when it is Lunalyr's own temporary one, otherwise only the files it wrote.</summary>
    public static void DiscardExport(IEnumerable<(CardFormat Format, string Path)> files, string directory, bool ownsDirectory)
    {
        try
        {
            if (ownsDirectory)
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return;
            }
            foreach (var (_, file) in files)
                if (File.Exists(file)) File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Recent exports made for copying are kept a week (pasted attachments and drafts still find them), then deleted.</summary>
    public static void PruneExpiredExports(string directory, string keeping, DateTime now)
    {
        if (!Directory.Exists(directory)) return;
        var cutoff = now - TimeSpan.FromDays(7);
        foreach (var export in Directory.EnumerateDirectories(directory))
        {
            if (string.Equals(Path.GetFullPath(export), Path.GetFullPath(keeping), StringComparison.OrdinalIgnoreCase)) continue;
            if (!Guid.TryParse(Path.GetFileName(export), out _)) continue;
            try
            {
                if (Directory.GetLastWriteTimeUtc(export) < cutoff.ToUniversalTime()) Directory.Delete(export, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

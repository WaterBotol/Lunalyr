// How long a lyric card's MP4 lasts: ported from the Mac's LyricCardLength.swift (Lunalyr's own code, no Verso lines).
//
// With a video background longer than two seconds the MP4 can last as long as the video (the default), with the lines
// on their own timing inside it: lines that end early hold their last one (fading it over the last half second when
// more than three seconds are left), and lines that run past the end are cut there, with a note. Or it can last as
// long as the lines, with the background looped (its seam crossfaded) or cut. Pictures, GIFs and very short clips
// always follow the lines. GIF and PNG exports are not affected.

using System.Globalization;

namespace Lunalyr.Core.Cards;

/// <summary>What decides an MP4's length.</summary>
public enum ClipLength { MatchBackground, MatchLyrics }

public static class ClipLengths
{
    public static IReadOnlyList<ClipLength> All { get; } = [ClipLength.MatchBackground, ClipLength.MatchLyrics];

    public static string Title(this ClipLength length) => length == ClipLength.MatchLyrics ? "Match lyrics" : "Match background";
}

/// <summary>
/// Where in a looping background a moment falls. Each pass after the first starts <see cref="Crossfade"/> seconds before
/// the one before it ends, and the two are blended across that overlap.
/// </summary>
public readonly record struct LoopTiming(double Duration, double Crossfade)
{
    public double Period => Math.Max(0.001, Duration - Crossfade);

    /// <summary>The frame to show at <paramref name="time"/>, and, inside a seam, the previous pass's frame with how much of the new one to show (0 to 1).</summary>
    public (double Position, double? Previous, double Weight) Sample(double time)
    {
        if (!(Duration > 0)) return (0, null, 1);
        time = Math.Max(0, time);
        var pass = Math.Floor(time / Period);
        var position = time - pass * Period;
        if (pass >= 1 && Crossfade > 0 && position < Crossfade) return (position, Period + position, position / Crossfade);
        return (Math.Min(position, Duration), null, 1);
    }

    /// <summary>When the seams start (each crossfade runs from here for <see cref="Crossfade"/> seconds).</summary>
    public IReadOnlyList<double> Seams(double end)
    {
        if (!(Crossfade > 0)) return [];
        var seams = new List<double>();
        // Swift's stride(from:to:by:) multiplies the step rather than adding it up.
        for (var i = 1; ; i++)
        {
            var at = Period * i;
            if (at >= end) break;
            seams.Add(at);
        }
        return seams;
    }
}

/// <summary>One page as the card plays it.</summary>
/// <param name="Page">The page.</param>
/// <param name="Start">The song time its frames start from.</param>
/// <param name="FirstInk">When its first word has started to appear; null for an untimed page.</param>
/// <param name="Duration">How long the page is on screen, including <paramref name="Tail"/>.</param>
/// <param name="Tail">The rest after the last page's last word.</param>
public sealed record PageClip(CardPage Page, double Start, double? FirstInk, double Duration, double Tail);

/// <summary>The MP4's length and what happens at its end.</summary>
public sealed record ClipPlan
{
    /// <summary>The rule in force (a short or still background always follows the lines).</summary>
    public ClipLength Length { get; init; }

    /// <summary>Whether the choice applies, so the control is shown.</summary>
    public bool OffersChoice { get; init; }

    /// <summary>How long the MP4 lasts.</summary>
    public double Duration { get; init; }

    /// <summary>How long the selected lines take, with the closing rest.</summary>
    public double LyricsDuration { get; init; }

    /// <summary>Lines that end after the MP4 does.</summary>
    public int LinesCut { get; init; }

    /// <summary>The 60-second cap is what leaves lines out.</summary>
    public bool Capped { get; init; }

    /// <summary>How a moving background repeats; null for a picture.</summary>
    public LoopTiming? BackgroundLoop { get; init; }

    public bool HoldsLastLine => Duration > LyricsDuration + 0.01;

    public bool FadesLastLine => Duration - LyricsDuration > LyricCardTiming.HoldFadeAfter;

    /// <summary>How much of the lyrics shows at <paramref name="time"/> (1 except while a held line fades).</summary>
    public double LyricOpacity(double time)
    {
        if (!FadesLastLine) return 1;
        return Math.Min(1, Math.Max(0, (Duration - time) / LyricCardTiming.LyricFadeOut));
    }

    /// <summary>Said under the control: the length, and what happens to the lines.</summary>
    public string Summary
    {
        get
        {
            if (Note is { } note) return note;
            var length = string.Format(CultureInfo.InvariantCulture, "{0:0.0} s", Duration);
            if (FadesLastLine) return $"{length}. The last line stays up, then fades out.";
            if (HoldsLastLine) return $"{length}. The last line stays up to the end.";
            return length;
        }
    }

    /// <summary>Said under the control when lines don't fit.</summary>
    public string? Note
    {
        get
        {
            if (LinesCut <= 0) return null;
            var lines = LinesCut == 1 ? "1 line won’t fit" : $"{LinesCut} lines won’t fit";
            if (Capped) return $"{lines} in 60 s; pick fewer lines or turn off Max 60 s";
            return $"{lines}; pick fewer lines or Match lyrics";
        }
    }
}

/// <summary>Each page's timing, the MP4's length, and the song windows that follow from them.</summary>
public static class LyricCardTiming
{
    /// <summary>A background this short or shorter is treated like a picture or GIF.</summary>
    public const double ShortBackground = 2.0;

    /// <summary>Held lines fade out only when more than this is left after them.</summary>
    public const double HoldFadeAfter = 3.0;

    public const double LyricFadeOut = 0.5;
    public const double LoopCrossfade = 0.3;
    public const double ShareCap = 60.0;

    /// <summary>Each page's timing; shared by the frames, the song's windows and the length.</summary>
    public static IReadOnlyList<PageClip> PageClips(LyricCardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var movingBackground = content.Background?.IsAnimated == true;
        var clips = new List<PageClip>(content.Pages.Count);
        for (var index = 0; index < content.Pages.Count; index++)
        {
            var page = content.Pages[index];
            var tail = index == content.Pages.Count - 1 ? 0.32 : 0;
            if (page.Animation is { } animation && animation.FirstInk is { } firstInk)
            {
                var start = Math.Min(firstInk, Math.Max(animation.Line.Start, firstInk - 0.12));
                var span = animation.End - start;
                if (double.IsFinite(span) && span > 0)
                {
                    clips.Add(new PageClip(page, start, firstInk, span + tail, tail));
                    continue;
                }
            }
            clips.Add(new PageClip(page, 0, null, content.Pages.Count == 1 ? (movingBackground ? 3.0 : 0.08) : 1.8, 0));
        }
        return clips;
    }

    public static ClipPlan Plan(LyricCardContent content)
    {
        var clips = PageClips(content);
        var lyrics = clips.Sum(clip => clip.Duration);
        var background = content.Background;
        double? video = background?.Kind == CardBackgroundKind.Video ? background.Duration : null;
        var offersChoice = (video ?? 0) > ShortBackground;
        var length = offersChoice ? content.ClipLength : ClipLength.MatchLyrics;
        var natural = length == ClipLength.MatchBackground ? (video ?? lyrics) : lyrics;
        var capped = content.CapsLength && natural > ShareCap;
        var duration = capped ? ShareCap : natural;
        int Cut(double end)
        {
            double start = 0;
            var count = 0;
            foreach (var clip in clips)
            {
                if (start + clip.Duration - clip.Tail > end + 0.05) count++;
                start += clip.Duration;
            }
            return count;
        }
        var linesCut = Cut(duration);
        LoopTiming? loop = null;
        if (background is { IsAnimated: true, Duration: > 0 })
        {
            var loops = duration > background.Duration + 0.01;
            var fade = loops && background.Kind == CardBackgroundKind.Video && background.Duration >= 1 ? LoopCrossfade : 0;
            loop = new LoopTiming(background.Duration, fade);
        }
        return new ClipPlan
        {
            Length = length, OffersChoice = offersChoice, Duration = duration, LyricsDuration = lyrics, LinesCut = linesCut,
            Capped = capped && Cut(natural) < linesCut, BackgroundLoop = loop,
        };
    }
}

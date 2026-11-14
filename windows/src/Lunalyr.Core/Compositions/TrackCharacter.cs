// Windows' own stand-in for the Mac's TrackMood (which is on the never-port list): what "Custom Mix" knows about a
// song, worked out from what Windows has -- the cover's colours, the lyric's pace and evenness, its line lengths -- and
// the choice of layout made from it when the rotation is off. Written from docs/compositions/matching.md, not from
// any Mac code.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>
/// The broad character of a song, stable for the whole track (never per line, so the layout it picks never changes
/// mid-song).
/// </summary>
/// <param name="Energy">0…1: how lively the song reads, from its sung pace, its cover's colourfulness and its line lengths.</param>
/// <param name="Organic">0…1: how played-by-hand it is, from how unevenly its words are paced (an even, machine-like
/// pace is low).</param>
/// <param name="Seed">A stable number for the song (its title and artist), for choices the character leaves open.</param>
public readonly record struct TrackCharacter(double Energy, double Organic, int Seed)
{
    /// <summary>Nothing known: middling energy, half organic.</summary>
    public static TrackCharacter Neutral => new(0.5, 0.5, 0);

    /// <summary>Sung words a second at which the pace counts as slow (0) and as fast (1).</summary>
    public const double SlowPace = 0.8, FastPace = 3.2;

    /// <summary>
    /// The character from the pieces Windows has: <paramref name="wordsPerSecond"/> (null when unknown), the cover's
    /// <paramref name="saturation"/> (0…1, null when there is no cover), the mean line length in characters
    /// (<paramref name="lineLength"/>, null when unknown) and the spread of the word pace
    /// (<paramref name="paceVariation"/>, the coefficient of variation of the gaps between word onsets; null when
    /// the words are not timed).
    /// </summary>
    public static TrackCharacter From(double? wordsPerSecond, double? saturation, double? lineLength, double? paceVariation, int seed)
    {
        var pace = wordsPerSecond is { } wps && double.IsFinite(wps) ? Easing.Smoothstep((wps - SlowPace) / (FastPace - SlowPace)) : 0.5;
        var colour = saturation is { } s && double.IsFinite(s) ? Math.Clamp(s, 0, 1) : 0.5;
        var length = lineLength is { } l && double.IsFinite(l) ? Easing.Smoothstep((l - 12) / 36) : 0.5;
        var energy = Math.Clamp(0.06 + 0.58 * pace + 0.28 * colour + 0.08 * length, 0, 1);
        var organic = paceVariation is { } v && double.IsFinite(v) ? Math.Clamp(v / 0.9, 0, 1) : 0.5;
        return new TrackCharacter(energy, organic, seed);
    }

    /// <summary>
    /// The character of a song from its synced <paramref name="lines"/> (null or empty when there are none), its cover's
    /// colours (gamma-encoded sRGB, e.g. the palette's artwork stops; empty when there is no cover) and its title and
    /// artist.
    /// </summary>
    public static TrackCharacter Of(IReadOnlyList<LyricLine>? lines, IReadOnlyList<Vector3>? cover, string? title, string? artist)
    {
        var seed = SeedOf(title, artist);
        double? saturation = null;
        if (cover is { Count: > 0 })
        {
            // The cover's colourfulness: its mean OKLab chroma, 0.16 or more counting as fully vivid.
            var chroma = cover.Average(colour => Perceptual.ToOkLab(new Double3(colour.X, colour.Y, colour.Z)).Chroma);
            saturation = Math.Clamp(chroma / 0.16, 0, 1);
        }
        double? pace = null, length = null, variation = null;
        var sung = lines?.Where(line => !line.IsInstrumental && line.Credit != true && line.Words.Count > 0).ToList() ?? [];
        if (sung.Count > 0)
        {
            var words = 0;
            var seconds = 0.0;
            var gaps = new List<double>();
            foreach (var line in sung)
            {
                var drawn = line.Words.Where(word => LyricRuns.IsVisible(word.Text)).ToList();
                if (drawn.Count == 0) continue;
                words += drawn.Count;
                seconds += Math.Max(0.3, Math.Max(drawn[^1].End, line.End) - Math.Min(drawn[0].Start, line.Start));
                if (!line.WordTimed) continue;
                for (var i = 1; i < drawn.Count; i++)
                {
                    var gap = drawn[i].Start - drawn[i - 1].Start;
                    if (gap > 0 && gap < 4) gaps.Add(gap);
                }
            }
            if (seconds > 0) pace = words / seconds;
            length = sung.Average(line => (double)TextElements.Count(line.Text.Trim()));
            if (gaps.Count >= 8)
            {
                var mean = gaps.Average();
                var deviation = Math.Sqrt(gaps.Average(gap => (gap - mean) * (gap - mean)));
                variation = mean > 0 ? deviation / mean : null;
            }
        }
        return From(pace, saturation, length, variation, seed);
    }

    /// <summary>A stable seed from the title and artist (FNV-1a, never the process-randomised string hash).</summary>
    public static int SeedOf(string? title, string? artist) =>
        (int)(RollingWords.Fnv1a((title ?? "").Trim().ToLowerInvariant() + "\u001f" + (artist ?? "").Trim().ToLowerInvariant()) & 0x7FFF_FFFF);
}

/// <summary>
/// "Custom Mix" with the rotation off: a layout chosen by the song's character alone, among the layouts this build
/// has. Deliberately coarse, since the layout changes how a whole song reads: dense, machine-even songs take the
/// linked web (Constellation), sparse, played-by-hand ones the echo, and everything between Editorial or Scattered by
/// the song's seed. A layout that is not available gives way to the next that is.
/// </summary>
public static class LayoutMatch
{
    public static string For(TrackCharacter character, IReadOnlyCollection<string> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        string[] wanted = character.Energy > 0.68 && character.Organic < 0.45 ? ["constellation", "spoken"]
            : character.Energy < 0.38 && character.Organic > 0.62 ? ["echo", "scattered"]
            : character.Seed % 2 == 0 ? ["scattered", "constellation"] : ["constellation", "scattered"];
        foreach (var layout in wanted.Concat(["constellation", "spoken"]))
            if (available.Contains(layout)) return layout;
        return available.FirstOrDefault(layout => layout != "automatic") ?? "constellation";
    }
}

/// <summary>
/// "Custom Mix" (the Mac's <c>LyricLayoutRotation</c>, Lunalyr's own, ported): each new song takes the next layout
/// in a rotation the user sets up -- which layouts take part and how many songs in a row each gets (0 leaves one out).
/// Layouts that don't suit a song's energy are skipped for that song. A song keeps its layout for as long as the app
/// runs, so replaying it looks the same. On Windows the rotation steps only through the layouts this build has.
/// </summary>
public sealed class LayoutRotation
{
    public const int MaximumCount = 5;

    /// <summary>The order songs step through: contrasting looks sit next to each other.</summary>
    public static IReadOnlyList<string> Order { get; } =
        ["cutout", "hinge", "constellation", "broadside", "portal", "spoken", "mosaic", "echo", "torque",
         "cascade", "fable", "scattered"];

    private readonly Dictionary<string, string> _assigned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <param name="cursor">Where the rotation got to last time.</param>
    /// <param name="counts">Songs in a row per layout (a layout missing here gets 1).</param>
    public LayoutRotation(int cursor = 0, IReadOnlyDictionary<string, int>? counts = null)
    {
        Cursor = Math.Max(0, cursor);
        SetCounts(counts);
    }

    /// <summary>Where the rotation has got to; saved so the next launch carries on from it.</summary>
    public int Cursor { get; private set; }

    /// <summary>Raised with the new cursor when a song moves the rotation on.</summary>
    public event Action<int>? CursorChanged;

    public int Count(string layout) => _counts.TryGetValue(layout, out var count) ? count : 1;

    public void SetCount(int value, string layout) => _counts[layout] = Math.Clamp(value, 0, MaximumCount);

    /// <summary>Takes in the counts from settings (layouts missing get 1 again).</summary>
    public void SetCounts(IReadOnlyDictionary<string, int>? counts)
    {
        _counts.Clear();
        foreach (var (layout, count) in counts ?? new Dictionary<string, int>()) SetCount(count, layout);
    }

    /// <summary>The layouts in turn, each as many times as its count says, among <paramref name="available"/>.</summary>
    public List<string> Sequence(IReadOnlyCollection<string> available) =>
        [.. Order.Where(available.Contains).SelectMany(layout => Enumerable.Repeat(layout, Count(layout)))];

    /// <summary>Layouts that read well for a song of this energy: slow songs skip the fast, busy ones, and the busiest
    /// songs skip the slowest.</summary>
    public static bool Suits(string layout, double energy) => layout switch
    {
        "cascade" or "constellation" or "constellationEditorial" => energy >= 0.3,
        "echo" => energy <= 0.8,
        _ => true,
    };

    /// <summary>The layout for <paramref name="track"/>: the one it had, or the next in the rotation that suits it.
    /// With every layout left out, the choice by character (<see cref="LayoutMatch"/>).</summary>
    public string Layout(string track, TrackCharacter character, IReadOnlyCollection<string> available)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(available);
        if (_assigned.TryGetValue(track, out var existing) && available.Contains(existing)) return existing;
        var turns = Sequence(available);
        if (turns.Count == 0) return LayoutMatch.For(character, available);
        var at = (Cursor + 1) % turns.Count;
        var pick = turns[at];
        for (var step = 1; step <= turns.Count; step++)
        {
            var index = (Cursor + step) % turns.Count;
            if (Suits(turns[index], character.Energy)) { pick = turns[index]; at = index; break; }
        }
        Cursor = at;
        CursorChanged?.Invoke(Cursor);
        _assigned[track] = pick;
        return pick;
    }
}

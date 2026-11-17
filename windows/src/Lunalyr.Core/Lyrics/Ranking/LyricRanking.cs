using System.Globalization;

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>Accepted candidates in ranking order, and recording mismatches with the reason.</summary>
public sealed record LyricScreening(IReadOnlyList<LyricCandidate> Accepted, IReadOnlyList<LyricRejection> Rejected);

/// <summary>
/// The order matches are ranked in, the recording check, and when a lookup can stop waiting. Ported from the Mac's
/// <c>LyricsFetcher</c> (Lunalyr's own code; the recording check follows the shared <c>LyricRules.c</c>).
/// </summary>
/// <remarks>
/// Timed lyrics rank above plain text. Among timed lyrics, sound timing ranks above timing with a fault (bunched
/// lines, crammed words), and a match for the playing version ranks above one for another version (Live, Remix,
/// English Version…, or lyrics in another language than the song's: <see cref="LyricLanguage"/>).
/// Then word-by-word and mixed timing rank together, first: Apple Music's own lyrics, then LRCLIB's, then the other
/// sources'. Line timing is next, Apple Music first. Beyond those leads, sources follow the configured order, which
/// in Accuracy first comes after agreement and a closer title and artist match. Apple Music's lyrics relayed by
/// Lyrics+ rank as Apple Music's own (<see cref="LyricCandidate.RankingSource"/>), just after the real cache's, and
/// LRCLIB whose timing two other sources agree against ranks after every other source at its timing.
/// </remarks>
public static class LyricRanking
{
    /// <summary>Starts the state of a source that an automatic lookup stopped, or never asked, because its answer could not have changed anything.</summary>
    public const string NotNeeded = "Not needed: ";

    /// <summary>Added to the source-list line of Lyrics+ relaying Apple Music's lyrics (which rank as Apple Music's).</summary>
    public const string AppleRelayNote = " · Apple Music’s lyrics";

    /// <summary>Added to the source-list line of a match in another language than the song's (another version's).</summary>
    public const string LanguageNote = " · lyrics in another language";

    /// <summary>Added to the source-list line of a match found under the song's original title (<see cref="OriginalTitles"/>).</summary>
    public const string OriginalTitleNote = " · found under the original title";

    /// <summary>
    /// Accepted candidates in ranking order (<see cref="Outranks"/>), and recording mismatches with the reason. Every
    /// source, Apple Music included, must pass the recording check. Candidates from sources not enabled are dropped.
    /// </summary>
    public static LyricScreening Screen(IEnumerable<LyricCandidate> candidates, LyricQuery query, LyricSourceSnapshot options)
    {
        var accepted = new List<LyricCandidate>();
        var rejected = new List<LyricRejection>();
        foreach (var candidate in candidates)
        {
            if (!options.IsEnabled(candidate.Source))
            {
                continue;
            }
            if (Rejection(candidate, query) is { } reason)
            {
                rejected.Add(new LyricRejection(candidate, reason));
            }
            else
            {
                accepted.Add(candidate);
            }
        }
        // Lyrics in another language than the song's rank as another version (LyricLanguage).
        accepted = LyricLanguage.Weigh(accepted, query);
        // Only matches for the playing version are compared: a live take has the same words but its own timing.
        var fitting = Enumerable.Range(0, accepted.Count).Where(i => FitsVersion(accepted[i], query)).ToList();
        var weighed = LyricAgreementRules.Weigh([.. fitting.Select(i => accepted[i])]);
        for (var k = 0; k < fitting.Count; k++)
        {
            accepted[fitting[k]] = weighed[k];
        }
        // The leading text, line-timed, with another match's word timing (the Mac's 63662fe).
        accepted = LyricTimingMerge.Merging(accepted, query, options);
        var comparer = new OutranksComparer(query, options);
        return new LyricScreening(
            [.. accepted.Order(comparer)],
            [.. rejected.OrderBy(r => options.OrderOf(r.Candidate.Source))]);
    }

    /// <summary>The accepted candidates in ranking order.</summary>
    public static IReadOnlyList<LyricCandidate> Ranked(IEnumerable<LyricCandidate> candidates, LyricQuery query, LyricSourceSnapshot options) =>
        Screen(candidates, query, options).Accepted;

    /// <summary>The highest-ranked of <paramref name="candidates"/> (<see cref="Outranks"/>) from an enabled source; the first on a tie.</summary>
    public static LyricCandidate? Choose(IEnumerable<LyricCandidate> candidates, LyricQuery query, LyricSourceSnapshot options)
    {
        LyricCandidate? chosen = null;
        foreach (var candidate in candidates)
        {
            if (options.IsEnabled(candidate.Source) && (chosen is null || Outranks(candidate, chosen, query, options)))
            {
                chosen = candidate;
            }
        }
        return chosen;
    }

    /// <summary>Whether <paramref name="a"/> ranks above <paramref name="b"/>.</summary>
    public static bool Outranks(LyricCandidate a, LyricCandidate b, LyricQuery query, LyricSourceSnapshot options)
    {
        var timing = LyricTimingRules.RankedTiming(a.Result);
        var other = LyricTimingRules.RankedTiming(b.Result);
        if (timing > 0 != other > 0)
        {
            return timing > 0;
        }
        // Timing that races through lines ranks below sound timing, ahead of every source's lead: shown, it shoots to the end.
        var sound = !LyricTimingRules.HasTimingFault(a.Result);
        var otherSound = !LyricTimingRules.HasTimingFault(b.Result);
        if (sound != otherSound)
        {
            return sound;
        }
        var fits = FitsVersion(a, query);
        var otherFits = FitsVersion(b, query);
        if (fits != otherFits)
        {
            return fits;
        }
        if (timing != other)
        {
            return timing > other;
        }
        // Mixed timing with few word-timed lines loses to timing that is word by word nearly throughout, whatever the
        // source's lead (the Mac's 46124810).
        if (timing == 2)
        {
            var full = LyricTimingRules.IsMostlyWordTimed(a.Result);
            var otherFull = LyricTimingRules.IsMostlyWordTimed(b.Result);
            if (full != otherFull)
            {
                return full;
            }
        }
        var lead = Lead(a, timing);
        var otherLead = Lead(b, timing);
        if (lead != otherLead)
        {
            return lead < otherLead;
        }
        if (options.Mode == LyricMatchMode.AccuracyFirst)
        {
            // Other services giving the same words: a tie-break, never above the leads.
            var peers = Math.Min(a.Agreement.Peers, 2);
            var otherPeers = Math.Min(b.Agreement.Peers, 2);
            if (peers != otherPeers)
            {
                return peers > otherPeers;
            }
            var closeness = Confidence(a, query);
            var otherCloseness = Confidence(b, query);
            if (closeness != otherCloseness)
            {
                return closeness > otherCloseness;
            }
        }
        return options.OrderOf(a.Source) < options.OrderOf(b.Source);
    }

    /// <summary>
    /// Whether the match names the same version as the playing song. A service that reported no checkable name
    /// repeats the search, so it fits. Lyrics in another language than the song's
    /// (<see cref="LyricCandidate.LanguageMismatch"/>, set by <see cref="Screen"/>) are another version's: a language
    /// version's, whatever its name says.
    /// </summary>
    public static bool FitsVersion(LyricCandidate candidate, LyricQuery query) =>
        !candidate.LanguageMismatch && VersionTags.Mismatch(query, candidate.Title).Count == 0;

    /// <summary>
    /// Where a source stands ahead of the configured order within a timing tier (the same leads as the Mac): 0 Apple
    /// Music's own lyrics and 1 Apple Music's lyrics relayed by Lyrics+, both at any timing; 2 Musixmatch at any timing
    /// (the owner's order, 16 Nov 2026: Apple Music, then Musixmatch, then LRCLIB; the Mac's 1ab6d1c); 3 LRCLIB, only
    /// with word-by-word or mixed timing; 4 every other source.
    /// </summary>
    /// <param name="source">The source that answered (or may still answer).</param>
    /// <param name="rankingAs">The source it ranks as (<see cref="LyricCandidate.RankingSource"/>).</param>
    internal static int Lead(LyricSourceId source, LyricSourceId rankingAs, int timing)
    {
        if (timing >= 1 && rankingAs == LyricSourceId.AppleMusicCache)
        {
            return source == LyricSourceId.AppleMusicCache ? 0 : 1;
        }
        if (timing >= 1 && source == LyricSourceId.Musixmatch) return 2;
        return source == LyricSourceId.Lrclib && timing == 2 ? 3 : 4;
    }

    /// <summary>
    /// The lead for a match. LRCLIB's timing is made by listeners: when two other services agree on the timing and
    /// LRCLIB's disagrees with both (<see cref="LyricAgreement.TimingOutlier"/>), it takes lead 5, after every other
    /// source at its timing tier. It keeps its tier, so its word timing still beats a pair that agree on line timing.
    /// Timing shifted to another recording or master (<see cref="LyricAgreement.Shift"/>) ranks after the other sources at
    /// its timing in the same way (the Mac's 265d728).
    /// </summary>
    internal static int Lead(LyricCandidate candidate, int timing) =>
        (candidate.Source == LyricSourceId.Lrclib && candidate.Agreement.TimingOutlier) || candidate.Agreement.Shift is not null
            ? 5
            : Lead(candidate.Source, candidate.RankingSource, timing);

    /// <summary>The highest confidence: both names exact and, when the playing length is known, the same length.</summary>
    private static double ClosestMatch(LyricQuery query) => query.Duration > 0 ? 2.25 : 2;

    /// <summary>
    /// How closely the names match, and a little more for a recording whose length is nearly the playing one's (never
    /// enough to outweigh a name).
    /// </summary>
    private static double Confidence(LyricCandidate candidate, LyricQuery query)
    {
        var value = query.TitleSimilarity(candidate.Title) + LyricQuery.Similarity(query.Artist, candidate.Artist);
        if (candidate.Duration is { } length && length > 0 && query.Duration > 0)
        {
            value += 0.25 * Math.Max(0, 1 - Math.Abs(length - query.Duration) / 10);
        }
        return value;
    }

    /// <summary>Why a candidate is not the playing recording, or null when it is.</summary>
    public static string? Rejection(LyricCandidate candidate, LyricQuery query)
    {
        var duration = candidate.Duration ?? 0;
        var timed = double.IsFinite(duration) && duration > 0 && query.Duration > 0;
        // A local export whose artist matches is not rejected on its [length:]: the files are written for the playing
        // track. Without an artist, the length is all that confirms the recording.
        var lengthDecides = !(candidate.Source == LyricSourceId.Lyrimuse && query.HasArtist);
        var match = RecordingCheck.Match(query.TitleSimilarity(candidate.Title), query.ArtistScore(candidate.Artist),
            compareArtist: query.HasArtist, lengthRequired: !query.HasArtist, compareLength: lengthDecides,
            query.Duration, duration);
        switch (match)
        {
            case RecordingMatch.TitleMismatch:
                return $"Rejected: title “{candidate.Title}”";
            case RecordingMatch.ArtistMismatch:
                return candidate.Artist.Length == 0 ? "Rejected: no artist given" : $"Rejected: artist “{candidate.Artist}”";
            case RecordingMatch.MissingIdentity:
                return "Rejected: no artist or song length to compare";
            case RecordingMatch.DurationMismatch:
                return $"Rejected: duration {LyricQuery.Clock(duration)} vs {LyricQuery.Clock(query.Duration)}";
        }
        // A service that reports no length is still caught serving a longer edit.
        if (!timed && !candidate.Source.IsLocal() && query.Duration > 0 && candidate.Result is LyricResult.Synced synced
            && synced.Lines.LastOrDefault(line => !line.IsInstrumental) is { } last && last.Start > query.Duration + 3)
        {
            return "Rejected: lyrics run past the end of this recording";
        }
        return null;
    }

    /// <summary>
    /// Whether a search hit names the playing recording, as <see cref="Rejection"/> judges a candidate, before its
    /// lyrics are fetched or chosen. A service offers one match, so a hit that fails the check must not be picked
    /// over one that passes it, however close its name.
    /// </summary>
    public static bool NamesRecording(LyricSourceId source, string title, string artist, double? duration, LyricQuery query)
    {
        var probe = new LyricCandidate(source, new LyricResult.Plain([], new LyricProvenance("", "")), title, artist, duration, 0);
        return Rejection(probe, query) is null;
    }

    /// <summary>The highest-scoring of one service's candidates, preferring those that pass the recording check; the first on a tie.</summary>
    public static LyricCandidate? BestOffer(IEnumerable<LyricCandidate> candidates, LyricQuery query)
    {
        LyricCandidate? best = null;
        var bestPasses = false;
        foreach (var candidate in candidates)
        {
            var passes = Rejection(candidate, query) is null;
            if (best is null || (passes ? 1 : 0, candidate.Score).CompareTo((bestPasses ? 1 : 0, best.Score)) > 0)
            {
                best = candidate;
                bestPasses = passes;
            }
        }
        return best;
    }

    /// <summary>
    /// Whether no pending source could change what <see cref="Choose"/> picks. <paramref name="among"/>: the matches in
    /// hand, whose line timing a pending source's word timing could still upgrade past <paramref name="best"/>
    /// (<see cref="CouldLendTiming"/>).
    /// </summary>
    public static bool IsSettled(LyricCandidate? best, IEnumerable<LyricSourceId> pending, LyricQuery query, LyricSourceSnapshot options,
        IReadOnlyList<LyricCandidate>? among = null)
    {
        var open = pending.Where(options.IsEnabled).ToList();
        if (open.Count == 0)
        {
            return true;
        }
        if (best is null)
        {
            return false;
        }
        return !open.Any(source => CouldBeat(best, source, query, options) || CouldLendTiming(best, source, among ?? [], query));
    }

    /// <summary>
    /// Whether <paramref name="source"/>'s word timing could still be merged into line-timed lyrics in hand
    /// (<see cref="LyricTimingMerge"/>) that would then outrank <paramref name="best"/>: a match from a leading source
    /// that no word timing in hand could be merged into yet (the Mac's 63662fe).
    /// </summary>
    public static bool CouldLendTiming(LyricCandidate best, LyricSourceId source, IEnumerable<LyricCandidate> found, LyricQuery query)
    {
        ArgumentNullException.ThrowIfNull(best);
        ArgumentNullException.ThrowIfNull(found);
        // Below word timing, CouldBeat already keeps every word-capable source.
        if (source.TimingCeiling() < 2 || LyricTimingRules.RankedTiming(best.Result) != 2)
        {
            return false;
        }
        var bestLead = Lead(best, 2);
        return found.Any(c => c.Source != source && LyricTimingMerge.IsMergeRecipient(c, query) && Lead(c, 2) < bestLead);
    }

    /// <summary>
    /// Whether the best result a source could still return would rank above <paramref name="best"/>. Nothing ranks
    /// above Apple Music's own word-by-word or mixed timing.
    /// </summary>
    public static bool CouldBeat(LyricCandidate best, LyricSourceId source, LyricQuery query, LyricSourceSnapshot options)
    {
        if (source == best.Source)
        {
            return false;
        }
        var timing = LyricTimingRules.RankedTiming(best.Result);
        var ceiling = Math.Min(source.TimingCeiling(), 2);
        if (ceiling > 0 != timing > 0)
        {
            return ceiling > 0;
        }
        // Another version's timed lyrics: any timed source could still find the playing one.
        if (timing > 0 && !FitsVersion(best, query))
        {
            return true;
        }
        if (ceiling != timing)
        {
            return ceiling > timing;
        }
        // Agreement is weighed only among the answers in hand; a later agreement against LRCLIB is what CouldDemote
        // keeps sources answering for. A pending Lyrics+ might relay Apple Music's own lyrics (lead 1).
        var lead = Lead(source, LyricSourceRanking.BestRankingSource(source), timing);
        var bestLead = Lead(best, timing);
        if (lead != bestLead)
        {
            return lead < bestLead;
        }
        // Accuracy first: a closer match could still arrive, the same names with a length that fits included.
        if (options.Mode == LyricMatchMode.AccuracyFirst && Confidence(best, query) < ClosestMatch(query))
        {
            return true;
        }
        return options.OrderOf(source) < options.OrderOf(best.Source);
    }

    /// <summary>
    /// Whether the best answer <paramref name="source"/> could give would rank above <paramref name="best"/> on timing,
    /// version or lead alone, before any tie-break (<see cref="CouldBeat"/>; the Mac's 291b50b).
    /// </summary>
    public static bool CouldOutlead(LyricCandidate best, LyricSourceId source, LyricQuery query)
    {
        ArgumentNullException.ThrowIfNull(best);
        if (source == best.Source)
        {
            return false;
        }
        var timing = LyricTimingRules.RankedTiming(best.Result);
        var ceiling = Math.Min(source.TimingCeiling(), 2);
        if (ceiling > 0 != timing > 0)
        {
            return ceiling > 0;
        }
        if (timing > 0 && !FitsVersion(best, query))
        {
            return true;
        }
        if (ceiling != timing)
        {
            return ceiling > timing;
        }
        return Lead(source, LyricSourceRanking.BestRankingSource(source), timing) < Lead(best, timing);
    }

    /// <summary>
    /// Whether a pending source must keep answering because, with another, it could still agree against the timing of
    /// LRCLIB showing and demote it (the Mac's <c>couldDemote</c>; ranking decisions of 12 Nov 2026). All of these hold:
    /// LRCLIB's word or mixed timing for the playing version is the best match, and not already an outlier; it agrees
    /// with no timed match for the same words already in hand; and the pending source can give word timing, or has
    /// some timing while a sound word-timed rival (one that would replace LRCLIB) is already in hand. Line-only and
    /// plain-text sources are otherwise stopped as before.
    /// </summary>
    /// <param name="best">What shows, or is about to.</param>
    /// <param name="source">The pending source.</param>
    /// <param name="accepted">The accepted matches in hand.</param>
    public static bool CouldDemote(LyricCandidate best, LyricSourceId source, IEnumerable<LyricCandidate> accepted, LyricQuery query)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        var found = accepted.ToList();
        // The timing showing might be shifted to another recording (the Mac's 265d728).
        if (CouldShift(best, source, found, query))
        {
            return true;
        }
        // LRCLIB for another version is not weighed for agreement (Screen), so nothing can make it an outlier; any timed
        // source could still find the playing version anyway (CouldBeat). The Mac's couldDemote asks the same (spec §3.4).
        if (best.Source != LyricSourceId.Lrclib || source == best.Source || best.Agreement.TimingOutlier
            || LyricTimingRules.RankedTiming(best.Result) != 2 || !FitsVersion(best, query) || source.TimingCeiling() < 1)
        {
            return false;
        }
        var text = LyricAgreementRules.Text(best);
        var timing = LyricAgreementRules.Timing(best);
        var rival = false;
        foreach (var other in found)
        {
            if (other.Source == best.Source || !FitsVersion(other, query))
            {
                continue;
            }
            var otherTiming = LyricAgreementRules.Timing(other);
            if (otherTiming.Count > 0 && LyricAgreementRules.SameWords(text, LyricAgreementRules.Text(other))
                && LyricAgreementRules.TimingAgrees(timing, otherTiming))
            {
                // LRCLIB can then never be the odd one out.
                return false;
            }
            rival |= LyricTimingRules.RankedTiming(other.Result) == 2 && !LyricTimingRules.HasTimingFault(other.Result);
        }
        return source.TimingCeiling() >= 2 || rival;
    }

    /// <summary>
    /// Whether <paramref name="source"/>'s answer could show the timing on screen to be shifted to another recording
    /// (<see cref="LyricAgreement.Shift"/>), taking away its lead: Apple Music's own lyrics could on their own; another
    /// source only when a match in hand already runs steadily off from it and no other service's timing in hand agrees
    /// with it (the Mac's 265d728).
    /// </summary>
    public static bool CouldShift(LyricCandidate best, LyricSourceId source, IEnumerable<LyricCandidate> accepted, LyricQuery query)
    {
        ArgumentNullException.ThrowIfNull(best);
        ArgumentNullException.ThrowIfNull(accepted);
        if (best.Source == LyricSourceId.AppleMusicCache || best.Agreement.Shift is not null || source == best.Source
            || source.TimingCeiling() < 1 || LyricTimingRules.RankedTiming(best.Result) < 1 || !FitsVersion(best, query))
        {
            return false;
        }
        if (source == LyricSourceId.AppleMusicCache)
        {
            return true;
        }
        var timing = LyricAgreementRules.Timing(best);
        var others = accepted.Where(other => other.RankingSource != best.RankingSource && FitsVersion(other, query))
            .Select(LyricAgreementRules.Timing).Where(other => other.Count > 0).ToList();
        if (others.Any(other => LyricAgreementRules.TimingAgrees(timing, other)))
        {
            return false;
        }
        return others.Any(other => LyricAgreementRules.IsShifted(other, timing) is not null);
    }

    /// <summary>
    /// Whether a pending source's answer could still change what shows, or the online choice that replays use.
    /// <paramref name="best"/> is what shows once any upgrade waiting for a better match has been made.
    /// </summary>
    public static bool Matters(LyricSourceId source, LyricCandidate best, LyricCandidate? onlineBest, LyricQuery query,
        LyricSourceSnapshot options, IReadOnlyList<LyricCandidate>? among = null)
    {
        // Nothing outranks Apple Music's own word-by-word or mixed timing, and a replay reads it again, so no service is needed.
        if (best.Source == LyricSourceId.AppleMusicCache && LyricTimingRules.RankedTiming(best.Result) == 2)
        {
            return false;
        }
        if (CouldBeat(best, source, query, options) || CouldLendTiming(best, source, among ?? [], query))
        {
            return true;
        }
        if (source.IsLocal())
        {
            return false;
        }
        return onlineBest is null || CouldBeat(onlineBest, source, query, options);
    }

    /// <summary>The source list's state lines for what was screened, marking the one <paramref name="shown"/>.</summary>
    public static Dictionary<LyricSourceId, string> Annotate(IReadOnlyDictionary<LyricSourceId, string> states,
        LyricScreening screened, LyricSourceId? shown, LyricQuery query)
    {
        var result = new Dictionary<LyricSourceId, string>(states);
        foreach (var candidate in screened.Accepted)
        {
            var found = states.TryGetValue(candidate.Source, out var before) && before.StartsWith("Saved match", StringComparison.Ordinal)
                ? "Saved match"
                : "Match found";
            // The Mac's order: version or language, original title, Apple relay, timing fault, agreement, showing.
            var other = VersionTags.Mismatch(query, candidate.Title);
            if (other.Count > 0)
            {
                found += $" · other version ({VersionTags.Describe(other)})";
            }
            else if (candidate.LanguageMismatch)
            {
                found += LanguageNote;
            }
            if (candidate.ViaOriginalTitle)
            {
                found += OriginalTitleNote;
            }
            // Lyrics+ relaying Apple Music's lyrics: it ranks as Apple Music, so the list says so.
            if (candidate.RankingSource == LyricSourceId.AppleMusicCache && candidate.Source != LyricSourceId.AppleMusicCache)
            {
                found += AppleRelayNote;
            }
            if (LyricTimingMerge.TimingDonor(candidate) is { } donor)
            {
                found += $" · word timing from {donor}";
            }
            if (LyricTimingRules.TimingFault(candidate.Result) is { } fault)
            {
                found += $" · {fault}";
            }
            if (LyricTimingRules.TimingTier(candidate.Result) >= 2 && LyricSpreadTiming.HasEvenlySpreadWordTiming(candidate.Result))
            {
                found += " · word timing spread evenly over each line (ranked as line timing)";
            }
            if (candidate.Agreement.Shift is { } shift)
            {
                var reference = screened.Accepted.Any(c => c.Source == LyricSourceId.AppleMusicCache) ? "Music’s own" : "the others";
                // Rounded from the exact value, as the Mac's "%.1f" does (1.8499… is "1.8", not "1.9").
                var seconds = Math.Round(Math.Abs(shift), 1, MidpointRounding.ToEven);
                found += string.Create(CultureInfo.InvariantCulture,
                    $" · timed to another recording ({seconds:0.0} s {(shift > 0 ? "later" : "earlier")} than {reference})");
            }
            else if (candidate.Agreement.TimingOutlier)
            {
                found += " · timing differs from other sources";
            }
            else if (candidate.Agreement.Peers > 0)
            {
                found += candidate.Agreement.Peers == 1 ? " · 1 other source agrees" : $" · {candidate.Agreement.Peers} other sources agree";
            }
            result[candidate.Source] = candidate.Source == shown ? found + " · showing" : found;
        }
        foreach (var rejection in screened.Rejected)
        {
            result[rejection.Candidate.Source] = rejection.Reason;
        }
        return result;
    }

    /// <summary>Orders candidates by <see cref="Outranks"/>; used with a stable sort.</summary>
    private sealed class OutranksComparer(LyricQuery query, LyricSourceSnapshot options) : IComparer<LyricCandidate>
    {
        public int Compare(LyricCandidate? x, LyricCandidate? y)
        {
            if (ReferenceEquals(x, y) || x is null || y is null)
            {
                return 0;
            }
            if (Outranks(x, y, query, options))
            {
                return -1;
            }
            return Outranks(y, x, query, options) ? 1 : 0;
        }
    }
}

/// <summary>The outcome of the recording check.</summary>
public enum RecordingMatch
{
    Match = 0,
    TitleMismatch = 1,
    ArtistMismatch = 2,
    MissingIdentity = 3,
    DurationMismatch = 4,
}

/// <summary>
/// Whether a match is the playing recording, from name confidences and lengths. Same rules as the Mac's shared
/// <c>ll_recording_match</c> (Shared/LyricRules, Lunalyr's own code).
/// </summary>
public static class RecordingCheck
{
    /// <summary>Title confidence below this is another song.</summary>
    public const double TitleThreshold = 0.75;

    /// <summary>Artist confidence below this is another artist.</summary>
    public const double ArtistThreshold = 0.65;

    public static RecordingMatch Match(double titleConfidence, double artistConfidence, bool compareArtist, bool lengthRequired,
        bool compareLength, double trackDuration, double candidateDuration)
    {
        if (!double.IsFinite(titleConfidence) || titleConfidence < TitleThreshold)
        {
            return RecordingMatch.TitleMismatch;
        }
        if (compareArtist && (!double.IsFinite(artistConfidence) || artistConfidence < ArtistThreshold))
        {
            return RecordingMatch.ArtistMismatch;
        }
        var hasLength = double.IsFinite(trackDuration) && double.IsFinite(candidateDuration) && trackDuration > 0 && candidateDuration > 0;
        if (lengthRequired && !hasLength)
        {
            return RecordingMatch.MissingIdentity;
        }
        if (compareLength && hasLength && Math.Abs(trackDuration - candidateDuration) > Math.Max(3, trackDuration * 0.02))
        {
            return RecordingMatch.DurationMismatch;
        }
        return RecordingMatch.Match;
    }
}

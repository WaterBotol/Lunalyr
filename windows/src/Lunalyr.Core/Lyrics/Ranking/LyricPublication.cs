namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>What a progressive lookup does with its current best match.</summary>
public enum PublicationStep
{
    /// <summary>Keep what is showing (or nothing yet).</summary>
    Wait,

    /// <summary>A better match is in hand but a pending source could beat it too: wait briefly so one swap does instead of two.</summary>
    HoldForUpgrade,

    /// <summary>Show the best match now.</summary>
    Publish,
}

/// <summary>
/// When a progressive lookup shows lyrics and whether a later arrival may replace them. The first match appears once
/// nothing pending could beat it, or when the grace period ends. After that a match that ranks higher
/// (<see cref="LyricRanking.Outranks"/>) replaces it and a lower one never does, so the lookup ends on the best match
/// found. The match showing is ranked afresh among every answer in hand, agreement included, so a later agreement
/// against LRCLIB's timing demotes it (ranking decision of 12 Nov 2026). The app swaps a replacement in at a line
/// boundary. Ported from the Mac's <c>LyricsFetcher.Publication</c>.
/// </summary>
public sealed class LyricPublication
{
    private readonly LyricSourceSnapshot _options;
    private readonly LyricQuery _query;

    /// <param name="locked">An explicit saved choice is showing; automatic results never replace it.</param>
    public LyricPublication(LyricSourceSnapshot options, LyricQuery query, bool locked = false)
    {
        _options = options;
        _query = query;
        Locked = locked;
        Revision = locked ? 1 : 0;
    }

    public bool Locked { get; }

    /// <summary>The match on screen, if any.</summary>
    public LyricCandidate? Shown { get; private set; }

    /// <summary>Increments each time <see cref="Shown"/> changes.</summary>
    public int Revision { get; private set; }

    /// <param name="best">The best accepted match so far.</param>
    /// <param name="settled">No pending source could change the choice.</param>
    /// <param name="graceOver">The grace period for the first match has ended.</param>
    /// <param name="settleOver">The wait for an upgrade to settle has ended.</param>
    /// <param name="accepted">
    /// Every accepted match so far, weighed together (<see cref="LyricRanking.Screen"/>). The match showing is judged
    /// as it ranks among them now, not as it ranked when it was shown: answers that came later can make two other
    /// sources agree against LRCLIB's timing, and it then loses its lead and is replaced (ranking decision of
    /// 12 Nov 2026). Without it, the match showing is judged as it was shown.
    /// </param>
    public PublicationStep Step(LyricCandidate? best, bool settled, bool graceOver, bool settleOver,
        IReadOnlyList<LyricCandidate>? accepted = null)
    {
        if (Locked || best is null)
        {
            return PublicationStep.Wait;
        }
        if (Shown is not { } current)
        {
            if (!settled && !graceOver)
            {
                return PublicationStep.Wait;
            }
            Show(best);
            return PublicationStep.Publish;
        }
        if (!IsUpgrade(best, Reweighed(current, accepted)))
        {
            return PublicationStep.Wait;
        }
        if (!settled && !settleOver)
        {
            return PublicationStep.HoldForUpgrade;
        }
        Show(best);
        return PublicationStep.Publish;
    }

    /// <summary>Whether <paramref name="next"/> from another source ranks above <paramref name="current"/>.</summary>
    public bool IsUpgrade(LyricCandidate next, LyricCandidate current) =>
        next.Source != current.Source && LyricRanking.Outranks(next, current, _query, _options);

    /// <summary>The match showing as it is weighed among <paramref name="accepted"/> now, or as it was shown.</summary>
    private static LyricCandidate Reweighed(LyricCandidate shown, IReadOnlyList<LyricCandidate>? accepted) =>
        accepted?.FirstOrDefault(c => c.Source == shown.Source && LyricTimingRules.SameResult(c.Result, shown.Result)) ?? shown;

    private void Show(LyricCandidate candidate)
    {
        Shown = candidate;
        Revision++;
    }
}

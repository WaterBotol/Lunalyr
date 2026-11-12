using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Pipeline;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Shell.Lyrics;

/// <summary>The controller's view of the lyric pipeline, so tests can stand in for it.</summary>
public interface ILyricLookup
{
    /// <summary>Why lookups don't run in this process (e.g. <c>--no-network</c>), or null when they do.</summary>
    string? DisabledReason { get; }

    /// <summary>The word distributor the presentation steps use; null for the built-in one.</summary>
    WordDistributor? WordDistributor { get; }

    /// <inheritdoc cref="LyricService.LookupAsync"/>
    Task<LyricLookupReport> LookupAsync(LyricQuery query, LyricSourceSnapshot options,
        Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellationToken);

    /// <summary>
    /// A search (Settings › Timing › Find lyrics): asks every enabled source, ignoring the cache and the miss cache, so
    /// every alternative is listed (<see cref="LyricService.LookupAsync"/> with <c>force</c>). A saved choice still shows.
    /// </summary>
    Task<LyricLookupReport> SearchAsync(LyricQuery query, LyricSourceSnapshot options,
        Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellationToken) =>
        LookupAsync(query, options, onUpdate, cancellationToken);

    /// <summary>Saves the user's choice for this recording (a pin); false when it couldn't be saved.</summary>
    bool Pin(LyricQuery query, LyricResult result) => false;

    /// <summary>Forgets the user's choice for this recording.</summary>
    void Unpin(LyricQuery query)
    {
    }
}

/// <summary>
/// The real pipeline. With a <paramref name="disabledReason"/> (<c>--no-network</c>) the service is still built, but
/// nothing is asked: every lookup returns an empty report and nothing is sent to any lyric service. Pins are kept on
/// this PC either way.
/// </summary>
public sealed class LyricServiceLookup(LyricService service, string? disabledReason = null) : ILyricLookup
{
    public string? DisabledReason { get; } = disabledReason;

    public WordDistributor? WordDistributor => service.WordDistributor;

    public Task<LyricLookupReport> LookupAsync(LyricQuery query, LyricSourceSnapshot options,
        Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellationToken) =>
        DisabledReason is null
            ? service.LookupAsync(query, options, force: false, onUpdate, cancellationToken)
            : Task.FromResult(new LyricLookupReport());

    public Task<LyricLookupReport> SearchAsync(LyricQuery query, LyricSourceSnapshot options,
        Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellationToken) =>
        DisabledReason is null
            ? service.LookupAsync(query, options, force: true, onUpdate, cancellationToken)
            : Task.FromResult(new LyricLookupReport());

    public bool Pin(LyricQuery query, LyricResult result) => service.Pin(query, result);

    public void Unpin(LyricQuery query) => service.Unpin(query);
}

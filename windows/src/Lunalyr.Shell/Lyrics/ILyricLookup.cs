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
}

/// <summary>The real pipeline.</summary>
public sealed class LyricServiceLookup(LyricService service) : ILyricLookup
{
    public string? DisabledReason => null;

    public WordDistributor? WordDistributor => service.WordDistributor;

    public Task<LyricLookupReport> LookupAsync(LyricQuery query, LyricSourceSnapshot options,
        Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellationToken) =>
        service.LookupAsync(query, options, force: false, onUpdate, cancellationToken);
}

/// <summary>No lookups at all: nothing is sent to any lyric service (<c>--no-network</c>).</summary>
public sealed class DisabledLyricLookup(string reason) : ILyricLookup
{
    public string? DisabledReason { get; } = reason;

    public WordDistributor? WordDistributor => null;

    public Task<LyricLookupReport> LookupAsync(LyricQuery query, LyricSourceSnapshot options,
        Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellationToken) =>
        Task.FromResult(new LyricLookupReport());
}

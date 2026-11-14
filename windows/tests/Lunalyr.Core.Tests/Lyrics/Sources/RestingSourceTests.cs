using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

/// <summary>A failing service rests instead of holding every lookup open (the lyric-sourcing evaluation, 15 Nov 2026).</summary>
public class RestingSourceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 15, 2, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Flaky : ILyricSource
    {
        public int Asked;
        public Func<LyricSourceException?> Fail = () => new LyricSourceException(LyricSourceFailure.HttpStatus, "HTTP 500", 500);
        public LyricSourceId Id => LyricSourceId.QQMusic;

        public Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
        {
            Asked++;
            return Fail() is { } error ? Task.FromException<RawLyrics?>(error) : Task.FromResult<RawLyrics?>(null);
        }
    }

    private static readonly LyricQuery Song = new("Song", "Artist", null, 200);

    [Fact]
    public async Task TwoFailuresInARowRestTheSourceAndTheRestGrows()
    {
        var clock = new Clock();
        var inner = new Flaky();
        var source = new RestingSource(inner, clock);
        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(Song, TestContext.Current.CancellationToken));
        Assert.Equal(clock.Now + RestingSource.FirstRest, source.RestingUntil);
        await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(Song, TestContext.Current.CancellationToken));
        Assert.Equal(2, inner.Asked); // resting: not asked
        clock.Now += RestingSource.FirstRest;
        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(Song, TestContext.Current.CancellationToken));
        Assert.Equal(clock.Now + RestingSource.FirstRest * 2, source.RestingUntil); // doubled
    }

    [Fact]
    public async Task AnAnswerEndsTheCountAndClientErrorsDontCount()
    {
        var clock = new Clock();
        var inner = new Flaky();
        var source = new RestingSource(inner, clock);
        await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(Song, TestContext.Current.CancellationToken));
        inner.Fail = () => null; // "not found" is an answer
        Assert.Null(await source.FetchAsync(Song, TestContext.Current.CancellationToken));
        inner.Fail = () => new LyricSourceException(LyricSourceFailure.HttpStatus, "HTTP 500", 500);
        await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(Song, TestContext.Current.CancellationToken));
        Assert.Null(source.RestingUntil);
        inner.Fail = () => new LyricSourceException(LyricSourceFailure.Offline, "offline");
        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(Song, TestContext.Current.CancellationToken));
        Assert.Null(source.RestingUntil); // offline is not the service's fault
    }
}

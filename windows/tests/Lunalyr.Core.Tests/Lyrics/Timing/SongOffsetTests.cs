using System.Security.Cryptography;
using System.Text;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Tests.Lyrics.Timing;

/// <summary>Per-song offsets are tuned to the lyric source on screen.</summary>
public class SongOffsetTests
{
    // Stand-ins for LyricQuery.RecordingKey (another workstream): any distinct strings do.
    private const string Song = "song\u0001artist\u0001studio\u0001200";
    private const string Other = "other\u0001artist\u0001\u0001100";
    private const string Longer = "song\u0001artist\u0001studio\u0001230";

    // Ported from SongOffsetTests.swift: testOffsetsFollowTheLyricSourceAndOlderOffsetsStillApply
    [Fact]
    public void OffsetsFollowTheLyricSourceAndOlderOffsetsStillApply()
    {
        // An in-memory store stands in for the preferences domain, so no real song offsets are touched.
        IReadOnlyDictionary<string, double> saved = new Dictionary<string, double>();
        var store = new SongOffsets(saved, values => saved = values);
        store.Set(1, Song);
        // Offsets saved before sources were told apart are keyed by recording only.
        var legacy = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Song)));
        Assert.Equal(1, saved[legacy]);
        Assert.Equal(1, store.OffsetFor(Song, "LRCLIB"));

        store.ActiveSource = "NetEase";
        store.Set(0.4, Song);
        Assert.Equal(0.4, store.OffsetFor(Song));
        Assert.Equal(1, store.OffsetFor(Song, "LRCLIB"));
        store.Set(0, Song);
        Assert.Equal(0, store.OffsetFor(Song)); // Resetting one source must not bring back the older offset
        var reloaded = new SongOffsets(saved, values => saved = values);
        Assert.Equal(0, reloaded.OffsetFor(Song, "NetEase"));
        Assert.Equal(1, reloaded.OffsetFor(Song, "LRCLIB"));

        store.Set(0.5, Other);
        store.Set(0, Other);
        Assert.Equal(2, saved.Count);

        // The wallpaper asks every frame; a remembered answer is only reused for the same recording and source, and
        // never outlives a change.
        Assert.Equal(1, reloaded.OffsetFor(Song, "LRCLIB"));
        Assert.Equal(0, reloaded.OffsetFor(Longer, "LRCLIB"));
        reloaded.Set(-0.3, Song, "LRCLIB");
        Assert.Equal(-0.3, reloaded.OffsetFor(Song, "LRCLIB"));
        reloaded.ActiveSource = "LRCLIB";
        Assert.Equal(-0.3, reloaded.OffsetFor(Song));
        reloaded.ActiveSource = "NetEase";
        Assert.Equal(0, reloaded.OffsetFor(Song));
    }

    [Fact]
    public void SourceKeysMatchTheMacs()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Song + "|source:LRCLIB")));
        Assert.Equal(expected, SongOffsets.SourceKey(Song, "LRCLIB"));
    }

    [Fact]
    public void OffsetsAreBoundedAndNoSongMeansNoOffset()
    {
        var store = new SongOffsets();
        store.Set(45, Song);
        Assert.Equal(30, store.OffsetFor(Song));
        store.Set(double.NaN, Song);
        Assert.Equal(30, store.OffsetFor(Song));
        Assert.Equal(0, store.OffsetFor(null));
        var loaded = new SongOffsets(new Dictionary<string, double> { [SongOffsets.RecordingHash(Song)] = -99 });
        Assert.Equal(-30, loaded.OffsetFor(Song));
        Assert.Equal(12.25, SongOffsets.LyricTime(12, 0.5, -0.25));
    }
}

// Ported from the Mac app's MusixmatchLyricsProvider.swift (MusixmatchState; Lunalyr's own code).

using System.Text.Json;
using System.Text.Json.Serialization;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Musixmatch's user token and pause, kept in a small file beside the lyric cache (never in settings.json) so a
/// relaunch neither asks for a new token nor forgets a pause; and, in memory, the songs already asked about. Thread-safe.
/// </summary>
public sealed class MusixmatchState
{
    /// <summary>How long a token is used before a new one is asked for (Musixmatch says when one is no longer accepted).</summary>
    public static readonly TimeSpan TokenFreshFor = TimeSpan.FromHours(24);

    /// <summary>The first pause after being turned away; each further one doubles, up to <see cref="LongestPause"/>.</summary>
    public static readonly TimeSpan FirstPause = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan LongestPause = TimeSpan.FromHours(2);

    /// <summary>A pause this long after the last one starts the doubling over.</summary>
    public static readonly TimeSpan ForgetBlocksAfter = TimeSpan.FromHours(24);

    /// <summary>How long a song's answer is reused instead of asking again.</summary>
    public static readonly TimeSpan AnswerLifetime = TimeSpan.FromHours(6);

    /// <summary>What is kept on disk.</summary>
    public sealed record Stored
    {
        public string? Token { get; init; }
        public DateTimeOffset? TokenFetchedAt { get; init; }
        public DateTimeOffset? BlockedUntil { get; init; }
        public DateTimeOffset? LastBlockedAt { get; init; }
        public int Blocks { get; init; }
    }

    private readonly string? _file;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, RawLyrics? Lyrics)> _answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<RawLyrics?>> _inFlight = new(StringComparer.Ordinal);
    private Stored _stored;

    /// <param name="file">Where the token and pause are kept; null keeps them in memory only (tests).</param>
    public MusixmatchState(string? file, TimeProvider? time = null)
    {
        _file = file;
        _time = time ?? TimeProvider.System;
        _stored = Load(file);
    }

    public Stored Snapshot
    {
        get { lock (_gate) return _stored; }
    }

    /// <summary>The key a song's answer and request are kept under.</summary>
    public static string KeyOf(LyricQuery query) =>
        $"{LyricQuery.Normalize(query.Title)}\u0001{LyricQuery.Normalize(query.Artist)}\u0001{Math.Round(query.Duration)}";

    public string? FreshToken()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_stored is not { Token: { } token, TokenFetchedAt: { } at } || now - at >= TokenFreshFor || at > now.AddMinutes(1)) return null;
            return token;
        }
    }

    public void Store(string token) => Update(s => s with { Token = token, TokenFetchedAt = _time.GetUtcNow() });

    public void DropToken(string token) => Update(s => s.Token == token ? s with { Token = null, TokenFetchedAt = null } : s);

    /// <summary>Time left in the current pause, null when not paused.</summary>
    public TimeSpan? PausedFor()
    {
        lock (_gate)
        {
            if (_stored.BlockedUntil is not { } until) return null;
            var left = until - _time.GetUtcNow();
            return left > TimeSpan.Zero ? left : null;
        }
    }

    /// <summary>Starts a pause: 10 minutes, doubling with each block, at most 2 hours. Returns its length.</summary>
    public TimeSpan NoteBlocked()
    {
        var pause = TimeSpan.Zero;
        Update(s =>
        {
            var now = _time.GetUtcNow();
            var blocks = s.LastBlockedAt is { } last && now - last > ForgetBlocksAfter ? 0 : s.Blocks;
            pause = TimeSpan.FromTicks(Math.Min(LongestPause.Ticks, (long)(FirstPause.Ticks * Math.Pow(2, Math.Min(blocks, 10)))));
            return s with { Blocks = blocks + 1, LastBlockedAt = now, BlockedUntil = now + pause };
        });
        return pause;
    }

    /// <summary>Musixmatch answered properly: the doubling starts over.</summary>
    public void ClearBlocked()
    {
        lock (_gate)
        {
            if (_stored.Blocks == 0 && _stored.BlockedUntil is null) return;
        }
        Update(s => s with { Blocks = 0, BlockedUntil = null, LastBlockedAt = null });
    }

    /// <summary>The request in flight for <paramref name="key"/>, joined if there is one, else the one <paramref name="start"/> makes.</summary>
    public Task<RawLyrics?> Request(string key, Func<Task<RawLyrics?>> start)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var running)) return running;
            var task = start();
            // A task that already ran to its end has finished its own bookkeeping; keep it only while it runs.
            if (!task.IsCompleted) _inFlight[key] = task;
            return task;
        }
    }

    public void Finish(string key)
    {
        lock (_gate) _inFlight.Remove(key);
    }

    /// <summary>The answer already given for a song, while it is recent.</summary>
    public bool Answered(string key, out RawLyrics? lyrics)
    {
        lock (_gate)
        {
            if (_answers.TryGetValue(key, out var answer) && _time.GetUtcNow() - answer.At < AnswerLifetime)
            {
                lyrics = answer.Lyrics;
                return true;
            }
            lyrics = null;
            return false;
        }
    }

    public void Remember(string key, RawLyrics? lyrics)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var old in _answers.Where(a => now - a.Value.At >= AnswerLifetime).Select(a => a.Key).ToList()) _answers.Remove(old);
            if (_answers.Count >= 200) _answers.Remove(_answers.MinBy(a => a.Value.At).Key);
            _answers[key] = (now, lyrics);
        }
    }

    private void Update(Func<Stored, Stored> change)
    {
        Stored copy;
        lock (_gate)
        {
            _stored = change(_stored);
            copy = _stored;
        }
        if (_file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temporary = _file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(copy, MusixmatchStateJson.Default.Stored));
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Kept in memory for this run.
        }
    }

    private static Stored Load(string? file)
    {
        if (file is null || !File.Exists(file)) return new Stored();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), MusixmatchStateJson.Default.Stored) ?? new Stored();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Stored();
        }
    }
}

[JsonSerializable(typeof(MusixmatchState.Stored))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class MusixmatchStateJson : JsonSerializerContext;

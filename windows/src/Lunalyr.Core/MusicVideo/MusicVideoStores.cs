// The music video's remembered state, ported from the Mac's MusicVideoResolver.swift (MusicVideoCache, MusicVideoOffsets,
// MusicVideoLinks; Lunalyr's own code). The Mac keeps each in its own UserDefaults key; Windows keeps the same keys
// (without "custom.") as JSON values in one file, {data}\music-video.json (MusicVideoStorage). Still-image verdicts are
// not kept: the check that made them reads undocumented YouTube stills and is not ported.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lunalyr.Core.MusicVideo;

/// <summary>Key-value storage for the music video's state, each value one JSON text (the Mac's UserDefaults keys).</summary>
public interface IMusicVideoStorage
{
    string? Get(string key);

    /// <summary>Stores <paramref name="json"/>, or removes the key when null.</summary>
    void Set(string key, string? json);
}

/// <summary>In memory (tests, and runs that keep nothing).</summary>
public sealed class MemoryMusicVideoStorage : IMusicVideoStorage
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string? Get(string key)
    {
        lock (_gate) return _values.GetValueOrDefault(key);
    }

    public void Set(string key, string? json)
    {
        lock (_gate)
        {
            if (json is null) _values.Remove(key);
            else _values[key] = json;
        }
    }
}

/// <summary>
/// One JSON file holding every key: read once, written whole (through a temporary file) after each change. A file that
/// can't be read starts empty, and is left in place until the next change rewrites it.
/// </summary>
public sealed class FileMusicVideoStorage : IMusicVideoStorage
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _values;

    public FileMusicVideoStorage(string path) => _path = path;

    public string? Get(string key)
    {
        lock (_gate) return Values().GetValueOrDefault(key);
    }

    public void Set(string key, string? json)
    {
        lock (_gate)
        {
            var values = Values();
            if (json is null)
            {
                if (!values.Remove(key)) return;
            }
            else
            {
                values[key] = json;
            }
            Write(values);
        }
    }

    private Dictionary<string, string> Values()
    {
        if (_values is not null) return _values;
        _values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path)) return _values;
            var bytes = File.ReadAllBytes(_path);
            // A file saved by hand may start with a UTF-8 byte-order mark.
            var start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
            using var document = JsonDocument.Parse(bytes.AsMemory(start));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return _values;
            foreach (var property in document.RootElement.EnumerateObject()) _values[property.Name] = property.Value.GetRawText();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Starts empty; nothing here is worth failing for.
        }
        return _values;
    }

    private void Write(Dictionary<string, string> values)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var (key, json) in values.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    writer.WriteRawValue(json, skipInputValidation: false);
                }
                writer.WriteEndObject();
            }
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, buffer.ToArray());
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory for this run.
        }
    }
}

internal sealed class MusicVideoCacheEntry
{
    [JsonPropertyName("at")] public double At { get; set; }
    [JsonPropertyName("video")] public string? Video { get; set; }
    [JsonPropertyName("v")] public int? Version { get; set; }
    [JsonPropertyName("miss")] public string? Miss { get; set; }
}

[JsonSerializable(typeof(Dictionary<string, MusicVideoCacheEntry>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class MusicVideoJson : JsonSerializerContext;

internal static class MusicVideoStorageExtensions
{
    public static Dictionary<string, T> ReadMap<T>(this IMusicVideoStorage storage, string key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<Dictionary<string, T>> type)
    {
        var json = storage.Get(key);
        if (json is null) return new Dictionary<string, T>(StringComparer.Ordinal);
        try
        {
            var map = JsonSerializer.Deserialize(json, type);
            return map is null ? new(StringComparer.Ordinal) : new Dictionary<string, T>(map, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, T>(StringComparer.Ordinal);
        }
    }

    public static void WriteMap<T>(this IMusicVideoStorage storage, string key, Dictionary<string, T> map,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<Dictionary<string, T>> type) =>
        storage.Set(key, JsonSerializer.Serialize(map, type));
}

/// <summary>
/// Online answers per song, so each song is looked up once: found videos for 60 days, "nothing official" for a week
/// (videos do get uploaded). Local files are not cached.
/// </summary>
public sealed class MusicVideoCache
{
    public const string Key = "musicVideoCache";
    public const double FoundLifetime = 60 * 86_400;
    public const double MissingLifetime = 7 * 86_400;
    public const int Capacity = 500;

    /// <summary>
    /// Found videos stored by an older pick are looked up again: the Mac's 2 added the still-image check, 3 the exact
    /// song-title match. Windows starts at the Mac's current version, so a Mac pick would read the same.
    /// </summary>
    public const int Version = 3;

    private readonly IMusicVideoStorage _storage;
    private readonly Dictionary<string, MusicVideoCacheEntry> _entries;
    private readonly Lock _gate = new();

    public MusicVideoCache(IMusicVideoStorage storage)
    {
        _storage = storage;
        _entries = storage.ReadMap(Key, MusicVideoJson.Default.DictionaryStringMusicVideoCacheEntry);
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>The stored answer for a song, unless it has expired.</summary>
    public MusicVideoResolution? Lookup(string songKey, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(songKey, out var entry)) return null;
            var age = now.ToUnixTimeMilliseconds() / 1000.0 - entry.At;
            var id = entry.Video ?? "";
            if (id.Length > 0 && YouTubeVideoId.IsValid(id))
            {
                var checkedPick = (entry.Version ?? 1) >= Version;
                return checkedPick && age < FoundLifetime ? MusicVideoResolution.Of(new MusicVideoSource.YouTube(id)) : null;
            }
            if (age >= MissingLifetime) return null;
            return MusicVideoResolution.Of(entry.Miss == "embed" ? MusicVideoMiss.NotEmbeddable : MusicVideoMiss.NotFound);
        }
    }

    public void Store(MusicVideoResolution resolution, string songKey, DateTimeOffset now)
    {
        if (!resolution.IsCacheable) return;
        lock (_gate)
        {
            var entry = new MusicVideoCacheEntry { At = now.ToUnixTimeMilliseconds() / 1000.0 };
            switch (resolution)
            {
                case MusicVideoResolution.Found { Source: MusicVideoSource.YouTube youTube }:
                    entry.Video = youTube.VideoId;
                    entry.Version = Version;
                    break;
                case MusicVideoResolution.Missing { Miss: MusicVideoMiss.NotEmbeddable }:
                    entry.Miss = "embed";
                    break;
                default:
                    entry.Miss = "none";
                    break;
            }
            _entries[songKey] = entry;
            if (_entries.Count > Capacity)
            {
                foreach (var old in _entries.OrderBy(p => p.Value.At).Take(_entries.Count - Capacity).Select(p => p.Key).ToList())
                    _entries.Remove(old);
            }
            Save();
        }
    }

    public void Remove(string songKey)
    {
        lock (_gate)
        {
            if (_entries.Remove(songKey)) Save();
        }
    }

    public void RemoveAll()
    {
        lock (_gate)
        {
            _entries.Clear();
            _storage.Set(Key, null);
        }
    }

    private void Save() => _storage.WriteMap(Key, _entries, MusicVideoJson.Default.DictionaryStringMusicVideoCacheEntry);
}

/// <summary>Where the offset on screen came from.</summary>
public enum MusicVideoOffsetOrigin
{
    /// <summary>No offset: the video plays from where the song is.</summary>
    None,
    /// <summary>The user's own nudge, tap or drag; always wins.</summary>
    Manual,
    /// <summary>Guessed from how much longer the video is than the song.</summary>
    Estimated,
}

/// <summary>
/// How far into each song's video the song starts, per recording. Two layers, the first present wins: the user's nudge,
/// tap or drag (kept even at 0 once set, for the video it was set on), then a guess from the video's length (kept in
/// memory only). Offsets Lunalyr once found by itself are no longer used: the retired key is dropped when it loads.
/// Thread-safe: the renderer reads the offset in force every frame.
/// </summary>
public sealed class MusicVideoOffsets
{
    public const string Key = "musicVideoOffsets";
    public const string RetiredAutomaticKey = "musicVideoAutoOffsets";
    public const string ManualVideoKey = "musicVideoOffsetVideos";

    private readonly IMusicVideoStorage _storage;
    private readonly Dictionary<string, double> _values;
    private readonly Dictionary<string, string> _manualVideos;
    private readonly Dictionary<string, double> _estimates = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public MusicVideoOffsets(IMusicVideoStorage storage)
    {
        _storage = storage;
        _values = storage.ReadMap(Key, MusicVideoJson.Default.DictionaryStringDouble);
        var videos = storage.ReadMap(ManualVideoKey, MusicVideoJson.Default.DictionaryStringString);
        _manualVideos = videos.Where(p => _values.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (storage.Get(RetiredAutomaticKey) is not null) storage.Set(RetiredAutomaticKey, null);
    }

    /// <summary>Raised after the user's offsets change (any thread).</summary>
    public event Action? Changed;

    /// <summary>The user's offsets, by song key.</summary>
    public IReadOnlyDictionary<string, double> Values
    {
        get { lock (_gate) return new Dictionary<string, double>(_values, StringComparer.Ordinal); }
    }

    /// <summary>The video each of the user's offsets was set on, by song key.</summary>
    public IReadOnlyDictionary<string, string> ManualVideos
    {
        get { lock (_gate) return new Dictionary<string, string>(_manualVideos, StringComparer.Ordinal); }
    }

    /// <summary>The cache key for a video: its YouTube ID or its file.</summary>
    public static string? VideoKey(MusicVideoSource? source) => source switch
    {
        MusicVideoSource.YouTube y => "yt:" + y.VideoId,
        MusicVideoSource.Local l => "file:" + l.Path,
        _ => null,
    };

    /// <summary>The offset in force for the song and video.</summary>
    public double Offset(string? songKey, string? video = null) => Resolved(songKey, video).Offset;

    public MusicVideoOffsetOrigin Origin(string? songKey, string? video = null) => Resolved(songKey, video).Origin;

    private (double Offset, MusicVideoOffsetOrigin Origin) Resolved(string? songKey, string? video)
    {
        if (songKey is null) return (0, MusicVideoOffsetOrigin.None);
        lock (_gate)
        {
            if (_values.TryGetValue(songKey, out var manual) && ManualApplies(songKey, video))
                return (MusicVideoTiming.ClampedOffset(manual), MusicVideoOffsetOrigin.Manual);
            if (video is null) return (0, MusicVideoOffsetOrigin.None);
            if (_estimates.TryGetValue(EstimateKey(songKey, video), out var estimate)) return (estimate, MusicVideoOffsetOrigin.Estimated);
            return (0, MusicVideoOffsetOrigin.None);
        }
    }

    /// <summary>The user set the offset for the song (and this video, when given).</summary>
    public bool HasManual(string? songKey, string? video = null)
    {
        if (songKey is null) return false;
        lock (_gate) return _values.ContainsKey(songKey) && ManualApplies(songKey, video);
    }

    /// <summary>
    /// A tap or nudge is kept for the video it was made on: another upload of the song has its own intro and does not
    /// inherit it. One set before videos were told apart applies to any.
    /// </summary>
    private bool ManualApplies(string songKey, string? video) =>
        video is null || !_manualVideos.TryGetValue(songKey, out var owner) || owner == video;

    /// <summary>
    /// The user's offset for the song, kept even at 0, for <paramref name="video"/> when given. To a hundredth of a
    /// second, so quarter-second nudges add up exactly.
    /// </summary>
    public void Set(double value, string songKey, string? video = null)
    {
        if (!double.IsFinite(value)) return;
        lock (_gate)
        {
            _values[songKey] = Math.Round(MusicVideoTiming.ClampedOffset(value) * 100, MidpointRounding.AwayFromZero) / 100;
            if (video is not null) _manualVideos[songKey] = video;
            else _manualVideos.Remove(songKey);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Moves the offset in force (estimated or not) by <paramref name="delta"/>; from then on it is the user's, for this video.</summary>
    public void Nudge(double delta, string songKey, string? video = null) => Set(Offset(songKey, video) + delta, songKey, video);

    /// <summary>The user's own offset for a song, as stored: its value and the video it was set on.</summary>
    public readonly record struct ManualEntry(double Value, string? Video);

    public ManualEntry? ManualEntryFor(string songKey)
    {
        lock (_gate)
            return _values.TryGetValue(songKey, out var value) ? new ManualEntry(value, _manualVideos.GetValueOrDefault(songKey)) : null;
    }

    /// <summary>Puts back what <see cref="ManualEntryFor"/> read (null: there was none).</summary>
    public void RestoreManual(ManualEntry? entry, string songKey)
    {
        if (entry is { } e) Set(e.Value, songKey, e.Video);
        else ClearManual(songKey);
    }

    /// <summary>Back to the estimate (or none).</summary>
    public void ClearManual(string songKey)
    {
        lock (_gate)
        {
            if (!_values.Remove(songKey)) return;
            _manualVideos.Remove(songKey);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>A starting guess from the video's length, for this session only.</summary>
    public void SetEstimate(double? value, string songKey, string video)
    {
        lock (_gate)
        {
            var key = EstimateKey(songKey, video);
            if (value is { } v) _estimates[key] = v;
            else _estimates.Remove(key);
        }
    }

    private void SaveLocked()
    {
        _storage.WriteMap(Key, _values, MusicVideoJson.Default.DictionaryStringDouble);
        _storage.WriteMap(ManualVideoKey, _manualVideos, MusicVideoJson.Default.DictionaryStringString);
    }

    private static string EstimateKey(string songKey, string video) => songKey + "\u0001" + video;
}

/// <summary>A YouTube link the user pasted (or picked in Search YouTube…) for a song, per recording. Played even with online search off.</summary>
public sealed class MusicVideoLinks
{
    public const string Key = "musicVideoLinks";

    private readonly IMusicVideoStorage _storage;
    private readonly Dictionary<string, string> _values;
    private readonly Lock _gate = new();

    public MusicVideoLinks(IMusicVideoStorage storage)
    {
        _storage = storage;
        _values = storage.ReadMap(Key, MusicVideoJson.Default.DictionaryStringString)
            .Where(p => YouTubeVideoId.IsValid(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    public string? VideoId(string? songKey)
    {
        if (songKey is null) return null;
        lock (_gate) return _values.GetValueOrDefault(songKey);
    }

    /// <summary>Stores the link's video; false when the text is not a YouTube link (or a bare video ID).</summary>
    public bool Set(string text, string songKey)
    {
        var trimmed = text.Trim();
        var id = YouTubeVideoId.FromUrl(trimmed) ?? (YouTubeVideoId.IsValid(trimmed) ? trimmed : null);
        if (id is null) return false;
        lock (_gate)
        {
            _values[songKey] = id;
            _storage.WriteMap(Key, _values, MusicVideoJson.Default.DictionaryStringString);
        }
        return true;
    }

    public void Remove(string songKey)
    {
        lock (_gate)
        {
            if (_values.Remove(songKey)) _storage.WriteMap(Key, _values, MusicVideoJson.Default.DictionaryStringString);
        }
    }
}

/// <summary>
/// Windows' stand-in for the Mac's Music library: a video file the user chose for a song, per recording. Lunalyr keeps
/// only the path (the file stays where it is); the Mac reads Apple Music's library through AppleScript, which Apple Music
/// for Windows doesn't offer.
/// </summary>
public sealed class MusicVideoFiles : IMusicVideoLibrary
{
    public const string Key = "musicVideoFiles";

    private readonly IMusicVideoStorage _storage;
    private readonly Dictionary<string, string> _values;
    private readonly Lock _gate = new();

    public MusicVideoFiles(IMusicVideoStorage storage)
    {
        _storage = storage;
        _values = storage.ReadMap(Key, MusicVideoJson.Default.DictionaryStringString);
    }

    public string? Path(string? songKey)
    {
        if (songKey is null) return null;
        lock (_gate) return _values.GetValueOrDefault(songKey);
    }

    public void Set(string path, string songKey)
    {
        lock (_gate)
        {
            _values[songKey] = path;
            _storage.WriteMap(Key, _values, MusicVideoJson.Default.DictionaryStringString);
        }
    }

    public void Remove(string songKey)
    {
        lock (_gate)
        {
            if (_values.Remove(songKey)) _storage.WriteMap(Key, _values, MusicVideoJson.Default.DictionaryStringString);
        }
    }

    public Task<string?> LocalVideoAsync(MusicVideoSong song, CancellationToken ct) => Task.FromResult(Path(song.Key));
}

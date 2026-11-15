using System.Security.Cryptography;
using System.Text;

namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>
/// Per-song timing corrections, in seconds (positive shows lyrics earlier). An offset is tuned to one lyric source's
/// timing, so it is stored per recording and source; offsets saved before that distinction existed are keyed by
/// recording only and still apply to every source until one is set for the source on screen.
/// </summary>
/// <remarks>
/// Keys are the lower-case hex SHA-256 of the recording key (<c>LyricQuery.RecordingKey</c>, supplied by the caller), or
/// of the recording key + <c>"|source:"</c> + source: the same keys as the Mac's <c>custom.songOffsets</c>, so saved
/// offsets can move between the apps. Storage is the caller's: pass the saved dictionary in and a callback that
/// persists the new one. Thread-safe; <see cref="OffsetFor(string?, string?)"/> is cheap enough for every frame (the
/// last answer is remembered until the song, the source or a saved value changes).
/// </remarks>
public sealed class SongOffsets
{
    /// <summary>The settings key the Mac stores these under.</summary>
    public const string SettingsKey = "custom.songOffsets";

    /// <summary>Offsets are kept within ±30 s.</summary>
    public const double Limit = 30;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, double> _values;
    private readonly Action<IReadOnlyDictionary<string, double>>? _save;
    private string? _activeSource;
    private Resolved? _resolved;

    private sealed record Resolved(string RecordingKey, string? Source, double Value);

    public SongOffsets(IReadOnlyDictionary<string, double>? saved = null,
                       Action<IReadOnlyDictionary<string, double>>? save = null)
    {
        _values = saved is null ? new(StringComparer.Ordinal) : new(saved, StringComparer.Ordinal);
        _save = save;
    }

    /// <summary>The lyric source on screen (its provenance name), kept current by the lyrics model.</summary>
    public string? ActiveSource
    {
        get { lock (_gate) return _activeSource; }
        set { lock (_gate) _activeSource = value; }
    }

    /// <summary>A copy of the stored offsets, by hashed key.</summary>
    public IReadOnlyDictionary<string, double> Values
    {
        get { lock (_gate) return new Dictionary<string, double>(_values, StringComparer.Ordinal); }
    }

    /// <summary>The offset for the source on screen; 0 when there is no song.</summary>
    public double OffsetFor(string? recordingKey)
    {
        string? source;
        lock (_gate) source = _activeSource;
        return OffsetFor(recordingKey, source);
    }

    /// <summary>The offset for <paramref name="recordingKey"/> as tuned for <paramref name="source"/>.</summary>
    public double OffsetFor(string? recordingKey, string? source)
    {
        if (recordingKey is null) return 0;
        lock (_gate)
        {
            if (_resolved is { } r && string.Equals(r.RecordingKey, recordingKey, StringComparison.Ordinal)
                && string.Equals(r.Source, source, StringComparison.Ordinal))
                return r.Value;
            double found = 0;
            var tuned = !string.IsNullOrEmpty(source) && _values.TryGetValue(SourceKey(recordingKey, source), out found);
            if (!tuned && !_values.TryGetValue(RecordingHash(recordingKey), out found)) found = 0;
            var value = double.IsFinite(found) ? Math.Clamp(found, -Limit, Limit) : 0;
            _resolved = new Resolved(recordingKey, source, value);
            return value;
        }
    }

    /// <summary>Saves the offset for the source on screen.</summary>
    public void Set(double value, string recordingKey)
    {
        string? source;
        lock (_gate) source = _activeSource;
        Set(value, recordingKey, source);
    }

    /// <summary>
    /// Saves <paramref name="value"/> (held to ±30 s; not-finite values are ignored) for the recording as tuned for
    /// <paramref name="source"/>, or for the recording as a whole when there is no source. Zero removes an entry,
    /// except that an explicit zero for a source is kept while an older recording-wide offset exists, so resetting
    /// this source does not bring that one back.
    /// </summary>
    public void Set(double value, string recordingKey, string? source)
    {
        ArgumentNullException.ThrowIfNull(recordingKey);
        if (!double.IsFinite(value)) return;
        var bounded = Math.Clamp(value, -Limit, Limit);
        Dictionary<string, double> snapshot;
        lock (_gate)
        {
            var recording = RecordingHash(recordingKey);
            if (string.IsNullOrEmpty(source))
            {
                if (bounded == 0) _values.Remove(recording);
                else _values[recording] = bounded;
            }
            else
            {
                var name = SourceKey(recordingKey, source);
                if (bounded == 0 && !_values.ContainsKey(recording)) _values.Remove(name);
                else _values[name] = bounded;
            }
            _resolved = null;
            snapshot = new Dictionary<string, double>(_values, StringComparer.Ordinal);
        }
        _save?.Invoke(snapshot);
    }

    /// <summary>The stored key for a recording-wide offset.</summary>
    public static string RecordingHash(string recordingKey) => Sha256Hex(recordingKey);

    /// <summary>The stored key for an offset tuned to one source.</summary>
    public static string SourceKey(string recordingKey, string source) => Sha256Hex(recordingKey + "|source:" + source);

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// The lyric time to show: the playback position plus the global offset (Settings, ±3 s) plus this song's offset.
    /// </summary>
    public static double LyricTime(double playbackTime, double globalOffset, double songOffset) =>
        playbackTime + globalOffset + songOffset;

    /// <summary>The global offset's range in Settings (±3 s, in 0.05 s steps on the Mac).</summary>
    public const double GlobalOffsetLimit = 3;
}

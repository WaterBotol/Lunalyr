using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lunalyr.Core.Logging;

namespace Lunalyr.Platform.Settings;

public sealed class SettingsChangedEventArgs<T>(T oldValue, T newValue) : EventArgs
{
    public T OldValue { get; } = oldValue;
    public T NewValue { get; } = newValue;
}

public sealed class SettingsSaveFailedEventArgs(string filePath, Exception error) : EventArgs
{
    public string FilePath { get; } = filePath;
    public Exception Error { get; } = error;
}

/// <summary>
/// A crash-safe JSON settings file: <c>{Folder}\{Name}.json</c>.
/// <list type="bullet">
/// <item>Saves are atomic (temp file, flush to disk, <see cref="File.Replace(string, string, string?)"/> keeping <c>.bak</c>)
/// and debounced; call <see cref="FlushAsync"/> at shutdown.</item>
/// <item>Load falls back main → <c>.bak</c> → defaults, keeps unreadable files as <c>.corrupt-{timestamp}</c> and
/// says what happened in <see cref="LoadReport"/>.</item>
/// <item>The file carries a <c>schemaVersion</c>; older files run through <see cref="SettingsStoreOptions{T}.Migrations"/>.</item>
/// <item>Thread-safe. Uses only source-generated System.Text.Json metadata, so it is Native AOT safe.</item>
/// </list>
/// </summary>
public sealed class SettingsStore<T> : IAsyncDisposable
    where T : class
{
    public const string SchemaVersionProperty = "schemaVersion";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // A file on disk, not HTML: keep lyrics and font names readable.
    };

    private readonly SettingsStoreOptions<T> _options;
    private readonly Dictionary<int, SettingsMigration> _migrations;
    private readonly int _oldestMigratableVersion;
    private readonly CategoryLogger _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private T _current;
    private long _changeSeq;
    private long _savedSeq;
    private DateTimeOffset? _firstUnsavedAt;
    private CancellationTokenSource? _debounce;
    private bool _disposed;
    private volatile Exception? _lastSaveError;
    private int _saveCount;
    private bool _keepUnreadableMainBeforeSave; // Only touched at load and under _ioLock.

    private SettingsStore(SettingsStoreOptions<T> options)
    {
        _options = options;
        _log = options.Logger.Settings;
        _migrations = options.Migrations.ToDictionary(m => m.FromVersion);
        _oldestMigratableVersion = options.Migrations.Count == 0 ? options.SchemaVersion : options.Migrations.Min(m => m.FromVersion);
        FilePath = Path.Combine(options.Folder, options.Name + ".json");
        _current = null!;
        LoadReport = null!;
    }

    /// <summary>Raised after every effective change, on the thread that made it. Read <see cref="NewValue"/> rather than re-reading <see cref="Current"/>.</summary>
    public event EventHandler<SettingsChangedEventArgs<T>>? Changed;

    /// <summary>Raised when a save fails (disk full, access denied). The change stays pending and is retried on the next change or flush.</summary>
    public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed;

    /// <summary>Test hook: raised after each successful write.</summary>
    internal event Action? Saved;

    public string FilePath { get; }

    /// <summary>What happened while loading. <see cref="SettingsLoadReport.UserMessage"/> is meant for the UI.</summary>
    public SettingsLoadReport LoadReport { get; private set; }

    /// <summary>The current settings snapshot. Treat it as immutable; change settings with <see cref="Update"/>.</summary>
    public T Current => Volatile.Read(ref _current);

    public bool HasUnsavedChanges
    {
        get
        {
            lock (_gate)
            {
                return _savedSeq < _changeSeq;
            }
        }
    }

    /// <summary>The error from the most recent save attempt, or null after a successful save.</summary>
    public Exception? LastSaveError => _lastSaveError;

    internal int SaveCount => Volatile.Read(ref _saveCount);

    /// <summary>Validates the options, loads (and if needed recovers or migrates) the file, and returns the store.</summary>
    public static SettingsStore<T> Open(SettingsStoreOptions<T> options)
    {
        Validate(options);
        var store = new SettingsStore<T>(options);
        _ = store.Serialize(options.CreateDefaults()); // Fail fast if T doesn't serialize to a JSON object.
        store.Load();
        return store;
    }

    /// <summary>
    /// Atomically replaces the settings with <c>change(Current)</c> and schedules a debounced save. <paramref name="change"/>
    /// runs under the store's lock, so keep it short and pure. Returns false when the result equals the current value.
    /// </summary>
    public bool Update(Func<T, T> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        T oldValue;
        T newValue;
        CancellationTokenSource? previous;
        CancellationTokenSource next;
        TimeSpan wait;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            oldValue = _current;
            newValue = change(oldValue) ?? throw new InvalidOperationException("A settings update returned null.");
            if (EqualityComparer<T>.Default.Equals(oldValue, newValue))
            {
                return false;
            }
            Volatile.Write(ref _current, newValue);
            _changeSeq++;

            var now = _options.TimeProvider.GetUtcNow();
            _firstUnsavedAt ??= now;
            wait = _options.SaveDelay;
            var untilMax = _firstUnsavedAt.Value + _options.MaxSaveDelay - now;
            if (untilMax < wait)
            {
                wait = untilMax < TimeSpan.Zero ? TimeSpan.Zero : untilMax;
            }
            previous = _debounce;
            next = new CancellationTokenSource();
            _debounce = next;
        }

        // The timer is created here, synchronously, so a test clock advanced right after Update sees it.
        previous?.Cancel();
        _ = SaveAfterAsync(wait, next.Token);
        Changed?.Invoke(this, new SettingsChangedEventArgs<T>(oldValue, newValue));
        return true;
    }

    /// <summary>Replaces the settings with <paramref name="value"/>. See <see cref="Update"/>.</summary>
    public bool Set(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Update(_ => value);
    }

    /// <summary>
    /// Writes any pending change now. Returns true when the latest settings are on disk; on failure
    /// <see cref="SaveFailed"/> has been raised and <see cref="LastSaveError"/> says why. Never throws.
    /// </summary>
    public async Task<bool> FlushAsync()
    {
        var saved = await SaveNowAsync().ConfigureAwait(false);
        if (saved)
        {
            // Nothing left for the debounce timer to do: stop it.
            CancellationTokenSource? pending = null;
            lock (_gate)
            {
                if (_savedSeq >= _changeSeq)
                {
                    pending = _debounce;
                    _debounce = null;
                }
            }
            pending?.Cancel();
        }
        return saved;
    }

    /// <summary>Flushes pending changes and stops accepting updates.</summary>
    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            pending = _debounce;
            _debounce = null;
        }
        pending?.Cancel();
        await SaveNowAsync().ConfigureAwait(false);
    }

    private async Task SaveAfterAsync(TimeSpan wait, CancellationToken token)
    {
        try
        {
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _options.TimeProvider, token).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield(); // Never write inline inside Update.
            }
        }
        catch (OperationCanceledException)
        {
            return; // Superseded by a later change (or disposal).
        }
        if (!token.IsCancellationRequested)
        {
            await SaveNowAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> SaveNowAsync()
    {
        await _ioLock.WaitAsync().ConfigureAwait(false);
        try
        {
            T value;
            long seq;
            lock (_gate)
            {
                if (_savedSeq >= _changeSeq)
                {
                    return true;
                }
                value = _current;
                seq = _changeSeq;
            }

            try
            {
                var bytes = Serialize(value);
                Directory.CreateDirectory(_options.Folder);
                if (_keepUnreadableMainBeforeSave)
                {
                    KeepUnreadableMain();
                }
                AtomicFile.Write(FilePath, bytes, _options.IoRetryDelay, _options.BeforeCommit);
            }
            catch (Exception ex)
            {
                _lastSaveError = ex;
                _log.Error($"Couldn't save {FilePath}; will retry on the next change", ex);
                try
                {
                    SaveFailed?.Invoke(this, new SettingsSaveFailedEventArgs(FilePath, ex));
                }
                catch (Exception handlerError)
                {
                    _log.Error("A SaveFailed handler threw", handlerError);
                }
                return false;
            }

            bool upToDate;
            lock (_gate)
            {
                _savedSeq = seq;
                upToDate = _savedSeq >= _changeSeq;
                _firstUnsavedAt = upToDate ? null : _options.TimeProvider.GetUtcNow();
            }
            _lastSaveError = null;
            Interlocked.Increment(ref _saveCount);
            Saved?.Invoke();
            return upToDate;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private byte[] Serialize(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, _options.TypeInfo) as JsonObject
            ?? throw new InvalidOperationException($"{typeof(T).Name} must serialize to a JSON object.");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber(SchemaVersionProperty, _options.SchemaVersion);
            foreach (var (name, child) in node)
            {
                if (name == SchemaVersionProperty)
                {
                    continue;
                }
                writer.WritePropertyName(name);
                if (child is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    child.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    // ---- Load and recovery ----

    private enum ReadStatus
    {
        Ok,
        Missing,
        Unreadable,
        Corrupt,
        MigrationFailed,
    }

    private readonly record struct ReadResult(ReadStatus Status, T? Value, int? Version, bool Migrated, string Detail);

    private void Load()
    {
        var problems = new List<SettingsProblem>();
        var backupPath = AtomicFile.BackupPath(FilePath);
        TryDelete(AtomicFile.TempPath(FilePath)); // An interrupted save; the committed file is authoritative.

        var main = ReadFile(FilePath);
        if (main.Status == ReadStatus.Ok)
        {
            Accept(main, SettingsSource.MainFile, problems, rewriteMain: main.Migrated);
            return;
        }
        if (main.Status != ReadStatus.Missing)
        {
            problems.Add(Problem(FilePath, main));
        }
        // A main file we couldn't open may be fine (and newer). Saves rotate it into .bak and then away, so copy it
        // aside first.
        _keepUnreadableMainBeforeSave = main.Status == ReadStatus.Unreadable;

        var backup = ReadFile(backupPath);
        if (backup.Status == ReadStatus.Ok)
        {
            if (main.Status == ReadStatus.Missing)
            {
                problems.Add(new SettingsProblem(FilePath, SettingsProblemKind.Missing, "The main file was missing; the backup was used.", null));
            }
            // A main file we merely couldn't open may be newer than the backup: don't overwrite it proactively.
            Accept(backup, SettingsSource.BackupFile, problems, rewriteMain: main.Status != ReadStatus.Unreadable);
            return;
        }
        if (backup.Status != ReadStatus.Missing)
        {
            problems.Add(Problem(backupPath, backup));
        }

        _current = _options.CreateDefaults() ?? throw new InvalidOperationException("CreateDefaults returned null.");
        LoadReport = new SettingsLoadReport(_options.Name, SettingsSource.Defaults, null, false, problems);
        if (problems.Count > 0)
        {
            _log.Warn($"Settings '{_options.Name}' reset to defaults: {Describe(problems)}");
        }
    }

    private void Accept(ReadResult result, SettingsSource source, List<SettingsProblem> problems, bool rewriteMain)
    {
        _current = result.Value!;
        if (result.Version > _options.SchemaVersion)
        {
            var copy = $"{FilePath}.schema-v{result.Version.Value.ToString(CultureInfo.InvariantCulture)}";
            string? preserved = null;
            try
            {
                File.Copy(source == SettingsSource.MainFile ? FilePath : AtomicFile.BackupPath(FilePath), copy, overwrite: true);
                preserved = copy;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Couldn't keep a copy of the newer settings file as {copy}", ex);
            }
            problems.Add(new SettingsProblem(FilePath, SettingsProblemKind.NewerSchemaVersion,
                $"schemaVersion {result.Version} is newer than {_options.SchemaVersion}.", preserved));
        }

        LoadReport = new SettingsLoadReport(_options.Name, source, result.Version, result.Migrated, problems);
        if (problems.Count > 0)
        {
            _log.Warn($"Settings '{_options.Name}' loaded from {source}: {Describe(problems)}");
        }
        if (result.Migrated)
        {
            _log.Info($"Settings '{_options.Name}' migrated from schema {result.Version} to {_options.SchemaVersion}");
        }

        // Rewrite the main file soon when it is stale: recovered from backup, or migrated to the new schema.
        if (rewriteMain)
        {
            var debounce = new CancellationTokenSource();
            lock (_gate)
            {
                _changeSeq++;
                _firstUnsavedAt = _options.TimeProvider.GetUtcNow();
                _debounce = debounce;
            }
            _ = SaveAfterAsync(_options.SaveDelay, debounce.Token);
        }
    }

    private SettingsProblem Problem(string path, ReadResult result)
    {
        var kind = result.Status switch
        {
            ReadStatus.Unreadable => SettingsProblemKind.Unreadable,
            ReadStatus.MigrationFailed => SettingsProblemKind.MigrationFailed,
            _ => SettingsProblemKind.Corrupt,
        };
        // A file we couldn't open may be perfectly fine: leave it where it is. Damaged ones are moved aside so the
        // next save can't rotate them over the good backup.
        var preserved = kind == SettingsProblemKind.Unreadable ? null : MoveAside(path);
        return new SettingsProblem(path, kind, result.Detail, preserved);
    }

    private ReadResult ReadFile(string path)
    {
        byte[] bytes;
        try
        {
            bytes = AtomicFile.ReadAllBytes(path, _options.IoRetryDelay);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ReadResult(ReadStatus.Missing, null, null, false, "Not found.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ReadResult(ReadStatus.Unreadable, null, null, false, ex.Message);
        }
        return Decode(bytes);
    }

    private ReadResult Decode(byte[] bytes)
    {
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            json = json[3..]; // Tolerate a BOM from hand edits in Notepad.
        }

        JsonObject obj;
        try
        {
            if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
                is not JsonObject parsed)
            {
                return Corrupt("The file is not a JSON object.");
            }
            obj = parsed;
        }
        catch (JsonException ex)
        {
            return Corrupt($"Not valid JSON: {ex.Message}");
        }

        var version = 1;
        if (obj[SchemaVersionProperty] is { } versionNode)
        {
            if (versionNode is not JsonValue versionValue || !versionValue.TryGetValue(out version) || version < 1)
            {
                return Corrupt($"Invalid {SchemaVersionProperty}: {versionNode.ToJsonString()}");
            }
        }
        obj.Remove(SchemaVersionProperty);

        var migrated = false;
        if (version < _options.SchemaVersion)
        {
            if (version < _oldestMigratableVersion)
            {
                return new ReadResult(ReadStatus.MigrationFailed, null, version, false,
                    $"Schema {version} is older than the oldest supported migration ({_oldestMigratableVersion}).");
            }
            for (var step = version; step < _options.SchemaVersion; step++)
            {
                try
                {
                    _migrations[step].Apply(obj);
                }
                catch (Exception ex)
                {
                    _log.Error($"Settings migration from schema {step} failed", ex);
                    return new ReadResult(ReadStatus.MigrationFailed, null, version, false, $"Migration from schema {step} failed: {ex.Message}");
                }
            }
            migrated = true;
        }

        T? value;
        try
        {
            // Lay the file over the serialized defaults, so keys the file doesn't have (settings added in a later
            // version) get their default values whatever shape T has. (The source generator fills missing init-only
            // properties with default(T), not with their initialisers.)
            var merged = JsonSerializer.SerializeToNode((_options.MissingKeyDefaults ?? _options.CreateDefaults)(), _options.TypeInfo) as JsonObject ?? [];
            MergeInto(merged, obj);
            value = merged.Deserialize(_options.TypeInfo);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        {
            return Corrupt($"Values don't match the settings: {ex.Message}");
        }
        return value is null
            ? Corrupt("The file contains null.")
            : new ReadResult(ReadStatus.Ok, value, version, migrated, string.Empty);

        ReadResult Corrupt(string detail) => new(ReadStatus.Corrupt, null, null, false, detail);
    }

    private void KeepUnreadableMain()
    {
        var stamp = _options.TimeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var copy = $"{FilePath}.unread-{stamp}";
        try
        {
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, copy, overwrite: true);
                _log.Info($"Kept the settings file that couldn't be read at start-up as {copy}");
            }
            _keepUnreadableMainBeforeSave = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still locked: the save will most likely fail too, and we try again next time.
            _log.Warn($"Couldn't copy {FilePath} aside yet", ex);
        }
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (name, child) in source)
        {
            if (child is JsonObject childObject && target[name] is JsonObject targetObject)
            {
                MergeInto(targetObject, childObject);
            }
            else
            {
                target[name] = child?.DeepClone();
            }
        }
    }

    private string? MoveAside(string path)
    {
        var stamp = _options.TimeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var target = $"{path}.corrupt-{stamp}";
        for (var n = 2; File.Exists(target); n++)
        {
            target = $"{path}.corrupt-{stamp}-{n.ToString(CultureInfo.InvariantCulture)}";
        }
        try
        {
            File.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Couldn't move the damaged file {path} aside", ex);
            return null;
        }
        PruneCorruptCopies();
        return target;
    }

    private void PruneCorruptCopies()
    {
        try
        {
            // Newest first by the timestamp in the name (a moved file keeps its original creation time).
            const string marker = ".corrupt-";
            var copies = Directory.GetFiles(_options.Folder, _options.Name + ".json*" + marker + "*")
                .OrderByDescending(f => f[(f.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..], StringComparer.Ordinal)
                .Skip(Math.Max(_options.MaxCorruptCopies, 1));
            foreach (var old in copies)
            {
                TryDelete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static string Describe(List<SettingsProblem> problems) =>
        string.Join("; ", problems.Select(p => $"{Path.GetFileName(p.FilePath)} {p.Kind}: {p.Detail}"
            + (p.PreservedAs is null ? string.Empty : $" (kept as {Path.GetFileName(p.PreservedAs)})")));

    private static void Validate(SettingsStoreOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        if (options.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || options.Name.Contains('.'))
        {
            throw new ArgumentException("Name must be a plain file stem such as \"settings\".", nameof(options));
        }
        ArgumentNullException.ThrowIfNull(options.TypeInfo);
        ArgumentNullException.ThrowIfNull(options.CreateDefaults);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SchemaVersion, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SaveDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSaveDelay, options.SaveDelay);

        var steps = options.Migrations.Select(m => m.FromVersion).Order().ToList();
        if (options.Migrations.Any(m => m is null || m.Apply is null))
        {
            throw new ArgumentException("Migrations must not be null.", nameof(options));
        }
        if (steps.Count > 0)
        {
            if (steps[0] < 1 || steps[0] >= options.SchemaVersion
                || !steps.SequenceEqual(Enumerable.Range(steps[0], options.SchemaVersion - steps[0])))
            {
                throw new ArgumentException(
                    $"Migrations must cover consecutive versions ending at SchemaVersion - 1 ({options.SchemaVersion - 1}); got [{string.Join(", ", steps)}].",
                    nameof(options));
            }
        }
    }
}

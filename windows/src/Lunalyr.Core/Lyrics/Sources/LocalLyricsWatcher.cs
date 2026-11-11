using Lunalyr.Core.Logging;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Watches only the lyric folder chosen in settings (not its subfolders) and reports changes once a burst settles, so
/// an LRC + TTML + reading export causes one re-read. File-level events include edits made in place. The callback runs
/// on a thread-pool thread.
/// </summary>
public sealed class LocalLyricsWatcher : IDisposable
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly CategoryLogger _log;
    private FileSystemWatcher? _watcher;
    private ITimer? _pending;
    private long _generation;
    private Action? _onChange;
    private string _path = "";
    private bool _disposed;

    /// <param name="debounce">How long the folder must stay quiet before the callback runs (default 0.7 s).</param>
    public LocalLyricsWatcher(TimeSpan? debounce = null, TimeProvider? timeProvider = null, Logger? logger = null)
    {
        _debounce = debounce ?? TimeSpan.FromMilliseconds(700);
        _time = timeProvider ?? TimeProvider.System;
        _log = (logger ?? Logger.Null).Lyrics;
    }

    /// <summary>The folder being watched; empty when none.</summary>
    public string Path
    {
        get
        {
            lock (_gate)
            {
                return _path;
            }
        }
    }

    /// <summary>
    /// Watches <paramref name="path"/> and calls <paramref name="onChange"/> after changes settle. Calling again with
    /// the same path only replaces the callback; an empty path stops watching. A folder that cannot be watched (missing,
    /// no access) is reported in the log and simply not watched.
    /// </summary>
    public void Watch(string path, Action onChange)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(onChange);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _onChange = onChange;
            if (string.Equals(path, _path, StringComparison.Ordinal))
            {
                return;
            }
            StopLocked();
            _path = path;
            if (path.Length == 0)
            {
                return;
            }
            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                watcher.Changed += OnEvent;
                watcher.Created += OnEvent;
                watcher.Deleted += OnEvent;
                watcher.Renamed += OnEvent;
                // A lost event buffer or a folder that vanished: re-read rather than miss a change.
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                _log.Warn($"Can't watch the lyrics folder: {exception.Message}");
                // Not watching: the same path may be tried again later.
                _path = "";
            }
        }
    }

    /// <summary>Reports a change, as a file event does: the callback runs once the debounce interval passes quietly.</summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (_disposed || _onChange is null)
            {
                return;
            }
            _pending?.Dispose();
            // A timer that already fired may still be on its way into Fire; the generation tells it it is stale.
            var generation = ++_generation;
            _pending = _time.CreateTimer(_ => Fire(generation), null, _debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            StopLocked();
            _onChange = null;
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Signal();

    private void OnError(object sender, ErrorEventArgs e)
    {
        _log.Debug($"Lyrics folder watcher error: {e.GetException().Message}");
        Signal();
    }

    private void Fire(long generation)
    {
        Action? callback;
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }
            _pending?.Dispose();
            _pending = null;
            callback = _onChange;
        }
        try
        {
            callback?.Invoke();
        }
        catch (Exception exception)
        {
            _log.Error("Lyrics folder change handler failed", exception);
        }
    }

    private void StopLocked()
    {
        _pending?.Dispose();
        _pending = null;
        _generation++;
        if (_watcher is { } watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnEvent;
            watcher.Created -= OnEvent;
            watcher.Deleted -= OnEvent;
            watcher.Renamed -= OnEvent;
            watcher.Error -= OnError;
            watcher.Dispose();
            _watcher = null;
        }
        _path = "";
    }
}

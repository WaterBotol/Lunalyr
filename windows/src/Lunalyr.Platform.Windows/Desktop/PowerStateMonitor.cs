namespace Lunalyr.Platform.Desktop;

/// <summary>
/// Holds the last known value of a power state (on battery, Energy saver on) and refreshes it whenever its
/// subscription reports a change, raising <see cref="Changed"/> only on real flips (charge-level updates and repeated
/// notifications are noise). Ported from the Mac's <c>PowerSourceMonitor</c>; tests give it their own reader and
/// subscription.
/// </summary>
public sealed class PowerStateMonitor : IDisposable
{
    /// <summary>Starts delivering change notices to the callback and returns what keeps them going; disposing it stops them.</summary>
    public delegate IDisposable? Subscribe(Action changed);

    private readonly Func<bool> _read;
    private readonly Lock _gate = new();
    private IDisposable? _subscription;
    private bool _state;
    private bool _disposed;

    public PowerStateMonitor(Func<bool> read, Subscribe subscribe)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(subscribe);
        _read = read;
        _state = read();
        _subscription = subscribe(Refresh);
        // A flip between the first read and the subscription would otherwise go unseen. Nobody has read the state
        // yet, so nothing is announced.
        bool now = read();
        lock (_gate) _state = now;
    }

    /// <summary>Raised on the thread that called <see cref="Refresh"/>, once per real change.</summary>
    public event EventHandler? Changed;

    public bool Value
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Re-reads the state, raising <see cref="Changed"/> if it moved.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        bool now = _read();
        lock (_gate)
        {
            if (_state == now) return;
            _state = now;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
    }
}

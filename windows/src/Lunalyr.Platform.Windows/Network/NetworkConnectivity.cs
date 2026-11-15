using Lunalyr.Core.Lyrics.Ranking;
using Windows.Networking.Connectivity;

namespace Lunalyr.Platform.Network;

/// <summary>
/// Whether the PC can reach the internet, from Windows' own network status (<c>NetworkInformation</c>), for the lyric
/// lookup: it skips sources while offline and looks the song up again when <see cref="Restored"/> is raised.
/// </summary>
/// <remarks>
/// "Online" means Windows reports internet access, full or constrained (a captive portal still lets some requests
/// through, and Windows' own probe is sometimes switched off by policy, so a constrained report is not trusted to mean
/// offline). Events are raised on a thread-pool thread. Dispose to stop listening.
/// </remarks>
public sealed class NetworkConnectivity : ILyricConnectivity, IDisposable
{
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private bool _online;
    private bool _disposed;

    public NetworkConnectivity(Action<string>? log = null)
    {
        _log = log;
        _online = ReadOnline();
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
    }

    public bool IsOnline
    {
        get { lock (_gate) return _online; }
    }

    /// <summary>The connection came back after being lost.</summary>
    public event EventHandler? Restored;

    /// <summary>The connection was lost.</summary>
    public event EventHandler? Lost;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
    }

    private void OnNetworkStatusChanged(object sender)
    {
        var online = ReadOnline();
        bool restored, lost;
        lock (_gate)
        {
            if (_disposed || online == _online) return;
            restored = online && !_online;
            lost = !online && _online;
            _online = online;
        }

        _log?.Invoke(online ? "Network: back online." : "Network: offline.");
        if (restored) Restored?.Invoke(this, EventArgs.Empty);
        if (lost) Lost?.Invoke(this, EventArgs.Empty);
    }

    private bool ReadOnline()
    {
        try
        {
            var level = NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel();
            return level is NetworkConnectivityLevel.InternetAccess or NetworkConnectivityLevel.ConstrainedInternetAccess;
        }
        catch (Exception ex)
        {
            // Never block lookups on a failed status read: assume online and let the sources answer.
            _log?.Invoke($"Network: couldn't read the connection status: {ex.Message}");
            return true;
        }
    }
}

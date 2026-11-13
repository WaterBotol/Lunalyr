using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Platform.NowPlaying;

/// <summary>
/// The sessions Lunalyr may follow under the player choice (<see cref="PlayerChoice"/>, Settings › Music players ›
/// Player): every session and Windows' current one for Automatic; with one player chosen, only that player's sessions,
/// and of those the one Windows calls current, else the first Windows lists. The others are never handed on, so nothing
/// subscribes to them or reads what they play: only their app id is looked at, to tell them apart.
/// </summary>
/// <remarks>Thread-safe. Events from the inner source are passed on; a new choice raises both events.</remarks>
public sealed class FollowedSessionSource : IMediaSessionSource
{
    private readonly IMediaSessionSource _inner;
    private readonly bool _ownsInner;
    private readonly Action<string>? _log;
    private volatile string _choice;

    public FollowedSessionSource(IMediaSessionSource inner, string? choice = null, bool ownsInner = false, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _ownsInner = ownsInner;
        _log = log;
        _choice = PlayerChoice.Normalize(choice);
        _inner.SessionsChanged += OnSessionsChanged;
        _inner.CurrentSessionChanged += OnCurrentSessionChanged;
    }

    /// <summary>The player followed (normalised; Automatic for anything unknown).</summary>
    public string Choice => _choice;

    public event EventHandler? SessionsChanged;

    public event EventHandler? CurrentSessionChanged;

    /// <summary>The sessions of the player chosen (every session for Automatic), in Windows' order.</summary>
    public IReadOnlyList<IMediaSession> Sessions
    {
        get
        {
            var choice = _choice;
            var all = _inner.Sessions;
            return PlayerChoice.IsAutomatic(choice) ? all : [.. all.Where(session => PlayerChoice.Follows(choice, session.SourceApp))];
        }
    }

    /// <summary>
    /// Automatic: the session Windows calls current. A chosen player: Windows' current session when it is that player's,
    /// else that player's first session, else none (the chosen player isn't sharing anything).
    /// </summary>
    public IMediaSession? CurrentSession
    {
        get
        {
            var choice = _choice;
            var current = _inner.CurrentSession;
            if (PlayerChoice.IsAutomatic(choice)) return current;
            if (current is not null && PlayerChoice.Follows(choice, current.SourceApp)) return current;
            foreach (var session in _inner.Sessions)
                if (PlayerChoice.Follows(choice, session.SourceApp)) return session;
            return null;
        }
    }

    /// <summary>Follows <paramref name="choice"/> from now on; the monitor moves to that player's session at once.</summary>
    public void SetChoice(string? choice)
    {
        var normalized = PlayerChoice.Normalize(choice);
        if (normalized == _choice) return;
        _choice = normalized;
        _log?.Invoke($"Now playing: following {(PlayerChoice.IsAutomatic(normalized) ? "whichever player Windows calls current" : $"{PlayerChoice.Title(normalized)} only")}.");
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionsChanged(object? sender, EventArgs e) => SessionsChanged?.Invoke(this, e);

    private void OnCurrentSessionChanged(object? sender, EventArgs e) => CurrentSessionChanged?.Invoke(this, e);

    public void Dispose()
    {
        _inner.SessionsChanged -= OnSessionsChanged;
        _inner.CurrentSessionChanged -= OnCurrentSessionChanged;
        if (_ownsInner) _inner.Dispose();
    }
}

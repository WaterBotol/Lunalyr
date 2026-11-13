// lunalyr:// links, ported from the Mac's LunalyrLinks.swift (LunalyrCommand, LunalyrLink, LunalyrLinkRouter; Lunalyr's
// own lines, blame-checked against a99fd65: none) and ListenProtocol.isValidRoom. The Mac parses with URLComponents;
// Windows parses the raw text by hand, as strictly or more so, because System.Uri would resolve "..", decode
// percent-escapes and accept forms the Mac turns away.

using System.Text;

namespace Lunalyr.Shell.Extras;

/// <summary>Something Lunalyr can be asked to do from outside: a <c>lunalyr://</c> link or a global shortcut ends up here.</summary>
public enum LunalyrCommand
{
    ToggleLyrics,
    SyncEarlier,
    SyncLater,
    CycleLayout,
    LyricCard,
    PauseWallpaper,
    OpenSettings,
    ToggleFloating,
}

public static class LunalyrCommands
{
    public static IReadOnlyList<LunalyrCommand> All { get; } = Enum.GetValues<LunalyrCommand>();

    /// <summary>
    /// What the command does, when it said nothing of its own (one that opens a window). The Mac's Shortcuts actions
    /// answer with it; Windows logs it.
    /// </summary>
    public static string DoneText(this LunalyrCommand command) => command switch
    {
        LunalyrCommand.ToggleLyrics => "Lyrics shown or hidden",
        LunalyrCommand.SyncEarlier => "Lyrics moved earlier",
        LunalyrCommand.SyncLater => "Lyrics moved later",
        LunalyrCommand.CycleLayout => "Next layout",
        LunalyrCommand.LyricCard => "Opening the lyric card",
        LunalyrCommand.PauseWallpaper => "Wallpaper paused or resumed",
        LunalyrCommand.OpenSettings => "Opening Lunalyr Settings",
        LunalyrCommand.ToggleFloating => "Lyrics moved between the desktop and floating",
        _ => command.ToString(),
    };
}

/// <summary>
/// A <c>lunalyr://</c> link, parsed. Pure: no settings, no network.
/// <list type="bullet">
/// <item><c>lunalyr://join/&lt;room&gt;</c>: listen along with a room (the Mac asks first; Windows has no listen-along yet)</item>
/// <item><c>lunalyr://toggle</c>: show or hide the lyrics</item>
/// <item><c>lunalyr://card</c>: a lyric card of the current line</item>
/// <item><c>lunalyr://settings</c>: open Settings</item>
/// <item><c>lunalyr://floating</c>: switch the lyrics between the desktop and floating</item>
/// </list>
/// A link can come from any web page or message, so anything else (another scheme, extra path parts, a query or
/// fragment, a user or port, a room that is not exactly ten lower-case letters or digits, any character outside plain
/// printable ASCII) is ignored.
/// </summary>
public abstract record LunalyrLink
{
    public const string Scheme = "lunalyr";

    /// <summary>Longer than any link Lunalyr makes; anything longer is ignored unread.</summary>
    public const int MaximumLength = 256;

    private LunalyrLink() { }

    public sealed record Join(string Room) : LunalyrLink;

    public sealed record Command(LunalyrCommand Value) : LunalyrLink;

    /// <summary>The link <paramref name="text"/> names, or null for anything that isn't exactly one of Lunalyr's.</summary>
    public static LunalyrLink? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaximumLength) return null;
        foreach (var c in text)
            if (c is < '!' or > '~') return null; // no spaces, controls or anything beyond printable ASCII
        // Scheme, then exactly "//": "lunalyr:toggle" has no host, "lunalyr:///toggle" an empty one.
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !text.AsSpan(0, colon).Equals(Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = text.AsSpan(colon + 1);
        if (!rest.StartsWith("//")) return null;
        rest = rest[2..];
        // No query or fragment, not even an empty one.
        if (rest.IndexOfAny('?', '#') >= 0) return null;
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? ReadOnlySpan<char>.Empty : rest[slash..];
        // The host alone: no user, password or port, and only letters (no escapes that could decode to one).
        if (authority.IsEmpty) return null;
        foreach (var c in authority)
            if (!char.IsAsciiLetter(c)) return null;
        var host = authority.ToString().ToLowerInvariant();

        // The raw path, so a percent-encoded room never sneaks past the check. "/abc" splits to ["", "abc"]; a single
        // trailing slash is allowed.
        var parts = path.ToString().Split('/').ToList();
        if (parts.Count > 0 && parts[0].Length == 0) parts.RemoveAt(0);
        if (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        switch (host)
        {
            case "join":
                return parts.Count == 1 && IsValidRoom(parts[0]) ? new Join(parts[0]) : null;
            case "toggle" or "card" or "settings" or "floating":
                if (parts.Count != 0) return null;
                return new Command(host switch
                {
                    "toggle" => LunalyrCommand.ToggleLyrics,
                    "card" => LunalyrCommand.LyricCard,
                    "floating" => LunalyrCommand.ToggleFloating,
                    _ => LunalyrCommand.OpenSettings,
                });
            default:
                return null;
        }
    }

    /// <summary>A listen-along room code: exactly ten lower-case ASCII letters or digits (the Mac's <c>ListenProtocol.isValidRoom</c>).</summary>
    public static bool IsValidRoom(string? room) =>
        room is { Length: 10 } && room.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    /// <summary>The link for a room, as the web listen-along page offers it.</summary>
    public static string? JoinUrl(string room) => IsValidRoom(room) ? $"{Scheme}://join/{room}" : null;

    /// <summary>
    /// <paramref name="text"/> made safe for the log: cut short, with anything outside printable ASCII escaped, as the
    /// text can come from any page.
    /// </summary>
    public static string ForLog(string? text)
    {
        if (text is null) return "(none)";
        var builder = new StringBuilder();
        foreach (var c in text.Length > 120 ? text[..120] : text)
            builder.Append(c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}");
        if (text.Length > 120) builder.Append('…');
        return builder.ToString();
    }
}

/// <summary>
/// Acts on opened <c>lunalyr://</c> links. What each link does is injected, so tests route links without touching
/// the app. Links that arrive while Lunalyr starts wait until it has (the controller is up), then run in order.
/// Call it on one thread (the app's UI thread).
/// </summary>
public sealed class LunalyrLinkRouter
{
    /// <summary>What a join link says while it can't be followed. Windows has no listen-along relay yet.</summary>
    public const string JoinUnavailable = "Listening along needs a relay, not yet in Lunalyr for Windows";

    /// <param name="Join">Follows a room; false when it can't (no relay).</param>
    /// <param name="Run">Runs a command exactly as its global shortcut does.</param>
    /// <param name="Note">Shows a short note (text, symbol) in the action HUD.</param>
    public sealed record Actions(Func<string, bool> Join, Action<LunalyrCommand> Run, Action<string, string> Note);

    private readonly Actions _actions;
    private readonly Action<string>? _log;
    private readonly List<LunalyrLink> _waiting = [];
    private bool _ready;

    public LunalyrLinkRouter(Actions actions, bool ready = false, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _actions = actions;
        _ready = ready;
        _log = log;
    }

    /// <summary>Handles every link it understands and ignores the rest.</summary>
    public void Open(IEnumerable<string> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        foreach (var text in links)
        {
            if (LunalyrLink.Parse(text) is not { } link)
            {
                _log?.Invoke($"Ignored a link that isn't one of Lunalyr's: {LunalyrLink.ForLog(text)}.");
                continue;
            }
            _log?.Invoke($"Link {LunalyrLink.ForLog(text)}{(_ready ? "" : " (runs once Lunalyr has started)")}.");
            if (_ready) Route(link);
            else _waiting.Add(link);
        }
    }

    /// <summary>A command from outside: runs now, or once the app has started (like a link).</summary>
    public void Run(LunalyrCommand command)
    {
        if (_ready) _actions.Run(command);
        else _waiting.Add(new LunalyrLink.Command(command));
    }

    /// <summary>The app has started: run what arrived with the launch.</summary>
    public void BecameReady()
    {
        if (_ready) return;
        _ready = true;
        var pending = _waiting.ToList();
        _waiting.Clear();
        foreach (var link in pending) Route(link);
    }

    private void Route(LunalyrLink link)
    {
        switch (link)
        {
            case LunalyrLink.Join join:
                if (!_actions.Join(join.Room)) _actions.Note(JoinUnavailable, "person.2");
                break;
            case LunalyrLink.Command command:
                _actions.Run(command.Value);
                break;
        }
    }
}

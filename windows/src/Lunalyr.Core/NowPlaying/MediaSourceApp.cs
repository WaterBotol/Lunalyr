namespace Lunalyr.Core.NowPlaying;

/// <summary>Which kind of app owns a media session.</summary>
public enum MediaSourceKind
{
    Unknown = 0,
    Spotify,
    AppleMusic,
    MediaPlayer,
    Browser,
    Other,
}

/// <summary>
/// The app behind a media session, recognised from GSMTC's <c>SourceAppUserModelId</c> (an AppUserModelID for
/// packaged apps, often just the exe name for desktop ones).
/// </summary>
public sealed record MediaSourceApp(string AppId, MediaSourceKind Kind, string DisplayName)
{
    // Checked in order, case-insensitively, against the whole id. Spotify and Apple Music come first so their
    // web-app wrappers are still recognised by name.
    private static readonly (string Needle, MediaSourceKind Kind, string Name)[] Known =
    [
        ("Spotify", MediaSourceKind.Spotify, "Spotify"),
        ("AppleInc.AppleMusicWin", MediaSourceKind.AppleMusic, "Apple Music"),
        ("AppleMusic", MediaSourceKind.AppleMusic, "Apple Music"),
        ("iTunes", MediaSourceKind.Other, "iTunes"),
        ("Microsoft.ZuneMusic", MediaSourceKind.MediaPlayer, "Media Player"),
        ("Microsoft.ZuneVideo", MediaSourceKind.Other, "Films & TV"),
        ("MSEdge", MediaSourceKind.Browser, "Microsoft Edge"),
        ("Chrome", MediaSourceKind.Browser, "Google Chrome"),
        ("308046B0AF4A39CB", MediaSourceKind.Browser, "Firefox"), // Firefox's AppUserModelID.
        ("Firefox", MediaSourceKind.Browser, "Firefox"),
        ("Brave", MediaSourceKind.Browser, "Brave"),
        ("Opera", MediaSourceKind.Browser, "Opera"),
        ("Vivaldi", MediaSourceKind.Browser, "Vivaldi"),
        ("TheBrowserCompany.Arc", MediaSourceKind.Browser, "Arc"),
    ];

    public static MediaSourceApp Unknown { get; } = new("", MediaSourceKind.Unknown, "Unknown app");

    public bool IsBrowser => Kind == MediaSourceKind.Browser;

    public static MediaSourceApp FromAppId(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return Unknown;
        var id = appId.Trim();
        foreach (var (needle, kind, name) in Known)
        {
            if (id.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return new MediaSourceApp(id, kind, name);
        }

        return new MediaSourceApp(id, MediaSourceKind.Other, FriendlyName(id));
    }

    // "Contoso.SuperPlayer_8wekyb3d8bbwe!App" → "SuperPlayer"; "foobar2000.exe" → "foobar2000".
    private static string FriendlyName(string id)
    {
        var name = id;
        var bang = name.IndexOf('!', StringComparison.Ordinal);
        if (bang >= 0)
        {
            name = name[..bang];
            var underscore = name.LastIndexOf('_');
            if (underscore > 0) name = name[..underscore];
            var dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        }
        else
        {
            var slash = name.LastIndexOfAny(['\\', '/']);
            if (slash >= 0) name = name[(slash + 1)..];
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        }

        return string.IsNullOrWhiteSpace(name) ? id : name;
    }
}

using Lunalyr.Core.MusicVideo;
using Lunalyr.Platform.MusicVideo;
using Lunalyr.Shell;

namespace Lunalyr.App.MusicVideo;

/// <summary>
/// What the music video's windows (Settings › Wallpaper › Music video, the tray flyout, the desktop player's video bar)
/// reach: set once by <c>AppHost.StartMusicVideo</c>. Null members mean that part is off for this run.
/// </summary>
internal static class MusicVideoUi
{
    public static MusicVideoController? Controller { get; set; }

    public static AppController? App { get; set; }

    /// <summary>The YouTube Data API key's store; null under --demo or --no-network (nothing may reach YouTube).</summary>
    public static YouTubeApiKeyStore? Keys { get; set; }

    /// <summary>Why YouTube is off for this run ("--demo", "--no-network", "--no-playback"), or null.</summary>
    public static string? YouTubeOffReason { get; set; }

    /// <summary>The client the picker's stills come through (YouTube's image host only), or null.</summary>
    public static MusicVideoHttpClient? Http { get; set; }

    /// <summary>Opens a watch link in the browser, only from a click; null while playback is off for the run.</summary>
    public static Action<string>? OpenUrl { get; set; }

    /// <summary>The search picker's state, kept while the app runs (the Mac's model outlives its window too).</summary>
    public static MusicVideoSearchModel? Search { get; set; }
}

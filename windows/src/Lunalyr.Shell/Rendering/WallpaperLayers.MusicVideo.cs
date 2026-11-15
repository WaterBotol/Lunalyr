using Lunalyr.Core.Logging;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.MusicVideo;

namespace Lunalyr.Shell.Rendering;

// The music video's layers (WallpaperLayers.cs hook): the song's video file ("musicVideo", also the album-colour stand-in
// while the video is looked for) and YouTube's see-through layer ("musicVideo.youtube"), both on the shared feed.
public static partial class WallpaperLayers
{
    static partial void AddMusicVideoLayers(List<IRenderLayerFactory> into, BackgroundOptions options, Logger? logger)
    {
        into.Add(new MusicVideoLayerFactory(MusicVideoFeed.Shared, options, logger));
        into.Add(new YouTubeVideoLayerFactory(MusicVideoFeed.Shared, options, logger));
    }
}

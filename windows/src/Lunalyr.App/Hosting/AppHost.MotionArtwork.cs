using Lunalyr.Core.Covers;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Platform.Media;
using Lunalyr.Shell;
using Lunalyr.Shell.Covers;

namespace Lunalyr.App.Hosting;

// Animated covers (Covers › "Use animated covers"): Apple's public pages are asked with Lunalyr's own User-Agent, and the
// chosen rendition is kept in {data}\AnimatedCovers (200 MB at most, least recently used first out). Nothing is looked
// up under --demo or --no-network. Power saving that holds the wallpaper to patterns or colour only pauses them.
internal sealed partial class AppHost
{
    private void StartMotionCovers(AppController controller)
    {
        if (_options.Demo || _options.NoNetwork)
        {
            controller.UseMotionCovers(null, _options.Demo ? "the demo" : "--no-network");
            _log.Info($"Animated covers: not looked up in this run ({(_options.Demo ? "the demo" : "--no-network")}).");
        }
        else
        {
            var http = LyricHttpClient.CreateDefaultHttpClient();
            var finder = new MotionArtworkFinder(new LyricHttpClient(http, new LyricHttpOptions { UserAgent = UserAgent }, Log), logger: Log);
            var cache = new MotionCoverCache(Path.Combine(DataRoot, "AnimatedCovers"), http, UserAgent, logger: Log);
            cache.Trim();
            // Checked to open with a video track (in memory, one frame) before it is ever drawn; no audio is read.
            var source = new MotionCoverService(finder, cache, path => VideoReader.HasVideo(path, out _), Log);
            controller.UseMotionCovers(source, portraitDisplays: () => _wallpaper?.Engine.Surfaces.Any(s => s.Height > s.Width) == true);
            _log.Info($"Animated covers: from Apple Music's public pages (storefront {finder.Storefront}); cache {cache.Size() / 1048576.0:0.#} MB of {cache.MaxBytes / 1048576} MB.");
        }
        if (_wallpaper?.Engine is { } engine) engine.StatusChanged += (_, e) => controller.SetPowerConstraint(e.Power);
    }
}

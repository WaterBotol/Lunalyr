// Read-only now-playing probe. Usage: NowPlayingProbe [seconds] [--hide-titles]
// It only reads sessions, metadata and timelines. It never calls a Try…Async command, so it can't play, pause or seek.
using Lunalyr.Platform.NowPlaying;

int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 30;
bool hide = args.Contains("--hide-titles");
string Show(string? t) => t is null ? "(none)" : hide ? $"<{t.Length} chars>" : t;
var t0 = DateTime.Now;
void Log(string m) => Console.WriteLine($"{(DateTime.Now - t0).TotalSeconds,7:F2}s  {m}");

using var source = await GsmtcMediaSessionSource.CreateAsync(m => Log("[gsmtc] " + m));
Log($"sessions: {source.Sessions.Count}");
foreach (var ses in source.Sessions) Log($"  session {ses.SourceAppUserModelId} -> {ses.SourceApp}");

using var monitor = new NowPlayingMonitor(source, TimeProvider.System, log: m => Log("[monitor] " + m));
monitor.SessionChanged += (_, _) => Log($"SessionChanged -> {monitor.Session?.SourceAppUserModelId ?? "(none)"}");
monitor.TrackChanged += (_, _) => Log($"TrackChanged -> '{Show(monitor.Track?.Title)}' by '{Show(monitor.Track?.Artist)}' / album '{Show(monitor.Track?.AlbumTitle)}' / album artist '{Show(monitor.Track?.AlbumArtist)}' / subtitle '{Show(monitor.Track?.Subtitle)}'");
monitor.PlaybackChanged += (_, _) => Log($"PlaybackChanged -> {monitor.PlaybackInfo.Status}, controls {monitor.PlaybackInfo.Controls}");
monitor.Clock.SeekDetected += (_, e) => Log($"SeekDetected {e}");
foreach (var ses in source.Sessions)
    ses.TimelinePropertiesChanged += (_, _) => { var tl = ses.GetTimeline(); Log($"timeline event [{ses.SourceApp}] {tl}"); };
monitor.Start();

var caps = monitor.Capabilities;
Log($"capabilities: {caps}");
var end = DateTime.Now.AddSeconds(seconds);
while (DateTime.Now < end)
{
    await Task.Delay(1000);
    var r = monitor.Clock.Read();
    Log($"clock pos={r.Position:mm\\:ss\\.ff} dur={r.Duration:mm\\:ss} playing={r.IsPlaying} rate={r.Rate} quality={r.Quality} conf={r.Confidence:F2}");
}

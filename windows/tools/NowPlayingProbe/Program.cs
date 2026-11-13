// Read-only now-playing probe. Usage: NowPlayingProbe [seconds] [--hide-titles] [--meter]
// --meter also logs when sound starts and stops at the default output (its peak meter only: nothing is captured or
// recorded), to measure how far a player's reported position is from the sound.
// It only reads sessions, metadata and timelines. It never calls a Try…Async command, so it can't play, pause or seek.
using Lunalyr.Core.Playback;
using Lunalyr.Platform.NowPlaying;

int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 30;
bool hide = args.Contains("--hide-titles");
string Show(string? t) => t is null ? "(none)" : hide ? $"<{t.Length} chars>" : t;
var t0 = DateTime.Now;
void Log(string m) => Console.WriteLine($"{(DateTime.Now - t0).TotalSeconds,7:F2}s  {m}");

// The probe only reads, so its guard refuses every command outright.
var guard = new PlaybackGuard();
guard.Block("NowPlayingProbe is read-only");
using var source = await GsmtcMediaSessionSource.CreateAsync(guard, m => Log("[gsmtc] " + m));
Log($"sessions: {source.Sessions.Count}");
foreach (var ses in source.Sessions) Log($"  session {ses.SourceAppUserModelId} -> {ses.SourceApp}");

using var monitor = new NowPlayingMonitor(source, TimeProvider.System, log: m => Log("[monitor] " + m));
monitor.SessionChanged += (_, _) => Log($"SessionChanged -> {monitor.Session?.SourceAppUserModelId ?? "(none)"}");
monitor.TrackChanged += (_, _) => Log($"TrackChanged -> '{Show(monitor.Track?.Title)}' by '{Show(monitor.Track?.Artist)}' / album '{Show(monitor.Track?.AlbumTitle)}' / album artist '{Show(monitor.Track?.AlbumArtist)}' / subtitle '{Show(monitor.Track?.Subtitle)}'");
monitor.PlaybackChanged += (_, _) => Log($"PlaybackChanged -> {monitor.PlaybackInfo.Status}, controls {monitor.PlaybackInfo.Controls}");
monitor.Clock.SeekDetected += (_, e) => Log($"SeekDetected {e}");
foreach (var ses in source.Sessions)
    ses.TimelinePropertiesChanged += (_, _) => { var tl = ses.GetTimeline(); if (tl is { } t) Log($"timeline [{ses.SourceApp.Kind}] pos={t.Position:c} updated={t.LastUpdatedTime:HH:mm:ss.fff} now={DateTimeOffset.Now:HH:mm:ss.fff} (age {(DateTimeOffset.Now - t.LastUpdatedTime).TotalMilliseconds:0} ms)"); };
monitor.Start();

if (args.Contains("--meter"))
{
    var meter = new Lunalyr.Platform.Audio.OutputActivityMeter();
    _ = Task.Run(async () =>
    {
        // "Sound starts" = a peak over 0.02 after at least 250 ms under 0.003 (a gap between songs, or a pause).
        var lastSound = DateTime.Now; var loud = true;
        while (DateTime.Now < t0.AddSeconds(seconds + 5))
        {
            var peak = meter.Peak() ?? 0;
            var now = DateTime.Now;
            if (peak > 0.02f && !loud && (now - lastSound).TotalMilliseconds > 250)
            {
                loud = true;
                var r = monitor.Clock.Read();
                Log($"SOUND STARTS peak={peak:F3} at {DateTimeOffset.Now:HH:mm:ss.fff}; clock says {r.Position.TotalSeconds:F3} s playing={r.IsPlaying}");
            }
            if (peak >= 0.003f) lastSound = now;
            else if (loud && (now - lastSound).TotalMilliseconds > 250) { loud = false; Log($"quiet since {DateTimeOffset.Now.AddMilliseconds(-250):HH:mm:ss.fff}"); }
            await Task.Delay(5);
        }
    });
}

var caps = monitor.Capabilities;
Log($"capabilities: {caps}");
var end = DateTime.Now.AddSeconds(seconds);
while (DateTime.Now < end)
{
    await Task.Delay(1000);
    var r = monitor.Clock.Read();
    Log($"clock pos={r.Position:mm\\:ss\\.ff} dur={r.Duration:mm\\:ss} playing={r.IsPlaying} rate={r.Rate} quality={r.Quality} conf={r.Confidence:F2} whole-second: on={monitor.Clock.WholeSecondState.Active} added={monitor.Clock.WholeSecondState.LastFraction:F3} uncertainty={monitor.Clock.WholeSecondState.Uncertainty:F3}");
}

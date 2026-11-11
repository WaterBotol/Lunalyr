// Manual live lyric lookup. Usage:
//   LyricProbe --title "Shape of You" --artist "Ed Sheeran" [--album "Divide"] --duration 233.7 [--search] [--lines 6]
// It asks the real lyric services (with the app's generic User-Agent), prints each source's state, the ranking and the
// chosen result's provenance and first lines. Saved choices and the cache go to a fresh temp folder, never the app's.
// It plays nothing and reads no media session.
using System.Globalization;
using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Pipeline;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Platform.Lyrics;
using Lunalyr.Platform.Readings;

Console.OutputEncoding = Encoding.UTF8;
string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
var title = Arg("--title");
var artist = Arg("--artist") ?? "";
if (title is null || !double.TryParse(Arg("--duration") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
{
    Console.WriteLine("Usage: LyricProbe --title <title> --artist <artist> [--album <album>] --duration <seconds> [--search] [--lines N]");
    return 2;
}
var query = new LyricQuery(title, artist, Arg("--album"), duration);
var search = args.Contains("--search");
var shownLines = int.TryParse(Arg("--lines"), out var n) ? n : 6;

var cache = Path.Combine(Path.GetTempPath(), "lunalyr-lyricprobe", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(cache);
var started = DateTime.Now;
void Log(string message) => Console.WriteLine($"{(DateTime.Now - started).TotalSeconds,6:F2}s  {message}");
try
{
    var service = LyricService.CreateDefault(cache, options: new LyricServiceOptions
    {
        PlatformSources = [new AppleMusicWindowsSource()],
        Readings = WindowsReadingServices.Create(),
        // The agent the development rules ask for: generic, nothing personal.
        Http = new LyricHttpOptions { UserAgent = "Lunalyr/0.1 (Windows port; dev)" },
    });
    var options = LyricSourceSnapshot.From(LyricSourceSnapshot.RecommendedOrder, LyricSourceSnapshot.RecommendedOrder.ToHashSet(),
        LyricMatchMode.AccuracyFirst);
    Console.WriteLine($"Query: \"{query.Title}\" by \"{query.Artist}\" ({LyricQuery.Clock(query.Duration)}){(search ? ", search (every source asked)" : "")}");
    Console.WriteLine("Sources:");
    foreach (var source in service.SourceList(options))
    {
        Console.WriteLine($"  {source.Title,-22} {(source.IsAvailable ? "available" : "unavailable: " + source.UnavailableReason)}");
    }
    var lastRevision = 0;
    var report = await service.LookupAsync(query, options, force: search, onUpdate: update =>
    {
        if (update.Revision != lastRevision)
        {
            lastRevision = update.Revision;
            Log($"showing (revision {update.Revision}): {update.Result?.Provenance.Label ?? "nothing"}");
        }
        return ValueTask.CompletedTask;
    });
    Log($"done{(report.Offline ? " (offline)" : "")}{(report.Cached ? " (cached)" : "")}");
    Console.WriteLine();
    Console.WriteLine("Source states:");
    foreach (var id in options.Enabled)
    {
        Console.WriteLine($"  {id.Title(),-22} {(report.States.TryGetValue(id, out var state) ? state : "(not asked)")}");
    }
    Console.WriteLine();
    Console.WriteLine("Ranking:");
    var rank = 0;
    foreach (var candidate in report.Candidates)
    {
        Console.WriteLine($"  {++rank}. {candidate.Provenance.Label,-45} \"{candidate.Title}\" / \"{candidate.Artist}\""
            + $" {(candidate.Duration is double d ? LyricQuery.Clock(d) : "?:??")}, {candidate.LineCount} lines, score {candidate.Score:F1}");
    }
    foreach (var rejected in report.Rejected)
    {
        Console.WriteLine($"  -  {rejected.Candidate.Provenance.Label,-45} {rejected.Reason}");
    }
    Console.WriteLine();
    switch (report.Result)
    {
        case LyricResult.Synced synced:
            Console.WriteLine($"Chosen: {synced.Provenance.Label}");
            foreach (var line in synced.Lines.Where(line => !line.IsInstrumental).Take(shownLines))
            {
                var words = line.WordTimed ? $"  [{line.Words.Count} timed words]" : "";
                Console.WriteLine($"  {LyricQuery.Clock(line.Start)}  {line.Text}{words}");
                if (line.Romanization is { } reading) Console.WriteLine($"         reading: {reading}");
                if (line.Translation is { } translation) Console.WriteLine($"         translation: {translation}");
            }
            break;
        case LyricResult.Plain plain:
            Console.WriteLine($"Chosen: {plain.Provenance.Label}");
            foreach (var line in plain.Lines.Where(line => line.Length > 0).Take(shownLines)) Console.WriteLine($"  {line}");
            break;
        default:
            Console.WriteLine("Chosen: nothing");
            break;
    }
    return 0;
}
finally
{
    try
    {
        Directory.Delete(cache, recursive: true);
    }
    catch (IOException)
    {
    }
}

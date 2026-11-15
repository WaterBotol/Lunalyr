// Manual live lyric lookup. Usage:
//   LyricProbe --title "Shape of You" --artist "Ed Sheeran" [--album "Divide"] --duration 233.7 [--genre Pop]
//              [--storefront au] [--search] [--lines 6] [--out result.txt]
// --out also writes everything printed to a UTF-8 file (PowerShell redirection can mangle CJK names).
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
    Console.WriteLine("Usage: LyricProbe --title <title> --artist <artist> [--album <album>] --duration <seconds> [--genre <genre>]"
        + " [--storefront <cc>] [--search] [--lines N] [--out <file>]");
    return 2;
}
var query = new LyricQuery(title, artist, Arg("--album"), duration) { Genre = Arg("--genre") };
var storefront = Arg("--storefront");
if (Arg("--out") is { } outPath)
{
    Console.SetOut(new TeeWriter(Console.Out, new StreamWriter(outPath, append: false, new UTF8Encoding(false)) { AutoFlush = true }));
}
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
        Storefront = storefront,
    });
    var options = LyricSourceSnapshot.From(LyricSourceSnapshot.RecommendedOrder, LyricSourceSnapshot.RecommendedOrder.ToHashSet(),
        LyricMatchMode.AccuracyFirst);
    Console.WriteLine($"Query: \"{query.Title}\" by \"{query.Artist}\" ({LyricQuery.Clock(query.Duration)}, {query.Duration.ToString(CultureInfo.InvariantCulture)} s)"
        + $"{(query.Album is { } album ? $", album \"{album}\"" : "")}{(query.Genre is { } genre ? $", genre \"{genre}\"" : "")}"
        + $"{(search ? ", search (every source asked)" : ", automatic lookup")}");
    Console.WriteLine($"Storefront for original titles: {storefront ?? OriginalTitles.DefaultStorefront()}; cache policy {LyricSourceSnapshot.CachePolicy}");
    Console.WriteLine("Sources:");
    foreach (var source in service.SourceList(options))
    {
        Console.WriteLine($"  {source.Title,-22} {(source.IsAvailable ? "available" : "unavailable: " + source.UnavailableReason)}");
    }
    var lastRevision = 0;
    var lastStates = new Dictionary<LyricSourceId, string>();
    var report = await service.LookupAsync(query, options, force: search, onUpdate: update =>
    {
        // Each source's state as it changes, so when sources answer, are stopped and the match shows can be read.
        foreach (var (id, state) in update.States)
        {
            if (!lastStates.TryGetValue(id, out var before) || before != state)
            {
                lastStates[id] = state;
                Log($"  {id.Title()}: {state}");
            }
        }
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
            + $" {(candidate.Duration is double d ? LyricQuery.Clock(d) : "?:??")}, {candidate.LineCount} lines, score {candidate.Score:F1},"
            + $" tier {LyricTimingRules.TimingTier(candidate.Result)}");
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

/// <summary>Writes everything to the console and to a file.</summary>
internal sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Write(string? value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        first.WriteLine(value);
        second.WriteLine(value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            second.Dispose();
        }
        base.Dispose(disposing);
    }
}

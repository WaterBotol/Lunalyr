// Invented songs for the opening title (the Mac's OpeningTitlePreviewTests songs: the same titles, artists, albums and
// first-line times), with made-up covers painted in code, the gradient's palette taken from each cover as the app takes
// it, and the Japanese title's reading worked out by the Windows readings.

using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Platform.Imaging;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Demo;
using static Lunalyr.Shell.Demo.LyricBuilder;

namespace Lunalyr.Tools.CompositionPreview;

public static class OpeningFixtures
{
    /// <summary>One song's opening: its scene and the moments worth a look.</summary>
    public sealed record Song(string Name, SceneSnapshot Scene, IReadOnlyList<(string Label, double Time)> Moments, double Joined = 0);

    private static readonly Lazy<(CoverArt Art, Palette Palette)> Warm = new(() => Cover(
        DemoCover.Paint(new(242, 128, 89), new(196, 90, 120), new(120, 60, 130), new(64, 38, 115), new(255, 214, 160))));

    private static readonly Lazy<(CoverArt Art, Palette Palette)> Night = new(() => Cover(
        DemoCover.Paint(new(30, 42, 112), new(84, 52, 160), new(16, 20, 60), new(200, 70, 120), new(250, 200, 120))));

    /// <summary>A painted cover as the app reads one: its colours for the gradient, and the cover the card draws.</summary>
    public static (CoverArt Art, Palette Palette) Cover(byte[] bytes)
    {
        var pixels = ArtworkDecoder.Decode(bytes) ?? throw new InvalidDataException("The painted cover could not be read.");
        var art = ArtworkDecoder.DecodeCover(bytes) ?? throw new InvalidDataException("The painted cover could not be read.");
        return (art, PaletteMapper.FromAlbum(new AlbumWallpaperPalette(AlbumColourGrid.Extract(pixels))));
    }

    private static LyricResult.Synced Lines(params (string Text, double Start)[] lines)
    {
        var payload = Ttml("en", [.. lines.Select(line => Even(line.Start, 0.42, 0.38, line.Text.Split(' ')))]);
        var synced = (LyricResult.Synced)Parse(LyricFormat.Ttml, payload, 240);
        var prepared = LyricPauseTiming.Prepare(synced.Lines, synced.Provenance.Source, treatLongPausesAsInstrumental: true);
        return new LyricResult.Synced(prepared.Lines, synced.Provenance);
    }

    private static SceneSnapshot Scene(string title, string artist, string album, LyricResult? lyrics, (CoverArt Art, Palette Palette)? cover,
                                       bool coverOn = true, bool reduceMotion = false, string? reading = null) => new()
    {
        Title = title,
        Artist = artist,
        Album = album,
        TrackKey = title,
        Lyrics = lyrics,
        Palette = cover?.Palette ?? PaletteMapper.Fallback,
        Cover = cover?.Art,
        TitleReading = reading,
        ReduceMotion = reduceMotion,
        Style = LyricStyle.Default with { OpeningTitle = true, OpeningTitleCover = coverOn, Credits = true },
    };

    /// <summary>The moments through a card: entering (a fifth and half way), holding, half way out, and once the first line has begun.</summary>
    public static IReadOnlyList<(string, double)> CardMoments(SceneSnapshot scene, double joined = 0)
    {
        var lines = (scene.Lyrics as LyricResult.Synced)?.Lines ?? [];
        var first = OpeningTitle.FirstSung(lines);
        var words = scene.Lyrics is LyricResult.Synced ? OpeningWords.Timed(first) : OpeningWords.None;
        if (OpeningTitle.Schedule(words, joined, new Lunalyr.Core.Motion.WordRevealOptions().PreviewLead) is not { } card)
            return [("0-no-card", 1.2), .. first is { } f ? new[] { ("5-first-line", f + 0.6) } : []];
        var entrance = card.Settled - card.Start;
        List<(string, double)> moments =
        [
            ("1-entering", card.Start + entrance * 0.2), ("2-entering", card.Start + entrance * 0.5),
            ("3-holding", (card.Settled + card.Leave) / 2), ("4-leaving", (card.Leave + card.End) / 2),
        ];
        if (first is { } line) moments.Add(("5-first-line", line + 0.6));
        return moments;
    }

    private static string? JapaneseReading(string title)
    {
        var services = WindowsReadingServices.Create(Path.Combine(Path.GetTempPath(), "lunalyr-previews", "dictionaries"));
        return new OpeningTitleMemory().Reading(title, RomanizationOptions.Default, new LyricRomanizer(services));
    }

    /// <summary>
    /// "Human Nature" by Yot Club from "Rare Hymns", first sung at 11 s (the full card, gone by 9.25 s), for tests: with
    /// the warm cover or none, "Show the album cover" on or off, and a production credit at 0.5 s if asked.
    /// </summary>
    public static SceneSnapshot HumanNature(bool cover = true, bool coverOn = true, bool credits = false)
    {
        var lines = Lines(("Always felt like you were gonna stick around", 11.0), ("Now the summer's over and I'm watching you go", 14.6));
        LyricResult lyrics = !credits ? lines : new LyricResult.Synced(
            [new LyricLine { Start = 0.5, End = 0.51, Text = "Produced by: Mara Lind", IsInstrumental = true, Credit = true }, .. lines.Lines],
            lines.Provenance);
        var scene = Scene("Human Nature", "Yot Club", "Rare Hymns", lyrics, cover ? Warm.Value : null, coverOn);
        return credits ? scene : scene with { Style = scene.Style with { Credits = false } };
    }

    public static IReadOnlyList<Song> Songs()
    {
        var standard = Lines(("Always felt like you were gonna stick around", 11.0), ("Now the summer's over and I'm watching you go", 14.6));
        var withCredits = new LyricResult.Synced(
            [new LyricLine { Start = 0.5, End = 0.51, Text = "Produced by: Mara Lind", IsInstrumental = true, Credit = true }, .. standard.Lines],
            standard.Provenance);
        var songs = new List<Song>();
        void Add(string name, SceneSnapshot scene, IReadOnlyList<(string, double)>? moments = null, double joined = 0) =>
            songs.Add(new Song(name, scene, moments ?? CardMoments(scene, joined), joined));

        Add("a-cover", Scene("Human Nature", "Yot Club", "Rare Hymns", withCredits, Warm.Value));
        Add("b-long", Scene("Everything You Said Before the Summer Ended (Midnight Version)", "Clara Vey & The Lanterns", "Paper Moons",
                            Lines(("Tell me what you said before the light went out", 13.0)), Night.Value));
        const string yoru = "夜に駆ける";
        Add("c-japanese", Scene(yoru, "YOASOBI", "THE BOOK", Lines(("沈む ように 溶けて ゆく ように", 9.0)), Night.Value,
                                reading: JapaneseReading(yoru)));
        Add("d-first-line-soon", Scene("Fast Start", "Kid Mono", "Fast Start - Single",
                                       Lines(("Straight in, no warning, we're already gone", 3.9)), Warm.Value));
        var reduced = Scene("Human Nature", "Yot Club", "Rare Hymns", standard, Warm.Value, reduceMotion: true);
        Add("e-reduce-motion", reduced, [("1-entering", 0.9), ("3-holding", 5.0), ("4-leaving", 8.6)]);
        var held = CardMoments(reduced).Where(moment => moment.Item1 == "3-holding").ToList();
        Add("g-cover-off", Scene("Human Nature", "Yot Club", "Rare Hymns", standard, Warm.Value, coverOn: false), held);
        Add("h-no-artwork", Scene("Human Nature", "Yot Club", "Rare Hymns", standard, null), held);
        Add("i-no-lyrics", Scene("Human Nature", "Yot Club", "Rare Hymns", null, Warm.Value) with { Status = "No lyrics found for this song" });
        Add("j-joined-part-way", Scene("Human Nature", "Yot Club", "Rare Hymns", standard, Warm.Value), joined: 95);
        return songs;
    }
}

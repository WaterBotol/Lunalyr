// What a lyric card is made from, and how it is built from the song on the wallpaper: the Mac's
// LyricCard.currentContent and LyricCardSource.live (LyricCard.swift, LyricCardPopout.swift, Lunalyr's own code).

using Lunalyr.Core.Cards;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Media;
using Lunalyr.Render.Cards;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Cards;

/// <summary>One page's readings as the wallpaper has them: word by word (for "below"), and the whole line.</summary>
public sealed record CardPageReadings(IReadOnlyList<LyricWord>? Words, string? Line);

/// <summary>A card to make: what it shows (<see cref="Content"/>), how it is coloured, each page's readings and its background.</summary>
public sealed record LyricCardJob
{
    public required LyricCardContent Content { get; init; }
    public required CardLook Look { get; init; }

    /// <summary>Readings per page, parallel to <see cref="LyricCardContent.Pages"/> (missing entries have none).</summary>
    public IReadOnlyList<CardPageReadings> Readings { get; init; } = [];

    /// <summary>The picture, GIF or video behind the card (its info is <see cref="LyricCardContent.Background"/>).</summary>
    public CardBackgroundSource? Background { get; init; }

    /// <summary>The same card with <paramref name="background"/> behind it (its colours too), or the album look when null.</summary>
    public LyricCardJob WithBackground(CardBackgroundSource? background, Palette? backgroundPalette) => this with
    {
        Background = background,
        Content = Content with { Background = background?.Info },
        Look = backgroundPalette is null ? Look : Look with { Palette = CardLook.ForCard(backgroundPalette, Look.Cover, Look.Style).Palette },
    };
}

/// <summary>A row of the card window's lyrics: the line's index in the song and its text.</summary>
public sealed record CardRow(int Id, string Text);

/// <summary>Builds cards from the scene on the wallpaper.</summary>
public static class LyricCardBuilder
{
    /// <summary>The rows to pick from: the vocal lines of synced lyrics, or the non-empty lines of plain ones.</summary>
    public static IReadOnlyList<CardRow> Rows(SceneSnapshot scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        switch (scene.Lyrics)
        {
            case LyricResult.Synced synced:
                return [.. synced.Lines.Select((line, index) => (line, index))
                    .Where(p => !p.line.IsInstrumental && p.line.Text.Length > 0)
                    .Select(p => new CardRow(p.index, p.line.Text))];
            case LyricResult.Plain plain:
                // The same filtered list Build indexes.
                return [.. plain.Lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select((text, index) => new CardRow(index, text))];
            default:
                return [];
        }
    }

    /// <summary>The row being sung at <paramref name="lyricTime"/> (the wallpaper's lyric clock), if any.</summary>
    public static int? ActiveRow(SceneSnapshot scene, double lyricTime)
    {
        if (scene.Lyrics is not LyricResult.Synced synced || !double.IsFinite(lyricTime)) return null;
        int? active = null;
        for (var i = 0; i < synced.Lines.Count; i++)
        {
            if (synced.Lines[i].Start > lyricTime) break;
            active = i;
        }
        return active;
    }

    /// <summary>
    /// A card of <paramref name="rows"/> (line indices, as <see cref="Rows"/> gives them) from <paramref name="scene"/>:
    /// each timed line drawn by <paramref name="composition"/>, untimed ones as text; a song without lines gives one
    /// empty page (the song alone). <paramref name="composition"/> is the card's layout or the wallpaper's.
    /// </summary>
    public static LyricCardJob Build(SceneSnapshot scene, IReadOnlyList<int> rows, string composition, CardAspect aspect,
                                     LyricStyle style, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var kind = LyricCardLayout.Composition(composition) ?? CompositionKind.Editorial;
        var display = style.Readings switch
        {
            ReadingPlacement.Instead => RomanizationDisplay.Replace,
            ReadingPlacement.Below => RomanizationDisplay.Below,
            _ => RomanizationDisplay.Off,
        };
        var pages = new List<CardPage>();
        var readings = new List<CardPageReadings>();
        if (scene.Lyrics is LyricResult.Synced synced)
        {
            foreach (var index in rows.Where(i => i >= 0 && i < synced.Lines.Count).Order())
            {
                var line = synced.Lines[index];
                scene.WordReadings.TryGetValue(index, out var wordReadings);
                scene.Readings.TryGetValue(index, out var lineReading);
                var words = Words(line, display == RomanizationDisplay.Replace ? wordReadings : null);
                readings.Add(new CardPageReadings(display == RomanizationDisplay.Off ? null : wordReadings,
                    display == RomanizationDisplay.Off ? null : lineReading));
                if (line.Words.Count == 0)
                {
                    pages.Add(new CardPage(words));
                    continue;
                }
                pages.Add(new CardPage(words, CardAnimation.Create(line, kind, index, wordReadings, display)));
            }
        }
        if (pages.Count == 0 && scene.Lyrics is LyricResult.Plain plain)
        {
            var lines = plain.Lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            foreach (var index in rows.Where(i => i >= 0 && i < lines.Count).Order())
            {
                pages.Add(new CardPage(lines[index].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
                readings.Add(new CardPageReadings(null, null));
            }
        }
        if (pages.Count == 0) pages.Add(new CardPage([]));
        var content = new LyricCardContent
        {
            Pages = pages, Title = scene.Title ?? "", Artist = scene.Artist ?? "", HasArtwork = scene.Cover is not null,
            Aspect = aspect, ReduceMotion = reduceMotion,
        };
        return new LyricCardJob { Content = content, Look = CardLook.ForCard(scene.Palette, scene.Cover, style), Readings = readings };
    }

    /// <summary>
    /// A line's words as the wallpaper sets them: syllables and characters that run together in the lyric are one word
    /// (沈むように, not 沈 む よ う に), and with readings shown instead, the readings.
    /// </summary>
    public static IReadOnlyList<string> Words(LyricLine line, IReadOnlyList<LyricWord>? readings)
    {
        var shown = readings is { Count: > 0 } ? readings : line.Words;
        var words = LyricRuns.Labels(shown, LyricRuns.Glue(line, shown));
        return words.Count == 0 ? line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries) : words;
    }
}

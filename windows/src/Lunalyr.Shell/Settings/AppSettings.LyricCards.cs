// Lyric cards' settings (milestone 7): the Mac's LunalyrExtras keys (custom.lyricCardComposition,
// custom.lyricCardLinesBefore/After) and LyricCardPopoutModel's (custom.lyricCardAspect, …Formats, …BackgroundPath,
// …UseSong, …TextScale), without "custom.". Read through the clamping accessors below, so a hand-edited value out of
// range reads as the default without this file touching Normalized().

using System.Text.Json.Serialization;
using Lunalyr.Core.Cards;

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    public const double DefaultLyricCardTextScale = 0.85, LyricCardTextScaleMin = 0.5, LyricCardTextScaleMax = 1.1;

    /// <summary>The card's layout: a composition id, or "" to follow the wallpaper (Mac <c>custom.lyricCardComposition</c>).</summary>
    public string LyricCardComposition { get; init; } = "";

    /// <summary>Lines before the one sung that a quick card takes, 0–5 (Mac <c>custom.lyricCardLinesBefore</c>).</summary>
    public int LyricCardLinesBefore { get; init; }

    /// <summary>Lines after it, 0–5 (Mac <c>custom.lyricCardLinesAfter</c>).</summary>
    public int LyricCardLinesAfter { get; init; }

    /// <summary>The card's shape: portrait, square, story or landscape (Mac <c>custom.lyricCardAspect</c>).</summary>
    public string LyricCardAspect { get; init; } = "portrait";

    /// <summary>What a card is made as: any of mp4, gif, png (Mac <c>custom.lyricCardFormats</c>; empty reads as MP4 and PNG).</summary>
    public IReadOnlyList<string> LyricCardFormats { get; init; } = ["mp4", "png"];

    /// <summary>The picture, GIF or video last chosen to sit behind the card, or "" (Mac <c>custom.lyricCardBackgroundPath</c>).</summary>
    public string LyricCardBackgroundPath { get; init; } = "";

    /// <summary>The song's own sound goes under the MP4 (Mac <c>custom.lyricCardUseSong</c>).</summary>
    public bool LyricCardUseSong { get; init; }

    /// <summary>How big the lyrics are on the card, 0.5–1.1 (Mac <c>custom.lyricCardTextScale</c>).</summary>
    public double LyricCardTextScale { get; init; } = DefaultLyricCardTextScale;

    /// <summary>The card's composition id, or null to follow the wallpaper.</summary>
    [JsonIgnore]
    public string? CardComposition =>
        LyricCardLayout.Composition(MigratedLayout(LyricCardComposition)) is { } kind ? Core.Compositions.CompositionTraits.Id(kind) : null;

    [JsonIgnore]
    public CardAspect CardAspect => CardAspects.Parse(LyricCardAspect) ?? Core.Cards.CardAspect.Portrait;

    [JsonIgnore]
    public IReadOnlySet<CardFormat> CardFormats
    {
        get
        {
            var formats = (LyricCardFormats ?? []).Select(Core.Cards.CardFormats.Parse).OfType<CardFormat>().ToHashSet();
            return formats.Count > 0 ? formats : new HashSet<CardFormat> { CardFormat.Mp4, CardFormat.Png };
        }
    }

    [JsonIgnore]
    public double CardTextScale => double.IsFinite(LyricCardTextScale)
        ? Math.Clamp(LyricCardTextScale, LyricCardTextScaleMin, LyricCardTextScaleMax)
        : DefaultLyricCardTextScale;

    [JsonIgnore]
    public int CardLinesBefore => LyricCardLayout.ClampedLineCount(LyricCardLinesBefore);

    [JsonIgnore]
    public int CardLinesAfter => LyricCardLayout.ClampedLineCount(LyricCardLinesAfter);

    /// <summary>The formats as stored: the Mac's raw values, sorted.</summary>
    public static IReadOnlyList<string> StoredCardFormats(IEnumerable<CardFormat> formats) =>
        [.. formats.Select(f => f.Raw()).Distinct().Order(StringComparer.Ordinal)];
}

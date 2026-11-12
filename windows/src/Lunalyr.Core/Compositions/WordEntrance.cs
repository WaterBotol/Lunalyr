// Word entrances. The original entrance list, its titles and "Match the style" are re-implemented from a description
// of the Mac's behaviour (LyricComposition.swift traces back to the squashed Verso import; see
// docs/compositions/shared.md and scattered.md). Lunalyr's own parts are ported as they are: the cutout, highlight,
// swipe and mix entrances and their properties (WordArrival in LyricComposition.swift), the transform table and paper
// mask (WordEntrance in ScatteredLyricText.swift), the swipe ink (LyricSwipe.swift) and the mix pick
// (Settings.arrival(in:for:)).

using Lunalyr.Core.Colour;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>The lyric compositions built on Windows so far.</summary>
public enum CompositionKind
{
    Editorial,
    Spoken,
    Scattered,
    Constellation,
    ConstellationEditorial,
    Echo,
}

/// <summary>How a word makes its entrance (the Mac's <c>WordArrival</c>; raw ids in <see cref="WordArrivals.Id"/>).</summary>
public enum WordArrival
{
    /// <summary>"Match the style": the composition's own entrance.</summary>
    Automatic,
    /// <summary>"Soft focus": fades in out of a blur.</summary>
    Fade,
    /// <summary>"Float up".</summary>
    Lift,
    Pop,
    Orbit,
    Glide,
    Unfold,
    /// <summary>"Cutout · Fold": a paper cutout folding up, unmasked from its leading edge.</summary>
    CutoutFold,
    /// <summary>"Cutout · Slide": a paper cutout sliding in with a turn.</summary>
    CutoutSlide,
    /// <summary>"Cutout · Highlight": the layout's own entrance, with a cutout tile pressed in behind each sung word.</summary>
    CutoutHighlight,
    /// <summary>"Satin Ribbon": a soft band of colour sweeps across the word as it arrives.</summary>
    SatinRibbon,
    /// <summary>"Split Ink": two bars of ink slide past each other across the word as it arrives.</summary>
    SplitInk,
    /// <summary>"Custom mix": each word takes one of the mix's entrances.</summary>
    Mix,
}

public static class WordArrivals
{
    private static readonly string[] Ids =
    [
        "automatic", "fade", "lift", "pop", "orbit", "glide", "unfold", "cutoutFold", "cutoutSlide", "cutoutHighlight",
        "satinRibbon", "splitInk", "mix",
    ];

    /// <summary>Every entrance but "Match the style" and the mix itself, in the Mac's order: what a mix can hold.</summary>
    public static IReadOnlyList<WordArrival> MixChoices { get; } =
    [
        WordArrival.Fade, WordArrival.Lift, WordArrival.Pop, WordArrival.Orbit, WordArrival.Glide, WordArrival.Unfold,
        WordArrival.CutoutFold, WordArrival.CutoutSlide, WordArrival.CutoutHighlight, WordArrival.SatinRibbon,
        WordArrival.SplitInk,
    ];

    /// <summary>The Mac's default mix: Soft focus, Float up and Cutout · Fold.</summary>
    public static IReadOnlyList<WordArrival> DefaultMix { get; } = [WordArrival.Fade, WordArrival.Lift, WordArrival.CutoutFold];

    /// <summary>The Mac's raw value (<c>cutoutFold</c>, <c>satinRibbon</c>…).</summary>
    public static string Id(this WordArrival arrival) => Ids[(int)arrival];

    public static string Title(this WordArrival arrival) => arrival switch
    {
        WordArrival.Automatic => "Match the style",
        WordArrival.Fade => "Soft focus",
        WordArrival.Lift => "Float up",
        WordArrival.Pop => "Pop",
        WordArrival.Orbit => "Orbit",
        WordArrival.Glide => "Glide",
        WordArrival.Unfold => "Unfold",
        WordArrival.CutoutFold => "Cutout · Fold",
        WordArrival.CutoutSlide => "Cutout · Slide",
        WordArrival.CutoutHighlight => "Cutout · Highlight",
        WordArrival.SatinRibbon => "Satin Ribbon",
        WordArrival.SplitInk => "Split Ink",
        _ => "Custom mix",
    };

    /// <summary>
    /// A <c>LyricStyle.Reveal</c> id: the Mac's raw values (any case), the spelled-out titles (<c>softFocus</c>,
    /// <c>floatUp</c>, <c>matchStyle</c>), or <c>mix</c> (optionally <c>mix:pop,orbit,…</c>, see
    /// <see cref="EntranceChoice"/>). Unknown ids are Soft focus.
    /// </summary>
    public static WordArrival Parse(string? id) => Known(id) ?? WordArrival.Fade;

    /// <summary>The entrance an id names, or null when it names none.</summary>
    private static WordArrival? Known(string? id)
    {
        var key = id?.Trim().ToLowerInvariant();
        if (key is not null && key.StartsWith("mix:", StringComparison.Ordinal)) return WordArrival.Mix;
        return key switch
        {
            "automatic" or "matchstyle" or "match" or "" or null => WordArrival.Automatic,
            "fade" or "softfocus" or "soft focus" => WordArrival.Fade,
            "lift" or "floatup" or "float up" => WordArrival.Lift,
            "pop" => WordArrival.Pop,
            "orbit" => WordArrival.Orbit,
            "glide" => WordArrival.Glide,
            "unfold" => WordArrival.Unfold,
            "cutoutfold" or "cutout fold" => WordArrival.CutoutFold,
            "cutoutslide" or "cutout slide" => WordArrival.CutoutSlide,
            "cutouthighlight" or "cutout highlight" or "highlight" => WordArrival.CutoutHighlight,
            "satinribbon" or "satin ribbon" => WordArrival.SatinRibbon,
            "splitink" or "split ink" => WordArrival.SplitInk,
            "mix" or "custommix" or "custom mix" => WordArrival.Mix,
            _ => null,
        };
    }

    internal static WordArrival? KnownChoice(string id) => Known(id);

    /// <summary>A paper cutout (Fold, Slide): unmasked from its leading edge, following the reveal.</summary>
    public static bool IsCutout(this WordArrival arrival) => arrival is WordArrival.CutoutFold or WordArrival.CutoutSlide;

    /// <summary>"Cutout · Highlight": words enter as the layout has them enter; each sung word gets a tile.</summary>
    public static bool HighlightsSungWords(this WordArrival arrival) => arrival == WordArrival.CutoutHighlight;

    /// <summary>Satin Ribbon and Split Ink: decorative ink crosses the word as it arrives.</summary>
    public static bool IsSwipe(this WordArrival arrival) => arrival is WordArrival.SatinRibbon or WordArrival.SplitInk;

    /// <summary>Entrances that unveil each word as it is sung, so words must be revealed one by one.</summary>
    public static bool RequiresReveal(this WordArrival arrival) => arrival.IsCutout() || arrival.IsSwipe();

    /// <summary>The largest turn the entrance makes, degrees.</summary>
    public static double TravelRotation(this WordArrival arrival) =>
        arrival is WordArrival.Orbit or WordArrival.CutoutSlide ? 12 : 0;

    /// <summary>"Match the style" resolved for a composition.</summary>
    public static WordArrival Resolved(this WordArrival arrival, CompositionKind composition) =>
        arrival != WordArrival.Automatic ? arrival : composition switch
        {
            CompositionKind.Scattered => WordArrival.Pop,
            CompositionKind.Constellation => WordArrival.Orbit,
            CompositionKind.ConstellationEditorial or CompositionKind.Echo => WordArrival.Lift,
            CompositionKind.Spoken => WordArrival.Glide,
            _ => WordArrival.Fade,
        };

    /// <summary>The entrance a word makes: "Cutout · Highlight" keeps the layout's own; every other is its own.</summary>
    public static WordArrival Motion(this WordArrival arrival, CompositionKind composition) =>
        arrival.HighlightsSungWords() ? WordArrival.Automatic.Resolved(composition) : arrival;

    /// <summary>The entrance's blur at <paramref name="progress"/>, in points: 3.5 for Soft focus, 2.5 otherwise.</summary>
    public static double Blur(this WordArrival arrival, double progress, bool reduceMotion = false) =>
        reduceMotion ? 0 : Math.Max(0, 1 - progress) * (arrival == WordArrival.Fade ? 3.5 : 2.5);

    /// <summary>
    /// The largest travel an entrance makes, for room that must be kept around a word: width and height in the same
    /// units as <paramref name="pointSize"/>.
    /// </summary>
    public static SizeD TravelBounds(this WordArrival arrival, double pointSize, bool active = true)
    {
        if (!active) return SizeD.Zero;
        var horizontal = arrival == WordArrival.Orbit ? 0.32 : arrival is WordArrival.Glide or WordArrival.CutoutSlide ? 0.72 : 0;
        var vertical = arrival == WordArrival.Lift ? 0.48 : arrival is WordArrival.Glide or WordArrival.Orbit ? 0.20 : 0;
        return new SizeD(horizontal * pointSize * 2, vertical * pointSize * 2);
    }

    /// <summary>
    /// How much of a cutout's width shows while it arrives, from its leading edge: <c>0.88 + 0.12 × eased</c>; 1 for
    /// every other entrance and once it has arrived.
    /// </summary>
    public static double PaperShown(this WordArrival arrival, double eased) =>
        arrival.IsCutout() && eased < 1 ? 0.88 + 0.12 * eased : 1;
}

/// <summary>
/// The chosen entrance as the style gives it: one entrance, "Match the style", or a custom mix and the entrances in it.
/// </summary>
/// <param name="Selected">The choice.</param>
/// <param name="MixEntrances">For <see cref="WordArrival.Mix"/>: the entrances in the mix, in <see cref="WordArrivals.MixChoices"/> order.</param>
public readonly record struct EntranceChoice(WordArrival Selected, IReadOnlyList<WordArrival>? MixEntrances = null)
{
    /// <summary>
    /// A <c>LyricStyle.Reveal</c> id. <c>mix</c> holds the Mac's default mix (Soft focus, Float up, Cutout · Fold);
    /// <c>mix:pop,orbit,satinRibbon</c> names its entrances (unknown names are skipped).
    /// </summary>
    public static EntranceChoice Parse(string? id)
    {
        var selected = WordArrivals.Parse(id);
        if (selected != WordArrival.Mix) return new EntranceChoice(selected);
        var colon = id!.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0) return new EntranceChoice(selected, WordArrivals.DefaultMix);
        var named = new HashSet<WordArrival>();
        foreach (var part in id[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (WordArrivals.KnownChoice(part) is { } arrival && arrival is not (WordArrival.Automatic or WordArrival.Mix))
                named.Add(arrival);
        }
        return new EntranceChoice(selected, [.. WordArrivals.MixChoices.Where(named.Contains)]);
    }

    /// <summary>
    /// The entrance a word with this visual seed is given in <paramref name="composition"/> (the Mac's
    /// <c>Settings.arrival(in:for:)</c>): the choice resolved for the composition, or for a mix the entrance at the
    /// seed modulo the mix's size (the layout's own when the mix is empty). "Cutout · Highlight" is returned as such:
    /// <see cref="WordArrivals.Motion"/> gives the entrance it moves with.
    /// </summary>
    public WordArrival For(CompositionKind composition, ulong seed)
    {
        if (Selected != WordArrival.Mix) return Selected.Resolved(composition);
        var options = MixEntrances ?? WordArrivals.DefaultMix;
        if (options.Count == 0) return WordArrival.Automatic.Resolved(composition);
        return options[(int)(seed % (ulong)options.Count)];
    }
}

/// <summary>Where an entrance has a word on this frame.</summary>
/// <param name="OffsetX">Travel still to cover, in the units of the word's size.</param>
/// <param name="OffsetY">Travel still to cover (positive is down).</param>
/// <param name="Turn">Turn still to unwind, degrees (signed).</param>
/// <param name="Scale">Entrance scale.</param>
/// <param name="Fold">Fold about the bottom edge, degrees (negative leans back).</param>
/// <param name="Eased">The 0…1 value the transforms follow.</param>
public readonly record struct EntranceTransform(double OffsetX, double OffsetY, double Turn, double Scale, double Fold, double Eased)
{
    public static EntranceTransform Rest => new(0, 0, 0, 1, 0, 1);

    /// <summary>
    /// The entrance a word is drawn with. Travel, turn and scale follow the word's spring (<paramref name="arrival"/>,
    /// which may overshoot), or an eased <paramref name="progress"/> without one; cutouts and swipes follow the
    /// monotone <paramref name="progress"/> itself. Shared with the layouts that keep room for it, so the room and the
    /// motion never disagree.
    /// </summary>
    public static EntranceTransform Make(double progress, double? arrival, WordArrival style, double size, double direction,
                                         bool reduceMotion, double maxTurn = 12)
    {
        if (reduceMotion) return Rest;
        var paper = style.IsCutout();
        var eased = paper || style.IsSwipe() ? progress : arrival ?? RollingMotion.MotionEase(progress);
        var remaining = 1 - eased;
        var travelsSideways = style is WordArrival.Orbit or WordArrival.Glide or WordArrival.CutoutSlide;
        var travelsDown = style is WordArrival.Lift or WordArrival.Glide or WordArrival.Orbit;
        return new EntranceTransform(
            OffsetX: travelsSideways ? remaining * direction * size * (style == WordArrival.Orbit ? 0.32 : 0.72) : 0,
            OffsetY: travelsDown ? remaining * size * (style == WordArrival.Lift ? 0.48 : 0.20) : 0,
            Turn: style is WordArrival.Orbit or WordArrival.CutoutSlide ? remaining * direction * Math.Min(12, Math.Max(0, maxTurn)) : 0,
            Scale: style == WordArrival.Pop || paper ? 0.76 + 0.24 * eased : 1,
            Fold: style is WordArrival.Unfold or WordArrival.CutoutFold ? (paper ? -30 : -48) * remaining : 0,
            Eased: eased);
    }
}

/// <summary>A rectangle in a word's box (top-left origin), turned about its own centre by <paramref name="Turn"/> degrees.</summary>
public readonly record struct SwipeBar(double X, double Y, double Width, double Height, double Turn = 0);

/// <summary>
/// The decorative ink Satin Ribbon and Split Ink draw over a word while it arrives (the Mac's <c>LyricSwipe</c>):
/// where its pieces sit in the word's box, and how strongly it shows. The ink is clipped to the box.
/// </summary>
public static class LyricSwipe
{
    /// <summary>Drawn only while the word arrives, and not under Reduce Motion.</summary>
    public static bool Shows(WordArrival style, double progress, bool reduceMotion) =>
        style.IsSwipe() && !reduceMotion && progress > 0 && progress < 1;

    /// <summary>The whole swipe's opacity: <c>sin(π × progress)</c>.</summary>
    public static double Opacity(double progress) => Math.Sin(progress * Math.PI);

    /// <summary>How far across the box the swipe has run (it starts and ends outside it).</summary>
    public static double Phase(double progress) => progress * 1.74 - 0.37;

    /// <summary>Satin Ribbon's band in a box of <paramref name="width"/> × <paramref name="height"/>, turned −13°.</summary>
    public static SwipeBar Ribbon(double progress, double direction, double width, double height)
    {
        var phase = Phase(progress);
        double w = width * 0.42, h = height * 1.35;
        var centreX = width * (direction >= 0 ? phase : 1 - phase);
        return new SwipeBar(centreX - w / 2, height / 2 - h / 2, w, h, -13);
    }

    /// <summary>
    /// Split Ink's bars: the top bar (ink at 68%) with its highlight edge (highlight at 88%) on its right, and the bottom
    /// bar (ink at 52%), sliding opposite ways.
    /// </summary>
    public static (SwipeBar Top, SwipeBar Edge, SwipeBar Bottom) Split(double progress, double direction, double width, double height)
    {
        var move = width * (Phase(progress) - 0.5) * direction;
        double topWidth = width * 0.53, barHeight = height * 0.48;
        var edge = Math.Min(topWidth, Math.Max(1, width * 0.06));
        var topLeft = width / 2 + move - topWidth / 2;
        var y = height * 0.25 - barHeight / 2;
        var top = new SwipeBar(topLeft, y, topWidth - edge, barHeight);
        var edgeBar = new SwipeBar(topLeft + topWidth - edge, y, edge, barHeight);
        var bottomWidth = width * 0.42;
        var bottom = new SwipeBar(width / 2 - move - bottomWidth / 2, height * 0.75 - barHeight / 2, bottomWidth, barHeight);
        return (top, edgeBar, bottom);
    }
}

/// <summary>
/// The swipe's colours, from the album's accent (hue and saturation): a ribbon, an ink and a highlight (the Mac's
/// <c>LyricSwipePalette</c>). Colours are sRGB.
/// </summary>
public readonly record struct LyricSwipePalette(Double3 Ribbon, Double3 Ink, Double3 Highlight)
{
    /// <summary>The palette before any album is known (hue 0.64, saturation 0.55).</summary>
    public static LyricSwipePalette Default { get; } = From(0.64, 0.55);

    public static LyricSwipePalette From(double hue, double saturation)
    {
        var strength = Math.Min(0.72, Math.Max(0.24, double.IsFinite(saturation) ? saturation : 0.55));
        if (!double.IsFinite(hue)) hue = 0.64;
        static double Turn(double value)
        {
            var t = value % 1;
            return t < 0 ? t + 1 : t;
        }
        return new LyricSwipePalette(
            ColourSpaces.Rgb(new Double3(Turn(hue + 0.10), strength * 0.6, 1)),
            ColourSpaces.Rgb(new Double3(Turn(hue + 0.48), strength, 0.76)),
            ColourSpaces.Rgb(new Double3(Turn(hue + 0.42), strength * 0.5, 1)));
    }

    /// <summary>
    /// The palette for a cover's nine colours (gamma-encoded sRGB, row by row): its most vivid sampled colour's hue and
    /// saturation (<see cref="AlbumColourGrid.Accent"/>; the fallback accent when there are none).
    /// </summary>
    public static LyricSwipePalette ForCover(IReadOnlyList<Double3> rgbStops)
    {
        ArgumentNullException.ThrowIfNull(rgbStops);
        var hsb = new Double3[rgbStops.Count];
        for (var i = 0; i < hsb.Length; i++) hsb[i] = AlbumColourGrid.Hsb(rgbStops[i]);
        var accent = AlbumColourGrid.Accent(hsb);
        return From(accent.X, accent.Y);
    }
}

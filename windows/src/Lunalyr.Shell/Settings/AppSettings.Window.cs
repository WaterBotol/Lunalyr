// Settings added with the settings window (milestone 5). The keys and defaults are the Mac's UserDefaults names and
// values. The Mac's per-layout entrance lookup (Settings.arrival(in:), on lines that trace back to the squashed Verso
// import) is re-implemented here from its behaviour: with Match the music, an entrance saved under "automatic" applies
// to every layout it picks; otherwise a layout's own entry, else the one entrance for all. The custom mix
// (custom.wordEntranceMix) and the sliders' ranges (LyricStylePage) are Lunalyr's own and are ported as they are.

using Lunalyr.Core.Compositions;
using Lunalyr.Render.Scene;
using Arrivals = Lunalyr.Core.Compositions.WordArrivals;

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    /// <summary>The key "Match the music"'s own entrance is saved under in <see cref="WordArrivalByStyle"/>.</summary>
    public const string AllLayoutsKey = "automatic";

    /// <summary>"Spread": how far Constellation and Constellation + Editorial throw their words apart, 0.4–1.8 (Mac <c>constellationSpread</c>).</summary>
    public double ConstellationSpread { get; init; } = ConstellationLayout.DefaultSpread;

    /// <summary>"Links": the constellations' hairlines, 0–0.6 (Mac <c>constellationLinkOpacity</c>).</summary>
    public double ConstellationLinkOpacity { get; init; } = ConstellationLayout.DefaultLinkOpacity;

    /// <summary>"Repeats": how present Echo's repeats are, 0–1.8 (Mac <c>echoStrength</c>).</summary>
    public double EchoStrength { get; init; } = EchoBackdrop.DefaultStrength;

    /// <summary>The custom mix's entrances, by raw value (Mac <c>custom.wordEntranceMix</c>; default Soft focus, Float up, Cutout · Fold).</summary>
    public IReadOnlyList<string> WordEntranceMix { get; init; } = [.. Arrivals.DefaultMix.Select(a => a.Id())];

    /// <summary>
    /// An entrance per layout (Mac <c>custom.wordArrivalByStyle</c>): composition id → <see cref="WordArrival"/> raw
    /// value, and "automatic" for Match the music's own. A layout without an entry uses <see cref="WordArrival"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> WordArrivalByStyle { get; init; } = new Dictionary<string, string>();

    /// <summary>How Lunalyr's own windows look: "system" (follow Windows), "light" or "dark". Windows only; never changes Windows' own setting.</summary>
    public string AppTheme { get; init; } = "system";

    public static IReadOnlyList<string> AppThemes { get; } = ["system", "light", "dark"];

    /// <summary>The slider ranges, from the Mac's Lyric style page.</summary>
    public const double SpreadMin = 0.4, SpreadMax = 1.8, LinksMin = 0, LinksMax = 0.6, RepeatsMin = 0, RepeatsMax = 1.8;

    /// <summary>Where the Lyric style page's entrance controls read and write: the chosen layout, or "automatic" for Match the music.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EntranceKey => LyricComposition is { Length: > 0 } c ? c : AllLayoutsKey;

    /// <summary>The entrance (raw value) the page shows for <see cref="EntranceKey"/>.</summary>
    public string EntranceForControls() =>
        (WordArrivalByStyle ?? new Dictionary<string, string>()).TryGetValue(EntranceKey, out var arrival) ? arrival : WordArrival;

    /// <summary>These settings with <paramref name="arrival"/> as the entrance for <see cref="EntranceKey"/>.</summary>
    public AppSettings WithEntranceForControls(string arrival)
    {
        var map = new Dictionary<string, string>(WordArrivalByStyle ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            [EntranceKey] = arrival,
        };
        return this with { WordArrivalByStyle = map };
    }

    /// <summary>These settings with <paramref name="arrival"/> in or out of the custom mix (kept in the Mac's order).</summary>
    public AppSettings WithMixEntrance(string arrival, bool included)
    {
        var chosen = new HashSet<string>(WordEntranceMix ?? [], StringComparer.Ordinal);
        if (included) chosen.Add(arrival);
        else chosen.Remove(arrival);
        return this with { WordEntranceMix = [.. Arrivals.MixChoices.Select(a => a.Id()).Where(chosen.Contains)] };
    }

    /// <summary>The entrances in the custom mix, in the Mac's order.</summary>
    public IReadOnlyList<Core.Compositions.WordArrival> MixEntrances() =>
        [.. Arrivals.MixChoices.Where(a => (WordEntranceMix ?? []).Contains(a.Id()))];

    private AppSettings NormalizedWindowSettings()
    {
        var defaults = new AppSettings();
        var mixIds = Arrivals.MixChoices.Select(a => a.Id()).ToArray();
        var byStyle = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (layout, arrival) in WordArrivalByStyle ?? new Dictionary<string, string>())
        {
            if (layout is not null && Compositions.Contains(layout) && arrival is not null && WordArrivals.Contains(arrival))
                byStyle[layout] = arrival;
        }

        return this with
        {
            ConstellationSpread = Clamp(ConstellationSpread, SpreadMin, SpreadMax, defaults.ConstellationSpread),
            ConstellationLinkOpacity = Clamp(ConstellationLinkOpacity, LinksMin, LinksMax, defaults.ConstellationLinkOpacity),
            EchoStrength = Clamp(EchoStrength, RepeatsMin, RepeatsMax, defaults.EchoStrength),
            WordEntranceMix = WordEntranceMix is null ? defaults.WordEntranceMix : [.. mixIds.Where(WordEntranceMix.Contains)],
            WordArrivalByStyle = byStyle,
            AppTheme = AppThemes.Contains(AppTheme) ? AppTheme : defaults.AppTheme,
        };

        static double Clamp(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }

    /// <summary>The window's settings in the renderer's style: the family's sliders, the custom mix and the entrances per layout.</summary>
    private LyricStyle WithWindowStyle(LyricStyle style)
    {
        var byStyle = WordArrivalByStyle ?? new Dictionary<string, string>();
        var reveal = style.Reveal;
        var perLayout = ValueMap.Empty;
        if (LyricComposition == AllLayoutsKey && byStyle.TryGetValue(AllLayoutsKey, out var all))
        {
            // Match the music's own entrance is for every layout it picks.
            reveal = SceneReveal(all);
        }
        else
        {
            perLayout = new ValueMap(byStyle.Where(pair => pair.Key != AllLayoutsKey)
                .Select(pair => KeyValuePair.Create(pair.Key, SceneReveal(pair.Value))));
        }

        return style with
        {
            Reveal = reveal,
            RevealByLayout = perLayout,
            MixEntrances = new ValueList(WordEntranceMix ?? []),
            ConstellationSpread = double.IsFinite(ConstellationSpread) ? Math.Clamp(ConstellationSpread, SpreadMin, SpreadMax) : ConstellationLayout.DefaultSpread,
            ConstellationLinkOpacity = double.IsFinite(ConstellationLinkOpacity) ? Math.Clamp(ConstellationLinkOpacity, LinksMin, LinksMax) : ConstellationLayout.DefaultLinkOpacity,
            EchoStrength = double.IsFinite(EchoStrength) ? Math.Clamp(EchoStrength, RepeatsMin, RepeatsMax) : EchoBackdrop.DefaultStrength,
        };
    }
}

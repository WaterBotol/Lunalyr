// Ported from the Mac's Sources/Settings/EntranceSupport.swift (Lunalyr's own; no Verso lines).

using Lunalyr.Core.Compositions;

namespace Lunalyr.Shell.Settings;

/// <summary>
/// Which word entrances a layout actually shows, for the Lyric style page. The stage layouts move word-timed lyrics
/// their own way: Hinge, Broadside, Portal and Mosaic never read the chosen entrance's motion, and Torque and Fable take
/// every chosen motion except Soft focus (their own default) and the swipes (Satin Ribbon, Split Ink). Every stage
/// layout still draws Cutout · Highlight's tiles. Lyrics timed only by the line enter as a whole with any entrance, in
/// every layout, so an entrance a layout ignores is marked, not removed.
/// </summary>
public static class EntranceSupport
{
    /// <summary>Whether <paramref name="layout"/> shows <paramref name="arrival"/> on lyrics timed word by word.</summary>
    public static bool AppliesToSungWords(WordArrival arrival, CompositionKind layout)
    {
        // Match the style is the layout's own; a custom mix is judged by its parts; every layout tiles the sung words.
        if (arrival is WordArrival.Automatic or WordArrival.Mix or WordArrival.CutoutHighlight) return true;
        return layout switch
        {
            CompositionKind.Hinge or CompositionKind.Broadside or CompositionKind.Portal or CompositionKind.Mosaic => false,
            CompositionKind.Torque or CompositionKind.Fable => arrival != WordArrival.Fade && !arrival.IsSwipe(),
            _ => true,
        };
    }

    /// <summary>The entrance as the menu lists it. <paramref name="layout"/> is null for Match the music, which marks nothing.</summary>
    public static string MenuTitle(WordArrival arrival, CompositionKind? layout) =>
        layout is { } kind && !AppliesToSungWords(arrival, kind) ? $"{arrival.Title()} (whole lines only)" : arrival.Title();

    /// <summary>The entrances in <paramref name="mix"/> that <paramref name="layout"/> leaves out for word-timed lyrics.</summary>
    public static IReadOnlyList<WordArrival> Ignored(IEnumerable<WordArrival> mix, CompositionKind layout) =>
        [.. mix.Where(arrival => !AppliesToSungWords(arrival, layout))];

    /// <summary>
    /// Under the entrance menu: why the chosen entrance, or part of the custom mix, doesn't show on word-timed lyrics in
    /// this layout. Null when it all shows (and always for Match the music).
    /// </summary>
    public static string? Note(WordArrival selected, CompositionKind? layout, IReadOnlyList<WordArrival> mix)
    {
        if (layout is not { } kind) return null;
        var left = selected == WordArrival.Mix ? Ignored(mix, kind) : Ignored([selected], kind);
        if (left.Count == 0) return null;
        var names = List([.. left.Select(a => a.Title())]);
        var verb = left.Count == 1 ? "plays" : "play";
        return $"{kind.Title()} moves each sung word its own way, so {names} {verb} only on lyrics timed by the line.";
    }

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string List(IReadOnlyList<string> names) =>
        names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
}

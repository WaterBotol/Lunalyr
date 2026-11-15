// -style name=value: the Lyric style extras (backing vocals, Editorial typography, mixed fonts, text colour, word reveal
// and the next-word preview) applied to every scene, so each can be rendered on and off.

using System.Globalization;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Render.Scene;

namespace Lunalyr.Tools.CompositionPreview;

internal static class StyleExtras
{
    public static LyricStyle Apply(LyricStyle style, IEnumerable<(string Name, string Value)> extras)
    {
        foreach (var (name, value) in extras)
        {
            style = name switch
            {
                "backing" => style with { BackingVocals = value != "off" },
                "backingStyle" => style with { BackingStyle = value.Length == 0 ? null : value },
                "editorial" => style with { EditorialTypography = true, EditorialAmount = Number(value) },
                "fonts" => style with { RandomFonts = value == "random" },
                "colour" => style with { ColourMode = LyricColourModes.SavedChoice(value) ?? LyricColourModes.DefaultMode },
                "strength" => style with { ColourStrength = Number(value) },
                "speed" => style with { ColourSpeed = Number(value) },
                "custom" => style with { ColourCustom = new ValueList(value.Split(',')) },
                "reveal" => style with { NoWordReveal = value == "off" ? AllLayouts() : ValueList.Empty },
                "preview" => style with { NoNextWordPreview = value == "off" ? AllLayouts() : ValueList.Empty },
                "lead" => style with { PreviewLead = Number(value) },
                "symbols" => style with { WordSymbols = value != "off" },
                "presence" => style with { WordPresence = Number(value) },
                "orb" => style with { OrbStyle = value },
                "actions" => style with { ActionWords = value != "off" },
                _ => throw new ArgumentException($"Unknown style extra {name}"),
            };
        }
        return style;
    }

    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static ValueList AllLayouts() =>
        new(Enum.GetValues<CompositionKind>().Select(CompositionTraits.Id));
}

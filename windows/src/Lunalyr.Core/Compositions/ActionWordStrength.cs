// "Action word strength" (the Mac's custom.actionWords.strength, 16 Nov 2026): how big the action words' motion is.
// The Mac's values and amounts: subtle 0.2, middle 0.5, wild 1.0; wild by default.

namespace Lunalyr.Core.Compositions;

public enum ActionWordStrength { Subtle, Middle, Wild }

public static class ActionWordStrengths
{
    public const ActionWordStrength Default = ActionWordStrength.Wild;

    /// <summary>The stored values, in the menu's order.</summary>
    public static IReadOnlyList<string> Values { get; } = ["subtle", "middle", "wild"];

    public static string RawValue(this ActionWordStrength strength) => strength switch
    {
        ActionWordStrength.Subtle => "subtle",
        ActionWordStrength.Middle => "middle",
        _ => "wild",
    };

    public static string Title(this ActionWordStrength strength) => strength switch
    {
        ActionWordStrength.Subtle => "Subtle",
        ActionWordStrength.Middle => "Middle",
        _ => "Wild",
    };

    /// <summary>How much of the full motion is drawn: 0.2, 0.5 or 1.</summary>
    public static double Amount(this ActionWordStrength strength) => strength switch
    {
        ActionWordStrength.Subtle => 0.2,
        ActionWordStrength.Middle => 0.5,
        _ => 1.0,
    };

    /// <summary>A stored value as a strength; anything unknown is the default.</summary>
    public static ActionWordStrength Parse(string? raw) => raw switch
    {
        "subtle" => ActionWordStrength.Subtle,
        "middle" => ActionWordStrength.Middle,
        _ => Default,
    };
}

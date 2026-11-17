// The welcome's typeface step, ported from the Mac's OnboardingTypefaces.swift (2ed3445d; Lunalyr's own lines), in
// Windows' faces. The Mac offers the faces on every Mac, so choosing one downloads nothing: SF Pro, Avenir Next
// Condensed, Charter, Rockwell and SF Rounded. Windows offers its own system face first (Segoe UI Variable, SF Pro's
// part, already the default) and then the faces Windows ships that play the others' parts: Bahnschrift (a condensed
// sans), Georgia (a book serif) and, where an Office install brought them, Rockwell and Arial Rounded MT Bold. A face
// that is not installed is not offered. The Installed font choice stays in Settings.

using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Onboarding;

/// <summary>A tile on the typeface step: its name, and the setting it makes (a font choice, and the family for an installed one).</summary>
public sealed record OnboardingTypeface(string Name, string FontChoice, string FontFamily, string Help);

public static class OnboardingTypefaces
{
    /// <summary>Every face the step may offer, in order, the Mac's counterpart in each help.</summary>
    public static IReadOnlyList<OnboardingTypeface> Candidates { get; } =
    [
        new("Segoe UI Variable", "system", "", "Windows\u2019 own typeface, as SF Pro is the Mac\u2019s."),
        new("Bahnschrift", "installed", "Bahnschrift", "A condensed sans, in Avenir Next Condensed\u2019s place."),
        new("Georgia", "installed", "Georgia", "A book serif, in Charter\u2019s place."),
        new("Rockwell", "installed", "Rockwell", "A slab serif."),
        new("Arial Rounded MT Bold", "installed", "Arial Rounded MT Bold", "A rounded sans, in SF Rounded\u2019s place."),
    ];

    /// <summary>The faces offered on this PC: the system face always, the others only when installed.</summary>
    public static IReadOnlyList<OnboardingTypeface> Offered(IEnumerable<string> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var families = installed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. Candidates.Where(f => f.FontChoice == "system" || families.Contains(f.FontFamily))];
    }

    /// <summary>The tile <paramref name="settings"/> have chosen, or null when their face is none of the tiles.</summary>
    public static OnboardingTypeface? Chosen(AppSettings settings, IReadOnlyList<OnboardingTypeface> offered)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return offered.FirstOrDefault(f => f.FontChoice == settings.FontChoice
            && (f.FontChoice != "installed" || string.Equals(f.FontFamily, settings.FontFamily, StringComparison.OrdinalIgnoreCase)));
    }
}

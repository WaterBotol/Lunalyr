using Lunalyr.Core.Player;
using Lunalyr.Platform.Desktop;
using Microsoft.Win32;

namespace Lunalyr.App.Player;

/// <summary>
/// Windows' own settings as the player draws them, read only (never changed): "Animation effects" (Reduce Motion),
/// "Transparency effects" (Reduce Transparency), a contrast theme, and light or dark app mode. A
/// <c>--player-debug</c> look overrides them for checks.
/// </summary>
internal static class SystemLook
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static unsafe PlayerLook Read(IReadOnlyList<string> debug)
    {
        var highContrast = new PlayerNative.HIGHCONTRASTW { cbSize = (uint)sizeof(PlayerNative.HIGHCONTRASTW) };
        var contrast = PlayerNative.SystemParametersInfo(PlayerNative.SPI_GETHIGHCONTRAST, highContrast.cbSize, &highContrast, 0)
            && (highContrast.dwFlags & PlayerNative.HCF_HIGHCONTRASTON) != 0;
        var look = new PlayerLook(
            ReduceMotion: SystemMotion.ReduceMotionFrom(SystemMotion.ReadAnimationEffects()),
            ReduceTransparency: ReadDword("EnableTransparency") == 0,
            HighContrast: contrast,
            Light: ReadDword("AppsUseLightTheme") == 1);
        foreach (var flag in debug)
        {
            look = flag switch
            {
                "light" => look with { Light = true },
                "dark" => look with { Light = false },
                "reduce-motion" => look with { ReduceMotion = true },
                "opaque" => look with { ReduceTransparency = true },
                "contrast" => look with { HighContrast = true },
                _ => look,
            };
        }

        return look;
    }

    private static int? ReadDword(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Personalize);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

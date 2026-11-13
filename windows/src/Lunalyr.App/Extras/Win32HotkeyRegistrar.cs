using Lunalyr.Shell.Extras;

namespace Lunalyr.App.Extras;

/// <summary>
/// Global shortcuts with Windows: <c>RegisterHotKey</c> on the app's hidden window (the tray host's), whose
/// <c>WM_HOTKEY</c> brings the presses back. Only these exact combinations reach Lunalyr, nothing else typed; a
/// combination another app holds fails to register. Keys go by position, as on the Mac: a scan code is turned into the
/// virtual key the keyboard layout in use puts there. Call it on the window's thread.
/// </summary>
internal sealed class Win32HotkeyRegistrar(nint hwnd) : IHotkeyRegistrar
{
    private readonly HashSet<int> _held = [];

    public bool Register(uint scanCode, uint modifiers, int id)
    {
        Unregister(id);
        var vk = VirtualKeyFor(scanCode);
        if (vk == 0) return false;
        // No repeats while the keys are held, as the Mac's hot keys fire once a press.
        if (!ExtrasNative.RegisterHotKey(hwnd, id, modifiers | HotkeyModifiersInfo.ModNoRepeat, vk)) return false;
        _held.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_held.Remove(id)) ExtrasNative.UnregisterHotKey(hwnd, id);
    }

    /// <summary>Releases every combination (before the window goes).</summary>
    public void UnregisterAll()
    {
        foreach (var id in _held.ToList()) Unregister(id);
    }

    /// <summary>The virtual key at <paramref name="scanCode"/> on the keyboard layout in use, or 0.</summary>
    public static uint VirtualKeyFor(uint scanCode) =>
        ExtrasNative.MapVirtualKeyEx(scanCode, ExtrasNative.MAPVK_VSC_TO_VK_EX, ExtrasNative.GetKeyboardLayout(0));

    /// <summary>
    /// The character the key at <paramref name="scanCode"/> types on the layout in use (a dead key's own character), or
    /// null: what the settings page shows for it.
    /// </summary>
    public static string? KeyName(uint scanCode)
    {
        var vk = VirtualKeyFor(scanCode);
        if (vk == 0) return null;
        var value = ExtrasNative.MapVirtualKeyEx(vk, ExtrasNative.MAPVK_VK_TO_CHAR, ExtrasNative.GetKeyboardLayout(0)) & 0x7FFFFFFF;
        if (value is < 0x20 or (>= 0xD800 and <= 0xDFFF) or > 0xFFFF) return null;
        return char.ConvertFromUtf32((int)value).ToUpperInvariant();
    }
}

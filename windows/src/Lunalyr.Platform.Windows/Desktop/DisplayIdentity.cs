using Lunalyr.Core.Floating;
using static Lunalyr.Platform.Desktop.DesktopNative;
using static Lunalyr.Platform.Desktop.FloatingNative;

namespace Lunalyr.Platform.Desktop;

/// <summary>A display the floating lyrics can go on: its lasting key, a name for the settings page, and the monitor now.</summary>
/// <param name="Key">The monitor's device path (stable across restarts and reconnections to the same port), else its GDI name.</param>
/// <param name="Name">The monitor's own name ("Lenovo Pro 27Q-10"), else "Display N".</param>
public sealed record DisplayChoice(string Key, string Name, MonitorInfo Monitor);

/// <summary>
/// The displays connected, main display first (the Mac's choice order: the chosen one when connected, else the main
/// one). Read through DisplayConfig, which names monitors as Windows' Settings › Display does. Nothing is changed.
/// </summary>
public static unsafe class DisplayIdentity
{
    /// <summary>Every display, the primary first, then left to right.</summary>
    public static IReadOnlyList<DisplayChoice> Connected()
    {
        var monitors = DesktopReader.Monitors();
        var targets = Targets();
        var ordered = monitors.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
        var list = new List<DisplayChoice>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            targets.TryGetValue(m.DeviceName, out var target);
            var key = string.IsNullOrEmpty(target.Path) ? m.DeviceName : target.Path;
            var name = string.IsNullOrWhiteSpace(target.Name) ? $"Display {i + 1}" : target.Name;
            list.Add(new DisplayChoice(key, name, m));
        }
        return list;
    }

    /// <summary>What an auto-hidden taskbar on <paramref name="monitor"/> covers when it slides in, per edge, in pixels.</summary>
    public static EdgeInsets AutoHideInsets(MonitorInfo monitor)
    {
        double left = 0, top = 0, right = 0, bottom = 0;
        var rc = new RECT
        {
            Left = (int)monitor.Bounds.X, Top = (int)monitor.Bounds.Y,
            Right = (int)(monitor.Bounds.X + monitor.Bounds.Width), Bottom = (int)(monitor.Bounds.Y + monitor.Bounds.Height),
        };
        foreach (var edge in new[] { ABE_LEFT, ABE_TOP, ABE_RIGHT, ABE_BOTTOM })
        {
            var data = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), uEdge = edge, rc = rc };
            nint bar = (nint)SHAppBarMessage(ABM_GETAUTOHIDEBAREX, &data);
            if (bar == 0 || !GetWindowRect(bar, out var r)) continue;
            // The bar keeps its full size while it hides (mostly off screen); 48 points when it can't be read.
            double thickness = edge is ABE_LEFT or ABE_RIGHT ? r.Right - r.Left : r.Bottom - r.Top;
            if (thickness <= 4) thickness = 48 * monitor.Scale;
            switch (edge)
            {
                case ABE_LEFT: left = thickness; break;
                case ABE_TOP: top = thickness; break;
                case ABE_RIGHT: right = thickness; break;
                default: bottom = thickness; break;
            }
        }
        return new EdgeInsets(left, top, right, bottom);
    }

    /// <summary>The display as the floating window's placement reads it.</summary>
    public static FloatingDisplay Display(MonitorInfo monitor) =>
        new(ToMotionBox(monitor.Bounds), ToMotionBox(monitor.WorkArea), monitor.Scale, AutoHideInsets(monitor));

    private static Core.Motion.Box ToMotionBox(Box box) => new(box.X, box.Y, box.Width, box.Height);

    /// <summary>GDI device name (\\.\DISPLAY1) → the first monitor on it: its device path and friendly name.</summary>
    private static Dictionary<string, (string Path, string Name)> Targets()
    {
        var found = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0 || pathCount == 0) return found;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[Math.Max(1, modeCount)];
            fixed (DISPLAYCONFIG_PATH_INFO* p = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* m = modes)
            {
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, p, ref modeCount, m, 0) != 0) return found;
            }
            for (var i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME),
                        adapterId = path.sourceInfo.adapterId, id = path.sourceInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(&source.header) != 0) continue;
                var gdi = new string(source.viewGdiDeviceName);
                if (found.ContainsKey(gdi)) continue; // duplicated displays: the first monitor names the pair
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME),
                        adapterId = path.targetInfo.adapterId, id = path.targetInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(&target.header) != 0) continue;
                found[gdi] = (new string(target.monitorDevicePath), new string(target.monitorFriendlyDeviceName));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return found;
    }
}

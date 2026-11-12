using System.Text;
using static Lunalyr.Platform.Desktop.DesktopNative;

namespace Lunalyr.Platform.Desktop;

/// <summary>How a wallpaper window sits behind the desktop icons.</summary>
public enum AttachMode
{
    /// <summary>A normal top-level window (the harness's <c>--windowed</c>, a preview).</summary>
    Windowed,
    /// <summary>Windows 11 24H2+: a child of <c>Progman</c> directly below <c>SHELLDLL_DefView</c>, above the system wallpaper's WorkerW.</summary>
    Progman24H2,
    /// <summary>Older builds: a child of the WorkerW that follows the icons' host window.</summary>
    LegacyWorkerW,
    /// <summary>The floating lyrics' see-through, click-through pop-up over every app (not attached to the desktop).</summary>
    Floating,
}

/// <summary>Finds where a wallpaper window goes, behind the desktop icons (from the measured spike).</summary>
public static class DesktopAttach
{
    /// <param name="Parent">The window to create the wallpaper windows under.</param>
    /// <param name="InsertAfter">The sibling to place them directly below (SHELLDLL_DefView on 24H2), or 0.</param>
    /// <param name="OriginX">Screen position of the parent's client origin: monitor bounds are offset by it.</param>
    public sealed record Target(AttachMode Mode, nint Parent, nint InsertAfter, int OriginX, int OriginY);

    /// <summary>
    /// The desktop on 24H2+: <c>Progman</c> (ex-style includes <c>WS_EX_NOREDIRECTIONBITMAP</c>) has two children,
    /// <c>SHELLDLL_DefView</c> (icons, top) and <c>WorkerW</c> (the wallpaper, bottom). On older builds the icons
    /// live in a top-level WorkerW and the wallpaper WorkerW follows it. Null while Explorer isn't running.
    /// </summary>
    public static Target? Find()
    {
        nint progman = FindWindow("Progman", null);
        if (progman == 0) return null;
        nint defView = FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        nint childWorker = FindWindowEx(progman, 0, "WorkerW", null);
        if (childWorker == 0 && defView != 0)
        {
            // Ask Explorer to create the wallpaper WorkerW (harmless and not persistent).
            SendMessageTimeout(progman, 0x052C, 0xD, 1, SMTO_NORMAL, 1000, out _);
            SendMessageTimeout(progman, 0x052C, 0, 0, SMTO_NORMAL, 1000, out _);
            childWorker = FindWindowEx(progman, 0, "WorkerW", null);
        }
        GetWindowRect(progman, out var pr);
        if (childWorker != 0 && defView != 0)
            return new Target(AttachMode.Progman24H2, progman, defView, pr.Left, pr.Top);

        // Legacy (pre-24H2): find the top-level window hosting the icons; the wallpaper WorkerW is the next one.
        nint legacy = 0;
        for (nint h = FindWindowEx(0, 0, "WorkerW", null); h != 0; h = FindWindowEx(0, h, "WorkerW", null))
        {
            if (FindWindowEx(h, 0, "SHELLDLL_DefView", null) != 0)
            {
                legacy = FindWindowEx(0, h, "WorkerW", null);
                break;
            }
        }
        if (legacy == 0 && defView != 0)
            legacy = FindWindowEx(0, progman, "WorkerW", null);
        if (legacy == 0) return null;
        GetWindowRect(legacy, out var lr);
        return new Target(AttachMode.LegacyWorkerW, legacy, 0, lr.Left, lr.Top);
    }

    /// <summary>Progman's children, front to back, for logs and checks.</summary>
    public static string DescribeTree()
    {
        var sb = new StringBuilder();
        nint progman = FindWindow("Progman", null);
        sb.Append($"Progman {progman:X} ex={GetWindowLongPtr(progman, GWL_EXSTYLE):X}");
        for (nint c = GetWindow(progman, GW_CHILD); c != 0; c = GetWindow(c, GW_HWNDNEXT))
        {
            GetWindowRect(c, out var r);
            sb.Append($"; {ClassOf(c)} {c:X} vis={IsWindowVisible(c)} ({r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top})");
        }
        return sb.ToString();
    }
}

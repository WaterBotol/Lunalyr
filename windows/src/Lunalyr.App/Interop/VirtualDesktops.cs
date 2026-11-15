using System.Runtime.InteropServices;

namespace Lunalyr.App.Interop;

/// <summary>
/// Windows' public <c>IVirtualDesktopManager</c> (shobjidl.h), called through its vtable (Native AOT safe). A window
/// stays on the virtual desktop it was opened on, and activating it from another desktop leaves it cloaked there: the
/// settings window then "opened" with nothing on screen (seen 16 Nov 2026).
/// </summary>
internal static unsafe partial class VirtualDesktops
{
    private static readonly Guid Clsid = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");
    private static readonly Guid Iid = new("a5cd92ff-29be-454c-8d04-d82879fb3f1b");

    /// <summary>
    /// False only when Windows says <paramref name="hwnd"/> is on another virtual desktop; true when it is on this one
    /// or the question can't be answered (no virtual desktops, the call failing).
    /// </summary>
    public static bool IsOnCurrentDesktop(nint hwnd)
    {
        if (hwnd == 0 || CoCreateInstance(Clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, Iid, out var manager) < 0 || manager == 0)
            return true;
        try
        {
            // IUnknown (3 slots), then IsWindowOnCurrentVirtualDesktop(HWND, BOOL*).
            var vtable = *(nint**)manager;
            var isOnCurrent = (delegate* unmanaged[Stdcall]<nint, nint, int*, int>)vtable[3];
            int on = 1;
            return isOnCurrent(manager, hwnd, &on) < 0 || on != 0;
        }
        finally
        {
            Marshal.Release(manager);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}

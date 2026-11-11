using System.Runtime.InteropServices;

namespace Lunalyr.Render.Engine;

/// <summary>Kernel waits for the render thread. Blittable, source-generated: Native-AOT safe.</summary>
internal static unsafe partial class RenderNative
{
    public const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 258, INFINITE = 0xFFFFFFFF;
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    public const uint TIMER_ALL_ACCESS = 0x1F0003;

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForMultipleObjects(uint count, nint* handles, [MarshalAs(UnmanagedType.Bool)] bool all, uint ms);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint ms);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW")]
    public static partial nint CreateWaitableTimerEx(nint attributes, char* name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(nint timer, long* dueTime, int period, nint completion, nint arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lunalyr.Install;

/// <summary>A running Lunalyr: its host window, process and executable.</summary>
public sealed record RunningCopy(nint Window, int ProcessId, string? ExePath);

/// <summary>Finding and closing running copies of Lunalyr. Tests use a fake.</summary>
public interface IRunningCopies
{
    IReadOnlyList<RunningCopy> Find();

    /// <summary>Asks a copy to quit the way its tray menu does, then waits for its process to end.</summary>
    bool Quit(RunningCopy copy, TimeSpan timeout);
}

/// <summary>
/// Finds Lunalyr's hidden host windows (class <c>Lunalyr.Host.…</c>) and asks them to quit with
/// <see cref="Product.QuitMessage"/>, so settings are flushed and the desktop wallpaper is restored. Never kills.
/// </summary>
public sealed partial class WindowsRunningCopies : IRunningCopies
{
    public IReadOnlyList<RunningCopy> Find()
    {
        var found = new List<RunningCopy>();
        var handle = GCHandle.Alloc(found);
        try
        {
            unsafe { EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle)); }
        }
        finally
        {
            handle.Free();
        }
        return found;
    }

    [UnmanagedCallersOnly]
    private static unsafe int OnWindow(nint hwnd, nint state)
    {
        var name = stackalloc char[256];
        var length = GetClassNameW(hwnd, name, 256);
        if (length <= 0) return 1;
        var cls = new string(name, 0, length);
        if (!cls.StartsWith(Product.HostWindowClassPrefix, StringComparison.Ordinal)) return 1;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return 1;
        var list = (List<RunningCopy>)GCHandle.FromIntPtr(state).Target!;
        list.Add(new RunningCopy(hwnd, (int)pid, ImagePath(pid)));
        return 1;
    }

    public bool Quit(RunningCopy copy, TimeSpan timeout)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(copy.ProcessId);
        }
        catch (ArgumentException)
        {
            return true; // Already gone.
        }
        using (process)
        {
            if (!PostMessageW(copy.Window, Product.QuitMessage, 0, 0) && !process.HasExited) return false;
            return process.WaitForExit(timeout);
        }
    }

    private static unsafe string? ImagePath(uint pid)
    {
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, 0, pid);
        if (process == 0) return null;
        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024u;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) != 0 ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [LibraryImport("user32.dll")]
    private static unsafe partial int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(nint hwnd, char* name, int max);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, int inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int QueryFullProcessImageNameW(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}

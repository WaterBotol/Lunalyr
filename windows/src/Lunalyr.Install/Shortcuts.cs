using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Lunalyr.Install;

/// <summary>The Start menu shortcut. Tests use a fake.</summary>
public interface IShortcuts
{
    void Create(string lnkPath, string target, string description);

    /// <summary>The program a shortcut opens, or null when there's no readable shortcut there.</summary>
    string? Target(string lnkPath);

    void Delete(string lnkPath);
}

/// <summary>Shell links through <c>IShellLinkW</c> and <c>IPersistFile</c> (source-generated COM, Native AOT safe).</summary>
public sealed partial class WindowsShortcuts : IShortcuts
{
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");

    /// <summary><c>%APPDATA%\Microsoft\Windows\Start Menu\Programs</c>.</summary>
    public static string StartMenuFolder() => Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    public static string StartMenuShortcut(string folder) => Path.Combine(folder, Product.Name + ".lnk");

    public void Create(string lnkPath, string target, string description)
    {
        var link = NewLink();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
        link.SetDescription(description);
        link.SetIconLocation(target, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        ((IPersistFile)link).Save(lnkPath, 1);
    }

    public unsafe string? Target(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        try
        {
            var link = NewLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var buffer = stackalloc char[1024];
            link.GetPath(buffer, 1024, 0, 0x4 /* SLGP_RAWPATH */);
            var text = new string(buffer);
            return text.Length == 0 ? null : text;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public void Delete(string lnkPath)
    {
        if (File.Exists(lnkPath)) File.Delete(lnkPath);
    }

    private static IShellLinkW NewLink()
    {
        // Any apartment will do (S_FALSE or RPC_E_CHANGED_MODE when this thread already has one).
        _ = CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
        var iid = typeof(IShellLinkW).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ClsidShellLink, 0, 1 /* CLSCTX_INPROC_SERVER */, iid, out var unknown));
        try
        {
            return (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint unknown);
}

[GeneratedComInterface, Guid("000214F9-0000-0000-C000-000000000046")]
internal unsafe partial interface IShellLinkW
{
    void GetPath(char* file, int max, nint findData, uint flags);
    void GetIDList(out nint idList);
    void SetIDList(nint idList);
    void GetDescription(char* name, int max);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetWorkingDirectory(char* dir, int max);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    void GetArguments(char* args, int max);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    void GetHotkey(out ushort hotkey);
    void SetHotkey(ushort hotkey);
    void GetShowCmd(out int show);
    void SetShowCmd(int show);
    void GetIconLocation(char* path, int max, out int index);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
    void Resolve(nint hwnd, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

[GeneratedComInterface, Guid("0000010b-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    void GetClassID(out Guid clsid);
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, int remember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
    void GetCurFile(out nint fileName);
}

/// <summary>Shortcuts as plain text files, for tests: the file holds the target.</summary>
public sealed class FakeShortcuts : IShortcuts
{
    public void Create(string lnkPath, string target, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        File.WriteAllText(lnkPath, target);
    }

    public string? Target(string lnkPath) => File.Exists(lnkPath) ? File.ReadAllText(lnkPath) : null;

    public void Delete(string lnkPath)
    {
        if (File.Exists(lnkPath)) File.Delete(lnkPath);
    }
}

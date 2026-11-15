using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Lunalyr.App.Interop;
using Lunalyr.App.Tray;

namespace Lunalyr.App.Hosting;

/// <summary>
/// One running Lunalyr per data folder: a named mutex decides, and a second launch hands its command line to the
/// first (through the first's hidden window) and exits. Scoping by data folder keeps a <c>--data</c> test instance
/// apart from the real one.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private bool _owned;

    private SingleInstance(Mutex mutex, bool owned, string key)
    {
        _mutex = mutex;
        _owned = owned;
        Key = key;
    }

    /// <summary>A short hash of the data folder, used in the mutex and window class names.</summary>
    public string Key { get; }

    public bool IsFirst => _owned;

    public string WindowClass => WindowClassFor(Key);

    public static string KeyFor(string dataRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    public static string WindowClassFor(string key) => $"Lunalyr.Host.{key}";

    public static SingleInstance Acquire(string dataRoot)
    {
        var key = KeyFor(dataRoot);
        var mutex = new Mutex(initiallyOwned: false, $@"Local\Lunalyr.{key}");
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // The previous instance died without releasing it; we take over.
        }

        return new SingleInstance(mutex, owned, key);
    }

    /// <summary>
    /// Sends <paramref name="args"/> to the running instance's window, waiting up to <paramref name="timeout"/> for it
    /// to appear (it may still be starting). Returns false if it never answered.
    /// </summary>
    public unsafe bool Forward(IReadOnlyList<string> args, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var hwnd = Win32.FindWindow(WindowClass, null);
            if (hwnd != 0)
            {
                Win32.GetWindowThreadProcessId(hwnd, out var pid);
                // Lets the first instance bring its settings window to the front.
                if (pid != 0) Win32.AllowSetForegroundWindow(pid);
                var text = string.Join('\0', args);
                fixed (char* chars = text)
                {
                    var data = new Win32.COPYDATASTRUCT { dwData = TrayHost.ActivationMagic, cbData = (uint)(text.Length * 2), lpData = (nint)chars };
                    var sent = Win32.SendMessageTimeout(hwnd, Win32.WM_COPYDATA, 0, (nint)(&data),
                        Win32.SMTO_ABORTIFHUNG | Win32.SMTO_BLOCK, 5000, out var result);
                    if (sent != 0 && result == 1) return true;
                }
            }

            if (watch.Elapsed >= timeout) return false;
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        if (_owned)
        {
            _owned = false;
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released on a different thread than it was taken on: the OS releases it at exit anyway.
            }
        }

        _mutex.Dispose();
    }
}

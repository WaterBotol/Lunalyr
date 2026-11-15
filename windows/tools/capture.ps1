# Captures what is on screen (the whole virtual screen) and, separately, the desktop window (Progman)
# through PrintWindow(PW_RENDERFULLCONTENT), which includes the wallpaper layer even when apps cover it.
# Read-only: it doesn't move, minimise or change any window.
param([Parameter(Mandatory)][string]$OutDir, [string]$Tag = "cap")

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Cap {
  [DllImport("user32")] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32")] public static extern int GetSystemMetrics(int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
[Cap]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
New-Item -ItemType Directory -Force $OutDir | Out-Null

# 1. The screen as the user sees it.
$x = [Cap]::GetSystemMetrics(76); $y = [Cap]::GetSystemMetrics(77); $w = [Cap]::GetSystemMetrics(78); $h = [Cap]::GetSystemMetrics(79)
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
$g.Dispose(); $bmp.Save((Join-Path $OutDir "$Tag-screen.png")); $bmp.Dispose()

# 2. The desktop window itself.
$pm = [Cap]::FindWindow("Progman", $null)
# Progman spans the virtual screen.
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc(); $ok = [Cap]::PrintWindow($pm, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save((Join-Path $OutDir "$Tag-desktop.png")); $bmp.Dispose()
"screen ${w}x${h}; desktop PrintWindow ok=$ok"

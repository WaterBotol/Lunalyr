# Captures one top-level window of a process (by PID and title substring) from the screen.
# Uses PrintWindow(PW_RENDERFULLCONTENT), so the window can be covered by other windows.
param([Parameter(Mandatory)][int]$ProcessId, [string]$Title = "", [Parameter(Mandatory)][string]$Out)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class CW {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public static IntPtr Find(uint pid, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
        if (sb.ToString().Contains(title)) { found = h; return false; } } return true; }, IntPtr.Zero);
    return found;
  }
}
'@
[CW]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$h = [CW]::Find([uint32]$ProcessId, $Title)
if ($h -eq [IntPtr]::Zero) { throw "window not found" }
$r = New-Object CW+RECT
[CW]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null  # DWMWA_EXTENDED_FRAME_BOUNDS
$wr = New-Object CW+RECT
[CW]::GetWindowRect($h, [ref]$wr) | Out-Null
$w = $r.R - $r.L; $hh = $r.B - $r.T
# PrintWindow draws the whole window rectangle, Windows 11's invisible resize borders included: draw it all, then keep
# the visible frame (otherwise the left border shows as a black strip and the right and bottom edges are cut off).
$full = New-Object System.Drawing.Bitmap ($wr.R - $wr.L), ($wr.B - $wr.T)
$g = [System.Drawing.Graphics]::FromImage($full); $hdc = $g.GetHdc(); $ok = [CW]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose(); if (-not $ok) { throw "PrintWindow failed" }
$bmp = $full.Clone((New-Object System.Drawing.Rectangle ($r.L - $wr.L), ($r.T - $wr.T), $w, $hh), $full.PixelFormat); $full.Dispose()
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$bmp.Save($Out); $bmp.Dispose()
"saved $Out (${w}x$hh)"


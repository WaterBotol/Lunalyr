# Shows what the desktop window (Progman: the wallpaper, Lunalyr's surfaces and the icons) is drawing, even when apps
# cover it, without moving, activating or covering anyone's windows.
#
# Default: PrintWindow(Progman, PW_RENDERFULLCONTENT), which asks DWM for Progman's composed content (its children's
# flip-model and DirectComposition surfaces included). Nothing appears on screen and nothing takes the focus.
# -Thumbnail: the older way, a DWM live thumbnail of Progman in a small topmost window captured from the screen for
# about a second. The window is shown without activation (WS_EX_NOACTIVATE, SW_SHOWNOACTIVATE), so it never takes the
# keyboard, but it is briefly visible at the top left.
param([Parameter(Mandatory)][string]$Out, [int]$Width = 1280, [switch]$Thumbnail)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class PeekDesktop {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct PROPS { public uint flags; public RECT dest; public RECT src; public byte opacity; [MarshalAs(UnmanagedType.Bool)] public bool visible; [MarshalAs(UnmanagedType.Bool)] public bool clientOnly; }
  [DllImport("dwmapi")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
  [DllImport("dwmapi")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref PROPS p);
  [DllImport("dwmapi")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
  [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, IntPtr n);
  [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
  [DllImport("user32")] static extern bool DestroyWindow(IntPtr h);
  [DllImport("user32")] static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32")] static extern bool PeekMessageW(out MSG m, IntPtr h, uint a, uint b, uint remove);
  [DllImport("user32")] static extern bool TranslateMessage(ref MSG m);
  [DllImport("user32")] static extern IntPtr DispatchMessageW(ref MSG m);
  [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr h; public uint msg; public IntPtr w, l; public uint time; public int x, y; }
  public static IntPtr Progman() { return FindWindowW("Progman", IntPtr.Zero); }
  public static RECT Bounds(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
  public static bool Print(IntPtr h, IntPtr hdc) { return PrintWindow(h, hdc, 2); }
  public static void Pump() { MSG m; while (PeekMessageW(out m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessageW(ref m); } }
  // A plain STATIC tool window, topmost and never activated.
  public static IntPtr ShowThumbnail(int w, int h, out IntPtr thumb, out int hr) {
    const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    IntPtr window = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "", WS_POPUP, 0, 0, w, h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    ShowWindow(window, 4); // SW_SHOWNOACTIVATE
    hr = DwmRegisterThumbnail(window, Progman(), out thumb);
    if (hr == 0) {
      var p = new PROPS { flags = 0x1 | 0x4 | 0x8, dest = new RECT { L = 0, T = 0, R = w, B = h }, opacity = 255, visible = true };
      hr = DwmUpdateThumbnailProperties(thumb, ref p);
    }
    return window;
  }
  public static void Close(IntPtr window, IntPtr thumb) { if (thumb != IntPtr.Zero) DwmUnregisterThumbnail(thumb); DestroyWindow(window); }
}
'@
[PeekDesktop]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$progman = [PeekDesktop]::Progman()
$r = [PeekDesktop]::Bounds($progman)
$pw = $r.R - $r.L; $ph = $r.B - $r.T
$h = [int]($Width * $ph / $pw)
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
if (-not $Thumbnail) {
  $full = New-Object System.Drawing.Bitmap $pw, $ph
  $g = [System.Drawing.Graphics]::FromImage($full); $hdc = $g.GetHdc(); $ok = [PeekDesktop]::Print($progman, $hdc); $g.ReleaseHdc($hdc); $g.Dispose()
  if (-not $ok) { throw "PrintWindow failed" }
  $small = New-Object System.Drawing.Bitmap $Width, $h
  $g = [System.Drawing.Graphics]::FromImage($small); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($full, 0, 0, $Width, $h); $g.Dispose()
  $full.Dispose(); $small.Save($Out); $small.Dispose()
  "printwindow saved $Out (${Width}x$h of ${pw}x$ph)"
  return
}
$thumb = [IntPtr]::Zero; $hr = 0
$window = [PeekDesktop]::ShowThumbnail($Width, $h, [ref]$thumb, [ref]$hr)
for ($i = 0; $i -lt 14; $i++) { [PeekDesktop]::Pump(); Start-Sleep -Milliseconds 50 }
$bmp = New-Object System.Drawing.Bitmap $Width, $h
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size); $g.Dispose()
[PeekDesktop]::Close($window, $thumb)
$bmp.Save($Out); $bmp.Dispose()
"thumbnail hr=$hr saved $Out (${Width}x$h)"

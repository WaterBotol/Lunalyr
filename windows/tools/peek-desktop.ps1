# Shows what the desktop window (Progman: wallpaper + icons) is drawing, even when apps cover it,
# by putting a DWM live thumbnail of it in a small topmost window for ~1 s and capturing that window.
# It doesn't minimise, move or change any of the user's windows.
param([Parameter(Mandatory)][string]$Out, [int]$Width = 1280)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Dwm {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct PROPS { public uint flags; public RECT dest; public RECT src; public byte opacity; [MarshalAs(UnmanagedType.Bool)] public bool visible; [MarshalAs(UnmanagedType.Bool)] public bool clientOnly; }
  [DllImport("dwmapi")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
  [DllImport("dwmapi")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref PROPS p);
  [DllImport("dwmapi")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
  [DllImport("user32")] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32")] public static extern int GetSystemMetrics(int i);
  public static int Show(IntPtr dest, int w, int h, out IntPtr thumb) {
    IntPtr src = FindWindow("Progman", null);
    int hr = DwmRegisterThumbnail(dest, src, out thumb);
    if (hr != 0) return hr;
    var p = new PROPS { flags = 0x1 | 0x4 | 0x8, dest = new RECT { L = 0, T = 0, R = w, B = h }, opacity = 255, visible = true };
    return DwmUpdateThumbnailProperties(thumb, ref p);
  }
}
'@
[Dwm]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$vw = [Dwm]::GetSystemMetrics(78); $vh = [Dwm]::GetSystemMetrics(79)
$h = [int]($Width * $vh / $vw)
$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle = 'None'; $f.TopMost = $true; $f.ShowInTaskbar = $false; $f.StartPosition = 'Manual'
$f.Location = New-Object System.Drawing.Point 0, 0; $f.ClientSize = New-Object System.Drawing.Size $Width, $h
$f.BackColor = [System.Drawing.Color]::Black
$f.Show(); [System.Windows.Forms.Application]::DoEvents()
$thumb = [IntPtr]::Zero
$hr = [Dwm]::Show($f.Handle, $Width, $h, [ref]$thumb)
Start-Sleep -Milliseconds 700; [System.Windows.Forms.Application]::DoEvents()
$bmp = New-Object System.Drawing.Bitmap $Width, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$pt = $f.PointToScreen((New-Object System.Drawing.Point 0, 0))
$g.CopyFromScreen($pt.X, $pt.Y, 0, 0, $bmp.Size); $g.Dispose()
[Dwm]::DwmUnregisterThumbnail($thumb) | Out-Null
$f.Close()
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$bmp.Save($Out); $bmp.Dispose()
"thumbnail hr=$hr saved $Out (${Width}x$h)"

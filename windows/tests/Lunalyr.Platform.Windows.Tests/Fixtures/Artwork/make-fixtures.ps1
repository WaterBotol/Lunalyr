param([string]$Out)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null

function Save-Jpeg($bmp, $path, $quality) {
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
    $params = New-Object System.Drawing.Imaging.EncoderParameters 1
    $params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), ([long]$quality)
    $bmp.Save($path, $codec, $params)
}

function Gradient($g, $w, $h, $c1, $c2, $angle) {
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, ([single]$angle)
    $g.FillRectangle($brush, $rect)
    $brush.Dispose()
}

# Warm: a sunset diagonal, deep red into gold, with a soft magenta disc.
$bmp = New-Object System.Drawing.Bitmap 600, 600
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
Gradient $g 600 600 ([System.Drawing.Color]::FromArgb(150, 24, 30)) ([System.Drawing.Color]::FromArgb(246, 176, 52)) 45
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 214, 52, 120))), 330, 60, 220, 220)
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 60, 14, 22))), 0, 470, 600, 130)
$g.Dispose(); Save-Jpeg $bmp (Join-Path $Out 'warm.jpg') 88; $bmp.Dispose()

# Cool: deep indigo to teal, with a pale cyan band.
$bmp = New-Object System.Drawing.Bitmap 512, 512
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
Gradient $g 512 512 ([System.Drawing.Color]::FromArgb(22, 28, 88)) ([System.Drawing.Color]::FromArgb(18, 140, 150)) 90
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 120, 220, 230))), -80, 300, 420, 160)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 96, 70, 190))), 340, 40, 130, 130)
$g.Dispose(); $bmp.Save((Join-Path $Out 'cool.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

# Mono: greys only, a black field with a white bar and a mid-grey disc.
$bmp = New-Object System.Drawing.Bitmap 400, 400
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
Gradient $g 400 400 ([System.Drawing.Color]::FromArgb(14, 14, 14)) ([System.Drawing.Color]::FromArgb(70, 70, 70)) 90
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 235, 235))), 40, 170, 320, 40)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(128, 128, 128))), 250, 40, 110, 110)
$g.Dispose(); Save-Jpeg $bmp (Join-Path $Out 'mono.jpg') 90; $bmp.Dispose()

# High contrast: saturated red and cyan stripes on black, a yellow square.
$bmp = New-Object System.Drawing.Bitmap 300, 300
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(8, 8, 10))
for ($x = 0; $x -lt 300; $x += 40) {
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 20, 40))), $x, 0, 14, 180)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(10, 220, 235))), ($x + 20), 120, 14, 180)
}
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 210, 20))), 200, 200, 80, 80)
$g.Dispose(); $bmp.Save((Join-Path $Out 'contrast.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

# Transparent corners: an opaque green disc on a clear 64x64 square.
$bmp = New-Object System.Drawing.Bitmap 64, 64, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::Transparent)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 180, 90))), 0, 0, 64, 64)
$g.Dispose(); $bmp.Save((Join-Path $Out 'disc.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

# Wide: 300x100, left half blue, right half orange.
$bmp = New-Object System.Drawing.Bitmap 300, 100
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 60, 200))), 0, 0, 150, 100)
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 130, 20))), 150, 0, 150, 100)
$g.Dispose(); Save-Jpeg $bmp (Join-Path $Out 'wide.jpg') 95; $bmp.Dispose()

# Not an image at all.
[System.IO.File]::WriteAllBytes((Join-Path $Out 'broken.jpg'), [byte[]](0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 74, 70, 73, 70, 0, 1, 2, 3, 4, 5))
Get-ChildItem $Out | Select-Object Name, Length

# Builds every image derived from branding/Thermonitor_logo.png:
#   src/Thermonitor/thermonitor.ico        app icon: "robot + thumb" at 48-256 px, "head" at 16-32 px
#   src/Thermonitor/wwwroot/img/mark.png   page header logo and favicon ("head", 64 px)
#   docs/logo.png                          README header (full logo, 520 px)
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File dev/make-icon.ps1   (Windows PowerShell 5.1)
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$source = Join-Path $root 'branding\Thermonitor_logo.png'

# Crops of the 1254 x 1254 source (x, y, width, height)
$Full = 175, 25, 900, 900    # robot, thermometer and thumb, no lettering
$Head = 395, 85, 560, 560    # skull, red eye and thermometer: stays readable at 16 px
$LargeSizes = 256, 128, 64, 48
$SmallSizes = 32, 24, 16

function New-Plate([float]$s) {
    # Rounded square like Windows app icons, inset so the rim is not clipped.
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $a = [Math]::Max(0.5, $s * 0.02); $b = $s - $a; $r = $s * 0.22
    $p.AddArc($a, $a, 2 * $r, 2 * $r, 180, 90); $p.AddArc($b - 2 * $r, $a, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($b - 2 * $r, $b - 2 * $r, 2 * $r, 2 * $r, 0, 90); $p.AddArc($a, $b - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure()
    return $p
}

function Get-Icon($img, [int[]]$crop, [int]$s) {
    $dst = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $plate = New-Plate $s
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $dark = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 34, 30, 30)), ([System.Drawing.Color]::FromArgb(255, 10, 8, 8)), 90.0
    $g.FillPath($dark, $plate)                       # the logo's own background is a ragged dark halo
    $g.SetClip($plate)
    $g.DrawImage($img, (New-Object System.Drawing.Rectangle 0, 0, $s, $s), (New-Object System.Drawing.Rectangle $crop[0], $crop[1], $crop[2], $crop[3]), 'Pixel')
    $g.ResetClip()
    $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(235, 236, 48, 40)), ([Math]::Max(1.0, $s * 0.035))
    $g.DrawPath($rim, (New-Plate $s))
    $g.Dispose()
    return $dst
}

function Get-Png($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()   # the comma keeps PowerShell from unrolling the byte array
}

# 32-bit DIB (BITMAPINFOHEADER + bottom-up BGRA + empty AND mask): readable by every Windows API.
function Get-Dib($bmp) {
    $n = $bmp.Width
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $n, $n), 'ReadOnly', ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $pixels = New-Object byte[] ($n * $n * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)
    $maskRow = [int]([Math]::Ceiling($n / 32.0)) * 4
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$n); $w.Write([Int32]($n * 2)); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]0); $w.Write([UInt32]($n * $n * 4 + $maskRow * $n)); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $n - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $n * 4, $n * 4) }
    $w.Write((New-Object byte[] ($maskRow * $n)))
    $w.Flush()
    return , $ms.ToArray()
}

function Save-Resized($img, [int]$size, [string]$path) {
    $dst = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.DrawImage($img, 0, 0, $size, $size)
    $g.Dispose()
    $dst.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $dst.Dispose()
}

$img = [System.Drawing.Bitmap]::FromFile($source)

# --- app icon
$entries = @()
foreach ($s in $LargeSizes + $SmallSizes) {
    $crop = if ($LargeSizes -contains $s) { $Full } else { $Head }
    $bmp = Get-Icon $img $crop $s
    $bytes = if ($s -ge 256) { Get-Png $bmp } else { Get-Dib $bmp }
    $bmp.Dispose()
    $entries += [pscustomobject]@{ Size = $s; Bytes = [byte[]]$bytes }
}
$ico = Join-Path $root 'src\Thermonitor\thermonitor.ico'
$fs = [System.IO.File]::Create($ico)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$e.Bytes.Length); $w.Write([UInt32]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $w.Write($e.Bytes) }
$w.Close()
"icon : $ico ($((Get-Item $ico).Length) octets)"

# --- page header logo and favicon (shown at 30 px, sharp up to 2x screens)
$mark = Get-Icon $img $Head 64
$markPath = Join-Path $root 'src\Thermonitor\wwwroot\img\mark.png'
$mark.Save($markPath, [System.Drawing.Imaging.ImageFormat]::Png); $mark.Dispose()
"page : $markPath ($((Get-Item $markPath).Length) octets)"

# --- README header
$readme = Join-Path $root 'docs\logo.png'
Save-Resized $img 520 $readme
"README : $readme ($((Get-Item $readme).Length) octets)"
$img.Dispose()

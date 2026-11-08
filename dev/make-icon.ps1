# Builds every image derived from branding/Thermonitor_logo.png:
#   src/Thermonitor/thermonitor.ico         app icon: "robot + thumb" at 48-256 px, "head" at 16-32 px
#   src/Thermonitor/wwwroot/img/logo.png    page header logo ("robot + thumb", 96 px)
#   src/Thermonitor/wwwroot/img/favicon.ico window / taskbar / tab icon ("head", 16-64 px)
#   docs/logo.png                           README header (full logo, 520 px)
# Small sizes are drawn at their exact pixel size (never scaled down by Windows or Chrome),
# on a tighter crop, then sharpened: the detailed artwork otherwise turns to mush at 24 px.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File dev/make-icon.ps1   (Windows PowerShell 5.1)
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class Sharpen {
  // Unsharp mask on RGB, alpha untouched: px + amount * (px - mean3x3)
  public static void Apply(Bitmap b, float amount) {
    var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    int w = b.Width, h = b.Height; var src = new byte[w * h * 4]; Marshal.Copy(d.Scan0, src, 0, src.Length);
    var dst = (byte[])src.Clone();
    for (int y = 1; y < h - 1; y++) for (int x = 1; x < w - 1; x++) for (int c = 0; c < 3; c++) {
      int sum = 0; for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) sum += src[((y + dy) * w + x + dx) * 4 + c];
      int i = (y * w + x) * 4 + c; float v = src[i] + amount * (src[i] - sum / 9f);
      dst[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v)));
    }
    Marshal.Copy(dst, 0, d.Scan0, dst.Length); b.UnlockBits(d);
  }
}
'@

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$source = Join-Path $root 'branding\Thermonitor_logo.png'

# Crops of the 1254 x 1254 source (x, y, width, height)
$Full = 175, 25, 900, 900    # robot, thermometer and thumb, no lettering
$Head = 440, 175, 420, 420   # skull, red eye and thermometer tube: readable down to 16 px

function New-Plate([float]$s) {
    # Rounded square like Windows app icons, inset so the rim is not clipped.
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $a = [Math]::Max(0.5, $s * 0.02); $b = $s - $a; $r = $s * 0.22
    $p.AddArc($a, $a, 2 * $r, 2 * $r, 180, 90); $p.AddArc($b - 2 * $r, $a, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($b - 2 * $r, $b - 2 * $r, 2 * $r, 2 * $r, 0, 90); $p.AddArc($a, $b - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure()
    return $p
}

function Get-Icon($img, [int[]]$crop, [int]$s, [float]$rimRatio) {
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
    $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(235, 236, 48, 40)), ([Math]::Max(1.0, $s * $rimRatio))
    $g.DrawPath($rim, (New-Plate $s))
    $g.Dispose()
    return $dst
}

function Get-LargeIcon($img, [int]$s) { Get-Icon $img $Full $s 0.035 }

function Get-SmallIcon($img, [int]$s) {
    $bmp = Get-Icon $img $Head $s 0.05
    [Sharpen]::Apply($bmp, 0.9)
    return $bmp
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

# $images: bitmaps of distinct square sizes. 256 px is stored as PNG (what Windows expects), the rest as DIBs.
function Save-Ico([string]$path, $images) {
    $entries = foreach ($bmp in $images) {
        $bytes = if ($bmp.Width -ge 256) { Get-Png $bmp } else { Get-Dib $bmp }
        [pscustomobject]@{ Size = $bmp.Width; Bytes = [byte[]]$bytes }
        $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $w = New-Object System.IO.BinaryWriter $fs
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]@($entries).Count)
    $offset = 6 + 16 * @($entries).Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$e.Bytes.Length); $w.Write([UInt32]$offset)
        $offset += $e.Bytes.Length
    }
    foreach ($e in $entries) { $w.Write($e.Bytes) }
    $w.Close()
    "$path ($((Get-Item $path).Length) octets, $(@($entries).Count) tailles)"
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
$wwwImg = Join-Path $root 'src\Thermonitor\wwwroot\img'

# --- app icon (exe, Explorer, Start menu)
'icone exe : ' + (Save-Ico (Join-Path $root 'src\Thermonitor\thermonitor.ico') @(
    (Get-LargeIcon $img 256), (Get-LargeIcon $img 128), (Get-LargeIcon $img 64), (Get-LargeIcon $img 48),
    (Get-SmallIcon $img 32), (Get-SmallIcon $img 24), (Get-SmallIcon $img 16)))

# --- favicon: the app window is a Chrome window, so its taskbar icon comes from here
'favicon : ' + (Save-Ico (Join-Path $wwwImg 'favicon.ico') @(
    (Get-SmallIcon $img 64), (Get-SmallIcon $img 48), (Get-SmallIcon $img 32), (Get-SmallIcon $img 24), (Get-SmallIcon $img 16)))

# --- page header logo (shown at 44 px, sharp on 2x screens)
$logo = Get-LargeIcon $img 96
$logoPath = Join-Path $wwwImg 'logo.png'
$logo.Save($logoPath, [System.Drawing.Imaging.ImageFormat]::Png); $logo.Dispose()
"en-tete : $logoPath ($((Get-Item $logoPath).Length) octets)"

# --- README header
$readme = Join-Path $root 'docs\logo.png'
Save-Resized $img 520 $readme
"README : $readme ($((Get-Item $readme).Length) octets)"
$img.Dispose()

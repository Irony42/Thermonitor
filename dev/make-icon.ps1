# Draws the app icon (same look as the page logo) into src/Thermonitor/thermonitor.ico.
#   powershell -NoProfile -ExecutionPolicy Bypass -File dev/make-icon.ps1
# Windows PowerShell 5.1 (System.Drawing). PNG-compressed entries at 16..256 px.
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\Thermonitor\thermonitor.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 24.0   # design grid: 24 x 24

    # Rounded square with the heat gradient (blue -> aqua -> yellow -> red), like .brand-mark
    $r = 6.5 * $s
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($size - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($size - 2 * $r, $size - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $size - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::Black), ([System.Drawing.Color]::Black), 45.0
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 4
    $blend.Colors = @(
        [System.Drawing.ColorTranslator]::FromHtml('#3987e5'),
        [System.Drawing.ColorTranslator]::FromHtml('#1baf7a'),
        [System.Drawing.ColorTranslator]::FromHtml('#eda100'),
        [System.Drawing.ColorTranslator]::FromHtml('#e34948'))
    $blend.Positions = @(0.0, 0.45, 0.7, 1.0)
    $brush.InterpolationColors = $blend
    $g.FillPath($brush, $path)

    # White thermometer: tube + bulb outline, filled "mercury" column
    $penWidth = [Math]::Max(1.2, 1.9 * $s)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $penWidth
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $tubeW = 4.2 * $s; $cx = 12 * $s; $top = 4.2 * $s; $bulbR = 3.6 * $s; $bulbCy = 16.6 * $s
    $t = New-Object System.Drawing.Drawing2D.GraphicsPath
    $t.AddArc($cx - $tubeW / 2, $top, $tubeW, $tubeW, 180, 180)
    $t.AddLine($cx + $tubeW / 2, $top + $tubeW / 2, $cx + $tubeW / 2, $bulbCy - $bulbR * 0.75)
    $t.AddArc($cx - $bulbR, $bulbCy - $bulbR, 2 * $bulbR, 2 * $bulbR, -55, 290)
    $t.CloseFigure()
    $g.DrawPath($pen, $t)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillEllipse($white, $cx - $bulbR * 0.55, $bulbCy - $bulbR * 0.55, $bulbR * 1.1, $bulbR * 1.1)
    if ($size -ge 24) { $g.FillRectangle($white, $cx - 0.6 * $s, 9 * $s, 1.2 * $s, $bulbCy - 9 * $s) }

    $g.Dispose()
    return $bmp
}

# 256 px as PNG (what Windows expects for that size); smaller ones as classic 32-bit DIBs,
# which every Windows API can read.
function To-Dib([System.Drawing.Bitmap]$bmp) {
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
    for ($y = $n - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $n * 4, $n * 4) }   # bottom-up BGRA rows
    $w.Write((New-Object byte[] ($maskRow * $n)))                                    # AND mask unused (alpha)
    $w.Flush()
    return , $ms.ToArray()   # the comma keeps PowerShell from unrolling the byte array
}

$images = foreach ($sz in $sizes) {
    $bmp = Draw-Icon $sz
    if ($sz -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    } else {
        $bytes = To-Dib $bmp
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $sz; Bytes = [byte[]]$bytes }
}

# ICO container: header, one directory entry per size, then the PNG payloads.
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$img.Bytes.Length); $w.Write([UInt32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $w.Write($img.Bytes) }
$w.Close()
"icon written: $((Resolve-Path $out).Path) ($((Get-Item $out).Length) bytes)"

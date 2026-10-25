# Generates src/Wavelit.App/Assets/wavelit.ico (and a 256px PNG) from code, so the artwork is original.
# Usage: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot '..\src\Wavelit.App\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [single]($size * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $s = [single]($size - 1)
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($s - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($s - $r * 2, $s - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $s - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 76, 41, 210)), ([System.Drawing.Color]::FromArgb(255, 236, 72, 153)), 45.0
    $g.FillPath($bg, $path)

    # Three flowing waves.
    $colors = @(
        [System.Drawing.Color]::FromArgb(235, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(170, 186, 230, 253),
        [System.Drawing.Color]::FromArgb(120, 253, 224, 71)
    )
    for ($w = 0; $w -lt 3; $w++) {
        $pen = New-Object System.Drawing.Pen $colors[$w], ([single]([Math]::Max(1.2, $size * 0.075)))
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
        $baseY = $size * (0.40 + $w * 0.15)
        for ($i = 0; $i -le 40; $i++) {
            $x = $size * (0.18 + 0.64 * $i / 40)
            $y = $baseY + [Math]::Sin(($i / 40) * [Math]::PI * 2 + $w * 0.9) * $size * 0.07
            $pts.Add((New-Object System.Drawing.PointF ([single]$x), ([single]$y)))
        }
        $g.DrawCurve($pen, $pts.ToArray())
        $pen.Dispose()
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($sz -eq 256) { $bmp.Save((Join-Path $outDir 'wavelit-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$fs = [System.IO.File]::Create((Join-Path $outDir 'wavelit.ico'))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $pngs[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $bw.Write($data) }
$bw.Close()
Write-Output "Wrote $(Join-Path $outDir 'wavelit.ico')"

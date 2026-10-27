# Generates the MSIX / Microsoft Store logos in packaging/Images from the same original artwork as make-icon.ps1
# (drawn from code at every size, so nothing is upscaled).
# Usage: powershell -ExecutionPolicy Bypass -File tools/make-store-assets.ps1
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot '..\packaging\Images'
New-Item -ItemType Directory -Force $outDir | Out-Null

# The mark: rounded gradient square with three flowing waves (same maths as make-icon.ps1), drawn into a box.
function Draw-Mark([System.Drawing.Graphics]$g, [single]$x0, [single]$y0, [single]$size) {
    $r = [single]($size * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $s = [single]($size - 1)
    $path.AddArc($x0, $y0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($x0 + $s - $r * 2, $y0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($x0 + $s - $r * 2, $y0 + $s - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc($x0, $y0 + $s - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF $x0, $y0, $size, $size
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 76, 41, 210)), ([System.Drawing.Color]::FromArgb(255, 236, 72, 153)), 45.0
    $g.FillPath($bg, $path)
    $bg.Dispose(); $path.Dispose()
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
            $x = $x0 + $size * (0.18 + 0.64 * $i / 40)
            $y = $y0 + $baseY + [Math]::Sin(($i / 40) * [Math]::PI * 2 + $w * 0.9) * $size * 0.07
            $pts.Add((New-Object System.Drawing.PointF ([single]$x), ([single]$y)))
        }
        $g.DrawCurve($pen, $pts.ToArray())
        $pen.Dispose()
    }
}

# A transparent canvas with the mark centred at `fill` of the shorter side (Store tiles want padding).
function Save-Logo([string]$name, [int]$w, [int]$h, [double]$fill) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $size = [single]([Math]::Min($w, $h) * $fill)
    Draw-Mark $g ([single](($w - $size) / 2)) ([single](($h - $size) / 2)) $size
    $g.Dispose()
    $bmp.Save((Join-Path $outDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

foreach ($scale in 100, 200) {
    $k = $scale / 100
    Save-Logo "Square44x44Logo.scale-$scale.png" (44 * $k) (44 * $k) 0.84
    Save-Logo "Square150x150Logo.scale-$scale.png" (150 * $k) (150 * $k) 0.6
    Save-Logo "Wide310x150Logo.scale-$scale.png" (310 * $k) (150 * $k) 0.6
    Save-Logo "StoreLogo.scale-$scale.png" (50 * $k) (50 * $k) 0.9
    Save-Logo "SplashScreen.scale-$scale.png" (620 * $k) (300 * $k) 0.5
}
# Taskbar and Start icons at exact pixel sizes (no plate, so Windows draws them crisp).
foreach ($px in 16, 24, 32, 48, 256) {
    Save-Logo "Square44x44Logo.targetsize-$px.png" $px $px 1.0
    Save-Logo "Square44x44Logo.targetsize-${px}_altform-unplated.png" $px $px 1.0
}
Write-Output "Wrote Store logos to $outDir"

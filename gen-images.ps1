<#
.SYNOPSIS
    Generates the MSIX package images (logos, widget icons, gallery previews)
    for the Mobile Data Widget using System.Drawing. Run from the repo root.

.DESCRIPTION
    App tiles use a neutral dark background with a white "signal bars" glyph.
    Per-carrier icons use the carrier brand colour with its initial. Previews are
    simple branded mock cards (placeholders until real Win+W screenshots exist).
#>

Add-Type -AssemblyName System.Drawing

$dir = Join-Path $PSScriptRoot "packaging\MobileData.Package\Images"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$fidoOrange   = [System.Drawing.Color]::FromArgb(232, 119, 34)
$rogersRed    = [System.Drawing.Color]::FromArgb(218, 41, 28)
$tileDark     = [System.Drawing.Color]::FromArgb(43, 43, 43)

function New-Graphics($bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return $g
}

function Save-Bmp($bmp, $name) {
    $path = Join-Path $dir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  $name ($($bmp.Width)x$($bmp.Height))" -ForegroundColor Green
}

# ---- App tile: dark background + white signal bars ----
function New-Tile([int]$w, [int]$h, [string]$name) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = New-Graphics $bmp
    $g.Clear($tileDark)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    $bw = [Math]::Max(2, [int]($h * 0.14))
    $gap = [Math]::Max(1, [int]($h * 0.07))
    $totalW = 3 * $bw + 2 * $gap
    $x0 = [int](($w - $totalW) / 2)
    $baseY = [int]($h * 0.70)
    $fracs = @(0.20, 0.34, 0.48)
    for ($i = 0; $i -lt 3; $i++) {
        $bh = [int]($h * $fracs[$i])
        $x = $x0 + $i * ($bw + $gap)
        $g.FillRectangle($white, $x, ($baseY - $bh), $bw, $bh)
    }
    $g.Dispose()
    Save-Bmp $bmp $name
}

# ---- Carrier icon: brand colour + white initial ----
function New-Icon([int]$size, [string]$name, $bg, [string]$letter) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = New-Graphics $bmp
    $g.Clear($bg)
    $font = New-Object System.Drawing.Font("Segoe UI", [int]($size * 0.58), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $g.DrawString($letter, $font, [System.Drawing.Brushes]::White, $rect, $sf)
    $g.Dispose()
    Save-Bmp $bmp $name
}

# ---- Gallery preview: branded mock card ----
function New-Preview([string]$name, $brand, [string]$title, [string]$big, [double]$fillFrac) {
    $w = 320; $h = 176
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = New-Graphics $bmp
    $g.Clear([System.Drawing.Color]::FromArgb(248, 248, 248))

    $dark = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(32, 32, 32))
    $brandBrush = New-Object System.Drawing.SolidBrush($brand)

    $titleFont = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $bigFont = New-Object System.Drawing.Font("Segoe UI", 30, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString($title, $titleFont, $dark, 20, 20)
    $g.DrawString($big, $bigFont, $brandBrush, 18, 52)

    # progress bar
    $barX = 20; $barY = 118; $barW = $w - 40; $barH = 14
    $trackBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 220, 220))
    $g.FillRectangle($trackBrush, $barX, $barY, $barW, $barH)
    $g.FillRectangle($brandBrush, $barX, $barY, [int]($barW * $fillFrac), $barH)

    $subFont = New-Object System.Drawing.Font("Segoe UI", 13, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $subBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(110, 110, 110))
    $g.DrawString("3 days remaining in bill cycle", $subFont, $subBrush, 20, 144)
    $g.Dispose()
    Save-Bmp $bmp $name
}

Write-Host "Generating package images..." -ForegroundColor Cyan
New-Tile 50  50  "StoreLogo.png"
New-Tile 44  44  "Square44x44Logo.png"
New-Tile 150 150 "Square150x150Logo.png"
New-Tile 310 150 "Wide310x150Logo.png"
New-Icon 96 "IconFido.png"   $fidoOrange "F"
New-Icon 96 "IconRogers.png" $rogersRed  "R"
New-Preview "PreviewFido.png"   $fidoOrange "Fido Data"   "38.78 GB left" 0.569
New-Preview "PreviewRogers.png" $rogersRed  "Rogers Data" "No data yet"   0.10
Write-Host "Done. Images in $dir" -ForegroundColor Cyan

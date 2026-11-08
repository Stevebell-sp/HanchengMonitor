# 產生瀚誠系統監控的多尺寸圖示（PNG 壓縮的 ICO，Windows Vista 以上支援）
param([string]$OutIco, [string]$OutPng)
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$s) {
    $bmp = [System.Drawing.Bitmap]::new($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0

    # 底：深藍到深紫漸層圓角方塊
    $rect = [System.Drawing.RectangleF]::new([float](8 * $k), [float](8 * $k), [float](240 * $k), [float](240 * $k))
    $path = New-RoundRect $rect.X $rect.Y $rect.Width $rect.Height (56 * $k)
    $bg = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, [System.Drawing.Color]::FromArgb(255, 22, 28, 48), [System.Drawing.Color]::FromArgb(255, 48, 22, 64), [float]45)
    $g.FillPath($bg, $path)

    # 玻璃光澤：上半部淡白
    $sheenRect = [System.Drawing.RectangleF]::new($rect.X, $rect.Y, $rect.Width, [float]($rect.Height * 0.5))
    $sheen = [System.Drawing.Drawing2D.LinearGradientBrush]::new($sheenRect, [System.Drawing.Color]::FromArgb(60, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255), [float]90)
    $g.SetClip($path)
    $g.FillRectangle($sheen, $sheenRect)
    $g.ResetClip()

    # 彩虹霓虹外框
    $edge = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, [System.Drawing.Color]::FromArgb(255, 40, 200, 255), [System.Drawing.Color]::FromArgb(255, 255, 100, 200), [float]45)
    $pen = [System.Drawing.Pen]::new($edge, [float]([Math]::Max(1.0, 8 * $k)))
    $g.DrawPath($pen, $path)

    # 三根發光長條：青、紫、粉
    $cols = @(
        [System.Drawing.Color]::FromArgb(255, 40, 200, 255),
        [System.Drawing.Color]::FromArgb(255, 175, 110, 255),
        [System.Drawing.Color]::FromArgb(255, 255, 100, 200)
    )
    $heights = @(70, 120, 165)
    for ($i = 0; $i -lt 3; $i++) {
        $bw = 38 * $k
        $bx = (52 + $i * 58) * $k
        $bh = $heights[$i] * $k
        $by = (200 * $k) - $bh
        $glow = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(70, $cols[$i]))
        $gp = New-RoundRect ($bx - 6 * $k) ($by - 6 * $k) ($bw + 12 * $k) ($bh + 12 * $k) (14 * $k)
        $g.FillPath($glow, $gp)
        $br = [System.Drawing.SolidBrush]::new($cols[$i])
        $bp = New-RoundRect $bx $by $bw $bh (10 * $k)
        $g.FillPath($br, $bp)
    }
    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $b = Draw-Icon $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    if ($s -eq 256 -and $OutPng) { $b.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
}

$fs = [System.IO.File]::Create($OutIco)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"ok"

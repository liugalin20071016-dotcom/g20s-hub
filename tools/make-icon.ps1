# 生成程序图标 assets\G20SHub.ico（多尺寸 PNG 帧）和 assets\icon-256.png。
# 图案：蓝色圆底 + 白色圆环（呼应 ☰ 窗口圆环），32px 以上再加四个方向点（↑→↓← 四个格子）。
# 只用系统自带的 System.Drawing；图标改了才需要重新运行，生成结果提交到仓库。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path $PSScriptRoot) 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [Math]::Max(0.5, $s * 0.03)
    $rect = New-Object System.Drawing.RectangleF $m, $m, ($s - 2 * $m), ($s - 2 * $m)
    $top = [System.Drawing.Color]::FromArgb(255, 0x40, 0x9C, 0xFF)
    $bottom = [System.Drawing.Color]::FromArgb(255, 0x00, 0x62, 0xE0)
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $top, $bottom, 90.0
    $g.FillEllipse($fill, $rect)

    $c = $s / 2.0
    $r = $s * ($(if ($s -ge 32) { 0.17 } else { 0.21 }))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ($s * ($(if ($s -ge 32) { 0.075 } else { 0.10 })))
    $g.DrawEllipse($pen, [float]($c - $r), [float]($c - $r), [float](2 * $r), [float](2 * $r))

    if ($s -ge 32) {
        $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
        $d = $s * 0.335; $dr = $s * 0.048
        foreach ($p in @(@(0, -1), @(1, 0), @(0, 1), @(-1, 0))) {
            $x = $c + $p[0] * $d; $y = $c + $p[1] * $d
            $g.FillEllipse($dot, [float]($x - $dr), [float]($y - $dr), [float](2 * $dr), [float](2 * $dr))
        }
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($s in $sizes) { , (New-IconPng $s) }

# ICO：6 字节文件头 + 每帧 16 字节目录 + PNG 数据
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))  # 256 记作 0
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'G20SHub.ico'), $out.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $assets 'icon-256.png'), $frames[-1])
Write-Host "已生成 $assets\G20SHub.ico（$($sizes -join '/')px）和 icon-256.png"

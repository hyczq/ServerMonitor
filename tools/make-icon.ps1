﻿﻿# 生成程序图标 app.ico（多尺寸）。
# 图案与标题栏左上角的标识一致：强调色圆角方块 + 白色机架图形。
# 输出到 src\ServerMonitor\app.ico
#
# 用法：powershell -ExecutionPolicy Bypass -File make-icon.ps1

Add-Type -AssemblyName System.Drawing

$outPath = Join-Path $PSScriptRoot '..\src\ServerMonitor\app.ico'
$outPath = [IO.Path]::GetFullPath($outPath)

# 与主题色一致
$accent = [System.Drawing.Color]::FromArgb(255, 0x39, 0x87, 0xE5)
$white  = [System.Drawing.Color]::White

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # 圆角方块底
    $radius = [double]$size * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush $accent
    $g.FillPath($brush, $path)

    # 机架：两个白色圆角横条 + 指示灯
    # 所有尺寸都按比例绘制，小图标才不会糊成一团
    $sz   = [double]$size
    $barX = $sz * 0.24
    $barW = $sz * 0.52
    $barH = $sz * 0.17
    $barR = $barH * 0.42
    $gap  = $sz * 0.10
    $top1 = $sz * 0.27
    $top2 = $top1 + $barH + $gap

    $whiteBrush  = New-Object System.Drawing.SolidBrush $white
    $accentBrush = New-Object System.Drawing.SolidBrush $accent

    $tops = @($top1, $top2)
    foreach ($top in $tops) {
        $t = [double]$top
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $dd = $barR * 2
        $p.AddArc([single]$barX, [single]$t, [single]$dd, [single]$dd, 180, 90)
        $p.AddArc([single]($barX + $barW - $dd), [single]$t, [single]$dd, [single]$dd, 270, 90)
        $p.AddArc([single]($barX + $barW - $dd), [single]($t + $barH - $dd), [single]$dd, [single]$dd, 0, 90)
        $p.AddArc([single]$barX, [single]($t + $barH - $dd), [single]$dd, [single]$dd, 90, 90)
        $p.CloseFigure()
        $g.FillPath($whiteBrush, $p)

        # 指示灯：小于 32px 时就省略，否则只会变成一个脏点
        if ($size -ge 32) {
            $dotD = $barH * 0.42
            $g.FillEllipse($accentBrush,
                [single]($barX + $barH * 0.30), [single]($t + ($barH - $dotD) / 2),
                [single]$dotD, [single]$dotD)
        }
        $p.Dispose()
    }

    $g.Dispose()
    $brush.Dispose(); $whiteBrush.Dispose(); $accentBrush.Dispose(); $path.Dispose()
    return $bmp
}

# 生成各尺寸的 PNG 数据
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , @{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

# 按 ICO 格式打包（PNG 压缩条目，Vista 及以上支持）
$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter $fs

$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type = icon
$bw.Write([UInt16]$images.Count)     # image count

$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img.Size
    $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))   # width（0 表示 256）
    $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))   # height
    $bw.Write([Byte]0)               # palette count
    $bw.Write([Byte]0)               # reserved
    $bw.Write([UInt16]1)             # color planes
    $bw.Write([UInt16]32)            # bits per pixel
    $bw.Write([UInt32]$img.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $bw.Write($img.Bytes) }

$bw.Flush(); $bw.Close(); $fs.Close()

Write-Host "已生成 $outPath" -ForegroundColor Green
Write-Host ("尺寸: " + (($sizes | ForEach-Object { "${_}x$_" }) -join ', '))
Write-Host ("文件大小: {0:N0} 字节" -f (Get-Item $outPath).Length)

# 顺带输出一张预览图，方便肉眼确认各尺寸下的观感
$preview = Join-Path $PSScriptRoot 'icon-preview.png'
$canvas = New-Object System.Drawing.Bitmap 420, 130
$cg = [System.Drawing.Graphics]::FromImage($canvas)
$cg.Clear([System.Drawing.Color]::FromArgb(255, 22, 28, 36))
$cg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$px = 12
foreach ($s in @(16, 24, 32, 48, 64)) {
    $b = New-IconBitmap $s
    $cg.DrawImage($b, $px, 34, $s, $s)
    $px += $s + 14
    $b.Dispose()
}
$big = New-IconBitmap 96
$cg.DrawImage($big, 300, 16, 96, 96)
$big.Dispose()
$cg.Dispose()
$canvas.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Dispose()
Write-Host "预览图: $preview" -ForegroundColor Green

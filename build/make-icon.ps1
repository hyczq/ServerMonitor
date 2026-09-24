# 生成程序图标 app.ico（多尺寸）。
# 图案与标题栏左上角的标识一致：强调色圆角方块 + 白色机架图形。
# 输出到 src\ServerMonitor\app.ico
#
# 用法：powershell -ExecutionPolicy Bypass -File build\make-icon.ps1

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

# PNG 条目数据。体积小，给大尺寸用。
function Get-IconPngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return $bytes
}

# 未压缩的 DIB 条目数据，给托盘用的小尺寸用。
#
# 为什么不全都用 PNG：通知区域的图标由 System.Drawing.Icon 读，而它只保证认得
# DIB 条目。PNG 压缩条目是 Vista 以后资源管理器的本事，不能假定托盘那条更老的
# 路径也认——本程序要跑在 Server 2012 R2 上，赌不起。
#
# GDI+ 存出来的 BMP 正好是 ICO 要的形状（40 字节 BITMAPINFOHEADER + BI_RGB +
# 自下而上的 32bpp 像素），只有两处必须改：
#   1) biHeight 记成两倍高：ICO 的 DIB 在像素之上还叠着一张 AND 掩码
#   2) 补上那张 AND 掩码（32bpp 自带 alpha，掩码全 0 = 不透明）
# biSizeImage 也顺手填上：GDI+ 存 BMP 时写 0，而 Windows 自己写图标时填的是
# "像素 + 掩码"的总长（实测 4bpp 16x16 的图标填 192 = 128 + 64），照着来最稳。
function Get-IconDibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $file = $ms.ToArray()
    $ms.Dispose()

    $dib = New-Object byte[] ($file.Length - 14)                    # 去掉 BITMAPFILEHEADER
    [Array]::Copy($file, 14, $dib, 0, $dib.Length)

    $headerSize = [BitConverter]::ToUInt32($dib, 0)
    $xorSize = $dib.Length - $headerSize

    $maskStride = [int][Math]::Ceiling($w / 32.0) * 4               # AND 掩码每行按 4 字节对齐
    $mask = New-Object byte[] ($maskStride * $h)

    [Array]::Copy([BitConverter]::GetBytes([int]($h * 2)), 0, $dib, 8, 4)              # biHeight
    [Array]::Copy([BitConverter]::GetBytes([int]($xorSize + $mask.Length)), 0, $dib, 20, 4)

    $out = New-Object byte[] ($dib.Length + $mask.Length)
    [Array]::Copy($dib, 0, $out, 0, $dib.Length)
    [Array]::Copy($mask, 0, $out, $dib.Length, $mask.Length)
    return $out
}

# 生成各尺寸的条目数据：小尺寸用 DIB（托盘要读），大尺寸用 PNG（省体积）
#
# [byte[]] 这个强转不能省：PowerShell 把函数的输出收进 Object[]，于是下面
# $bw.Write($img.Bytes) 会挑中"写一个字节"的那个重载，只写下数组的第一个字节
# （DIB 的第一个字节是 0x28）——文件小得像玩笑，且要到运行时才发现图标是坏的。
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $bytes = [byte[]]$(if ($s -le 48) { Get-IconDibBytes $bmp } else { Get-IconPngBytes $bmp })
    $images += , @{ Size = $s; Bytes = $bytes }
    $bmp.Dispose()
}

# 按 ICO 格式打包（小尺寸未压缩 DIB + 大尺寸 PNG）
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

# 自检：用托盘图标实际要走的那条路（System.Drawing.Icon）把每个尺寸都读一遍。
# 读不出来就当场失败——否则要等到程序在目标机上跑起来，才发现托盘里什么都没有。
foreach ($s in $sizes) {
    $check = [System.IO.File]::OpenRead($outPath)
    try {
        $probe = New-Object System.Drawing.Icon($check, $s, $s)
        $got = "{0}x{1}" -f $probe.Width, $probe.Height
        $probe.Dispose()
        Write-Host ("  {0,3} -> {1}" -f $s, $got)
    } catch {
        throw ("图标自检失败（{0}x{0}）：{1}" -f $s, $_.Exception.Message)
    } finally {
        $check.Close()
    }
}
Write-Host "图标自检通过" -ForegroundColor Green

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

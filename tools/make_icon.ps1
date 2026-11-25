# 生成 src/PlainSnip/App.ico（PixPin 风格：蓝紫渐变底 + 白色对角取景框 + 中心快门圆点，多尺寸）
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File tools\make_icon.ps1
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Drawing

$icoPath    = Join-Path $PSScriptRoot '..\src\PlainSnip\App.ico'
$previewDir = Join-Path $PSScriptRoot '..\.workbuddy'

# 设计坐标系固定 256，逐尺寸 4x 超采样绘制后高质量降采样
function New-RoundedPath([System.Drawing.RectangleF]$r, [float]$rad) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $rad * 2
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# $weight/$arm/$dot 均为 256 设计坐标系：线宽 / 角臂长 / 中心圆点半径
function New-IconBitmap([int]$size, [int]$weight, [int]$arm, [int]$dot) {
    $ss  = 4
    $big = $size * $ss
    $bmp = New-Object System.Drawing.Bitmap($big, $big)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode    = 'AntiAlias'
    $g.PixelOffsetMode  = 'HighQuality'
    $scale = $big / 256.0
    $g.ScaleTransform([float]$scale, [float]$scale)

    # 背景：对角渐变圆角矩形（蓝 → 靛紫）
    $bgRect = New-Object System.Drawing.RectangleF(10, 10, 236, 236)
    $bgPath = New-RoundedPath $bgRect 58
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $bgRect,
        [System.Drawing.Color]::FromArgb(255, 82, 140, 255),
        [System.Drawing.Color]::FromArgb(255, 108, 92, 246), 45)
    $g.FillPath($grad, $bgPath)
    $edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(45, 255, 255, 255), 3)
    $g.DrawPath($edge, $bgPath)

    # 白色对角取景框：四个 L 形角括号（对角分布），圆头粗线
    $f0 = 66; $f1 = 190   # 取景框左上/右下
    $fp = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(250, 255, 255, 255), $weight)
    $fp.StartCap = 'Round'; $fp.EndCap = 'Round'
    $fp.LineJoin = 'Round'
    foreach ($corner in @(
        @{ X = $f0; Y = $f0; DX = 1;  DY = 1  },   # 左上：横臂向右、竖臂向下
        @{ X = $f1; Y = $f0; DX = -1; DY = 1  },   # 右上
        @{ X = $f1; Y = $f1; DX = -1; DY = -1 },   # 右下
        @{ X = $f0; Y = $f1; DX = 1;  DY = -1 })) { # 左下
        $g.DrawLine($fp, $corner.X, $corner.Y, ($corner.X + $arm * $corner.DX), $corner.Y)
        $g.DrawLine($fp, $corner.X, $corner.Y, $corner.X, ($corner.Y + $arm * $corner.DY))
    }
    $fp.Dispose()

    # 中心快门圆点
    $c = ($f0 + $f1) / 2
    $g.FillEllipse([System.Drawing.Brushes]::White, ($c - $dot / 2), ($c - $dot / 2), $dot, $dot)

    $grad.Dispose(); $edge.Dispose(); $bgPath.Dispose(); $g.Dispose()

    # 降采样到目标尺寸
    $dst = New-Object System.Drawing.Bitmap($size, $size)
    $g2 = [System.Drawing.Graphics]::FromImage($dst)
    $g2.InterpolationMode = 'HighQualityBicubic'
    $g2.PixelOffsetMode   = 'HighQuality'
    $g2.DrawImage($bmp, 0, 0, $size, $size)
    $g2.Dispose(); $bmp.Dispose()
    return $dst
}

# Bitmap -> ICO DIB 条目（BITMAPINFOHEADER + 自底向上 BGRA + 全零 AND 掩码）
function ConvertTo-IcoDib([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = $w * 4
    $pixels = New-Object byte[] ($row * $h)
    for ($y = 0; $y -lt $h; $y++) {
        $ptr = [IntPtr]::Add($data.Scan0, [int64](($h - 1 - $y) * $data.Stride))
        [System.Runtime.InteropServices.Marshal]::Copy($ptr, $pixels, ($y * $row), $row)
    }
    $bmp.UnlockBits($data)

    $header = New-Object byte[] 40
    [BitConverter]::GetBytes([int]40).CopyTo($header, 0)        # biSize
    [BitConverter]::GetBytes([int]$w).CopyTo($header, 4)        # biWidth
    [BitConverter]::GetBytes([int]($h * 2)).CopyTo($header, 8)  # biHeight = 2x（XOR+AND）
    [BitConverter]::GetBytes([uint16]1).CopyTo($header, 12)     # biPlanes
    [BitConverter]::GetBytes([uint16]32).CopyTo($header, 14)    # biBitCount
    [BitConverter]::GetBytes([int]($row * $h)).CopyTo($header, 20)  # biSizeImage

    $maskRow = [math]::Ceiling($w / 32.0) * 4
    $mask = New-Object byte[] ([int]($maskRow * $h))

    $ms = New-Object System.IO.MemoryStream
    $ms.Write($header, 0, 40)
    $ms.Write($pixels, 0, $pixels.Length)
    $ms.Write($mask, 0, $mask.Length)
    return ,($ms.ToArray())   # 前置逗号防 PowerShell 把 byte[] 展开进管道
}

# ---- 组装 ICO：小尺寸加粗线（缩小后仍可辨），256 精细 ----
$entries = @(
    @{ Size = 16;  Weight = 22; Arm = 44; Dot = 32 },
    @{ Size = 24;  Weight = 20; Arm = 42; Dot = 28 },
    @{ Size = 32;  Weight = 19; Arm = 42; Dot = 26 },
    @{ Size = 48;  Weight = 17; Arm = 40; Dot = 24 },
    @{ Size = 256; Weight = 15; Arm = 36; Dot = 22 }
)

$blobs = @()
foreach ($e in $entries) {
    $bmp = New-IconBitmap $e.Size $e.Weight $e.Arm $e.Dot
    if ($e.Size -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $blobs += ,@{ Size = $e.Size; Data = $ms.ToArray() }
        $ms.Dispose()
        # 顺便落一张预览图
        New-Item -ItemType Directory -Force -Path $previewDir | Out-Null
        $bmp.Save((Join-Path $previewDir 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        $blobs += ,@{ Size = $e.Size; Data = (ConvertTo-IcoDib $bmp) }
    }
    $bmp.Dispose()
    Write-Host ("  尺寸 {0}px 条目已生成（{1} 字节）" -f $e.Size, $blobs[-1].Data.Length)
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0)              # reserved
$bw.Write([uint16]1)              # type: icon
$bw.Write([uint16]$blobs.Count)   # count
$offset = 6 + 16 * $blobs.Count
foreach ($b in $blobs) {
    $bw.Write([byte]($(if ($b.Size -eq 256) { 0 } else { $b.Size })))
    $bw.Write([byte]($(if ($b.Size -eq 256) { 0 } else { $b.Size })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$b.Data.Length)
    $bw.Write([uint32]$offset)
    $offset += $b.Data.Length
}
foreach ($b in $blobs) { $bw.Write([byte[]]$b.Data) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$bw.Dispose()
Write-Host ("App.ico 已写入: {0}（{1} 字节）" -f $icoPath, $out.Length)

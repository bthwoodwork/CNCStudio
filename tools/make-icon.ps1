# Draws the CNC Studio icon (an end mill cutting a glowing toolpath into a workpiece) at every
# icon size and writes src\CNCStudio.Desktop\Assets\cncstudio.ico plus a 256 px PNG preview.
# Each size is drawn as vectors at that size (not scaled down), so small icons stay crisp.
# Run:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "src\CNCStudio.Desktop\Assets"
New-Item -ItemType Directory -Force $outDir | Out-Null

function C($hex, $a = 255) { [System.Drawing.Color]::FromArgb($a, [Convert]::ToInt32($hex.Substring(0,2),16), [Convert]::ToInt32($hex.Substring(2,2),16), [Convert]::ToInt32($hex.Substring(4,2),16)) }
function RoundRect($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)
    $detail = $size -ge 32

    # Background tile: deep blue, like the app.
    $tile = RoundRect 8 8 240 240 48
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, 8), (New-Object System.Drawing.Point 0, 248), (C "24476E"), (C "0E1824"))
    $g.FillPath($bg, $tile)
    if ($detail) { $g.DrawPath((New-Object System.Drawing.Pen (C "5D7A96" 160), 3), $tile) }

    # Workpiece slab at the bottom.
    $g.SetClip($tile)
    $slab = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, 168), (New-Object System.Drawing.Point 0, 248), (C "3A536D"), (C "1B2734"))
    $g.FillRectangle($slab, 8, 168, 240, 80)
    $g.DrawLine((New-Object System.Drawing.Pen (C "8FB0CF"), 4), 8, 168, 248, 168)

    # Toolpath cut into the top of the slab, ending under the bit: a wide glowing band and a bright core.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddBezier(18, 214, 60, 214, 70, 186, 104, 186)
    $path.AddBezier(104, 186, 118, 186, 124, 190, 128, 190)
    $glow = New-Object System.Drawing.Pen (C "2EE69A" 90), ($(if ($detail) { 26 } else { 34 }))
    $glow.StartCap = 'Round'; $glow.EndCap = 'Round'
    $g.DrawPath($glow, $path)
    $core = New-Object System.Drawing.Pen (C "45F0A8"), ($(if ($detail) { 9 } else { 16 }))
    $core.StartCap = 'Round'; $core.EndCap = 'Round'
    $g.DrawPath($core, $path)
    $g.ResetClip()

    # Small sizes: the bit is drawn wider so it still reads as a cutter at 16-24 px.
    if (-not $detail) { $g.TranslateTransform(128, 0); $g.ScaleTransform(1.7, 1); $g.TranslateTransform(-128, 0) }

    # Spindle collet (top) and shank.
    $collet = New-Object System.Drawing.Drawing2D.GraphicsPath
    $collet.AddPolygon([System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 92, 22), (New-Object System.Drawing.PointF 164, 22), (New-Object System.Drawing.PointF 150, 64), (New-Object System.Drawing.PointF 106, 64)))
    $metal = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 92, 0), (New-Object System.Drawing.Point 164, 0), (C "9FB3C8"), (C "E8F1FA"))
    $metal.SetBlendTriangularShape(0.55)
    $g.FillPath($metal, $collet)
    if ($detail) { $g.DrawLine((New-Object System.Drawing.Pen (C "6F8499"), 3), 99, 43, 157, 43) }
    $g.FillRectangle($metal, 114, 64, 28, 40)

    # Fluted cutting part: darker steel with diagonal flutes, ending in the cut.
    $flute = New-Object System.Drawing.Drawing2D.GraphicsPath
    $flute.AddLine(112, 104, 144, 104); $flute.AddLine(144, 104, 144, 178); $flute.AddLine(144, 178, 136, 188)
    $flute.AddLine(136, 188, 120, 188); $flute.AddLine(120, 188, 112, 178); $flute.CloseFigure()
    $steel = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 112, 0), (New-Object System.Drawing.Point 144, 0), (C "7C93AA"), (C "D5E2EE"))
    $steel.SetBlendTriangularShape(0.6)
    $g.FillPath($steel, $flute)
    if ($detail) {
        $g.SetClip($flute)
        $dark = New-Object System.Drawing.Pen (C "3E5266"), 7
        foreach ($y in 92, 116, 140, 164, 188) { $g.DrawLine($dark, 108, $y + 18, 148, $y - 6) }
        $g.ResetClip()
        $g.DrawPath((New-Object System.Drawing.Pen (C "2B3A4A"), 2.5), $flute)
    }

    # A small hot spot where the bit meets the material.
    if ($detail) {
        $spark = New-Object System.Drawing.SolidBrush (C "FFFFFF" 230)
        $g.FillEllipse($spark, 123, 183, 10, 8)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

# Icon image data: 256 px as PNG, smaller sizes as classic 32-bit DIBs (BGRA, bottom-up, plus an
# all-zero AND mask) – the most compatible form for Windows and for .NET's Icon class.
function Dib($bmp) {
    $sz = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $sz, $sz
    $data = $bmp.LockBits($rect, 'ReadOnly', ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $pixels = New-Object byte[] ($data.Stride * $sz)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)
    $maskRow = [int]([Math]::Ceiling($sz / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$sz); $w.Write([Int32]($sz * 2)); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]0); $w.Write([UInt32]($sz * $sz * 4 + $maskRow * $sz)); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $sz - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $data.Stride, $sz * 4) }
    $w.Write((New-Object byte[] ($maskRow * $sz)))
    $w.Flush()
    return $ms.ToArray()
}

$images = @()
foreach ($sz in $sizes) {
    $bmp = Draw $sz
    if ($sz -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += ,@($sz, $ms.ToArray())
        $bmp.Save((Join-Path $outDir "cncstudio-256.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else { $images += ,@($sz, (Dib $bmp)) }
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create((Join-Path $outDir "cncstudio.ico"))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($p in $images) {
    $sz = $p[0]; $data = $p[1]
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $images) { $w.Write([byte[]]$p[1]) }
$w.Close()
"Wrote $(Join-Path $outDir 'cncstudio.ico') with sizes $($sizes -join ', ')"
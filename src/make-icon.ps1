# Draws the DuoSeat icon (two cursors, orange + blue) into a multi-size .ico
param([string]$Out = "$PSScriptRoot\duoseat.ico")
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $k = $s / 256.0
  $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
  $r = 56 * $k; $w = $s - 1
  $bg.AddArc(0, 0, $r, $r, 180, 90); $bg.AddArc($w - $r, 0, $r, $r, 270, 90)
  $bg.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $bg.AddArc(0, $w - $r, $r, $r, 90, 90); $bg.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(24, 28, 40))), $bg)
  function Arrow($ox, $oy, $color) {
    $pts = @((0,0),(0,120),(30,92),(50,138),(72,128),(52,84),(92,84)) | ForEach-Object { New-Object System.Drawing.PointF (($ox + $_[0]) * $k), (($oy + $_[1]) * $k) }
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $color), [System.Drawing.PointF[]]$pts)
    $g.DrawPolygon((New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1, 8 * $k))), [System.Drawing.PointF[]]$pts)
  }
  Arrow 44 40 ([System.Drawing.Color]::FromArgb(0, 120, 215))
  Arrow 128 82 ([System.Drawing.Color]::FromArgb(255, 140, 0))
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += , $ms.ToArray()
  $bmp.Dispose()
}
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()

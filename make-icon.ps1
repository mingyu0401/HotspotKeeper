# Build helper: wrap Assets/avatar.jpg into a 256x256 PNG-compressed ICO at Assets/App.ico
Add-Type -AssemblyName System.Drawing

$src = Join-Path $PSScriptRoot 'Assets\avatar.jpg'
$dst = Join-Path $PSScriptRoot 'Assets\App.ico'

$img = [System.Drawing.Image]::FromFile($src)
# center-crop to square
$side = [Math]::Min($img.Width, $img.Height)
$cropped = [System.Drawing.Bitmap]::new($side, $side)
$g0 = [System.Drawing.Graphics]::FromImage($cropped)
$g0.DrawImage($img, (New-Object System.Drawing.Rectangle(0, 0, $side, $side)),
    (New-Object System.Drawing.Rectangle([int](($img.Width - $side) / 2), [int](($img.Height - $side) / 2), $side, $side)),
    [System.Drawing.GraphicsUnit]::Pixel)
$g0.Dispose(); $img.Dispose()

$bmp = [System.Drawing.Bitmap]::new(256, 256)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.DrawImage($cropped, 0, 0, 256, 256)
$g.Dispose(); $cropped.Dispose()

$ms = [System.IO.MemoryStream]::new()
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
$png = $ms.ToArray(); $ms.Dispose()

$out = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)   # header: reserved, type=icon, count=1
$bw.Write([byte]0); $bw.Write([byte]0)                              # 256x256
$bw.Write([byte]0); $bw.Write([byte]0)                              # colors, reserved
$bw.Write([uint16]1); $bw.Write([uint16]32)                         # planes, bpp
$bw.Write([uint32]$png.Length); $bw.Write([uint32]22)               # size, offset
$bw.Write($png)
$bw.Flush()
[System.IO.File]::WriteAllBytes($dst, $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Host "icon written: $dst ($((Get-Item $dst).Length) bytes)"

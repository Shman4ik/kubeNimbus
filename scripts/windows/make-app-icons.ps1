# Assembles the shipped app icons from the design masters. The masters are
# rendered per size from vector by scripts/design/make-masters.ps1 (16 and 24
# from their own simplified marks, not downscales) - this script does NOT
# resample the small, legibility-critical sizes: it copies them verbatim and
# only downscales the larger, non-critical sizes from a bigger master.
#
#   INPUT  design/masters/icon/icon-{16,24,32,48,256,1024}.png   square tiles
#          design/masters/window/window-{light,dark}-256.png     transparent glyph
#
#   OUTPUT src/KubeNimbus.App/Assets/app.ico             exe + installer icon (multi-size)
#          src/KubeNimbus.App/Assets/window-icon-light.ico   light-theme window icon
#          src/KubeNimbus.App/Assets/window-icon-dark.ico    dark-theme  window icon
#          src/KubeNimbus.App/Assets/Msix/{Square44x44Logo,Square150x150Logo,StoreLogo}
#              .scale-{100,125,150,200,400}.png           MSIX plated tiles, one file per DPI
#          src/KubeNimbus.App/Assets/Msix/Square44x44Logo
#              .targetsize-{16,24,32,48,256}_altform-{unplated,lightunplated}.png
#              transparent taskbar/Alt+Tab/Start icon - without these, Windows adds
#              its own backplate around the plated logo on those surfaces
#
# Windows-only (uses System.Drawing/GDI+). Run after the masters change:
#   pwsh scripts/design/make-masters.ps1
#   pwsh scripts/windows/make-app-icons.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo    = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$iconDir = Join-Path $repo 'design\masters\icon'
$winDir  = Join-Path $repo 'design\masters\window'
$outDir  = Join-Path $repo 'src\KubeNimbus.App\Assets'
$msixDir = Join-Path $outDir 'Msix'

function Get-Master([int]$size) {
    $p = Join-Path $iconDir "icon-$size.png"
    if (-not (Test-Path $p)) { throw "Missing icon master: $p (run scripts/design/make-masters.ps1)" }
    return $p
}

# A square tile bitmap at the requested size. If a master exists at exactly that
# size it is loaded as-is (no resample); otherwise it is high-quality-downscaled
# from `fromSize` (always a LARGER master, never upscaled) so detail survives.
function Get-Tile([int]$size, [int]$fromSize) {
    $exact = Join-Path $iconDir "icon-$size.png"
    if (Test-Path $exact) {
        return New-Object System.Drawing.Bitmap($exact)
    }
    $src = New-Object System.Drawing.Bitmap((Get-Master $fromSize))
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose(); $src.Dispose()
    return $bmp
}

# Picks the window-glyph master for a size: an exact-size one when it exists
# (24 and 16 have their own, drawn from the simplified marks), else the 256 to
# downscale from. Same rule as the plated tiles - never resample the small,
# legibility-critical sizes out of a master drawn for a bigger one.
function Resolve-WindowMaster([string]$theme, [int]$size) {
    $exact = Join-Path $winDir "window-$theme-$size.png"
    if (Test-Path $exact) { return $exact }
    return (Join-Path $winDir "window-$theme-256.png")
}

# Alpha-preserving downscale of a transparent master. Used for the unplated
# MSIX taskbar/Alt+Tab icons, sourced from the window-glyph masters.
function Get-TransparentTile([string]$masterPath, [int]$size) {
    $src = New-Object System.Drawing.Bitmap($masterPath)
    if ($src.Width -eq $size -and $src.Height -eq $size) { return $src }
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose(); $src.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray(); $ms.Dispose()
    Write-Output -NoEnumerate $bytes
}

# Classic uncompressed ICO entry: BITMAPINFOHEADER + bottom-up BGRA + AND mask.
# Only app.ico needs this: the Windows shell itself reads that file (Explorer,
# installer/ARP), and there PNG compression is only spec-blessed for the 256px
# entry - smaller sizes go in as plain BMP for maximum shell compatibility. The
# window-icon .ico files below are all-PNG instead: they are decoded only
# in-app (Avalonia), never handed to the shell as a file.
#
# The AND mask is built from the alpha channel, NOT left zeroed. A zeroed mask
# means "every pixel opaque", and consumers that honour the mask instead of the
# 32bpp alpha (icon editors, older shell paths) then paint the disc's
# transparent corners with whatever RGB happens to sit under alpha 0 - white
# for the PNG masters copied verbatim, but BLACK for the sizes GDI+ produces by
# downscaling, which is what made 64/128 render as a black square next to a
# clean 256. Masked pixels also get their RGB zeroed, because a mask bit means
# "XOR this colour onto the screen": a non-zero colour there inverts the
# background instead of leaving it alone.
function Get-BmpEntryBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40)          # BITMAPINFOHEADER size
    $bw.Write([int32]$s)           # width
    $bw.Write([int32]($s * 2))     # height (XOR + AND mask)
    $bw.Write([uint16]1)           # planes
    $bw.Write([uint16]32)          # bpp
    $bw.Write([uint32]0)           # BI_RGB
    $bw.Write([uint32]0); $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)  # 1bpp AND mask, rows padded to 32 bits
    $mask = New-Object byte[] ($maskRow * $s)
    for ($y = $s - 1; $y -ge 0; $y--) {       # bottom-up BGRA rows
        $maskOffset = ($s - 1 - $y) * $maskRow
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            if ($c.A -eq 0) {
                $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0)
                $mask[$maskOffset + [int][Math]::Floor($x / 8)] = $mask[$maskOffset + [int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8))
            } else {
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
    }
    $bw.Write($mask)
    $bw.Flush()
    $bytes = $ms.ToArray(); $bw.Dispose(); $ms.Dispose()
    Write-Output -NoEnumerate $bytes
}

# Writes a multi-size .ico from already-encoded per-size payloads.
function Write-Ico([object[]]$Entries, [string]$Path) {
    $ms = New-Object System.IO.MemoryStream
    $w  = New-Object System.IO.BinaryWriter($ms)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$Entries.Count)
    $offset = 6 + 16 * $Entries.Count
    foreach ($e in $Entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]([byte[]]$e.Bytes).Length); $w.Write([uint32]$offset)
        $offset += ([byte[]]$e.Bytes).Length
    }
    foreach ($e in $Entries) { $w.Write([byte[]]$e.Bytes) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    $w.Dispose(); $ms.Dispose()
}

New-Item -ItemType Directory -Force -Path $msixDir | Out-Null

# --- per-theme window icons: a real multi-size .ico (16/24/32/48/256, all
#     PNG-compressed entries - Windows Vista+ decodes PNG at any .ico size, so
#     this needs no BMP fallback like app.ico's legacy sizes do) built from the
#     transparent 256px glyph. A flat single-size PNG here leaves a Win32
#     WM_SETICON call with only one oversized image to downscale, which Windows
#     silently fails to apply to the title bar/taskbar on some Windows 11 builds.
$windowIconSizes = 16, 24, 32, 48, 256
foreach ($pair in @(
        @{ Theme = 'light'; Dst = 'window-icon-light.ico' },
        @{ Theme = 'dark';  Dst = 'window-icon-dark.ico' })) {
    $s = Resolve-WindowMaster $pair.Theme 256
    if (-not (Test-Path $s)) { throw "Missing window master: $s (run scripts/design/make-masters.ps1)" }
    $entries = foreach ($size in $windowIconSizes) {
        $t = Get-TransparentTile (Resolve-WindowMaster $pair.Theme $size) $size
        $bytes = Get-PngBytes $t
        $t.Dispose()
        @{ Size = $size; Bytes = $bytes }
    }
    Write-Ico $entries (Join-Path $outDir $pair.Dst)
    Write-Host ("wrote src\KubeNimbus.App\Assets\$($pair.Dst) ({0} sizes)" -f ($windowIconSizes -join ', '))
}

# --- app.ico: 16/24/32/48/256 are per-size masters copied as-is; 64/128 are
#     downscaled from the 256 master ---
$icoPlan = @(
    @{ Size = 16;  From = 16  }, @{ Size = 24;  From = 24  },
    @{ Size = 32;  From = 32  }, @{ Size = 48;  From = 48  },
    @{ Size = 64;  From = 256 }, @{ Size = 128; From = 256 },
    @{ Size = 256; From = 256 })
$images = foreach ($e in $icoPlan) {
    $t = Get-Tile $e.Size $e.From
    if ($e.Size -ge 256) { [byte[]]$b = Get-PngBytes $t } else { [byte[]]$b = Get-BmpEntryBytes $t }
    $t.Dispose()
    @{ Size = $e.Size; Bytes = $b }
}
Write-Ico $images (Join-Path $outDir 'app.ico')
Write-Host ("wrote src\KubeNimbus.App\Assets\app.ico ({0} sizes: {1})" -f $images.Count, (($icoPlan | ForEach-Object { $_.Size }) -join ', '))

# --- MSIX plated tiles: one file per DPI scale factor (100/125/150/200/400%)
#     for each logo, instead of a single flat file - Windows falls back to
#     scaling (and backplating) a lone unqualified asset when it cannot find a
#     qualifier-matched size for the surface it is rendering. Small tiles
#     (Square44x44Logo/StoreLogo) come from the 48 master so the glyph stays
#     crisp; the medium tile (Square150x150Logo) from the 256 master - but
#     scale-200/400 can exceed both, so anything larger than the logo's small
#     master falls back to the 1024 master instead of upscaling (blurring) it.
$msixScales = @(
    @{ Suffix = 'scale-100'; Factor = 1.0 },
    @{ Suffix = 'scale-125'; Factor = 1.25 },
    @{ Suffix = 'scale-150'; Factor = 1.5 },
    @{ Suffix = 'scale-200'; Factor = 2.0 },
    @{ Suffix = 'scale-400'; Factor = 4.0 })
foreach ($logo in @(
        @{ Base = 44;  SmallFrom = 48;  Name = 'Square44x44Logo' },
        @{ Base = 50;  SmallFrom = 48;  Name = 'StoreLogo' },
        @{ Base = 150; SmallFrom = 256; Name = 'Square150x150Logo' })) {
    foreach ($s in $msixScales) {
        $size = [int][Math]::Round($logo.Base * $s.Factor)
        $from = if ($size -le $logo.SmallFrom) { $logo.SmallFrom } else { 1024 }
        $t = Get-Tile $size $from
        [System.IO.File]::WriteAllBytes((Join-Path $msixDir "$($logo.Name).$($s.Suffix).png"), (Get-PngBytes $t))
        $t.Dispose()
    }
    Write-Host "wrote src\KubeNimbus.App\Assets\Msix\$($logo.Name).scale-{100,125,150,200,400}.png"
}

# --- MSIX unplated Square44x44Logo: transparent taskbar/Alt+Tab/Start icon.
#     Dark-theme (altform-unplated) uses the light-drawn window-dark glyph;
#     light-theme (altform-lightunplated) uses the dark-drawn window-light one.
$unplatedSizes = 16, 24, 32, 48, 256
foreach ($pair in @(
        @{ Theme = 'dark';  Suffix = 'altform-unplated' },
        @{ Theme = 'light'; Suffix = 'altform-lightunplated' })) {
    foreach ($size in $unplatedSizes) {
        $src = Resolve-WindowMaster $pair.Theme $size
        if (-not (Test-Path $src)) { throw "Missing window master: $src" }
        $t = Get-TransparentTile $src $size
        $name = "Square44x44Logo.targetsize-${size}_$($pair.Suffix).png"
        [System.IO.File]::WriteAllBytes((Join-Path $msixDir $name), (Get-PngBytes $t))
        $t.Dispose()
    }
}
Write-Host "wrote src\KubeNimbus.App\Assets\Msix\Square44x44Logo.targetsize-{16,24,32,48,256}_altform-{unplated,lightunplated}.png"

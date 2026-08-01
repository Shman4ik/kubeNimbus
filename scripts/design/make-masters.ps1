# Renders every design master from the three committed SVG marks. Unlike
# pgNimbus - whose masters are hand-drawn bitmaps and whose scripts only
# assemble them - kubeNimbus's mark is vector, so the masters themselves are
# generated here and the small-size "hand drawing" happens once, in SVG
# (design/logo-small.svg and design/logo-micro.svg). See design/LOGO-ASSETS.md.
#
#   INPUT  design/logo.svg        + logo-dark.svg         full mark (>= 32 px)
#          design/logo-small.svg  + logo-small-dark.svg   simplified (24 px)
#          design/logo-micro.svg  + logo-micro-dark.svg   simplified (16 px)
#
#   OUTPUT design/masters/icon/icon-{16,24,32,48,256,1024}.png   square tiles
#          design/masters/window/window-{light,dark}-256.png     transparent glyph
#          design/masters/logo/wordmark-{light,dark}.svg         lockup, text baked to paths
#          design/masters/logo/wordmark-{light,dark}.png         same at 2x
#          design/masters/logo/social-preview.png                1280x640, solid bg
#
# Needs Inkscape (rasteriser + text-to-path) and, for the social card,
# System.Drawing - so: Windows. Run after editing any design/logo*.svg, then
# run scripts/windows/make-app-icons.ps1 to rebuild the shipped icons.
#
#   pwsh scripts/design/make-masters.ps1
param(
    # Override when Inkscape lives somewhere else (or is only on PATH).
    [string]$Inkscape
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo      = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$designDir = Join-Path $repo 'design'
$iconDir   = Join-Path $designDir 'masters\icon'
$winDir    = Join-Path $designDir 'masters\window'
$logoDir   = Join-Path $designDir 'masters\logo'
$tmpDir    = Join-Path ([System.IO.Path]::GetTempPath()) ("kubenimbus-masters-" + [guid]::NewGuid().ToString('n'))

foreach ($d in @($iconDir, $winDir, $logoDir, $tmpDir)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

if (-not $Inkscape) {
    $candidates = @(
        'C:\Program Files\Inkscape\bin\inkscape.com',
        'C:\Program Files (x86)\Inkscape\bin\inkscape.com') +
        @((Get-Command inkscape -ErrorAction SilentlyContinue).Source)
    $Inkscape = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Inkscape) { throw "Inkscape not found. Install it or pass -Inkscape <path to inkscape.com>." }

function Invoke-Inkscape([string[]]$InkArgs) {
    $out = & $Inkscape @InkArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "inkscape $($InkArgs -join ' ') failed:`n$out" }
}

# SVG -> PNG at an exact pixel size. Inkscape honours the <style> block, so the
# .ink/.paper classes render the same way a browser shows them.
function Export-Png([string]$Svg, [string]$Png, [int]$Size) {
    Invoke-Inkscape @($Svg, '--export-type=png', "--export-filename=$Png", '-w', "$Size", '-h', "$Size")
    Write-Host ("wrote {0} ({1}px)" -f ($Png.Substring($repo.Length + 1)), $Size)
}

# ---------------------------------------------------------------- icon tiles
# Which master feeds which size is the whole point of having three marks: the
# full traced mark below 32px is mud (see LOGO-ASSETS.md "Why there are three
# masters"), so 24 and 16 come from the simplified ones instead of a downscale.
$iconPlan = @(
    @{ Size = 16;   Src = 'logo-micro.svg' },
    @{ Size = 24;   Src = 'logo-small.svg' },
    @{ Size = 32;   Src = 'logo.svg' },
    @{ Size = 48;   Src = 'logo.svg' },
    @{ Size = 256;  Src = 'logo.svg' },
    @{ Size = 1024; Src = 'logo.svg' })
foreach ($e in $iconPlan) {
    Export-Png (Join-Path $designDir $e.Src) (Join-Path $iconDir "icon-$($e.Size).png") $e.Size
}

# ------------------------------------------------------------ window masters
# Transparent line art for the unplated taskbar/Alt+Tab surfaces: the same mark
# with the disc removed, so what is left is the two-tone glyph on transparency.
# Stripping the <circle> from the committed SVG (rather than keeping two more
# hand-maintained files) keeps design/logo*.svg the single source of geometry.
function New-GlyphSvg([string]$SrcSvg, [string]$DstSvg) {
    $svg = Get-Content -Raw $SrcSvg
    $stripped = [regex]::Replace($svg, '\s*<circle[^>]*\br="512"[^>]*/>', '')
    if ($stripped -eq $svg) { throw "No full-bleed disc (r=512) found to strip in $SrcSvg" }
    Set-Content -Path $DstSvg -Value $stripped -Encoding UTF8
}
# window-light = for LIGHT surfaces, so the field must be the dark ink: that is
# logo-dark.svg's palette. window-dark is the mirror. (Same convention as
# pgNimbus: the name is the theme it is used *on*, not the colour it is drawn in.)
foreach ($pair in @(
        @{ Src = 'logo-dark.svg'; Dst = 'window-light-256.png' },
        @{ Src = 'logo.svg';      Dst = 'window-dark-256.png' })) {
    $glyph = Join-Path $tmpDir ("glyph-" + $pair.Dst + ".svg")
    New-GlyphSvg (Join-Path $designDir $pair.Src) $glyph
    Export-Png $glyph (Join-Path $winDir $pair.Dst) 256
}

# ------------------------------------------------------------------ wordmark
# (There is deliberately no bare-mark PNG master: `masters/icon/icon-1024.png`
# already *is* logo.svg at 1024, and a second copy of the same render under
# masters/logo/ only invites the two to drift apart.)
# Horizontal lockup: the mark at 240px beside "kubeNimbus" set in Segoe UI
# Bold. The text is baked to paths by Inkscape so the committed SVG renders
# identically on a machine without that font (GitHub's, for one).
$markHeight = 240.0
$markScale  = $markHeight / 1024.0
$gap        = 56.0
$fontSize   = 150.0
$pad        = 24.0

function New-WordmarkSvg([string]$SrcSvg, [string]$TextFill, [string]$DstSvg) {
    $svg = Get-Content -Raw $SrcSvg
    # Body = everything after the </style> close: the mark's own geometry, which
    # carries a literal fill on every path, so dropping class= loses nothing and
    # frees the lockup from needing the <style> block (and from colliding with
    # the text's own fill).
    $body = $svg.Substring($svg.IndexOf('</style>') + '</style>'.Length)
    $body = $body.Substring(0, $body.LastIndexOf('</svg>'))
    $body = [regex]::Replace($body, '\sclass="[^"]*"', '')
    $textX = $markHeight + $gap
    @"
<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="$markHeight" viewBox="0 0 1200 $markHeight">
  <g transform="scale($markScale)">$body</g>
  <text x="$textX" y="$($markHeight / 2)" fill="$TextFill"
        style="font-family:'Segoe UI', Arial, Helvetica, sans-serif;font-weight:700;font-size:${fontSize}px;dominant-baseline:central">kubeNimbus</text>
</svg>
"@ | Set-Content -Path $DstSvg -Encoding UTF8
}

# Inkscape's own measurement of the baked document, in px (--query-* is the
# only reliable way to size the lockup: the text's advance width depends on the
# font, so it cannot be hardcoded).
function Get-DocBox([string]$Svg) {
    $x = [double](& $Inkscape $Svg --query-x)
    $y = [double](& $Inkscape $Svg --query-y)
    $w = [double](& $Inkscape $Svg --query-width)
    $h = [double](& $Inkscape $Svg --query-height)
    return @{ X = $x; Y = $y; W = $w; H = $h }
}

foreach ($v in @(
        @{ Src = 'logo.svg';      Fill = '#242b36'; Name = 'wordmark-light' },
        @{ Src = 'logo-dark.svg'; Fill = '#f5f7fa'; Name = 'wordmark-dark' })) {
    $raw   = Join-Path $tmpDir "$($v.Name)-raw.svg"
    $baked = Join-Path $logoDir "$($v.Name).svg"
    New-WordmarkSvg (Join-Path $designDir $v.Src) $v.Fill $raw
    Invoke-Inkscape @($raw, '--export-type=svg', '--export-plain-svg', '--export-text-to-path', "--export-filename=$baked")

    # Tighten the viewBox onto the baked content + uniform padding.
    $box  = Get-DocBox $baked
    $vbX  = [Math]::Round($box.X - $pad, 2)
    $vbY  = [Math]::Round($box.Y - $pad, 2)
    $vbW  = [Math]::Round($box.W + 2 * $pad, 2)
    $vbH  = [Math]::Round($box.H + 2 * $pad, 2)
    $text = Get-Content -Raw $baked
    $text = [regex]::Replace($text, '(<svg\b[^>]*?)\swidth="[^"]*"', '$1', 'Singleline')
    $text = [regex]::Replace($text, '(<svg\b[^>]*?)\sheight="[^"]*"', '$1', 'Singleline')
    $text = [regex]::Replace($text, '(<svg\b[^>]*?)\sviewBox="[^"]*"', '$1', 'Singleline')
    $text = [regex]::Replace($text, '<svg\b', "<svg width=`"$vbW`" height=`"$vbH`" viewBox=`"$vbX $vbY $vbW $vbH`"", 'Singleline')
    Set-Content -Path $baked -Value $text -Encoding UTF8
    Write-Host ("wrote design/masters/logo/$($v.Name).svg ({0} x {1})" -f $vbW, $vbH)

    # PNG fallback at 2x the SVG's own size, for surfaces that will not take SVG.
    Invoke-Inkscape @($baked, '--export-type=png', "--export-filename=$(Join-Path $logoDir "$($v.Name).png")", '-w', "$([int][Math]::Round($vbW * 2))")
    Write-Host "wrote design/masters/logo/$($v.Name).png (2x)"
}

# ------------------------------------------------------------ social preview
# GitHub's repo social card: 1280x640, solid background (a transparent one
# renders as white in some clients and black in others), wordmark centred at
# ~62% width so it survives the aggressive crop link unfurlers apply.
$cardW = 1280; $cardH = 640
$card  = New-Object System.Drawing.Bitmap($cardW, $cardH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g     = [System.Drawing.Graphics]::FromImage($card)
$g.Clear([System.Drawing.Color]::FromArgb(255, 0x24, 0x2B, 0x36))   # .ink, matching design/logo.svg
$g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$wm = New-Object System.Drawing.Bitmap((Join-Path $logoDir 'wordmark-dark.png'))
$targetW = [int]($cardW * 0.62)
$targetH = [int]($wm.Height * ($targetW / $wm.Width))
$g.DrawImage($wm, [int](($cardW - $targetW) / 2), [int](($cardH - $targetH) / 2), $targetW, $targetH)
$wm.Dispose(); $g.Dispose()
$card.Save((Join-Path $logoDir 'social-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$card.Dispose()
Write-Host "wrote design/masters/logo/social-preview.png (1280x640)"

Remove-Item -Recurse -Force $tmpDir
Write-Host "`nmasters rebuilt. Next: pwsh scripts/windows/make-app-icons.ps1"

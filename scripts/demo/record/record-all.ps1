# Records every scene to $env:KN_DEMO_OUT (default %TEMP%\kn-demo\out), one mp4 per scene.
# -Only applications,palette-logs re-records single scenes.
param([string[]]$Only)
$scenes = 'applications', 'palette-logs', 'unhealthy-logs'
if ($Only) { $scenes = $scenes | Where-Object { $Only -contains $_ } }
foreach ($s in $scenes) {
    "== $s"
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "scenes\$s.ps1")
}

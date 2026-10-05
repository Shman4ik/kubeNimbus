# Palette: Ctrl+K, "logs check", Enter - one merged stream for every pod of a Deployment,
# the failing pod's lines in red, then a search inside the stream.
param([string]$Theme = 'dark', [switch]$Dry)
. "$PSScriptRoot\..\demo-lib.ps1"
Prep-Data $Theme 'Resources'
try {
    Start-App | Out-Null
    Pause 3.5; Fit-App; Park-Mouse
    if (-not $Dry) { Start-Rec 'palette-logs' -Mouse }
    Pause 1.2
    Key 'ctrl+k' 700
    Type-Slow 'logs check' 75
    Pause 1.4
    Key 'enter' 2200
    Click 330 666 400
    Type-Slow 'gateway' 90
    Pause 2.2
    Park-Mouse
    Pause 0.8
} finally { Stop-Rec; Stop-App }

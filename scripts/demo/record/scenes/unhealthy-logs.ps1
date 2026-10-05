# Unhealthy only + L: narrow a pod list to what is wrong, then one key to the crashing pod's logs.
param([string]$Theme = 'dark', [switch]$Dry)
. "$PSScriptRoot\..\demo-lib.ps1"
Prep-Data $Theme 'Resources'
try {
    Start-App | Out-Null
    Pause 3.5; Fit-App; Park-Mouse
    if (-not $Dry) { Start-Rec 'unhealthy-logs' -Mouse }
    Pause 1.4
    Click 1362 108 1500                             # Unhealthy only
    Click 500 183 600                               # first unhealthy row
    Pause 0.6
    Key 'l' 2400                                    # row key: logs of that pod
    Pause 1.0
    Park-Mouse
    Pause 0.8
} finally { Stop-Rec; Stop-App }

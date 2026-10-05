# Applications: the list says what is wrong and why, one click opens the page with the facts,
# the crashing pod's last run is already there.
param([string]$Theme = 'dark', [switch]$Dry)
. "$PSScriptRoot\..\demo-lib.ps1"
Prep-Data $Theme 'Applications'
try {
    Start-App | Out-Null
    Pause 3.5; Fit-App; Park-Mouse
    if (-not $Dry) { Start-Rec 'applications' -Mouse }
    Pause 1.0
    Move-To 190 270 700; Pause 0.4                  # reads the reason under "checkout"
    Click 200 251 1900                              # one click opens the page
    Move-To 215 330 500; Pause 1.0                  # findings quote the fields they came from
    Scroll 215 500 4; Pause 1.2                     # the quota finding
    Move-To 700 366 600; Pause 1.4                  # the error line in the last run
    Click 36 65 1000                                # back to the list
} finally { Stop-Rec; Stop-App }

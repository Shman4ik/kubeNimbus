<#
.SYNOPSIS
    Checks, in about ten seconds, that a desktop QA run can start, and says what is missing.

.DESCRIPTION
    The 0.7.0 run found its blockers one at a time, an hour in: no kubectl on PATH, no
    elevated shell, a sandbox that was not where the plan said, a console that locked
    itself between two batches and silently turned every click into a no-op. This checks
    all of it up front and prints one line per check: `ok`, `WARN` (the run can start, but
    a check will be UNSURE or something may bite later) or `FAIL` (it cannot start).
    Exit code 1 when anything failed.

    Fixes the script applies itself: none. It reads; it never installs, unlocks or changes
    a setting.

.EXAMPLE
    ./scripts/qa-preflight.ps1
#>
[CmdletBinding()]
param([string]$Kubeconfig)

$failed = $false
function Report([string]$level, [string]$name, [string]$detail) {
    if ($level -eq 'FAIL') { $script:failed = $true }
    '{0,-5} {1,-22} {2}' -f $level, $name, $detail
}

# --- the VM flag -----------------------------------------------------------------------
if ($env:KUBENIMBUS_QA_VM -eq '1') { Report ok 'QA VM flag' 'KUBENIMBUS_QA_VM=1' }
else { Report FAIL 'QA VM flag' "KUBENIMBUS_QA_VM is '$env:KUBENIMBUS_QA_VM'. The owner sets it as a user variable and restarts the session; never set it from here." }

# --- tools -----------------------------------------------------------------------------
# A session started before kubectl was installed has a stale PATH; the app inherits it too,
# so the terminals it opens say "kubectl was not found on this app's PATH". qa-app.ps1
# refreshes it for the app; here it is only reported.
$fresh = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$kubectl = Get-Command kubectl -ErrorAction SilentlyContinue
if (-not $kubectl) {
    $saved = $env:Path; $env:Path = "$fresh;$env:Path"
    $kubectl = Get-Command kubectl -ErrorAction SilentlyContinue
    $env:Path = $saved
    if ($kubectl) { Report WARN 'kubectl' "found only on the refreshed PATH ($($kubectl.Source)); this session's PATH is stale, qa-app.ps1 and qa-seed.ps1 compensate" }
    else { Report FAIL 'kubectl' 'not installed. Ask the owner (winget install Kubernetes.kubectl); do not download it from here.' }
} else { Report ok 'kubectl' $kubectl.Source }
$sdk = (dotnet --version 2>$null)
if ($sdk -match '^10\.') { Report ok 'dotnet SDK' $sdk } else { Report FAIL 'dotnet SDK' "need 10.x, found '$sdk'" }

# --- the cluster -----------------------------------------------------------------------
$repo = (git rev-parse --show-toplevel 2>$null)
if (-not $Kubeconfig) {
    $common = (git rev-parse --path-format=absolute --git-common-dir 2>$null)
    if ($common) { $Kubeconfig = Join-Path (Split-Path $common -Parent) '.sandbox/kubeconfig.yaml' }
}
if ($Kubeconfig -and (Test-Path $Kubeconfig)) {
    $servers = @(Select-String -Path $Kubeconfig -Pattern '^\s*server:\s*(\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value })
    $bad = @($servers | Where-Object { ([Uri]$_).Host -notin @('127.0.0.1', 'localhost', '[::1]', '::1') })
    if ($bad) { Report FAIL 'kubeconfig' "names a non-local server: $($bad -join ', '). qa-app.ps1 refuses it; forward the port to this machine's loopback instead." }
    else { Report ok 'kubeconfig' "$Kubeconfig -> $($servers[0])" }
    $uri = [Uri]$servers[0]
    $tcp = Test-NetConnection $uri.Host -Port $uri.Port -WarningAction SilentlyContinue -InformationLevel Quiet
    if (-not $tcp) { Report FAIL 'sandbox port' "nothing listens on $($uri.Host):$($uri.Port). Start the sandbox (./scripts/sandbox-up.ps1, with -Wsl in this VM) or ask the owner." }
    else {
        $listener = Get-NetTCPConnection -LocalPort $uri.Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        $who = if ($listener) { (Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue).ProcessName } else { '?' }
        Report ok 'sandbox port' "$($uri.Host):$($uri.Port) is open (served by $who)"
        if ($kubectl) {
            $nodes = & $kubectl.Source --kubeconfig $Kubeconfig get nodes --no-headers --request-timeout=10s 2>&1
            if ($LASTEXITCODE -eq 0) { Report ok 'cluster' "$(@($nodes).Count) node(s): $((@($nodes) | Select-Object -First 1) -replace '\s+', ' ')" }
            else { Report FAIL 'cluster' "kubectl get nodes failed: $($nodes | Select-Object -First 1)" }
            $shop = & $kubectl.Source --kubeconfig $Kubeconfig -n qa-shop get deploy shop-web --no-headers 2>&1
            if ($LASTEXITCODE -eq 0) { Report ok 'qa-shop' ($shop -replace '\s+', ' ') } else { Report WARN 'qa-shop' 'missing. Run ./scripts/qa-seed.ps1' }
        }
    }
    # Connection-drop checks use qa-netcut.ps1, which needs no elevation; the old plan's
    # netsh port proxy does, so elevation is information only.
    $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')
    Report ok 'elevation' "$(if ($elevated) { 'elevated' } else { 'not elevated' }) (not needed: connection drops use scripts/qa-netcut.ps1)"
} else { Report FAIL 'kubeconfig' "no kubeconfig at '$Kubeconfig'. The owner puts the sandbox's kubeconfig there, or run ./scripts/sandbox-up.ps1." }

# --- the desktop ------------------------------------------------------------------------
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
try {
    $bitmap = New-Object System.Drawing.Bitmap 1, 1
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.CopyFromScreen(0, 0, 0, 0, $bitmap.Size); $g.Dispose(); $bitmap.Dispose()
    Report ok 'interactive desktop' 'screen capture works, so real input will reach the app'
} catch {
    Report FAIL 'interactive desktop' 'the console is locked or disconnected: clicks, keys and screenshots would silently do nothing. Ask the owner to unlock the VM.'
}
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$g = [System.Drawing.Graphics]::FromHwnd([IntPtr]::Zero); $scale = [math]::Round($g.DpiX / 96 * 100); $g.Dispose()
Report ok 'display' "$($screen.Width)x$($screen.Height) px at $scale % scaling, $([System.Windows.Forms.Screen]::AllScreens.Count) monitor(s); 1280 DIP = $([int](1280 * $scale / 100)) px"
$saver = (Get-ItemProperty 'HKCU:\Control Panel\Desktop' -ErrorAction SilentlyContinue)
$idle = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue).InactivityTimeoutSecs
if ($saver.ScreenSaveActive -eq '1' -and $saver.ScreenSaverIsSecure -eq '1') { Report WARN 'lock timeout' "the screen saver locks after $($saver.ScreenSaveTimeOut) s idle; a long run loses real input when it fires. Ask the owner to turn it off for the QA VM." }
elseif ($idle) { Report WARN 'lock timeout' "the machine locks after $idle s of inactivity; a long run loses real input when it fires." }
else { Report ok 'lock timeout' 'no screen-saver or inactivity lock configured' }

# --- an app already running -----------------------------------------------------------------
$state = Join-Path ([IO.Path]::GetTempPath()) 'kubenimbus-qa/app.pid'
if (Test-Path $state) {
    $p = Get-Content $state -Raw | ConvertFrom-Json
    if (Get-Process -Id $p.Pid -ErrorAction SilentlyContinue) { Report WARN 'QA app' "an instance is running (pid $($p.Pid)); stop it with ./scripts/qa-app.ps1 -Stop" }
}
if ($repo) { Report ok 'repo' "$(git -C $repo log --oneline -1)" }

if ($failed) { 'preflight: FAILED'; exit 1 } else { 'preflight: ok' }

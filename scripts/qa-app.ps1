<#
.SYNOPSIS
    Starts (or stops) a Debug build of kubeNimbus for an agent to drive: inside the
    QA VM only, isolated profile, sandbox cluster only, one instance at a time.

.DESCRIPTION
    The kn-qa agent checks the running app through Windows UI Automation (qa-ui.ps1).
    Four things make that safe to hand to a cheap model, and this script is where they hold:

    - The QA VM only. Real mouse and keyboard input takes over whatever desktop the app
      runs on, so desktop checks run only inside the owner's Hyper-V QA VM, never on the
      owner's own desktop (owner's decision, 2026-10-10, #259). The VM sets the
      environment variable KUBENIMBUS_QA_VM=1; anywhere else this script refuses to
      start, and a desktop check is written into the PR body for the VM instead.

    - Isolation. KUBENIMBUS_PROFILE_DIR points the app at a fresh settings/workspace
      directory and restricts the kubeconfig search to $KUBECONFIG alone, so the
      developer's own tabs, preferences and ~/.kube/config contexts never appear.
    - Local clusters only. Every `server:` in the kubeconfig must be 127.0.0.1,
      localhost or [::1]. An agent that presses Delete or Drain must not be able to
      press it on a real cluster, whatever kubeconfig it was handed.
    - One instance. DevTools attaches to "the" running app and the desktop has one
      mouse; a second instance would make every observation ambiguous.

.EXAMPLE
    ./scripts/qa-app.ps1              # build, then start against the sandbox
    ./scripts/qa-app.ps1 -NoBuild     # start the existing Debug build
    ./scripts/qa-app.ps1 -Exe publish/app/kubeNimbus.exe   # the NativeAOT publish instead
    ./scripts/qa-app.ps1 -Stop        # stop it and delete the profile
#>
[CmdletBinding()]
param(
    [string]$Kubeconfig,
    [switch]$NoBuild,
    # Drive another build, normally the NativeAOT publish the release checklist tests.
    # Isolation and the local-cluster check apply to it unchanged; nothing is built.
    [string]$Exe,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'

$repo = (git rev-parse --show-toplevel).Trim()
$stateDir = Join-Path ([IO.Path]::GetTempPath()) 'kubenimbus-qa'
$pidFile = Join-Path $stateDir 'app.pid'
New-Item -ItemType Directory -Force $stateDir | Out-Null

function Get-RunningQaApp {
    if (-not (Test-Path $pidFile)) { return $null }
    $state = Get-Content $pidFile -Raw | ConvertFrom-Json
    $process = Get-Process -Id $state.Pid -ErrorAction SilentlyContinue
    if ($process -and $process.ProcessName -eq 'kubeNimbus') { return $state }
    Remove-Item $pidFile -Force
    return $null
}

if ($Stop) {
    $state = Get-RunningQaApp
    if ($state) {
        Stop-Process -Id $state.Pid -Force
        Remove-Item $pidFile -Force
        Remove-Item $state.Profile -Recurse -Force -ErrorAction SilentlyContinue
        "Stopped kubeNimbus (pid $($state.Pid)) and removed $($state.Profile)."
    } else {
        'No QA instance is running.'
    }
    return
}

# Stopping is allowed anywhere; starting only in the QA VM.
if ($env:KUBENIMBUS_QA_VM -ne '1') {
    throw ("Refusing: this machine is not marked as the QA VM (KUBENIMBUS_QA_VM is not '1'). " +
        "Desktop checks run only inside the owner's Hyper-V QA VM, never on the owner's own " +
        "desktop. List the check in the PR body for the VM instead.")
}

$running = Get-RunningQaApp
if ($running) {
    throw "A QA instance is already running (pid $($running.Pid)). Stop it first: ./scripts/qa-app.ps1 -Stop"
}

# The sandbox kubeconfig lives in the main checkout, which a worktree does not have.
if (-not $Kubeconfig) {
    $common = (git rev-parse --path-format=absolute --git-common-dir).Trim()
    $Kubeconfig = Join-Path (Split-Path $common -Parent) '.sandbox/kubeconfig.yaml'
}
if (-not (Test-Path $Kubeconfig)) {
    throw "No kubeconfig at $Kubeconfig. Start the sandbox first: ./scripts/sandbox-up.ps1"
}

$servers = Select-String -Path $Kubeconfig -Pattern '^\s*server:\s*(\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value }
if (-not $servers) { throw "No server: entry in $Kubeconfig." }
foreach ($server in $servers) {
    $hostName = ([Uri]$server).Host
    if ($hostName -notin @('127.0.0.1', 'localhost', '[::1]', '::1')) {
        throw "Refusing: $Kubeconfig names $server. QA runs only against a local cluster."
    }
}

if (-not $NoBuild -and -not $Exe) {
    dotnet build (Join-Path $repo 'src/KubeNimbus.App/KubeNimbus.App.csproj') -c Debug -v q --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

$exe = if ($Exe) { (Resolve-Path $Exe).Path } else { Join-Path $repo 'src/KubeNimbus.App/bin/Debug/net10.0/kubeNimbus.exe' }
if (-not (Test-Path $exe)) { throw "No build at $exe." }

$profileDir = Join-Path $stateDir ("profile-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$env:KUBENIMBUS_PROFILE_DIR = $profileDir
$env:KUBECONFIG = (Resolve-Path $Kubeconfig).Path
$process = Start-Process -FilePath $exe -PassThru
@{ Pid = $process.Id; Profile = $profileDir; Kubeconfig = $env:KUBECONFIG; Exe = $exe } |
    ConvertTo-Json | Set-Content $pidFile

"Started kubeNimbus (pid $($process.Id))"
"  profile:    $profileDir"
"  kubeconfig: $($env:KUBECONFIG)"
"Drive it with ./scripts/qa-ui.ps1. Stop with ./scripts/qa-app.ps1 -Stop"

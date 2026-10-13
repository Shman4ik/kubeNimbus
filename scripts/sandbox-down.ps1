#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Tears down a sandbox cluster started by sandbox-up.ps1.

.PARAMETER Name
    Docker container name. Default: kubenimbus-sandbox.

.PARAMETER Kubeconfig
    The sandbox's kubeconfig, deleted after the container (it points at a cluster that no
    longer exists). Default: derived from -Name, the way the up scripts write it:
    .sandbox/argo-kubeconfig.yaml for kubenimbus-argo, .sandbox/kubeconfig.yaml otherwise.
    The file is deleted only when its current-context is -Name (sandbox-up renames k3s's
    `default` context to the container name), so tearing down one sandbox never deletes
    another one's kubeconfig (#379).

.PARAMETER KeepKubeconfig
    Leave the kubeconfig in place.

.PARAMETER Wsl
    Route docker through `wsl.exe --exec docker ...` — pass this if the sandbox was
    brought up with sandbox-up.ps1 -Wsl. Once no `kubenimbus*` container is left in the
    distro, it also stops the WSL keep-alive the up scripts started and deletes its record
    (wsl-keepalive.json beside the kubeconfig); while another one is left, the Argo sandbox
    for instance, the keep-alive stays, because that container needs the distro up.

.PARAMETER WslDistribution
    WSL distro to target (passed as `wsl -d <name>`). Only meaningful with
    -Wsl.
#>
[CmdletBinding()]
param(
    [string] $Name = 'kubenimbus-sandbox',
    [string] $Kubeconfig,
    [switch] $KeepKubeconfig,
    [switch] $Wsl,
    [string] $WslDistribution
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
# The Argo sandbox writes its own file (argo-sandbox-up.ps1 and the documented sandbox-up line
# use .sandbox/argo-kubeconfig.yaml); falling back to the shared file here deleted the running
# kubenimbus-sandbox's kubeconfig on `sandbox-down.ps1 -Name kubenimbus-argo` (#379).
if (-not $Kubeconfig) {
    $file = if ($Name -eq 'kubenimbus-argo') { 'argo-kubeconfig.yaml' } else { 'kubeconfig.yaml' }
    $Kubeconfig = Join-Path $repoRoot ".sandbox/$file"
}

# The context a kubeconfig selects, read as text (no YAML parser in a bare pwsh); $null when
# the file has no top-level current-context line.
function Get-CurrentContext([string] $Path) {
    $line = Select-String -Path $Path -Pattern '^current-context:\s*["'']?([^"''\s]+)' | Select-Object -First 1
    if ($line) { $line.Matches[0].Groups[1].Value } else { $null }
}

# The same `docker` shadow and keep-alive functions sandbox-up.ps1 -Wsl uses.
if ($Wsl) { . (Join-Path $PSScriptRoot 'wsl-docker.ps1') }

$existing = (docker ps -a --filter "name=^/$Name$" --format '{{.Names}}') -join ''
if ($existing) {
    docker rm -f $Name | Out-Null
    Write-Host "Removed container '$Name'." -ForegroundColor Green
}
else {
    Write-Host "No container named '$Name'." -ForegroundColor DarkGray
}

# The keep-alive holds the distro up for the k3s containers in it (#370), so it goes with the
# last of them and not before (#376). The record sits beside the kubeconfig, so it is read
# before the kubeconfig is deleted below.
if ($Wsl) {
    $keepAliveRecord = Join-Path (Split-Path $Kubeconfig -Parent) 'wsl-keepalive.json'
    $left = @(docker ps -a --filter 'name=^/kubenimbus' --format '{{.Names}}' | Where-Object { $_ })
    if ($left.Count -gt 0) {
        Write-Host "WSL keep-alive left running: $($left -join ', ') still in the distro." -ForegroundColor DarkGray
    }
    elseif ($stopped = Stop-WslKeepAlive $keepAliveRecord) {
        Write-Host "Stopped the WSL keep-alive (pid $($stopped.Pid)) and removed $keepAliveRecord." -ForegroundColor Green
    }
}

if (-not $KeepKubeconfig -and (Test-Path $Kubeconfig)) {
    $context = Get-CurrentContext $Kubeconfig
    if ($context -ne $Name) {
        # Another sandbox's file (or one this script did not write): never ours to delete.
        Write-Warning "Kept ${Kubeconfig}: its current context is '$context', not '$Name'. Pass the sandbox's own file with -Kubeconfig, or delete it by hand."
    }
    else {
        Remove-Item $Kubeconfig -Force
        Write-Host "Removed $Kubeconfig." -ForegroundColor Green
    }
}

# A copy installed by -InstallKubeconfig is not ours to delete (the user may have
# merged other clusters into it since), but a config still pointing at a cluster
# that no longer exists is worth saying out loud.
$classic = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.kube/config'
if ((Test-Path $classic) -and (Select-String -Path $classic -Pattern ([regex]::Escape($Name)) -Quiet)) {
    Write-Host "Note: $classic still references context '$Name', which no longer exists." -ForegroundColor Yellow
}

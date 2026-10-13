#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Tears down a sandbox cluster started by sandbox-up.ps1.

.PARAMETER Name
    Docker container name. Default: kubenimbus-sandbox.

.PARAMETER KeepKubeconfig
    Leave .sandbox/kubeconfig.yaml in place (default is to delete it, since it
    points at a cluster that no longer exists).

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
if (-not $Kubeconfig) { $Kubeconfig = Join-Path $repoRoot '.sandbox/kubeconfig.yaml' }

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
    Remove-Item $Kubeconfig -Force
    Write-Host "Removed $Kubeconfig." -ForegroundColor Green
}

# A copy installed by -InstallKubeconfig is not ours to delete (the user may have
# merged other clusters into it since), but a config still pointing at a cluster
# that no longer exists is worth saying out loud.
$classic = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.kube/config'
if ((Test-Path $classic) -and (Select-String -Path $classic -Pattern ([regex]::Escape($Name)) -Quiet)) {
    Write-Host "Note: $classic still references context '$Name', which no longer exists." -ForegroundColor Yellow
}

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
    Route docker through `wsl.exe docker ...` — pass this if the sandbox was
    brought up with sandbox-up.ps1 -Wsl.

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

if ($Wsl) {
    function docker {
        if ($WslDistribution) { & wsl.exe -d $WslDistribution docker @args }
        else { & wsl.exe docker @args }
    }
}

$existing = (docker ps -a --filter "name=^/$Name$" --format '{{.Names}}') -join ''
if ($existing) {
    docker rm -f $Name | Out-Null
    Write-Host "Removed container '$Name'." -ForegroundColor Green
}
else {
    Write-Host "No container named '$Name'." -ForegroundColor DarkGray
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

<#
.SYNOPSIS
    Docker Engine inside WSL2 for the sandbox scripts: dot-sourced by sandbox-up.ps1 and
    argo-sandbox-up.ps1 when they are given -Wsl. Not meant to be run on its own.

.DESCRIPTION
    Dot-source it at script scope, after the caller's param block has set $WslDistribution
    (empty for WSL's default distro):

        if ($Wsl) { . (Join-Path $PSScriptRoot 'wsl-docker.ps1') }

    It defines:

      * `docker`, a function that shadows any docker.exe for the rest of the caller and runs
        `wsl.exe [-d <distro>] --exec docker ...`. `--exec` keeps the Linux shell out of it
        (bash would eat the backslashes of a Windows path), and `$input |` forwards the
        caller's pipeline to the process's stdin, which `docker exec -i ... kubectl apply
        -f -` needs. $LASTEXITCODE flows through from the `&` call.
      * `Start-WslKeepAlive <record path>`, which keeps the distro's VM running. WSL stops
        an idle VM about a minute after its last process ends, and that stops dockerd and
        every k3s container in it (Exited (2)): the 2026-10-13 QA VM run lost both sandbox
        containers that way a minute after `sandbox-up.ps1 -Wsl` (#370). A hidden
        `wsl.exe --exec sleep infinity` is a process in the distro, so the VM stays up for as
        long as it runs. Its pid goes in a JSON record beside the kubeconfig, which
        qa-preflight.ps1 reads; a live keep-alive for the same distro is reused, not doubled.
        Stop it by hand (Stop-Process -Id <pid>) when the sandbox is no longer needed;
        nothing else does.
#>

function docker {
    if ($WslDistribution) {
        $input | & wsl.exe -d $WslDistribution --exec docker @args
    }
    else {
        $input | & wsl.exe --exec docker @args
    }
}

function Get-WslKeepAlive([string] $RecordPath) {
    if (-not (Test-Path $RecordPath)) { return $null }
    try { $record = Get-Content -Raw $RecordPath | ConvertFrom-Json } catch { return $null }
    $process = Get-Process -Id $record.Pid -ErrorAction SilentlyContinue
    # A pid is reused once its process ends: the record names the start time too.
    if (-not $process -or $process.ProcessName -ne 'wsl' -or
        $process.StartTime.ToUniversalTime().Ticks -ne [long]$record.StartedTicks) { return $null }
    $record
}

function Start-WslKeepAlive([string] $RecordPath) {
    $distro = if ($WslDistribution) { $WslDistribution } else { '' }
    $live = Get-WslKeepAlive $RecordPath
    if ($live -and $live.Distribution -eq $distro) { return $live }

    $arguments = @('--exec', 'sleep', 'infinity')
    if ($distro) { $arguments = @('-d', $distro) + $arguments }
    $process = Start-Process -FilePath 'wsl.exe' -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 500
    if ($process.HasExited) {
        throw "The WSL keep-alive (wsl.exe $($arguments -join ' ')) exited at once with code $($process.ExitCode); WSL would stop the sandbox's containers when idle."
    }
    $record = [pscustomobject]@{
        Pid          = $process.Id
        Distribution = $distro
        StartedTicks = $process.StartTime.ToUniversalTime().Ticks
    }
    New-Item -ItemType Directory -Force (Split-Path $RecordPath -Parent) | Out-Null
    $record | ConvertTo-Json | Set-Content -Path $RecordPath -Encoding utf8
    $record
}

<#
.SYNOPSIS
    A loopback TCP forwarder you can cut and restore without administrator rights: the way
    a QA run drops the app's connection to the sandbox on purpose.

.DESCRIPTION
    Several desktop checks need the connection to the API server to die and come back: a
    dropped watch, a log pane's pod-list watch, a failed Reconnect (G-11, P-6, M-10 in
    docs/qa/desktop-checks.md). The 0.7.0 run planned to delete a `netsh interface
    portproxy` rule for that, which needs an elevated shell and a sandbox that is reached
    through one, and had neither: the sandbox runs inside the VM's WSL and port 6550 is
    served by wslrelay. All three checks were reported UNSURE.

    This forwards 127.0.0.1:<ListenPort> to the sandbox's API server from a small hidden
    pwsh process of its own. The k3s certificate names 127.0.0.1, not a port, so a
    kubeconfig whose server is https://127.0.0.1:<ListenPort> verifies exactly as the real
    one does; `qa-seed.ps1` adds that context as `qa-cut`. Cutting kills the process, which
    closes the listener and resets every open connection (a watch sees its stream die, a new
    connection is refused); restoring starts it again. Nothing touches the sandbox, the
    VM's network, or any firewall rule.

    The forwarder only ever listens on loopback and only ever dials the server named in the
    sandbox kubeconfig, which must itself be loopback.

.EXAMPLE
    ./scripts/qa-netcut.ps1 start      # forward 127.0.0.1:6560 to the sandbox
    ./scripts/qa-netcut.ps1 cut        # drop it: connections reset, new ones refused
    ./scripts/qa-netcut.ps1 restore    # bring it back
    ./scripts/qa-netcut.ps1 stop       # remove it (cut and forget)
    ./scripts/qa-netcut.ps1 status
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [ValidateSet('start', 'cut', 'restore', 'stop', 'status', 'serve')]
    [string]$Action,
    [int]$ListenPort = 6560,
    [string]$Kubeconfig,
    # Internal: the target of the hidden worker process.
    [string]$TargetHost,
    [int]$TargetPort
)

$ErrorActionPreference = 'Stop'
$stateDir = Join-Path ([IO.Path]::GetTempPath()) 'kubenimbus-qa'
$pidFile = Join-Path $stateDir 'netcut.pid'

if ($Action -eq 'serve') {
    Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
public static class QaForwarder {
    public static void Run(int listenPort, string host, int port) {
        var listener = new TcpListener(IPAddress.Loopback, listenPort);
        listener.Start();
        while (true) {
            var client = listener.AcceptTcpClient();
            Task.Run(() => Pipe(client, host, port));
        }
    }
    static async Task Pipe(TcpClient client, string host, int port) {
        try {
            using (client)
            using (var server = new TcpClient()) {
                await server.ConnectAsync(host, port);
                var a = client.GetStream();
                var b = server.GetStream();
                await Task.WhenAny(a.CopyToAsync(b), b.CopyToAsync(a));
            }
        } catch { }
    }
}
'@
    [QaForwarder]::Run($ListenPort, $TargetHost, $TargetPort)
    return
}

New-Item -ItemType Directory -Force $stateDir | Out-Null

function Get-Worker {
    if (-not (Test-Path $pidFile)) { return $null }
    $state = Get-Content $pidFile -Raw | ConvertFrom-Json
    $process = Get-Process -Id $state.Pid -ErrorAction SilentlyContinue
    if ($process) { return $state }
    Remove-Item $pidFile -Force
    $null
}

function Stop-Worker {
    $state = Get-Worker
    if ($state) { Stop-Process -Id $state.Pid -Force; Start-Sleep -Milliseconds 300 }
    $state
}

function Get-Target {
    if (-not $Kubeconfig) {
        $common = (git rev-parse --path-format=absolute --git-common-dir).Trim()
        $script:Kubeconfig = Join-Path (Split-Path $common -Parent) '.sandbox/kubeconfig.yaml'
    }
    if (-not (Test-Path $Kubeconfig)) { throw "No kubeconfig at $Kubeconfig. Start the sandbox first: ./scripts/sandbox-up.ps1" }
    # The first cluster's server is the sandbox itself; the qa-cut cluster, if seeded, points
    # back at this forwarder and must not be mistaken for the target.
    $servers = Select-String -Path $Kubeconfig -Pattern '^\s*server:\s*(\S+)' |
        ForEach-Object { [Uri]$_.Matches[0].Groups[1].Value } |
        Where-Object { $_.Port -ne $ListenPort }
    $uri = $servers | Select-Object -First 1
    if (-not $uri) { throw "No server: entry in $Kubeconfig other than the forwarder's own port." }
    if ($uri.Host -notin @('127.0.0.1', 'localhost', '[::1]', '::1')) {
        throw "Refusing: $Kubeconfig names $uri. The forwarder only fronts a local cluster."
    }
    $uri
}

function Start-Worker {
    if (Get-Worker) { return 'already forwarding' }
    $uri = Get-Target
    $arguments = @('-NoProfile', '-File', $PSCommandPath, 'serve', '-ListenPort', $ListenPort,
        '-TargetHost', $uri.Host, '-TargetPort', $uri.Port)
    $process = Start-Process pwsh -ArgumentList $arguments -WindowStyle Hidden -PassThru
    @{ Pid = $process.Id; ListenPort = $ListenPort; Target = "$($uri.Host):$($uri.Port)" } | ConvertTo-Json | Set-Content $pidFile
    # Wait for the listener, so the next command can use it at once.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        try {
            $probe = [Net.Sockets.TcpClient]::new()
            $probe.Connect('127.0.0.1', $ListenPort); $probe.Dispose()
            return "forwarding 127.0.0.1:$ListenPort -> $($uri.Host):$($uri.Port) (pid $($process.Id))"
        } catch { Start-Sleep -Milliseconds 200 }
    }
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
    throw "The forwarder did not start listening on 127.0.0.1:$ListenPort in 15 s."
}

switch ($Action) {
    { $_ -in 'start', 'restore' } { Start-Worker }
    { $_ -in 'cut', 'stop' } {
        $state = Stop-Worker
        if ($state) { "cut: 127.0.0.1:$($state.ListenPort) no longer forwards to $($state.Target)" } else { 'not forwarding' }
        if ($_ -eq 'stop') { Remove-Item $pidFile -Force -ErrorAction SilentlyContinue }
    }
    'status' {
        $state = Get-Worker
        if ($state) { "forwarding 127.0.0.1:$($state.ListenPort) -> $($state.Target) (pid $($state.Pid))" } else { 'not forwarding' }
    }
}

#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Brings up a throwaway k3s-in-Docker cluster, pre-loaded with demo workloads,
    and writes its kubeconfig to .sandbox/kubeconfig.yaml.

.DESCRIPTION
    One command to get a cluster kubeNimbus can be pointed at. The demo apps are
    chosen to light up every surface the app has: healthy and broken workloads,
    multiple namespaces, CRDs (including two same-named Kinds in different API
    groups), Helm releases, RBAC subjects, PVCs, batch jobs that keep firing, and
    containers that log continuously.

    Idempotent: re-running against a live container reuses it and re-applies the
    manifests. Use -Recreate to start from scratch.

    Nothing here is production-shaped. All credentials in the manifests are
    obviously-fake sandbox strings.

.PARAMETER Name
    Docker container name. Default: kubenimbus-sandbox.

.PARAMETER Port
    Host port for the Kubernetes API. Default: 6550.

.PARAMETER K3sVersion
    rancher/k3s image tag. Default: v1.33.4-k3s1.

.PARAMETER Kubeconfig
    Where to write the kubeconfig. Default: <repo>/.sandbox/kubeconfig.yaml (git-ignored).

.PARAMETER InstallKubeconfig
    Also install the kubeconfig at the classic location (~/.kube/config), so the
    app and kubectl find the cluster with no $KUBECONFIG set — which is what a
    GUI launched from Explorer, a shortcut or Visual Studio actually sees.
    Refuses to clobber an existing ~/.kube/config unless -Force is given (it is
    backed up alongside first).

.PARAMETER Force
    Allow -InstallKubeconfig to replace an existing ~/.kube/config (backed up to
    config.<timestamp>.bak).

.PARAMETER Recreate
    Delete an existing container first.

.PARAMETER SkipApps
    Bring up a bare cluster, apply no demo workloads.

.PARAMETER Wsl
    Run every docker command through `wsl.exe docker ...` instead of a native
    Windows docker.exe — for Docker Engine installed inside a WSL2 distro per
    https://learn.microsoft.com/windows/wsl/tutorials/wsl-containers, with no
    Docker Desktop involved. Host-path arguments (the manifests dir) are
    translated to their /mnt/... form via `wsl wslpath -u` first.

.PARAMETER WslDistribution
    WSL distro to target (passed as `wsl -d <name>`). Only meaningful with
    -Wsl. Default: WSL's own default distro.

.EXAMPLE
    ./scripts/sandbox-up.ps1
.EXAMPLE
    ./scripts/sandbox-up.ps1 -Recreate -Port 6551 -Name kubenimbus-sandbox-b
.EXAMPLE
    ./scripts/sandbox-up.ps1 -Wsl
#>
[CmdletBinding()]
param(
    [string] $Name = 'kubenimbus-sandbox',
    [int]    $Port = 6550,
    [string] $K3sVersion = 'v1.33.4-k3s1',
    [string] $Kubeconfig,
    [switch] $InstallKubeconfig,
    [switch] $Force,
    [switch] $Recreate,
    [switch] $SkipApps,
    [switch] $Wsl,
    [string] $WslDistribution
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestDir = Join-Path $PSScriptRoot 'manifests'
if (-not $Kubeconfig) { $Kubeconfig = Join-Path $repoRoot '.sandbox/kubeconfig.yaml' }

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note([string] $Message) { Write-Host "    $Message" -ForegroundColor DarkGray }

# When -Wsl is set, every `docker` call in this script below is routed through
# `wsl.exe docker ...` — a function shadows the external docker.exe for the
# rest of this process, so nothing below needs to know which one is in play.
# $LASTEXITCODE still flows through: it's set by the `&` call inside the
# function and untouched by returning from it.
if ($Wsl) {
    # `$input |` matters: this script pipes YAML into `docker exec -i ... kubectl
    # apply -f -`, and a function with no pipeline input touches $input nowhere
    # near the external process's stdin unless it's forwarded explicitly.
    function docker {
        if ($WslDistribution) {
            $input | & wsl.exe -d $WslDistribution docker @args
        }
        else {
            $input | & wsl.exe docker @args
        }
    }
}

function ConvertTo-DockerHostPath([string] $WindowsPath) {
    if (-not $Wsl) { return $WindowsPath }
    $wslPathArgs = @('wslpath', '-u', $WindowsPath)
    if ($WslDistribution) { $wslPathArgs = @('-d', $WslDistribution) + $wslPathArgs }
    $translated = (& wsl.exe @wslPathArgs).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $translated) {
        throw "wsl wslpath -u '$WindowsPath' failed to translate the manifests path."
    }
    return $translated
}

function Invoke-Kubectl {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $KubectlArgs)
    $output = docker exec $Name kubectl @KubectlArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "kubectl $($KubectlArgs -join ' ') failed:`n$output"
    }
    return $output
}

# --- Docker preflight -------------------------------------------------------
Write-Step 'Checking Docker'
$null = docker version --format '{{.Server.Version}}' 2>&1
if ($LASTEXITCODE -ne 0) {
    if ($Wsl) {
        throw "Docker is not available inside WSL (wsl.exe docker failed). Check that Docker Engine is installed and dockerd is running in the target distro — see scripts/README.md."
    }
    throw 'Docker is not available or the daemon is not running. Start Docker Desktop and retry (or pass -Wsl to use Docker Engine inside WSL2 instead).'
}

$existing = (docker ps -a --filter "name=^/$Name$" --format '{{.Names}} {{.State}}') -join ''
if ($existing -and $Recreate) {
    Write-Step "Removing existing container '$Name'"
    docker rm -f $Name | Out-Null
    $existing = ''
}

# --- Cluster ----------------------------------------------------------------
if (-not $existing) {
    Write-Step "Starting k3s ($K3sVersion) as '$Name', API on 127.0.0.1:$Port"
    docker run -d --name $Name --privileged -p "${Port}:6443" `
        "rancher/k3s:$K3sVersion" server --tls-san 127.0.0.1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker run failed for '$Name'." }
}
elseif ($existing -notmatch 'running') {
    Write-Step "Starting existing container '$Name'"
    docker start $Name | Out-Null
}
else {
    Write-Step "Reusing running container '$Name'"
}

# --- Kubeconfig -------------------------------------------------------------
Write-Step 'Waiting for the API server'
$raw = $null
for ($i = 0; $i -lt 120; $i++) {
    $raw = (docker exec $Name cat /etc/rancher/k3s/k3s.yaml 2>$null) -join "`n"
    if ($LASTEXITCODE -eq 0 -and $raw -match 'clusters:') { break }
    $raw = $null
    Start-Sleep -Seconds 1
}
if (-not $raw) { throw "Timed out waiting for k3s to write its kubeconfig. Check: docker logs $Name" }

# Point at the published host port, and give the context a name worth showing in
# the app's tab strip (k3s calls everything "default").
$raw = $raw -replace 'https://127\.0\.0\.1:6443', "https://127.0.0.1:$Port"
$raw = $raw -replace '(?m)\bdefault\b', $Name

$kubeconfigDir = Split-Path -Parent $Kubeconfig
if (-not (Test-Path $kubeconfigDir)) { New-Item -ItemType Directory -Path $kubeconfigDir -Force | Out-Null }
Set-Content -Path $Kubeconfig -Value $raw -Encoding utf8NoBOM -NoNewline
Write-Note "kubeconfig → $Kubeconfig (context: $Name)"

if ($InstallKubeconfig) {
    # The classic path. An app started from Explorer/VS inherits no $KUBECONFIG,
    # so this is the only place it will look.
    $classic = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.kube/config'
    $classicDir = Split-Path -Parent $classic
    if (-not (Test-Path $classicDir)) { New-Item -ItemType Directory -Path $classicDir -Force | Out-Null }

    if ((Test-Path $classic) -and -not $Force) {
        Write-Warning "$classic already exists — not touching it. Re-run with -Force to replace it (a backup is kept), or set `$env:KUBECONFIG=`"$Kubeconfig`" instead."
    }
    else {
        if (Test-Path $classic) {
            $backup = "$classic.$(Get-Date -Format yyyyMMdd-HHmmss).bak"
            Copy-Item $classic $backup -Force
            Write-Note "backed up existing config → $backup"
        }

        Copy-Item $Kubeconfig $classic -Force
        Write-Note "kubeconfig → $classic (classic path)"
        Write-Note 'Note: -Recreate mints a new CA and client certs; re-run with -InstallKubeconfig -Force to refresh this copy.'
    }
}

Write-Step 'Waiting for the node to become Ready'
# `kubectl wait --all` fails outright when the collection is still empty, and the
# node object appears a few seconds after the API server starts serving.
for ($i = 0; $i -lt 120; $i++) {
    $nodes = docker exec $Name kubectl get nodes --no-headers 2>$null
    if ($LASTEXITCODE -eq 0 -and $nodes) { break }
    Start-Sleep -Seconds 1
}
$null = Invoke-Kubectl wait --for=condition=Ready node --all --timeout=180s

if ($SkipApps) {
    Write-Step 'Skipping demo workloads (-SkipApps)'
}
else {
    # --- Demo workloads -----------------------------------------------------
    Write-Step 'Applying demo workloads'
    docker exec $Name rm -rf /kubenimbus-manifests | Out-Null
    $manifestSource = ConvertTo-DockerHostPath $manifestDir
    docker cp $manifestSource "${Name}:/kubenimbus-manifests" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'docker cp of the manifests failed.' }

    Write-Note '00-namespaces.yaml'
    $null = Invoke-Kubectl apply -f /kubenimbus-manifests/00-namespaces.yaml

    # A brand-new namespace has no `default` ServiceAccount for a second or two,
    # and a pod created before it exists is rejected outright.
    foreach ($ns in 'demo-shop', 'demo-data', 'demo-batch', 'demo-broken') {
        for ($i = 0; $i -lt 60; $i++) {
            $null = docker exec $Name kubectl get serviceaccount default -n $ns 2>$null
            if ($LASTEXITCODE -eq 0) { break }
            Start-Sleep -Seconds 1
        }
    }

    foreach ($file in '10-shop.yaml', '20-data.yaml', '30-batch.yaml', '40-broken.yaml', '50-crds.yaml', '70-argocd-crds.yaml') {
        Write-Note $file
        $null = Invoke-Kubectl apply -f "/kubenimbus-manifests/$file"
    }

    # CRs need their CRD's endpoint to exist first.
    $null = Invoke-Kubectl wait --for=condition=Established crd/widgets.shop.kubenimbus.io `
        crd/widgets.factory.kubenimbus.io crd/backups.demo.kubenimbus.io `
        crd/applications.argoproj.io crd/appprojects.argoproj.io --timeout=60s
    Write-Note '51-custom-resources.yaml'
    $null = Invoke-Kubectl apply -f /kubenimbus-manifests/51-custom-resources.yaml
    Write-Note '60-rbac.yaml'
    $null = Invoke-Kubectl apply -f /kubenimbus-manifests/60-rbac.yaml
    Write-Note '71-argocd-applications.yaml'
    $null = Invoke-Kubectl apply -f /kubenimbus-manifests/71-argocd-applications.yaml

    # --- Synthetic Helm release --------------------------------------------
    # k3s stores its own bundled charts (traefik, coredns...) as real Helm
    # release Secrets, but each at revision 1. This one carries three revisions
    # so the release history view has something to page through. It is a record
    # only — nothing is installed by it.
    Write-Step 'Seeding a multi-revision Helm release (demo-shop/checkout)'
    $template = Get-Content -Raw (Join-Path $manifestDir 'helm-release.template.json')
    $first = (Get-Date).ToUniversalTime().AddDays(-6)
    $revisions = @(
        @{ Revision = 1; Status = 'superseded'; Chart = '0.1.0'; App = '1.4.0'; Replicas = 1; Age = -6; Description = 'Install complete' }
        @{ Revision = 2; Status = 'superseded'; Chart = '0.2.0'; App = '1.5.0'; Replicas = 2; Age = -2; Description = 'Upgrade complete' }
        @{ Revision = 3; Status = 'deployed';   Chart = '0.2.1'; App = '1.5.1'; Replicas = 3; Age = 0;  Description = 'Upgrade complete' }
    )

    foreach ($rev in $revisions) {
        $deployed = (Get-Date).ToUniversalTime().AddDays($rev.Age).ToString('o')
        $json = $template `
            -replace '__REVISION__', $rev.Revision `
            -replace '__STATUS__', $rev.Status `
            -replace '__CHART_VERSION__', $rev.Chart `
            -replace '__APP_VERSION__', $rev.App `
            -replace '__REPLICAS__', $rev.Replicas `
            -replace '__DESCRIPTION__', $rev.Description `
            -replace '__FIRST_DEPLOYED__', $first.ToString('o') `
            -replace '__LAST_DEPLOYED__', $deployed

        # Helm's storage format: base64(gzip(json)) — Kubernetes then base64s
        # the Secret value on top, which is what stringData does for us here.
        $plain = [System.Text.Encoding]::UTF8.GetBytes($json)
        $buffer = [System.IO.MemoryStream]::new()
        $gzip = [System.IO.Compression.GZipStream]::new($buffer, [System.IO.Compression.CompressionLevel]::Optimal)
        $gzip.Write($plain, 0, $plain.Length)
        $gzip.Dispose()
        $payload = [Convert]::ToBase64String($buffer.ToArray())
        $buffer.Dispose()

        $secret = @"
apiVersion: v1
kind: Secret
metadata:
  name: sh.helm.release.v1.checkout.v$($rev.Revision)
  namespace: demo-shop
  labels:
    owner: helm
    name: checkout
    version: "$($rev.Revision)"
    status: $($rev.Status)
type: helm.sh/release.v1
stringData:
  release: $payload
"@
        $secret | docker exec -i $Name kubectl apply -f - | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to apply Helm release secret revision $($rev.Revision)." }
    }
}

# --- Summary ----------------------------------------------------------------
Write-Step 'Cluster contents'
docker exec $Name kubectl get pods -A --no-headers 2>$null |
    ForEach-Object { $_ } | Group-Object { ($_ -split '\s+')[0] } |
    ForEach-Object { Write-Note ("{0,-14} {1} pods" -f $_.Name, $_.Count) }

Write-Host ''
Write-Host 'Sandbox is up.' -ForegroundColor Green
Write-Host ''
Write-Host '  Run the app against it:' -ForegroundColor Gray
if ($InstallKubeconfig) {
    Write-Host '    dotnet run --project src/KubeNimbus.App' -ForegroundColor White
    Write-Host '    (installed at ~/.kube/config, so no $KUBECONFIG needed — works from VS/Explorer too)' -ForegroundColor DarkGray
}
else {
    Write-Host "    `$env:KUBECONFIG = `"$Kubeconfig`"" -ForegroundColor White
    Write-Host '    dotnet run --project src/KubeNimbus.App' -ForegroundColor White
    Write-Host '    (or re-run this script with -InstallKubeconfig to use ~/.kube/config instead)' -ForegroundColor DarkGray
}
Write-Host ''
Write-Host '  Run the integration tests (auto-discovers .sandbox/kubeconfig.yaml):' -ForegroundColor Gray
Write-Host '    dotnet test tests/KubeNimbus.Core.Tests/KubeNimbus.Core.Tests.csproj' -ForegroundColor White
Write-Host ''
Write-Host "  Tear down:  ./scripts/sandbox-down.ps1 -Name $Name" -ForegroundColor Gray
Write-Host ''
Write-Host '  Note: some demo workloads are broken on purpose (demo-broken namespace)' -ForegroundColor DarkGray
Write-Host '  so the error/pending/crashloop states have something to render.' -ForegroundColor DarkGray

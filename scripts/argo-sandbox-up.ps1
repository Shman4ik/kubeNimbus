#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Installs a real, pinned Argo CD into a throwaway sandbox cluster, with applications in
    any namespace turned on and a Git server inside the cluster, for the Argo live tests.

.DESCRIPTION
    The ordinary sandbox carries a stand-in Application CRD and no controller (see
    scripts/README.md), which proves a patch lands on the object and nothing more. This
    script turns a SECOND sandbox into a real Argo CD installation, so the live tests in
    tests/KubeNimbus.Core.Tests/Live/ArgoLiveTests.cs can watch Argo's own controller act on
    what kubeNimbus writes.

    Bring the cluster up first, separately from the shared one and with no demo workloads
    (the demo's stand-in Argo CRDs claim the names real Argo installs):

        ./scripts/sandbox-up.ps1 -Name kubenimbus-argo -Port 6551 -SkipApps `
            -Kubeconfig .sandbox/argo-kubeconfig.yaml
        ./scripts/argo-sandbox-up.ps1

    What it installs, and why each piece is pinned:

      * install.yaml of Argo CD $ArgoVersion, downloaded from the release tag and refused
        unless its SHA-256 is the one recorded below.
      * Every image the install runs, rewritten from its tag to tag@digest, so the cluster
        cannot pull a different image under the same tag. The Dex Deployment is dropped:
        nothing here signs in through SSO, and it is one image fewer to pull.
      * application.namespaces = argo-tenant in argocd-cmd-params-cm, plus the cluster-wide
        role Argo's documentation says argocd-server needs for it, so an Application in the
        tenant namespace is reconciled ("applications in any namespace").
      * argocd-cm data.url = https://argocd.sandbox.test, which is what "Open in Argo CD"
        reads. Nothing serves that address; the tests only read where the link would go.
      * A Git server (`git daemon`) in the argocd namespace, running the argocd image Argo
        already pulled — no other image. It serves git://argo-git.argocd/demo.git with two
        tags: `with-removed` (ConfigMaps keep and removed) and `without-removed` (the commit
        after it, which deleted removed.yaml). Moving an Application from the first to the
        second is a resource leaving Git, which is what prune acts on. Its `slow/` path holds
        a PreSync hook Job that sleeps for ten minutes (the argocd image again), so a sync of
        it stays Running long enough to be terminated.
      * An AppProject `tenant` whose sourceNamespaces names argo-tenant.

    Idempotent: re-running re-applies everything.

.PARAMETER Name
    The sandbox's Docker container. Refuses the shared `kubenimbus-sandbox`.

.PARAMETER Wsl
    Run every docker command through `wsl.exe --exec docker ...`, for Docker Engine inside
    a WSL2 distro, as `sandbox-up.ps1 -Wsl` does; pass it when the cluster was brought up
    that way. It also starts (or reuses) the same keep-alive, so WSL does not stop the
    distro, and the cluster with it, when idle. See scripts/wsl-docker.ps1.

.PARAMETER WslDistribution
    WSL distro to target (passed as `wsl -d <name>`). Only meaningful with -Wsl.
    Default: WSL's own default distro.

.PARAMETER Kubeconfig
    The cluster's kubeconfig, used only to place the keep-alive record beside it.
    Default: <repo>/.sandbox/argo-kubeconfig.yaml.

.EXAMPLE
    ./scripts/sandbox-up.ps1 -Wsl -Name kubenimbus-argo -Port 6551 -SkipApps -Kubeconfig .sandbox/argo-kubeconfig.yaml
    ./scripts/argo-sandbox-up.ps1 -Wsl
#>
[CmdletBinding()]
param(
    [string] $Name = 'kubenimbus-argo',
    [switch] $Wsl,
    [string] $WslDistribution,
    [string] $Kubeconfig
)

$ErrorActionPreference = 'Stop'

# The same `docker` routing as sandbox-up.ps1 -Wsl, and its keep-alive (#370): the 2026-10-13
# QA VM run had to forward docker to WSL by hand here.
if ($Wsl) {
    . (Join-Path $PSScriptRoot 'wsl-docker.ps1')
    if (-not $Kubeconfig) { $Kubeconfig = Join-Path (Split-Path -Parent $PSScriptRoot) '.sandbox/argo-kubeconfig.yaml' }
    $keepAlive = Start-WslKeepAlive (Join-Path (Split-Path $Kubeconfig -Parent) 'wsl-keepalive.json')
    Write-Host "    WSL keep-alive: pid $($keepAlive.Pid)" -ForegroundColor DarkGray
}

# The release this installs, and the SHA-256 of its install.yaml. Changing the version is
# changing both lines, after computing the new file's hash yourself.
$ArgoVersion = 'v3.5.4'
$InstallSha256 = '1feb02cc7bacf3a379da58b0ca8c6f07d18288cd04d531dabd08816375e5e010'

# The images install.yaml names, by the digest their registry served for that tag on
# 2026-10-11 (quay.io's tag API for argocd; public.ecr.aws's manifest HEAD for redis).
$ImageDigests = @{
    'quay.io/argoproj/argocd:v3.5.4'                    = 'sha256:49dff79439bb38b1b942b19a113fe1fec7e6b6c671ddf9fe6a4a7c46bde0b77b'
    'public.ecr.aws/docker/library/redis:8.2.3-alpine' = 'sha256:08ad0b1d280850169a790dba1393ff7a90aef951fc19632cf4d3ce4f78e679ba'
}

$TenantNamespace = 'argo-tenant'
$ArgoUrl = 'https://argocd.sandbox.test'

if ($Name -eq 'kubenimbus-sandbox') {
    throw 'Refusing to install Argo CD into the shared sandbox. Bring up a second one (see the help).'
}

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

function Invoke-Kubectl {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $KubectlArgs)
    $output = docker exec $Name kubectl @KubectlArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "kubectl $($KubectlArgs -join ' ') failed:`n$output"
    }
    return $output
}

function Invoke-KubectlApply([string] $Yaml, [string[]] $Extra = @()) {
    $output = $Yaml | docker exec -i $Name kubectl apply @Extra -f - 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "kubectl apply failed:`n$output"
    }
}

# --- The cluster must be up, and must not be carrying the stand-in CRDs -------------
$state = docker inspect -f '{{.State.Running}}' $Name 2>$null
if ($state -ne 'true') {
    throw "Container $Name is not running. Start it with ./scripts/sandbox-up.ps1$(if ($Wsl) { ' -Wsl' }) -Name $Name -Port 6551 -SkipApps."
}

$standIn = docker exec $Name kubectl get crd applications.argoproj.io -o 'jsonpath={.metadata.labels.app\.kubernetes\.io/part-of}' 2>$null
if ($LASTEXITCODE -eq 0 -and $standIn -eq 'kubenimbus-sandbox') {
    throw 'This cluster carries the sandbox''s stand-in Argo CRDs. Use a cluster brought up with -SkipApps.'
}

# --- Download and check install.yaml ------------------------------------------------
Write-Step "Downloading Argo CD $ArgoVersion install.yaml"
$work = Join-Path ([IO.Path]::GetTempPath()) "kubenimbus-argo-$ArgoVersion-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Path $work | Out-Null
$installPath = Join-Path $work 'install.yaml'
Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/argoproj/argo-cd/$ArgoVersion/manifests/install.yaml" -OutFile $installPath
$actual = (Get-FileHash -Algorithm SHA256 $installPath).Hash.ToLowerInvariant()
if ($actual -ne $InstallSha256) {
    throw "install.yaml SHA-256 is $actual, expected $InstallSha256. Not applying it."
}

# --- Pin images by digest, drop Dex -------------------------------------------------
$text = (Get-Content -Raw $installPath) -replace "`r`n", "`n"
$documents = $text -split "(?m)^---\s*$" | Where-Object {
    -not ($_ -match '(?m)^kind: Deployment\s*$' -and $_ -match '(?m)^  name: argocd-dex-server\s*$')
}
$text = $documents -join "`n---`n"
foreach ($image in $ImageDigests.Keys) {
    $text = $text.Replace("image: $image`n", "image: $image@$($ImageDigests[$image])`n")
}
$unpinned = [regex]::Matches($text, '(?m)^\s+image: (\S+)$') | ForEach-Object { $_.Groups[1].Value } |
    Where-Object { $_ -notmatch '@sha256:' } | Sort-Object -Unique
if ($unpinned) {
    throw "install.yaml names images this script has no digest for: $($unpinned -join ', ')"
}

# --- Install ---------------------------------------------------------------------------
Write-Step 'Applying Argo CD into namespace argocd'
Invoke-KubectlApply "apiVersion: v1`nkind: Namespace`nmetadata:`n  name: argocd`n"
Invoke-KubectlApply "apiVersion: v1`nkind: Namespace`nmetadata:`n  name: $TenantNamespace`n"
# Server-side: the ApplicationSet CRD is larger than client-side apply's annotation limit,
# which is what Argo's own install documentation says too.
Invoke-KubectlApply $text @('-n', 'argocd', '--server-side', '--force-conflicts')

Write-Step 'Turning on applications in any namespace'
Invoke-Kubectl -n argocd patch configmap argocd-cmd-params-cm --type merge `
    "--patch={`"data`":{`"application.namespaces`":`"$TenantNamespace`"}}" | Out-Null
Invoke-Kubectl -n argocd patch configmap argocd-cm --type merge `
    "--patch={`"data`":{`"url`":`"$ArgoUrl`"}}" | Out-Null

# What examples/k8s-rbac/argocd-server-applications grants: argocd-server managing
# Applications and ApplicationSets outside its own namespace.
Invoke-KubectlApply @"
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRole
metadata:
  name: argocd-server-any-namespace
rules:
  - apiGroups: [argoproj.io]
    resources: [applications, applicationsets]
    verbs: [create, get, list, watch, update, patch, delete]
  - apiGroups: [""]
    resources: [events]
    verbs: [create, list]
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: argocd-server-any-namespace
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: argocd-server-any-namespace
subjects:
  - kind: ServiceAccount
    name: argocd-server
    namespace: argocd
"@

# --- A Git server from the image Argo already pulled -----------------------------------
Write-Step 'Starting the in-cluster Git server'
$argoImage = "quay.io/argoproj/argocd:v3.5.4@$($ImageDigests['quay.io/argoproj/argocd:v3.5.4'])"
Invoke-KubectlApply @"
apiVersion: v1
kind: ConfigMap
metadata:
  name: argo-git-script
  namespace: argocd
data:
  serve.sh: |
    set -eu
    rm -rf /tmp/work /tmp/repos
    mkdir -p /tmp/work/app /tmp/repos
    cd /tmp/work
    git init -q -b main
    git config user.email sandbox@kubenimbus.test
    git config user.name sandbox
    printf 'apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: keep\ndata:\n  value: stays\n' > app/keep.yaml
    printf 'apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: removed\ndata:\n  value: leaves-git\n' > app/removed.yaml
    mkdir -p slow
    printf 'apiVersion: batch/v1\nkind: Job\nmetadata:\n  name: slow-presync\n  annotations:\n    argocd.argoproj.io/hook: PreSync\n    argocd.argoproj.io/hook-delete-policy: BeforeHookCreation\nspec:\n  backoffLimit: 0\n  template:\n    spec:\n      restartPolicy: Never\n      containers:\n        - name: wait\n          image: %s\n          imagePullPolicy: IfNotPresent\n          command: [sleep, "600"]\n' "`$ARGO_IMAGE" > slow/hook.yaml
    printf 'apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: after-slow-hook\ndata:\n  value: synced\n' > slow/configmap.yaml
    git add -A
    git commit -qm 'keep and removed'
    git tag with-removed
    git rm -q app/removed.yaml
    git commit -qm 'remove removed.yaml'
    git tag without-removed
    git clone -q --bare /tmp/work /tmp/repos/demo.git
    touch /tmp/repos/demo.git/git-daemon-export-ok
    exec git daemon --reuseaddr --export-all --base-path=/tmp/repos --listen=0.0.0.0 --port=9418 /tmp/repos
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: argo-git
  namespace: argocd
spec:
  replicas: 1
  selector:
    matchLabels: { app: argo-git }
  template:
    metadata:
      labels: { app: argo-git }
    spec:
      containers:
        - name: git
          image: $argoImage
          imagePullPolicy: IfNotPresent
          command: [bash, /scripts/serve.sh]
          env:
            - { name: ARGO_IMAGE, value: "$argoImage" }
          ports:
            - containerPort: 9418
          volumeMounts:
            - { name: script, mountPath: /scripts }
      volumes:
        - name: script
          configMap: { name: argo-git-script }
---
apiVersion: v1
kind: Service
metadata:
  name: argo-git
  namespace: argocd
spec:
  selector: { app: argo-git }
  ports:
    - port: 9418
      targetPort: 9418
---
apiVersion: argoproj.io/v1alpha1
kind: AppProject
metadata:
  name: tenant
  namespace: argocd
spec:
  sourceRepos: ['*']
  sourceNamespaces: [$TenantNamespace]
  destinations:
    - { server: https://kubernetes.default.svc, namespace: '*' }
"@

# The controller and the server read application.namespaces at start; the Git server
# rebuilds its repository from the script on every start.
Write-Step 'Restarting the controller and the server so they read the new settings'
Invoke-Kubectl -n argocd rollout restart statefulset/argocd-application-controller deployment/argocd-server deployment/argo-git | Out-Null
foreach ($workload in 'statefulset/argocd-application-controller', 'deployment/argocd-server',
    'deployment/argocd-repo-server', 'deployment/argocd-redis', 'deployment/argo-git') {
    Invoke-Kubectl -n argocd rollout status $workload --timeout=300s | Out-Null
}

Remove-Item -Recurse -Force $work
Write-Step "Argo CD $ArgoVersion is running; Applications may live in argocd and $TenantNamespace."
Write-Host "    Git: git://argo-git.argocd.svc.cluster.local:9418/demo.git (tags with-removed, without-removed)"
Write-Host "    Tear down: ./scripts/sandbox-down.ps1 -Name $Name"

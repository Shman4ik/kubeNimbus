<#
.SYNOPSIS
    Creates (or resets, or removes) everything a desktop QA run needs in the sandbox, so a
    run starts with one command instead of an hour of copy-pasted setup.

.DESCRIPTION
    The desktop checks in docs/qa/desktop-checks.md mutate things, so they run in a
    namespace of their own, `qa-shop`, never in the demo namespaces other work shares:

    - qa-shop: a copy of scripts/manifests/10-shop.yaml (shop-web x3 with two containers,
      shop-api x2, shop-distroless, Services, an Ingress, a ConfigMap, a Secret) with the
      HPA pinned at three replicas, so a scale check is not undone by the autoscaler.
    - qa-tick (a CronJob, for Suspend/Resume), qa-quiet (logs nothing), qa-restarter
      (its container exits every ~20 s, so it has previous-container logs), qa-chatty
      (about 20 lines a second, for the scrollback-trim check; the first run used 200 and
      it hung UI Automation for minutes), qa-del-a/b/c (the bulk-delete checks).
    - qa-limited: a ServiceAccount that may read pods, CronJobs, Deployments and events,
      may delete only qa-del-a and qa-del-b, may not read pod logs and may not patch
      CronJobs: the narrow-RBAC checks' 403s.
    - Three contexts in the kubeconfig, all on the same sandbox: `qa-prod` (a name the app
      classifies as production), `qa-limited` (that ServiceAccount's 24-hour token) and
      `qa-cut` (the same server through the forwarder of qa-netcut.ps1, port 6560, which a
      check can cut and restore without administrator rights).

    Every step is idempotent and the script prints one line per step. `-Reset` also puts
    back what a check changes (shop-web at 3 replicas, qa-tick unsuspended, qa-del-* and
    the other pods recreated). `-Down` deletes the namespace and the contexts.

    The kubeconfig must name a loopback server (the same rule as qa-app.ps1), because this
    script applies manifests and mints tokens.

.EXAMPLE
    ./scripts/qa-seed.ps1            # create what is missing, wait for the shop
    ./scripts/qa-seed.ps1 -Reset     # also undo what the last run changed
    ./scripts/qa-seed.ps1 -Down      # remove qa-shop and the three contexts
#>
[CmdletBinding()]
param(
    [string]$Kubeconfig,
    [switch]$Reset,
    [switch]$Down
)

$ErrorActionPreference = 'Stop'
$repo = (git rev-parse --show-toplevel).Trim()

# A session that was started before kubectl was installed does not have it on PATH.
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + $env:Path
if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) { throw 'kubectl is not on PATH. Install it (winget install Kubernetes.kubectl); this script does not download tools.' }

if (-not $Kubeconfig) {
    $common = (git rev-parse --path-format=absolute --git-common-dir).Trim()
    $Kubeconfig = Join-Path (Split-Path $common -Parent) '.sandbox/kubeconfig.yaml'
}
if (-not (Test-Path $Kubeconfig)) { throw "No kubeconfig at $Kubeconfig. Start the sandbox first: ./scripts/sandbox-up.ps1" }
$Kubeconfig = (Resolve-Path $Kubeconfig).Path
foreach ($m in Select-String -Path $Kubeconfig -Pattern '^\s*server:\s*(\S+)') {
    $hostName = ([Uri]$m.Matches[0].Groups[1].Value).Host
    if ($hostName -notin @('127.0.0.1', 'localhost', '[::1]', '::1')) { throw "Refusing: $Kubeconfig names a non-local server ($hostName)." }
}

# A function does not pass its pipeline to the native command unless it is told to.
function K {
    if ($MyInvocation.ExpectingInput) { $input | & kubectl --kubeconfig $Kubeconfig @args }
    else { & kubectl --kubeconfig $Kubeconfig @args }
}
function Step([string]$text) { Write-Host "  $text" }

$ns = 'qa-shop'
$clusterName = (K config view --raw -o jsonpath='{.clusters[0].name}')
$userName = (K config view --raw -o jsonpath='{.users[0].name}')
if (-not $clusterName) { throw "No cluster in $Kubeconfig." }

if ($Down) {
    K delete namespace $ns --ignore-not-found --wait=false | Out-Null
    foreach ($c in 'qa-prod', 'qa-limited', 'qa-cut') { K config delete-context $c 2>$null | Out-Null }
    K config delete-cluster qa-cut 2>$null | Out-Null
    K config unset users.qa-limited 2>$null | Out-Null
    Step "namespace $ns is being deleted; contexts qa-prod, qa-limited, qa-cut removed"
    return
}

# --- the namespace and the shop ---------------------------------------------------------
K create namespace $ns --dry-run=client -o yaml | K apply -f - | Out-Null
Step "namespace $ns"

$yaml = Get-Content (Join-Path $repo 'scripts/manifests/10-shop.yaml') -Raw
# Drop the cluster-scoped PriorityClass document: it exists already, owned by demo-shop.
$docs = $yaml -split '(?m)^---\s*$' | Where-Object { $_ -notmatch '(?m)^kind:\s*PriorityClass' }
$yaml = ($docs -join "`n---`n") -replace 'namespace: demo-shop', "namespace: $ns" `
    -replace 'minReplicas: 2', 'minReplicas: 3' -replace 'maxReplicas: 6', 'maxReplicas: 3'
$yaml | K apply -f - | Out-Null
if ($Reset) { K -n $ns scale deploy/shop-web --replicas=3 | Out-Null }
Step 'shop (shop-web x3, shop-api x2, shop-distroless, services, ingress, HPA pinned at 3)'

# --- the extra workloads ------------------------------------------------------------------
function Pod([string]$name, [string]$restart, [string]$script) {
    @"
apiVersion: v1
kind: Pod
metadata: { name: $name, namespace: $ns }
spec:
  restartPolicy: $restart
  containers:
    - name: app
      image: busybox:1.36
      command: [sh, -c, '$script']
"@
}
$pods = @(
    (Pod 'qa-quiet' 'Always' 'sleep 86400'),
    (Pod 'qa-restarter' 'Always' 'echo "started $(date -Iseconds)"; i=0; while [ $i -lt 10 ]; do i=$((i+1)); echo "INFO run line $i"; sleep 2; done; echo "ERROR exiting"; exit 1'),
    (Pod 'qa-chatty' 'Always' 'i=0; while true; do i=$((i+1)); echo "INFO chatty line $i"; usleep 50000; done')
)
foreach ($n in 'qa-del-a', 'qa-del-b', 'qa-del-c') { $pods += (Pod $n 'Never' 'sleep 86400') }
if ($Reset) {
    # A short grace period and a wait: busybox's `sleep` ignores SIGTERM, and a pod still
    # Terminating would take the apply below and never come back.
    K -n $ns delete pod qa-quiet qa-restarter qa-chatty qa-del-a qa-del-b qa-del-c --ignore-not-found --grace-period=1 --wait=true | Out-Null
}
$pods -join "`n---`n" | K apply -f - 2>&1 | Out-Null
Step 'pods qa-quiet, qa-restarter, qa-chatty, qa-del-a, qa-del-b, qa-del-c'

K -n $ns create cronjob qa-tick --image=busybox:1.36 --schedule='*/30 * * * *' -- sh -c 'echo tick' 2>$null | Out-Null
K -n $ns patch cronjob qa-tick --type=merge -p '{"spec":{"suspend":false}}' | Out-Null
Step 'cronjob qa-tick (not suspended)'

# --- the narrow RBAC ----------------------------------------------------------------------
@"
apiVersion: v1
kind: ServiceAccount
metadata: { name: qa-limited, namespace: $ns }
---
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata: { name: qa-limited, namespace: $ns }
rules:
  - apiGroups: [""]
    resources: [pods, events, services, configmaps]
    verbs: [get, list, watch]
  - apiGroups: [""]
    resources: [pods]
    resourceNames: [qa-del-a, qa-del-b]
    verbs: [delete]
  - apiGroups: [apps, batch]
    resources: [deployments, replicasets, cronjobs, jobs]
    verbs: [get, list, watch]
---
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata: { name: qa-limited, namespace: $ns }
subjects: [ { kind: ServiceAccount, name: qa-limited, namespace: $ns } ]
roleRef: { apiGroup: rbac.authorization.k8s.io, kind: Role, name: qa-limited }
"@ | K apply -f - | Out-Null
Step 'serviceaccount, role and rolebinding qa-limited'

# --- the contexts --------------------------------------------------------------------------
K config set-context qa-prod --cluster=$clusterName --user=$userName --namespace=$ns | Out-Null
# The token is a sandbox-only credential that lives a day and is never printed.
$token = K -n $ns create token qa-limited --duration=24h
K config set-credentials qa-limited --token=$token | Out-Null
K config set-context qa-limited --cluster=$clusterName --user=qa-limited --namespace=$ns | Out-Null
$ca = K config view --raw -o jsonpath="{.clusters[?(@.name=='$clusterName')].cluster.certificate-authority-data}"
K config set-cluster qa-cut --server=https://127.0.0.1:6560 | Out-Null
if ($ca) { K config set clusters.qa-cut.certificate-authority-data $ca | Out-Null }
K config set-context qa-cut --cluster=qa-cut --user=$userName --namespace=$ns | Out-Null
Step 'contexts qa-prod, qa-limited (24 h token), qa-cut (via ./scripts/qa-netcut.ps1 start)'

# --- ready? ---------------------------------------------------------------------------------
K -n $ns rollout status deploy/shop-web --timeout=180s | Out-Null
$can = (K --context qa-limited auth can-i delete pods/qa-del-c -n $ns 2>$null)
$canA = (K --context qa-limited auth can-i delete pods/qa-del-a -n $ns 2>$null)
Step "qa-limited can delete qa-del-a: $canA; qa-del-c: $can (expected yes / no)"
K -n $ns get 'deploy,pods,cronjob' --no-headers | ForEach-Object { Step $_ }

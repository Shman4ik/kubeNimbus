# Scripts

`sandbox-*` bring up a throwaway Kubernetes cluster to develop and test
against (below). The other two directories are the logo/icon pipeline, which
is documented in full in [`design/LOGO-ASSETS.md`](../design/LOGO-ASSETS.md):

| Script | Rebuilds |
|---|---|
| `design/make-masters.ps1` | `design/masters/**` from `design/logo*.svg` (needs Inkscape) |
| `windows/make-app-icons.ps1` | `src/KubeNimbus.App/Assets/**` — `app.ico`, window icons, MSIX tiles |
| `windows/make-store-logos.ps1` | `design/store/**` — Partner Center listing images |

## Sandbox scripts

One command to get a throwaway Kubernetes cluster, pre-loaded with demo
workloads, that kubeNimbus and the integration tests can be pointed at.

```powershell
./scripts/sandbox-up.ps1          # Windows / pwsh
```

```bash
./scripts/sandbox-up.sh           # Linux / macOS
```

Requires Docker. Takes ~40s cold (image pull), ~15s warm. Writes the kubeconfig
to `.sandbox/kubeconfig.yaml` — the path the test suite auto-discovers and the
one to point `$KUBECONFIG` at:

```powershell
$env:KUBECONFIG = ".sandbox/kubeconfig.yaml"
dotnet run --project src/KubeNimbus.App
```

Tear down with `./scripts/sandbox-down.ps1` (or `.sh`). Re-running `sandbox-up`
reuses a live container and re-applies the manifests; `-Recreate` / `--recreate`
starts from scratch.

| Flag | Default | |
|---|---|---|
| `-Name` / `--name` | `kubenimbus-sandbox` | container name — vary it to run a second cluster and exercise multi-cluster/fleet views |
| `-Port` / `--port` | `6550` | host port for the API server |
| `-K3sVersion` / `--k3s-version` | `v1.33.4-k3s1` | `rancher/k3s` image tag |
| `-Kubeconfig` / `--kubeconfig` | `.sandbox/kubeconfig.yaml` | where to write the kubeconfig |
| `-InstallKubeconfig` / `--install-kubeconfig` | off | **also** write `~/.kube/config` — see below |
| `-Force` / `--force` | off | let `-InstallKubeconfig` replace an existing `~/.kube/config` (backed up first) |
| `-Recreate` / `--recreate` | off | delete an existing container first |
| `-SkipApps` / `--skip-apps` | off | bare cluster, no demo workloads |

## The classic path (`~/.kube/config`)

`$KUBECONFIG` only reaches processes started from a shell that has it set. An app
launched from Explorer, a shortcut or Visual Studio inherits none of it and will
show an empty context picker. For those, install the kubeconfig where every
Kubernetes tool looks by default:

```powershell
./scripts/sandbox-up.ps1 -InstallKubeconfig
```

Then `dotnet run --project src/KubeNimbus.App` (and `kubectl`) find the cluster
with no environment variable at all. An existing `~/.kube/config` is never
clobbered silently — the script refuses unless `-Force`, which backs it up to
`config.<timestamp>.bak` first.

The trade-off: this is a **copy**. `-Recreate` mints a new CA and client certs,
which makes the copy fail on TLS until you re-run with
`-InstallKubeconfig -Force`. Pointing `$KUBECONFIG` at `.sandbox/kubeconfig.yaml`
never goes stale, so prefer it when you launch from a terminal.

A second cluster for the fleet views:

```powershell
./scripts/sandbox-up.ps1 -Name kubenimbus-sandbox-b -Port 6551 -Kubeconfig .sandbox/kubeconfig-b.yaml
$env:KUBECONFIG = ".sandbox/kubeconfig.yaml;.sandbox/kubeconfig-b.yaml"
```

## What gets deployed, and which feature it exercises

Everything lives in `scripts/manifests/`. The workloads exist to make app
surfaces non-empty — that's the selection criterion.

| Manifest | Contents | Exercises |
|---|---|---|
| `00-namespaces.yaml` | `demo-shop`, `demo-data`, `demo-batch`, `demo-broken` | namespace picker, all-namespaces mode |
| `10-shop.yaml` | Deployment (2 containers) + Service + Ingress + HPA + ConfigMap + Secret; literal / `configMapKeyRef` / `secretKeyRef` / `fieldRef` / `envFrom` env vars; one container logs INFO/WARN/ERROR lines every 3s | container picker, live log follow + severity coloring + search, Environment tab and per-key Reveal, owner-chain navigation, CPU/Mem + sparklines |
| `20-data.yaml` | StatefulSet with `volumeClaimTemplates`, headless Service, DaemonSet, standalone PVC | Storage section, non-Deployment workload kinds, PV/PVC binding (k3s local-path) |
| `30-batch.yaml` | Job, a CronJob firing every minute, a CronJob failing every 2 minutes | live watch actually moving, Jobs/CronJobs, Warning events |
| `40-broken.yaml` | CrashLoopBackOff, ImagePullBackOff, unschedulable Pending pod, never-Ready pod | the error/warn status pills, `0/1 Ready` vs Pending distinction, Events |
| `50-crds.yaml` + `51-custom-resources.yaml` | three CRDs — **two share the Kind `Widget` in different API groups** — plus instances, namespaced and cluster-scoped | discovery-driven CRD section, group-aware sidebar filter, short-name filter (`wdg`, `bkp`) |
| `60-rbac.yaml` | ServiceAccounts, Role/RoleBinding, ClusterRole/ClusterRoleBinding, and one **dangling** binding whose Role doesn't exist | RBAC access review, both the "what may I do" and "where does this come from" directions |
| `70-argocd-crds.yaml` + `71-argocd-applications.yaml` | a **stand-in** Argo CD Application/AppProject CRD pair and five Applications covering Synced+Healthy, Synced+Degraded, OutOfSync, Missing and never-compared | the Argo sidebar section, the GitOps dashboard's counts and attention ordering, the Application detail pane, and the sync/refresh patches (which really do land on the object) |
| `helm-release.template.json` | a synthetic `checkout` release at **three revisions** (values, rendered manifest, notes) | Helm release list, values/manifest/notes tabs, revision history |

The Argo pair is a *shape*, not an installation: nothing reconciles those Applications, so
a sync writes `operation` and no controller picks it up, and a refresh annotation stays
where it is put. To test against real Argo CD, **delete both CRDs first** — they claim the
same names — and then install it the usual way (`kubectl create namespace argocd &&
kubectl apply -n argocd -f https://raw.githubusercontent.com/argoproj/argo-cd/stable/manifests/install.yaml`).

Metrics come free: k3s ships metrics-server, so `metrics.k8s.io` is live and the
CPU/Memory columns, usage sparklines and the Usage tab populate on their own. To
see the *absent*-metrics degradation path instead, bring the cluster up and
`kubectl -n kube-system delete deployment metrics-server`.

Helm: k3s stores its own bundled charts (traefik, traefik-crd) as genuine
`helm.sh/release.v1` Secrets, so the Helm view has real releases too — but each
at revision 1. The synthetic `checkout` release exists purely to give the
history view several revisions to page through; it is a storage record only,
nothing is installed by it and no Helm binary is involved.

## Caveats

- **Everything here is disposable and fake.** The Secrets hold obviously-fake
  sandbox strings (`sandbox-not-a-real-password`); never point these scripts at
  a cluster you care about.
- `.sandbox/` is git-ignored — it holds the cluster CA and client certs.
- The `demo-broken` namespace is broken *on purpose*. Red rows there are the
  script working.
- The two scripts are twins; a change to one belongs in the other.

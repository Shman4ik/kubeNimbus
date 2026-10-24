# Scripts

`sandbox-*` bring up a throwaway Kubernetes cluster to develop and test
against (below). `test.ps1` / `test.sh` build the solution and run both test
suites by launching their executables directly — the invocation that cannot
silently run nothing, which `dotnet test` has done here twice (the header of each
script says how) — and fail a run in which a suite ran zero tests. Runner
arguments pass through: `./scripts/test.ps1 -RunnerArgs '--treenode-filter','/*/*/DemoRowsTests/*'`
or `scripts/test.sh -- --treenode-filter '/*/*/DemoRowsTests/*'`. The rest is the logo/icon pipeline, documented in full in
[`design/LOGO-ASSETS.md`](../design/LOGO-ASSETS.md), plus the release packaging:

| Script | Rebuilds |
|---|---|
| `design/make-masters.ps1` | `design/masters/**` from `design/logo*.svg` (needs Inkscape) |
| `windows/make-app-icons.ps1` | `src/KubeNimbus.App/Assets/**` — `app.ico`, window icons, MSIX tiles |
| `windows/make-store-logos.ps1` | `design/store/**` — Partner Center listing images |
| `windows/make-store-hero.ps1` | `design/store/SuperHeroArt-16x9-1920x1080.png` — pgNimbus's hero layout with the Applications screenshot |
| `windows/make-store-trailer.ps1` | `design/store/trailer/**` — feature trailer and thumbnail from the committed demo GIFs; needs ffmpeg |

## Packaging scripts

Called by `.github/workflows/release.yml` on a tag, and runnable by hand
against any `dotnet publish` output — which is how to check a packaging change
without cutting a release. Each takes the same four arguments:
`<publish-dir> <version> <rid> <out-dir>`.

| Script | Builds | Runs on |
|---|---|---|
| `linux/build-packages.sh` | `.deb`, and `.AppImage` with its `.zsync` update file | Linux (`dpkg-deb`, `zsyncmake`; downloads `appimagetool`) |
| `linux/check-glibc.sh` | nothing; fails when a Linux publish output needs a glibc above the ceiling | Linux (`objdump`) |
| `macos/build-app-bundle.sh` | `kubeNimbus.app` and a drag-to-Applications `.dmg` | macOS (`sips`, `iconutil`, `hdiutil`, `codesign`) |
| `windows/build-msix.ps1` | `.msix` for Microsoft Store submission | Windows (Windows SDK `makeappx`/`makepri`) |

Windows has no direct download: it ships through the Microsoft Store only, from
the `.msix` that `windows/build-msix.ps1` builds.

```bash
dotnet publish src/KubeNimbus.App -c Release -r linux-x64 -p:PublishAot=true -o publish/app
./scripts/linux/build-packages.sh publish/app 0.0.0-dev linux-x64 dist
```

## Measuring startup

`measure-startup.ps1` (PowerShell 7, Windows, Linux and macOS) times **kubeNimbus's own
startup** and nothing else (#234). It launches a built binary N times with
`--smoke-test`, which starts the real app, waits for a composited frame and exits on its
own, so it needs no cluster, no input and no window anyone looks at. It prints median,
p90, min and max, plus every run, for:

- **first frame (in app)** — the number `--smoke-test` prints, from `Main` to the first
  composited frame;
- **first frame (wall)** — from process start, as the script sees it, to that line
  arriving: the in-app number plus loading the process and the runtime;
- **window mapped (wall)** — Linux with `xdotool` only: process start to the first visible
  X11 window, polled every 20 ms. This is the weaker metric the
  [2026-08-17 KubeUI comparison](../docs/research/2026-08-17-kubeui-positioning.md) used,
  because it is the one another app can be measured on identically.

The default scenario (`-Scenario empty`) runs each launch on a fresh isolated profile
(`KUBENIMBUS_PROFILE_DIR`) with an empty `KUBECONFIG`, so it neither reads nor changes the
settings, workspace or kubeconfig of whoever runs it. `-Scenario unreachable-cluster`
restores a cluster nothing listens on instead and also reports when the failed connect has
been shown. On Linux with no `DISPLAY` the script starts one Xvfb for the whole series.

```powershell
# NativeAOT is the shipping configuration; time that, not a Debug build.
dotnet publish src/KubeNimbus.App -c Release -r win-x64 -p:PublishAot=true -o publish/app   # MSVC env on Windows: see CLAUDE.md
./scripts/measure-startup.ps1 -Runs 20            # defaults to publish/app/kubeNimbus(.exe)
./scripts/measure-startup.ps1 -Runs 10 -Markdown  # for a PR body or a job summary
```

CI's `NativeAOT publish (linux-x64)` job runs it (10 runs, under Xvfb, with `xdotool`) after
the launch check and writes the table to the job summary. It is informational: shared
runners are noisy, so no budget is enforced and the step cannot fail the job.

**What it does not measure.** Cold start: the first `-Warmup` launch (default 1) is reported
separately and left out of the statistics, but even that one reads a page cache warmed by
the publish, so it is not a first launch after a reboot. And **no other app**: the
head-to-head against KubeUI and FreeLens that #234 asked for was **not run**, because the
owner's rule is that no competitor binary is downloaded on the development machine, and
nothing in CI downloads one. The only head-to-head numbers are the August 2026 ones in the
research above, measured once.

First numbers (2026-10-11, commit `f9b118f`, win-x64 NativeAOT, Windows 11 Pro 10.0.26340,
AMD Ryzen 9 5900X, 24 logical CPUs, 20 runs after 1 warm-up, `-Scenario empty`): first frame
**347 ms median, 374 ms p90** in app; **368 ms median, 398 ms p90** from process start
(warm-up: 516 / 591 ms). With `-Scenario unreachable-cluster`, 10 runs: first frame
361 / 376 ms in app, failed connect shown 2441 / 2461 ms (median / p90), which includes the connect attempt
to a closed loopback port and its failure (where those two seconds go was not broken down).

## Sandbox scripts

One command to get a throwaway Kubernetes cluster, pre-loaded with demo
workloads, that kubeNimbus and the integration tests can be pointed at.

```powershell
./scripts/sandbox-up.ps1          # Windows / pwsh, native Docker Desktop
./scripts/sandbox-up.ps1 -Wsl     # Windows / pwsh, Docker Engine inside WSL2 (no Docker Desktop)
```

```bash
./scripts/sandbox-up.sh           # Linux / macOS, or from inside a WSL2 distro's own bash
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

`sandbox-down` deletes the sandbox's kubeconfig after the container, unless
`-KeepKubeconfig` / `--keep-kubeconfig` is passed. It deletes the file only when the file's
`current-context` is the `-Name` it tears down (`sandbox-up` names the context after the
container), and otherwise keeps it with a warning. So tearing down a second sandbox never
deletes the shared one's kubeconfig (#379).

Without `-Kubeconfig`, all four scripts take the kubeconfig path from `-Name`, with one rule:
`.sandbox/kubeconfig.yaml` for `kubenimbus-sandbox`, and `.sandbox/<name>-kubeconfig.yaml` for
any other name, less its `kubenimbus-` prefix (`argo-kubeconfig.yaml` for `kubenimbus-argo`,
`sandbox-b-kubeconfig.yaml` for `kubenimbus-sandbox-b`). So bringing up a second sandbox never
overwrites the shared one's kubeconfig either (#383). `-PrintKubeconfigPath` /
`--print-kubeconfig-path` prints the path the other flags resolve to and exits without touching
Docker.

| Flag | Default | |
|---|---|---|
| `-Name` / `--name` | `kubenimbus-sandbox` | container name — vary it to run a second cluster and exercise multi-cluster/fleet views |
| `-Port` / `--port` | `6550` | host port for the API server |
| `-K3sVersion` / `--k3s-version` | `v1.33.4-k3s1` | `rancher/k3s` image tag |
| `-Kubeconfig` / `--kubeconfig` | from `-Name`: `.sandbox/kubeconfig.yaml` for the default name | where to write the kubeconfig |
| `-PrintKubeconfigPath` / `--print-kubeconfig-path` | off | print the resolved kubeconfig path and exit |
| `-InstallKubeconfig` / `--install-kubeconfig` | off | **also** write `~/.kube/config` — see below |
| `-Force` / `--force` | off | let `-InstallKubeconfig` replace an existing `~/.kube/config` (backed up first) |
| `-Recreate` / `--recreate` | off | delete an existing container first |
| `-SkipApps` / `--skip-apps` | off | bare cluster, no demo workloads |
| `-Wsl` (`sandbox-up.ps1`, `sandbox-down.ps1` and `argo-sandbox-up.ps1`) | off | route every docker call through `wsl.exe --exec docker ...` instead of a native Windows docker.exe; the up scripts also start a WSL keep-alive (below) |
| `-WslDistribution` | WSL default | which distro `-Wsl` targets (`wsl -d <name>`) |

## A real Argo CD for the Argo live tests (optional)

`Live/ArgoLiveTests` drive a real Argo CD controller: a sync that runs and names its
initiator, a refresh whose annotation the controller clears, a deny window blocking a manual
sync, prune deleting a resource that left Git, terminate stopping a sync in flight, the 403 a
read-only user gets, and "Open in Argo CD" with applications in any namespace. They need a
**second** sandbox, never the shared one (the demo manifests' stand-in Argo CRDs claim the same
names), and skip with the reason without it:

```powershell
./scripts/sandbox-up.ps1 -Name kubenimbus-argo -Port 6551 -SkipApps   # writes .sandbox/argo-kubeconfig.yaml
./scripts/argo-sandbox-up.ps1                     # refuses the shared kubenimbus-sandbox
./scripts/sandbox-down.ps1 -Name kubenimbus-argo  # when done; removes only .sandbox/argo-kubeconfig.yaml
```

`argo-sandbox-up.ps1` downloads `install.yaml` of **Argo CD v3.5.4** from the release tag and
refuses it unless its SHA-256 is
`1feb02cc7bacf3a379da58b0ca8c6f07d18288cd04d531dabd08816375e5e010`, rewrites the two images it
runs to their digests (`quay.io/argoproj/argocd:v3.5.4@sha256:49dff794…0b77b`,
`public.ecr.aws/docker/library/redis:8.2.3-alpine@sha256:08ad0b1d…e679ba`; the full digests are
in the script) and drops the Dex Deployment, which nothing here needs. It then turns on
applications in any namespace for `argo-tenant` (`application.namespaces` in
`argocd-cmd-params-cm`, plus the cluster role argocd-server needs for it), sets `argocd-cm`
`data.url` to `https://argocd.sandbox.test`, adds an AppProject `tenant` whose
`sourceNamespaces` names `argo-tenant`, and starts a Git server inside the cluster from the
argocd image Argo already pulled (`git daemon`, serving `git://argo-git.argocd/demo.git`:
`app/` at tags `with-removed` and `without-removed`, and a `slow/` path whose PreSync hook
sleeps ten minutes, for terminate). No other image is pulled. The tests find the kubeconfig at
`.sandbox/argo-kubeconfig.yaml` or `$KUBENIMBUS_ARGO_KUBECONFIG`.

## cert-manager for the live tests (by hand, optional)

A few live tests need CRDs and a mutating admission webhook this repository did not write
(`Live/ApplyPreviewTextDiffLiveTests`, the cert-manager half of `Live/PrinterColumnsLiveTests`).
They use cert-manager, which the sandbox scripts do not install; those tests skip with the
reason when it is absent. To install it, once per sandbox, from the official release manifest:

```powershell
gh release download v1.21.2 -R cert-manager/cert-manager -p cert-manager.yaml -D <empty dir>
# SHA-256 e03b668ec8675214af6b0a671699d088f2601fa3878e0dbe1b41d3feafd1879f
# (the digest GitHub lists for the asset)
kubectl apply -f <empty dir>/cert-manager.yaml
kubectl -n cert-manager rollout status deploy/cert-manager-webhook
```

It pulls three images from `quay.io/jetstack` (controller, cainjector, webhook, all
`v1.21.2`). No test installs or removes it.

## Docker without Docker Desktop (WSL2)

Docker Desktop is not required. Docker Engine can run directly inside a WSL2
distro, per Microsoft's own tutorial:
<https://learn.microsoft.com/windows/wsl/tutorials/wsl-containers>. Short
version, in an Ubuntu-on-WSL2 shell:

```bash
sudo apt-get update && sudo apt-get install -y docker.io
sudo usermod -aG docker "$USER"      # avoid sudo on every docker call; re-open the shell after
sudo service docker start            # or enable systemd in /etc/wsl.conf and `sudo systemctl enable --now docker`
```

Two ways to drive it from there:

- **From inside WSL bash**: `./scripts/sandbox-up.sh` unmodified — it is already
  the Linux twin, and `docker` there is the WSL-hosted engine. `dotnet run` still
  runs on the Windows side, pointed at `.sandbox/kubeconfig.yaml` (WSL2 forwards
  `localhost:6550` to Windows automatically).
- **From Windows pwsh**: `./scripts/sandbox-up.ps1 -Wsl` shells every docker
  command out to `wsl.exe --exec docker ...`, so the rest of the workflow — the
  kubeconfig path, `$env:KUBECONFIG`, `dotnet run` — is unchanged. Pass
  `-WslDistribution <name>` if it isn't your default distro. Tear down the same
  way: `./scripts/sandbox-down.ps1 -Wsl`. `argo-sandbox-up.ps1 -Wsl` works the same.
  WSL stops an idle distro about a minute after its last process ends, and the
  k3s containers with it, so `-Wsl` also starts a hidden `wsl.exe --exec sleep
  infinity` and records its pid in `wsl-keepalive.json` beside the kubeconfig
  (`scripts/wsl-docker.ps1`); a live one is reused. `qa-preflight.ps1` fails a
  WSL-hosted sandbox without it. `sandbox-down.ps1 -Wsl` stops it and deletes the
  record once no `kubenimbus*` container is left in the distro; while the Argo sandbox
  (or another one) is still there, it leaves it running.

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
./scripts/sandbox-up.ps1 -Name kubenimbus-sandbox-b -Port 6551   # writes .sandbox/sandbox-b-kubeconfig.yaml
$env:KUBECONFIG = ".sandbox/kubeconfig.yaml;.sandbox/sandbox-b-kubeconfig.yaml"
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
where it is put. To test against real Argo CD, use a second sandbox and
`argo-sandbox-up.ps1` (above) rather than installing over these CRDs, which claim the same names.

Metrics come free: k3s ships metrics-server, so `metrics.k8s.io` is live and the
CPU/Memory columns, usage sparklines and the Usage tab populate on their own. To
see the *absent*-metrics degradation path instead, bring the cluster up and
`kubectl -n kube-system delete deployment metrics-server`.

Helm: k3s stores its own bundled charts (traefik, traefik-crd) as genuine
`helm.sh/release.v1` Secrets, so the Helm view has real releases too — but each
at revision 1. The synthetic `checkout` release exists purely to give the
history view several revisions to page through; it is a storage record only,
nothing is installed by it and no Helm binary is involved.

## Checking the running app (Windows, QA VM only)

Both scripts run only inside the owner's Hyper-V QA VM, which sets `KUBENIMBUS_QA_VM=1`, and
refuse anywhere else: real input takes over the desktop it runs on, and the owner's own
desktop is not a test machine (2026-10-10, #259). `qa-app.ps1 -Stop` works anywhere.

`qa-app.ps1` starts a Debug build on an isolated profile (`KUBENIMBUS_PROFILE_DIR`) against
the local sandbox only — it refuses a kubeconfig whose server is not loopback — and
`qa-app.ps1 -Stop` ends it and deletes the profile; `-Stop -KeepProfile` keeps it, and
`qa-app.ps1 -ProfileDir <that folder>` starts again on it, for the checks of what a relaunch
restores. `qa-ui.ps1` then reads and drives the window through Windows UI
Automation (`dump`, `find`, `wait`, `invoke`, `set-text`, `click`, `double-click`, `keys`,
`screenshot`, which captures one element cropped to its bounds); the header of the script
lists every command. The `kn-qa` agent uses both.
One instance at a time. `click` (with `-Modifiers` and `-OffsetX/-OffsetY`), `double-click`,
`right-click`, `drag`, `wheel` and `keys` move the real pointer and keyboard. See CLAUDE.md,
"Checking the running app".

A full pre-release run, from [`docs/qa/desktop-checks.md`](../docs/qa/desktop-checks.md), also uses:

- `qa-preflight.ps1` — ten seconds, reads only: the QA flag, `kubectl`, the kubeconfig and the
  sandbox port, whether the console is locked, the display scaling. Exit 1 if it cannot start.
- `qa-seed.ps1` — `qa-shop` (a copy of the shop with its HPA pinned), the `qa-*` workloads,
  the narrow-RBAC ServiceAccount and the contexts `qa-prod`, `qa-limited` and `qa-cut`.
  Idempotent; `-Reset` undoes what a batch changed; `-Down` removes it.
- `qa/` — fixtures a check creates and deletes itself rather than the seed: two YAML files to
  open (`qa-y-file.yaml`, the three-document `qa-y-stack.yaml`), a ConfigMap to force-apply
  (`qa-force.yaml`), a pod logging 200 lines a second that only R-9 ever starts
  (`qa-firehose.yaml`), and a context whose credential plugin is slow
  (`qa-slow-auth.kubeconfig.yaml`, only merged into a temporary copy of the run's kubeconfig).
- `qa-netcut.ps1 start|cut|restore|stop` — a loopback forwarder (127.0.0.1:6560 to the
  sandbox) that the `qa-cut` context points at; cutting it drops the app's connection with no
  elevation and no touch on the sandbox.
- `qa-result.ps1` and `qa-report.ps1` — an agent records each verdict as it reaches it (a PASS
  must quote what it saw); the report, counts and failures are built from those records.

## Caveats

- **Everything here is disposable and fake.** The Secrets hold obviously-fake
  sandbox strings (`sandbox-not-a-real-password`); never point these scripts at
  a cluster you care about.
- `.sandbox/` is git-ignored — it holds the cluster CA and client certs.
- The `demo-broken` namespace is broken *on purpose*. Red rows there are the
  script working.
- The two scripts are twins; a change to one belongs in the other.

> **DRAFT — not published.** Nothing links to this page yet, the README included. It is
> the text of a comparison page (#230), written to be checked before anyone reads it.
> Publishing it, and choosing where, is the owner's step. Every claim about another
> product carries a source and the date it was checked; every claim about kubeNimbus
> points at a file in this repository. A claim that goes stale is a defect in this page.
>
> Last checked: **2026-10-11**.

# kubeNimbus compared with other Kubernetes desktop clients

kubeNimbus is a free, MIT-licensed Kubernetes desktop client compiled ahead of time
(NativeAOT). It is the narrower, faster, quieter one of the native clients. This page
says what that means in numbers, where other clients are ahead, and what has not been
measured.

It does **not** compare feature counts. On feature count, at least one open-source peer
(KubeUI) is ahead in several places, listed below, and Aptakube and Lens have more
surface than kubeNimbus does on purpose. The axes here are the ones kubeNimbus chose to
compete on: startup, footprint, telemetry, the size of the surface, and metrics that work
without Prometheus.

## What each one is

| | Stack | Licence and price | Telemetry | Activity (as of 2026-10-11) |
|---|---|---|---|---|
| **kubeNimbus** | Avalonia 12 on .NET 10, **NativeAOT** (`src/KubeNimbus.App/KubeNimbus.App.csproj`, `.github/workflows/release.yml`) | MIT, free | **None** (`SECURITY.md`, "No telemetry, ever"; `PRIVACY.md`) | This repository |
| [**KubeUI**](https://github.com/IvanJosipovic/KubeUI) | Avalonia 12 on .NET 10, JIT with ReadyToRun, self-contained single file — no `PublishAot` ([`KubeUI.Desktop.csproj` at v1.5.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.5.0/src/KubeUI.Desktop/KubeUI.Desktop.csproj)) | MIT, free ([LICENSE](https://github.com/IvanJosipovic/KubeUI/blob/main/LICENSE)) | **On by default**, opt-out in Settings ([`Settings.cs` at v1.5.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.5.0/src/KubeUI.Avalonia/Options/Settings.cs), [PRIVACY.md](https://github.com/IvanJosipovic/KubeUI/blob/main/PRIVACY.md)) | v1.5.0 released 2026-10-09 ([releases](https://github.com/IvanJosipovic/KubeUI/releases)) |
| [**Lens**](https://lenshq.io/) | Electron | Free for personal use; commercial use and Premium are paid ([pricing](https://lenshq.io/pricing/lens-k8s-ide), checked 2026-08-17) | Not checked | Releases monthly ([product-loop matrix](product-loop/COMPETITOR_MATRIX.md), 2026-09-22) |
| [**FreeLens**](https://github.com/freelensapp/freelens) | Electron (`electron` in [`freelens/package.json`](https://github.com/freelensapp/freelens/blob/main/freelens/package.json), checked 2026-10-11) | MIT, free | Not checked | Last release v1.10.3, 2026-07-07 ([releases](https://github.com/freelensapp/freelens/releases)) |
| [**Aptakube**](https://github.com/aptakube/aptakube) | Tauri; its README leads with "NOT another Electron app" | Closed source, paid ([pricing](https://aptakube.com/pricing), [EULA](https://aptakube.com/legal/eula), checked 2026-08-17) | Not checked | 1.21.2 released 2026-10-06 ([releases](https://github.com/aptakube/aptakube/releases)) |
| [**Headlamp**](https://github.com/kubernetes-sigs/headlamp) | Web-first; the desktop app is Electron (`electron-builder` in [`app/package.json`](https://github.com/kubernetes-sigs/headlamp/blob/main/app/package.json), checked 2026-08-17) | Apache-2.0, free | Not checked | v0.45.0 released 2026-08-20 ([releases](https://github.com/kubernetes-sigs/headlamp/releases)) |
| [**k9s**](https://github.com/derailed/k9s) | Terminal UI (Go) | Apache-2.0, free | Not checked | v0.51.0 released 2026-06-06 ([releases](https://github.com/derailed/k9s/releases)) |
| [**Seabird**](https://github.com/getseabird/seabird) | Go + GTK4, native | MPL-2.0, free | Not checked | **Dormant**: last commit 2025-08-13, no commit since ([commits](https://github.com/getseabird/seabird/commits/main), checked 2026-10-11). Listed because it is the native client most "Lens alternative" lists still cite ([Show HN](https://news.ycombinator.com/item?id=39176541)) |

"Not checked" means exactly that: this page makes no claim either way.

## The closest peer is KubeUI

KubeUI is the one open-source client built the same way kubeNimbus is: same UI framework,
same runtime, same licence. It is actively released and roughly comparable in features. A
comparison that left it out would be the one thing worse than not publishing one, so it
is the main comparison here.

| Axis | kubeNimbus | KubeUI | Source |
|---|---|---|---|
| **Startup**, time to first mapped window, linux-x64, same machine, same harness, 5 runs | **~156 ms** median | **~645 ms** median | Measured once, 2026-08-17, KubeUI v1.1.0-beta.4 ([research](research/2026-08-17-kubeui-positioning.md), §1 "The measurement, actually run") |
| **Footprint on disk**, linux-x64 | ~62 MB (47.6 MB executable + two native libraries) | a single 382 MiB file (installed payload; the download is compressed and smaller) | Same measurement, 2026-08-17 |
| **Why the startup gap exists** | Compiled ahead of time; the Kubernetes client is the AOT build (`KubernetesClient.Aot`) | JIT with ReadyToRun; the reflection-based `KubernetesClient` 19.0.2, and CRD models compiled with Roslyn at runtime — which NativeAOT cannot do | [`KubeUI.Kubernetes.csproj` at v1.5.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.5.0/src/KubeUI.Kubernetes/KubeUI.Kubernetes.csproj); [`Cluster.cs`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Kubernetes/Client/Cluster.cs) (checked 2026-08-17) |
| **Telemetry** | None, and no update check | OpenTelemetry logs and metrics, on by default, opt-out | [`Settings.cs` at v1.5.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.5.0/src/KubeUI.Avalonia/Options/Settings.cs); kubeNimbus: `SECURITY.md`, `PRIVACY.md` |
| **Surface** | One window (UI rule 16), Helm read-only, no AI or MCP integration, no auto-update | Multi-window docking layout, resource graph, MCP server and agent integration, auto-update | [KubeUI README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md) (checked 2026-08-17) |

**The startup and footprint numbers are one machine, one run of five, Linux/X11, two
months old.** They were not re-run for this page, and will not be on this machine: the
owner's rule is that no competitor binary is downloaded here (#234). What *is* repeatable
is kubeNimbus's own half: [`scripts/measure-startup.ps1`](../scripts/measure-startup.ps1)
measures it on any machine, and CI's NativeAOT job runs it on every PR that can change it
(see "kubeNimbus's own startup, repeatably" below). KubeUI's ReadyToRun build has changed
since August (it no longer sets `PublishReadyToRunComposite`), so its number may have moved
in either direction. The gap (about four times) is large enough that a plausible
measurement error does not reverse it, but it is a directional result, not a benchmark.

### Where KubeUI is ahead

Said plainly, because a reader comparing the two will find these within minutes:

- **Distribution.** KubeUI ships signed and notarized binaries, installers with
  auto-update (Velopack), and Homebrew and winget packages ([`publish-installer.yml`](https://github.com/IvanJosipovic/KubeUI/blob/main/.github/workflows/publish-installer.yml),
  [Homebrew tap](https://github.com/IvanJosipovic/homebrew-repo), checked 2026-08-17).
  kubeNimbus's direct downloads are **unsigned** and do not update themselves; only its
  [Microsoft Store](https://apps.microsoft.com/detail/9MZ3C28M65PB) build is signed (by
  Microsoft) and self-updating (`README.md`, "Install").
- **YAML editing.** KubeUI has schema-aware completion for built-in kinds and CRDs, with
  field documentation in the tooltips ([README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md)).
  kubeNimbus has none. Both preview an apply with a server-side dry run
  (kubeNimbus: `docs/engineering/apply-preview.md`).
- **Typed CRD models**, as a consequence of the runtime model generation above — the same
  thing that costs it NativeAOT.
- **Multi-window and docking**, which kubeNimbus does not do by decision (UI rule 16).

### Where kubeNimbus is ahead of KubeUI

Not found in KubeUI's tree on 2026-08-17 ([research](research/2026-08-17-kubeui-positioning.md),
"What it actually ships" — absence of a file is weaker evidence than presence):

- An **Applications** landing page with a health verdict and a one-line reason per app,
  from deterministic rules (`docs/engineering/applications-mode.md`).
- **Fleet lists** across several clusters at once (`docs/engineering/fleet-views.md`).
- An **RBAC access review** ("who can do X") (`docs/engineering/rbac-access-review.md`).
- **Helm release browsing** (`docs/engineering/helm-releases.md`).
- A built-in **demo cluster** that needs no cluster or credentials (`docs/engineering/demo-cluster.md`).

## Metrics with no Prometheus

The Lens line's cluster overview and node metrics need Prometheus. On a cluster that runs
only metrics-server, the charts read "Metrics are not available due to missing or invalid
Prometheus configuration", and users have asked for metrics-server support since 2021:
[lens#2437](https://github.com/lensapp/lens/issues/2437) (opened 2021-04-02, open on
2026-10-11) and [freelens#627](https://github.com/freelensapp/freelens/issues/627)
"Metrics are not displayed using metrics-server" (open on 2026-10-11), among the
most-reacted issues in the FreeLens repository
([research](research/2026-08-16-node-ops-and-overview.md), 2026-08-16).

kubeNimbus reads `metrics.k8s.io` — what metrics-server serves — and nothing else. What
ships today (`docs/engineering/metrics.md`):

- **CPU and memory columns** on the pod and node lists, polled every 15 s.
- **Usage tabs** in pod detail and node detail, with a short in-memory graph of the
  samples taken while the pane is open.
- **Allocatable vs requested** on node detail, which needs no metrics API at all: it is
  read from the node and its pods (`src/KubeNimbus.App/Views/NodeDetailView.axaml`).
- **No metrics-server, no error**: when the API is absent or unhealthy, the columns are
  hidden rather than filled with dashes or an error (`MetricsUnavailableException`).

**Pending, not shipped:** a cluster-wide capacity strip on the Nodes list (#206) is being
built. Until it merges, kubeNimbus has no single cluster-total view, and this page must not
claim one.

This differentiates against Lens and FreeLens only. **Aptakube** reads metrics-server as
well as Prometheus (its [README](https://github.com/aptakube/aptakube) and landing page,
checked 2026-08-16), and Headlamp and k9s show metrics-server usage too
([matrix](product-loop/COMPETITOR_MATRIX.md), 2026-09-22).

## kubeNimbus's own startup, repeatably

[`scripts/measure-startup.ps1`](../scripts/measure-startup.ps1) launches a published
binary N times with `--smoke-test` (which starts the real app, waits for a composited
frame and exits) on an isolated empty profile, and reports median and p90 for the time to
first frame, measured both inside the app and from outside it, and on Linux with `xdotool`
also the time to the first mapped window — the metric the KubeUI comparison used. It needs
no cluster and no visible display. How to run it: [`scripts/README.md`](../scripts/README.md#measuring-startup).

Numbers from it so far:

| Date, build | Machine | Runs | First frame, in app (median / p90) | First frame, from process start (median / p90) |
|---|---|---|---|---|
| 2026-10-11, `f9b118f`, win-x64 NativeAOT | Windows 11 Pro 10.0.26340, AMD Ryzen 9 5900X | 20 after 1 warm-up | 347 / 374 ms | 368 / 398 ms |
| 2026-08-17, linux-x64 NativeAOT (before this script; [research](research/2026-08-17-kubeui-positioning.md)) | one container, Xvfb | a handful | 103–108 ms | window mapped: ~156 ms median of 5 |

The two rows are not comparable with each other: different OS and windowing system, different
machine. The CI job summary of
each PR that touches the app carries a fresh linux-x64 row from a shared runner.

**Not run:** a head-to-head against KubeUI or FreeLens. #234 asked for one, scripted, on
more than one machine and OS; the owner's rule is that no competitor binary is downloaded
on the development machine, and nothing in CI downloads one either. The script measures
kubeNimbus only. Whoever re-runs the comparison does it on a machine where downloading
the other apps is allowed, with the same script's Linux window-mapped metric as the
common ground and the competitor launched by the same 20 ms `xdotool` poll the research
describes.

## Sources

- KubeUI: [repo](https://github.com/IvanJosipovic/KubeUI) · [releases](https://github.com/IvanJosipovic/KubeUI/releases) · [README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md) · [PRIVACY.md](https://github.com/IvanJosipovic/KubeUI/blob/main/PRIVACY.md) · [Desktop csproj, v1.5.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.5.0/src/KubeUI.Desktop/KubeUI.Desktop.csproj) · [Kubernetes csproj, v1.5.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.5.0/src/KubeUI.Kubernetes/KubeUI.Kubernetes.csproj) · [Settings.cs, v1.5.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.5.0/src/KubeUI.Avalonia/Options/Settings.cs)
- Seabird: [repo](https://github.com/getseabird/seabird) · [Show HN](https://news.ycombinator.com/item?id=39176541)
- Lens: [pricing](https://lenshq.io/pricing/lens-k8s-ide) · [lens#2437](https://github.com/lensapp/lens/issues/2437)
- FreeLens: [repo](https://github.com/freelensapp/freelens) · [freelens#627](https://github.com/freelensapp/freelens/issues/627)
- Aptakube: [repo/README](https://github.com/aptakube/aptakube) · [pricing](https://aptakube.com/pricing) · [EULA](https://aptakube.com/legal/eula)
- Headlamp: [repo](https://github.com/kubernetes-sigs/headlamp) · [app/package.json](https://github.com/kubernetes-sigs/headlamp/blob/main/app/package.json)
- k9s: [repo](https://github.com/derailed/k9s)
- The research behind this page: [KubeUI positioning (2026-08-17)](research/2026-08-17-kubeui-positioning.md) · [node operations and overview (2026-08-16)](research/2026-08-16-node-ops-and-overview.md) · [competitor matrix (2026-09-22)](product-loop/COMPETITOR_MATRIX.md)

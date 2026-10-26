> Every claim about another product carries a source and the date it was checked; every
> claim about kubeNimbus points at a file in this repository. A claim that has gone stale
> is a defect in this page: open an issue and it gets fixed or removed.
>
> Last checked: **2026-10-26**, against kubeNimbus 1.0.0 (released 2026-10-26).

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

| | Stack | Licence and price | Telemetry | Activity (as of 2026-10-26) |
|---|---|---|---|---|
| **kubeNimbus** | Avalonia 12 on .NET 10, **NativeAOT** (`src/KubeNimbus.App/KubeNimbus.App.csproj`, `.github/workflows/release.yml`) | MIT, free (`LICENSE`) | **None**, and no update check (`SECURITY.md`, "No telemetry, ever"; `PRIVACY.md`) | This repository: 1.0.0 released 2026-10-26, 0.8.0 on 2026-10-19 |
| [**KubeUI**](https://github.com/IvanJosipovic/KubeUI) | Avalonia 12 on **.NET 11** (`net11.0`, built with an 11.0 release-candidate SDK), JIT with ReadyToRun, self-contained single file, no `PublishAot` ([`Directory.Build.props`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/Directory.Build.props), [`global.json`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/global.json), [`KubeUI.Desktop.csproj`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Desktop/KubeUI.Desktop.csproj), all at v1.8.0, checked 2026-10-26) | MIT, free ([LICENSE](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/LICENSE), checked 2026-10-26) | **On by default**, opt-out in File > Settings. While it is on, release builds send OpenTelemetry logs and metrics to `otel-grpc.kubeui.com` ([`Settings.cs`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Avalonia/Options/Settings.cs), [`Program.cs`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Desktop/Program.cs), [PRIVACY.md](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/PRIVACY.md), all at v1.8.0, checked 2026-10-26) | v1.8.0 released 2026-10-19 is the latest stable release; v1.9.0-beta.7 is a pre-release from 2026-10-25 ([releases](https://github.com/IvanJosipovic/KubeUI/releases), checked 2026-10-26) |
| [**Lens**](https://lenshq.io/) | Electron, going by the retired open-source version ([`package.json` on the `master` branch](https://github.com/lensapp/lens/blob/master/package.json), last commit 2023-08-09, checked 2026-10-26); what the current closed-source product is built on is not published there ([README](https://github.com/lensapp/lens/blob/lens-desktop/README.md)) | Personal plan free for students, hobbyists and engineers at small organizations; use by organizations with over $10 million in annual revenue or funding needs a paid plan: Plus $25/month, Pro $25/seat/month, Enterprise $50/seat/month on an annual subscription, $30, $30 and $60 billed monthly ([pricing](https://lenshq.io/pricing/), checked 2026-10-26) | Not checked | Lens 2026.10.51501-latest, release notes dated 2026-10-08 ([release notes](https://docs.lenshq.io/release-notes/lens-k8s-ide/lens-2026-10-51501/), checked 2026-10-26) |
| [**FreeLens**](https://github.com/freelensapp/freelens) | Electron (`electron` in [`freelens/package.json` at v1.10.3](https://github.com/freelensapp/freelens/blob/v1.10.3/freelens/package.json), checked 2026-10-26) | MIT, free ([LICENSE](https://github.com/freelensapp/freelens/blob/main/LICENSE), checked 2026-10-26) | Not checked | Last release v1.10.3, 2026-07-07; no later release or tag on 2026-10-26, while `main` (version 2.0.0-0) was pushed to on 2026-10-25 ([releases](https://github.com/freelensapp/freelens/releases), checked 2026-10-26) |
| [**Aptakube**](https://github.com/aptakube/aptakube) | Tauri; its README leads with "NOT another Electron app" ([README](https://github.com/aptakube/aptakube/blob/main/README.md), checked 2026-10-26) | Closed source: the repository holds only a README and `.github`, and the [EULA](https://aptakube.com/legal/eula) (updated 2026-05-19) is proprietary. Free on clusters with up to 5 nodes; beyond that Personal is $79/year ($7/month) and Team is $59/seat/year ($5/seat/month) ([pricing](https://aptakube.com/pricing), checked 2026-10-26) | Not checked | 1.21.6 released 2026-10-21 ([releases](https://github.com/aptakube/aptakube/releases), checked 2026-10-26) |
| [**Headlamp**](https://github.com/kubernetes-sigs/headlamp) | Web-first; the desktop app is Electron (`electron` and `electron-builder` in [`app/package.json` at v0.46.0](https://github.com/kubernetes-sigs/headlamp/blob/v0.46.0/app/package.json), checked 2026-10-26) | Apache-2.0, free ([LICENSE](https://github.com/kubernetes-sigs/headlamp/blob/main/LICENSE), checked 2026-10-26) | Not checked | v0.46.0 released 2026-10-15 ([releases](https://github.com/kubernetes-sigs/headlamp/releases), checked 2026-10-26) |
| [**k9s**](https://github.com/derailed/k9s) | Terminal UI (Go) | Apache-2.0, free ([LICENSE](https://github.com/derailed/k9s/blob/master/LICENSE), checked 2026-10-26) | Not checked | Latest release v0.51.0 from 2026-06-06, still the latest on 2026-10-26, while `master` was pushed to on 2026-10-24 ([releases](https://github.com/derailed/k9s/releases), checked 2026-10-26) |
| [**Seabird**](https://github.com/getseabird/seabird) | Go + GTK4 and libadwaita, native (build dependencies in the [README](https://github.com/getseabird/seabird/blob/main/README.md), checked 2026-10-26) | MPL-2.0, free ([LICENSE](https://github.com/getseabird/seabird/blob/main/LICENSE), checked 2026-10-26) | Not checked | **Dormant**: the last commit on `main` is `v0.6.0`, 2025-08-13, which is also the last release, and no branch has been pushed to since ([commits](https://github.com/getseabird/seabird/commits/main), checked 2026-10-26). Listed because it is the other open-source client on a native toolkit instead of a web view; it was announced as "the native Kubernetes desktop client" on [Show HN](https://news.ycombinator.com/item?id=39176541) (2024-01-29) |

"Not checked" means exactly that: this page makes no claim either way.

## The closest peer is KubeUI

KubeUI is the one open-source client built from the same parts as kubeNimbus: the same UI
framework, the same licence, and a .NET runtime one version newer. It is actively released
and roughly comparable in features. A comparison that left it out would be the one thing
worse than not publishing one, so it is the main comparison here.

| Axis | kubeNimbus | KubeUI | Source |
|---|---|---|---|
| **Startup**, time to first mapped window, linux-x64, same machine, same harness, 5 runs | **~156 ms** median | **~645 ms** median | Measured once, 2026-08-17, on a kubeNimbus `main` build from before its first tagged release (0.3.0) and KubeUI v1.1.0-beta.4 ([research](research/2026-08-17-kubeui-positioning.md), §1 "The measurement, actually run"). Not re-run since |
| **Footprint on disk**, linux-x64 | ~62 MB (47.6 MB executable + two native libraries) | a single 382 MiB file (installed payload; the download is compressed and smaller) | Same measurement, same builds, 2026-08-17. Not re-run since |
| **How they are built** | Compiled ahead of time; the Kubernetes client is the AOT build (`KubernetesClient.Aot`) (`src/KubeNimbus.App/KubeNimbus.App.csproj`, `docs/engineering/tech-stack.md`) | JIT with ReadyToRun, no `PublishAot` in any of its project files; the `KubernetesClient` 21.0.8 package, not the `.Aot` build; an ASP.NET Core framework reference and the OpenTelemetry exporters | [`KubeUI.Desktop.csproj`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Desktop/KubeUI.Desktop.csproj) and [`KubeUI.Kubernetes.csproj`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Kubernetes/KubeUI.Kubernetes.csproj), both at v1.8.0 (checked 2026-10-26) |
| **Telemetry** | None, and no update check | OpenTelemetry logs and metrics, on by default, opt-out. Its PRIVACY.md lists app and view open and close events, action names, CPU and memory use, log messages, exceptions with stack traces, and OS name, version and architecture | [`Settings.cs`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Avalonia/Options/Settings.cs), [`Program.cs`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Desktop/Program.cs) and [PRIVACY.md](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/PRIVACY.md), all at v1.8.0 (checked 2026-10-26); kubeNimbus: `SECURITY.md`, `PRIVACY.md` |
| **Surface** | One window (UI rule 16), Helm read-only, no AI or MCP integration, no updater of its own (the Store build is updated by the Store) | Multi-window docking layout, resource relationship graph, an opt-in local MCP server and an ACP agent client, automatic updates | kubeNimbus: `docs/engineering/ui-rules.md`, `README.md` ("Known limitations"). KubeUI at v1.8.0, checked 2026-10-26: [README](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/README.md), [MCP server guide](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/website/docs/mcp-server.md), [`src/KubeUI.AI/Acp`](https://github.com/IvanJosipovic/KubeUI/tree/v1.8.0/src/KubeUI.AI/Acp) |

**The startup and footprint numbers are one machine, one run of five, Linux/X11, about ten
weeks old, and neither app is the version that was measured.** kubeNimbus has had ten tagged
releases since (0.3.0 to 1.0.0). KubeUI went from v1.1.0-beta.4 to v1.8.0, and was on .NET 10
and `KubernetesClient` 19.0.2 in August ([research](research/2026-08-17-kubeui-positioning.md))
against .NET 11 and 21.0.8 now. The numbers were not re-run for this page, and will not be on
this machine: the owner's rule is that no competitor binary is downloaded here (#234). What
*is* repeatable is kubeNimbus's own half:
[`scripts/measure-startup.ps1`](../scripts/measure-startup.ps1) measures it on any machine,
and CI's NativeAOT job runs it on every PR that can change it (see "kubeNimbus's own startup,
repeatably" below). The gap in August (about four times) was large enough that a plausible
measurement error would not have reversed it, but with both apps changed since, it is a
directional result from August, not a benchmark of today's builds.

**A claim this page used to make is gone.** It said KubeUI compiles CRD models with Roslyn at
runtime, which NativeAOT cannot do, and listed typed CRD models as something KubeUI was ahead
on. That was already out of date when it was last checked: KubeUI's Kubernetes project stopped
referencing the model generator by v1.5.0, and at v1.8.0 its source has no Roslyn use and no
runtime assembly generation. It treats custom resources as a generic JSON object, as
kubeNimbus does ([`GenericKubernetesObject.cs`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Kubernetes/Models/GenericKubernetesObject.cs),
[`ClusterModelCatalog.cs`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Kubernetes/Client/ClusterModelCatalog.cs),
checked 2026-10-26). So the gap in the "How they are built" row is the publish mode and the
`KubernetesClient` package, and nothing found shows that KubeUI could not now be published
NativeAOT. This page does not claim how much of the startup gap each difference accounts for.

### Where KubeUI is ahead

Said plainly, because a reader comparing the two will find these within minutes:

- **Distribution on macOS and Linux, and signing there.** KubeUI ships signed and notarized
  binaries (Azure Trusted Signing on Windows, a Developer ID with notarization on macOS),
  installers with auto-update (Velopack), and a Homebrew cask, a winget package and a
  Microsoft Store listing ([`cicd.yml`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/.github/workflows/cicd.yml)
  turns code signing on for [`publish-installer.yml`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/.github/workflows/publish-installer.yml);
  [README](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/README.md) "Supported Platforms";
  [Homebrew tap](https://github.com/IvanJosipovic/homebrew-repo), which holds `Casks/kubeui.rb`;
  all checked 2026-10-26. The winget and Store listings are taken from the README and were not
  looked up in those catalogues). kubeNimbus's macOS and Linux downloads are **unsigned**: the
  macOS app is only ad-hoc signed and macOS quarantines it. They do not update themselves,
  except that the Linux AppImage has carried update information since 0.8.0, so
  AppImageUpdate can bring it to the latest release (`README.md`, "Download" and "Known
  limitations"; `CHANGELOG.md`, 0.8.0).
- **Windows is no longer part of that gap.** Since 1.0.0 kubeNimbus has no Windows download:
  it installs from the [Microsoft Store](https://apps.microsoft.com/detail/9MZ3C28M65PB) or
  with `winget install --id 9MZ3C28M65PB --source msstore`, which is the same Store package,
  signed by Microsoft and updated by the Store. The portable zip published up to 0.8 is no
  longer built (`README.md`, "Download"; `.github/workflows/release.yml`; `CHANGELOG.md`,
  1.0.0). The cost is that Windows has one channel, and it is the Store.
- **YAML editing.** KubeUI has completion for built-in kinds and CRDs, with field
  documentation in the tooltips, and validation while editing
  ([README](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/README.md), checked
  2026-10-26). kubeNimbus has no schema-aware completion (`docs/engineering/mission.md`). Both
  preview an apply with a server-side dry run (kubeNimbus: `docs/engineering/apply-preview.md`).
  1.0.0 added opening a local YAML file in the editor (`CHANGELOG.md`, 1.0.0), which does not
  change this.
- **Multi-window and docking**, which kubeNimbus does not do by decision (UI rule 16,
  `docs/engineering/ui-rules.md`).

### Where kubeNimbus is ahead of KubeUI

Not found in KubeUI's source tree at v1.8.0 on 2026-10-26 (searched for each term; absence of
a file is weaker evidence than presence):

- An **Applications** landing page with a health verdict and a one-line reason per app,
  from deterministic rules (`docs/engineering/applications-mode.md`). KubeUI's graph does read
  Argo CD tracking annotations and Flux objects
  ([`GitOpsRelationshipProvider.cs`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Kubernetes/Resources/Relationships/Providers/GitOpsRelationshipProvider.cs)),
  but there is no landing page or verdict.
- **Fleet lists** across several clusters at once (`docs/engineering/fleet-views.md`).
- An **RBAC access review** ("who can do X") (`docs/engineering/rbac-access-review.md`).
  KubeUI runs a `SelfSubjectAccessReview` to adapt its navigation to the account
  ([`Cluster.Auth.cs`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Kubernetes/Client/Cluster.Auth.cs))
  and its graph links RoleBindings to their Roles and ServiceAccounts
  ([`RbacRelationshipProvider.cs`](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Kubernetes/Resources/Relationships/Providers/RbacRelationshipProvider.cs)),
  but no view answers that question.
- **Helm release browsing** (`docs/engineering/helm-releases.md`). KubeUI's tree mentions Helm
  only for Flux `HelmRelease` objects in the same GitOps provider.
- A built-in **demo cluster** that needs no cluster or credentials (`docs/engineering/demo-cluster.md`).

## Metrics with no Prometheus

**FreeLens.** Its cluster overview charts need a Prometheus provider. On a cluster that runs
only metrics-server they read "Metrics are not available due to missing or invalid Prometheus
configuration" in FreeLens v1.10.3
([`cluster-no-metrics.tsx`](https://github.com/freelensapp/freelens/blob/v1.10.3/packages/core/src/renderer/components/cluster/cluster-no-metrics.tsx),
checked 2026-10-26). Users have asked for metrics-server support since early 2025:
[freelens#627](https://github.com/freelensapp/freelens/issues/627) "Metrics are not displayed
using metrics-server" (opened 2025-04-18 against FreeLens 1.2.0, open on 2026-10-26, 21
reactions, the fourth most of any issue in the repository) and
[freelens#466](https://github.com/freelensapp/freelens/issues/466) "Metrics Server and External
Prometheus" (opened 2025-03-24, open on 2026-10-26, 40 reactions). Its lists do read
metrics-server: the Nodes list shows a metrics-server CPU value, and v1.10.3 fixed how it is
formatted ([#2084](https://github.com/freelensapp/freelens/pull/2084), merged 2026-07-02). So
the difference is the overview and detail charts, not every number on screen.

**Lens.** This page used to say that Lens needs Prometheus for its overview too, on the strength
of [lens#2437](https://github.com/lensapp/lens/issues/2437) (opened 2021-04-02, still open on
2026-10-26). That issue belongs to the retired open-source repository, and it no longer shows
what current Lens does: Lens's own release notes for 2024.9.300059 (2024-10-01) say it added
support for displaying basic metrics from metrics-server alone, without saying in which views
([release notes](https://docs.lenshq.io/release-notes/lens-k8s-ide/lens-2024-9-300059/),
checked 2026-10-26). Lens is closed source and was not run for this page, so how much of its
overview works without Prometheus is Not checked, and this page no longer claims kubeNimbus
differs from Lens on this axis.

kubeNimbus reads `metrics.k8s.io`, which is what metrics-server serves, and nothing else.
What ships in 1.0.0 (`docs/engineering/metrics.md`):

- **CPU and memory columns** on the pod and node lists, polled every 15 s.
- **Usage tabs** in pod detail and node detail, with a short in-memory graph of the
  samples taken while the pane is open.
- **Allocatable vs requested** on node detail, which needs no metrics API at all: it is
  read from the node and its pods (`src/KubeNimbus.App/Views/NodeDetailView.axaml`).
- **No metrics-server, no error**: when the API is absent or unhealthy, the columns are
  hidden rather than filled with dashes or an error (`MetricsUnavailableException`). Since
  0.8.0 that also holds when the account may not read metrics (a 403): the columns are
  hidden and the status bar gives the reason (`CHANGELOG.md`, 0.8.0).
- **A capacity strip above the Nodes list** (#206, shipped in 0.7.0): the node count and how
  many are NotReady, CPU and memory used against allocatable (from metrics-server), and CPU
  and memory requested against allocatable. A part the cluster does not let it read says so
  in place instead of showing zero (`src/KubeNimbus.App/Views/CapacityStripView.axaml`, sums
  in `src/KubeNimbus.Core/ClusterCapacity.cs`).

This differentiates against FreeLens's charts only. **Aptakube** reads metrics-server as
well as Prometheus: its README says metrics work "with metrics-server and Prometheus", and its
landing page says it "Works with metrics-server and Prometheus!"
([README](https://github.com/aptakube/aptakube/blob/main/README.md), [aptakube.com](https://aptakube.com/),
checked 2026-10-26). **Headlamp** shows resource usage when metrics-server is installed and a
notice when it is not ([`docs/installation/metrics-server.md` at v0.46.0](https://github.com/kubernetes-sigs/headlamp/blob/v0.46.0/docs/installation/metrics-server.md),
checked 2026-10-26). **k9s**'s README gives read access to the `metrics.k8s.io` API in its RBAC
example, marked as the metrics server "if present", so that it can show metrics
([README at v0.51.0](https://github.com/derailed/k9s/blob/v0.51.0/README.md), checked
2026-10-26).

## kubeNimbus's own startup, repeatably

[`scripts/measure-startup.ps1`](../scripts/measure-startup.ps1) launches a published
binary N times with `--smoke-test` (which starts the real app, waits for a composited
frame and exits) on an isolated empty profile, and reports median and p90 for the time to
first frame, measured both inside the app and from outside it, and on Linux with `xdotool`
also the time to the first mapped window, which is the metric the KubeUI comparison used. It
needs no cluster and no visible display. How to run it: [`scripts/README.md`](../scripts/README.md#measuring-startup).

Numbers from it so far:

| Date, build | Machine | Runs | First frame, in app (median / p90) | First frame, from process start (median / p90) |
|---|---|---|---|---|
| 2026-10-11, `f9b118f` (a `main` build 55 commits after 0.6.0, released in 0.7.0), win-x64 NativeAOT | Windows 11 Pro 10.0.26340, AMD Ryzen 9 5900X | 20 after 1 warm-up | 347 / 374 ms | 368 / 398 ms |
| 2026-08-17, `main` before 0.3.0, linux-x64 NativeAOT (before this script; [research](research/2026-08-17-kubeui-positioning.md)) | one container, Xvfb | a handful | 103 to 108 ms | window mapped: ~156 ms median of 5 |

The two rows are not comparable with each other: different OS and windowing system, different
machine. **Neither row is from 1.0.0 or 0.8.0.** They were not re-measured for this page: the
script needs a published NativeAOT binary, and Windows no longer has a downloadable one. The CI
job summary of each PR that touches the app carries a fresh linux-x64 row from a shared runner.

**Not run:** a head-to-head against KubeUI or FreeLens. #234 asked for one, scripted, on
more than one machine and OS; the owner's rule is that no competitor binary is downloaded
on the development machine, and nothing in CI downloads one either. The script measures
kubeNimbus only. Whoever re-runs the comparison does it on a machine where downloading
the other apps is allowed, with the same script's Linux window-mapped metric as the
common ground and the competitor launched by the same 20 ms `xdotool` poll the research
describes.

## Sources

All checked 2026-10-26 unless a date is given beside the claim above.

- KubeUI: [repo](https://github.com/IvanJosipovic/KubeUI) · [releases](https://github.com/IvanJosipovic/KubeUI/releases) · [README, v1.8.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/README.md) · [PRIVACY.md, v1.8.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/PRIVACY.md) · [Directory.Build.props, v1.8.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/Directory.Build.props) · [Desktop csproj, v1.8.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Desktop/KubeUI.Desktop.csproj) · [Kubernetes csproj, v1.8.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Kubernetes/KubeUI.Kubernetes.csproj) · [Settings.cs, v1.8.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Avalonia/Options/Settings.cs) · [Program.cs, v1.8.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/src/KubeUI.Desktop/Program.cs) · [cicd.yml, v1.8.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/.github/workflows/cicd.yml) · [publish-installer.yml, v1.8.0](https://github.com/IvanJosipovic/KubeUI/blob/v1.8.0/.github/workflows/publish-installer.yml)
- Lens: [pricing](https://lenshq.io/pricing/) · [release notes](https://docs.lenshq.io/release-notes/lens-k8s-ide/) · [2024.9.300059 notes](https://docs.lenshq.io/release-notes/lens-k8s-ide/lens-2024-9-300059/) · [lensapp/lens README](https://github.com/lensapp/lens/blob/lens-desktop/README.md) · [lens#2437](https://github.com/lensapp/lens/issues/2437)
- FreeLens: [repo](https://github.com/freelensapp/freelens) · [releases](https://github.com/freelensapp/freelens/releases) · [package.json, v1.10.3](https://github.com/freelensapp/freelens/blob/v1.10.3/freelens/package.json) · [freelens#627](https://github.com/freelensapp/freelens/issues/627) · [freelens#466](https://github.com/freelensapp/freelens/issues/466) · [#2084](https://github.com/freelensapp/freelens/pull/2084)
- Aptakube: [repo/README](https://github.com/aptakube/aptakube) · [pricing](https://aptakube.com/pricing) · [EULA](https://aptakube.com/legal/eula) · [aptakube.com](https://aptakube.com/)
- Headlamp: [repo](https://github.com/kubernetes-sigs/headlamp) · [app/package.json, v0.46.0](https://github.com/kubernetes-sigs/headlamp/blob/v0.46.0/app/package.json) · [metrics-server guide, v0.46.0](https://github.com/kubernetes-sigs/headlamp/blob/v0.46.0/docs/installation/metrics-server.md)
- k9s: [repo](https://github.com/derailed/k9s) · [releases](https://github.com/derailed/k9s/releases) · [README, v0.51.0](https://github.com/derailed/k9s/blob/v0.51.0/README.md)
- Seabird: [repo](https://github.com/getseabird/seabird) · [Show HN](https://news.ycombinator.com/item?id=39176541)
- The research behind the August measurement: [KubeUI positioning (2026-08-17)](research/2026-08-17-kubeui-positioning.md)

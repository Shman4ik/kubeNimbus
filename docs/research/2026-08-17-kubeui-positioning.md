# KubeUI, and whether the market paragraph survives it

*2026-08-17. Question asked (DIST-6): `CLAUDE.md`'s Mission paragraph claims "nobody
ships truly fast + open source + modern native desktop UI". [KubeUI](https://github.com/IvanJosipovic/KubeUI)
is an actively developed, MIT-licensed, Avalonia + .NET Kubernetes desktop client that
the paragraph does not name. Confirm the paragraph or rewrite it.*

**The short version: the paragraph is false as written, and the specific sentence that
fails is the headline one.** KubeUI is open source (MIT), a native-widget Avalonia 12 /
.NET 10 desktop app, actively released, and feature-comparable to kubeNimbus. "Nobody
ships open source + modern native desktop UI" is a claim someone can disprove with one
link, and it should not survive this report.

What *does* survive — and was measured rather than argued — is the speed half. KubeUI
does not publish NativeAOT, cannot cheaply become NativeAOT, and on one machine under
one harness opens in **~645 ms against kubeNimbus's ~156 ms**, from a **382 MiB** single
file against a **~62 MB** runtime payload. The honest position is narrower than the
current paragraph and is stated in §4.

The second finding is less comfortable: **KubeUI is ahead of kubeNimbus on distribution
and on several features**, and a comparison table written by someone else would say so.
That is recorded in §3 and in the proposed backlog rows.

## What was searched, and what could not be reached

Reachable from this session: `git clone` over HTTPS, `raw.githubusercontent.com`, web
search, and `WebFetch` against `github.com` HTML. **Not** reachable: `api.github.com`
(403 — the repository is not attached to this session), `github.com` via `curl` (403,
though `WebFetch` succeeds), and the vendor sites `aptakube.com`, `kubeui.com`,
`apps.microsoft.com` and `img.shields.io` (egress-blocked, consistent with the
[2026-08-16 report](2026-08-16-node-ops-and-overview.md)).

Consequences the reader should weigh:

- **KubeUI's source was read directly**, not inferred: the repository was cloned in full
  (200 tags, all branches) and its csprojs, workflows, licence and telemetry wiring were
  read as files. Everything in §1 marked "checked in the repo" comes from that clone.
- **Release download counts could not be obtained** (`api.github.com` and `img.shields.io`
  both blocked). Adoption below is stars, release cadence and distribution channels only.
  No download number is claimed.
- **KubeUI's landing page was read from its own `gh-pages` branch**, since `kubeui.com`
  is blocked. It is the same content the domain serves ([`CNAME`](https://github.com/IvanJosipovic/KubeUI/blob/gh-pages/CNAME)).
- **No Reddit or Hacker News thread about KubeUI was found.** Search returned none. That
  is a finding — see the adoption note in §1 — not a gap in searching.

## 1. KubeUI's actual state

| | Evidence |
|---|---|
| **Licence** | **MIT**, "Copyright (c) 2023 Ivan Josipovic" — [`LICENSE`](https://github.com/IvanJosipovic/KubeUI/blob/main/LICENSE). Note the README's own License section links only a [FOSSA](https://app.fossa.com/projects/git%2Bgithub.com%2FIvanJosipovic%2FKubeUI) badge, so the licence file is the authority |
| **Actively developed?** | **Yes, unambiguously.** 200 tags; first `v1.0.0-alpha.1` 2024-06-02, **`v1.0.0` 2026-05-18**, `v1.0.2` 2026-06-12, and a `v1.1.0-beta` series running 2026-08-07 → **2026-08-12**, five days before this report. Commits across all branches: 60 (Feb 2026), 63 (Mar), 73 (Apr), 60 (May), 27 (Jun), 4 (Jul), 63 (Aug). [Releases](https://github.com/IvanJosipovic/KubeUI/releases) |
| **Contributors** | Effectively **one human** plus Renovate. `git shortlog -sne` on `main`: Ivan Josipovic 24, renovate[bot] 31. Bus factor 1 |
| **Governance / release process** | semantic-release with `alpha`/`beta`/`main` channels, Renovate, Codecov, xUnit + Microsoft.Testing.Platform, KinD-gated E2E tests in CI ([`build-test.yml`](https://github.com/IvanJosipovic/KubeUI/blob/main/.github/workflows/build-test.yml)). More release machinery than kubeNimbus has |
| **Stars / issues** | **322 stars**, 23 forks, 3 open issues ([repo](https://github.com/IvanJosipovic/KubeUI)) — against FreeLens 5.4k, Seabird 1.4k, Aptakube 858. Low issue traffic; no HN/Reddit thread found. Promoted by [Avalonia UI's own account](https://x.com/AvaloniaUI/status/1975118850166518229) with precisely the feature list DIST-6 quotes, which is very likely where this item's signal came from |

### What it actually ships

DIST-6 names "multi-cluster, YAML editing, logs, console and port-forwarding". **All five
confirmed**, and the real list is longer. From the [README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md)
cross-checked against source files:

- **Multi-cluster** — `ClusterManager` holds an `ObservableCollection<IClusterRuntime>`,
  loaded from the default kubeconfig plus configured ones ([`ClusterManager.cs`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Kubernetes/Client/ClusterManager.cs)).
- **Logs / console / port-forward** — `PodLogsViewModel`, `PodConsoleViewModel`,
  `PortForwarderListViewModel` ([directory](https://github.com/IvanJosipovic/KubeUI/tree/main/src/KubeUI.Avalonia/Resources/Workloads/v1/Pod/ViewModels)),
  over [`Cluster.PortForward.cs`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Kubernetes/Client/Cluster.PortForward.cs).
  The console is built on **`SvcSystems.UI.Terminal`** — the same library kubeNimbus's
  FEAT-10 proposes, written by KubeUI's author (as the [terminal report](2026-08-15-terminal-libraries.md) already noted).
- **Beyond DIST-6's list**, and these are the ones that matter for a comparison table:
  - **Server-side dry run from edit mode before saving** (README, *YAML Editor*) — this is
    kubeNimbus's unbuilt `FEAT-5`.
  - **Kubernetes-aware YAML completion for built-ins and CRDs, with field documentation in
    completion tooltips**, and inline validation while editing.
  - **Node cordon / uncordon / drain** ([`V1NodeConfig.cs`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Avalonia/Resources/Core/v1/Node/V1NodeConfig.cs)) — kubeNimbus's unbuilt FEAT-21/22/23.
  - **Service** port-forward as well as pod ([`V1ServiceConfig.cs`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Avalonia/Resources/Network/v1/Service/V1ServiceConfig.cs)) — kubeNimbus is pod-only (`ClusterClient.PortForward.cs`).
  - **Secret certificate inspection incl. expiry** ([`CertificateItemView.axaml.cs`](https://github.com/IvanJosipovic/KubeUI/tree/main/src/KubeUI.Avalonia/Resources/Configuration/v1/Secret/Views)).
  - **Resource relationship visualization** (`Features/Resources/Visualization`, over `AvaloniaGraphControl`).
  - **Multi-window, multi-monitor, dockable layout** (`Dock.Avalonia`) — kubeNimbus has
    exactly one window by UI rule 16.
  - **Automatic updates** via **Velopack**.
  - **MCP server + ACP agent integration**, a whole `src/KubeUI.AI` project landed
    2026-08-11 in [`196553d`](https://github.com/IvanJosipovic/KubeUI/commit/196553d86fdc2773cbbdc08ab876bfe543aa74c7)
    (`ModelContextProtocol` 2.1.0), shipping in `v1.1.0-beta.4`.

What it does **not** appear to have, against kubeNimbus's shipped surface: aggregated
multi-cluster ("fleet") list views, a cluster-wide RBAC "who can do X" review, Helm
release browsing, or a no-credentials demo dataset. Those were searched for in the tree
and not found; absence of a file is weaker evidence than presence, so treat this list as
indicative rather than settled.

### UI stack, and the AOT question — the decisive part

**Checked in the repo, not inferred.**

- **Avalonia 12.0.4**, Fluent + Semi.Avalonia + FluentAvaloniaUI + Irihi.Ursa themes,
  `AvaloniaUseCompiledBindingsByDefault=true`, `CommunityToolkit.Mvvm` 8.4.2 +
  `PropertyGenerator.Avalonia` source generators, AvaloniaEdit + TextMate for YAML,
  LiveChartsCore for charts, `Dock.Avalonia` for docking
  ([`KubeUI.Avalonia.csproj`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Avalonia/KubeUI.Avalonia.csproj)).
  This is the same architectural family as kubeNimbus — genuinely native widgets on Skia,
  not a webview.
- **It is not NativeAOT.** [`KubeUI.Desktop.csproj`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Desktop/KubeUI.Desktop.csproj)
  sets `PublishSingleFile` + `PublishSelfContained` + `PublishReadyToRun` +
  `PublishReadyToRunComposite` + `IncludeNativeLibrariesForSelfExtract`, with
  `EnableCompressionInSingleFile=false`. There is **no `PublishAot`, no `PublishTrimmed`
  and no `TrimMode`** anywhere in the repository — `grep` across every `.csproj`, `.props`
  and workflow returns nothing, and the string "AOT" does not appear in the repo at all.
  The publish step is a plain `dotnet publish -c Release -r <rid>`
  ([`publish-installer.yml`](https://github.com/IvanJosipovic/KubeUI/blob/main/.github/workflows/publish-installer.yml)).
  So: **JIT with ReadyToRun pre-compilation, self-contained, single-file.**
- **And it cannot cheaply become NativeAOT.** Three independent blockers, each in the
  source:
  1. It references **`KubernetesClient` 19.0.2** — the reflection-based client
     ([`KubeUI.Kubernetes.csproj`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Kubernetes/KubeUI.Kubernetes.csproj)),
     i.e. exactly the package `CLAUDE.md` forbids because "that one does not survive
     NativeAOT".
  2. It **generates and loads CRD model assemblies at runtime**:
     `_generator.GenerateAssembly(crd, "KubeUI.Models")` in
     [`Cluster.cs`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Kubernetes/Client/Cluster.cs),
     via [`KubernetesCRDModelGen`](https://github.com/IvanJosipovic/KubernetesCRDModelGen),
     which compiles C# with Roslyn and hands back an `Assembly` with an `UnloadHandle`.
     Runtime IL generation is categorically impossible under NativeAOT. This is not a
     dependency swap; it is the centre of how KubeUI models CRDs.
  3. A reflection `ViewLocator` plus `MakeGenericType`/`Activator.CreateInstance` for
     `ResourceListViewModel<T>` in six places.

  Worth noting fairly: KubeUI's own [`AGENTS.md`](https://github.com/IvanJosipovic/KubeUI/blob/main/AGENTS.md)
  says "avoid reflection whenever possible; prefer source generators" — so this is a
  considered trade, not carelessness. It buys typed CRD models, which is a real feature
  kubeNimbus answers differently (`DynamicResource` over `JsonElement`).
- **No startup-time claim is made anywhere** — not in the README, not on the landing page.
  They do care about it: there is an `AppStartupBenchmarks.StartAndStopDesktopApp`
  BenchmarkDotNet case on the `v1.1.0-beta.4` tag, but it measures host + `AppBuilder`
  setup in-process, not time to first frame, and publishes no number.

### The measurement, actually run

Because the whole surviving claim rests on speed, it was measured rather than asserted.

**Method.** Both apps published `linux-x64` in this container and started under one
`Xvfb :77` 1280×800 server with an identical harness: fork the process, poll
`xdotool search --onlyvisible` every 20 ms until a window reports geometry, record
wall-clock. Five runs each, no kubeconfig present, KubeUI's telemetry and file logging
disabled in `~/.kubeui/settings.json` so a blocked OTLP endpoint could not distort it.

| | kubeNimbus | KubeUI |
|---|---|---|
| Publish | `-p:PublishAot=true` (the shipping config) | `dotnet publish -c Release -r linux-x64`, exactly as its workflow does |
| Runs (ms) | 174, **147, 153**, 180, 156 | 899, **655, 650**, 645, 611 |
| Median | **~156 ms** | **~645 ms** |
| Shipped payload | 47.6 MB executable + `libSkiaSharp.so` 11.2 MB + `libHarfBuzzSharp.so` 2.8 MB ≈ **62 MB** | **a single 382 MiB file** (400 883 353 bytes) |

**≈4× on startup, ≈6× on disk.** Caveats, stated so the number can be trusted or
discounted:

- KubeUI's `global.json` pins SDK `10.0.301`; this container has `10.0.110`, so the pin
  was relaxed locally to build. Only the SDK feature band differs; the publish properties
  are KubeUI's own, unmodified.
- The harness measures *window mapped with geometry*, not *composited frame*. It is
  therefore slightly generous to whichever app maps early — kubeNimbus's own
  `--smoke-test`, which waits for a real compositor tick, reported **103–108 ms** on the
  same binary, i.e. the harness **overstates kubeNimbus** and the true gap is wider.
- All ten runs were page-cache-warm. Cold start was not compared like-for-like (the very
  first `--smoke-test` invocation, under a fresh `xvfb-run`, took 3 436 ms), and a
  382 MiB file will suffer more from a cold cache than a 62 MB one, not less.
- One machine, one container, Linux/X11 only. This is a directional result, not a
  benchmark suite — see the proposed `DIST-8` row.
- KubeUI's *download* is smaller than 382 MiB: Velopack compresses the release assets.
  The 382 MiB is the installed on-disk payload.

### Distribution — where KubeUI is ahead of kubeNimbus, plainly

From [`publish-installer.yml`](https://github.com/IvanJosipovic/KubeUI/blob/main/.github/workflows/publish-installer.yml):
**six RIDs** (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`),
**Azure Trusted Signing on Windows**, **Developer ID signing + notarization on macOS**,
Velopack installers *and* portables, **auto-update**, a
[Homebrew cask](https://github.com/IvanJosipovic/homebrew-repo) (verified directly at
v1.0.2), plus `winget install KubeUI` and a Microsoft Store listing claimed in the README
(neither independently verifiable here — `apps.microsoft.com` is blocked and the
`winget-pkgs` manifest was not found at the paths tried).

kubeNimbus today: four RIDs, **unsigned**, no installer, no auto-update, no package
manager. That is `DIST-1`, `DIST-2` and `DIST-3`, all sitting unprioritised in the Inbox.
This report does not propose new rows for them; it reports that a direct peer has shipped
all three, which is the argument for raising them.

### Telemetry — where kubeNimbus is ahead, plainly

`TelemetryEnabled` defaults to **`true`** ([`Settings.cs`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Avalonia/Options/Settings.cs)),
and Release builds export OpenTelemetry logs and metrics to
`https://otel-grpc.kubeui.com` with an embedded API key
([`Program.cs`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Desktop/Program.cs)).
[`PRIVACY.md`](https://github.com/IvanJosipovic/KubeUI/blob/main/PRIVACY.md) documents it
honestly and it is opt-out in Settings. kubeNimbus lists telemetry as a **non-goal
forever** and `SECURITY.md` claims none. Against the one peer that is otherwise the same
shape, that is a real and checkable differentiator — and it is currently not mentioned in
the Mission paragraph at all.

## 2. Which clauses of the paragraph fail

Clause by clause, no softening.

| Clause | Verdict |
|---|---|
| "Lens is subscription-only for commercial use" | **Stands, with a wording risk.** Lens's free tier is scoped to *personal* use and paid tiers are ~$14.90–$25/user/month ([pricing](https://lenshq.io/pricing/lens-k8s-ide), [plans update](https://lenshq.io/blog/lens-plans-and-pricing-update)). "Subscription-**only**" slightly overstates it — a free tier exists; "subscription-gated for commercial use" is the accurate phrasing |
| "Mirantis moved exec/logs/shell into proprietary code in 6.3" | **Accurate.** [lensapp/lens#6823](https://github.com/lensapp/lens/issues/6823) "OpenLens 6.3.0 — No Logs or Shell buttons"; the [HN thread](https://news.ycombinator.com/item?id=34233790); and [`openlens-node-pod-menu`](https://github.com/alebcay/openlens-node-pod-menu), the extension that existed to restore exactly Logs/Shell/Attach |
| "and a heavy Electron app" | **Accurate** |
| "OpenLens is dead" | **Accurate.** Last release v6.5.2-366, June 2023; sources removed upstream ([MuhammedKalkan/OpenLens#188](https://github.com/MuhammedKalkan/OpenLens/issues/188)). Not formally archived, so "dead" is a judgement — a defensible one |
| "FreeLens (the surviving fork) is still Electron" | **Accurate**, verified in source: `electron`, `electron-vite` and `electron-builder` in [`freelens/package.json`](https://github.com/freelensapp/freelens/blob/main/freelens/package.json). MIT, 5.4k stars |
| "Aptakube is fast and polished but paid/closed" | **Accurate.** No `LICENSE` in [its repo](https://github.com/aptakube/aptakube) (404) and no source — a marketing README and an issue tracker; [EULA](https://aptakube.com/legal/eula) and [pricing](https://aptakube.com/pricing) confirm commercial. Built on Tauri, and its README leads with "😉 **NOT** another Electron app" |
| "Headlamp is web-first" | **Accurate, and understated.** Its desktop shell is *also* Electron — `electron-builder` in [`app/package.json`](https://github.com/kubernetes-sigs/headlamp/blob/main/app/package.json) |
| "k9s is a keyboard TUI" | **Accurate** |
| **"Nobody ships truly fast + open source + modern native desktop UI"** | **FALSE as written.** KubeUI is MIT, Avalonia-native, actively released and feature-comparable. Two of the three conjuncts are plainly satisfied by a project that has existed since June 2024 and hit 1.0 in May 2026. Only "truly fast" is defensible, and only against a measurement this report had to run itself because nobody publishes one |
| "kubeNimbus fills that gap" | **Follows the false clause and falls with it.** There is no vacant gap; there is a narrower differentiator |

The failure is not a technicality. The sentence is the paragraph's load-bearing claim, it
is stated absolutely ("nobody"), and it is refuted by a single link to a project in the
same language, the same UI framework and the same licence.

## 3. What else the paragraph omits

Every open-source desktop Kubernetes client found that is **not** Electron, plus the ones
that are and get miscounted. Activity measured by cloning and reading `git log`.

| Project | Stack | Licence | State (checked 2026-08-17) | Does it embarrass the paragraph? |
|---|---|---|---|---|
| [**KubeUI**](https://github.com/IvanJosipovic/KubeUI) | **Avalonia 12 / .NET 10**, native widgets | MIT | **Very active** — `v1.1.0-beta.4` 2026-08-12; 322★ | **Yes — decisively.** §1, §2 |
| [**Seabird**](https://github.com/getseabird/seabird) | **Go + GTK4/libadwaita**, fully native | MPL-2.0 | **Stalled.** 508 commits in 2024, **11 in 2025, none in 2026**; last commit and last release (`v0.6.0`) both 2025-08-13; 1.4k★ | **Yes, but softly.** 1.4k stars and a [Show HN](https://news.ycombinator.com/item?id=39176541) mean it appears in every "Lens alternative" list. It is the strongest *native* counter-example, and it is dormant — which is usable, but only if the paragraph says "dormant" rather than ignoring it |
| [**kubegui**](https://github.com/gerbil/kubegui) | **Wails** (Go + React in a system webview) | MIT | **Very active** — last commit 2026-08-16, `v2.0.7` 2026-08-15; 88★ | Partly. Not Electron, but a webview UI, so "modern native desktop UI" is arguable. Ships logs, exec, port-forward, CRDs, RBAC graph, Trivy CVE scanning |
| [**Kubus**](https://github.com/FloSch62/Kubus) | **Electron** (`@kubus/electron`) | MIT | Active, v0.7.0; 22★ | No — belongs in the "still Electron" list |
| [**zapkube**](https://github.com/zapkube/zapkube) | Wails3 | **Closed** ("not open source (yet)", releases-only repo) | New; 3★ | No, but note the tagline: *"The fastest Kubernetes desktop client."* Someone is already competing on our sentence |
| [**kubenav**](https://github.com/kubenav/kubenav) | Flutter + Go | Apache-2.0 | Active-ish (v5.5.1, 2026-05) | No — it has repositioned as a **mobile** app (App Store / Play Store) |
| **KubeDesk** | Java 21 + JavaFX | MIT (per write-up) | Unknown | Weak evidence: only a [blog post](https://blog.devops.dev/kubedesk-a-free-open-source-desktop-gui-for-kubernetes-your-kubectl-without-the-terminal-ee9cd11ca25a) was found; **the repository could not be located**. Recorded for completeness, not relied on |

**Conclusion for §3:** the paragraph omits one project that refutes it (KubeUI), one that
would refute it if it were alive (Seabird), and one active near-miss (kubegui). No further
stale facts were found — every other clause in the paragraph checks out, which is a good
result for a paragraph written before any of this was verified.

## 4. The proposed replacement

The current text cannot be patched by adding a clause; the sentence it is built around is
false. The replacement below keeps the register — an engineering contract stating a
position and the evidence for it — and drops "nobody ships" for a claim that survives a
reader clicking every link. It runs ~5 lines longer than the original, which is the cost
of naming a real competitor with numbers instead of asserting there isn't one.

**Lift this verbatim into `CLAUDE.md`, replacing the second paragraph of `## Mission`:**

```markdown
The 2026 Kubernetes GUI market has one crowded end and one thin one. Lens is
subscription-gated for commercial use (Mirantis moved exec/logs/shell into
proprietary code in 6.3) and a heavy Electron app; OpenLens is dead; FreeLens
(the surviving fork) is still Electron, and so is Headlamp's desktop shell;
Aptakube is fast and polished but closed and paid; k9s is a keyboard TUI.
**The one true peer is [KubeUI](https://github.com/IvanJosipovic/KubeUI)** —
MIT, Avalonia 12, .NET 10, actively released and feature-comparable; the only
other native open-source client, [Seabird](https://github.com/getseabird/seabird)
(Go/GTK4), has had no commit since August 2025. So the claim is **not** "nobody
ships open source + native". KubeUI is **not** NativeAOT and cannot cheaply
become so — it ships ReadyToRun self-contained on the reflection-based
`KubernetesClient`, and generates CRD models with Roslyn at runtime — and that
is where kubeNimbus differs measurably: **~156 ms to first window against
~645 ms, a ~62 MB payload against a 382 MiB single file** (measured head to
head, linux-x64, `docs/research/2026-08-17-kubeui-positioning.md`), plus **no
telemetry** where KubeUI's is on by default. kubeNimbus is the narrower, faster,
quieter one: Aptakube's polish, NativeAOT startup, MIT, Kubernetes-first.
```

**Optional appended sentence**, if `CLAUDE.md` should also record where this app is
*behind* — recommended, since the file's whole discipline is writing down what is true
rather than what is flattering:

```markdown
Where KubeUI is ahead and we are not: signed and notarized binaries, installers
with auto-update, winget/Store/Homebrew distribution, server-side dry-run,
schema-aware YAML completion and node drain. None of that is a reason to change
course; all of it is a reason not to write a comparison table yet.
```

**`README.md` carries the same claim in shorter form (lines 25–30) and has the same
defect.** Replacing it is part of the same edit:

```markdown
> **Why another one?** The 2026 Kubernetes GUI market is thin in one specific
> place. Lens is subscription-gated for commercial use and heavy Electron;
> OpenLens is dead; FreeLens is still Electron, and so is Headlamp's desktop
> app; Aptakube is polished but closed and paid; k9s is a keyboard TUI.
> [KubeUI](https://github.com/IvanJosipovic/KubeUI) is the closest thing to
> kubeNimbus — also MIT, also Avalonia — and is the comparison worth making:
> kubeNimbus is NativeAOT, opens in ~150 ms rather than ~650 ms, ships a ~62 MB
> payload rather than 382 MiB, and sends no telemetry at all.
```

Three things about the proposed text, so the implementer does not have to re-derive them:

1. **Every number in it is from §1's measurement**, which is one machine and Linux/X11
   only. If that feels too thin to publish in the README, `DIST-8` below is the row that
   makes it robust — but the *relative* result is large enough (4×) that no plausible
   measurement error reverses it.
2. **"Feature-comparable" is deliberate**, not "broader" or "narrower". KubeUI is ahead on
   dry-run, YAML completion, node ops, visualization, docking and distribution; kubeNimbus
   is ahead on fleet views, RBAC who-can, Helm browsing, the demo cluster and startup.
   Neither dominates.
3. **The Seabird clause is the one most likely to go stale.** It is true today
   (no commits since 2025-08-13); if Seabird revives, that clause is wrong and the
   paragraph weakens. It is worth the sentence because Seabird is the native
   counter-example a reader will reach for first.

## Proposed backlog items

Format matches `docs/BACKLOG.md`'s existing Inbox rows — six columns, notes folded into
the Signal cell as `**Notes:**` — so these paste in directly. IDs left as `—` for a human
to assign; the priority column is blank by rule, and the `Rec` column is this report's
recommendation only.

| — | Rewrite `CLAUDE.md`'s Mission market paragraph and `README.md`'s "Why another one?" block from `docs/research/2026-08-17-kubeui-positioning.md` §4 — *done when neither file claims "nobody ships fast + open source + native"* | The claim is **false**: [KubeUI](https://github.com/IvanJosipovic/KubeUI) is MIT, Avalonia 12/.NET 10, `v1.1.0-beta.4` on 2026-08-12, and ships multi-cluster/YAML/logs/console/port-forward plus dry-run, node drain and schema completion. **Notes:** the report contains both replacement blocks ready to lift verbatim; this is a docs-only edit with no code change. Do **not** hand-edit the wording — the numbers in it are tied to a measurement recorded in the report. Blocks DIST-4, which would otherwise publish the false claim to users | S | **P1** | |
| — | Add KubeUI (and dormant-but-cited Seabird) to `DIST-4`'s comparison page scope | `DIST-4` names Lens / FreeLens / Aptakube / Headlamp / k9s and omits the only direct open-source native peer. A comparison table that skips the closest competitor is the one thing worse than not publishing one. **Notes:** amends an existing Inbox row rather than adding work; the honest axes are startup, footprint, telemetry and surface size — **not** feature count, where KubeUI wins several. Pairs with `DIST-7` | S | P2 | |
| — | A repeatable startup benchmark: kubeNimbus vs KubeUI vs FreeLens, scripted, on more than one machine and OS — *done when a committed script reproduces the numbers and CI can re-run it* | The entire surviving positioning claim now rests on one number measured once, in one container, on Linux/X11 (§1). Marketing emphasis: the README badge already says "NativeAOT — shipping config" and `DIST-5` promises a capture showing "it opens in ~150 ms". **Notes:** the harness used here (Xvfb + `xdotool` poll to first mapped window) is ~15 lines and is described in the report; `--smoke-test` already gives kubeNimbus a first-*frame* number, so the honest cross-app metric is the weaker window-mapped one. Windows/macOS numbers need `VER-*`-class access this repo does not have | M | P2 | |
| — | Server-side **dry-run** before apply, as a first step toward `FEAT-5`'s full diff | Marketing: KubeUI leads its YAML Editor section with "Server-side dry run from edit mode before saving" ([README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md)). **Notes: this is not a new row so much as evidence for the existing `FEAT-5`** (dry-run *diff*, `P2`) — the finding is that a direct peer already ships the cheap half. Consider splitting `FEAT-5` into "dry-run + report what the server says" (S) and "render a diff" (M), since the first is `?dryRun=All` on the existing apply path in `ClusterClient.Dynamic.cs` and needs no new UI concept | S | P2 | |
| — | Port-forward a **Service**, not only a Pod — resolve the service's endpoints and forward to one, naming which pod was chosen | Marketing/table stakes: KubeUI ships service port-forward alongside pod ([`V1ServiceConfig.cs`](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Avalonia/Resources/Network/v1/Service/V1ServiceConfig.cs)), as does `kubectl port-forward svc/x`. **Notes:** `ClusterClient.PortForward.cs` is pod-only today. The API has no service port-forward endpoint — kubectl resolves the service to a pod client-side, so the pane must **say which pod it picked** and behave sanely when that pod is replaced (UI rules 9 and 11). No demand evidence was found, only competitor parity — weigh accordingly | M | P3 | |
| — | Decode `kubernetes.io/tls` Secrets: subject, issuer, SANs and **expiry**, beside the existing reveal | Marketing: KubeUI ships "Secret certificate inspection, including certificate detail views such as expiry" ([README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md), [`CertificateItemView`](https://github.com/IvanJosipovic/KubeUI/tree/main/src/KubeUI.Avalonia/Resources/Configuration/v1/Secret/Views)). **Notes:** small and self-contained — `X509Certificate2` over the already-decoded base64, no new dependency and AOT-safe. Fits the existing masked/reveal model (a certificate is public; the *key* half stays masked). No user demand evidence found, only competitor parity | S | P3 | |

### Reported as pressure, not proposed — decisions that are already made

Per the brief's instruction to separate "we lack it" from "we chose not to":

- **Multi-window and a dockable layout.** KubeUI leads its README's *Cluster and Workspace*
  section with "Multi-window and multi-monitor friendly workspace" and a dockable layout
  (`Dock.Avalonia`). kubeNimbus has **exactly one window by UI rule 16**, and the rule
  documents a concrete bug (the DWM caption-colour failure) that made it. This is market
  pressure on a deliberate decision. **No row proposed**; if the decision is ever
  revisited, `nimbusUi` and the DWM half of `ThemedWindowChrome` are the prerequisites the
  rule already names.
- **Auto-update.** KubeUI ships Velopack auto-update. `DIST-3` already exists and is
  correctly framed as a *policy* decision first, because the README promises no network
  connection beyond the user's clusters. Nothing here changes that framing — it only
  raises the pressure.
- **Telemetry.** A peer shipping opt-out telemetry is *not* an argument to relax the
  non-goal; it is the clearest reason yet to advertise it. Folded into the paragraph in §4
  rather than proposed as work. **Challenges no stated non-goal.**
- **Typed CRD models.** KubeUI's runtime Roslyn generation buys typed CRDs and costs it
  NativeAOT. kubeNimbus's `DynamicResource`/`JsonElement` approach is the same trade taken
  the other way, and hard rule 2 and the AOT constraint make it non-negotiable here. Noted
  so nobody re-opens it: **the thing that makes KubeUI slower is the thing that makes its
  CRD support typed.**

## Sources

- KubeUI: [repo](https://github.com/IvanJosipovic/KubeUI) · [README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md) · [LICENSE](https://github.com/IvanJosipovic/KubeUI/blob/main/LICENSE) · [PRIVACY.md](https://github.com/IvanJosipovic/KubeUI/blob/main/PRIVACY.md) · [AGENTS.md](https://github.com/IvanJosipovic/KubeUI/blob/main/AGENTS.md) · [releases](https://github.com/IvanJosipovic/KubeUI/releases) · [Desktop csproj](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Desktop/KubeUI.Desktop.csproj) · [Avalonia csproj](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Avalonia/KubeUI.Avalonia.csproj) · [Kubernetes csproj](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Kubernetes/KubeUI.Kubernetes.csproj) · [publish-installer.yml](https://github.com/IvanJosipovic/KubeUI/blob/main/.github/workflows/publish-installer.yml) · [build-test.yml](https://github.com/IvanJosipovic/KubeUI/blob/main/.github/workflows/build-test.yml) · [Program.cs](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Desktop/Program.cs) · [Settings.cs](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Avalonia/Options/Settings.cs) · [Cluster.cs](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Kubernetes/Client/Cluster.cs) · [ClusterManager.cs](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Kubernetes/Client/ClusterManager.cs) · [MCP/ACP commit](https://github.com/IvanJosipovic/KubeUI/commit/196553d86fdc2773cbbdc08ab876bfe543aa74c7) · [Homebrew tap](https://github.com/IvanJosipovic/homebrew-repo) · [Avalonia UI promotion](https://x.com/AvaloniaUI/status/1975118850166518229)
- [KubernetesCRDModelGen](https://github.com/IvanJosipovic/KubernetesCRDModelGen)
- Lens: [pricing](https://lenshq.io/pricing/lens-k8s-ide) · [plans update](https://lenshq.io/blog/lens-plans-and-pricing-update) · [lens#6823](https://github.com/lensapp/lens/issues/6823) · [HN 34233790](https://news.ycombinator.com/item?id=34233790) · [openlens-node-pod-menu](https://github.com/alebcay/openlens-node-pod-menu)
- OpenLens: [repo](https://github.com/MuhammedKalkan/OpenLens) · [#188 "Public sources are gone"](https://github.com/MuhammedKalkan/OpenLens/issues/188)
- FreeLens: [repo](https://github.com/freelensapp/freelens) · [freelens/package.json](https://github.com/freelensapp/freelens/blob/main/freelens/package.json)
- Aptakube: [repo/README](https://github.com/aptakube/aptakube) · [pricing](https://aptakube.com/pricing) · [EULA](https://aptakube.com/legal/eula)
- Headlamp: [repo](https://github.com/kubernetes-sigs/headlamp) · [app/package.json](https://github.com/kubernetes-sigs/headlamp/blob/main/app/package.json) · [desktop docs](https://headlamp.dev/docs/latest/installation/desktop/)
- k9s: [repo](https://github.com/derailed/k9s)
- Other native/near-native clients: [Seabird](https://github.com/getseabird/seabird) ([Show HN](https://news.ycombinator.com/item?id=39176541)) · [kubegui](https://github.com/gerbil/kubegui) · [Kubus](https://github.com/FloSch62/Kubus) · [zapkube](https://github.com/zapkube/zapkube) · [kubenav](https://github.com/kubenav/kubenav) · [KubeDesk write-up](https://blog.devops.dev/kubedesk-a-free-open-source-desktop-gui-for-kubernetes-your-kubectl-without-the-terminal-ee9cd11ca25a)

# Competitor delta, 2026-09-22 to 2026-11-14

The field survey after release 1.1.0. It records what each competing Kubernetes desktop
client shipped, charged for or started leading with since the last survey
([`history/v0.4.0/research.md`](../product-loop/history/v0.4.0/research.md), 2026-09-22),
what their users ask for, and a ranked candidate list. The matrix it feeds is
[`COMPETITOR_MATRIX.md`](../product-loop/COMPETITOR_MATRIX.md). Every claim links its source;
"not found" and "not checked" are kept distinct.

## What was searched, and what could not be reached

- **Release pages**: Aptakube, Headlamp, FreeLens, k9s, Seabird and KubeUI on GitHub (pages
  and the releases API); Radar's releases through the API.
- **Lens**: the blog index and feed on lenshq.io, the 2026.11.1 post and the pricing page.
  The same posts under `lens.dev` answered **403** and were read through `lenshq.io`. The
  2026.10, 2026.10.2 and 2026.9.1 posts were read only as feed summaries.
- **Demand**: the GitHub search API, open issues sorted by reactions, for FreeLens,
  Headlamp, k9s, Aptakube, KubeUI and Radar, plus issues created after 2026-09-15.
- **Community**: **Reddit could not be read** (old.reddit.com refused the fetch, and web
  search returned no r/kubernetes threads). No Hacker News thread about a Kubernetes desktop
  client in this window was found; the one HN hit was an unrelated deployment tool. In their
  place: a hands-on review of six desktop clients dated 2026-10-29
  ([alexandre-vazquez.com](https://alexandre-vazquez.com/kubernetes-desktop-clients-2026/)),
  and a review of Radar ([dev.to](https://dev.to/alexandrev/radar-a-new-kubernetes-ide-worth-knowing-about-vs-openlens-freelens-4bma)).
  Both are written by one author and are secondary sources.
- **Kunobi**: only its press release, a vendor blog and listings were found
  ([press release](https://www.wboc.com/online_features/press_releases/kunobi-desktop-launches-to-bring-operational-clarity-to-kubernetes-and-gitops-teams/article_28ccf5a6-e987-5a89-8441-321acf119bab.html),
  [Zondax blog](https://zondax.ch/blog/kunobi-a-local-kubernetes-desktop-for-gitops-and-multi-cluster-management),
  [Product Hunt](https://www.producthunt.com/products/kunobi)). No changelog for the window was
  found, so nothing about it since 2026-09-22 is verified.

## The delta, product by product

### Lens (Mirantis) — five releases, everything new is paid, and the story is now AI

From the [blog index](https://lenshq.io/blog), the [feed](https://lenshq.io/feed.xml) and
the [2026.11.1 post](https://lenshq.io/blog/lens-release-2026-11/):

- **2026.9.1 (2026-09-21)**: Cost Monitor — spend per namespace with nothing installed in
  the cluster; VictoriaMetrics auto-detection.
- **2026.10 (2026-09-29)**: "AI-built extensions" (describe an extension and an AI tool
  writes it) and an Extension Marketplace.
- **2026.10.2 (2026-10-28)**: **Lens Studio** — split view, tabs in their own windows.
- **2026.11.1 (2026-11-10)**: **Argo Rollouts** (navigator group, current step and traffic
  share, promote/pause/resume/abort/restart with confirmations, "Needs attention" on the Argo
  dashboard), **MCP server with almost 90 tools** (changes open in Lens for review before they
  apply), startup "more than twice as fast" (no number), and recent/favourite searches on
  every list. The post puts Rollouts and MCP on Plus, Pro and Enterprise; faster startup and
  searches on every plan.
- **Pricing now** ([pricing](https://lenshq.io/pricing)): Personal free; Plus $25/month
  annual or $30 monthly; Pro $25–30/seat; Enterprise $50–60/seat. **Organisations with more
  than $10M revenue or funding must pay.** GitOps (Argo CD *and* Flux), Ask AI, cloud
  integrations, Security Center, Lens Studio, cost monitoring and the advanced editor are
  Plus. The pricing table puts the MCP server in Pro and Enterprise only, which contradicts the
  release post's "Plus, Pro, and Enterprise"; the matrix follows the post and flags it.
- Half the blog posts since August are about *Lens Agents* sandboxes for coding agents, not
  the IDE.

### FreeLens — v2.0.0 (2026-10-28)

[Releases](https://github.com/freelensapp/freelens/releases): a breaking extension API (v1
extensions must be updated and reinstalled), a standalone terminal outside a cluster session,
custom resources listed from discovery without CRD read access, search that ignores dashes and
underscores, an option to skip namespace-authorization and metrics checks on large clusters,
SOCKS5 proxy, Vite and React 19, macOS 13 minimum. Still Electron.

Its top-reacted open issues are unchanged and all about metrics without Prometheus
([#466](https://github.com/freelensapp/freelens/issues/466) 40,
[#627](https://github.com/freelensapp/freelens/issues/627) 21,
[#524](https://github.com/freelensapp/freelens/issues/524) 20,
[#1670](https://github.com/freelensapp/freelens/issues/1670) 9). Also open:
auto-update ([#552](https://github.com/freelensapp/freelens/issues/552), 8), *Clear logs*
([#2503](https://github.com/freelensapp/freelens/issues/2503)) and a terminal connected with the
user's kubeconfig ([#2880](https://github.com/freelensapp/freelens/issues/2880)).

### Aptakube — 1.21.1 to 1.22.3, and a free tier

[Releases](https://github.com/aptakube/aptakube/releases), in order of how much they bear
on kubeNimbus:

- **Change Protection** (1.22.0): a context can be Protected or Read-only, which blocks
  cluster-changing actions; Protected unlocks temporarily. Contexts get an icon, colour, tags
  and (1.22.2) an alias shown instead of the context name.
- **Rollback** of Deployments/StatefulSets/DaemonSets with the current revision tagged and its
  Rollback disabled (1.22.0).
- **Helm Upgrade and Rollback** in the background, the rollback modal comparing values
  (1.21.5); uninstall in the background (1.22.2).
- **Health page** merging the cluster and workloads overviews, broken down by namespace (1.22.2).
- **Port-forwards** to Services or workloads follow their pods; a redesigned manager with saved
  forwards and Start All (1.22.2).
- **Aggregated logs across several selected workloads** (1.21.2); an EOF entry when a
  container exits; the log histogram replaced with a range selector (1.21.3).
- `G`-then-key jumps, palette actions, regex list filter, drain with progress and cancel,
  VPA, MutatingAdmissionPolicy, CSRs, Argo sync with prune, a "Sign in with…" button when an
  EKS/GKE/AKS SSO session expires (1.21.6), downloads about 30% smaller.
- **Pricing** ([pricing](https://aptakube.com/pricing)): "Free for small clusters" — every
  feature free up to 5 nodes per cluster; Personal $7/month or $79/year; Team $5/seat/month.
  This replaces "paid" in the old matrix.

### Headlamp — v0.46.0 (2026-10-15)

[Releases](https://github.com/kubernetes-sigs/headlamp/releases): DRA devices (alpha),
Job suspend/resume, a guided Endpoints form, a setting for where the details drawer docks,
OIDC auto-login, service-backed port-forwards that reconnect after pod restarts, log copy.
The plugin SDK 0.15 alphas (November) add resource-map and project APIs. Its top-reacted
open issues are about in-cluster deployment, a built-in terminal
([#2353](https://github.com/kubernetes-sigs/headlamp/issues/2353), 24), proxy support and
**AWS SSO / exec-plugin login** ([#358](https://github.com/kubernetes-sigs/headlamp/issues/358),
[#1716](https://github.com/kubernetes-sigs/headlamp/issues/1716),
[#1582](https://github.com/kubernetes-sigs/headlamp/issues/1582)). An Argo CD plugin is
work in progress ([#5260](https://github.com/kubernetes-sigs/headlamp/issues/5260)); a
Cilium/Hubble network-flow UI is proposed ([#8007](https://github.com/kubernetes-sigs/headlamp/issues/8007)).

### k9s — no release since v0.51.0 (2026-06-06)

[Releases](https://github.com/derailed/k9s/releases). Top-reacted open issues: editing a
Secret without base64 by hand ([#1017](https://github.com/derailed/k9s/issues/1017), **45**,
open since 2021; also [#4080](https://github.com/derailed/k9s/issues/4080)), sorting pods by
CPU or memory broken ([#3793](https://github.com/derailed/k9s/issues/3793), 24), a misleading
error on expired auth ([#3730](https://github.com/derailed/k9s/issues/3730), 9), select-all
([#4247](https://github.com/derailed/k9s/issues/4247)), and a Helm view that downloads every
revision on each refresh ([#4375](https://github.com/derailed/k9s/issues/4375)).

### KubeUI — v1.3.0 to v1.8.1, betas of v1.9.0

[Releases](https://github.com/IvanJosipovic/KubeUI/releases): check-for-updates (1.3.0) and
an Update Available view (1.8.0); font zoom (1.4.0); Crossplane managed-resource diff
(1.5.0); an optional **MCP server**, with a v1.6.1 security fix — requests were accepted
without Host and Origin validation; cluster gauges (1.7.0); a Metrics Viewer in the 1.9.0
betas. Its desktop project still sets `PublishReadyToRun`, not NativeAOT
([csproj](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Desktop/KubeUI.Desktop.csproj)).

### Seabird — stalled

Latest release v0.6.0 ([releases](https://github.com/getseabird/seabird/releases)); the
2026-10-29 review reports no release in about 14 months and a prebuilt binary that did not
start on Debian 12 ([review](https://alexandre-vazquez.com/kubernetes-desktop-clients-2026/)).

### Newcomers with traction

- **Radar** (Skyhook, Apache-2.0, Go, [repo](https://github.com/skyhook-io/radar)): 4,123
  stars on 2026-11-14 and roughly weekly releases (v1.14 to v1.20.3 since mid-September).
  Leads with "See what's broken, why, and what changed": issues, topology, a persistent event
  timeline, Helm with revision diffs, Argo CD and Flux, live service traffic, cluster audits
  and a built-in MCP server. Measured at ~610 MB PSS in the review. Its top issues are about
  remote-MCP OAuth ([#1434](https://github.com/skyhook-io/radar/issues/1434)) and parallel
  contexts ([#199](https://github.com/skyhook-io/radar/issues/199)).
- **PodSteer, Spinoza, KubeSpade** (review only): PodSteer leads with an "assessment-first"
  overview that flags missing data, plus a read-only MCP; Spinoza with typed confirmations for
  destructive actions and drain previews; KubeSpade with running your own AI CLI behind
  approval-gated MCP tools.
- **Kunobi** (Zondax, Rust + Tauri, v1 in May 2026, Community free, Pro $9/month): Flux and
  Argo CD auto-detection with reconcile/sync/suspend, an AI assistant and MCP server, and
  local KIND clusters.
- **k10s** — a clickable TUI with AI, Show HN 2026-10-08 ([zeli.app mirror](https://zeli.app/story/50009904)); not a desktop client.

The review's criteria for judging a client are worth recording as the market's current
language: honest reporting of what the client cannot see under restricted RBAC, safety guard
rails against destructive actions, clear licensing, memory, polish and multi-cluster.

## Demand signals, ranked by strength of evidence

1. **Metrics without Prometheus** — FreeLens's four top issues, 90 reactions in total. kubeNimbus
   has it; this is positioning, not a gap.
2. **Edit a Secret's values decoded** — k9s #1017, 45 reactions, the top open issue in a
   tool with ~30k stars; #4080 repeats it.
3. **A built-in or kubeconfig-bound terminal** — Headlamp #2353 (24), FreeLens #2880, Radar
   #1953 (closed). kubeNimbus has both an exec terminal and "open a terminal on this cluster".
4. **Sorting by CPU/memory that works** — k9s #3793 (24). kubeNimbus sorts any column.
5. **Exec-plugin / SSO logins that work** — Headlamp #358, #1716, #1582 (41 together); Aptakube
   shipped a "Sign in with…" button (1.21.6). kubeNimbus finds plugins like a login shell and
   refreshes a 401 in place.
6. **Auto-update** — FreeLens #552 (8). Refused for kubeNimbus by the owner (DIST-3).
7. **Debug pods** — Aptakube #108 (5, open since 2023). kubeNimbus has debug containers.
8. **Clear logs** — FreeLens #2503. kubeNimbus has it.
9. **Rollback / revisions** — no new demand found this window; the old evidence
   (Headlamp #3222, FreeLens #418) and fresh marketing (Aptakube 1.22.0) stand.
10. **MCP / AI** — thin demand (Radar #1434, 6 reactions, is about auth for an MCP server it
    already has); very strong marketing: Lens, KubeUI, Radar, PodSteer, Kunobi and KubeSpade
    all ship or lead with it.

## Candidates

At most ten, ranked by evidence × size of the gap × inverse cost. None is built unless
the fourth column says so.

| User outcome | Signal, with the link | Size | Does kubeNimbus have it? | Notes |
|---|---|---|---|---|
| **1. Edit a Secret's values as text, not base64** — the YAML editor offers the decoded `data` for editing and re-encodes on apply | **Demand**: [k9s#1017](https://github.com/derailed/k9s/issues/1017) (45, top open issue), [k9s#4080](https://github.com/derailed/k9s/issues/4080) | S–M | **Partial**: read-only Reveal panel; `data` stays base64 (`docs/engineering/configmaps-and-secrets.md`) | The doc records "matching kubectl" as a choice, so this asks to revisit it. Must not leak decoded values into the undo history or a saved file beyond what the user asks; must keep binary values intact (non-UTF-8 stays base64). No dependency |
| **2. A workload's revisions and rollback** — Revisions tab (number, age, images, change-cause, current), pod-template diff against current, rollback on the confirm strip | **Marketing**: Aptakube 1.22.0 ([releases](https://github.com/aptakube/aptakube/releases)); Headlamp v0.41; k9s `Ctrl-l`. **Demand** (old): [headlamp#3222](https://github.com/kubernetes-sigs/headlamp/issues/3222) | M | **No** (no Revisions tab in `WorkloadDetailView.axaml`; never filed) | Carried from v0.4.0 candidates 3–5. Argo self-heal undoes a rollback of a managed workload; the strip should say so. Workload rollback is not Helm rollback, but the owner may want the two decisions to read alike |
| **3. Protected / read-only contexts** — a per-context switch that disables every mutating command, unlockable for a while | **Marketing**: Aptakube 1.22.0 Change Protection; k9s `--readonly` ([#4293](https://github.com/derailed/k9s/issues/4293) shows people rely on it); the review's "safety guard rails" criterion ([review](https://alexandre-vazquez.com/kubernetes-desktop-clients-2026/)) | M | **Partial**: environment colours, confirm strip naming the cluster, production deletes always ask | A setting keyed by context name, not a credential (settings rule 4 holds). Every mutating command already goes through `RowActionViewModel`, which is where a gate would live; the palette and the Argo/Helm panes are the other entry points |
| **4. Helm revision diff (read-only)** — compare the values and manifest of two revisions of a release | **Marketing**: Aptakube 1.21.5 rollback modal compares values; Radar "Helm with revision diffs" ([repo](https://github.com/skyhook-io/radar)) | S | **Partial**: history and per-revision load exist (`docs/engineering/helm-releases.md`); no diff | Stays inside the read-only Helm decision. `TextDiff` from the apply preview already exists |
| **5. Logs of several selected workloads in one stream** | **Marketing**: Aptakube 1.21.2 ([releases](https://github.com/aptakube/aptakube/releases)) | S–M | **Partial**: one workload per stream; an Argo app of several workloads is one stream (`docs/engineering/multi-pod-logs.md`) | Multi-select exists since 0.7.0; the per-pod tail budget already divides by pod count |
| **6. Flux in the Applications mode and GitOps section** | **Marketing**: Lens Plus, Aptakube, Radar, Kunobi all detect Argo *and* Flux ([Lens pricing](https://lenshq.io/pricing)). **No demand issue found** | M–L | **No** (no Flux code in `src/`) | Applications mode's health rules would need Flux Kustomization/HelmRelease semantics. A survey of Flux users' demand is still owed (v0.4.0 note) |
| **7. Argo Rollouts: see a rollout's step and promote/abort** | **Marketing**: Lens 2026.11.1, paid ([post](https://lenshq.io/blog/lens-release-2026-11/)); a Headlamp community plugin | M | **No**: Rollout CRs are browsable generically | Fits the Argo-first position; Lens charges for it. Promote/abort are mutating, so the confirm strip |
| **8. Context aliases and tags in the switcher** | **Marketing**: Aptakube 1.22.0–1.22.2, and an open ask for tag presets ([aptakube#586](https://github.com/aptakube/aptakube/issues/586)) | S | **Partial**: environment colours (`docs/engineering/cluster-switcher.md`) | A display name is a preference, not a credential. Kubeconfig names stay what the terminal handoff uses |
| **9. Restore last session's port-forwards** | **Marketing**: Aptakube 1.22.2 saved forwards and Start All | S | **Chose not to**: "a list of last session's forwards would be a history of sockets that no longer exist" (`docs/engineering/port-forward.md`) | Market pressure on a stated choice; listed for the owner, not proposed over the reason given |
| **10. An MCP server for AI agents** | **Marketing, very strong**: Lens (paid), KubeUI, Radar, PodSteer, Kunobi, KubeSpade. **Demand, thin**: [radar#1434](https://github.com/skyhook-io/radar/issues/1434) | L | **No** | **Tension with the stance, not a listed non-goal**: no telemetry and "no network connection other than to the clusters" — a local stdio server sends nothing itself, but it hands cluster data to whatever agent the user runs. KubeUI's v1.6.1 Host/Origin fix shows the attack surface. Would need an AOT-safe MCP implementation. Owner decision |

**Not proposed, with the reason:**

- **A cluster issues panel / Health page** — superseded by the Applications mode (BACKLOG.md).
- **Cost per namespace** (Lens Plus) — needs usage over time, which is Prometheus's job (non-goal).
- **A persistent event timeline** (Radar) — needs storage beyond the session; it is
  long-range history, the same non-goal as metrics history.
- **Auto-update and signed macOS builds** — pressure exists (FreeLens #552, KubeUI 1.8.0,
  every paid client), but both were decided by the owner (DIST-1 not planned, DIST-3).
- **Helm upgrade/rollback** — Aptakube shipped both; read-only stays the deliberate scope.
- **Topology / resource map** (Radar, Headlamp, PodSteer) — marketing only, no demand found,
  large; the application page's linked resources cover the "what belongs to this app" half.

## Sources

- Lens: [blog index](https://lenshq.io/blog) · [feed](https://lenshq.io/feed.xml) ·
  [2026.11.1](https://lenshq.io/blog/lens-release-2026-11/) · [pricing](https://lenshq.io/pricing) ·
  [#272](https://github.com/lensapp/lens/issues/272) · [#3771](https://github.com/lensapp/lens/issues/3771)
- FreeLens: [releases](https://github.com/freelensapp/freelens/releases) ·
  [#466](https://github.com/freelensapp/freelens/issues/466) · [#627](https://github.com/freelensapp/freelens/issues/627) ·
  [#524](https://github.com/freelensapp/freelens/issues/524) · [#1670](https://github.com/freelensapp/freelens/issues/1670) ·
  [#552](https://github.com/freelensapp/freelens/issues/552) · [#2376](https://github.com/freelensapp/freelens/issues/2376) ·
  [#2503](https://github.com/freelensapp/freelens/issues/2503) · [#2705](https://github.com/freelensapp/freelens/issues/2705) ·
  [#2880](https://github.com/freelensapp/freelens/issues/2880) · [#418](https://github.com/freelensapp/freelens/issues/418) ·
  [#687](https://github.com/freelensapp/freelens/issues/687)
- Aptakube: [releases](https://github.com/aptakube/aptakube/releases) · [pricing](https://aptakube.com/pricing) ·
  [#108](https://github.com/aptakube/aptakube/issues/108) · [#586](https://github.com/aptakube/aptakube/issues/586)
- Headlamp: [releases](https://github.com/kubernetes-sigs/headlamp/releases) ·
  [#2353](https://github.com/kubernetes-sigs/headlamp/issues/2353) · [#358](https://github.com/kubernetes-sigs/headlamp/issues/358) ·
  [#1716](https://github.com/kubernetes-sigs/headlamp/issues/1716) · [#1582](https://github.com/kubernetes-sigs/headlamp/issues/1582) ·
  [#5260](https://github.com/kubernetes-sigs/headlamp/issues/5260) · [#8007](https://github.com/kubernetes-sigs/headlamp/issues/8007) ·
  [#8128](https://github.com/kubernetes-sigs/headlamp/issues/8128) · [#3222](https://github.com/kubernetes-sigs/headlamp/issues/3222) ·
  [#6974](https://github.com/kubernetes-sigs/headlamp/issues/6974)
- k9s: [releases](https://github.com/derailed/k9s/releases) · [#1017](https://github.com/derailed/k9s/issues/1017) ·
  [#4080](https://github.com/derailed/k9s/issues/4080) · [#3793](https://github.com/derailed/k9s/issues/3793) ·
  [#3730](https://github.com/derailed/k9s/issues/3730) · [#4247](https://github.com/derailed/k9s/issues/4247) ·
  [#4293](https://github.com/derailed/k9s/issues/4293) · [#4375](https://github.com/derailed/k9s/issues/4375)
- KubeUI: [releases](https://github.com/IvanJosipovic/KubeUI/releases) ·
  [desktop csproj](https://github.com/IvanJosipovic/KubeUI/blob/main/src/KubeUI.Desktop/KubeUI.Desktop.csproj)
- Seabird: [releases](https://github.com/getseabird/seabird/releases)
- Radar: [repo](https://github.com/skyhook-io/radar) · [#1434](https://github.com/skyhook-io/radar/issues/1434) ·
  [#199](https://github.com/skyhook-io/radar/issues/199) ·
  [dev.to review](https://dev.to/alexandrev/radar-a-new-kubernetes-ide-worth-knowing-about-vs-openlens-freelens-4bma)
- Kunobi: [press release](https://www.wboc.com/online_features/press_releases/kunobi-desktop-launches-to-bring-operational-clarity-to-kubernetes-and-gitops-teams/article_28ccf5a6-e987-5a89-8441-321acf119bab.html) ·
  [Zondax blog](https://zondax.ch/blog/kunobi-a-local-kubernetes-desktop-for-gitops-and-multi-cluster-management) ·
  [Product Hunt](https://www.producthunt.com/products/kunobi)
- Reviews: [six desktop clients, 2026-10-29](https://alexandre-vazquez.com/kubernetes-desktop-clients-2026/) ·
  [k10s Show HN mirror](https://zeli.app/story/50009904)

# Feature deep dives, the full index

This is the index that `CLAUDE.md` carried until 2026-10-10 (#259), moved here verbatim with its
links rebased. `CLAUDE.md` keeps a shorter one, grouped by area, so that every session does not pay
for these descriptions. In the text below, "this file" means `CLAUDE.md`. A new page gets a line
here and a link in `CLAUDE.md`'s short index.

## Feature deep dives (docs/engineering/)

Each feature's design rules, and the incidents behind them, live in a page of their own under [`docs/engineering/`](./), so a session loads only the ones it touches. **Read the page for any feature you change before changing it**, and keep it current in the same PR — the same discipline as this file.

- [Connecting: credential plugins, proxies, failures and reconnect](connecting.md) — BuildClientSetupAsync as the one entry→client path, KubeconfigReader instead of the library's YAML loader (banned; it froze YamlDotNet), bare plugin commands found like a login shell would (never in the current directory), proxy-url on both transports, our own certificate check replacing the library's (host name, tls-server-name, every root of a CA bundle, skip-verify and plain http:// stated), impersonation headers (live-checked against the sandbox), tokenFile read on every build, plugin stderr redacted, the failure view (step, cause, facts, no credential ever a fact), RefreshCredentialsAsync's in-place swap and 401-as-expiry, kubeconfig folders with rescan-on-focus (a folder's context is never opened on its own), AppDataDirectory (owner-only, atomic writes, a random per-process fallback).
- [The Applications mode](applications-mode.md) — The first screen: apps (Argo or bare workloads) with health and a reason from Core's deterministic rules, per-namespace fallback under narrow RBAC, its own namespace picker (one or several, namespaces from the rows, starts no watch) and maintained header sort, the application page (findings with quoted evidence, pods, linked resources, timeline, what changed, embedded logs), the kubelet's one-run-per-container log rule, DemoData.Now.
- [Multi-pod logs (one workload, one stream)](multi-pod-logs.md) — WorkloadLogsTabViewModel: selector-resolved pods, per-pod tail budget, 50-stream cap, two-stage timestamp merge; and what both log panes say when a follow ends (LogStreamEnd reads the pod).
- [One click to logs from the row, and logs opened full-size](row-logs-and-maximized.md) — The row's logs icon (hover/selected, IsVisible style, Shift+click), Shift+L, the "Open logs maximized" preference read by OpenLogsForAsync, Esc restore; L3's logs from every list that names a pod (OpenNamedLogs, RowLogsGesture, stated "gone").
- [Reading a log: find, levels, clear, local time, remembered display](log-pane-reading.md) — Both log panes: search that finds (highlight, n of m, Enter/Shift+Enter) or filters, Levels with unleveled lines always shown, Clear that keeps the stream, local time with UTC one click away, display toggles in settings.json (never Previous), the default-container annotation, "not started" pods, one logs glyph, terminal colour codes removed (not drawn), bidi and zero-width characters shown as markers, a 1 MiB line cap; and the log-viewer pass — level keyword coloured not the line, earliest/structured/klog severity, stack traces inheriting, NonBacktracking regex and match case, `!word` exclusions, grep -C context, error jump, overview ruler, pinned highlights, JSON lines opened in place.
- [Log severity is three classes, not a brush binding](log-severity-classes.md) — Why severity is style classes and never a Foreground binding (the invisible-plain-line bug, twice).
- [Pod detail's Overview tab (conditions, tolerations, QoS, priority, probes)](pod-overview-tab.md) — Conditions/tolerations/QoS/probes tab: index 4, condition polarity, API-server probe defaults, signature-guarded rebuild.
- [Requests and limits are text on the Usage tab](requests-and-limits.md) — Usage tab's declared requests/limits: words not blanks, not gated on metrics.
- [ConfigMaps are shown, Secrets are masked](configmaps-and-secrets.md) — Env tab: ConfigMap refs resolve on open, Secret refs stay masked behind an eye, every key ref opens its object; a Secret's certificates (subject, SANs, expiry) are read without a Reveal, the key never; a copied Secret value is kept out of Windows clipboard history and cleared after a minute.
- [The sidebar is 224px and the reader can drag it](sidebar-width.md) — Absolute sidebar width, GridSplitter bounds, the SidebarWidthChanged write-back.
- [macOS has a real menu bar, and the app is called kubeNimbus](macos-menu-bar.md) — Application.Name, MacMenu.cs, platform-gated native menu built from CommandCatalog.
- [Accessible names come from the tooltip](accessible-names.md) — AutomationNames copies a control's tooltip into its UI Automation name (hand-written names win), list items name themselves through ToString, preferences cards from their labels, the harness walks the peer tree; and why the Applications page host has no IsVisible binding.
- [The theme toggle wrote a string nothing could read](theme-toggle-string.md) — Stringly-typed settings must write through the same helper that reads them.
- [Several namespaces at once](several-namespaces.md) — Both pickers' gestures (a click for one, the box or Ctrl/Cmd+click to add), the drawn check, SelectedNamespace as the first of SelectedNamespaces, one watch per namespace merged with a namespace-scoped Reset and a verdict that waits for every namespace, the fleet's whole-cluster read, TabSnapshot.Namespaces.
- [The cluster switcher and environment colours](cluster-switcher.md) — Ctrl/Cmd+P switcher (flat list, ranking) and environment colours (biased toward production).
- [CRD printer columns](crd-printer-columns.md) — additionalPrinterColumns: lazy CRD GET, JSONPath subset, ten fixed XAML slots, Tag-based column identity.
- [The resource grid is the reader's to re-cut](resource-grid-resize-sort.md) — Column drag + header sort: sorts VisibleRows never Rows, maintained sort, per-kind layout in workspace.json.
- [The Events list reads like `kubectl get events`](events-list.md) — Last seen (fallback chain, series before eventTime) / Type / Reason / Object / Count / Message, newest-first default with a remembered clear, both Event groups, why not printer slots.
- [Unhealthy only: the list's second narrowing](unhealthy-only.md) — Warn/error predicate over StatusHealth, per-Modified re-evaluation, kind gate, third empty state, list-scoped Ctrl+Z.
- [An Auto DataGrid column ratchets, and only one grid can afford it](datagrid-auto-columns.md) — Why the resource list has no Width=Auto columns (measured ratchet) and why Helm/Argo keep them.
- [Mutating workload actions (scale, rollout restart, delete, CronJob run/suspend)](workload-actions.md) — Scale / rollout restart / delete: merge patches, scale subresource, capability from discovery; a CronJob's run-now (kubectl's Job, server-named), suspend/resume, Open Job; every confirm names its cluster, production deletes always ask (fleet rows by their own cluster), scale says "from N to M" and warns about zero and ten-fold jumps.
- [Networking: Service, Ingress and NetworkPolicy panes, and the list columns](networking-detail.md) — Service pane joins selector-matched pods to EndpointSlice endpoints (slices by the `kubernetes.io/service-name` label, not owner refs; no verdict before both watches sync; the three degenerate shapes as three sentences); Ingress routes with a URL built from a validated host, never copied; NetworkPolicy rules in words with the empty selector meaning every pod; kubectl's list columns for Ingress/Endpoints/EndpointSlice/NetworkPolicy; Gateway API filed under Network by group.
- [Node operations (detail, cordon / uncordon, drain)](node-operations.md) — Node detail (System card, Events by kind+name, measured Usage vs allocatable), cordon/uncordon, drain: allocatable math, eviction plan table, partial-drain lifetime; pods-on-node and the drain are one field-selected watch, not a poll.
- [The exec terminal](exec-terminal.md) — SvcSystems.UI.Terminal over XTerm.NET: bytes in/out, stateful UTF-8 decoder, keyboard ownership, reverse-video defect; paste filtered, bracketed when asked and armed when multi-line into a shell that did not ask; shells by the pod's OS (powershell/cmd on Windows nodes), "no shell" as a verdict over every attempt, and the debug container (kubectl debug's ephemeral container: SYS_PTRACE with a Pod Security fallback, watched start, reuse; the default image fully qualified and pinned by index digest, updated by hand).
- [The machine's own terminal ("open a terminal on this cluster")](machine-terminal.md) — TerminalLauncher: one-key overlay kubeconfig, env-inheritance trap on wt.exe/open, per-platform launch, every candidate started by an absolute path (never found in the current directory).
- [The apply preview (server-side dry run)](apply-preview.md) — Server-side dry-run diff, TextDiff/LCS bounds, view modes, strict fieldValidation with pre-1.27 fallback.
- [Metrics (metrics.k8s.io)](metrics.md) — metrics.k8s.io via discovery, the one polled API, UsageHistory ring and Sparkline.
- [Helm release browsing (read-only)](helm-releases.md) — Reading Helm 3 release Secrets (base64+gzip) with no Helm binary, a decompression cap with unreadable releases listed and explained; synthetic sidebar kind.
- [Argo CD (GitOps in the navigator)](argo-cd.md) — Argo CD through the Kubernetes API only: sync is a top-level operation patch whose initiator is the user a SelfSubjectReview names, authorised by Kubernetes RBAC rather than Argo's roles, sync vs health pills; where "Open in Argo CD" goes (Argo's own namespace, never one a tenant names).
- [RBAC access review](rbac-access-review.md) — SelfSubjectRulesReview, binding provenance, who-can rule scan mirroring API-server matching.
- [Multi-cluster aggregated (fleet) views](fleet-views.md) — ClusterFleet/AsyncMerge: per-cluster descriptors, cluster-scoped Reset, cluster-qualified keys.
- [The status dot, and where it survives](status-dot.md) — The health dot survives only beside CRD printer columns; the Helm grid is separate.
- [The meter track was invisible, and the token was the reason](meter-track.md) — MeterTrackBrush: never reuse a hover token as a chart colour.
- [Sidebar labels come from the server's plural, and now actually do](sidebar-plural-labels.md) — Sidebar labels re-case the server's plural.

## Sections moved out of CLAUDE.md

On 2026-10-10 (#259) `CLAUDE.md` was cut from 163 KB to about 34 KB: it keeps each rule in a line or
two, and the full text of these sections moved here verbatim. Each section of `CLAUDE.md` links to
its page.

- [Mission and positioning](mission.md) — the market, KubeUI as the one true peer, the headline benchmark and what it measures.
- [Tech stack](tech-stack.md) — the packages and the must-nevers, with the reasons (YamlDotNet freeze, the TLS callback, EditorDefaults).
- [UI design rules](ui-rules.md) — rules 1–23 in full, with the kubeNimbus incident behind each.
- [The demo cluster](demo-cluster.md) — the sentinel context, no client, one dataset, and why the banner never goes away.
- [Settings, and what belongs in which file](settings.md) — settings.json against workspace.json, the five rules, the preferences page.
- [Workload detail and namespace navigation](workload-detail.md) — the workload pane, row keys, the namespace picker.
- [The command catalog](command-catalog.md) — one source for commands and gestures; the palette's network-backed rows.
- [The Advanced view](advanced-view.md) — the sidebar allow-list and why nothing else is gated on it.
- [The release train](release-train.md) — the train, its agents, and why each load-bearing rule exists.
- [The AOT watch/log implementation, discovery, apply, exec and port-forward](aot-watch-and-discovery.md) — the hand-rolled watch, exec-plugin failures, JSON depth and line caps, 401 refresh, batching, discovery and its cache.
- [Sandbox cluster bootstrap](sandbox-cluster.md) — the reachability gate, skipped-not-passed, the live tests' four rules, the sandbox scripts and manifests.
- [Verification workflow](verification.md) — the commands and their traps, view-model tests, the launch check, the MSVC recipe, kn-qa, the screenshot harness, the stress mode.
- [Repository layout](repository-layout.md) — the OSS Scanner image and the public docs table.

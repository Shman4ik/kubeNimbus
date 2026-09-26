# The Applications mode

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.

A second way into a cluster tab, beside the explorer (now the **Resources** mode, which is
unchanged). It exists for one job: "someone pinged me that service X is broken", or "I just
deployed, is everything green?". Before it, reaching a log line meant cluster tab,
namespace, sidebar kind, row, pod, logs. Now the first screen of a cluster tab is a list of
applications with their health and a one-line reason, and one Enter lands on a page with
the facts, the pods and the logs.

The product principle behind every rule below, stated by the owner: when there is no hurry
they use Claude Code with kubectl instead of this app. So this mode only wins under time
pressure — a glance, no typing, one click to the right log line, the same answer every time.
Anything that needs "thinking" is out of scope.

Code: `KubeNimbus.Core/Applications/` (the rules, UI-free), `ApplicationsViewModel`,
`ApplicationRowViewModel`, `ApplicationPageViewModel`, `ApplicationsView`,
`ApplicationPageView`, `Controls/TimelineStrip`, `Controls/RadioChip`, `Views/RowActionStrip`.

## What an application is

An **Argo CD Application** wherever one tracks the workload; otherwise **the workload
itself** — a Deployment, StatefulSet, DaemonSet or CronJob, and a standalone Job only while
it is running or has failed (a finished Job is history, not something that is up or down; a
CronJob's Jobs belong to the CronJob). ReplicaSets and pods are never rows: they are what
the rows are made of.

1. **Argo claims a workload by its own status first.** `status.resources` lists every object
   an Application manages by group, kind, namespace and name, and that is matched first. Only
   a workload no status lists falls back to Argo's two tracking marks — the
   `app.kubernetes.io/instance` label and the `argocd.argoproj.io/tracking-id` annotation,
   whose first field (up to the colon) must equal the Application's name, so `checkout`
   never claims what `checkout-v2` tracks. An annotation naming another app outranks the
   label.
2. **A tracked workload gets no row of its own.** Two rows for one thing would double every
   problem the list exists to point at.
3. **No Argo API server, and no history of our own.** Argo is read through its CRDs exactly as
   [argo-cd](argo-cd.md) reads them, and nothing from a watch is persisted: "what changed"
   comes only from what the cluster still holds.

## The rules (Core)

`ApplicationRules.Evaluate` is a pure function from an `ApplicationInput` (workloads, their
ReplicaSets, a CronJob's Jobs, their pods, an optional Argo Application, optional Events,
and *now*) to a status, a one-line reason and findings. Every rule is pinned by
`ApplicationRulesTests` with a negative case; `ApplicationSupportTests` pins the rest.

1. **Deterministic and read from status.** Container states and last terminations, pod
   conditions, controller conditions and rollout counters, Argo's own status. No model, no
   heuristics over log text. The same cluster produces the same page every time.
2. **A finding is a fact, not a diagnosis.** It has a severity, a one-sentence title, a
   detail, and **evidence that quotes the field it was read from** (`field: value`, or the
   Event with its reason and count). The page's subtitle says "Facts read from the cluster,
   not a diagnosis", and a page where nothing fired says so and adds that this is not proof
   users are fine.
3. **Status is the most urgent implication.** Each finding says which status it pushes the
   app toward; the app takes the earliest in `AppStatus` order — Degraded, Missing,
   SyncFailed, Unknown, Stalled, OutOfSync (the "Needs attention" group), then Progressing,
   Suspended, Healthy — folded with Argo's own health. Pod facts outrank a Healthy Argo
   verdict: Argo can call a Deployment Healthy while one of its pods crash-loops.
4. **Time is an argument.** Every rule takes *now*; nothing reads the clock. That is what
   makes the rules testable and what lets the demo cluster read its fixed dataset at
   `DemoData.Now` (see below). "Recent" is one hour (`RecentWindow`): an OOM kill an hour
   old on a container that is running again is history (Info, moves nothing); ten minutes
   old it is a Warning in the reason line, still without making the app unhealthy.
5. **A pod that became un-Ready less than two minutes ago is warming up** (`WarmUpGrace`),
   so a rollout's new pod does not flash the app Degraded.
6. **The reason line is the two most urgent distinct findings**, joined by " · " —
   "Crash-looping (exit 1) · 2 pods not created: namespace quota". A scheduler message is
   reduced to its shortage, "Insufficient" first, because the scheduler lists taints before
   the resource that is actually short.
7. **The list needs no Events.** Every rule reads object status; the page adds Warning Events
   as evidence (BackOff, FailedScheduling, FailedCreate, Unhealthy) and as timeline marks.
8. **A workload short of Ready pods that no rule explains still says so** (`unavailable`), so
   the list never calls an app healthy on the strength of silence.

The rule set for this pass: CrashLoopBackOff, OOMKilled (with the memory limit, or the fact
that there is none), ErrImagePull/ImagePullBackOff, CreateContainerConfigError /
CreateContainerError, Unschedulable, running-but-not-Ready, ReplicaFailure (quota),
ProgressDeadlineExceeded, rollout in progress and the old version serving alone, StatefulSet
and DaemonSet revision lag, a failed Job (BackoffLimitExceeded, DeadlineExceeded) and a
CronJob whose last Job failed, a suspended CronJob, a paused rollout, Argo operation
Failed/Error, ComparisonError, OutOfSync (not attention while a sync is running), Missing,
and Argo's own Degraded/Unknown when nothing read from the workloads explains it.

## Data honesty (Kubernetes semantics)

1. **The kubelet keeps one terminated container per container name.** Its
   `validateContainerLogStatus` (`pkg/kubelet/kubelet_pods.go`) serves `lastState.terminated`
   for `previous=true`, and for `previous=false` the running container, else the terminated
   one, else `lastState.terminated` again. So for a container **waiting** in CrashLoopBackOff
   `logs` and `logs --previous` return the *same* run. `PodLogRuns.For` follows that code:
   such a container has one "Last run", and the page offers "Run before" only where a
   distinct earlier run exists (a container running, or itself terminated, with a last
   termination). Both branches also need a non-empty `containerID`, the kubelet's signal that
   it still has the container. The prototype's "Run before" tab on a waiting pod is exactly
   the mistake this avoids; `PodLogRunsTests` pins it and a mutation offering two runs turns
   it red.
2. **Restarts cannot be dated past the last one.** Per container only `lastState.terminated`
   is known. The timeline draws one termination per container, never one per restart, and
   the Restarts column's tooltip says so. The column is red only when the last restart ended
   within the hour.
3. **Events live about an hour.** The page says "No Warning events in the last hour", never
   "no events", and the timeline window is bounded by the same hour.
4. **"No logs" is a state with a reason** (`PodLogRuns.NoLogsReason`): not scheduled (with the
   scheduler's message), image cannot be pulled, container could not be created, being
   created, init container not finished, phase. Never an empty panel.

## The list

1. **It owns its own watches.** One list+watch per kind (Deployment, StatefulSet, DaemonSet,
   CronJob, Job, ReplicaSet, Pod, and Argo's Application when discovery serves it), through
   the same `WatchResourceAsync` informer every other list uses, started the first time the
   mode is shown for a tab and stopped only when the tab closes. The Resources list's watch
   is never touched, so switching modes restarts nothing in either direction.
2. **Narrow RBAC is the expected case.** Each kind is tried across the cluster. A 403 (on the
   list, or on the watch where list is allowed and watch is not) moves it to one watch per
   namespace from `ApplicationScope.CandidateNamespaces`: the kubeconfig context's namespace,
   the selected namespace, the recent namespaces, and every Argo destination (plus `argocd`
   for Applications). New Argo destinations extend the fallback as they appear. **The list
   states what it covers and what was refused**, in the API server's words — a silently
   partial list reads as "all is well" exactly where it is not looking. With nowhere to fall
   back to, it says what to do.
3. **A Reset clears only its own scope.** One namespace's watch relisting after a 410 must not
   blank the others; `A_reset_in_one_namespace_keeps_the_rows_of_the_others` pins it.
4. **No verdict before the data (UI rule 18).** "No applications found" is said only after every
   read has delivered `Synced` or been refused. Rows appear as soon as they can be built, and
   while some kinds are still arriving the title line says "Still reading Pods…" instead of a
   spinner over rows that are already useful.
5. **Rebuilds are coalesced and off the UI thread.** Watch events mark the store dirty; a
   200 ms timer takes one snapshot and assesses it on the pool (`SnapshotIndex` keeps it
   linear in pods rather than apps × pods); a 30 s tick re-reads *now*. Rows are updated in
   place by key, and the visible collection is synced by moves, never a Clear, so the
   selection and the scroll position survive every event.
6. **Search matches name and namespace, never status** (UI rule 13's reason: "Degraded" would
   match whatever is broken, and that question has a chip). The chips are one-of: a
   `RadioChip` is a ToggleButton a click turns on and never off, because a plain one over a
   two-way binding unchecked itself while the list stayed narrowed — the binding does not
   re-read a source that refused the write, which only the harness's pointer click caught.
   Workloads whose every namespace is `kube-*` are hidden behind a chip stating their count.
   Filter-matched-nothing is its own state, with the way back.
7. **No `Width="Auto"` columns** ([datagrid-auto-columns](datagrid-auto-columns.md)): the list
   is a `ListBox` with fixed-width columns and one star column, header and rows sharing the
   same `ColumnDefinitions`. A group caption rides in the first row of its group, the
   switcher's pattern, and the row body is what lights up.
8. **Last deploy** is Argo's newest `status.history` entry (a short SHA, or `chart 62.3.0` for a
   Helm chart source), else the newest ReplicaSet's creation with its revision; accent when
   under an hour, which is also the "Deployed < 1 h" chip. A StatefulSet or DaemonSet outside
   Argo shows "—": its ControllerRevisions are not read for the list.

## The page (layout A)

1. **It reads the list's snapshot.** Every rebuild re-reads the open page's application, so the
   page is live with no watch of its own. What it adds, once per namespace when it opens:
   Warning Events (evidence and timeline), the Services / Ingresses / HTTPRoutes / HPAs /
   PDBs its pods are wired to, and `argocd-cm`. Each failure is stated where its answer
   would have been.
2. **Linked resources come from spec references, not labels** (`LinkedResources.Find`): a
   Service whose selector matches the pod template's labels, an Ingress or HTTPRoute backed by
   one of those Services, ConfigMaps and Secrets the pod spec mounts or reads, the HPA by
   `scaleTargetRef`, the PDB by selector, PVCs from volumes. A click switches to Resources
   with that object selected (`ClusterTabViewModel.RevealInResources`: kind, namespace, then
   the row as soon as the watch delivers it).
3. **Logs are the aggregated pane, embedded — no third pipeline.** One
   `WorkloadLogsTabViewModel` over every selector-bearing workload of the app in its first
   namespace (a CronJob contributes its latest Job), with `WorkloadLogsOptions`: no pod strip
   (the page's Pods list is the selector), a focus pod, the run before, and Errors only over
   the same severity classes the lines are coloured with. A crash-looping pod is preselected
   on its last run, and the log ends with "Container worker exited with code 1 (Error) at
   08:54:36" (`Footer`, drawn after the last line inside the same scroll). Selecting a pod
   re-includes buffered lines; every pod keeps streaming.
4. **The timeline** (`ApplicationTimeline`) draws deploys (Argo `deployedAt` with the
   revision, and ReplicaSet creation with its revision — merged into one mark when the
   ReplicaSet follows the sync within a minute, since they are one deploy), container
   terminations and Warning events, over the smallest of 15, 30, 45 or 60 minutes that holds
   every mark from the last hour. The caption names the deploy before the window. Hovering a
   mark shows its detail; the kind is always in words as well as colour.
5. **What changed** (`PodTemplateDiff`): the current against the previous ReplicaSet, matched by
   `deployment.kubernetes.io/revision` — image, resources, command/args, env names added,
   removed or changed and where they come from, envFrom, mounted ConfigMaps/Secrets/PVCs, the
   service account, and a rollout restart. **No env value and nothing from a Secret is ever
   printed**: two different literals say "value changed". `No_env_value_is_ever_printed` pins
   it and a mutation printing the value turns it red. A compare link comes from the
   Application's `repoURL` and its two latest history revisions for github.com, GitLab
   (`/-/compare/a...b`), Azure DevOps (`branchCompare?baseVersion=GC<a>&targetVersion=GC<b>`,
   https and ssh forms) and Bitbucket Cloud (`branches/compare/<newer>..<older>#diff`); an
   unknown host shows both SHAs, a chart source shows the chart versions, and nothing else is
   guessed. It opens in the system browser.
6. **Actions reuse what exists.** Restart and Sync arm the very confirm strip the Resources list
   uses (`RowActionStrip`, UI rule 17). Edit YAML opens the existing editor in the Resources
   dock — and when the Argo app has `selfHeal: true`, the strip first says Argo will revert a
   manual edit and names the Git path. "Open in Argo CD" exists only when `argocd-cm`
   `data.url` was readable (hidden in the demo cluster, which has no Argo UI to open).
7. **Esc returns to the list with the same row selected**, from anywhere on the page except a
   text box, whose Esc is its own.

## The mode switch

A segmented `ListBox` in the command bar, right of the sidebar toggle and left of the cluster
tabs, bound by index (one value, no toggling command — UI rule 8b) and carrying the `User`
decoration role (UI rules 12 and 15). The sidebar toggle is disabled, not hidden, in the
Applications mode: a vanishing control would slide the mode switch under the pointer. The
mode is session state in `workspace.json` (`ShellMode`), default Applications; both modes'
views stay alive, one hidden. Ctrl/Cmd+Shift+A and +R, the palette (the mode not on screen),
the macOS View menu and the F1 sheet all come from the catalog.

## The demo cluster

Every state above plays out on the demo cluster through production code (demo rules 1–6).
`LoadDemo` pours the dataset into the same store the watches fill, and the rules read it at
`DemoData.Now` (2026-07-30 08:56 UTC): the dataset's timestamps are fixed, so relative times
against the real clock would drift further from the story every day, and the screenshots are
deterministic for the same reason. The Resources mode's ages still read the real clock.

The dataset tells one story per state: `checkout` (Argo, crash-looping after deploy 8f3c1d9
removed `PAYMENT_GATEWAY_URL`, two replicas refused by the namespace quota, the old version
serving alone), `fraud-detector` (no pod schedulable, no logs), `settlement-batch` (sync
failed, Missing), `risk-scoring` (ComparisonError), `payment-service-report-generator`
(stalled rollout, no Argo), `ledger-api` (OutOfSync, auto-sync off), `nightly-reconcile`
(last Job failed), `notification-dispatcher` (rolling out), `quarterly-report` (suspended),
`monitoring-stack` (Helm chart source), `redis-cache` (quiet) and `kube-proxy` (a `kube-*`
DaemonSet).

## Verification

`ApplicationRulesTests` and `ApplicationSupportTests` (Core) pin every rule with a negative
case; `ApplicationsListTests` and `ShellModeTests` (App) pin order, groups, chips, search,
watch-apply keeping row identity, per-scope Reset, loading, RBAC statements, Esc back to the
row and mode persistence. The screenshot harness renders `applications-list*` and
`applications-page-*` in both themes, and `ux-applications-keys` drives arrows, Enter, Esc,
`/`, Ctrl/Cmd+F, the chips by pointer and the mode switch by pointer against the rendered
window.

Not verified here: a real cluster (Argo CD, SSO, narrow RBAC — the 403 fallback is pinned
against the fixture seam, not an API server), the win-x64 publish, macOS.

## Follow-ups

The Argo-style resource tree (layout B); an optional Argo CD API connection for a live-vs-Git
diff; a cross-cluster list with a Cluster column; ControllerRevisions for a StatefulSet's
last deploy and "what changed".

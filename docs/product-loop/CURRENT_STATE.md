# kubeNimbus — current state

What the app does today, what is partial, what is broken, and what is missing. The
release train's SURVEY phase updates this incrementally: it changes only the sections
the scan contradicts, and it traces the code before calling anything missing (a gap in
the docs is not a gap in the product).

Last surveyed: 2026-09-22, train v0.4.0, at `d1b8663` (v0.3.3 plus the train machinery).

## Implemented

- **Connecting.** Kubeconfig chain (`$KUBECONFIG` + `~/.kube/config` + picked paths),
  exec-plugin auth, parallel connect, aggregated discovery (v2/v2beta1) with a six-hour
  disk cache, the initial Pods watch started before discovery finishes, restored tabs
  connecting in parallel. Multi-cluster tabs, a cluster switcher (Ctrl/Cmd+P) with
  environment colours, and a built-in demo cluster that needs no kubeconfig.
- **Browsing.** Discovery-driven sidebar (seven sections, filterable, collapsible, a
  Recent section, CRDs first-class), list+watch with Reset/Synced frames, per-list
  search (Ctrl/Cmd+F), sortable and resizable columns kept per kind, CRD
  `additionalPrinterColumns`, fleet (all-clusters) views, a namespace picker with
  recents (Ctrl/Cmd+Shift+N), k9s-style row keys (L, P, S, F, E, R, Delete, /).
- **Pods.** Pod detail with Logs (follow, previous, filter, wrap, timestamps, copy,
  download, severity colouring), Env (ConfigMap refs resolved, Secret refs masked),
  Events, Usage (metrics.k8s.io, sparklines, requests and limits as text) and Overview
  (conditions, tolerations, QoS, priority, probes). Owner navigation. Exec in a real VT
  terminal. Port-forward.
- **Workloads.** Workload detail for Deployments/StatefulSets/DaemonSets (replicas,
  progress, conditions, events, live pod list). Multi-pod logs for any selector-bearing
  workload. Scale, rollout restart and delete on a confirm strip.
- **Nodes.** Node detail (allocatable vs requested, conditions, taints, pods on node),
  cordon/uncordon, drain with an eviction plan.
- **Editing.** YAML editor with server-side apply, strict field validation, a
  server-side dry-run diff preview, conflict handling with force-apply.
- **Ecosystem.** Helm release browsing (read-only, no Helm binary), Argo CD dashboard
  with sync/refresh, RBAC access review and who-can.
- **Shell.** Command palette (Ctrl/Cmd+K), F1 cheat sheet generated from the command
  catalog, preferences overlay, macOS native menu, "open a terminal on this cluster".
- **Shipping.** NativeAOT on four RIDs, launch check on every published binary,
  MSI/.dmg/.deb/AppImage/MSIX packaging.

## Partial

- **Events list.** The generic Events kind renders through the ordinary list: the Name
  column shows the Event object's own name (`checkout-worker-5d8f7b9c4-qz9pl.17f2a1`),
  Status shows `Reason ×count`, there is no message, no involved-object column and no
  "last seen", and Age is the event's creation time (blank in the fixture render). What
  happened and to what is therefore one double-click per event away. (Friction walk,
  `cluster-tab-events-list`.)
- **Workload detail Events tab** renders as a DataGrid whose Type and Count columns clip
  (`Norma`, `Cour`) in a larger font than the rest of the pane, while pod detail's Events
  tab renders the same data as readable cards. (`ux-workload-events`.)
- **Pod detail Events** print an absolute timestamp with offset
  (`07/20/2026 04:58:00 +00:00`) where every list uses relative age.
- **Logs.** Follow does not reconnect after a dropped stream (FEAT-34); the single-pod
  pane fetches a hardcoded `tailLines: 200` with no since/tail control (FEAT-31); no
  structured JSON rendering (FEAT-32).
- **Namespace-scoped list columns.** With one namespace selected, the Namespace column
  repeats the same value on every row and costs Name its width (pod names truncate at
  1280px). Cluster-scoped kinds (Nodes, PVs) render an always-empty Namespace column and
  a disabled namespace picker (ENG-23).

## Broken

Nothing user-visible found broken in this scan. CI on `main` is green (run
35786789694).

## Missing (traced in code)

- A cluster-wide "what is unhealthy" view (FEAT-24 / FEAT-9): answering it still means
  visiting Pods, Deployments, Nodes and Events one by one, per namespace.
- Rollout / revision history for Deployments (ReplicaSet revisions), and a way to see
  "what changed".
- `SelfSubjectReview` ("who am I on this cluster", FEAT-56).
- Job/CronJob trigger-now and suspend (FEAT-8); multi-select and bulk actions (FEAT-6).
- Creating a resource from pasted YAML (FEAT-12).
- Signed binaries, auto-update, winget/Homebrew (DIST-1..3) — need a human.

## Environment notes (cloud session, 2026-09-22)

- `dockerd` starts, but Docker Hub's blob CDN answers 403, so `scripts/sandbox-up.sh`
  cannot pull k3s. The k3s **binary** and its airgap image tarball download from GitHub
  and `k3s server` comes up natively — but `runc` cannot start containers in this VM
  ("can't get final child's PID from pipe"), so the result is an **API-server-only**
  cluster: discovery, list/watch, CRDs, RBAC, apply/dry-run, patches and evictions of
  never-running pods are real; logs, exec, port-forward and metrics are not.

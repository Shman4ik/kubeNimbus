# kubeNimbus — current state

What the app does today, what is partial, what is broken, and what is missing. The
release train's SURVEY phase updates this incrementally: it changes only the sections
the scan contradicts, and it traces the code before calling anything missing (a gap in
the docs is not a gap in the product).

Last surveyed: 2026-09-22, train v0.4.0, at `d1b8663` (v0.3.3 plus the train machinery).
Reconciled on 2026-10-05 against v0.6.0 (`b72e9b8`): the sections below were corrected
for what shipped in 0.4.0 to 0.6.0. That was a reading of the changelog and the backlog,
not a fresh scan, so the next train's SURVEY still starts from the code.

## Implemented

- **Applications mode (the first screen).** Every Argo CD Application and every workload
  no Application tracks, with a health verdict and a one-line reason from deterministic
  rules, a per-namespace fallback under narrow RBAC, and an application page with
  findings, pods, linked resources, a timeline, what changed and embedded logs. The
  explorer below is the Resources mode beside it.
- **Connecting.** Kubeconfig chain (`$KUBECONFIG` + `~/.kube/config` + picked paths),
  exec-plugin auth, parallel connect, aggregated discovery (v2/v2beta1) with a six-hour
  disk cache, the initial Pods watch started before discovery finishes, restored tabs
  connecting in parallel. Multi-cluster tabs, a cluster switcher (Ctrl/Cmd+P) with
  environment colours, and a built-in demo cluster that needs no kubeconfig.
- **Browsing.** Discovery-driven sidebar (seven sections, filterable, collapsible, a
  Recent section, CRDs first-class), list+watch with Reset/Synced frames, per-list
  search (Ctrl/Cmd+F), sortable and resizable columns kept per kind, CRD
  `additionalPrinterColumns`, fleet (all-clusters) views, a namespace picker with
  recents (Ctrl/Cmd+Shift+N) that also opens a namespace by name when RBAC refuses
  `list namespaces`, k9s-style row keys (L, P, S, F, E, R, Delete, /), an Unhealthy
  only narrowing on every list, and an Events list that reads like `kubectl get events`.
- **Pods.** Pod detail with Logs (follow, previous, filter, wrap, timestamps, copy,
  download, severity colouring), Env (ConfigMap refs resolved, Secret refs masked),
  Events, Usage (metrics.k8s.io, sparklines, requests and limits as text) and Overview
  (conditions, tolerations, QoS, priority, probes). Logs can be searched or filtered,
  narrowed by level, read in local time or UTC, and fetched over a tail or since range;
  a follow that ends says why. Logs are one key (or one click on the row) from any list
  that names a pod, and the palette lists them. Owner navigation. Exec in a real VT
  terminal, with powershell/cmd on Windows nodes and a debug container for images
  with no shell. Port-forward.
- **Workloads.** Workload detail for Deployments/StatefulSets/DaemonSets (replicas,
  progress, conditions, events, live pod list). Multi-pod logs for any selector-bearing
  workload. Scale, rollout restart and delete on a confirm strip; a CronJob can be run
  now, suspended and resumed.
- **Networking.** Service, Ingress and NetworkPolicy panes, kubectl's list columns for
  the networking kinds, Gateway API filed under Network.
- **Nodes.** Node detail (allocatable vs requested, conditions, taints, pods on node),
  cordon/uncordon, drain with an eviction plan.
- **Editing.** YAML editor with server-side apply, strict field validation, a
  server-side dry-run diff preview, conflict handling with force-apply.
- **Ecosystem.** Helm release browsing (read-only, no Helm binary), Argo CD dashboard
  with sync/refresh, RBAC access review and who-can.
- **Shell.** Command palette (Ctrl/Cmd+K), F1 cheat sheet generated from the command
  catalog, preferences overlay (four tabs, interface and code fonts), macOS native menu, "open a terminal on this cluster".
- **Shipping.** NativeAOT on four RIDs, launch check on every published binary,
  .dmg/.deb/AppImage/MSIX packaging and a portable Windows zip. Listed in the
  Microsoft Store.
- **Connection failures.** A failed connect is a view that names the step, the cause
  and the facts; exec plugins found like a login shell would, `proxy-url`, kubeconfig
  folders, and a 401 treated as expired credentials and refreshed in place.

## Partial

- **Workload detail Events tab** renders as a DataGrid whose Type and Count columns clip
  (`Norma`, `Cour`) in a larger font than the rest of the pane, while pod detail's Events
  tab renders the same data as readable cards. (`ux-workload-events`; the train's T4, not
  yet built.)
- **Pod detail Events** print an absolute timestamp with offset where every list uses
  relative age. (Also T4.)
- **Namespace column with one namespace selected** repeats the same value on every row and
  costs Name its width. The owner's chosen fix is a multi-select namespace picker
  (FEAT-67). Cluster-scoped kinds no longer show the column (ENG-23).
- **The confirm strip** reads as a raw form (FEAT-77).

## Broken

Nothing user-visible known broken. One correctness defect is open: `LabelSelector.Parse`
drops a `matchExpressions` entry it cannot read, which widens a selector instead of
narrowing it (ENG-50).

## Missing (traced in code)

- `SelfSubjectReview` ("who am I on this cluster", FEAT-56, P3).
- Multi-select and bulk actions (FEAT-6).
- Creating a resource from pasted YAML or a local file (FEAT-12).
- A port-forward manager that outlives its tab (FEAT-7).
- Signed binaries, auto-update, Homebrew and AUR (DIST-1, DIST-3, the rest of DIST-2) —
  need a human.

The 2026-09-22 list also named a cluster-wide "what is unhealthy" view and "what changed";
the Applications mode and Unhealthy only now answer the first, and the application page's
timeline and pod-template diff the second.

## Environment notes (cloud session, 2026-09-22)

- `dockerd` starts, but Docker Hub's blob CDN answers 403, so `scripts/sandbox-up.sh`
  cannot pull k3s. The k3s **binary** and its airgap image tarball download from GitHub
  and `k3s server` comes up natively — but `runc` cannot start containers in this VM
  ("can't get final child's PID from pipe"), so the result is an **API-server-only**
  cluster: discovery, list/watch, CRDs, RBAC, apply/dry-run, patches and evictions of
  never-running pods are real; logs, exec, port-forward and metrics are not.

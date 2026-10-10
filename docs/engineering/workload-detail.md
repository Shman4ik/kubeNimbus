# Workload detail and namespace navigation

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "Workload detail and namespace navigation" section keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

## Workload detail and namespace navigation

Double-click opens Deployments, StatefulSets, DaemonSets and batch Jobs in
`WorkloadDetailTabViewModel`. The pane shows replica counts (a Job's completions,
running and failed against its backoff limit), controller progress,
conditions, events and a live pod list. The pod watch uses the workload selector,
including match expressions. Closing the pane cancels its requests and watch.
A header click sorts the pod list (the same in node detail; see
[resource-grid-resize-sort](resource-grid-resize-sort.md), "The inspector grids sort too").
The workload status follows its list row; Refresh also reads the object directly,
and tells the list so a row it heals or breaks is re-filtered (ENG-33). The pod
grids of this pane and node detail sync their selection from code-behind
(`Views/GridSelectionSync`), never a two-way `SelectedItem`: DataGrid writes a null
back as the inspector switches tabs, which lost the selection (ENG-43).

Double-click and Enter open a selected pod. L opens its logs and Shift+L opens
them maximized, through the resource list's own open-logs path (see
[row-logs-and-maximized](row-logs-and-maximized.md), "L3").
S opens its shell.
E opens the workload YAML. The Actions menu offers scale and rollout restart
through the existing confirmation strip. Each action retains the original row,
descriptor and cluster, even after the main list changes. Owner navigation uses
the same detail routing. Events use the object UID when available.

The namespace picker filters on input and commits only on Enter or a row click.
It chooses several namespaces too: a row's box, Ctrl/Cmd+click or Space adds one and keeps
it open, and the list then runs one watch per namespace — see
[several-namespaces](several-namespaces.md).
Ctrl/Cmd+Shift+N opens it and focuses its search field. Escape closes it.
Five recent namespaces appear first after All namespaces. `workspace.json`
persists them per kubeconfig path and context. Deleted namespaces stay out of
its results. The existing palette entries still work.
Until the cluster's namespaces have been listed — and for good when RBAC refuses
`list namespaces`, the expected case on a shared cluster — the picker also keeps
the recent namespaces in its list and offers the typed name as a first row marked
"Open by name", so Enter opens it. Only a valid RFC 1123 label is offered. Once the
list has been read, a typed name is not offered: a name missing from a list that
was read has been deleted, and opening it looks like a broken watch.
`ClusterTabNamespacePickerTests` pins both sides.

One kubeconfig file that exists and does not parse costs that file, not the chain:
`Kubeconfig.LoadContextsAsync` records it in its `failures` list and loads the rest,
and the status line names the file and the parser's first line. A single explicit
file passed with no failure list still throws.

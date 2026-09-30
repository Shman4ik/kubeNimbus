# Multi-cluster aggregated (fleet) views

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`ClusterFleet.cs` + `AsyncMerge.cs` (Core) fan one resource query out across every
connected cluster and interleave the results. Four things are load-bearing:

- **Each cluster resolves its own descriptor.** `ClusterFleet.ResolveAsync` looks
  the requested `(group, kind)` up in *that member's* discovery catalog — the
  same CRD kind is routinely served at `v1beta1` on one cluster and `v1` on
  another, and reusing one cluster's descriptor elsewhere would query a path
  that doesn't exist there.
- **A `Reset` is scoped to the cluster that sent it.** Watches relist on 410
  Gone, and `ClusterTabViewModel.ApplyFleet` therefore clears only that
  cluster's rows. Treating a fleet Reset like a single-cluster one would wipe
  four healthy clusters because the fifth reconnected. The clearing is one pass and one
  notification (`Rows.ReplaceAll`), not a `Rows.Remove` per row: a member relisting 5,000
  pods was a linear search and two notifications per row (the stress mode's `fleet-relist`).
- **Partial is normal, and is always stated.** A kind missing from a cluster, or
  a cluster that can't be reached, never fails the view: the header shows
  "n of m clusters serve X" and unreachable members surface in the inline
  warning. `AsyncMerge` reports per-source failures and keeps the rest flowing
  for the same reason.
- **Rows, tab keys and metrics keys are all cluster-qualified.** The same
  namespace/name exists on every cluster in a fleet, so
  `ResourceRowViewModel.KeyFor`, `PodDetailTabViewModel.KeyFor` and
  `YamlEditorTabViewModel.KeyFor` all fold the cluster name in — otherwise the
  second cluster's pod silently reuses the first one's row and inspector tab.
  Opening a row uses **its own** cluster's client and descriptor
  (`ClusterTabViewModel.ClientFor`/`DescriptorFor`), or a YAML apply would land
  on the wrong cluster; owner-chain navigation stays pinned to the same cluster.

Why a channel-based merge (`AsyncMerge`): the sources are long-lived watch
streams that each block indefinitely, so a sequential `await foreach` over them
would starve every cluster but the first. `AsyncMergeTests` pins exactly that,
plus per-source failure isolation and teardown-on-abandon.

UI-wise this is a **toggle on the existing list**, not a new view: the sidebar,
namespace picker, filter and inspector are all unchanged, the list gains a
Cluster column (shown/hidden from code-behind, same DataGridColumn reason as the
usage columns — 120px and regular weight since ENG-6: at 150 semibold it was the widest
fixed column and the heaviest text on screen for a value repeated down every row, and the
ten columns overflowed 1280px until Age's header read "Ag"; with Age's minimum raised to
its width the squeeze falls on CPU and Memory and the list fits 1280px, and narrower than
that the grid scrolls sideways, which `LayoutChecks.GridReachesLastColumn` asserts), and
the toggle only appears with more than one cluster connected
— a fleet of one is the tab you are already looking at (UI rule 1). The command
palette carries the same toggle. `MainWindowViewModel` owns the member list and
makes cluster names unique (two tabs on one context would otherwise merge into
one apparent cluster) and re-fans active aggregated watches when a tab opens or
closes, so no tab keeps watching a disposed client.

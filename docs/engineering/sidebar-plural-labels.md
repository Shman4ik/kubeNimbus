# Sidebar labels come from the server's plural, and now actually do

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`SidebarKindViewModel.Pluralize` claimed to label rows from the server's own plural. It
did not: it used `descriptor.Plural` only to test equality with the Kind and otherwise
appended `"s"` (or `"es"` after s/x), so `NetworkPolicy` rendered as **`NetworkPolicys`**.
Every Kind ending consonant+y was affected, which on a CRD-heavy cluster is a lot of them.
It reads the plural now and re-cases it against the Kind's own capitalisation, so
`NetworkPolicy` + `networkpolicies` gives `NetworkPolicies`. A descriptor with no plural at
all (the hand-built statics, fixtures) keeps the Kind as written.

**A plural that parts from the Kind before its last letter shows the Kind instead.** The
first version fell back to the server's string only when the two shared no prefix at all,
and Kubernetes itself breaks that: `metrics.k8s.io` serves Kind `NodeMetrics` as resource
`nodes` and `PodMetrics` as `pods`. Re-casing `nodes` against `NodeMetrics` gave `Nodes`,
so the Cluster section carried two identical "Nodes" rows, one of them the metrics API.
One letter of slack is exactly what `y`→`ies` needs (`Policy`/`policies`), and anything
earlier means the resource name is not a plural of this Kind. `SidebarAdvancedSectionTests`
pins both halves.

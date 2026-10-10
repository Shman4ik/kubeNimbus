# The Advanced view

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "The Advanced view" section keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

## The Advanced view

One global persisted boolean, default **on**, mirrored onto every cluster tab. It
governs exactly one thing: which kinds the sidebar lists. Off keeps a curated allow-list
of about 20 everyday built-ins (`SidebarGrouping.BasicViewKinds`, keyed by group *and*
Kind so a CRD called `Deployment` is not mistaken for the built-in): Pods, Deployments,
StatefulSets, DaemonSets, ReplicaSets, Jobs, CronJobs, HPAs; Services, Ingresses,
NetworkPolicies; ConfigMaps, Secrets, core Events, ServiceAccounts, ResourceQuotas, PDBs;
PVCs, PVs, StorageClasses; Nodes and Namespaces. Everything else in the discovery-driven
sections (`SidebarGrouping.IsCuratedSection`) waits for the switch, and a section is
hidden only when none of its kinds survives, which is what happens to CRDs and to all of
Cluster except Nodes and Namespaces. Argo, Helm and Recent are never curated.

It used to hide whole sections, Cluster and CRDs, and that was too coarse in both
directions. It took Nodes along with the API machinery, so node detail, cordon and drain
had no route in the basic view. And it left the machinery filed in the *other* sections in
place, so a real 1.31–1.33 cluster still opened on about 35 rows: ControllerRevisions,
PodTemplates and ReplicationControllers in Workloads; Endpoints (deprecated in 1.33),
EndpointSlices, IngressClasses, IPAddresses and ServiceCIDRs in Network; a second "Events"
row (`events.k8s.io`, the same objects as core Events) and LimitRanges in Config; and the
CSI plumbing in Storage. The allow-list is an allow-list rather than a deny-list so that a
built-in a future Kubernetes adds lands in the advanced view until someone decides it is
everyday. Two close calls were decided on purpose: ReplicaSets stay (owner navigation lands
on them), and PersistentVolumes stay beside the claims that bind them. The gate is derived
per kind in `ApplySidebarFilter`, which `ApplySidebarChrome` ends by calling.

It keeps its place — an icon-only `ToggleButton Classes="chip"` docked right of the
sidebar's filter box, the same spot as pgNimbus's tree-options button — because people who
use both should find it where they left it. Its glyph is **eye-plus**
(`EyePlusIconGeometry`), "show more". It used to be `TuneIconGeometry`, the sliders, which
is also the Config section's header icon and is pgNimbus's glyph for an options *menu*: on a
direct toggle it read as "settings" and sat one row above an identical icon that meant
something else.

**It used to hide a great deal more, and removing that is the point of the current
shape.** Off took the CPU/Memory columns and their sparklines, pod detail's Usage tab,
the fleet toggle, both log toolbars' Wrap/Copy/Download, YAML force-apply, the Helm and
RBAC palette entries, and a CRD's own `priority: 1` printer columns (the switch acting
as kubectl's `-o wide`). So a complaint about a crowded *sidebar* was answered by hiding
controls all over the *content area*, where nothing was crowded — and what it hid there
was mostly what somebody had deliberately gone looking for: the cluster's own usage
numbers, the one gesture that gets a log into a bug report, the only in-app resolution
to an apply conflict, and columns a CRD's author had declared. All of those are
unconditional now.

Five things are load-bearing:

- **Nothing outside the sidebar may be gated on it again.** That is the rule the rework
  exists to establish, and `SidebarAdvancedSectionTests` pins the negative half of it
  (the usage columns and the fleet toggle survive the switch going off) precisely so a
  re-gating shows up as a red test rather than as a control someone cannot find.
- **It is a display switch and nothing else.** Flipping it must never restart a watch,
  refetch anything, or lose list/inspector state.
- **Nothing it hides becomes unreachable, and that is what makes hiding safe.** The
  sidebar's own filter reaches into a hidden section — a query is a deliberate search
  for one thing, and a match that then renders nothing is the "worse than no match"
  failure the palette's rules already name — and every kind keeps its Ctrl/Cmd+K entry
  whatever the switch says. `ApplySidebarChrome` and `ApplySidebarFilter` both derive
  the gate, because the filter is one of its inputs.
- **The shell owns it; tabs carry a mirror.** `MainWindowViewModel` persists it
  (`AppSettings.IsAdvancedView`) and broadcasts; `ClusterTabViewModel` holds the copy the
  sidebar binds. `InspectorTabViewModelBase` no longer carries one at all — with the
  content area ungated, nothing read it, and a mirror nothing reads is the "setting
  nothing reads" this file forbids.
- **The kind-count badge stays on the switch.** "How much is hiding in here?" is a
  question about the catalog, so it belongs to the control that governs the catalog.
  It is pushed per section from `ApplySidebarChrome`, which is why the screenshot
  harness has to call that after building its sections by hand — a fixture that skips
  it renders the sidebar as though the switch were off however it is set.

`cluster-tab-workloads-list` and `cluster-tab-basic-sidebar` are the same fixture tab
rendered on and off, and what the pair has to show is as much the *sameness* of the
content area as the difference in the sidebar. `cluster-tab-crd-printer-columns-wide`
is gone: with every declared column always drawn it rendered identically to
`cluster-tab-crd-printer-columns`.

**The old value is deliberately not migrated.** `App.MigrateWorkspacePreferences` used
to carry `WorkspaceSettings.IsAdvancedView` across; it no longer reads it, because that
flag answered a question that no longer exists and most people never touched it — so
migrating would have opted nearly everyone into the shorter sidebar on the strength of
a default they never chose. The workspace property is kept unread so a downgrade still
finds it.

# Several namespaces at once

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.

Both namespace pickers, the Resources list's and the Applications list's, choose one
namespace or several (2026-10). A team that owns `payments`, `checkout` and `ledger` used to
choose between one of them and the whole cluster; Lens and Headlamp both let it choose the
three. **The two pickers make one choice, the tab's** (2026-10-12): a namespace chosen in
Applications is the one Resources shows, and the other way round, and the workspace keeps it.
This page covers the Resources list, where the choice is a watch; the Applications
list only narrows rows it has already read, and its half is in
[applications-mode](applications-mode.md), "The list", rule 9.

## The gestures, the same in both pickers

`Views/NamespacePickerGestures` is the one reading of a click:

- **A click on a row chooses that namespace alone and closes the picker**, which is what the
  single-namespace picker always did, so the common case did not change. Enter does the same
  from the keyboard.
- **A click on the row's box, or a Ctrl/Cmd+click anywhere on it, adds or removes it and keeps
  the picker open.** Space does the same from the keyboard. A line under the search box says
  so, with the modifier taken from `Hotkeys` (UI rule 4); it is read when the view loads, so a
  scheme changed mid-session shows on the next window.
- **The All namespaces row's box clears the choice.**

The box is drawn (`Border.nsCheckHit` around `Border.nsCheck`), not a `CheckBox`. A CheckBox
inside a row that also answers a tap is two controls toggling one value, UI rule 8b's double
flip by another route; drawn, the view model is the only writer of `IsChecked`. The row's
accessible name says ", chosen". A toggle updates the rows' checks in place rather than
rebuilding them, because a rebuild moves the recent namespaces to the top under the pointer
and takes the keyboard's row away (`_togglingNamespace`).

The Applications picker shipped first with a click that toggled. It was changed to this
before release: in the Resources list most choices are of one namespace, and a click that
added to the selection instead of replacing it would have turned the common case into two
steps.

## The model: the first namespace is still `SelectedNamespace`

`ClusterTabViewModel.SelectedNamespaces` is every chosen namespace, empty for All
namespaces. Its first is `SelectedNamespace`, and the others are held apart
(`_additionalNamespaces`). That split is the design:

- **Every reader that needs exactly one namespace still gets one**, unchanged: the access
  review, a terminal's namespace, the palette's "Namespace:" rows. Only the readers that list
  objects read the set: the watch, the metrics poll, the Helm browser, the palette's log rows.
- **Anything that sets `SelectedNamespace` sets it alone.** The palette, a restore, the
  namespace list refreshing: `OnSelectedNamespaceChanging` clears the others unless
  `SetNamespaces` is the one moving it. A reveal of an object in a namespace already chosen
  leaves the choice as it is.
- **A list of one is the old state exactly**, single watch and all, so nothing that worked
  before took a new path.

## The watch

Several namespaces run one list+watch each, merged (`Core/NamespaceWatch`, the fleet view's
`AsyncMerge` with the namespace in the cluster's place). Not one watch across the cluster,
filtered: narrow RBAC is the expected case, and a user granted three namespaces can watch
each of them and not the cluster.

- **A Reset clears only its own namespace's rows**, in one pass, as a fleet member's Reset
  clears only its cluster's. One namespace relisting after a 410 must not blank the others.
- **The verdict waits for every namespace (UI rule 18).** The list stops loading at its first
  row or when every namespace has delivered `Synced`. The first namespace to come back empty
  is not "No pods found" while another is still being read. A namespace whose watch fails
  answers with a `Synced` of its own (`NamespaceWatch.TagAsync`), and a 403 does too, so one
  refused namespace cannot hold the spinner up for ever; the warning names it.
- **Fleet mode reads each cluster whole and keeps the chosen namespaces' rows**
  (`_fleetNamespaceFilter`) rather than running clusters × namespaces watches. One namespace,
  or none, still goes to the server as before. Under narrow RBAC a cluster-wide fleet read is
  refused where per-namespace reads would not be; that is the trade, and it is stated here.
- **The metrics poll asks once per chosen namespace**, the way the watch reads them.
- **The demo cluster** pours each chosen namespace's objects into the list.

## The Namespace column

The resource list draws its Namespace column only where it tells rows apart:
`ClusterTabViewModel.IsNamespaceColumnShown`, re-read by `ClusterTabView.ApplySummaryColumns`
whenever the kind or `SelectedNamespaces` changes.

- **All namespaces, or several**: shown. That is what the column is for.
- **Exactly one**: hidden. Every row is in it, and the column printed the picker's own value
  down the list. A fleet list in one namespace hides it too: the namespace is read on every
  cluster, so its rows are still all in that one, and the Cluster column tells them apart.
- **A cluster-scoped kind**: hidden, as before. No kind yet keeps it, the shape the list opens
  with.

A sort on the Namespace column stays in force while the column is hidden; with one namespace
every row compares equal on it and the sort's own tie-break decides. `NamespaceColumnTests` pins the rule (confirmed red with the one-namespace case
removed). The Helm release list is a grid of its own and follows the same rule, since it reads the same
choice (its synthetic kind is namespaced); a release's history has no Namespace column.

## One choice for both modes

`ApplicationsViewModel.SelectedNamespaces` mirrors `ClusterTabViewModel.SelectedNamespaces`;
the Applications picker writes through `ClusterTabViewModel.SetNamespaces` and redraws when
the tab raises `SelectedNamespaces`, so whatever moves the choice (either picker, the palette,
a reveal, a restore) moves it in both modes. Until 2026-10-12 the Applications picker kept a
session-only choice of its own and opened on All namespaces, so a reader who chose three
namespaces there found them gone on the next launch, and found Resources showing something
else. A choice made in Applications re-reads the Resources list behind it; with the shared
watches ([aot-watch-and-discovery](aot-watch-and-discovery.md), "The shared watches") a kind
the Applications mode already reads costs no second list.

## What is kept

`workspace.json` keeps every chosen namespace in `TabSnapshot.Namespaces` beside
`Namespace`, which stays the first, so a version that knows only one opens on that one.
`ApplyInitialView` restores the rest under the rule the single namespace already had: a name
missing from a namespace list that was read has been deleted and is dropped; one missing
because listing was refused is kept. `PRIVACY.md` says the workspace holds "the namespace or
namespaces showing".

**A tab with nothing saved opens on All namespaces** in both modes, not on the kubeconfig
context's namespace (2026-10-12): the choice is shared with the Applications mode, which is
the whole cluster at a glance, and the context's `default` opened it on an empty list. The
exception is a cluster whose namespace list was refused: there the cluster-wide read is
refused too, and the context's namespace is how kubectl copes, so the tab opens on it. The
demo follows the same rule and opens on all of its namespaces.

## Verification

`ClusterTabNamespacesTests` pins the model, the two gestures and the in-place checks, the
namespace-scoped Reset and the verdict waiting for every namespace (both confirmed red with
the Reset made cluster-wide and the wait removed), the demo rows and the restore.
`NamespaceWatchTests` (Core) pins the tagging and the failed namespace's `Synced`. In the
harness, `ux-namespace-picker` adds a namespace by its box with the picker kept open, reads
the list's rows, then chooses one alone by a click on the row; `ux-applications-keys` does the
same for the Applications picker; `cluster-tab-namespace-picker-several` renders the picker
open with two chosen.

Not verified here: a real API server with several namespaces and narrow RBAC (the 403 path is
reasoned, not run), and the fleet filter against several real clusters.

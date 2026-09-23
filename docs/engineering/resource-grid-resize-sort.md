# The resource grid is the reader's to re-cut

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


Every column in the resource list can be dragged to a new width, a header click sorts
by that column, and both are remembered per kind. `ResourceColumn` and
`ResourceRowComparer` (App layer) are the identity and the ordering, `GridLayoutStore`
+ `WorkspaceSettings.GridLayouts` are the memory, and `ClusterTabView`'s code-behind is
what connects them to the grid.

It is the answer to the 2026-08-19 audit's widest finding, and the reason it is an
answer rather than a re-cut of the numbers is arithmetic: the list has nine fixed
columns at 1280px, so widening one narrows another, and a CRD can declare eleven of its
own on top (KEDA's ScaledObject). Measured on the rendered fixture list, the Namespace
column held ~110px of the same value on every row while two pods of one ReplicaSet
ellipsised at exactly the character that told them apart. No single set of minimums
survives all of that; which column matters is a property of the question being asked,
which is why it belongs to whoever is asking.

Twelve things are load-bearing.

1. **Sorting orders `VisibleRows` and never `Rows`.** UI rule 13's invariant is that
   `Rows` is the informer's own list in arrival order, and everything that produces
   rows — the watch, the fleet merge, the demo dataset, the fixtures — writes to it
   knowing nothing about the projection. Sorting it in place would look right and break
   the informer underneath: the arrival order would be gone for good, so clearing the
   sort could never come back to it. `ClusterTabSortTests` pins that, and the break was
   written and confirmed red before the tests were called done.
2. **The DataGrid's own sorting is not used, and cannot be.** It orders the collection
   view behind `ItemsSource`, which is the list above. `ClusterTabView.OnGridSorting`
   sets `e.Handled = true` and hands the click to `ClusterTabViewModel.ToggleSort`.
3. **`CanUserSort="True"` on every column is what makes the click arrive at all.** These
   are `DataGridTemplateColumn`s with no `SortMemberPath`, and Avalonia's `ProcessSort`
   returns *before* raising `Sorting` for a column whose `CanUserSort` is false — which
   is the default for a template column. Measured, not inferred: a headless probe over a
   real grid saw zero `Sorting` events until the flag was set, and one per click after.
4. **A column is compared by what it means, not by the string it renders.** Restarts is
   a count (the cell carries "(43m ago)" with it, and "10" sorts above "9" as text), Age
   is an instant, CPU and memory are the latest sample's nanocores and bytes, Ready is
   the fraction "2/3" resolves to, and a CRD's `type: date` column is the instant behind
   the age it prints. **Ascending Age means the youngest first**, which is the opposite
   direction to the instants — the number people read is the age, not the timestamp.
   The Events list's Last seen prints an age too and follows the same direction, which
   is why its newest-first default is the ascending arrow (see
   [events-list](events-list.md)); it is also the one kind with a default sort at all.
5. **A row with no value sorts after the rows that have one** in ascending order, rather
   than as a zero or as an empty string above them. A pod that reports no CPU is not a
   pod using none, and a ConfigMap has no Ready to be worst at. The tie-break is the row
   key and is deliberately *not* reversed with the direction: it exists to make the
   order total, and a tie-break that flipped would make the list jump for reasons the
   sorted column cannot explain.
6. **The third click clears the sort.** Two states would leave no way back to arrival
   order, which on a live list is information — it is where the watch put a newly
   created object.
7. **The sort is maintained, not applied once.** A watch is a stream: an object created
   while the list is sorted is inserted where the sort puts it (binary search, so a tick
   on a 5000-row list costs a handful of comparisons), and a Modified that changes the
   sorted value moves the row — the CrashLoopBackOff case on a list sorted by Status.
   `RepositionRow` does nothing while the row is still between its neighbours, so an
   ordinary status refresh moves nothing under the pointer. A list that quietly stops
   being sorted seconds after it was sorted is worse than one that never was;
   that break was written and confirmed red too. The **metrics poll** is the one event
   that rewrites every row's sort key at once, and it re-orders the list *in place*
   (`ResortVisibleRows`, an insertion pass) rather than rebuilding it: a rebuild raises a
   Reset, a DataGrid answers a Reset by dropping the scroll position, and a CPU-sorted
   list that jumped back to the top every fifteen seconds would be useless for the one
   job a CPU sort has. That break was written and confirmed red as well.
8. **A CRD column is identified by the CRD author's name for it, never by slot.** The
   grid's printer columns are ten fixed positional slots, and which column a slot draws
   is a property of the kind in front of the reader — two kinds declaring different
   columns put different things in slot 3. A width or a sort keyed by slot would silently
   move to a neighbour on the next kind. (It used to shift under the Advanced view too,
   which brought a CRD's `priority: 1` columns in and out mid-session; that gate is gone,
   but slot identity was never the right key regardless.) A remembered sort by a column
   the kind no longer declares falls back to arrival order rather than shuffling the list
   against cells that are not there.
9. **A star column and an Auto column do not keep a drag in the same place**, which is
   why the stored width carries its unit. Measured on a real grid: dragging a `2*`
   column leaves it a star column and rewrites the ratio (`2*` → `2.52*`), while
   dragging an `Auto` column leaves its declared width alone and changes only what it
   displays. So a star column is remembered as its ratio — which reproduces the same
   proportional layout at any window width — and everything else as the pixels it ended
   at, restored as an absolute width. A column nobody has dragged is reset to the width
   the XAML declares, or a kind with no remembered layout would inherit the previous
   kind's.
10. **Only what actually changed during the drag is remembered.** There is no
   column-resize event in Avalonia — the DataGrid changes the width from inside the
   header's own pointer handling and tells nobody — so the gesture is bracketed instead:
   snapshot every column on pointer press, compare on release, store the difference.
   Storing every column instead would pin the Auto columns to whatever their content
   happened to measure at that moment, which is a choice nobody made.
11. **Widths and sort are one record with two writers, and neither may drop the other's
   half.** Pixels are not view-model state, so the view owns the widths; the sort is
   what orders the rows, so the view model owns it. Both go through
   `GridLayoutStore.Update`, which takes a function over the stored layout and does a
   read-modify-write **of the file** — never of a cached snapshot, exactly as
   `App.Update` does and for the same reason: the shell writes the same workspace (tabs,
   pins, environment overrides) from another path entirely.
12. **This is session state, so it is `workspace.json` and not `settings.json`.** The
   test is the one in "Settings, and what belongs in which file": deleting the workspace
   should lose how the window looked and nothing else, and a column width is exactly
   that. Keyed by `<group>/<Kind>` — not the version, because a cluster promoting a CRD
   from v1beta1 to v1 is still the same list to whoever widened its Name column, and not
   the cluster, so a width chosen for Pods holds everywhere.

**The sort indicator is drawn into the header text, and that is not laziness.** Fluent's
`:sortascending`/`:sortdescending` header pseudo-classes are set from the collection
view's sort descriptions, which stay empty here *because* the sorting is ours — so the
arrow has to be drawn rather than styled. A header template would mean a `Control` in
place of a string, which opts the header cell out of Fluent's own column-header template
(the same reason a printer column's `description` is not a header tooltip). The header
therefore stops being a constant, which is the other half of why column identity moved
to `Tag`.

**Two things this deliberately does not do.** The Helm release list is a second grid with
its own hardcoded columns and is untouched, so `FEAT-72` (its `Updated` column printing a
clipped absolute timestamp) is not subsumed by this. And nothing here adds *horizontal
scroll*: dragging redistributes the width the window has, so the fleet list's ten columns
at 1280px still clip their rightmost headers (`ENG-6`) — a reader can now trade one of
them away, which is a workaround and not the fix.

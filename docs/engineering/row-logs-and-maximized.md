# One click to logs from the row, and logs opened full-size

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.

Opening a row's logs took two interactions (select the row, press L — or right-click and
read the menu), and reading them properly took one more: the inspector opens in a ~300px
dock under the list, so a log with long lines and a history worth scrolling needed the
maximize icon every time. L2 (owner request, "quick access to logs") made both one
interaction:

- **The row's logs icon.** A small icon at the right end of the Name cell, drawn on the
  hovered row and the selected row, on every row whose object has logs. A click opens
  that row's logs; a Shift+click opens them full-size.
- **Shift+L** on the list opens the selected row's logs with the inspector already
  maximized. Esc, or the dock's restore icon, goes back to the split.
- **"Open logs maximized"** (Preferences → Logs and metrics, `AppSettings
  .OpenLogsMaximized`) makes full-size the default for every open-logs route — L, the
  icon, the context menu and the palette's `Logs: …` rows.

Nine things are load-bearing.

1. **Every route is `ClusterTabViewModel.OpenLogsForAsync(target, previous, maximized)`.**
   `maximized` is a `bool?`: `true` from Shift+L and a Shift+click, `null` from everything
   else, meaning "ask the preference". That makes the preference something the open path
   reads rather than something each route remembers to read — which is CLAUDE.md's settings
   rule 3 ("a setting nothing reads is worse than no setting") enforced by shape. It is read
   with `App.LoadSettings()` at the moment logs open, the same choice the delete confirm
   makes, so turning it on applies to the very next open. `RowLogsTests` pins all three
   routes, and replacing the read with `false` was run and turned three of them red.
2. **Maximizing only ever happens after a pane actually opened, and opening never
   un-maximizes.** `OpenLogsPaneAsync` reports whether it opened (or re-selected) a pane; a
   workload whose selector names nothing opens none, and maximizing the inspector over
   whatever else happened to be open would be a dock change nobody asked for. In the other
   direction, logs opened while the inspector already fills the area stay full-size with
   the preference off — the reader chose that state, and the way back is Esc.
3. **Which rows get the icon is `LogTarget.CanOpen(resource)`**, cached per row as
   `ResourceRowViewModel.HasLogs` and recomputed on every update: a core `v1` Pod, or any
   object whose `spec.selector` names pods (`LabelSelector.ForPodsOf`). That is exactly the
   evidence L and "Logs (all pods)" are gated on, read off the object rather than off a list
   of kinds, so the icon can never be offered where L would do nothing — a ConfigMap, a
   Node, a Deployment with an empty selector, a CRD that happens to be called `Pod` in
   another group — or be missing where L works (a Service, a Job, a CRD with a pod
   selector). It is a public static so L3's panes that name pods can ask the same question.
4. **The icon is inside the Name cell, not a column of its own.** A column is one more
   width the per-kind layout remembers ("The resource grid is the reader's to re-cut") and
   one more thing a CRD's printer columns compete with at 1280px. It sits in an `Auto`
   column of the cell's own two-column grid.
5. **It takes width only while it is drawn, and that was measured, not assumed.** The first
   cut reserved the 24px slot on every row that has logs and hid the icon with opacity, so
   a name would never re-trim as the pointer moved. Rendered at 860px wide, that slot was a
   fifth of every pod name's cell on rows nobody was pointing at — "payment-servi…" became
   "payment-s…" down the whole list. So the icon is hidden with `IsVisible` instead, the
   slot collapses with it, and the 24px come out of the name only on the hovered and the
   selected row. The cost, stated: the hovered row's name loses about three characters
   while the pointer is on it (its tooltip has the whole name).
6. **The show/hide rule is a style, and that dictates the template's shape.**
   `Button.rowAction` (app `Styles/Theme.axaml`) is `IsVisible=False` and becomes visible
   under `DataGridRow:pointerover`, `DataGridRow:selected` and the `ListBoxItem`
   equivalents, so any list can reuse it. A *local* `IsVisible="{Binding HasLogs}"` on the
   button would outrank those style setters and the icon would show on every row, so the
   row's own verdict sits on a `Panel` wrapped around the button instead.
7. **The click is a `Click` handler, not a `Command`, because Shift matters and a Button's
   `Click` carries no modifiers.** The release is what raises Click, so a Tunnel
   `PointerReleased` handler on the grid (`OnGridPointerReleasedForRowAction`) records the
   modifiers before the button's own class handler runs, and `OnRowLogsClick` reads and
   clears them. The button also swallows the press the grid would have selected the row
   with, so `OpenRowLogsAsync` selects the row itself — the list should say which row the
   inspector is showing. The button is not focusable and the handler puts focus back on the
   grid, so L / Shift+L / Esc keep working without a second click. UI rule 8: the button's
   template paints a Transparent background, so its whole 20×20 hit-tests (`ux-row-logs`
   clicks it 1.5px from its right edge, where the glyph is not), it has the hand cursor, and
   `:pressed` here is a real Button pseudo-class.
8. **A maximized inspector collapses the list, not the list's focus.** Shift+L leaves
   keyboard focus on the grid, which is now zero pixels tall. So `OnGridKeyDown` ignores its
   row keys while `IsInspectorMaximized` — E would otherwise open a YAML tab for a row
   nobody can see, and Delete would arm a strip over a list that is not there — and handles
   Esc there directly. It deliberately leaves every other key unhandled, so the window's own
   bindings (Ctrl/Cmd+K, Ctrl/Cmd+Shift+L) still reach the window.
9. **Esc restores the split from anywhere in the tab except a text-editing control.**
   `OnViewKeyDown` is a Bubble handler on the whole view and acts only on an Esc nothing
   below it handled, and never when the key came from a `TextBox`, the YAML editor or the
   exec terminal: Esc there belongs to the text (a search box clearing itself, AvaloniaEdit's
   search panel, a shell running vim), and a key that meant two things would be worse than
   either. The restore icon's tooltip now names Esc.

**Why Shift+L and not a new letter.** The pair reads as one gesture — the same logs, bigger
— and Shift is also the click modifier on the icon. `CommandBindings.Matches` compares
modifiers exactly, so Shift+L can never also run L's command; `RowLogsTests` pins that.

**The palette offers it too.** With a row selected that has logs, Ctrl/Cmd+K lists
"Logs, maximized" beside "Logs", so a mouse user who reaches for the palette learns the key
from its subtitle.

**What the icon is not.** It is not in the Helm or Argo *list* grids (their rows are
releases and Applications, which have no pods of their own to tail — an Application's
managed workloads are another matter, below), and it does not replace the context menu's
Logs / Logs (all pods) items, whose captions stay the place a mouse user learns L.

## L3: logs from everywhere a pod is named

L2 made logs one interaction from a row of the resource list. Every other place the app
names a pod still took four: back to the list, Pods, find the row again, L. L3 (owner
request) puts the same affordance — L, Shift+L, the hover/selected icon with Shift+click,
and a "Logs" menu item — on each list that names a pod or a workload:

| Where | L / Shift+L | Row icon | Menu | Notes |
| --- | --- | --- | --- | --- |
| Workload detail → Pods | yes | yes | Logs, Logs maximized | Enter / double-click still open the pod |
| Node detail → Pods | yes | yes | Logs, Logs maximized, Open pod | the chevron still opens the pod |
| Events list, an Event about a pod | yes | yes, in the Object cell | the list's own "Logs" | only when `involvedObject`/`regarding` is a core pod |
| Argo Application → Resources | no | yes, on hover | none | pods, built-in workloads and Argo Rollouts |

Where it deliberately is **not**: pod detail's Events tab (every event there is about the
pod whose Logs tab is one click away), workload detail's Events tab (its events are about
the workload), and node detail's Events tab (about the node). The Argo resource rows have
no L because they have no selection — they are an `ItemsControl`, with nothing for a key to
act on.

Eight things are load-bearing.

1. **One resolver, and the panes are handed it.** `ClusterTabViewModel.OpenNamedLogsAsync`
   is the only new entry point, and it ends in `OpenLogsForAsync` — so the pane chosen (pod
   detail on Logs, or the one-stream workload pane), the inspector tab reused and the "Open
   logs maximized" preference are the list's own, whichever list the gesture started in.
   Each pane receives it as an `OpenNamedLogs` delegate bound to the pane's own cluster and
   client (`NamedLogsOpener`), the same pattern as the `_openOwner` delegate owner chips
   use, so a fleet row's pane opens logs on that row's cluster. No pane builds a log tab of
   its own. `PaneLogsTests` pins the routing, and making the pane opener ignore the
   preference (passing `false` where `null` means "ask it") was run and turned the two
   preference tests red.
2. **The object is read before its logs open, and that is how "gone" gets said.** A pane
   that names an object does not always hold it (an Argo row is a kind and a name), and a
   pane's list can be older than the cluster: node detail's pods are one list, not a watch,
   and an Event outlives its pod by up to an hour. So the resolver GETs it first. Missing,
   it returns "Pod shop/web-1 no longer exists — it was deleted or replaced since this was
   listed."; refused, the server's own 403 sentence; resolved but naming no pods (a
   Deployment with an empty selector, a Job the kind hint let through), it says so. The
   sentence is shown where the gesture was made — a `LogsNotice` `infoBar.warn` above the
   pane's list (present only while there is something to say, UI rule 9), or the list's
   own inline warning for an Event — never as a click that does nothing. On the demo
   cluster the same code reads through `NamedObjectSource.Demo`, and a missing object is
   "isn't part of the demo dataset", which is not a claim about a real cluster.
3. **`NamedObjectSource` is delegates, for `LogTargetSource`'s reason.** Catalog and read
   are functions rather than a `ClusterClient`, so the demo dataset and the tests' stand-in
   (a pod that 404s, a read that 403s) go through exactly the code a real cluster does.
   A real API server's "gone" is therefore pinned by a unit test, not only argued.
4. **An Event's logs are its pod's, and only a pod's.** `LogTarget.InvolvedPod` admits an
   Event (core/v1 or events.k8s.io, `involvedObject` or `regarding`) whose object is a core
   `v1` Pod; `LogTarget.CanOpen` — the row icon's rule — now includes it, and
   `HasOwnLogs` is the old rule, which the resolver applies to what it read. An Event about a
   Deployment gets no icon: its double-click opens that Deployment, whose own row has L, and
   an icon that opened a workload's logs from an event would be a second meaning for one
   glyph. The list's "Logs" item and L's pod half are gated on
   `CanOpenPodLogsForSelectedRow` (a pod row, or an Event about one); exec, port-forward and
   previous logs stay on `IsPodRowSelected`, because they act on a pod row itself.
5. **The keys and the Shift+click are decided once (`Views/RowLogsGesture`).**
   `MatchLogsKey` matches L and Shift+L against the catalog's `CommandScope.List` rows — the
   resource list's own — so a pane can never answer to a key the cheat sheet does not name;
   `Track` records pointer-release modifiers in the Tunnel phase for the icon's Shift+click,
   which is L2's mechanism lifted out of `ClusterTabView` rather than copied. The panes'
   grids register their key handler in the **Tunnel** phase for the reason the resource
   list does: DataGrid's class handler consumes Enter before a bubble handler sees it
   (workload detail's Enter, "open the pod", shares that handler).
6. **The Argo row's slot is fixed, unlike the Name cell's.** Rule 5 above collapses the
   icon's slot so a pod name gets its width back. An Argo row's text is not beside the icon
   — its sync and health pills are — so a collapsing slot only made the pills jump 26px
   sideways under the pointer and kept rows with and without logs from lining their pills
   up (seen in the first cut's `cluster-tab-argo-resource-logs-hover`). Every row reserves
   the slot; the icon inside it is revealed by `Grid.argoResourceRow:pointerover`, a local
   style, since the shared `Button.rowAction` reveal keys on `DataGridRow`/`ListBoxItem` and
   an `ItemsControl` item is neither. The row Grid has a Transparent background so that
   hover hit-tests across its whole width (UI rule 8). Which Argo rows get it is a kind list
   (`LogTarget.MayHaveLogs`: Pod, Deployment, StatefulSet, DaemonSet, ReplicaSet, Job, and
   `argoproj.io` Rollout) — the one place a kind list is acceptable, because the row carries
   no object body and the resolver's `HasOwnLogs` on the object it reads has the last word.
   Rollout is on it because on a cluster that uses Argo Rollouts it *is* the workload, and
   the first cut hid the icon on exactly the row an Argo reader most wants logs from. A
   duplicate build of L3 (#90, closed) went the other way and offered the icon on every
   row; that was not taken, because on ConfigMaps, Services and Argo's own Applications it
   is an always-visible control whose only answer is "names no pods" (UI rule 1). Other
   selector-bearing CRDs keep their L on their own list rows.
7. **A name is not an identity, so the UID is checked.** A pane that listed a pod passes
   its UID along (workload detail from the watch, node detail from its one list, an Event
   from `involvedObject.uid`). A StatefulSet recreates `web-0` as `web-0`, so a GET by name
   can return a *different* pod; when the UIDs differ the resolver says "was replaced since
   this was listed" and opens nothing, rather than showing the new instance's logs as if
   they were the ones picked. Argo rows carry no UID and are resolved by name alone.
8. **The read belongs to the pane that asked for it.** `OpenNamedLogs` carries the naming
   pane's own `CancellationToken`, and the resolver checks it once more after the read
   returns. Closing workload detail, node detail or an Argo pane while the GET is in flight
   therefore opens no logs tab and states nothing — without the second check, a reader
   that ignores the token still answered late and opened a pane over a closed one.
   And in each pane's grid a right click selects the row under it before the menu's Logs
   reads the selection. DataGrid 12 already does that over a *cell*, but not past the last
   column, where nothing hit-tests: node detail's menu opened there acted on the
   previously selected pod. `RowLogsGesture.Track` does it for every grid it tracks —
   the resource list's own copy of the same handler moved there — first from the event
   source, then by which realized row spans the pointer's height.

**Verification.** `PaneLogsTests` (App tests) drives each pane the real double-click opens
on the demo cluster, plus the resolver's real-cluster sentences through a stand-in source.
The screenshot harness's `ux-pane-logs-workload`, `-node`, `-events` and `-argo` checks drive
each list for real — icon hidden at rest, shown on the selected and hovered row, clicked
1.5px from its edge, L and Shift+L on the pane's own grid, and (workload and node) a right
click past the last column that must select the row under it — and throw on a regression.
The UID and cancellation guards are mutation-checked: disabling either turns its test red.
`cluster-tab-node-detail-pod-gone` is the stated-gone state. Not verified: a live cluster
(the sandbox cannot run pods here), so the 404/403 sentences are pinned against the
stand-in, not an API server.

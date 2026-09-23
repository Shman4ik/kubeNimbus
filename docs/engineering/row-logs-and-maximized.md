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

**What the icon is not.** It is not in the Helm or Argo grids (their rows are releases and
Applications, which have no pods of their own to tail), and it does not replace the context
menu's Logs / Logs (all pods) items, whose captions stay the place a mouse user learns L.

# Unhealthy only: the list's second narrowing

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.

A chip beside the resource list's search box (and Ctrl+Z on the focused list, and a
palette row) narrows the list to rows whose health verdict is warn or error — k9s's
"toggle faults". `ClusterTabViewModel.IsUnhealthyOnly` is the mode,
`ResourceHealth.IsUnhealthy` the predicate, `ResourceRowViewModel.IsUnhealthy` the
per-row read of it.

It exists because the list had no answer to "what is broken here?" short of sorting by
Status and scanning, once per kind; the search box deliberately does not match status
(UI rule 13), and that is still right. Eight things are load-bearing.

1. **It reads the verdict, never the text, which is why it does not reopen rule 13.**
   Rule 13 keeps status out of the *search box* because free text over status is noise:
   "Running" matches most of a healthy list. This matches no text. It reads
   `StatusHealth`, the same value that colours the status pill, so what it keeps is
   exactly what is drawn amber or red and the two can never disagree about a row.
2. **Warn and error, not idle.** `idle` is the verdict for "claims nothing": a
   Deployment scaled to zero, a CRD phase the app does not recognise. Listing those as
   problems would fill the narrowed list with things nobody has to fix. Warn stays in
   because "in flight" and "degraded" are what someone hunting for trouble wants: a pod
   Pending for an hour is warn, and so is one stuck Terminating.
3. **It filters `VisibleRows` and never `Rows`, through the text filter's own path.**
   `MatchesRowFilter` is one predicate with two halves (query, health), so the append
   arm of the mirror, the rebuild, the sort and the "n of m" caption all learned about
   health by changing one function. Nothing that writes to `Rows` — the watch, the fleet
   merge, the demo dataset, the fixtures — knows the mode exists.
4. **A Modified has to re-evaluate the row, and that is the new part.** A name never
   changes under an object, so the search box only ever had to be evaluated when a row
   was added. Health does change, and the watch updates the row object *in place*, so
   nothing reaches `Rows.CollectionChanged`. `RefreshRowVisibility` runs on every
   Modified (in `Apply` and `ApplyFleet`, where `RepositionRow` used to be called
   directly): a row that breaks is inserted, one that heals is removed, and one that
   stays is repositioned exactly as before. The insert goes where the row would have
   been all along — the sort's binary-search position, or its place in `Rows`' arrival
   order (`ArrivalIndexFor`, one walk over both lists) — never appended, so toggling the
   mode off and on reproduces the list the events produced. Both halves were written
   wrong on purpose and confirmed red in `ClusterTabHealthFilterTests` before the tests
   were called done: calling `RepositionRow` alone fails five tests, appending instead of
   inserting fails two. The workload detail pane's Refresh also updates a list row in
   place (`WorkloadDetailTabViewModel.RefreshAsync`) and does not re-evaluate it; the
   watch's own Modified for the same change does, a moment later.
5. **A kind with no verdict disables the chip instead of emptying the list.**
   `CanFilterUnhealthy` is `ResourceStatusSummary.ShowsStatus` — the same classification
   that hides the Status column for ConfigMaps, Secrets, Services and the rest, where
   every row is idle and the mode could only ever produce an empty list. The chip is
   disabled with `ToolTip.ShowOnDisabled` so the tooltip saying why is still reachable,
   and it is dimmed (`ToggleButton.chip.checkedAccent:disabled`) so a checked chip over a
   list it is not filtering does not look like one that is.
6. **It is a mode, so it survives a kind change; the search box is a question, so it
   does not.** Someone hunting for trouble wants it on Deployments after Pods. Per tab,
   never persisted: reopening the app on a narrowed list that does not say why is the
   "empty-looking list" bug with a restart in the middle. A CRD kind that passes the gate
   but whose objects report nothing the app can read still lands on the good-news empty
   state, which then says "None of them reports a status this list can judge" rather than
   claiming health it cannot see.
7. **Its empty state is a third one, and the good-news one.** `IsHealthFilterEmpty` sits
   beside `IsListEmpty` ("there is nothing here") and `IsFilterEmpty` ("nothing is called
   that"): "Nothing unhealthy among N <kind>", a check mark rather than the kind's glyph
   or a magnifier, and "Show every row" as the way back. When both narrowings are on, the
   split is on whether the *search* matched anything — a search that matches nothing is
   the search's empty state whatever the health filter says, because the typo is the thing
   to fix. Loading wins over all three (UI rule 18). The count behind the title is only
   taken while the list is showing nothing, so it never costs a busy list a scan.
8. **Every entry point sets the one property.** The chip is a two-way `IsChecked` with no
   command (UI rule 8b). The palette row captures an explicit target, and is offered to
   switch *off* wherever the mode is on — including a kind where the disabled chip cannot
   — so there is always a way out. The key is list-scoped literal Ctrl+Z
   (`CommandId.ToggleUnhealthyOnly`): a window binding on it would steal Undo from every
   text box, the YAML editor included, and on macOS Cmd+Z is Undo everywhere while Ctrl+Z
   is exactly k9s's key. `ux-unhealthy-toggle` in the screenshot harness drives the key, a
   real pointer click on the chip and the palette against the rendered view, because a
   ToggleButton wired with both halves of rule 8b renders perfectly and does nothing.

**The chip needed its own checked foreground, and the reason is shared.** nimbusUi's
checked-chip rule washes the chip in `AppSelectionBrush` (16% alpha blue) and leaves
Fluent's checked foreground, which is white. On the light theme an icon-only chip was a
white glyph on a pale wash and rendered as an empty circle. `checkedAccent` paints the
glyph in `AppAccentBrush`; it is a class rather than a fix to every chip because the rest
of that rule is shared with pgNimbus, and the sidebar's Advanced switch and the fleet
"All clusters" chip still have the problem there.

**The header row it joined overflowed before it did.** The connection warning's
`TextTrimming` never fired, because it sat in a horizontal `StackPanel`, which measures
children at infinite width; in the fleet-partial state a long warning drew straight over
the search box. It is a `DockPanel` now and ellipsises, with the full text in a tooltip
and the status bar. At 1024px with fleet mode on and a warning showing, the row still
does not fit (the fleet toggle, its summary, the search box and this chip are all fixed
widths); that is the header half of `ENG-6`, not something this change fixes.

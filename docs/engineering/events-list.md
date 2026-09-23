# The Events list reads like `kubectl get events`

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.

Selecting Events in the sidebar used to render the generic list: Name was the Event
object's own generated name (`checkout-worker-5d8f7b9c4-qz9pl.17f2a1`), Status was
`Reason ×count`, Age was the Event's `creationTimestamp`, and neither the message nor
the object the event was about appeared anywhere. Finding out what had happened in a
namespace meant opening each event in turn — one double-click per event. kubectl prints
LAST SEEN, TYPE, REASON, OBJECT and MESSAGE, and that is the job the list now does: the
answer to "what happened here, and to what" is on screen in one read.

`EventFields` (Core, `ClusterClient.Events.cs`) reads the fields; `ResourceRowViewModel`'s
event cells hold them; `ClusterTabView.ApplySummaryColumns` swaps the columns in; and
`ClusterTabViewModel.DefaultSortFor` opens the list newest first.

Nine things are load-bearing.

1. **Both Event kinds, one reader.** Discovery lists core/v1 `Event` (Config section) and
   `events.k8s.io/v1` `Event` beside it, and they are the same object under different
   field names: `regarding` for `involvedObject`, `note` for `message`,
   `deprecatedCount`/`deprecatedLastTimestamp`/`deprecatedFirstTimestamp` for the
   counters. Every `EventFields` reader accepts either shape, and `EventFields.IsEventKind`
   is group-first — a CRD called `Event` in some other group is not one, the same rule the
   sidebar applies to a CRD called `Deployment`. Double-click navigation reads `regarding`
   too, so it now works from either list.
2. **Last seen is a fallback chain, and the series comes before `eventTime`.** The chain is
   `lastTimestamp` (or `deprecatedLastTimestamp`) → `series.lastObservedTime` → `eventTime`
   → `firstTimestamp` (or `deprecatedFirstTimestamp`) → `metadata.creationTimestamp`. The
   train spec listed `eventTime` before the series; that order is wrong for exactly the
   events the new API writes as a series (the scheduler's `FailedScheduling` is the common
   one), whose `eventTime` is the *first* occurrence — it would print "47m" for an event
   that fired two minutes ago and sort it below older ones. kubectl's own printer reads the
   series first, and so does this. A zero `metav1.Time` (`0001-01-01T00:00:00Z`) is unset,
   not an event two thousand years old; an event with nothing set at all renders `—` with a
   tooltip that says so, and sorts last. The tooltip is the exact local instant, plus the
   first-seen instant and the count when the event repeated.
3. **Last seen follows Age's direction, so "newest first" is its ascending order.**
   The cell prints an age ("5m"), and the grid's rule for Age is that ascending means the
   smallest age first ("The resource grid is the reader's to re-cut", rule 4). A column
   that prints the same kind of number and sorts the other way would make the arrow mean
   two things in one app. The spec's "Last seen descending" is read as *descending by time*
   — newest first — which in this grid's convention is the ↑ arrow on Last seen. Like Age,
   the text is re-rendered by the list's shared timer, not by watch events.
4. **The default sort is a per-kind default, and clearing it is remembered.** Every other
   kind opens in arrival order; Events open on Last seen, because an informer's relist
   order is the API server's storage order and says nothing to a reader. A stored layout's
   `null` sort means "no choice made", so the default applies; clearing the sort on a kind
   that has a default stores `ResourceColumn.Unsorted`, or the next visit to the kind would
   quietly re-sort a list the reader had asked to leave alone. Choosing the default again
   stores `null`, which lets the layout drop out of `workspace.json` (the file lists the
   choices somebody made). `ClusterTabEventsListTests` pins both halves, and storing `null`
   for a clear was written and confirmed red before the tests were called done.
5. **The columns replace Name, Status and Age; Namespace and Cluster stay.** The Event's
   own name identifies nothing a reader types; `Reason ×count` is now two columns; and the
   Event's creation is not when it last happened. Namespace stays (every kind shows it,
   single-namespace or not — this change does not start hiding it for one kind), and the
   fleet view's Cluster column stays. Type is a `statusPill` that is tinted amber for
   Warning and left neutral for Normal — the word is always printed, so the tint is never
   the only signal, and a list that is mostly green says nothing. `ResourceStatusSummary`
   still classifies Warning as warn, which is what the unhealthy-only chip keeps
   ([unhealthy-only](unhealthy-only.md)); it now does so for `events.k8s.io` too.
6. **They are ordinary tagged columns, not CRD printer slots, and that was a choice.**
   The printer-slot machinery ([crd-printer-columns](crd-printer-columns.md)) would have
   given sort, resize and persistence for free, and so do ordinary tagged columns — both
   go through the same `Tag`/`ResourceColumn` id, `ResourceRowComparer` and
   `GridLayoutStore`. What the slots cannot do is what Events need: a slot is one
   JSONPath evaluated to text, and Last seen is a five-step fallback, Object is a
   concatenation of two fields, Type is a tinted pill, and Last seen and Message want a
   tooltip that is not the cell text. Bending the slots to carry that would have meant
   computed "printer columns" with their own cell templates — and the slots' other
   behaviour (Status and Details step aside, the health dot comes back, a CRD GET per kind,
   `crd:` ids in the stored layout) would all have needed an Events exception. Six fixed
   XAML columns, hidden for every other kind, cost nothing at runtime and are compiled
   bindings like the rest (AOT).
7. **Widths: no `Auto`, and minimums that keep the words.** Bounded cells are fixed (Last
   seen 108, Type 90, Count 84), prose is star (Reason 1.3\*, Object 1.3\*, Message 2.4\*),
   per [datagrid-auto-columns](datagrid-auto-columns.md). The minimums matter more than the
   widths: a narrow window squeezes fixed columns down to them, and the first cut let Type
   shrink until the pill read `Warni…` at 1024px — which turns the text-not-colour-only rule
   back into colour-only. Type's minimum is its width for that reason, and Last seen's and
   Count's fit their headers. At 1280px with the sidebar open the Message column gets ~270px;
   that is the trade, the full text is the tooltip, and every column is draggable.
8. **The search box matches Reason, Object and Message for Events** — UI rule 13's
   "identity, not status" argument applied to what identifies an event. Nobody types an
   Event's generated name; they type "BackOff", "checkout-worker" or "Insufficient cpu".
   Type is still not matched ("Normal" would match most of the list). The placeholder says
   what it matches (`RowFilterPlaceholder`), since for this one kind it is more than a name.
9. **The empty state says events expire.** An empty Events list most often means "nothing
   recent", not "nothing ever" — the API server's `--event-ttl` defaults to one hour — so
   the "No Events found / in <ns>" state carries a third line saying so (`EmptyListHint`),
   and a quiet namespace after yesterday's incident does not read as a broken watch.

**Double-click and Enter open the involved object**, as before, through the same
resolve-and-open path owner chips use — and in fleet mode now on the *event's* cluster.
The route used to call `OpenOwnerAsync` without the row's cluster and client, so a fleet
row's object was looked up on the tab's own cluster. An event that names no object falls
through and opens the event itself (its YAML).

**Screenshots.** `cluster-tab-events-list` is the demo dataset's events, exactly (demo
rule 3); `cluster-tab-events-empty`, `-search` and `-narrow` (1024px) are the states
named above. `cluster-tab-events-edge-cases` is deliberately synthetic, over a fleet: an
`events.k8s.io`-shaped series whose `lastObservedTime` is newer than its `eventTime`, an
event naming no object, one with no timestamp at all and a multi-line message. None of
those belongs in a dataset a Store reviewer browses, and a real API server always stamps
`creationTimestamp`, so "no timestamp" exists only there.

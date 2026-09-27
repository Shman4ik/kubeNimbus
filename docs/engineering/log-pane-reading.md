# Reading a log: find, levels, clear, local time, remembered display

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.

Both log panes — pod detail's Logs tab (`PodDetailTabViewModel`) and the one-stream
multi-pod pane (`WorkloadLogsTabViewModel`, which is also the Applications page's log view)
— share one set of reading tools. They came in one bundle (FEAT-33, FEAT-36, FEAT-37,
FEAT-38, FEAT-39, FEAT-40, ENG-38, ENG-45), all aimed at the same thing: fewer clicks from
"service X is broken" to the log line that says why. Each is the same in both panes, and
where the code is shared (`LogFind`, `LogLevelFilter`, `LogLineText`, `LogSearchGestures`)
that is so the two cannot drift apart.

## The search finds, and filters as its other mode (FEAT-33)

Typing in a log pane's search box used to hide every line that did not match. That answers
"how often", and it throws away the thing an incident needs: the lines around the match.
The box now **finds** by default: every line stays, each match is highlighted in place, the
box shows "3 of 17" and ↑/↓, and **Enter / Shift+Enter** step to the next (later) or
previous (earlier) match, wrapping at both ends. The funnel chip inside the box switches to
**filtering**, which is exactly the old behaviour. Esc in the box empties it.

Six things are load-bearing.

1. **Matches are lines, not occurrences.** A line that says "timeout" twice is one stop for
   next/previous and both occurrences are highlighted on it. A counter that stepped twice
   through one line would read as the pane not moving.
2. **A new query lands on the newest match.** The panes follow the bottom of the stream, and
   the first question in an incident is "when did this last happen"; Previous then walks
   back in time. After that the current match is kept across new lines, trims and
   level/pod changes for as long as it is still shown — a following stream must never drag
   the reader off the line they are on (`LogPaneTests`, "A_new_matching_line_does_not_move").
3. **The search matches the message, never the timestamp prefix.** `LogLineViewModel
   .Contains` is the one rule both modes and the highlight use, and `LogLineText` skips the
   first `MessageOffset` characters of the displayed text — otherwise a search for "08:41"
   would light up text the counter does not count.
4. **The highlight is painted, not built from `Inlines`.** `Controls/LogLineText` is a
   `SelectableTextBlock` that fills the match rectangles from its own `TextLayout
   .HitTestTextRange` in `RenderTextLayout` (the hook `TextBlock`'s sealed `Render` calls
   with the text's origin), then lets the base class draw selection and glyphs over them.
   The text stays one bound string, so selection, Ctrl+C and the severity classes behave
   exactly as before, and the boxes follow wrapping and the font for free. It declares
   `StyleKeyOverride => typeof(SelectableTextBlock)`, which is what keeps the theme and the
   `.logError`/`.logWarn`/`.logInfo` selectors applying to it.
5. **The brushes are per theme, and translucent.** `LogMatchBrush` / `LogCurrentMatchBrush`
   in `Styles/Theme.axaml`'s theme dictionaries: a severity colour still reads through a
   match, and the current match is the same hue far stronger. A yellow that reads on the
   light card glares on the dark one, which is why there are two.
6. **Scrolling to a match is the view's job, and it is posted.** The view model exposes
   `CurrentLogMatch`; the view calls `ContainerFromItem(line).BringIntoView()` at Background
   priority when it changes (and when the view is re-attached), for the same reason the
   follow's scroll-to-end is posted: the container may not be measured yet in the tick the
   match moved. Moving to an older match takes the view off the bottom, which is what stops
   the follow yanking it back — the existing scroll lock, unchanged.

Not built: regex. A user-typed pattern would need `RegexOptions.NonBacktracking` over a
buffer of thousands of lines on the UI thread, and nobody on the owner's side has asked for
it; the substring search is the whole of the demand that was found.

## Levels: Error, Warn, Info — and the unleveled always shown (FEAT-36)

A **Levels** button in each pane's toolbar opens three checkboxes. Its own caption is the
filter's state — "Levels" when nothing is hidden, otherwise the levels still shown ("Error,
Warn", or "Unleveled only") — and it is drawn in the accent while it narrows
(`Button.chip.accentText`), because a filter whose state lives only in a closed flyout is a
trap. It is a `Button` with a flyout, not a `ToggleButton`: it has no checked state of its
own to fight the flyout over (UI rule 8b).

**A line with no level keyword is always shown.** Severity is read off a keyword in the
text (`LogLineViewModel.Severity`), and most real output carries none — nginx access logs,
Go's `log.Print`, anything JSON. Hiding them because they are "not Error" would turn "hide
the INFO noise" into "hide most of the log", and that population is exactly the one the
pane once made invisible by accident ([log-severity-classes](log-severity-classes.md)).
`LogLevelFilter.Admits` is the rule; `LogPaneTests` pins that a plain line survives all
three being off, and turning the unleveled case to `false` was run and turned it red.

A pane emptied by the level filter says so ("Every buffered line is at a hidden level") —
it must not read as a quiet container. The Applications page's embedded pane keeps its own
**Errors only** instead and does not show Levels: two severity controls side by side would
be two answers to one question, and Errors only is the stricter one that page wants (it
does hide unleveled lines, and says so in its tooltip).

## Clear keeps the stream (FEAT-40)

**Clear** (in the pane's `⋯` menu) empties the pane and leaves every stream running, so "clear, then
watch what happens next" no longer means stopping and restarting — which lost the follow
and fetched the tail again, bringing back the very lines being cleared. Lines received but
not yet drawn go too. Until something new arrives the pane says "Cleared N lines — still
following", not "no lines", which after a clear would be a verdict about the stream it has
no grounds for. It lives in the pane rather than only in the palette, as the backlog row
guessed: the palette has no notion of the focused inspector tab.

## The toolbar keeps what is read, and the `⋯` menu keeps the rest

The bar carries Range, Follow, Previous, Levels and Copy. Everything else — Timestamps, UTC,
Wrap, Clear and Save — is in the `⋯` menu beside Copy, in both panes. The bar used to hold all
ten in one row of mixed icons and words, which read as noise at the moment someone is scanning
it for Previous. Copy stays out because it is the one gesture that gets a log into a bug report;
the three display toggles went in because they are set once and remembered (below). They are
CheckBoxes with a two-way `IsChecked` and no Command (UI rule 8b), and Clear and Save close the
menu as they run (`OnLogMenuActionClick`), which a `Flyout` does not do for a Button inside it.

## Local time, UTC one click away (FEAT-39)

With timestamps on, lines print `2026-07-20 10:41:02.114` in this machine's local time, no
offset (every line shares it). An **In UTC** box under Timestamps in the `⋯` menu is enabled only while timestamps
are shown; checked, the line prints the server's own RFC3339 token untouched, nanoseconds
and all, so it can be matched character for character against another system's log. Its
tooltip names the local offset. Local conversion is `DateTimeOffset.ToLocalTime()`
(through the `LogLineViewModel.ToLocal` seam the tests pin a zone with), never a
`TimeZoneInfo` lookup by id, which wants tzdata a NativeAOT binary on Linux may lack.
**Copy and Download always write `RawLine`** — the server's line, UTC — whatever the
display says; a converted timestamp pasted into an incident ticket is a second clock for the
next reader to reconcile.

## What is remembered, and what deliberately is not (FEAT-37)

Timestamps, UTC and Wrap are preferences: `AppSettings.LogShowTimestamps` / `LogTimestampsUtc`
/ `LogWrapLines` in `settings.json`, read when a pane opens and written through `App.Update`
when changed, in either pane. The next pane opens the way the last one was left.

**Nothing that changes which lines are read is remembered.** Previous is the case that
proves the rule: Freelens persisted it and had to take it back (freelens#2095, #2096),
because it made a crashed run's logs the default view of every healthy pod. The search text,
its mode and the level filter are not persisted either — a filter carried silently into the
next pane is a pane that looks quiet. `LogPaneTests` pins both halves, the negative one by
asserting no such setting exists.

## Which container (FEAT-38)

Every pane that picks a container on its own — pod detail, the multi-pod pane's one source
per pod, S on a pod row, workload detail's Shell — goes through `PodDetails.DefaultContainer`:
the container named by `kubectl.kubernetes.io/default-container` when the pod has one by that
name (in any of the three arrays, as kubectl looks it up), otherwise the first of
`spec.containers`. A mesh or log-shipper sidecar injected ahead of the app is what the
annotation exists for, and opening on the proxy's access log was the wrong first screen. An
annotation naming a container the pod does not have is ignored, as kubectl ignores it.
Pod detail keeps its own older refinement past that: with no annotation it opens on the
first *app* container rather than an init container that has already exited.

## A pod that never started (ENG-45)

An unscheduled pod answers a follow request with an immediate **204 No Content** (read on
the k3s 1.33 sandbox, along with the pod's shape then: no `nodeName`, no container statuses,
`PodScheduled=False/Unschedulable`), so its stream "ends" at once — cleanly, not as an
error, unlike a scheduled pod whose container is still being created, which is a 400 and
the pane's existing Failed path. `LogStreamEnd.DescribePod` now says what that means — "has not started — the
pod is not scheduled onto a node yet (Unschedulable)" — and returns `NotStarted`, which puts
the multi-pod pane's chip in its own **not started** state rather than "ended", and makes the
body repeat the chips' sentence. The demo cluster says it through the same method about its
own unschedulable `fraud-detector` pods, so the demo cannot teach a different sentence for
the same state; before this the chip read "ended" with 0 lines beside a body that disagreed.

Both panes also pick such a container up once it runs: the multi-pod pane re-opens a
not-started source on the pod watch's next Modified that no longer reads as never started,
and pod detail restarts its follow on the watch tick that shows the container running.
Neither re-opens on every status update of a still-pending pod.

## ENG-36 and ENG-38, briefly

- `LabelSelector.ForPodsOf` refuses a PersistentVolumeClaim (its selector picks volumes) and a
  prometheus-operator ServiceMonitor (its selector picks Services), so L, "Logs (all pods)" and
  the row logs icon are no longer offered on a claim. It is the one kind list in that file,
  and a refusal, so a mistake in it hides an action rather than offering a wrong one.
- Logs have one glyph, `LogsIconGeometry` (lines of text): the row icon, every palette Logs
  row, "Logs (all pods)", and the catalog's Logs / Previous logs / logs-palette commands. The
  clock stays where it means time — the timestamp toggle and the sidebar's Recent section.

## Verification

`LogPaneTests` (App tests) drives the real `Enqueue`/`Flush` of both panes: find vs filter,
next/previous wrap, the kept current match, the highlight ranges, levels with the unleveled
rule, clear in both panes, local/UTC display with a pinned zone and a real server's
nine-digit fraction, the persisted-and-not-persisted split, the default container, a PVC's
missing logs, and the never-started verdict on the demo's own pod. Mutation-checked: making
typing filter again (the pre-FEAT-33 behaviour), hiding unleveled lines, and ignoring the
annotation each turn their tests red. Screenshots: `cluster-tab-demo-pod-detail-find`,
`cluster-tab-demo-pod-detail-levels`, `cluster-tab-workload-logs-find`,
`cluster-tab-workload-logs-not-started`. Read from the sandbox: the 204 and the unscheduled
pod's shape. Not verified against a live cluster: the panes themselves on that pod, and the
pick-up once it is scheduled (the sandbox's one node was not made to accept it); the Levels
flyout open, which the harness cannot render; and the win-x64 NativeAOT publish, whose
trim/AOT analysis showed only the known DataGrid warnings but whose native link failed on a
full disk, so no `--smoke-test` of this change has run.

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
3. **The search matches the message, never the timestamp prefix.** `LogQuery` (through
   `LogLineViewModel.Matches`, which remembers the answer per query) is the one rule both
   modes, the highlight and the overview ruler use, and `LogLineText` skips the first
   `MessageOffset` characters of the displayed text — otherwise a search for "08:41" would
   light up text the counter does not count.
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

## A log viewer good enough not to leave (the log-viewer pass)

The owner's brief: nobody should copy a log into Notepad++ to find something in it. The
research behind what was built, with sources, is
[`docs/research/2026-09-28-log-viewers.md`](../research/2026-09-28-log-viewers.md). Everything
here is deterministic, works on the lines already in the pane, and needs no server change.
The shared half lives in `LogQuery`, `LogProjection`, `LogProblems`, `LogPins`, `LogSearch`,
`Controls/LogLineText` and `Controls/LogOverviewRuler`, so the two panes cannot drift; each
pane keeps only its own narrowing (pods, levels, Errors only) as `LogProjection`'s predicate.
`LogViewerTests` pins the behaviour.

- **Only the level keyword is coloured; errors and warnings mark the row.** See
  [log-severity-classes](log-severity-classes.md). Warnings get the bar without the wash:
  Dozzle took its amber wash away because a routine retry line was the noisiest thing in the
  stream. The timestamp prefix is dimmed (`LogLineText.PrefixBrush`).
- **Severity is read from what the logger printed.** The *earliest* level keyword wins, so
  `info: retrying after error` is an info line (it used to be red: the rule was "ERROR
  anywhere"); a structured level field (`"level":`, `level=`, `"LogLevel":`, `"@l":`) beats
  words in the message, and an explicit debug level is not an error; klog's `E0928 …` header
  is read; `fail:`/`crit:` and zerolog's `ERR`/`WRN`/`INF` count.
- **A stack trace takes the level of the line that threw it.** An unlevelled line that
  continues the one above it from the same pod (indented, `at `, `Caused by:`, `... N more`,
  Python's traceback header, a flush-left `x.y.SomeException:`) inherits an error or warning
  (`LogLineViewModel.InheritFrom`). The bar runs down the trace and Errors only keeps it
  whole; the error jump and the counts skip inherited lines, so a trace is one stop.
- **Regular expressions and match case,** as VS Code's find widget: `.*` and `Aa` in the box,
  Alt+R and Alt+C. The engine is `RegexOptions.NonBacktracking`, linear whatever is typed —
  the search runs on the UI thread over thousands of lines per keystroke, and a per-line
  timeout only bounds each line. Backreferences and lookarounds are refused in words; a
  pattern that does not parse filters nothing and says why in an error bar.
- **`!word` hides lines, in either mode** — k9s's `/!`, VS Code's output filter. Only when the
  box holds such a word is it split on spaces; otherwise the text is one phrase, as before.
  The counter adds "N hidden", so a pane that lost lines to an exclusion never looks quiet.
- **Filtering keeps context** — `grep -C`, the one thing a filter alone could not do, via the
  `Context` chip (0/2/5/10/25) that appears in filter mode. It is kept incrementally as lines
  stream in (`LogProjection`), context lines are dimmed, a gap between groups is a rule, and
  the counter counts matches, not the context around them.
- **A filtered line, double-clicked, is shown in the full log**: the filter comes off, the line
  becomes the current match with the cursor on it, and the query stays highlighted around it.
- **Errors and warnings are counted and jumped between, hiding nothing** (`LogProblems`): the
  `⊗ 3` / `▲ 12` chips in the bar, each only when not zero. A click lands on the latest, the
  next walks back (the find's order, for the find's reason); Alt+↑ / Alt+↓ anywhere in the
  pane. This is the counterpart of Levels and Errors only, which narrow the pane and so take
  away the lines that explain the error.
- **An overview ruler** beside the scrollbar (`LogOverviewRuler`) ticks every error, warning,
  match, pin and the cursor at the height its row sits — read from the rendered rows, so it is
  exact with wrapping on and a short log's ticks stay beside its lines. Rows are bucketed into
  pixel rows first, so four thousand lines cost as many rectangles as the strip is tall. A
  click scrolls to the nearest tick.
- **Pinned highlights** (Notepad++'s Mark, klogg's colour labels): the pin button or Alt+P
  keeps the current search as one of five colours, painted under the text and on the ruler,
  and empties the box for the next question. Chips above the log remove them. Not persisted.
- **A JSON line opens under itself**: a chevron on lines whose message is a JSON object shows
  it indented. A view, never a rewrite — the line, the search, Copy and Save keep the server's
  bytes (Headlamp's prettify rewrote its buffer and lost lines).

Not built, on the research's evidence: a histogram (only hosted tools with a stored log have
one), collapsing repeated lines (nobody asked), bookmarks (thin demand), and rendering the
application's own ANSI colours (they would compete with the level colour; see below).

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

The bar carries Range, Follow, Previous, Levels and Copy, plus the error and warning counts
when there are any and the Context chip while filtering. Everything else — Timestamps, UTC,
Wrap, Clear and Save — is in the `⋯` menu beside Copy, in both panes. The bar used to hold all
ten in one row of mixed icons and words, which read as noise at the moment someone is scanning
it for Previous. Copy stays out because it is the one gesture that gets a log into a bug report;
the three display toggles went in because they are set once and remembered (below). They are
CheckBoxes with a two-way `IsChecked` and no Command (UI rule 8b), and Clear and Save close the
menu as they run (`OnLogMenuActionClick`), which a `Flyout` does not do for a Button inside it.

**At a narrow window the bar gives way rather than running off the pane (ENG-51).** Pod
detail's bar shares its row with the five-tab strip, and below about 1150px the right-hand end
— the `⋯` menu with Clear and Save first — was cut off. `Views/LogBarOverflow` now moves, in
order and only as many as the row needs, **Range**, **Levels**, the filter's **context** chip
and then **Copy** into the `⋯` menu, where a copy of each waits hidden; once all four have gone,
the search box narrows from 230px towards 160px. Search, the problem counts, Follow, Previous
(the CrashLoopBackOff gesture) and the menu never move. Copy is last to go because it is the
gesture that gets a log into a bug report, and it went at all because the first cut, without
it, fitted Segoe UI and overflowed Linux CI's DejaVu Sans by 19px. Measured at the window's 960px
minimum: pod detail's bar needed about 240px more than its row had; it now moves all four and
the box is 201px in Segoe UI and 169px in a DejaVu Sans or Verdana face; at 1100px only Range moves; the multi-pod pane fits at 960px with nothing
moved. Each movable control sits in a `Panel` slot and the slot is what is hidden, because the
controls carry visibility bindings of their own (the context chip only while filtering, Levels
not on the Applications page) that a value set from code would replace. The harness's
`ux-log-bar-*` scenarios (960px and 1100px, with the problem counts showing, the grep shot with
the context chip, and both 960px bars again in the wider face, which `-wide-face` forces for one
capture) run `LayoutChecks.LogBarFits`: every tool ends inside the pane, the
box is at least 160px and clear of the tools, and each control is on the bar or in the menu,
never both. Before the change the first of them failed with the search box overlapping the
tools by 240px. Not checkable in the harness: the menu opened, with a Range combo box inside
it (a popup inside a flyout).

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

## Terminal colour codes are removed, not drawn

A container's stdout is often written for a terminal that is not attached: .NET's console
logger, zap, Rails and most CLI tools colour their output with ANSI SGR sequences, and the
kubelet stores those bytes verbatim. The panes drew them as text — a box glyph for ESC, then
`[40m[32minfo[39m[22m[49m` — at the start of every line of a real ASP.NET pod, ahead of the
words and in the way of the search. `TerminalEscapes.Strip` removes every ECMA-48 escape
(CSI, OSC and the other string sequences, two-byte escapes) and every other control
character except tab, once, in `LogLineViewModel`'s constructor, so `RawLine` is already
clean: the display, the search, the severity keywords and Copy/Download all see the same
text. A line with no control character is returned as the same string, so an application
that does not colour its output costs nothing.

They are removed rather than rendered as colour, on purpose. The pane's own colour is the
severity, and it means the same on every line; an application's colours would compete with
it line by line. And a line drawn as one bound string is what keeps selection, Copy and the
search highlight simple (`Controls/LogLineText`) — rendering colour means `Inlines` of runs
and giving all three up. If that trade is ever wanted, this is the decision to revisit.

**Bidi and zero-width characters are shown as markers.** They are Unicode *format*
characters, not controls, so the stripping above let them through: a right-to-left override
(U+202E) reordered the rest of its line, so a log line could be made to read as a different
one, and a zero-width space made two different strings look the same. A container's stdout is
written by whatever runs in it. `InvisibleCharacters.Reveal` (Core) replaces each of them with
a visible `⟨U+202E⟩`, right after the escape stripping, so the display, the search, Copy and
Download all see the marker — what is found and copied is what is shown. The set is the bidi
embeddings, overrides, isolates and marks (U+061C, U+200E/F, U+202A–202E, U+2066–2069), the
zero-width space, the word joiner and U+FEFF. The zero-width joiner and non-joiner (U+200C/D)
are left alone: they shape Persian and Indic text and join emoji sequences, reorder nothing,
and marking them would turn ordinary text into noise. The same helper marks an Event's reason,
object and message (the Events list and every Events tab), a CRD's printer-column cells, Argo
CD's health, operation and condition messages, and the Applications page's findings, reason
line and timeline; the YAML editor and the Helm viewers draw the same characters as an amber
marker box through `Editing/InvisibleCharacterGenerator`, without changing the document.
`InvisibleCharactersTests`, `UntrustedTextTests` and the harness's `ux-yaml-editor-links` /
`ux-helm-editor-links` pin it.

**A line has a length cap.** The log stream is read by `BoundedLineReader`, not
`StreamReader.ReadLineAsync`, which would hold a line with no newline in memory for as long as
it kept coming. A line past 1 MiB (`ClusterClient.MaxLogLineBytes`) arrives as its head, cut on
a whole character, followed by ` … [line cut at 1 MiB by kubeNimbus]`, and the rest of it up to
its newline is read and dropped without being kept. Line endings are the ones `StreamReader`
used (`\n`, `\r`, `\r\n`), so a progress bar drawn with carriage returns still splits the way
it did.

**.NET's console-logger levels are severities.** `Microsoft.Extensions.Logging` prints
`info:`, `warn:`, `fail:`, `crit:`. The first two already matched the INFO/WARN keywords once
the escapes were gone; `fail:` and `crit:` are read as Error, and only with the colon, the
shape that logger prints, so "tests fail" in a sentence is not an error line.
`LogLineCleanupTests` pins all of this.

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

## The last line clears the scroll bar

Fluent's scroll bars hide themselves and are drawn over the content rather than beside it, so
a pane scrolled to its end, which is where Follow keeps it, used to put the last line under
the horizontal bar, half hidden (reported by the owner, 2026-10-09). Both panes' scrolled
content has a 14px bottom margin, the bar's 10 (`ScrollBarSize`) and a gap, so the end of the
log always stops above the bar, whether or not the bar is showing. It is a margin on the
scrolled content and not padding on the card, because only space inside the scroll extent is
reached by scrolling to the end. The harness's `ux-log-end-gap-pod` and
`ux-log-end-gap-workload` scroll each pane to its end and fail if the last row ends closer to
the bottom edge than the bar is tall (`LayoutChecks.LogEndClearsScrollBar`); with the margin
removed both measured 0px.

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

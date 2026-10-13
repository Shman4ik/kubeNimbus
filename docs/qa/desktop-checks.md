# The desktop check suite

The checks a `kn-qa` agent runs against a live kubeNimbus in the QA VM, in the order they
are cheapest to run. It started as the 0.7.0 pre-release run (PRs #295, #296, #299, #302,
#304, #307) and is kept as the regression suite: **a new release adds the checks for its own
PRs under a new batch and retires none until the behaviour is gone.** How a run works, what
it costs and why it is built this way: [desktop-qa](../engineering/desktop-qa.md).

## Running it

```powershell
./scripts/qa-preflight.ps1        # ten seconds; fixes nothing, names what is missing
./scripts/qa-seed.ps1             # qa-shop, the qa-* pods, the qa-prod/qa-limited/qa-cut contexts
./scripts/qa-report.ps1 -Clear    # forget the last run's verdicts
```

Then one `kn-qa` per batch, one at a time (one desktop, one pointer), each told only:
the batch ID, the clone's path, and "read `docs/qa/desktop-checks.md` and
`.claude/agents/kn-qa.md`". The agent records every verdict with `scripts/qa-result.ps1`;
at the end `./scripts/qa-report.ps1` writes the report with the counts, the failures and the
UNSURE reasons on top. Nobody re-types a result.

Every batch starts with `./scripts/qa-app.ps1` and ends with `./scripts/qa-app.ps1 -Stop`,
also after a failure. `./scripts/qa-seed.ps1 -Reset` between batches puts back what the last
one changed; a batch says when it needs that.

## Kinds of check

The tag after each ID says what the agent can honestly decide, so it spends no time on what
it cannot:

- **[text]** the verdict is a quoted `find`/`dump`/`wait` line, clipboard value or kubectl
  line. The agent decides PASS or FAIL.
- **[input]** needs real mouse or keyboard (`click -Modifiers`, `drag`, `wheel`, `keys`),
  then reads the result as text. The agent decides.
- **[look]** a judgement of colour, alignment or a ring: the agent saves a cropped
  screenshot, records UNSURE with its path and moves on. The orchestrating session or the
  owner looks. Do not retry these.
- **[timing]** a state that lasts a fraction of a second; one honest attempt, UNSURE if not
  caught, and a view-model test is the better home for it.
- **[opt]** needs something this VM may not have (a second cluster, 150 % scaling, a second
  monitor); the preflight says whether it has.

## Rules for every batch

- Mutate only `qa-*` namespaces. The demo namespaces (`demo-shop`, `demo-broken`, …) are
  read-only. Never drain or cordon the node, never stop or recreate the sandbox, and never
  scale `metrics-server`: a check that would need any of them is UNSURE "not allowed on the
  shared node".
- Close what you open (terminals, Notepad, a browser, save dialogs) before the next check.
- A PASS quotes a line. A screenshot is evidence for a FAIL or a [look]; never the only
  evidence for a PASS.
- Clipboard checks: `Set-Clipboard 'QA-SENTINEL'` first; "unchanged" means it still reads that.
- `NamespaceButton`'s flyout and the sidebar's kind rows need a real `click`; `select` and
  `invoke` report success and change nothing. Make sure the namespace is `qa-shop`, not
  "All namespaces", before opening a workload's logs.
- Never open `qa-chatty`'s logs except in G-9 with the scrollback at 200.

## Batch C — dock and keyboard

Setup: Resources, Pods, namespace `qa-shop`. Both themes where noted (`ThemeToggleButton`).

- **C-1 [text][look]** (#293) Open three pods. Tab to a tab in `InspectorTabStrip`, then Right,
  Right, Left: the selected `TabItem` changes with each key (text). The focus ring, a
  semi-transparent blue ring about 2 px outside the tab, is a [look]: crop after each key, in
  both themes.
- **C-2 [text][input]** (#269) Open 10 or more tabs (pods, a ConfigMap, a Secret, a Deployment,
  an exec). The strip stays one row, about 30 DIP high, and the dock content starts under it.
  `wheel -Id InspectorTabStrip -Delta -3` (the plain wheel) and `-Horizontal` both scroll it
  (#313: the 0.7.0 run found only the horizontal wheel did). End on a focused tab selects the last tab.
  A thin line across the selected tab's bottom edge with the pointer away is a [look].
- **C-3 [input]** (#266) Exec on a `shop-api-…` pod (S), click into the terminal, then
  `drag -Id DockSplitter -DY -100` and `-DY 100`. With no click, `echo hi` still reaches the
  shell. The terminal text is not in the UIA tree: prove it with `echo hi > /tmp/m` and
  `kubectl exec … cat /tmp/m`.
- **C-4 [look]** (#293, PR #286) Scale… on `shop-web`: the word "to" sits on the number's
  baseline, also at a width where "to [N]" shares the sentence's first line. Cancel; nothing
  is scaled (`kubectl get deploy shop-web`).
- **C-5 [input]** (PR #263) With three dock tabs, click the front tab's close button: it is
  gone, a neighbour is in front, the dock is not empty.
- **C-6 [input]** (PR #263) Focus the sidebar's Pods row with Tab from ☰ (and, separately, with
  a click). Down then Enter opens the next kind; Home then Enter folds the first section
  (Recent, once kinds were opened), Enter again unfolds it.
- **C-7 [text][look]** (#213, #244) No row in Workloads, Network, Config, Storage, Cluster or
  CRDs draws an icon before its name, nor `EndpointSlices`/`Events` under `events.k8s.io`;
  after selecting four or five kinds the Recent rows do.
- **C-8 [text][look]** (#297) Open a `shop-web-…` pod. Tab to its Overview/Logs/… strip, then
  Right, Right, Left, End, Home: the selected segment changes with each key (text), and the
  focus ring sits on the segment each key reached, never on none (a [look]: crop after each key,
  both themes). Then ⚙ → Preferences, Tab to the General/Appearance/… tabs, Right and Left:
  the same, with the tab's page following.
- **C-9 [look]** (#215, #237) A pod's Overview and a node's Overview (`kubenimbus-sandbox`'s
  node): SCHEDULING, CONDITIONS, TOLERATIONS, PROBES (and the node's four) are headers with no
  border or fill around their rows; each condition's dot sits left of the CONDITIONS header and
  its type starts directly under the header's first letter. Crop the dock, both themes.
- **C-10 [look]** (#215) Logs, Exec (S on a `shop-api-…` pod), E on a Deployment and Review… a
  change to its replicas, and a Helm release's Values tab if the cluster has a release: none draws a bordered box inside the
  dock; the apply preview (the editor hidden while it is open) is set off from the pane's toolbar by a single line above it. Crop the dock,
  both themes.
- **C-11 [text][input]** (#236) Open a second cluster tab (the demo cluster). `dump` shows a
  `Tab 'Clusters' [TabStrip]` holding one `TabItem` per cluster, named by the cluster, the one in
  front selected. Tab from ☰ reaches the strip as one stop; Right brings the next cluster to the
  front (the cluster switches), Left back. The focus ring on the focused tab is a [look].
- **C-12 [input]** (#236) Delete on the focused demo cluster tab closes it and leaves focus on the
  tab now in front. With three dock tabs open, Tab to the strip and press Delete: the focused tab
  closes, a neighbour is in front and has the focus; Delete again closes that one. No close
  button is a Tab stop.
- **C-13 [text]** (#236) `dump` the sidebar: each section header is a `Group` named by its title
  with the ExpandCollapse pattern, state Expanded for Workloads and Collapsed for Config; UIA
  `collapse` on Workloads folds it on screen and `expand` unfolds it.
- **C-14 [text][look]** (#317) Select Services, ConfigMaps, then Pods from the sidebar. Exactly one
  sidebar row is drawn selected (Workloads/Pods), not its copy in Recent. Fold Workloads: the
  Recent copy of Pods is drawn selected instead; unfold: back to Workloads. Then click Recent's
  ConfigMaps (Config folded): only the Recent row is drawn selected. Crop the sidebar each time.
- **C-15 [text]** (#236) Open the namespace picker: each namespace's box is a `CheckBox` named by
  the namespace, Off or On as drawn; UIA `toggle` on `qa-shop`'s box adds it with the picker kept
  open, and again takes it out. Escape closes the picker.
- **C-16 [text]** (#236) `dump` the Pods list: each `DataItem` row is named "<pod>, qa-shop";
  select two rows (click, Ctrl+click) and both report the item status "Selected", the others none.
  Right-click a Deployment row: the menu item reads `MenuItem 'Logs (all pods)'` (its help text is
  the longer tooltip), and the column chooser's items are named by their column.

## Batch L1 — selection and bulk delete

Setup: `qa-seed.ps1 -Reset`; Pods, `qa-shop` only; `qa-del` in `RowFilterBox`. Shift/Ctrl
clicks are `click -Modifiers shift|ctrl`. Rows have no selected flag in the tree; the
"N selected" text left of the search box is the evidence.

- **L-1 [input]** (PR #299) Click `qa-del-a`, Shift+click `qa-del-c`: "3 selected".
- **L-2 [input]** Ctrl+click `qa-del-b`: "2 selected"; again: "3 selected".
- **L-3 [input]** Delete: one strip "Delete 3 Pods in qa-shop on kubenimbus-sandbox", the three
  lines, red Delete and Cancel; `kubectl` still shows all three.
- **L-4 [input]** (#156) Cancel; right-click `qa-del-b` with three selected: still "3 selected",
  menu Delete arms the same three-pod strip. Click only `qa-del-a`, right-click `qa-del-c`: the
  menu acts on `qa-del-c` ("Delete Pod qa-del-c in qa-shop on …").
- **L-5 [input]** Select all three, Delete, confirm: each line "Deleted", "Deleted 3 Pods.",
  Close dismisses, rows go (pods take about 30 s to leave Terminating), `kubectl get pod
  qa-del-a` NotFound.
- **L-6 [input]** (PR #286) `qa-seed.ps1 -Reset`. Preferences → Changes → "Confirm before
  deleting" off. Context `qa-prod`: production border; two rows, Delete still asks, sentence
  ends "(production)". Deployments `shop-web`, Scale…: "Scale Deployment shop-web in qa-shop on
  qa-prod (production) from 3 replicas (3 running) to" with the box right after; 30 shows
  "… is 10× the current …" and Scale stays enabled; 0 shows the warn InfoBar. Cancel; turn
  the preference back on.
- **L-7 [input]** Context `qa-limited`: select the three, Delete, confirm: a and b "Deleted"; c
  `pods "qa-del-c" is forbidden: User "system:serviceaccount:qa-shop:qa-limited" cannot
  delete resource "pods"…`; "Deleted 2 of 3 Pods. 1 refused — each one's reason is in the list
  below."; `qa-del-c` still exists. Then `qa-seed.ps1 -Reset`.
- **L-8 [input]** (#156) Double-click a `shop-web-…` pod: tab `Pod/shop-web-…`; a Deployment:
  `Deployment/shop-web`; a ConfigMap: its YAML editor tab.
- **L-9 [input]** (#156) NamespaceButton → `qa-shop` row: flyout closes, button reads
  `qa-shop`. Its check box on `demo-shop` (tooltip "Add this namespace to the list, or take it
  out", not in the UIA tree: `click -Id NamespaceList -OffsetX … -OffsetY …`): flyout stays,
  button reads both names. "All namespaces" reads "All namespaces".
- **L-15 [input]** (#305) Select `qa-del-a` to `qa-del-c` ("3 selected"), right-click inside the
  selection: Logs, Previous logs, Exec, Port-forward and Edit YAML are present and disabled,
  Delete… is enabled. Hover Edit YAML: tooltip "Acts on one object, and 3 rows are selected.
  Select one row to use it; Delete acts on all of them." (`help=`). Press E and L with the three
  selected: no tab opens. Click one row: the items are enabled again.
- **L-16 [input]** (#305) `qa-seed.ps1 -Reset`. Preferences → Changes → "Confirm before deleting"
  off, context `kubenimbus-sandbox` (not production). Select `qa-del-a` alone, Delete: it is
  deleted on the press, no strip question. `qa-seed.ps1 -Reset`; select `qa-del-a` and `qa-del-b`,
  Delete: the strip asks ("Delete 2 Pods in qa-shop on …", red Delete and Cancel) and `kubectl`
  still shows both. Cancel; turn the preference back on.
- **L-17 [look]** (#305) In L-7's run at 1280 px wide, the refused line's sentence wraps under
  itself and is not cut with "…" before "cannot delete resource". Crop the strip.

## Batch L2 — columns and the Service row

Setup: Pods in `qa-shop`.

- **L-10 [input]** (PR #299) Right-click the Restarts header: a column menu, not the row menu,
  Name ticked and disabled, "Reset columns" last.
- **L-11 [input]** Untick Restarts: gone; Deployments then Pods: still gone; the profile's
  `workspace.json` has `HiddenColumns` with `restarts`. "Still hidden after a restart" is
  UNSURE: `qa-app.ps1` starts every run on a fresh profile.
- **L-12 [input]** Sort by Status, `drag` a column edge, then Reset columns: Restarts is back,
  the width is the default, the sort arrow stays.
- **L-18 [input]** (#301) Click the Age header (arrow on Age), then untick Age in the column
  menu: Age goes and no header shows an arrow; the rows are back in arrival order. Untick
  Restarts with the list sorted by Status: the Status arrow stays.
- **L-13 [text]** Ctrl+K "reset the list": "Reset the list's columns" appears only while a
  column is hidden or dragged.
- **L-14 [input]** (#280) Services, `shop-web`, F: tab `Forward: svc/shop-web:80`; the row menu's
  Port-forward does the same. Do not press Start. An ExternalName Service does not exist in
  the sandbox: UNSURE.

## Batch Y — YAML editor, New YAML, apply preview

Setup: Resources, `qa-shop`. The editor's text is not in the UIA tree: type or paste with the
real keyboard and the clipboard (click in, Ctrl+A, Ctrl+V), read it back through the clipboard.

- **Y-1 [text]** (PR #299) ☰ → "New resource from YAML…": tab "New YAML", empty, InfoBar "Paste or
  type one manifest, then Review… applies it to kubenimbus-sandbox. A namespaced object without
  metadata.namespace goes to qa-shop."
- **Y-2 [text]** E on a pod, then New YAML twice: the pod and "New YAML 2", "New YAML 3" are
  separate tabs and none is replaced (plus any earlier tab).
- **Y-3 [text]** A ConfigMap `qa-l-test` with no namespace, Review…: "This object is not on the
  server. Applying would create it:"; Apply: `kubectl -n qa-shop get cm qa-l-test`, tab
  `ConfigMap/qa-l-test`, InfoBar gone, Reload and Delete enabled. `kubectl delete` it.
- **Y-4 [text]** (#177) A minimal Deployment `qa-defaults` (one busybox container, `sleep 3600`,
  no strategy), Review…: scroll the preview and quote two server-defaulted lines
  (`revisionHistoryLimit: 10`, `strategy:` / `type: RollingUpdate`, `terminationMessagePath`,
  `imagePullPolicy`) from the tree, not a picture. Cancel.
- **Y-5 [text]** Two documents separated by `---`: "Nothing was applied: this text holds more than
  one document (---)…"; nothing created.
- **Y-6 [text]** `apiVersion: example.com/v1`, `kind: Nope`: "Nothing was applied: this cluster serves
  no Nope in API group example.com…".
- **Y-7 [text]** "Explore demo cluster", ☰: "New resource from YAML…" disabled; Ctrl+K "yaml" lists no
  such row (the query "new yaml" matches nothing even on a real cluster; use "yaml").
- **Y-8 [input][look]** (#173) On `shop-api`: E, change one env value and add one with a ~200
  character value (not `replicas`: the objects were made with `kubectl apply`, and a replicas
  edit ends in a field-manager conflict, which is Y-12), Review…. The mode strip's
  `Diff`, `Split`, `Fields` follow real clicks; hover a trimmed line in Split for its
  full-line tooltip (`help=` has the whole line); maximize the dock ("Maximize inspector",
  Esc restores) and repeat. At the default dock height say whether the diff is usable (0.7.0:
  about two lines, overlapping the footer; since #316 the diff takes the editor's place and
  shows about six lines, the footnote under them). Nothing applied: `kubectl get deploy shop-api`.
- **Y-9 [text]** (#300) New YAML, a ConfigMap `qa-y-rekey` with no namespace, Review…, Apply
  changes: tab `ConfigMap/qa-y-rekey`. ConfigMaps, `qa-shop`, select `qa-y-rekey`, press E: the
  same tab comes to the front and no second `ConfigMap/qa-y-rekey` tab opens. `kubectl delete` it.
- **Y-10 [text]** (#300) E on ConfigMap `shop-config` (or any `qa-shop` ConfigMap), paste a
  second document under the text (`---` then another ConfigMap), Review…: "Nothing was applied:
  this text holds more than one document (---)…"; `kubectl get cm` shows no second object and
  the first unchanged. Reload to discard.
- **Y-11 [look]** (#316) Y-8's Review at the default dock height: the editor gives way to the
  diff, about six lines show, the footnote sits under them, nothing overlaps. Cancel brings the
  editor back with the edit still in it. Crop the dock.
- **Y-12 [text][look]** (#316) On `shop-api`: E, change `spec.replicas`, Review…: the panel's
  headline starts "Apply conflict. A force apply would take these fields and make", the
  conflict sentence names `.spec.replicas` and its manager under it, the diff shows the replicas
  line, and the confirm reads "Force apply". No separate conflict card below. Cancel: the
  conflict card comes back with its own Force apply. Nothing applied: `kubectl get deploy
  shop-api` replicas unchanged. Reload to discard.

## Batch G1 — log panes

Setup: Pods in `qa-shop`; the log bar's ⋯ is `LogMenuButton` (`MenuCopy`, "Clear the pane",
"Save to a file…"), the search `LogSearchBox`. **At 960 DIP the bar keeps Copy visible and
`MenuCopy` is not in the menu**; to get it there make the pane narrower with `drag -Id
SidebarSplitter -DX 300` (and back).

- **G-1 [input]** (#288) A `shop-api-…` pod's logs, narrow pane. Sentinel; ⋯ → "Copy the visible
  lines": the menu closes and the clipboard holds timestamped lines, the same format as the
  bar's Copy at a wide window.
- **G-2 [input]** ⋯ → "Clear the pane": the pane empties and the menu closes. ⋯ → "Save to a
  file…": a save dialog opens; cancel it.
- **G-3 [input]** `shop-web` row menu "Logs (all pods)" (its UIA name is the help text "Tail every
  pod this workload owns in one pane…"). ⋯ → Clear: "Cleared N lines — still following N pods; …"; once new lines arrive, the bar above
  them reads "Cleared N lines; the lines shown arrived since." (#319).
  Save: the dialog "Save aggregated logs", file name `shop-web-all-pods`; cancel.
- **G-4 [text]** (#290) The Range combo's tooltip: "How far back the log is read: a number of
  lines, a time window, or everything the node kept", no mention of the demo. On the demo
  cluster the combo is disabled with "Log range (unavailable for canned demo streams)".
- **G-5 [input]** (#291) Place the window (`move -X … -Y …`) and the dock (`drag -Id DockSplitter
  -DY 70`) so the open ⋯ menu hangs past the window's bottom edge; `click -Match "Clear the
  pane" -Type Button` prints `clicked (real mouse)`, not a refusal. (0.7.0 failed: the check
  looked for a popup window that UI Automation does not expose.)
- **G-6 [input]** (#170) `Logs/shop-web`: a chip off gives "2 of 3 pods · …" and that pod's lines
  leave; after 20 s on, its lines from the hidden interval are back (clipboard timestamps).
  "Follow every pod" off: three "ended" chips and the line count stops; on: it grows again;
  one click is one toggle. WARN in the search plus the funnel leaves only WARN lines; the bar's
  Copy puts pod-prefixed lines on the clipboard.
- **G-7 [text]** (#226) `shop-api-…`: each Range item loads lines that keep arriving with Follow on
  (compare two reads of `LogItems`' height 5 s apart). Follow off with "Last 5 minutes" and
  with "Last 200 lines": "Selected range loaded — this is a snapshot." in the bar above the
  lines (#319; 0.7.0 showed it only as the empty-pane placeholder). `qa-quiet` "Last 5 minutes": Follow on "No lines
  received yet in the last 5 minutes — following new output.", off "No lines in the last 5
  minutes."
- **G-8 [text]** (#226, #319) `qa-restarter`, Previous (P): "Previous logs loaded — this is a snapshot,
  not a live stream." in the bar above the lines, which show `started …` and `ERROR exiting`; Range "Last 200 lines" and "Last 5
  minutes" reload them.
- **G-9 [text]** (#226) Preferences → Logs and metrics → Log scrollback 200, **then** open
  `qa-chatty` (about 20 lines a second): "Older lines were trimmed at the 200-line scrollback
  limit." within about ten seconds. Close the tab; scrollback back to 4000. A workload pane
  with a failed or pending pod that also trims has no fixture: UNSURE.

## Batch G2 — logs against the live API server

Setup: `qa-seed.ps1 -Reset`; `./scripts/qa-netcut.ps1 start` for G-11.

- **G-10 [text]** (#168) `Logs/shop-web` open, then `kubectl -n qa-shop rollout restart
  deploy/shop-web`: new chips appear ("connecting", then none), old chips read "deleted"
  (tooltip ends "Pod deleted; its lines are kept.") once they leave the API, and the buffer
  keeps their lines (the summary's "N lines buffered from M pods"). The search matches line
  text, not pod names.
- **G-11 [input]** (#168) Switch to context `qa-cut`, open `Logs/shop-web`, then
  `./scripts/qa-netcut.ps1 cut`, wait 20 s, `restore`. The shown lines stay (count before and
  after); cut chips say "The connection closed, not the container — … Follow resumes the
  stream."; after the restore a deleted `shop-web` pod produces a new chip.
- **G-12 [text]** (#169) If the node has fewer than 50 pods: `kubectl -n qa-shop create deployment
  qa-many --image=busybox:1.36 --replicas=55 -- sh -c 'while true; do echo tick; sleep 5;
  done'`, "Logs (all pods)": "Streaming the first 50 pods. More match app=qa-many — narrow the
  selection or use a single pod's log pane for the rest." and 50 chips. Delete it at once.
- **G-13 [text]** (#184) `qa-gated`, a pod with `schedulingGates: [{name: kubenimbus.io/qa-hold}]`
  that logs a line every 2 s: its logs say "app has not started — the pod is not scheduled onto
  a node yet (SchedulingGated), so there is no log to read."; remove the gate
  (`kubectl patch … --type=json -p '[{"op":"remove","path":"/spec/schedulingGates"}]'`) and,
  with no click, "INFO gated pod line …" appears within about 30 s; a `container "app" in pod
  "qa-gated" is not available` or `is waiting to start` sentence may show in between, as a wait
  (not in warning colour). Repeat with `Logs (all pods)` on a Deployment whose pod is gated: the chip
  reads "not started", then streams. **0.7.0 failed:** the pane stayed on `container "app" in pod
  "qa-gated" is not available (400 Bad Request)` (#184; fixed by #309's retry, and the rule is a
  live test, `GatedPodLogsLiveTests`; what stays here is the panes' own wiring). Delete the pod.
- **G-14 [input]** (#222) Ctrl+Shift+L: the palette opens prefilled `logs `; "shop-web" and the pod
  row streams (a line under 10 s old), "Logs: Deployment/shop-web" opens the multi-pod pane. The
  chord in a YAML editor opens it. In an exec terminal it does **not** (owner decision on #222:
  the terminal keeps the chord; 0.7.0 observed it firing there): see T-9. Ctrl+Tab out, then it opens.
- **G-15 [text]** (#225) "Logs" and "Logs, maximized" from workload detail's Pods tab, "Logs" from
  node detail's Pods tab (read only, no Shell item) and from an Event about a `qa-shop` pod each
  open that pod's streaming logs.
- **G-16 [input]** (#225) The "pod gone" notice ("Pod qa-shop/<name> no longer exists — it was
  deleted or replaced since this was listed.") needs a row that outlives its pod, which a
  real click cannot catch: UNSURE unless a fixture holds the row. On `qa-limited` a pod's logs
  show `pods "…" is forbidden: User "system:serviceaccount:qa-shop:qa-limited" cannot get resource
  "pods/log" … (403 Forbidden)`.
- **G-17 [text]** (#331) `qa-gated` again (gated, logs a line every 2 s). Open its logs, turn
  Follow off, pick the range "Last 5 minutes": the pane shows the not-started sentence as a wait.
  Remove the gate: within about 30 s the pane shows the pod's lines and "Selected range loaded —
  this is a snapshot.", and Follow stays **off** (no new line arrives in the next 10 s). Before
  #331 it began following. Delete the pod.
- **G-18 [text]** (#331) `Logs (all pods)` on `shop-web`, Follow off. `kubectl rollout restart
  deploy/shop-web -n qa-shop`: each new pod's chip reads "loaded" (a snapshot), never no word
  at all (how a streaming chip looks), and the status line keeps "Log streaming is stopped. Press Follow to start it."
  Press Follow: every chip streams.
- **G-19 [text]** (#332) `Logs (all pods)` on `shop-web`, Follow on, then `kubectl rollout restart
  deploy/shop-web -n qa-shop` three times, waiting for each to finish: every old pod's chip ends
  "deleted" with "Pod deleted; its lines are kept." on hover, never "failed" with "the server could not
  find the requested resource". One attempt per restart; the 404 is a race the VM may not hit,
  so a run that never shows "failed" is a PASS.
- **G-20 [text]** (#341) Open a `shop-web` pod's logs in pod detail, Follow on, then
  `kubectl -n qa-shop delete pod <that pod>`: the pane ends with "Pod deleted; its lines are kept."
  in an ordinary notice (not in warning colour), and the lines it had stay. Never "the server could
  not find the requested resource" as a problem. The 404 is a race the VM may not hit (the stream
  may end cleanly first, with the container's exit); one attempt, and a run that never shows the
  404 as a problem is a PASS.

## Batch T — terminal, exec and copy

Setup: exec = S on a `shop-api-…` row (busybox `sh`). The terminal's text is not in the UIA
tree, and `qa-ui.ps1` has no terminal element: aim at the dock pane with `-OffsetX/-OffsetY`,
and read what ran through the clipboard or a marker file (`echo hi > /tmp/m`,
`kubectl exec … cat /tmp/m`).

- **T-1 [input]** (#265) Sentinel; right-click the terminal → "Select all", `keys ^+c`: the
  clipboard holds the screen text with no trailing blanks.
- **T-2 [input]** (#265) `ls /`; `drag` across two output lines, `keys ^+c`: the clipboard holds exactly
  those two lines.
- **T-3 [input]** (PR #295) `echo hi` prints `hi`; `sleep 100` then Ctrl+C returns the prompt in under
  2 s. Alt+B / Alt+F (#315): run `cat -v`, press Alt+B, Alt+F, Alt+Shift+B, then Enter: the line
  echoed back reads `^[b^[f^[B` (0.7.0 saw `^[B^[F`, uppercase). Ctrl+C. Busybox `ash` has no
  word motion, so the cursor half needs a readline shell: where a pod runs `bash`, type `echo one
  two`, Alt+B moves the cursor to the start of `two` and Alt+F back to the end, with nothing
  inserted; UNSURE when no such pod is allowed.
- **T-4 [text][look]** (#278, #290) Preferences → General → Terminal = `wt`; ☰ → "Open a terminal on
  this cluster": PowerShell opens, notice "Your preferred terminal was not used — Preferred
  terminal (wt): Windows Terminal opens its tabs from a process of its own …"; its severity colour
  is a [look]. Close the window.
- **T-5 [text]** (PR #295) Clear the field: the notice does not say "preferred". Close it.
- **T-6 [text]** (#289) A `context-*.kubeconfig` exists under `<profile>\terminal\`; nothing new in
  `%APPDATA%\kubeNimbus\terminal` (the folder does not exist).
- **T-7 [input]** (#189) `Set-Clipboard "echo one`necho two"`, Ctrl+Shift+V: the card "Paste 2 lines? This shell
  has not turned on bracketed paste, so each line runs as soon as it arrives." with `Paste` focused
  and `Cancel`; nothing ran (marker files). Esc closes it. A one-line paste has no card.
- **T-9 [input]** (#222) In the exec terminal type `echo abc` (no Enter), then Ctrl+Shift+L: no
  palette opens and the screen clears to the prompt with `echo abc` still typed (`^L`). Ctrl+K: no
  palette opens. Ctrl+P: the cluster switcher does not open. Then Ctrl+Tab out of the terminal and
  Ctrl+Shift+L: the palette opens prefilled `logs `. Close it.
- **T-8 [opt]** (PR #286) The cordon one-click strip: not allowed on the shared node. UNSURE.
- **T-13 [input]** (#337) In the exec terminal run `cat -v`, then press F1, F5 and F12, then Enter:
  no cheat sheet opens and the line echoed back reads `^[OP^[[15~^[[24~`. Ctrl+C. Then Ctrl+Tab out
  of the terminal and press F1: the cheat sheet opens. Close it with its ✕.
- **T-11 [input]** (#198) Right-click a `shop-api-…` row: "Exec in my terminal" sits under Exec,
  no ellipsis. Click it: a PowerShell window opens in the container (`hostname` prints the pod
  name), and the notice above the list says the terminal opened. Close the window. With
  `qa-del-a` to `qa-del-c` selected, the item is disabled and its tooltip is L-15's sentence.
- **T-12 [input]** (#198) Nodes, right-click the sandbox node: "Node shell in my terminal" is
  present (it is absent on a pod row). Read its tooltip only; do not click it (it starts a
  privileged debug pod on the shared node).

## Batch P — panes

- **P-1 [text]** (#268, #172) `demo-broken/never-ready-…` Overview Conditions rows show an age; its
  tooltip is "Last transition YYYY-MM-DD HH:MM:SS UTC-07:00" (local time with its offset, #320;
  the VM's own offset, "UTC" when it is zero). A row's Age tooltip ("Created …") and an event's
  "Last seen …" carry the offset too, and so do an application page's exit line ("… at
  HH:MM:SS UTC-07:00"), its timeline's "now" tick and a timeline mark's tooltip. Same on the node's
  Overview and "Since" in `shop-web`'s Conditions tab.
- **P-2 [text]** (#171, #320) A `shop-web-…` pod, dock not maximized, Usage tab: the pod's CPU and
  memory totals side by side, and BY CONTAINER's first container on screen with no scroll. `web`
  "request 10m · limit 200m · N% of limit" with N a figure such as `0.4%` (not `<1%`; `<0.1%`
  only under 0.05 %), memory "request 24 MiB · limit 128 MiB · N% of limit", `access-log-tailer`
  "request 5m · no limit"; within rounding of `kubectl top pod -n qa-shop --containers`.
- **P-3 [text]** (#166) Backups are cluster-scoped. `kubectl apply` a `Backup` `qa-last-run` with
  `status.lastRunTime` now (UTC `yyyy-MM-ddTHH:mm:ssZ`; the CRD has no status subresource):
  "Last run" reads `4s`, later `29s`, `1m` with no refresh, in step with Age. Delete it.
- **P-4 [input]** (#167, #320) `Widgets` in `demo-shop`, then `Backups`, window 960 DIP: no CRD
  header overlaps another, and a header cut short ends in an ellipsis with its whole name on a
  tooltip (Age may sit off to the right). Press a header a few pixels left of its resize grip and
  drag 30 px: the column resizes or nothing happens, the sort does not change. At 1280
  `drag` a CRD header edge: it resizes **and holds after switching kind and back**
  (0.7.0 failed with an injected drag; re-test with the real `drag`). Three header clicks
  cycle ascending, descending, none; dragging a header onto another moves nothing. The
  overflow-at-1280 half has no sandbox CRD wide enough: UNSURE.
- **P-5 [text]** (#176, #320) Double-click the node: tabs Overview, Pods, Events, Usage; the
  Overview's pods row reads "Active pods N / 110" and, when finished pods sit on the node, the
  header "M pods on this node, N active" (the plain count otherwise), with the same N; the Pods tab
  follows a `kubectl scale` with no refresh; "Re-read the events about this node" works.
  Right-click → "Drain…" (UIA name is its tooltip) arms the strip; **never press Drain**; tick and
  untick "Evict unmanaged pods" and "Delete emptyDir data" and quote the plan line each time
  ("18 pods will be evicted …", 24, 26, 20, 18 in 0.7.0). Stopping a running drain: UNSURE.
- **P-6 [text][input]** (#183) The `shop-web` Service pane: `kubectl scale deploy/shop-web
  --replicas=2` is followed with no refresh (the HPA, pinned at 3, puts it back within seconds).
  Dropped watch: on `qa-cut`, `qa-netcut.ps1 cut` for 20 s: an error InfoBar "Could not read the
  pods: Watch connection lost (…); retrying in …", and after `restore` the verdict again with no
  click. Ingress `qa-shop/shop`: Copy puts `http://shop.sandbox.local/` on the clipboard, footer
  "Copied …"; Open starts a browser (an Edge already running in the background hides the launcher
  process: take the new window's title). Close it.
- **P-7 [text]** (#224) `demo-broken/unschedulable`'s FailedScheduling "Last seen" tooltip: "Last
  seen … · first seen … (N times)", matching `kubectl get events -n demo-broken -o wide` within a
  minute; an image-pull event renders and double-click opens its object. The fleet half needs a
  second cluster: [opt].
- **P-8 [input]** (#191) `qa-tick` row → Suspend fires on one click: "qa-tick is suspended. Jobs
  already running carry on; …" and `kubectl … -o jsonpath='{.spec.suspend}'` is `true`. Resume
  asks ("Resume CronJob qa-tick in qa-shop on …"), confirm: "qa-tick is scheduled again.". On
  `qa-limited` Suspend fails with `cronjobs.batch "qa-tick" is forbidden: … cannot patch resource
  "cronjobs" … (403 Forbidden)` and `.spec.suspend` is unchanged. The strip's Close has the
  caption Close's name: aim by bounds.

## Batch K — Argo CD on the demo cluster

Setup: "Explore demo cluster". Nothing is mutated.

- **K-1 [text]** (PR #302) Argo → Argo CD, right-click `notification-dispatcher`: the Terminate item
  (UIA name "Stop the sync Argo is running, where it is") is enabled; on `checkout` it is disabled.
- **K-2 [text]** The strip "Stop syncing Application notification-dispatcher in argocd on Demo cluster",
  "Argo terminates the sync where it is and records it as Failed. What it already applied stays
  applied…"; "Stop syncing" disabled, the demo notice shown; Cancel closes it.
- **K-3 [input]** (#191) Applications mode, `notification-dispatcher`: a real click on `PART_SecondaryButton`
  ("More sync options: sync with prune, terminate a running sync") shows both items, Terminate
  enabled; on `checkout` it is disabled.
- **K-4 [text]** Ctrl+K "terminate" with `notification-dispatcher` selected: "Argo CD: terminate sync…,
  argocd/notification-dispatcher · stop the running sync where it is"; none with `checkout` selected
  (check the selection first: a click on a row's text does not always select it).
- **K-5 [text]** ApplicationSets, `payments-services`: tabs Overview / Applications / Conditions; "5
  generated · 3 out of sync · 2 degraded or missing"; `fraud-detector` first, `checkout` last; each
  chevron opens that Application's pane; three green condition dots [look].
- **K-6 [text]** AppProjects, `payments`: five cards; "Every namespaced kind except the deny list
  below."; "Matches applications *. Manual syncs are still allowed."
- **K-7 [input]** Applications, double-click `checkout`: the "Argo/checkout" pane, not the YAML;
  right-click → "Edit YAML" opens `Application/checkout`.

## Batch S — preferences

- **S-1 [input]** (#159) Write a one-context copy of the kubeconfig (`qa-prod` renamed
  `qa-extra-prod`) to `$env:TEMP\kubenimbus-qa\extra.yaml`; Preferences → General → "Add file…", then
  `set-text -Dialog -Id 1148 -Text <that path>` and `invoke -Dialog -Id 1` (#314; if `-Dialog` finds
  no dialog, say so in the verdict and fall back to clicking the name field and `SendWait`): Ctrl+P lists `qa-extra-prod` at once; Remove takes it out, the file stays.
- **S-2 [text]** (#159) Appearance → Theme → Dark recolours at once with no OK button; `settings.json`
  has `"Theme": "dark"`; back to System.
- **S-3 [opt]** (#159) Migration from a pre-migration `workspace.json` needs a prepared profile;
  `qa-app.ps1` always starts on a fresh one: UNSURE.

## Batch W — Windows chrome (150 % scaling, two monitors) [opt]

Runs only if the owner has set the VM's display to 150 % (and added a second monitor). The
preflight's `display` line says what it is; otherwise both are UNSURE "the VM does not allow it".

- **W-1** (#157) The three `PART_*` caption buttons are fully visible and do not overlap the palette,
  theme or preferences buttons at 960 DIP; dragging an empty part of the command bar moves the
  window; double-click maximizes and restores; a maximized window is not clipped at the top;
  hovering Maximize opens Snap Layouts.
- **W-2** (#157) The same two points after moving the window to the other monitor.

## Batch A — a real Argo CD [opt]

Runs only if the owner has the Argo sandbox up (`sandbox-up.ps1 -Name kubenimbus-argo -Port 6551
-SkipApps -Kubeconfig .sandbox/argo-kubeconfig.yaml`, then `argo-sandbox-up.ps1`) and its
kubeconfig is merged into the run's. Otherwise all nine are UNSURE "no Argo sandbox".

- **A-1** (PR #302) "All clusters": a Cluster column first, "2 of 2 clusters run Argo CD", counts equal
  the two dashboards' sum; Sync on the other cluster's row names that cluster and lands there.
- **A-2** (PR #302) Application `qa-slow` (path `slow`): Sync, reload until Running, "Terminate sync…" →
  "Stop syncing": "Terminate of the sync on … requested"; after reload the last operation is
  Failed, "Operation terminated".
- **A-3** (#191) On an `app`/`with-removed` Application, Sync and "Refresh from Git" fire on one click
  with an in-flight line then the result; "Sync with prune…" asks and its confirm prunes.
- **A-4** (#190) `qa-tenant` in `argo-tenant`: "Open in Argo CD" is enabled with a tooltip naming
  `argocd.sandbox.test` and namespace `argocd`; clicking starts a browser for
  `https://argocd.sandbox.test/applications/argo-tenant/qa-tenant`. An Application in a namespace
  `qa-rogue` with its own `argocd-cm` (`url: https://argocd.attacker.test`) and
  `status.controllerNamespace: qa-rogue`: no link.
- **A-5** (#154) In Argo's own UI the A-3 sync shows "Initiated by" `system:admin (kubeNimbus)`. The
  owner signs in; the agent never types the password.
- **A-6** (#224) Events, "All clusters": double-click an event from the Argo cluster; the tab title
  carries "· kubenimbus-argo".
- **A-7 [text]** (#303) The Argo cluster's own dashboard open (not "All clusters"), an `app`/`with-removed`
  Application selected and its row scrolled into view. From a shell, `kubectl patch application <name>
  -n argocd --type merge -p '{"operation":{"sync":{"revision":"with-removed"}}}'`: with no click and
  no reload, the row's tooltip "Last operation:" reads Running and then Succeeded within about a
  minute, and the selection stays on that row. `kubectl delete application <name> -n argocd`: the
  row leaves and the counts drop by one.
- **A-8 [text]** (#336) "All clusters" on the dashboard with the Argo cluster and the sandbox both
  connected. Repeat A-7's `kubectl patch` on an Application of the Argo cluster: with no click and no
  reload, that row's "Last operation:" tooltip reads Running and then Succeeded, and no other row
  changes. Close the Argo cluster's tab: its rows leave the fleet list and the header no longer
  counts it.
- **A-9 [text]** (#339) A context for a ServiceAccount `qa-argo-narrow` in `argocd` with a Role on
  `applications` get/list/watch in `argocd` only (delete the three objects after). Its Argo CD
  dashboard shows "This account may not read Argo CD Applications" with the server's sentence
  (`… cannot list resource "applications" … at the cluster scope`), no spinner and no "Watch
  connection lost" line, within a few seconds, and still so a minute later. **0.7.x:** "Watch
  connection lost … retrying" over a spinner that never ended.

## Batch M — capacity strip, managed fields, failed reconnect

- **M-1 [timing]** (#206) Nodes: "1 node · Ready CPU 527m of 8 cores used (7%) · 330m of 8 cores requested
  (4%) Memory 1.08 GiB of 3.82 GiB used (28%) · 452 MiB of 3.82 GiB requested (12%)"; "usage:
  reading…" and "requests: reading…" show before the numbers and no figure is 0 before it is read.
  The strip appears fully populated within 25 ms in 0.7.0 (five 10 ms polls never caught
  "reading…"): one attempt, then UNSURE; a view-model test is the better check.
- **M-2 [text]** (PR #307) Usage figures change across polls with the CPU column; the usage tooltip
  is "Measured by metrics-server over its last window, against allocatable."
- **M-3 [text]** No strip over Pods or Helm releases; it returns over Nodes.
- **M-12 [text]** (#308) `kubectl create quota qa-quota -n qa-shop --hard=pods=50,services.loadbalancers=0`
  (a `qa-*` object; delete it after). Context `qa-limited`, Config → ResourceQuotas in `qa-shop`:
  the row's Status reads "Within quota", Details `pods: <n>/50, services.loadbalancers: 0/0`. Patch
  `pods` down to the current count (`kubectl patch quota qa-quota -n qa-shop --type=merge -p
  '{"spec":{"hard":{"pods":"<n>"}}}'`): within a few seconds "Full: pods" in error colour, and
  Unhealthy only keeps the row. If `qa-limited` cannot list resourcequotas, note the 403 sentence
  and repeat on the sandbox context.
- **M-13 [look]** (#308) Demo cluster, ResourceQuotas in `payments`: "Almost full: limits.memory 98%"
  (warn) and "Full: persistentvolumeclaims" (error) pills, both themes.
- **M-4 [text]** (PR #307) Create `qa-nodes-only`: a ServiceAccount, and a ClusterRole and ClusterRoleBinding
  of that name granting only `nodes` get/list/watch (two cluster-scoped `qa-*` objects; delete them
  after), a token, and a context built like `qa-limited`. Nodes shows "1 node · Ready CPU usage not
  permitted · requests not permitted Memory usage not permitted · requests not permitted"; the
  requests tooltip carries `pods is forbidden: User … cannot list resource "pods" … at the cluster
  scope`. **0.7.0 failed:** the usage half read "no node has reported usage yet" although
  `nodes.metrics.k8s.io` answered 403.
- **M-5 [opt]** No metrics-server (PR #307): needs it scaled to 0 in `kube-system`, which this run may
  not touch. UNSURE.
- **M-6 [text]** (#270) `shop-web`'s YAML (E): no `managedFields:` in the text (clipboard), the chip
  `Show metadata.managedFields…` left of Delete; pressing it shows `managedFields:` inside
  metadata, the tab is not edited, Reload does not warn. Both themes [look].
- **M-7 [text]** (#270) With the field shown add `qa-check: m7` to `metadata.labels` (not `replicas`);
  press the chip again: the field goes, the edit stays, Reload is enabled. Review… → Apply: the
  managers listed with the field shown are the old ones plus `kubenimbus`. `kubectl label deploy
  shop-web qa-check-` afterwards.
- **M-8 [input][text]** (PR #307, #236) A stray `{` in the text, then a **real** click on the chip:
  it stays unpressed and the status line reads "The managed fields can be shown again once the
  text above reads as one YAML document." Then UIA `toggle` on the chip: the same refusal, and its
  toggle state reads Off afterwards (0.7.0: On). Close the tab without applying.
- **M-9 [text]** (PR #307) A `qa-shop` ConfigMap's YAML has the chip; a Deployment's YAML on the demo
  cluster does not.
- **M-10 [input]** (#294) On `qa-cut`: Applications mode open, then `qa-netcut.ps1 cut`, then Reconnect: the
  failure view in both modes; switching kind in the Resources sidebar behind it shows no "Loading…"
  and starts nothing (no new "Watch connection lost"). `restore`, Retry: both modes load.
  Delete the `qa-nodes-only` objects and context when M-4 is done.
- **M-11 [text]** (#310) Context `qa-limited` (reads pods in `qa-shop`, no metrics): Pods in `qa-shop`
  has no CPU or Memory column, and the status bar reads "CPU and memory are hidden: this user may
  not read pod usage (pods.metrics.k8s.io is forbidden: …)." Switch to Deployments and back to Pods:
  the same, at once (the refusal is remembered; no "—" columns appear first). Reconnect the tab:
  asked once more, same result. **0.7.0:** the columns read "—" for ever.
- **M-14 [text]** (#339) Context `qa-limited`, Pods with All namespaces: within a few seconds the
  list reads "This account may not list Pods", the server's sentence (`pods is forbidden: … at the
  cluster scope`) and "Choose a namespace this account can read from the namespace picker." No
  spinner, no "Watch connection lost" line, and still so a minute later. Choose `qa-shop`: the pods
  load.

## Batch Q — the QA driver itself

Setup: a fresh `qa-app.ps1` start; these check `qa-ui.ps1`, not the app (#314).

- **Q-1 [text]** Right after the start, `resize -Width 960 -Height 720`: it prints `resized:` with
  bounds near 960x720, no `SetWindowPos failed`. Then `move -X 40 -Y 40`: `moved:`.
- **Q-2 [text]** `select` on a sidebar kind row (`-Within SidebarItems`) whose selection the app does
  not take: the command fails with "Select changed nothing" rather than printing `selected:`, or
  the kind really opens. A `click` on the same row opens it. The same for a namespace-picker row
  in its open flyout.
- **Q-3 [text]** Preferences → General → "Add file…": `find -Dialog -Id 1148` lists an Edit;
  `set-text -Dialog -Id 1148 -Text <a kubeconfig path>`, `invoke -Dialog -Id 1`: the dialog closes
  and the file is in the list. With no dialog open, `find -Dialog -Id 1` says "No file dialog is open".
- **Q-4 [input]** G-5 again: `click -Id MenuCopy` on the log pane's ⋯ menu hanging past the window's
  bottom edge prints `clicked (real mouse)` and copies.

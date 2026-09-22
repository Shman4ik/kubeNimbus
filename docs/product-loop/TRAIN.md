# Release train — v0.4.0

Owned by `/release-train` (`.claude/skills/release-train/SKILL.md`). The owner steers
by editing **Config** or **Owner notes**; the next step applies it first.

## Config

| Key | Value |
|---|---|
| `MIN_ITEMS` / `MAX_ITEMS` | 5 / 10 |
| `CAPACITY` | 14 (S = 1, M = 2, no L) |
| `TRAIN_DAYS` | 4 |
| `MIN_DAYS_BETWEEN_RELEASES` | 2 |
| `MAX_FIX_ROUNDS` | 2 |
| `PARALLEL` | 1 |
| `RELEASE_MODE` | auto |
| `RESEARCH_FETCH_BUDGET` | 30 |

## State

Phase: BUILD · Branch: claude/relaxed-franklin-gssbew (session-designated; stands in for `train/v0.4.0`) · Selected: 2026-09-22 · PR: — · Started: 2026-09-22 · Last release: v0.3.3 (2026-09-22)
Baseline: first frame 99 ms (median of 3 warm runs: 99/99/95; cold first run 3529 ms, font cache), executable 51.3 MiB (53 769 168 B; payload with libSkiaSharp + libHarfBuzzSharp 64.6 MiB) · RID: linux-x64 · AOT warnings: the known DataGrid IL2104/IL3053 pair only
Sandbox: **API-server-only.** Docker Hub blobs 403, so `sandbox-up.sh` fails; native `k3s server` (GitHub binary + airgap images) comes up and the demo manifests apply (50-crds/51-custom-resources included, so VER-24 is paid), but `runc` cannot start containers here — no pod ever runs. Real: discovery, list/watch, CRDs, RBAC, SSA/dry-run, patches, evictions. Not real: logs, exec, port-forward, metrics. Must be re-started each session (see CURRENT_STATE.md → Environment notes).

## Owner notes

- 2026-09-22 (owner, in chat): **pin "quick access to logs" as the next thing built.** Asked
  which part, the owner chose all four: logs from the palette, a logs button on the row,
  logs from everywhere a pod is named, and logs opened full-size. Applied as L1–L3 below,
  built right after T1. To stay inside `CAPACITY` 14, T4, T5 and T8 move to reserve (the
  three lowest-scoring non-forced UX items; T9/T10 stay because this is the first session
  with a real API server).

## Items

| # | Source | Deliverable (user outcome) | Kind | Size | Score | Status | Rounds | Commit |
|---|---|---|---|---|---|---|---|---|
| T1 | research #1, headlamp#6974, k9s `Ctrl-z` | Show only what is unhealthy, on any list | UX | S | 4.05 | landed | 0 | fa4f30e |
| L1 | owner pin | Open any pod's or workload's logs from the palette, from anywhere | UX | M | pin | planned | 0 | |
| L2 | owner pin | One click to logs from the row, and logs opened full-size | UX | S | pin | planned | 0 | |
| L3 | owner pin | Logs from everywhere a pod is named | UX | S | pin | planned | 0 | |
| T2 | friction walk (`cluster-tab-events-list`) | The Events list reads like `kubectl get events`: last seen, type, reason, object, message | UX | M | 3.65 | planned | 0 | |
| T3 | Ready FEAT-31 (P1, forced) | Reach a container's whole retained log: tail and since controls | UX | S | 3.45 | planned | 0 | |
| T4 | research #6 + friction walk (`ux-workload-events`) | Events read the same everywhere, with relative times and a warning count on the tab | UX | S | 3.10 | reserve (owner pin displaced it) | 0 | |
| T5 | friction walk + Ready ENG-23 | The Namespace column appears only when it says something | UX | S | 2.85 | reserve (owner pin displaced it) | 0 | |
| T6 | Ready FEAT-34 (P1, forced) | Log follow survives a dropped stream and says so in place | Reliability | S | 2.60 | planned | 0 | |
| T7 | Ready FEAT-32 (P1, forced) | JSON log lines are readable: keys coloured, level lifted into severity | UX | M | 2.55 | planned | 0 | |
| T8 | research #3, headlamp#3222 | See a workload's revision history on its detail pane | UX | M | 2.50 | reserve (owner pin displaced it) | 0 | |
| T9 | Inbox VER-31 (P1, now payable) | Exec-plugin auth proven against a real API server | Verification debt | S | 2.25 | planned | 0 | |
| T10 | Inbox VER-13 + VER-36 (P1, now payable, API-server half) | Mutating actions and strict apply proven against a real API server | Verification debt | M | 2.00 | planned | 0 | |
| R1 | research #4 | Diff a revision against the current template (needs T8) | UX | S | 2.40 | reserve | 0 | |
| R2 | Ready ENG-22 | A running drain cannot be confirmed, by construction | Reliability | S | 2.30 | reserve | 0 | |
| R3 | Ready FEAT-56 | "Who am I on this cluster" beside the access review | UX | S | 2.20 | reserve | 0 | |

Capacity: 14 of 14 points after the owner pin (T1, T3, T6, T9, L2, L3 = 6 × S; T2, T7, T10,
L1 = 4 × M). Build order: T1, L1, L2, L3, then T2, T3, T6, T7, T9, T10. User-visible
workflow/UX: 7 of 10. Reliability/verification debt: T6, T9, T10. Reserve order: T8, T4, T5,
R1, R2, R3. (Originally selected: T1–T10 at 14 points; see Owner notes.)
`shared/nimbusUi`: none planned — an implementer that finds it needs a shared style must
say so rather than add one.

Scoring (1–5 each; Effort S = 2, M = 3; `Value = .20F + .20T + .15Tr + .15UX + .10R + .10C +
.10Conf`, `Score = Value − .25(E−1) − .25(Risk−1)`), the inputs that decided the order:
T1 F5 T5 Tr5 UX4 R5 C3 Conf4 E2 Risk2 · T2 F4 T4 Tr5 UX5 R5 C3 Conf5 E3 Risk2 ·
T3 F4 T4 Tr4 UX3 R4 C4 Conf5 E2 Risk2 · T4 F4 T2 Tr4 UX4 R4 C3 Conf5 E2 Risk2 ·
T5 F5 T2 Tr1 UX4 R5 C2 Conf5 E2 Risk2 · T6 F3 T3 Tr4 UX3 R4 C3 Conf4 E2 Risk3 ·
T7 F4 T3 Tr3 UX4 R3 C4 Conf4 E3 Risk3 · T8 F2 T4 Tr4 UX3 R3 C4 Conf3 E3 Risk2.
T9 and T10 score low on UX by construction and enter on the verification-debt rule: this is
the first session in the project's history with a real API server, and it may not recur.

Not taken, and why: **FEAT-24** (cluster issues panel, P1 in the Inbox, not Ready) — T1 is
its cheapest slice, and the refresh-model decision (watch, against Headlamp's move to
polling) deserves a train of its own once T1 shows how the predicate gets used.
**Rollback** (research #5) — two tensions for the owner (Helm rollback is deliberately out
of scope; Argo self-heal undoes a rollback), and T8 is its prerequisite anyway. **VER-1**
(P0, Ready, blocked) — infeasible here: it needs `linux-arm64`/`osx-arm64` runners or a
human dispatch of `release.yml`. **VER-12, VER-14, ENG-25** (Ready, P2) — carried; doc and
decision rows that score below every user-visible item. **Live passes over logs, exec and
port-forward** — the sandbox here cannot run a pod.

## Specs

### T1 — Show only what is unhealthy, on any list        (UX, S, score 4.05, source: research #1)
Problem + evidence:   Finding what is broken means opening a kind, choosing All namespaces and scanning or
                      sorting Status, once per kind; the row search deliberately does not match status (UI
                      rule 13). headlamp#6974: "no way to see what is unhealthy across a cluster without
                      opening objects one at a time"; k9s ships `Ctrl-z` "toggle faults" on every list.
Now → after:          "find broken pods in a namespace of 200": pick kind + scroll/sort + scan (3 + a scan)
                      → pick kind + one toggle (2), and the list holds only problems.
Acceptance:           - One toggle beside the list's search box (chip style, icon + tooltip naming the key),
                        plus a catalog command with a gesture and a palette entry, narrows the list to rows
                        whose computed health (`ResourceRowViewModel.StatusHealth` or equivalent) is not ok —
                        warn and error both shown. Works in fleet mode and composes with the text search.
                      - It filters `VisibleRows` only, never `Rows` (UI rule 13); a Modified event that makes
                        a hidden row unhealthy makes it appear, and one that heals a shown row removes it.
                        Pinned by tests in `tests/KubeNimbus.App.Tests` beside `ClusterTabRowFilterTests`.
                      - A kind whose rows carry no health verdict (no status summary) leaves the toggle
                        disabled with a tooltip saying why, never an always-empty list.
                      - Its own empty state: "Nothing unhealthy among N <kind>" with a way back — distinct
                        from `IsListEmpty` and `IsFilterEmpty`.
                      - Kept per tab across kind changes (it is a mode, unlike the text filter which clears);
                        not persisted to disk.
States:               loading (unchanged, the loading state wins), empty list, all-healthy empty state,
                      combined with text filter matching nothing, disconnected (unchanged), fleet partial.
Keyboard + entry:     catalog command (List scope or window binding — pick one that does not collide with
                      typing; k9s's Ctrl-z is a reasonable precedent, resolved through `Hotkeys`), palette
                      entry, the chip itself. Update `docs/keyboard-shortcuts.md` via the golden-file flow.
Verify:               App tests for the predicate + mirror invariants; screenshot scenarios for the toggled
                      list and the all-healthy empty state (both themes); sandbox: toggle on Pods in
                      demo-broken on the API-server-only cluster (every pod there is unhealthy — pods never
                      start here — so assert the healthy-row half with the demo tab instead).
Risk:                 breaking the Rows/VisibleRows mirror; the fix is to reuse the exact path the text
                      filter uses.

### L1 — Open any pod's or workload's logs from the palette, from anywhere        (UX, M, owner pin)
Problem + evidence:   Owner request (2026-09-22, "quick access to logs"). Today logs need the right kind
                      selected, the row found, then L / double-click / menu: sidebar Pods → search → select →
                      L = 4 interactions, more if the tab is on another kind or namespace.
Now → after:          "logs of checkout-worker": 4+ → Ctrl/Cmd+K, type "checkout", Enter (2 + typing).
Acceptance:           - The palette, on a connected cluster tab, offers "Logs: <pod>" rows for the tab's pods
                        and "Logs: <Kind>/<workload>" rows for its Deployments/StatefulSets/DaemonSets (the
                        latter open the existing multi-pod pane). Matching is the palette's own fuzzy match on
                        the name; the row subtitle names namespace and status.
                      - Source: the tab's current namespace selection. Filled by a one-shot list when the
                        palette opens (not a new watch; reuse the current list's rows when the tab is already
                        on Pods), bounded (state the cap, e.g. 2 000 pods), with a "loading pods…" row while
                        it is in flight and the stale-but-available rows shown meanwhile; RBAC-denied or
                        failed list → no rows plus one disabled row saying why. Never blocks typing.
                      - Enter opens the logs exactly the way L does today (same command, same inspector tab
                        rules — UI rule 5: never overwrites an active editor tab).
                      - A dedicated gesture opens the palette pre-filtered to logs (e.g. Ctrl/Cmd+Shift+L via
                        `Hotkeys`, prefix "logs " or similar); catalog + cheat sheet + golden docs updated.
                      - Demo cluster: works over the demo dataset.
                      - Fleet mode: rows name their cluster.
States:               loading, loaded, empty namespace, RBAC-denied, disconnected, demo, fleet.
Keyboard + entry:     Ctrl/Cmd+K and the dedicated logs gesture.
Verify:               App tests for row building (pods + workloads, cap, denied); screenshot of the palette
                      filtered to logs (both themes); sandbox: palette lists the sandbox's pods (logs themselves
                      cannot stream here — say so).
Risk:                 palette latency on a large cluster; build rows off the UI thread and cap them.

### L2 — One click to logs from the row, and logs opened full-size        (UX, S, owner pin)
Problem + evidence:   Owner request. Logs from the list need select + L or a context menu; and logs open in a
                      ~300px dock where long lines and history are cramped — maximizing is another click.
Now → after:          "open logs for this row": select + L (2) → hover + click the row's logs button (1);
                      "read logs big": open + maximize (2) → Shift+L or Shift+click (1).
Acceptance:           - Rows of kinds that have logs (pods, and the workload kinds L already supports) show a
                        small logs icon button in the row on hover/selection (not always-visible chrome — UI
                        rule 1), with tooltip naming L; it hit-tests its whole area and has the hand cursor
                        (UI rule 8). It never appears on kinds without logs.
                      - Shift+L (and Shift+click on that button) opens the logs with the inspector maximized
                        (`IsInspectorMaximized`); Esc or the existing restore control returns to split.
                      - A preference "Open logs maximized" in `settings.json` (preferences page card, immediate
                        apply) makes maximized the default for L; the setting is actually read by the open
                        path (CLAUDE.md settings rule 3).
                      - Catalog/cheat sheet/golden docs updated for Shift+L.
States:               hover, selected, kinds without logs, demo, maximized/restored.
Keyboard + entry:     the row button, Shift+L, the preference.
Verify:               App tests (button visibility rule per kind, the maximized open path, the setting read);
                      screenshots of a hovered/selected row with the button and of logs opened maximized.
Risk:                 a DataGrid template column for the button fights the per-kind column layout (FEAT-66)
                      — prefer an overlay in the Name cell or a fixed narrow slot, and re-render
                      `cluster-tab-workloads-list` and `cluster-tab-crd-printer-columns`.

### L3 — Logs from everywhere a pod is named        (UX, S, owner pin)
Problem + evidence:   Owner request. Pods named inside other panes (workload detail's pod list, node detail's
                      pods-on-node, an event whose involved object is a pod, Argo application resources) do
                      not open logs directly; the user navigates to Pods and finds the row again.
Now → after:          "logs of a pod I see in workload detail": back to list → Pods → find → L (4) → select it
                      in the pane + L or its logs button (1–2).
Acceptance:           - In workload detail's Pods list, node detail's pods list and (after T2 or on the current
                        Events list) an Event row whose involved object is a Pod: L opens that pod's logs, and a
                        context menu item / row logs button does the same, reusing L2's affordance.
                      - Argo application resource rows that are Pods (or workloads) get the same, if the pane
                        lists them; if it does not, say so and skip.
                      - Every entry resolves to the same open-logs command, so tab rules and the maximized
                        preference from L2 apply everywhere.
                      - Catalog/cheat sheet reflect the scope (L works in these lists too).
States:               pod present, pod gone (stated, not a dead click), demo.
Keyboard + entry:     L and the row button in each list.
Verify:               App tests that each list routes to the shared command; screenshots of workload detail and
                      node detail pod lists with the affordance.
Risk:                 duplicated key handling per view; route through one handler.

### T2 — The Events list reads like `kubectl get events`        (UX, M, score 3.65, source: friction walk)
Problem + evidence:   `cluster-tab-events-list`: Name shows the Event object's own name
                      (`checkout-worker-5d8f7b9c4-qz9pl.17f2a1`), Status shows `Reason ×count`, no message, no
                      involved object, and Age is the Event's creationTimestamp (blank in the fixture). What
                      happened and to what is one double-click per event away. kubectl prints LAST SEEN, TYPE,
                      REASON, OBJECT, MESSAGE.
Now → after:          "what happened in this namespace": open each event (1 + N) → read the list (1).
Acceptance:           - When the selected kind is core/v1 Event (and `events.k8s.io` Event if it is listed),
                        the grid shows: Last seen (relative; from `lastTimestamp` → `eventTime` →
                        `series.lastObservedTime` → `firstTimestamp` → creation, tooltip exact instant),
                        Type (Warning visually distinct, text not colour-only), Reason, Object
                        (`Kind/name`), Count, Message (single line, trimmed, full text in tooltip).
                        Namespace stays in all-namespaces and fleet mode.
                      - Default sort is Last seen descending; the grid's own header sort still works on each
                        column and the per-kind layout persistence (FEAT-66) keeps working.
                      - Double-click still opens the involved object (existing `InvolvedObject()` route at
                        `ClusterTabViewModel` ~line 2933); the text search also matches Reason, Object and
                        Message for Events (they identify an event the way a name identifies a pod — state
                        this in `ResourceRowViewModel.Matches` and in the CLAUDE.md rule-13 paragraph).
                      - Uses the existing fixed column slots / column mechanism (read
                        `docs/engineering/crd-printer-columns.md` and `resource-grid-resize-sort.md` first);
                        no `Width="Auto"` (`datagrid-auto-columns.md`).
                      - Demo dataset events render the same (demo rule 3: one dataset).
States:               loading, empty ("No events in <ns>" — events expire after 1h by default; say so),
                      search matching nothing, fleet, an event with no involved object, with no timestamps.
Keyboard + entry:     unchanged (sidebar Events / palette); Enter/double-click opens the involved object.
Verify:               App/Core tests for the timestamp fallback chain and the Object text; screenshot
                      `cluster-tab-events-list` (both themes, 1280px) re-rendered and read; sandbox: the
                      API-server-only cluster generates real FailedScheduling/FailedCreatePodSandBox events.
Risk:                 the slot/column machinery is shared with CRD printer columns; a change there can
                      regress CRD lists — re-render `cluster-tab-crd-printer-columns` too.

### T3 — Reach a container's whole retained log: tail and since        (UX, S, score 3.45, source: FEAT-31)
Problem + evidence:   `PodDetailTabViewModel.cs:1226` follows with a literal `tailLines: 200`; nothing older
                      than 200 lines is reachable by any gesture. ~162 reactions across k9s#1228/#1507/#994/
                      #901. Full evidence in `docs/BACKLOG.md` FEAT-31.
Now → after:          "see the log from before the last 200 lines": impossible (kubectl) → 2 (pick range).
Acceptance:           - A single compact range control on the log pane's existing toolbar row (UI rule 10: no
                        new row) offering: last 200 lines (default), last 1000, 5 m, 1 h, 24 h, everything.
                        Changing it restarts the stream with `tailLines` or `sinceSeconds` accordingly; follow
                        state is preserved.
                      - The same control on the multi-pod pane (`WorkloadLogsTabViewModel`), where a line
                        budget is per pod as it is today (read `docs/engineering/multi-pod-logs.md`).
                      - "Everything" still respects the scrollback cap (`LogBufferLines`), and the pane says
                        when the cap trimmed older lines rather than implying it has them all.
                      - Previous-container logs honour the same range.
                      - Core: `StreamPodLogsAsync` gains `sinceSeconds`; pinned by a request-shape test.
                      - Demo cluster: control works over the canned streams or is honestly disabled.
States:               streaming, restarting on change, empty range ("No lines in the last 5 minutes"),
                      trimmed-by-cap notice, demo.
Keyboard + entry:     the toolbar control; palette entries only if cheap.
Verify:               Core request-shape tests; App tests for the range → query mapping; screenshots of both
                      panes with the control; live verification impossible here (no running pod) → Inbox row.
Risk:                 toolbar overflow at the default ~300px dock width; check the screenshot at 1280px.

### T4 — Events read the same everywhere, with a warning count        (UX, S, score 3.10, source: research #6)
Problem + evidence:   `ux-workload-events`: the workload Events tab is a DataGrid whose Type/Count columns
                      clip (`Norma`, `Cour`) in a bigger font than the pane; pod detail renders the same data
                      as readable cards but prints `07/20/2026 04:58:00 +00:00` instead of an age. Aptakube
                      1.19.5 added a warning-count badge on its Events tab.
Now → after:          "does this workload have warnings?": open Events tab and read (2) → read the tab label (1).
Acceptance:           - Workload detail's Events tab uses the same event card template as pod detail
                        (extract one shared DataTemplate/control inside the app, not nimbusUi).
                      - Every event card shows a relative "last seen" with the exact instant in a tooltip.
                      - The Events segment label on pod, workload (and node, if it has one) detail reads
                        "Events · 3 warnings" (text, not a dot) when warnings exist, plain "Events" otherwise;
                        updates when events refresh.
                      - UI rule 10's two-row chrome budget holds.
States:               loading, no events ("No events — they expire after an hour"), warnings present, error.
Keyboard + entry:     unchanged.
Verify:               screenshots `ux-workload-events`, `cluster-tab-pod-detail-events` (both themes);
                      an App test for the warning-count text.
Risk:                 `SelectedDetailTabIndex` indices are load-bearing (UI rule 10) — change the label, not
                      the order.

### T5 — The Namespace column appears only when it says something        (UX, S, score 2.85, source: friction walk + ENG-23)
Problem + evidence:   With one namespace selected, every row repeats it and pod names truncate at 1280px
                      (`cluster-tab-workloads-list`); cluster-scoped kinds show an always-empty Namespace
                      column (`cluster-tab-node-detail`, ENG-23). kubectl omits NAMESPACE unless `-A`.
Now → after:          "read a pod's full name": hover each truncated name (1 per row) → read it (0).
Acceptance:           - Namespace column hidden for cluster-scoped kinds and when a single namespace is
                        selected; shown for All namespaces and in fleet mode (with Cluster).
                      - The freed width goes to Name (min-width rules stay; rule 14 gutters stay).
                      - Per-kind persisted column widths (FEAT-66) keep working: a hidden column's saved
                        width survives and comes back when the column does.
                      - Cluster-scoped kinds: the namespace picker is hidden or clearly inapplicable rather
                        than a disabled box still reading "payments".
                      - ENG-23 in `docs/BACKLOG.md` marked done with the commit.
States:               single ns, all ns, fleet, cluster-scoped, CRD cluster-scoped.
Keyboard + entry:     n/a.
Verify:               App tests for the visibility rule; screenshots `cluster-tab-workloads-list`,
                      `cluster-tab-node-list`, `cluster-tab-fleet-list`, `cluster-tab-crd-printer-columns`.
Risk:                 `ResourceGridSort`/column identity by Tag; the CRD slots — re-render them.

### T6 — Log follow survives a dropped stream and says so        (Reliability, S, score 2.60, source: FEAT-34)
Problem + evidence:   Watches reconnect with resourceVersion resume (hard rule 2); log follow only states a
                      reason and stops (`EndLogStreamAsync`). lens#8163, headlamp#6256 (38 comments). Full
                      evidence in `docs/BACKLOG.md` FEAT-34.
Now → after:          "keep reading logs after an API-server blip or a pod restart": notice, press Follow
                      again, lose position (2+) → nothing (0), with a visible seam line.
Acceptance:           - On a transport failure while following, reconnect with bounded exponential back-off
                        (cap and attempt limit stated), resuming with `sinceTime` = last received line's
                        timestamp (streams already request `timestamps=true`), and drop duplicate lines at
                        the seam (same timestamp + text).
                      - The pane shows an in-place seam line ("reconnected after 4 s — lines may be missing
                        between 12:01:03 and 12:01:07" or "stream resumed") styled as a notice, not a log line,
                        and never included in Copy/Download.
                      - A container that actually exited or a pod that was deleted is not retried forever:
                        a 404 / container-not-found ends with the existing stated reason.
                      - Cancellation (closing the tab, toggling follow off) cancels any pending retry.
                      - Same behaviour in the multi-pod pane per stream if it shares the path; if it does
                        not, say so and file an Inbox row.
                      - Core tests against an `HttpListener` stand-in: drop mid-stream → reconnect with the
                        right `sinceTime`; duplicate removal; 404 → no retry; cancellation.
States:               following, reconnecting (with attempt), resumed, given up (reason), cancelled.
Keyboard + entry:     none new.
Verify:               Core/App tests above; a screenshot of the seam line; live verification impossible here
                      (no running pod) → Inbox row.
Risk:                 an infinite retry loop on a permanent error; classify errors explicitly.

### T7 — JSON log lines are readable        (UX, M, score 2.55, source: FEAT-32)
Problem + evidence:   k9s#364 (147 👍), lens#4320 (24 👍, open since 2021); full evidence and the three
                      shipped designs in `docs/BACKLOG.md` FEAT-32. `LogLineViewModel.DetectSeverity` is a
                      token scan.
Now → after:          "read a structured log": scan a raw JSON blob per line → level + message first, fields
                      after, keys dimmed.
Acceptance:           - A line whose message part is a JSON object (after the timestamp) is detected with
                        `System.Text.Json` `JsonDocument` (AOT-safe; no new package).
                      - Rendered in place, one line (no re-indent — Headlamp's prettify lost lines): level
                        (from `level`/`lvl`/`severity`), then `msg`/`message`, then remaining `key=value`
                        pairs with keys in a muted style. Severity comes from the parsed level and feeds the
                        existing three severity classes (read `docs/engineering/log-severity-classes.md` —
                        never a Foreground binding).
                      - A toggle on the existing log toolbar ("Raw"/structured) in both single- and multi-pod
                        panes; Copy/Download always write the raw server line.
                      - Non-JSON lines and malformed JSON render exactly as today; the filter matches the raw
                        text.
                      - Performance: parsing happens once per line on arrival, not per render; a 4 000-line
                        buffer of JSON stays responsive (state the measured cost in the report).
                      - Demo logs gain a JSON-logging container (demo rule 4) if none exists.
States:               JSON, plain, mixed, malformed, raw toggle, filter active.
Keyboard + entry:     the toolbar toggle; palette entry if cheap.
Verify:               App tests for detection, level mapping, field ordering, malformed fallback; screenshots
                      of a JSON pod's logs (both themes, both toggle states).
Risk:                 rendering runs inside a line needs `Inlines` built in code; a bound TextBlock cannot do
                      it — keep it off the UI thread's hot path.

### T8 — See a workload's revision history        (UX, M, score 2.50, source: research #3)
Problem + evidence:   headlamp#3222 ("see the history of rollouts") shipped in Headlamp v0.41; k9s via its
                      ReplicaSets view. kubeNimbus: ReplicaSets/ControllerRevisions are only raw kinds.
Now → after:          "what changed in this deployment": ReplicaSets kind → filter by name → open each YAML
                      (4+) → workload detail → Revisions (2).
Acceptance:           - A Revisions segment on workload detail (UI rule 10: a segment on the existing strip,
                        appended after the existing ones so current indices hold) for Deployments (owned
                        ReplicaSets, `deployment.kubernetes.io/revision`) and StatefulSets/DaemonSets (owned
                        ControllerRevisions, `.revision`).
                      - Each row: revision number, age, image(s) of the pod template, change-cause
                        annotation when present, replicas for ReplicaSets, and a "current" marker. Newest
                        first. Double-click opens the ReplicaSet/ControllerRevision's YAML read-only (or the
                        ReplicaSet row's normal detail).
                      - One list call filtered by ownerReference UID (`ListResourceOnceAsync` or equivalent),
                        refreshed with the pane's existing Refresh; no new watch.
                      - Demo dataset gains ReplicaSet history for one Deployment (demo rule 3/4).
States:               loading, one revision only, none readable (RBAC 403 → stated), error, demo.
Keyboard + entry:     the segment; palette "Show revisions" only if cheap.
Verify:               Core tests for revision extraction (both owner kinds, missing annotations); screenshots
                      of the Revisions segment (both themes); sandbox: create a Deployment on the
                      API-server-only cluster, patch its image twice, confirm three revisions list (ReplicaSets
                      are created by the controller even though pods never start).
Risk:                 ControllerRevision `data` shapes differ between StatefulSet and DaemonSet; images must
                      be read defensively.

### T9 — Exec-plugin auth proven against a real API server        (Verification debt, S, score 2.25, source: VER-31)
Problem + evidence:   CLAUDE.md hard rule 4 and README claim exec-plugin auth works; no test in `tests/` has a
                      kubeconfig with an `exec:` block. `docs/BACKLOG.md` VER-31.
Now → after:          claimed → observed and pinned.
Acceptance:           - Sandbox-gated integration tests (skip via `SandboxCluster` when no cluster) that build
                        a kubeconfig whose user is an `exec:` block pointing at a generated script printing an
                        `ExecCredential` with a real bearer token for the sandbox (e.g. a ServiceAccount token
                        minted via the TokenRequest API with the sandbox's admin credentials), and assert:
                        connect + list succeeds; the token is re-requested after `expirationTimestamp` passes;
                        a missing binary yields the app's stated error; a plugin writing to stderr and exiting
                        non-zero surfaces a useful error.
                      - Runs green on this session's API-server-only k3s; skips cleanly without one.
                      - Any gap found is fixed if small or filed as an Inbox row (FEAT-48/49/50 are the known
                        neighbours — do not build them here).
                      - VER-31 marked done with the commit.
States:               n/a (test-only), unless a defect is found.
Keyboard + entry:     n/a.
Verify:               the tests themselves, run against the live sandbox (report succeeded/skipped counts).
Risk:                 the plugin script must work on Windows too or be skipped there — state which.

### T10 — Mutating actions and strict apply proven against a real API server        (Verification debt, M, score 2.00, source: VER-13 + VER-36)
Problem + evidence:   Scale, rollout restart, delete, cordon and strict-validation apply shipped against
                      stand-ins; "a wrong patch fails silently as nothing happened". `docs/BACKLOG.md` VER-13,
                      VER-36, VER-29 (cordon half), VER-32 (dry-run half).
Now → after:          argued → observed and pinned.
Acceptance:           - Sandbox-gated integration tests that, in a throwaway namespace: scale a Deployment via
                        the app's Core path and read `spec.replicas` back; rollout-restart and read the
                        `restartedAt` annotation on the pod template (rolling of pods cannot be observed here —
                        say so); delete a pod and observe it gone; cordon/uncordon the node and read
                        `spec.unschedulable`; apply a manifest with a misspelled field and assert the strict
                        rejection is classified by the app as a validation error (widen the classifier if the
                        real message differs — that is the point); dry-run a valid change and assert the object
                        is unchanged afterwards; an impersonated/limited user's 403 surfaces the server message.
                      - Every test cleans up after itself.
                      - Update VER-13/VER-36/VER-29/VER-32 rows: mark what was observed, and leave the
                        running-pod halves open with that stated.
States:               n/a (test-only), unless a defect is found.
Keyboard + entry:     n/a.
Verify:               the tests themselves against the live sandbox; report counts.
Risk:                 tests that leak state between runs; use a unique namespace per run and delete it.

### Reserve

- **R1 — Diff a revision against the current template** (S, needs T8): select a revision on the Revisions
  segment and see its pod template against the current one in the apply preview's `TextDiff` renderer,
  with `pod-template-hash` and metadata stripped.
- **R2 — ENG-22**: `RowActionViewModel.CanConfirm` checks `IsDraining`; pinned by an App test.
- **R3 — FEAT-56**: `SelfSubjectReview` ("who am I") beside the access review; states pre-1.26 servers.

## Log

- 2026-09-22 — T1 landed (`fa4f30e`), verifier PASS first round (re-ran build, both suites,
  170 screenshots, AOT publish + smoke test; mutation-checked the Modified path). Inbox:
  VER-38, ENG-31..34.

- 2026-09-22 — Owner pin applied (quick access to logs → L1–L3, built next after T1);
  T4, T5, T8 to reserve.

- 2026-09-22 — SELECT: research delta in (`history/v0.4.0/research.md`, matrix seeded).
  10 items + 3 reserve at 14/14 points; the three P1 Ready rows forced (FEAT-31/32/34);
  VER-1 infeasible here. Phase → BUILD on the session-designated branch.

- 2026-09-22 — SURVEY started. Researcher (delta since 2026-08-15; deep theme "what is
  broken → why → what changed": cluster-wide triage and change history) running in the
  background. Repo scan: no open issues or PRs, CI green on `main`, one commit since
  v0.3.3 (the train machinery), no new TODO/FIXME. Friction walk (7 screens) found the
  Events list unreadable as a timeline (no message/object/last-seen), the workload Events
  tab clipping, absolute timestamps in pod events, and a redundant Namespace column
  when one namespace is selected. `CURRENT_STATE.md` created. Waiting on the researcher
  before SELECT.
- 2026-09-22 — Core suite against the API-server-only sandbox: 394 total, 391 passed,
  **0 skipped**, 3 failed — exactly the three that need a running pod
  (`Exec_runs_a_shell_command_in_a_running_pod`, `PortForward_connects_to_a_pod_tcp_port`,
  `StreamPodLogs_returns_lines_and_honors_cancellation`, each timing out waiting for a pod
  that can never start here). Every API-server-backed integration test ran for real.

- 2026-09-22 — train machinery created; the first train starts at SURVEY. Being the
  first, its researcher seeds `COMPETITOR_MATRIX.md` from `docs/research/` (August
  2026) and researches only what changed since those reports.

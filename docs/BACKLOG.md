# kubeNimbus backlog

The queue `/backlog-cycle` works from. Everything below is either **validated by
a human and ready to build** (the Ready table) or **waiting for a human to
validate it** (the Inbox). The loop may only take work from Ready — that gate is
deliberate and is the one rule of this file.

## Config

The cycle re-reads these each run, so changing one here changes the loop's
behaviour on the next tick.

| Key | Value | Meaning |
|---|---|---|
| `AUTO_PR` | `no` | On PASS, push the branch only. `yes` also opens a pull request. |
| `MAX_FIX_ROUNDS` | `2` | Verifier FAIL → implementer fix rounds before the item is marked `blocked`. |
| `RESEARCH_EVERY` | `5` | Cycles between competitor-research runs. |
| `READY_POOL_MIN` | `5` | Research also runs whenever Ready drops below this. |

**Status:** `inbox` → `ready` → `in-progress` → `needs-fix` → `done`, or
`blocked` / `rejected`.
**Priority** is `P0`–`P3` and is set **by a human only**. An item with no
priority is not ready, whatever its status column says.
**Size** is `S` (a session), `M` (a day), `L` (multi-session, wants its own
design pass first).
**Rec** is the recommendation this file was drafted with — a starting point for
your prioritization, not a decision.

---

## Ready

*Empty. Move rows here from the Inbox once you have set a priority, and the loop
will start working through them top-down.*

| ID | Item | Prio | Size | Status | Rounds |
|---|---|---|---|---|---|
| | | | | | |

---

## Inbox — needs your validation and priorities

### A. Verification debt

Every row here is something `CLAUDE.md` records as claimed-but-never-checked.
This section exists because that debt has already cost this project a release:
three of four RIDs shipped a binary that could not start, and nothing caught it
because nothing ever launched one.

| ID | Item — *done when* | Signal | Size | Rec | Prio |
|---|---|---|---|---|---|
| VER-1 | Re-publish `linux-x64`, `linux-arm64` and `osx-arm64` NativeAOT after the `WindowIcons.Apply` fix and **launch each one** — *done when each binary opens a real window, recorded in CLAUDE.md* | The icon-converter bug was diagnosed as cross-platform, but only win-x64 has actually been run since the fix. Until this passes, the release workflow still ships three RIDs on a hypothesis | S | **P0** | |
| VER-2 | CI launches the published AOT binary under Xvfb and asserts a window appears — *done when a deliberately broken startup path fails the job* | `ci.yml` publishes AOT but never runs the output. That is exactly how the `/Assets/app.ico` `FileNotFoundException` reached three release binaries | M | **P0** | |
| VER-3 | Change the Ctrl/Cmd hotkey scheme with the app open — *done when bindings, the F1 sheet and every tooltip re-render, and the old gesture stops working* | `BuildKeyBindings` clearing first is the part that fails silently; the whole path is new and untested | S | **P1** | |
| VER-4 | Hands-on live-cluster pass over the redesigned port-forward pane and the Env eye toggle — *done when a real forward starts/stops from the new UI and a real Secret reveals and re-hides* | Both were redesigned after their last live verification. Core is proven by `pftest.cs`; the panes are not | M | **P1** | |
| VER-5 | Automated regression: a `Modified` watch event for a filtered-out row must not resurface it, and `VisibleRows` must stay mirrored — *done when the test fails if `Rows` is filtered in place* | UI rule 13's central invariant has no test, and the failure mode (a row reappearing mid-filter) looks like a watch bug, not a filter bug | S | **P1** | |
| VER-6 | Drive DataGrid row interactions with a real mouse — double-click to open, right-click context menu on the row under the cursor, namespace picker | DevTools synthetic clicks return `handled:false` on `DataGridRow`/`ComboBoxItem`, so these are verified by construction only. The context menu ends in Delete | S | P2 | |
| VER-7 | Windows: 150 % DPI, multi-monitor, caption drag, double-click-to-maximize, Snap Layouts | The 3 × 45 DIP caption reserve has only been checked at 100 % on one monitor; drag/Snap could not be driven by any available input path | M | P2 | |
| VER-8 | macOS: traffic lights against a 40 px bar, full-screen collapsing the 78 DIP reserve, and the osx-arm64 binary launching | No macOS machine has ever run this app. Full screen is the ordinary gesture there, and a stale reserve leaves a dead 78 px hole | M | P2 | |
| VER-9 | Preferences hands-on: kubeconfig add/remove, immediate-apply, and the `workspace.json` → `settings.json` migration against a real pre-migration file | The migration guard exists to prevent "the update ate my settings" and has never run against a real old file | S | P2 | |

### B. Product gaps

**Unvalidated by research.** These are drafted from the shipped surface and from
what comparable tools do; `kn-researcher` has not run yet. Treat the Signal
column as a hypothesis until it does — that run is what turns these into
evidence, and it is worth doing before you commit to anything below P1.

| ID | Item — *done when* | Signal (hypothesis) | Size | Rec | Prio |
|---|---|---|---|---|---|
| FEAT-1 | Workload actions: scale a Deployment/StatefulSet, `rollout restart`, delete a pod to force recreation — *done when each is on the row context menu, the palette, and confirmable* | The app is read-mostly: today the only way to scale is editing YAML. Every competitor (Lens, Aptakube, k9s, Headlamp) has these as one-click, and "restart deployment" is the single most common on-call GUI action | M | **P0** | |
| FEAT-2 | Drive list columns from a CRD's `additionalPrinterColumns` — *done when a CRD-heavy cluster shows the same columns `kubectl get` does, with `ResourceStatusSummary` still owning the built-ins* | CRDs declare their own printer columns and kubectl honours them. Today every CRD shows the generic Status column, which is the weakest surface in an app that sells CRDs as first-class | M | **P1** | |
| FEAT-3 | Tail logs across every pod of a Deployment/selector in one pane, colour-keyed by pod — *done when a rolling deployment reads as one stream* | This is what `stern` exists for and it is consistently among krew's most-installed plugins; Aptakube markets multi-pod logs prominently | L | **P1** | |
| FEAT-4 | Node detail (conditions, taints, allocatable vs requested, pods on the node) plus cordon / uncordon / drain — *done when a node can be drained with progress and eviction failures surfaced* | `NodeMetrics` already exists and `ResourceStatusSummary` already reads `unschedulable`, so half of it is present with no UI. Node operations are the classic reason to open a GUI at 3 a.m. | L | **P1** | |
| FEAT-5 | Show a server-side apply **dry-run diff** before applying — *done when the editor previews exactly what the server would change, conflicts included* | The current apply is blind; a diff is the difference between an editor people trust in production and one they only use in dev | M | P2 | |
| FEAT-6 | Multi-select in the resource list + bulk delete with one confirmation naming the count | Deleting eight failed pods is currently eight round-trips through a two-step confirm | M | P2 | |
| FEAT-7 | A port-forward manager: every active forward in one place, surviving the tab that started it | Forwards die with their tab today, and there is no way to see what is still listening | M | P2 | |
| FEAT-8 | Job/CronJob: trigger now, suspend/resume — *done when a CronJob can be fired manually and a Job's pods are reachable from it* | `kubectl create job --from=cronjob` is the standard move and has no GUI here | S | P2 | |
| FEAT-9 | A cluster overview: node capacity, warning-event feed, workloads not in a healthy state | The screen Lens leads its marketing with. It is also the honest answer to "what is wrong with this cluster right now", which today requires visiting four kinds | L | P2 | |
| FEAT-10 | Real terminal emulation in the exec pane (cursor addressing, colour, `vi`/`top`) instead of stripping ANSI | The current pane strips escapes, so any full-screen program is unusable. Must stay AOT-safe — likely a hand-rolled VT parser, same argument as `Sparkline` | L | P2 | |
| FEAT-11 | A `describe` view — the events + conditions + tolerations digest `kubectl describe` produces | Regularly the first thing anyone runs on a broken pod; the YAML editor is not a substitute | M | P3 | |
| FEAT-12 | Apply a local YAML file, and create a resource from a template | There is no way to create anything that does not already exist | M | P3 | |
| FEAT-13 | Column chooser, persisted per kind | Nine fixed columns is the right default and the wrong ceiling on a wide monitor | M | P3 | |
| FEAT-14 | Global search across kinds ("find anything called `payments`") | The list filter is per-kind; finding an object whose kind you have forgotten has no answer | M | P3 | |
| FEAT-15 | Pod file browser and copy in/out (`kubectl cp`) | Common in debugging, absent here; needs a design pass against the streaming rules first | L | P3 | |

### C. Distribution and adoption

Nothing in this section changes the app, and two rows probably matter more to
adoption than anything in section B.

| ID | Item — *done when* | Signal | Size | Rec | Prio |
|---|---|---|---|---|---|
| DIST-1 | Sign the binaries — Authenticode on Windows, notarization on macOS — *done when a fresh download opens without a SmartScreen or Gatekeeper warning* | Every release body currently ships a workaround for a scary OS dialog. For a security-adjacent tool asking for cluster credentials, an unsigned binary is a conversion cliff. Costs real money; that is your call, not the loop's | M | **P1** | |
| DIST-2 | Package for winget, Homebrew cask and AUR, and submit to the Microsoft Store — *done when `winget install kubeNimbus` works* | The demo cluster was built for Store certification and the submission has not happened. A GitHub release page is not a distribution channel for a desktop app | M | **P1** | |
| DIST-3 | **Policy decision first**, then an opt-in update check | README promises "no network connection other than to the clusters you point it at". An update check breaks that sentence unless it is opt-in and documented. Decide the promise before writing the code | S | P2 | |
| DIST-4 | A landing page with an honest comparison against Lens / FreeLens / Aptakube / Headlamp / k9s | The positioning is sharp and lives only in `CLAUDE.md`, where no prospective user will read it | M | P2 | |
| DIST-5 | A short screen capture of the demo cluster for the README and the Store listing | Static screenshots do not show the thing being sold — that it opens in ~150 ms and streams live | S | P3 | |

### D. Engineering hygiene

| ID | Item — *done when* | Signal | Size | Rec | Prio |
|---|---|---|---|---|---|
| ENG-1 | Land the four open PRs — #21 (YamlDotNet 16→18), #31 (Avalonia group), #32 (test tooling), #30 (screenshot refresh) | #21 has been open since 3 Aug and YamlDotNet is load-bearing for every apply; a major bump is not a rubber stamp | S | **P1** | |
| ENG-2 | `scripts/test.sh` / `.ps1` that runs the test executable directly, with a comment naming both ways `dotnet test` has silently run nothing here | Two distinct silent-zero-test failures are already documented. A wrapper makes the correct invocation the easy one | S | P2 | |
| ENG-3 | A load scenario — 5 000 pods, a watch storm, a 100-kind catalog — with a startup and steady-state frame budget | "~150 ms to first frame" is the headline claim and nothing measures it | M | P2 | |
| ENG-4 | Accessibility pass: keyboard-only navigation, focus visuals, automation names | Also a Microsoft Store certification item, so it pairs with DIST-2 | M | P2 | |
| ENG-5 | Persist the sidebar's Recent kinds across restarts | Explicitly deferred in `CLAUDE.md` pending a `WorkspaceSettings` schema change | S | P3 | |
| ENG-6 | Horizontal scroll for the fleet list — ten columns do not fit 1280 px and the rightmost headers clip | Known and documented as unchanged since before the gutter pass | S | P3 | |

---

## Done

| ID | Item | Landed | Commit |
|---|---|---|---|
| | | | |

---

## Rejected / deliberately not doing

Kept so the same proposal does not come back through the Inbox every quarter.

| Item | Why |
|---|---|
| Long-range metrics history | Prometheus's job. `UsageHistory` is bounded to the session and never persisted — a permanent non-goal. |
| Helm install / upgrade / rollback | Read-only browsing is the deliberate scope; mutation stays Helm's. Revisit only with real demand evidence. |
| Cluster provisioning, in-cluster agents, telemetry | Permanent non-goals. |
| Coalescing same-named CRD kinds into one sidebar row | Rejected once already: nesting inside a 100-kind section costs more than the group label it would replace. |

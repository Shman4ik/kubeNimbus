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
| `AUTO_PR` | `yes` | On PASS, push the branch **and** open a pull request against `main`, following `.github/PULL_REQUEST_TEMPLATE.md`. Set to `no` to push the branch only. |
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

Worked top-down, one per cycle. Every row here is something an agent can finish
**without a human at a keyboard** — no live cluster it cannot start, no Windows
or macOS box, no account or purchase. That is the entry test for this table, and
it is why several P0/P1 rows are still in the Inbox with a `needs a human` note
rather than here.

| ID | Item | Prio | Size | Status | Rounds |
|---|---|---|---|---|---|
| VER-2 | CI and the release workflow **launch** every binary they publish — Xvfb on Linux, the matching runner for win-x64 and osx-arm64 — and fail if a window never appears | P0 | M | in-progress | 0 |
| VER-1 | With VER-2's runners in place, confirm `linux-x64`, `linux-arm64` and `osx-arm64` actually start after the `WindowIcons.Apply` fix, and record the result in CLAUDE.md | P0 | S | ready | 0 |
| ENG-1 | Land the four open PRs — #21 (YamlDotNet 16→18, load-bearing for every apply), #31, #32, #30 — building and testing each rather than rubber-stamping | P1 | S | ready | 0 |
| VER-5 | Regression test: a `Modified` watch event for a filtered-out row must not resurface it, and `VisibleRows` must stay mirrored from `Rows` | P1 | S | ready | 0 |
| FEAT-1 | Workload actions — scale, `rollout restart`, delete a pod — on the row context menu and in the palette, with confirmation | P0 | M | ready | 0 |
| FEAT-16 | Open the machine's own terminal with `KUBECONFIG` and the context pointed at this cluster; explicit state when `kubectl` is missing | P1 | S | ready | 0 |
| FEAT-10 | Replace the exec pane's ANSI stripping with `SvcSystems.UI.Terminal` over `XTerm.NET` — keep the WebSocket transport and the bash→sh→ash probe | P1 | M | ready | 0 |
| VER-3 | Change the Ctrl/Cmd hotkey scheme with the app running (Xvfb is enough) — bindings, F1 sheet and tooltips must all re-render, old gesture must stop working | P1 | S | ready | 0 |
| FEAT-2 | Drive list columns from a CRD's `additionalPrinterColumns`, leaving `ResourceStatusSummary` owning the built-ins | P1 | M | ready | 0 |
| DIST-6 | Re-check the positioning against KubeUI and either confirm or rewrite CLAUDE.md's market paragraph — a `kn-researcher` job, not an implementer one | P1 | S | ready | 0 |
| FEAT-3 | Tail logs across every pod of a Deployment/selector in one pane, colour-keyed by pod | P1 | L | ready | 0 |
| FEAT-4 | Node detail (conditions, taints, allocatable vs requested, pods on the node) plus cordon / uncordon / drain | P1 | L | ready | 0 |

---

## Inbox — needs your validation and priorities

### A. Verification debt

Every row here is something `CLAUDE.md` records as claimed-but-never-checked.
This section exists because that debt has already cost this project a release:
three of four RIDs shipped a binary that could not start, and nothing caught it
because nothing ever launched one.

| ID | Item — *done when* | Signal | Size | Rec | Prio |
|---|---|---|---|---|---|
| VER-1 | Re-publish `linux-x64`, `linux-arm64` and `osx-arm64` NativeAOT after the `WindowIcons.Apply` fix and **launch each one** — *done when each binary opens a real window, recorded in CLAUDE.md* | The icon-converter bug was diagnosed as cross-platform, but only win-x64 has actually been run since the fix. Until this passes, the release workflow still ships three RIDs on a hypothesis | S | **P0** |  → Ready |
| VER-2 | CI launches the published AOT binary under Xvfb and asserts a window appears — *done when a deliberately broken startup path fails the job* | `ci.yml` publishes AOT but never runs the output. That is exactly how the `/Assets/app.ico` `FileNotFoundException` reached three release binaries | M | **P0** |  → Ready |
| VER-3 | Change the Ctrl/Cmd hotkey scheme with the app open — *done when bindings, the F1 sheet and every tooltip re-render, and the old gesture stops working* | `BuildKeyBindings` clearing first is the part that fails silently; the whole path is new and untested | S | **P1** |  → Ready |
| VER-4 | Hands-on live-cluster pass over the redesigned port-forward pane and the Env eye toggle — *done when a real forward starts/stops from the new UI and a real Secret reveals and re-hides* | Both were redesigned after their last live verification. Core is proven by `pftest.cs`; the panes are not | M | **P1** |  P1 · needs a human |
| VER-5 | Automated regression: a `Modified` watch event for a filtered-out row must not resurface it, and `VisibleRows` must stay mirrored — *done when the test fails if `Rows` is filtered in place* | UI rule 13's central invariant has no test, and the failure mode (a row reappearing mid-filter) looks like a watch bug, not a filter bug | S | **P1** |  → Ready |
| VER-6 | Drive DataGrid row interactions with a real mouse — double-click to open, right-click context menu on the row under the cursor, namespace picker | DevTools synthetic clicks return `handled:false` on `DataGridRow`/`ComboBoxItem`, so these are verified by construction only. The context menu ends in Delete | S | P2 |  P2 · needs a human |
| VER-7 | Windows: 150 % DPI, multi-monitor, caption drag, double-click-to-maximize, Snap Layouts | The 3 × 45 DIP caption reserve has only been checked at 100 % on one monitor; drag/Snap could not be driven by any available input path | M | P2 |  P2 · needs a Windows box |
| VER-8 | macOS: traffic lights against a 40 px bar, full-screen collapsing the 78 DIP reserve, and the osx-arm64 binary launching | No macOS machine has ever run this app. Full screen is the ordinary gesture there, and a stale reserve leaves a dead 78 px hole | M | P2 |  P2 · needs a Mac |
| VER-9 | Preferences hands-on: kubeconfig add/remove, immediate-apply, and the `workspace.json` → `settings.json` migration against a real pre-migration file | The migration guard exists to prevent "the update ate my settings" and has never run against a real old file | S | P2 | |

### B. Product gaps

**Unvalidated by research.** These are drafted from the shipped surface and from
what comparable tools do; `kn-researcher` has not run yet. Treat the Signal
column as a hypothesis until it does — that run is what turns these into
evidence, and it is worth doing before you commit to anything below P1.

| ID | Item — *done when* | Signal (hypothesis) | Size | Rec | Prio |
|---|---|---|---|---|---|
| FEAT-1 | Workload actions: scale a Deployment/StatefulSet, `rollout restart`, delete a pod to force recreation — *done when each is on the row context menu, the palette, and confirmable* | The app is read-mostly: today the only way to scale is editing YAML. Every competitor (Lens, Aptakube, k9s, Headlamp) has these as one-click, and "restart deployment" is the single most common on-call GUI action | M | **P0** |  → Ready |
| FEAT-2 | Drive list columns from a CRD's `additionalPrinterColumns` — *done when a CRD-heavy cluster shows the same columns `kubectl get` does, with `ResourceStatusSummary` still owning the built-ins* | CRDs declare their own printer columns and kubectl honours them. Today every CRD shows the generic Status column, which is the weakest surface in an app that sells CRDs as first-class | M | **P1** |  → Ready |
| FEAT-3 | Tail logs across every pod of a Deployment/selector in one pane, colour-keyed by pod — *done when a rolling deployment reads as one stream* | This is what `stern` exists for and it is consistently among krew's most-installed plugins; Aptakube markets multi-pod logs prominently | L | **P1** |  → Ready |
| FEAT-4 | Node detail (conditions, taints, allocatable vs requested, pods on the node) plus cordon / uncordon / drain — *done when a node can be drained with progress and eviction failures surfaced* | `NodeMetrics` already exists and `ResourceStatusSummary` already reads `unschedulable`, so half of it is present with no UI. Node operations are the classic reason to open a GUI at 3 a.m. | L | **P1** |  → Ready |
| FEAT-5 | Show a server-side apply **dry-run diff** before applying — *done when the editor previews exactly what the server would change, conflicts included* | The current apply is blind; a diff is the difference between an editor people trust in production and one they only use in dev | M | P2 | |
| FEAT-6 | Multi-select in the resource list + bulk delete with one confirmation naming the count | Deleting eight failed pods is currently eight round-trips through a two-step confirm | M | P2 | |
| FEAT-7 | A port-forward manager: every active forward in one place, surviving the tab that started it | Forwards die with their tab today, and there is no way to see what is still listening | M | P2 | |
| FEAT-8 | Job/CronJob: trigger now, suspend/resume — *done when a CronJob can be fired manually and a Job's pods are reachable from it* | `kubectl create job --from=cronjob` is the standard move and has no GUI here | S | P2 | |
| FEAT-9 | A cluster overview: node capacity, warning-event feed, workloads not in a healthy state | The screen Lens leads its marketing with. It is also the honest answer to "what is wrong with this cluster right now", which today requires visiting four kinds | L | P2 | |
| FEAT-10 | Real terminal emulation in the exec pane — *done when `vi`, `top` and `mc` are usable, colour and cursor addressing render, and Ctrl+C/Ctrl+D/Tab still reach the remote shell* | The pane strips ANSI, so every full-screen tool is unusable. Not a hand-rolled VT parser after all: `SvcSystems.UI.Terminal` (MIT, Avalonia 12.1.1, `Feed(byte[])`/`UserInput`/`Resize`) over `XTerm.NET` fits the WebSocket transport exactly and **published AOT-clean with zero trim warnings** — see [the research](research/2026-08-15-terminal-libraries.md). Keep the transport and the bash→sh→ash probe; only rendering and input change | M | **P1** |  → Ready |
| FEAT-16 | Open the machine's own terminal with `KUBECONFIG` and the context already pointed at this cluster — *done when it opens on all three platforms and says so when `kubectl` is missing* | The daily gesture people leave a GUI for. ~60 lines of `Process.Start` heuristics (`wt.exe` / `open -a Terminal` / `xdg-terminal-exec` → `x-terminal-emulator` → probes); no dependency, nothing new to break the AOT publish. Paths only, never credentials | S | **P1** |  → Ready |
| FEAT-17 | "Open this exec session in my terminal" — `kubectl exec -it` handed to the external terminal, from the exec pane and the row context menu | The honest answer to "your terminal is not my terminal". Depends on FEAT-16 | S | P2 | |
| FEAT-18 | A local shell tab inside the app, cluster context preset | Weak: FEAT-16 covers it better. Would add `Porta.Pty` (MIT, ConPTY + forkpty, pulls `Vanara.PInvoke`) — the only place this app would need a PTY at all | M | P3 | |
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
| DIST-1 | Sign the binaries — Authenticode on Windows, notarization on macOS — *done when a fresh download opens without a SmartScreen or Gatekeeper warning* | Every release body currently ships a workaround for a scary OS dialog. For a security-adjacent tool asking for cluster credentials, an unsigned binary is a conversion cliff. Costs real money; that is your call, not the loop's | M | **P1** |  P1 · needs a certificate |
| DIST-2 | Package for winget, Homebrew cask and AUR, and submit to the Microsoft Store — *done when `winget install kubeNimbus` works* | The demo cluster was built for Store certification and the submission has not happened. A GitHub release page is not a distribution channel for a desktop app | M | **P1** |  P1 · needs accounts |
| DIST-3 | **Policy decision first**, then an opt-in update check | README promises "no network connection other than to the clusters you point it at". An update check breaks that sentence unless it is opt-in and documented. Decide the promise before writing the code | S | P2 | |
| DIST-4 | A landing page with an honest comparison against Lens / FreeLens / Aptakube / Headlamp / k9s | The positioning is sharp and lives only in `CLAUDE.md`, where no prospective user will read it | M | P2 | |
| DIST-5 | A short screen capture of the demo cluster for the README and the Store listing | Static screenshots do not show the thing being sold — that it opens in ~150 ms and streams live | S | P3 | |
| DIST-6 | Re-check the positioning against [KubeUI](https://github.com/IvanJosipovic/KubeUI) — *done when `CLAUDE.md`'s market paragraph is either confirmed or rewritten* | Found while researching terminals: an actively developed **open-source Avalonia + .NET** Kubernetes client with multi-cluster, YAML editing, logs, console and port-forwarding. The whole "nobody ships fast + open source + modern native desktop UI" framing is stated without it. Better to know now than in a comparison table someone else writes | S | **P1** |  → Ready |

### D. Engineering hygiene

| ID | Item — *done when* | Signal | Size | Rec | Prio |
|---|---|---|---|---|---|
| ENG-1 | Land the four open PRs — #21 (YamlDotNet 16→18), #31 (Avalonia group), #32 (test tooling), #30 (screenshot refresh) | #21 has been open since 3 Aug and YamlDotNet is load-bearing for every apply; a major bump is not a rubber stamp | S | **P1** |  → Ready |
| ENG-2 | `scripts/test.sh` / `.ps1` that runs the test executable directly, with a comment naming both ways `dotnet test` has silently run nothing here | Two distinct silent-zero-test failures are already documented. A wrapper makes the correct invocation the easy one | S | P2 | |
| ENG-3 | A load scenario — 5 000 pods, a watch storm, a 100-kind catalog — with a startup and steady-state frame budget | "~150 ms to first frame" is the headline claim and nothing measures it | M | P2 | |
| ENG-4 | Accessibility pass: keyboard-only navigation, focus visuals, automation names | Also a Microsoft Store certification item, so it pairs with DIST-2 | M | P2 | |
| ENG-5 | Persist the sidebar's Recent kinds across restarts | Explicitly deferred in `CLAUDE.md` pending a `WorkspaceSettings` schema change | S | P3 | |
| ENG-6 | Horizontal scroll for the fleet list — ten columns do not fit 1280 px and the rightmost headers clip | Known and documented as unchanged since before the gutter pass | S | P3 | |
| ENG-7 | `AsyncMergeTests.Blocking` emits CS8425 (`CancellationToken` parameter with no `[EnumeratorCancellation]`) — *done when the build is back to 0 warnings* | `CLAUDE.md` records "build: 0 warnings" as the standard, and `main` no longer meets it. Worth fixing while it is one warning, because one is visible and three are noise | S | P3 | |

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

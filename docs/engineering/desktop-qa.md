# Desktop QA: how a full run works, what the 0.7.0 run cost, and why it is built this way

The first full desktop run before a tag (0.7.0, 2026-10-11, in the owner's Hyper-V QA VM)
took about **2 h 20 min of agent time in eleven `kn-qa` invocations, about 950 000 tokens of
subagent work and 890 tool calls**, plus an hour of setup and re-typing around them, for 73
PASS, 5 FAIL and 32 UNSURE. It found five real defects, which is why it is worth running. This
page is what the run showed about where the time went and the rules that came out of it.
Who runs it and why only in the VM is in [verification](verification.md); the checks
themselves are in [`docs/qa/desktop-checks.md`](../qa/desktop-checks.md).

## Where the time went

Measured from the run's own durations and tool-call counts unless marked as an estimate.

| Cost | What happened | Fix |
|---|---|---|
| Blocked setup | The plan assumed a sandbox on the host reached through a `netsh` port proxy, an elevated shell and `kubectl` on PATH. The sandbox ran in the VM's own WSL (port 6550 was served by `wslrelay`), the shell was not elevated and `kubectl` was only on the refreshed PATH. Found one at a time, with a stop-and-ask each. | `qa-preflight.ps1` reads all of it in ten seconds. |
| Setup by hand | Namespace, seven workloads, a ServiceAccount with a Role, three contexts and a token: about twenty commands pasted from the plan, and again when a batch needed the pods back. | `qa-seed.ps1` (idempotent; `-Reset` between batches; `-Down`). |
| Checks that cannot run | G-11, P-6's drop half and M-10 all needed the connection cut, which the plan did with `netsh interface portproxy` and so needed elevation. Three checks UNSURE, none attempted. | `qa-netcut.ps1`: a loopback forwarder you kill and restart, no elevation; context `qa-cut`. |
| Agents re-learning the tool | In four of eleven batches (L1, G1, T, P) the agent wrote its own `user32` helper for a Shift-click, a drag, a terminal click or a wheel, and each prompt carried a paragraph of quirks. The prompts were long because the check text lived in the prompt. | `qa-ui.ps1` has `click -Modifiers`, `-OffsetX/-OffsetY`, `drag`, `wheel`, `move`; the quirks are in `kn-qa.md`; the checks are in the repo and a prompt is a batch ID. |
| Wrong popup test | `qa-ui.ps1 click` refused an item of the log pane's ⋯ menu that hung past the window's edge (G-5 FAILed). The 0.7.0 fix walked up to "the popup's HWND", but UI Automation gives an Avalonia popup no node of its own: its items are children of the main window. | The point is judged by asking Windows which window is there (`WindowFromPoint`): it must be one of the app's own. |
| Locked console | Between two batches the VM's console locked. `SendKeys` threw "The operation completed successfully", `CopyFromScreen` "The handle is invalid", and `click` reported success and did nothing. The agent lost the end of batch M and a re-run cost 13 minutes. | Every real-input command checks the desktop first and says so; the preflight warns about a screen-saver or inactivity lock. |
| Re-typing results | The orchestrating session copied eleven reports into one Markdown file by hand, then counted verdicts with a throwaway script: about 45 tool calls and a large share of its context. | `qa-result.ps1` records each verdict as it is reached; `qa-report.ps1` builds the report and the counts. |
| Unwinnable checks attempted | Of the 32 UNSURE, about fourteen (an estimate) were known beforehand to need a human (a focus ring, a baseline, a colour), a second cluster, 150 % scaling or something the shared node forbids, and the agents still spent calls on them. | Each check carries a kind tag; `[look]`, `[timing]` and `[opt]` are one attempt, then UNSURE. |
| Commands that lied or could not reach (#314) | `select` on a namespace-picker item and `select`/`invoke` on sidebar rows printed success and changed nothing, so a check could be recorded on a state that never happened; the first `resize` threw `SetWindowPos failed.`; the Open-kubeconfig file dialog had no command at all. | `select`, `toggle` and a selecting `invoke` read the state back after 400 ms and fail when it did not change; InvokePattern says it cannot be read back. `resize`/`move` take the handle from UI Automation rather than `Process.MainWindowHandle` (a per-call guess that can be 0 right after start, the likeliest cause), retry once and name the Win32 error. `-Dialog` scopes any command to the file dialog (class `#32770`), whose name box is `1148` and Open `1`. Written from the code, not yet run in the VM. |
| Slow reads | One `dump` scans the whole window, grids and the sidebar's hundred kinds included: 2.8 s against 0.7 s inside one container, and an agent makes hundreds. | `-Within <AutomationId>`. |
| One hung agent | `qa-chatty` wrote 200 lines a second; at that rate UI Automation threw "Unexpected HRESULT" for minutes, with the scrollback at 4000 and at 200. | 20 lines a second, enough to trim a 200-line buffer in about ten seconds. |
| Wrong check text | G-1 expected `MenuCopy` in the ⋯ menu at 960 DIP (it only appears in a narrower pane), Y-7 searched the palette for "new yaml" (matches nothing on a real cluster either), Y-2 expected three tabs with one already open, C-6 asked for Tab from ☰ but the agent clicked. Each cost a detour. | Corrected in the suite; the notes say what to use instead. |

## What a run is now

```
./scripts/qa-preflight.ps1      # can it start? (ten seconds, fixes nothing)
./scripts/qa-seed.ps1           # fixtures, contexts, the narrow RBAC
./scripts/qa-report.ps1 -Clear
(one kn-qa per batch; each records with qa-result.ps1; qa-seed.ps1 -Reset where a batch says)
./scripts/qa-report.ps1         # the report, counts and failures first
```

The expected saving is the setup, the re-typing, the three checks that could not run and the
detours per batch, not the checks themselves: a UI Automation round trip costs what it cost. A
full run is still tens of minutes of agent time, and that is a target, not a measurement until
the next run records one in this table.

## What the second run (2026-10-13) changed

The run on main at 0b3c20b ([report](https://github.com/Shman4ik/kubeNimbus/issues/327#issuecomment-6148529150))
ran the #314 fixes in the VM for the first time. Every finding below turned a check into an
UNSURE, or into a FAIL that said nothing about the app (#370):

| Finding | Fix |
|---|---|
| `select` on a namespace-picker row read back "selected" and changed nothing (Q-2): that list's selection is a highlight, and it chooses on a press or Enter. | `select` also fails when the item sits in a popup window (its point is on a top-level window of the app other than the main one) that is still open afterwards. A combo's drop-down that closes on the choice passes. |
| The file dialog's `1148` and `1` are Panes with no Value or Invoke pattern on the VM's Windows 11 (Q-3, S-1). | `set-text` writes to the Edit inside the target and reads the text back; `invoke -Dialog` presses a Button inside it, or names `keys -Dialog -Id 1148 -Keys '{ENTER}'`; real input with `-Dialog` brings the dialog forward, not its disabled owner. |
| `dump` printed no "not selected", no expand state and no item status, and there was no `collapse` (C-13, C-16). | `dump` prints `selected`/`not selected`, `expand=…` and `status='…'` whenever the element has them; `collapse` reads its state back. |
| Range drop-down items were refused as "not on one of the app's windows". A ComboBox has the Scroll pattern, and its bounds are the closed box, so its items in the popup were judged clipped by it. | A scrolling ancestor clips a point only when it is drawn in the same native window, and never while it is an expanded drop-down. A point on the app that a list does clip now says so instead. `Show-Window` no longer restores a maximized window, which would close the popup the click was aimed at. |
| The dock's content had no AutomationId, so C-9, C-10 and Y-11 attached whole-window crops. | `DockContent` (accessible-names item 15); the harness's `ux-dock-tab-delete` asserts its peer. |
| The VM's WSL stopped its idle distro a minute after `sandbox-up.ps1 -Wsl`, and both k3s containers with it (Exited (2)); the preflight had run before. `argo-sandbox-up.ps1` had no `-Wsl`. | `scripts/wsl-docker.ps1`: the `docker` routing both scripts share, and a hidden `wsl.exe --exec sleep infinity` recorded in `wsl-keepalive.json` beside the kubeconfig (reused while it lives). The preflight fails a WSL-hosted sandbox with no live keep-alive and reads each `kubenimbus*` container's state. |
| G-10 to G-13 and G-15 to G-20 were not run: the session's permission check refused `kubectl -n qa-shop rollout restart deploy/shop-web` ("Interfere With Workloads"). | The rule below. |
| Check text: K-7 expected a context menu the Applications list does not have, A-3 a menu item named "Refresh from Git", K-6 "five cards" the UIA tree cannot count, P-5 one run's drain numbers. | Rewritten in the suite to what the app does and what a run can read. |

### Batch G2's kubectl

G2 restarts, creates, patches and deletes in `qa-shop`, and an agent's mutating `kubectl` is
refused by default. Every G2 command in the suite is written `kubectl -n qa-shop …`, with the
namespace first, so one pair of prefix rules covers them. The owner adds them to the VM
checkout's **`.claude/settings.local.json`**, which the repository's `.gitignore` names so a
`git add -A` in the VM cannot commit it, and never to the committed `.claude/settings.json`:
that file also loads on the owner's own desktop, where `kubectl` points at real clusters.

```json
{ "permissions": { "allow": [ "PowerShell(kubectl -n qa-shop *)", "Bash(kubectl -n qa-shop *)" ] } }
```

**What the rules let through is wider than G2.** The `*` takes any verb and any further
argument, so `kubectl -n qa-shop delete ns <x>`, `… get pods -A`, `… --context <other>` and
`… --kubeconfig <other file>` all pass. Narrowing them to G2's exact commands does not help:
every command with a variable part (a pod name, the gated pod's manifest) needs a `*`, and a
`*` admits the same extra flags. The preflight's loopback check reads only the kubeconfig it is
given, not what an agent passes to `kubectl`. So the rules are safe only because of what the
VM holds: **the sandbox's kubeconfig is the only one on the VM**, and every context in it
(`kubectl config get-contexts`: the sandbox's and `qa-seed.ps1`'s `qa-prod`, `qa-limited`,
`qa-cut`) points at the VM's own loopback. The owner checks that before adding the rules, and
removes them again if a real cluster's kubeconfig ever lands on the VM. The worst a rule can
then do is break the throwaway sandbox, which `sandbox-up.ps1 -Recreate` puts back.

Without the rules, the agent stops at each G2
mutation and a person runs it: G-10, G-18 and G-19's `rollout restart deploy/shop-web`, G-12's
`create deployment qa-many` and its delete, G-13's gated pod and its `patch`, and G-20's
`delete pod`.

## Rules that came out of it

1. **A prompt names a batch and a path; the checks live in the repo.** Check text in a prompt
   is copied, drifts and costs tokens on every spawn. A check that turned out wrong is
   corrected in `docs/qa/desktop-checks.md`, with what to use instead.
2. **The tool does the input; the agent never writes its own.** A missing capability is an
   UNSURE and a `-Surprise` first, then a line in `qa-ui.ps1`. Real input that silently does
   nothing is a bug in the tool: it checks and says so.
3. **Verdicts are data, not prose.** One JSON line per check; a PASS must quote what was
   observed or the recorder refuses it. The report is generated.
4. **No step of a run needs elevation, a host, or an install.** Anything the run needs from
   the owner (the QA flag, `kubectl`, an unlocked console, the Argo sandbox, 150 % scaling)
   is something the preflight can read, so the owner is asked once, before the run.
5. **Say what a check can decide.** `[look]` is a crop and an UNSURE, once.
6. **A fixture that can hang the tool is a defect in the fixture.** `qa-chatty` is capped at
   a rate the tree survives.

## What is not done, and where it belongs

- **Parallel batches.** One desktop and one pointer; two apps would make every real click
  ambiguous. Batches that need no real input (K, parts of P and M) could share an app, but
  `qa-app.ps1` allows one instance and the saving is small against the risk.
- **Moving checks out of the desktop.** Several checks decide something no real window is
  needed for, and belong in the suites that already run in CI: the capacity strip's "reading…"
  and its 403 wording (M-1, M-3, M-4: a view-model test with a forbidden metrics client; the
  desktop run could not even catch the first), the column chooser's rules (L-10 to L-13), the
  Argo menus on the demo dataset (K-1 to K-7: harness scenarios), the log pane's range and
  snapshot messages (G-7, G-8; the snapshot half is now `LogStatusNoticeTests`), and G-13's retry
  (its rule is now `Live/GatedPodLogsLiveTests` and `LogFollowRetryTests`; the panes' wiring
  between them still needs the desktop). The desktop
  run then keeps what only a desktop shows: real input, popups, focus, a second window, a
  real API server's timing.
- **The 0.7.0 findings themselves** (C-2's wheel, G-13, P-4c, M-4, and G-14's terminal chord)
  are issues, not part of this page.

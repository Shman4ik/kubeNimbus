# kubeNimbus — Claude working notes

Keep this file current in **every** PR, same discipline as pgNimbus. It is the
contract for how this repo is built; if a rule below changes, change it here in
the same change that breaks it.

This file holds the rules, each with its reason in a line. The incidents and measurements
behind them live in [`docs/engineering/`](docs/engineering/), one page per area, linked from
each section. Every session and subagent loads this file and re-sends it with every tool
call, so it stays short: a new rule gets a line here and its evidence goes on the page.

## Chat response style — caveman mode (applies to every session)

Adopted from [JuliusBrussee/caveman](https://github.com/JuliusBrussee/caveman)
(`skills/caveman/SKILL.md`, MIT). Default level **full**. It governs what is said
*in chat*, never what is written to disk — see Boundaries below, which is the
half that keeps it compatible with this file's own prose discipline.

Respond terse like smart caveman. All technical substance stay. Only fluff die.

**Persistence.** Active every response, no revert after many turns, no filler
drift, still active if unsure. Off only on "stop caveman" / "normal mode".
Switch level with `/caveman lite|full|ultra|off`.

**Rules.**

- Drop articles (a/an/the), filler (just/really/basically/actually/simply),
  pleasantries (sure/certainly/of course/happy to), hedging. Fragments OK.
  Short synonyms — *big* not *extensive*, *fix* not *implement a solution for*.
- No tool-call narration, no decorative tables or emoji, no dumping long raw
  error logs unless asked — quote the shortest decisive line.
- Standard well-known acronyms OK (DB/API/HTTP/CRD/RBAC/AOT). **Never invent
  abbreviations** (cfg/impl/req/res/fn): the tokenizer splits them the same as
  the full word, so zero tokens saved and the reader still has to decode. Same
  for causal arrows (→) — own token, saves nothing.
- Technical terms exact. Code blocks unchanged. Errors quoted exact. Numbers and
  units exact.
- **Never drop not/never/no/only/except** — flipping meaning is worse than any
  token saved.
- **Never ADD a word to sound caveman.** Compression only; style never grows
  output. No inserted pronoun or copula to fake broken grammar ("when it not"
  costs one token more than "when not"). Keep the correct verb form when it
  costs the same — "sees" and "see" are both one token, so mangling buys nothing
  and reads worse. If caveman phrasing is not shorter than plain phrasing, use
  plain.
- Tool calls fire direct: no preamble, plan or progress note before or between
  them. After a result, the next call or the final answer — never announce the
  next call. Text before a call only to clarify, to warn about a security or
  irreversible action, or to resolve ambiguity.
- No self-reference. Never name or announce the style; no "caveman mode on", no
  third-person tags, never a normal answer plus a "Caveman:" recap.
- Reply in the language the user writes in. Compress the style, not the
  language.

Pattern: `[thing] [action] [reason]. [next step].`

Not: "Sure! I'd be happy to help you with that. The issue you're experiencing is
likely caused by…"
Yes: "Bug in auth middleware. Token expiry check use `<` not `<=`. Fix:"

**Intensity.**

| Level | What changes |
|---|---|
| **lite** | No filler or hedging. Articles and full sentences stay. Professional but tight. |
| **full** (default) | Drop articles, fragments OK, short synonyms. No tool-call narration, no decorative tables or emoji, no long raw error dumps unless asked. Standard acronyms OK, invented ones never. |
| **ultra** | Strip conjunctions where cause-then-effect stays unambiguous. One word when one word is enough. State each fact once. Still no prose abbreviations and no arrows. Code symbols, function names, API names and error strings are never touched. |

**Auto-clarity — drop caveman when:** warning about security; confirming an
irreversible action (a delete, a scale, a `rollout restart`, a force-apply, a
push); a multi-step sequence where fragment order or an omitted conjunction
risks a misread; compression itself creates technical ambiguity; or the user
asks to clarify or repeats a question. Resume once the clear part is done.

**Boundaries — anything persisted outside the chat is normal prose.** Code,
comments, commit messages, PR and issue bodies, `docs/**`, `CHANGELOG.md`,
`README.md` and **this file** are written the way the rest of this document
demands: full sentences, the reason behind each rule, no compression. The
caveman rules are about the reply in the session, and nothing else. "Open a
defect" or "file a bug" means the same as "open an issue" — the body goes to
other humans, so the body is normal English.

## Mission

A fast, open-source (MIT) Kubernetes desktop client, the sibling of
[pgNimbus](https://github.com/Shman4ik/pgNimbus) and an alternative to Lens. Against its one
open-source native peer, KubeUI, it is the narrower, faster, quieter one: NativeAOT startup,
no telemetry, Kubernetes-first. It opens on the **Applications** mode (apps with health and
a one-line reason from deterministic rules only); the **Resources** explorer is one click
away. **NativeAOT publish is the shipping configuration**, so every dependency must be
AOT/trimming-compatible from day one. Positioning and benchmarks: [mission](docs/engineering/mission.md).

## Tech stack

Full text: [tech-stack](docs/engineering/tech-stack.md).

- **net10.0** everywhere. **Core references only `KubernetesClient.Aot`**; NEVER the
  reflection-based `KubernetesClient`, which does not survive NativeAOT.
- **Kubeconfig files are read by `KubeconfigReader`, never the library's loaders**
  (`LoadKubeConfig*`, `BuildConfigFromConfigFile*`, `BuildDefaultConfig`), which froze
  YamlDotNet; `BannedSymbols.txt` makes a call a build error.
- **The library's certificate validation callback is replaced, never trusted**:
  `ApiServerCertificateValidator` (kubectl's rules) on the HTTP handler and, through
  `ApiServerWebSocketBuilder`, on exec and port-forward (`ApiServerTlsTests`). kubeNimbus
  sends impersonation and reads `tokenFile` itself. The library's typed API is unused; every
  kind goes through the generic JSON watch ([connecting](docs/engineering/connecting.md)).
- **App**: Avalonia 12 (Fluent, DataGrid, AvaloniaEdit, `SvcSystems.UI.Terminal`),
  `CommunityToolkit.Mvvm` source generators (no hand-written INPC), compiled bindings only.
- **Every AvaloniaEdit `TextEditor` goes through `Editing/EditorDefaults`** (`Apply` /
  `ApplyViewer`), which turns link rendering off; `EditorChecks` fails one that skipped it.
- **Tests are TUnit on Microsoft.Testing.Platform. NEVER add `Microsoft.NET.Test.Sdk`** — it
  breaks discovery.
- Nullable enabled; async all the way (no `.Result`/`.Wait()`); DTOs are records.

## The sibling project, and what is shared with it

pgNimbus (`X:\source\pgNimbus`, normally checked out beside this repo) is the
same product for a different database, and the two must look and behave like one
family. The shared half lives in **[`shared/nimbusUi`](shared/nimbusUi/)** — a
git subtree of [nimbusUi](https://github.com/Shman4ik/nimbusUi), referenced as an
ordinary `ProjectReference`:

- `Theme/Tokens.axaml` — the palette, radii, scrollbars, Fluent resource overrides.
- `Theme/Icons.axaml` — the MDI glyphs both apps draw.
- `Theme/Theme.axaml` — the shared style classes (`card`, `layer`, `overlayCard`, `scrim`, `chip`,
  `toolbar`, `searchpill`, `statusBar`, …).
- `Theme/Controls.axaml` — the Fluent **control** retheming: `TextBox`/`ComboBox`/
  `NumericUpDown` radius and brand text selection, `ListBox`/`ListBoxItem`/`TreeView`
  rounded rows, `DataGrid` soft rules, the `.soft` and `.danger` button families,
  `TabControl`. This is the half the first extraction missed, and missing it is
  why the two apps stopped looking alike — see "The design-parity pass" below.
- `Chrome/` — the one-bar window chrome and its drawn caption buttons.
- `Hotkeys.cs` — Ctrl/Cmd resolution; `KubeNimbus.App.Hotkeys` forwards to it and
  adds this app's own gestures.
- **[`DESIGN.md`](shared/nimbusUi/DESIGN.md) — the UI rules, single source.**

Three rules about it:

1. **A change to a shared surface is a change to both apps.** Edit the files in
   place, build kubeNimbus, then `git subtree push --prefix shared/nimbusUi`,
   pull it into pgNimbus and build that too. Both working copies are normally
   open side by side, so this is one session's work, not a follow-up ticket. The
   PR template asks for the paired PR.
2. **The membership test is "can it be described without naming Kubernetes?"**
   If yes it probably belongs up there; if no it stays here. When in doubt leave
   it here — a wrong thing pulled up has to be un-shared against two consumers.
3. **`DESIGN.md` owns the rule text; this file owns the evidence.** The UI rules
   below that are shared say so, and what they keep is the kubeNimbus-specific
   incident that produced them. Don't restate a shared rule here in full — that
   is exactly how the two files started disagreeing.

## Hard architectural rules (non-negotiable)

1. **KubeNimbus.Core has ZERO Avalonia/UI dependencies.** The engine stays
   reusable for a future CLI/test harness. No `Avalonia.*` or
   `CommunityToolkit.Mvvm` types in Core.
2. **Streaming + cancellation everywhere.** Resource lists use list+watch
   (informer-style local cache) so the UI updates live without polling; large
   lists paginate via `continue` tokens and render incrementally via
   `IAsyncEnumerable`. Pod logs stream with follow-mode honoring
   `CancellationToken` mid-stream. Watch connections auto-reconnect with
   resourceVersion resume + relist on 410 Gone; connection loss is surfaced in
   the UI, never a silent hang.
3. **Kubernetes-native, not lowest-common-denominator.** CRDs are first-class
   browsable resources (discovery API, not a hardcoded list). YAML edits go
   through server-side apply with a field manager, showing conflicts. Events,
   `metrics.k8s.io`, and owner-reference navigation (pod → replicaset →
   deployment) are core, not afterthoughts — **shipped**: `ClusterClient.Metrics.cs`
   queries `metrics.k8s.io` with the version read from **discovery** (never
   hardcoded to `v1beta1`), raised as `MetricsUnavailableException` when the
   group is absent or registered-but-unhealthy, so a cluster without
   metrics-server degrades to no CPU/Mem column rather than an error.
4. **No credentials ever persisted by the app.** Kubeconfig is the single source
   of truth (all `$KUBECONFIG` entries + `~/.kube/config`); exec-plugin auth
   (`aws eks get-token`, `gke-gcloud-auth-plugin`, `azure kubelogin`) must work.
   Never copy tokens/certs into app storage; re-resolve through the kubeconfig
   chain at connect time.

## UI design rules

Rules 1, 2, 5, 8, 8b, 9, 11, 12, 14 and 22 are shared with pgNimbus and owned by
[`shared/nimbusUi/DESIGN.md`](shared/nimbusUi/DESIGN.md). **Each rule's full text and the
incident behind it are in [ui-rules](docs/engineering/ui-rules.md); read the ones you touch
before changing a view.** Docs and code cite these numbers.

1. **Minimalist.** Every always-visible control must be justified; secondary actions go in
   the palette or context menus.
2. **Double-click = default action**; Space = quick-peek. Applications rows open on one click,
   because that list is navigation and nothing acts on a selected-but-unopened row.
3. **Multi-cluster via tabs**, each bound to a kubeconfig context and restored by the workspace;
   a cluster that is not a tab yet is reached through the cluster switcher, never a list control.
4. **No hardcoded Ctrl gestures**: `Hotkeys.cs` resolves Ctrl vs Cmd per platform, and labels
   and gestures built in a loop (Ctrl/Cmd+1…9) derive from it.
5. **Opening a resource/YAML never overwrites an active editor tab.**
6. **The sidebar filters and collapses**, because a catalog runs past 100 kinds: sections by
   API group (Kind decides only in the core group), Config/Cluster/CRDs collapsed by default,
   a filter on name, group and short names, Recent (5, per cluster), exactly one kind drawn
   selected (derived in `MarkSelectedKind`), synthetic Helm/Argo rows only with evidence.
7. **The inspector docks along the bottom, full width**, so logs read on long lines. The dock
   states live in code-behind `ApplyDockState`, because a `GridSplitter` fights a binding.
   Its tab strip is a `ListBox` the keyboard and UI Automation reach as tabs, its selection
   pushed one way from `SelectedInspectorTab`, never bound two-way (ENG-54).
7b. **Every region is reachable by Tab and Shift+Tab, every stop shows focus, nothing traps
   it**: a list is one stop the arrow keys move inside (the sidebar, a log pane's list), and
   Ctrl+Tab leaves the YAML editor and the terminal (`ux-keyboard-walk`, accessible-names).
8. **A click target hit-tests across its whole area** (`Background="Transparent"` or taps on
   the items control), with `Cursor="Hand"` and a pressed *class*, never `Border:pressed`.
8b. **A `ToggleButton` gets a two-way `IsChecked` OR a toggling `Command`, never both** — a
   guaranteed no-op that shipped three times.
9. **Every list/panel state gets an explicit visual** — loading, empty, disconnected, conflict,
   no kubeconfig, a failed connect — never a blank rectangle; a command that cannot run is
   disabled, never a silent no-op.
10. **An inspector panel gets two rows of chrome**, the tab strip being one, because every row
    comes out of a ~300px dock (`ListBox.segmented` + `TabControl.headerless`).
11. **Labels above inputs (`fieldLabel`), state in an InfoBar (`Border.infoBar`)**, WinUI's own
    patterns; a pair whose one half is always disabled is one control.
12. **The command bar is the title bar**, and the window never prints its own name. Use
    `WindowDecorationProperties.ElementRole` (never `BeginMoveDrag`; every control in the bar
    takes `User`); on Windows the caption buttons are ours (`PART_*` names are load-bearing);
    the caption reserve follows `WindowDecorationMargin.Top`; Linux keeps system decorations.
    The harness cannot render any of it.
13. **The list has its own search box. `Rows` stays the watch's list; the grid renders
    `VisibleRows`** (`ClusterTabRowFilterTests`). It matches only a kind's identity fields, a
    per-kind table written once in [list-search](docs/engineering/list-search.md) and
    `RowFilterFields`, never status; no-match is its own state; Unhealthy only is a second narrowing.
14. **A `DataGridCell` has a gutter on both sides** (a right-aligned `—` beside `5d` read as a
    negative age), and the column minimums pay for it; check `cluster-tab-workloads-list` at 1280px.
15. **The two apps' command bars read the same left to right** (☰, sidebar toggle, the app's
    middle, search, theme, ⚙, ?), and ☰ ends with Preferences…, Keyboard shortcuts, About.
16. **This app has exactly one window**; a second would need pgNimbus's DWM caption fix.
16b. **A panel you open, use and dismiss is an `OverlayPanel`**, bound two-way to `Is…Open` with
    no closing command; it takes focus and gives it back. The palette and switcher are not ones.
17. **An action that destroys, disrupts or cannot be taken back arms the `RowActionStrip`;
    one that can be taken back fires on the click** (`RowActionViewModel.FiresOnClick`;
    handing kubectl exec or a node shell to your own terminal is one). An ellipsis means it
    asks; the strip names the cluster; production deletes always ask.
18. **While waiting, say so; never show a verdict not yet had**: `Reset` means started,
    `Synced` (or the first row) ends loading, every wait ends even on error
    (`ClusterTabLoadingStateTests`). Reason about a second of latency.
19. **Both modes sit on the same surface** (lists and the dock in a `card`, `layer` for
    overlays only) and use the same table type (DESIGN.md rule 14).
20. **Nothing repeats what is on screen, and nothing states a fact it does not have.**
21. **A UI change re-renders the published screenshots** (`design/screenshots/`,
    `design/store/screenshots/`) **in the same PR**, on Windows; a PR that cannot says so and
    files an issue.
22. **Every tooltip answers the pointer**: `ToolTipHitTesting.Install()` gives a background to
    whatever lacks one, so never write `Background="Transparent"` beside a `ToolTip.Tip`.
23. **Fonts are settings; no view writes a font name**: the interface face comes from
    `AppSettings.InterfaceFont`, code uses the `mono` class or `MonoFont` (`FontChecks`).

## Feature deep dives (docs/engineering/)

**Read the page for any feature you change before changing it, and keep it current in the
same PR.** Read only those pages. [The full index](docs/engineering/README.md) describes each.

- Connecting and clusters: [connecting](docs/engineering/connecting.md),
  [cluster-switcher](docs/engineering/cluster-switcher.md),
  [several-namespaces](docs/engineering/several-namespaces.md),
  [fleet-views](docs/engineering/fleet-views.md),
  [machine-terminal](docs/engineering/machine-terminal.md).
- Applications and GitOps: [applications-mode](docs/engineering/applications-mode.md),
  [argo-cd](docs/engineering/argo-cd.md), [helm-releases](docs/engineering/helm-releases.md).
- Logs: [multi-pod-logs](docs/engineering/multi-pod-logs.md),
  [row-logs-and-maximized](docs/engineering/row-logs-and-maximized.md),
  [log-pane-reading](docs/engineering/log-pane-reading.md),
  [log-severity-classes](docs/engineering/log-severity-classes.md).
- The resource list: [crd-printer-columns](docs/engineering/crd-printer-columns.md),
  [resource-grid-resize-sort](docs/engineering/resource-grid-resize-sort.md),
  [events-list](docs/engineering/events-list.md),
  [list-search](docs/engineering/list-search.md),
  [unhealthy-only](docs/engineering/unhealthy-only.md),
  [datagrid-auto-columns](docs/engineering/datagrid-auto-columns.md),
  [status-dot](docs/engineering/status-dot.md).
- Detail panes and actions: [pod-overview-tab](docs/engineering/pod-overview-tab.md),
  [requests-and-limits](docs/engineering/requests-and-limits.md),
  [configmaps-and-secrets](docs/engineering/configmaps-and-secrets.md),
  [workload-actions](docs/engineering/workload-actions.md),
  [networking-detail](docs/engineering/networking-detail.md),
  [port-forward](docs/engineering/port-forward.md),
  [node-operations](docs/engineering/node-operations.md),
  [exec-terminal](docs/engineering/exec-terminal.md),
  [apply-preview](docs/engineering/apply-preview.md),
  [metrics](docs/engineering/metrics.md), [meter-track](docs/engineering/meter-track.md),
  [rbac-access-review](docs/engineering/rbac-access-review.md).
- Shell: [sidebar-width](docs/engineering/sidebar-width.md),
  [sidebar-plural-labels](docs/engineering/sidebar-plural-labels.md),
  [macos-menu-bar](docs/engineering/macos-menu-bar.md),
  [accessible-names](docs/engineering/accessible-names.md),
  [theme-toggle-string](docs/engineering/theme-toggle-string.md).

## The demo cluster

`ClusterContext.Demo` is a built-in dataset with no cluster behind it, so a Store reviewer
or an evaluator sees the app work. Full text: [demo-cluster](docs/engineering/demo-cluster.md).

1. **A demo tab is a `ClusterContext` whose `KubeconfigPath` is the `"<demo>"` sentinel**; it
   classifies as Development.
2. **It has no `ClusterClient`** (`Client` stays null), so it cannot touch the network. Never
   add an offline client pointed at a dead port.
3. **One dataset** (`src/KubeNimbus.App/Demo/`), which the harness's fixtures pass through to.
4. **What can work, works through production code**; one metrics entry per running pod.
5. **What cannot work says so in place**, with its commands disabled.
6. **Nobody may mistake it for a real cluster**: a `demoBar` stays for the tab's life.

## The privacy policy and the Store listing text

[`PRIVACY.md`](PRIVACY.md) is a contract like this file, and a Store certification has
already failed on it once: the 2026-09-28 submission (0.4.0) was rejected under policy 10.5.1
because the listing's privacy URL pointed at `SECURITY.md`, which says what the app does not
do but not what it collects, stores or shares. Four rules keep that from happening again:

1. **The address is a fixed point.** `https://github.com/Shman4ik/kubeNimbus/blob/main/PRIVACY.md`
   is in Partner Center (Properties → Privacy policy URL) and in `AboutView`. Renaming or moving
   the file is a change to both plus the README and `SECURITY.md`, and a listing that points at a
   page that stops resolving fails the same check. Never point the field at another document.
2. **A change to what the app stores or sends changes the page in the same PR.** A new file under
   `AppDataDirectory`, a new setting persisted, a new network destination or a new button that
   opens a URL is a line in `PRIVACY.md` and a new "Effective" date. Hard rule 4 and the
   "No telemetry" paragraph of `SECURITY.md` are the same claims from the security side.
3. **The policy says only what the code does.** Its file list is checked against
   `AppSettings`, `WorkspaceSettings`, `DiscoveryCache` and `TerminalLauncher`, and its network
   list against a grep for `HttpClient` and `Process.Start` in `src/`. A policy that over-claims is
   as wrong as one that omits.
4. **The listing text lives in [`design/store/listing/store-listing.md`](design/store/listing/store-listing.md)**
   — What's new, short description, description, features, keywords, certification notes and the
   privacy URL — so it is reviewed like code. Nothing uploads it; pasting it into Partner Center is a
   manual step, like the screenshots.

## Settings, and what belongs in which file

`settings.json` (`AppSettings`, Core) is *preferences*; `workspace.json` (`WorkspaceStore`,
App) is *session*. Deleting the workspace loses tabs and nothing else; deleting the settings
resets preferences and closes no cluster. Full text: [settings](docs/engineering/settings.md).

1. **Every setter goes through `App.Update(s => s with { … })`**, never a cached snapshot.
2. **The store validates** (`AppSettings.Normalized()` on read and write, clamping).
3. **A setting nothing reads is worse than no setting.**
4. **Nothing here may become a credential**; kubeconfig entries are paths only.
5. **Tests and the harness redirect both stores** (`DirectoryOverride`).

The preferences page applies immediately, has no OK/Cancel, and has exactly four tabs
(General, Appearance, Logs and metrics, Changes); a new setting goes on one of them.

## Workload detail and namespace navigation

Workload and node detail sync their pod grids' selection in code-behind, never through a
two-way `SelectedItem`. The namespace picker offers a typed name only until the namespace
list has been read. Full text: [workload-detail](docs/engineering/workload-detail.md).

## The command catalog (shortcuts, palette, cheat sheet, docs)

`KubeNimbus.Core/Commands/` is the single source for every command and gesture; the App
projects it. Full text: [command-catalog](docs/engineering/command-catalog.md).

- **Core stays UI-free** (`CommandKey`, not Avalonia's `Key`).
- **Gestures are properties**, rebuilt (cleared first) when the Ctrl/Cmd scheme changes.
- **Every open-logs gesture goes through `OpenLogsForAsync` / `OpenNamedLogsAsync`.**
- **An action with no gesture is `PaletteOnly`.** `ChordModifiers.Control` is literal Ctrl.
- **Single-letter row keys are list-scoped, never window bindings.**
- **`docs/keyboard-shortcuts.md` is a golden file**; `KUBENIMBUS_UPDATE_DOCS=1` regenerates it.

## The Advanced view

One persisted boolean, default on, that governs **only** which kinds the sidebar lists
(`SidebarGrouping.BasicViewKinds` when off). Nothing outside the sidebar may be gated on it
(`SidebarAdvancedSectionTests`), it never restarts a watch, and the filter and palette still
reach a hidden kind. Full text: [advanced-view](docs/engineering/advanced-view.md).

## Repository layout

```
src/KubeNimbus.Core        Engine: kubeconfig, ClusterClient (watch/logs). No UI.
src/KubeNimbus.App         Avalonia 12 desktop shell.
tests/KubeNimbus.Core.Tests  TUnit unit + integration tests; the latter skip with no cluster.
tests/KubeNimbus.App.Tests   TUnit view-model tests. No Avalonia app, no cluster, no display.
tools/Screenshot           Headless visual-verification harness. Dev-only.
design/                    Logo masters (.af) + generated SVG/masters/store/screenshots.
installer/                 Packaging inputs: macOS Info.plist, .desktop, MSIX manifest.
scripts/                   Sandbox bootstrap, the icon/logo pipeline, and the installer builds.
.oss-scanner/              Image + threat model for Anthropic's OSS Scanner (see below).
```

`.oss-scanner/` ([repository-layout](docs/engineering/repository-layout.md)): its k3s version follows
`scripts/sandbox-up.sh`, and a change that moves a trust boundary or accepts a finding by
design updates `threat_model.md` in the same PR.

Each public doc has one job; don't duplicate between them (the table is in
[repository-layout](docs/engineering/repository-layout.md)): `README.md` (deciding to
download), `CONTRIBUTING.md` (opening a PR, releasing), `SECURITY.md` (reporting, the
security model), `PRIVACY.md` (the privacy policy), `CHANGELOG.md` (release history, read by
the release workflow), `CODE_OF_CONDUCT.md`, this file (changing the code),
`docs/product-loop/` (the train), `docs/BACKLOG.md` (how the issue backlog works),
`docs/PRE-LAUNCH-CHECKLIST.md`, `docs/RELEASE-CHECKLIST.md`.

## The release train

Work ships as **trains** of 5–10 deliverables, one step per `/release-train` invocation
([skill](.claude/skills/release-train/SKILL.md)). The train selects its own work and the owner
steers through `TRAIN.md`; state lives on the train branch and every step pushes it;
verification debt becomes an issue; `MAX_FIX_ROUNDS` ends in a revert; each train starts from
a new survey. Full text: [release-train](docs/engineering/release-train.md).

**Agents**: `kn-implementer` (Opus, medium) builds one item; `kn-verifier` (Sonnet, high, no
Edit tool) checks it; `kn-researcher` (Opus, medium); `kn-bundle` (Opus, medium) builds
related issues as one PR and never edits `CHANGELOG.md`; `kn-qa` (Sonnet) drives a running
app. Opus 5.5 at medium does what Opus 5 did at high; Sonnet below high can stop early or
report a check it never ran. Every agent prompt says what ends its run and forbids extra
review rounds and scope.

**Token cost** (2026-10 backlog sweep, #259): finished subagents used about 2.4M tokens in a
few hours — `kn-bundle` runs of 377k–485k with 150–180 tool calls, a `kn-qa` run of 316k with
full-desktop screenshots, verifiers re-running all of CI. The main driver was this file at
163 KB, paid again on every tool call of every subagent. So:

- **No agent re-reads this file** (it is in context); it reads only the engineering pages for
  the features it touches.
- **The verifier takes build, tests, harness checks and the AOT publish from the PR's CI**
  (`gh pr checks`), re-runs only targeted and live tests, and reads only cropped PNGs of the
  scenarios the diff touches.
- **Desktop checks (launching the app, real input, `kn-qa`) run only inside the owner's
  Hyper-V QA VM, never on the owner's desktop**; `scripts/qa-app.ps1` refuses without
  `KUBENIMBUS_QA_VM=1`. Elsewhere they go in the PR body as numbered checks.
- **Screenshots are cropped to the element**, never a whole desktop.

## App icon / logo assets

Moved to [`design/CLAUDE.md`](design/CLAUDE.md), which loads when working under `design/`. The short version: nothing in `design/*.svg` is hand-edited (the `.af` files are the art), and the base and broom are shared byte-for-byte with pgNimbus, so a change to either is a pair of PRs.

## The AOT watch/log implementation (important, non-obvious)

Full text: [aot-watch-and-discovery](docs/engineering/aot-watch-and-discovery.md).

- `KubernetesClient.Aot` has no `WatchAsync`: `ClusterClient` streams watches and log follows
  on the client's own `HttpClient`, with auth from `Credentials.ProcessHttpRequestAsync`.
- **A failing exec plugin is reported by what it printed** (`ExecCredentialCapture`).
- **Every parse of cluster JSON goes through `ClusterJson`**; one unreadable object is
  skipped and named, never the end of a watch.
- **Both streams have a line cap** (`BoundedLineReader`).
- **A name from another object never builds a path unchecked** (`ResourceDescriptor`).
- **A 401 is not transient**: `RefreshCredentialsAsync` swaps the client in place; never cache
  what a plugin returned, and read `_client` once per operation.
- Runtime kinds, pods included, go through `WatchResourceAsync` with `DynamicResource`: one
  live-list path. **A watch reaches the UI thread in batches**, never one hop per event.

## Discovery, server-side apply, events, exec, port-forward

Aggregated discovery with a per-group fallback; a kind is listable when its verbs name
`list` or are absent (`IsListable`). Server-side apply sends JSON from `YamlJson.cs`, which
uses only YamlDotNet's structural model, never its reflection serializer. Exec is WebSocket;
port-forward opens one websocket per local connection.

## Sandbox cluster bootstrap (how tests get a real cluster)

Integration tests run against a real local cluster (`./.sandbox/kubeconfig.yaml` or
`$KUBENIMBUS_TEST_KUBECONFIG`), gated on a reachability probe, and **a skipped test is
reported as skipped**. The live tests share the sandbox with other sessions: each run mutates only
a namespace of its own (`kn-live-<run id>`, labelled so a later run sweeps it if the run was
killed) and never drains the node. Start it with `./scripts/sandbox-up.ps1`
(`scripts/README.md`). Full text: [sandbox-cluster](docs/engineering/sandbox-cluster.md).

## Verification workflow

Full text, with the incident behind each trap: [verification](docs/engineering/verification.md).

```powershell
dotnet build KubeNimbus.slnx
./scripts/test.ps1        # both suites; ./scripts/test.sh on Linux and macOS
dotnet run --project tools/Screenshot -- <scratch dir> [scenario filter]
dotnet run --project tools/Screenshot -- --stress
dotnet publish src/KubeNimbus.App -c Release -r win-x64 -p:PublishAot=true -o publish/app
publish/app/kubeNimbus --smoke-test        # Linux: wrap in xvfb-run -a
```

- **Never trust a green test run without a count.** A positional csproj runs nothing and
  exits 0; `dotnet test --project` on this machine's SDK 10.0.400-preview reports "Zero tests
  ran". `scripts/test.ps1` runs the test executables directly and fails a zero-test run.
- **A clean publish is not a working binary**: launch it with `--smoke-test` (and
  `--smoke-test=unreachable-cluster`). Never call the synchronous `ClusterClient.Connect` on
  the UI thread. Without MSVC, publish linux-x64 for the same AOT analysis.
- **View-model tests** start no Avalonia app and redirect both stores and the kubeconfig
  search; a test that writes and reads back a setting is `[NotInParallel]`.
- **The screenshot harness is the XAML smoke test.** Host views in a real `MainWindow`; two
  runs of one commit must produce the same PNGs.
- **The stress mode's budgets are counts**; a list bound to cluster data virtualizes and is
  rebuilt with one notification.
- **`kn-qa` runs only in the owner's QA VM** (`KUBENIMBUS_QA_VM=1`), never on the owner's
  desktop.

### NativeAOT publish needs the MSVC toolchain (Windows)

The ILCompiler links with `link.exe` and locates it via `vswhere.exe`. On this
machine the raw `dotnet publish -p:PublishAot=true` fails with
`'vswhere.exe' is not recognized` unless run from a VC dev environment **with the
VS Installer dir on PATH**. Working invocation:

```bat
call "C:\Program Files\Microsoft Visual Studio\18\Insiders\VC\Auxiliary\Build\vcvars64.bat"
set "PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer"
dotnet publish src\KubeNimbus.App\KubeNimbus.App.csproj -c Release -r win-x64 -p:PublishAot=true -o publish\app
```

Known AOT warnings today: `Avalonia.Controls.DataGrid` emits IL2104/IL3053 trim
warnings. The publish still succeeds and the app runs; revisit if DataGrid gets
an AOT-clean release. Do not let *new* trim/AOT warnings from our own code slip
in unnoticed.

## Release, CI and packaging

The release workflow, installers, Microsoft Store (MSIX) identity, the assembly-name coupling and the 0.5 GB Actions storage budget are in the `release` skill ([`.claude/skills/release/SKILL.md`](.claude/skills/release/SKILL.md)). Load it before cutting a release or touching `.github/workflows`, `installer/` or the packaging scripts. Rules that must not wait for it: **every `upload-artifact` sets `retention-days`**, **the MSIX identity in `installer/msix/Package.appxmanifest` is never edited**, **every `actions/checkout` sets `persist-credentials: false`** (no step runs git against the remote; `gh` takes `GH_TOKEN` from env), **a tool a release downloads and runs is pinned by version and SHA-256 and checked before it runs** (appimagetool and the AppImage runtime it embeds, in `scripts/linux/build-packages.sh`), and **packages come from nuget.org only**, through the root `nuget.config` (`<clear />` plus a source mapping), so a developer's own feeds cannot supply one. Provenance is attested in the `release` job only, and a real release is dispatched from `main` only; the skill says why (2026-10, security audit block 5). The Windows direct download is a portable zip since 2026-10-04 (the MSI is gone), and NativeAOT is deliberately not compiled for size (`OptimizationPreference=Size` was measured: 1.2 MB off the download for slower startup and about 20 MB more memory); both are reasoned in the skill.

**Repository settings are kept the same in kubeNimbus and pgNimbus** (2026-09-30; nimbusUi carries the security half). On `main`: the required checks (`Build & test` and `dependency-review`), resolved threads, no force push or deletion, and **no required approval** (CODEOWNERS only names who is asked). `v*` tags sit under a `Release tags` ruleset (create, never move or delete), releases are immutable, every `uses:` is pinned to a commit SHA (`sha_pinning_required`), Dependabot alerts and security updates, secret scanning with push protection and private vulnerability reporting are on, and merged branches are deleted. A change to one repo's settings is made to the other in the same session.

## History

The MVP scope and the per-pass verification log (what each pass shipped, verified and left unverified) are in [`docs/status-history.md`](docs/status-history.md). Record a new pass there, not here.

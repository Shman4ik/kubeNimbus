# kubeNimbus — Claude working notes

Keep this file current in **every** PR, same discipline as pgNimbus. It is the
contract for how this repo is built; if a rule below changes, change it here in
the same change that breaks it.

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

A fast, open-source (MIT) Kubernetes desktop client — the Kubernetes sibling of
[pgNimbus](https://github.com/Shman4ik/pgNimbus). An alternative to Lens.

The 2026 Kubernetes GUI market has one crowded end and one thin one. Lens is
subscription-gated for commercial use (Mirantis moved exec/logs/shell into
proprietary code in 6.3) and a heavy Electron app; OpenLens is dead; FreeLens
(the surviving fork) is still Electron, and so is Headlamp's desktop shell;
Aptakube is fast and polished but closed and paid; k9s is a keyboard TUI.
**The one true peer is [KubeUI](https://github.com/IvanJosipovic/KubeUI)** —
MIT, Avalonia 12, .NET 10, actively released and feature-comparable; the only
other native open-source client, [Seabird](https://github.com/getseabird/seabird)
(Go/GTK4), has had no commit since August 2025. So the claim is **not** "nobody
ships open source + native". KubeUI is **not** NativeAOT and cannot cheaply
become so — it ships ReadyToRun self-contained on the reflection-based
`KubernetesClient`, and generates CRD models with Roslyn at runtime — and that
is where kubeNimbus differs measurably: **~156 ms to first window against
~645 ms, a ~62 MB payload against a 382 MiB single file** (measured head to
head, linux-x64, `docs/research/2026-08-17-kubeui-positioning.md`), plus **no
telemetry** where KubeUI's is on by default. kubeNimbus is the narrower, faster,
quieter one: Aptakube's polish, NativeAOT startup, MIT, Kubernetes-first.

Where KubeUI is ahead and we are not: signed and notarized binaries, auto-update,
winget/Store/Homebrew distribution, and schema-aware YAML completion. Node drain,
server-side dry-run and *installers* were on that list and are not any more — see
"Node operations", "The apply preview" and "Installers" below; what is left of the
installer gap is auto-update and a certificate, not the packages themselves. None of the rest is a
reason to change course; all of it is a reason not to write a comparison table yet.

**Headline benchmark:** ~150 ms to first frame (vs Electron's seconds) —
`--smoke-test`, which waits for a real compositor tick, reported **103–108 ms**
on a published linux-x64 binary. That is a *different event* from the ~156 ms
above and not a contradiction of it: the head-to-head figure comes from a
cross-app harness that polls for a **mapped window**, the only thing both apps
could be measured on identically, and it therefore reads high for kubeNimbus —
the comparison is deliberately the less flattering of the two. Both numbers are
recorded in `docs/research/2026-08-17-kubeui-positioning.md`. NativeAOT publish
is the *shipping* configuration, not an afterthought — every dependency choice
must be AOT/trimming-compatible from day one.

## Tech stack

- **net10.0** everywhere. NativeAOT is the shipping config.
- **KubeNimbus.Core** — references ONLY the official Kubernetes client, via the
  **`KubernetesClient.Aot`** package (source-generated serialization). NEVER swap
  it for the reflection-based `KubernetesClient` — that one does not survive
  NativeAOT.
- **KubeNimbus.App** — Avalonia 12 (Fluent theme, Inter font, DataGrid,
  AvaloniaEdit for YAML, `SvcSystems.UI.Terminal` over `XTerm.NET` for the exec
  pane — see "The exec terminal"), `CommunityToolkit.Mvvm` source generators
  (`[ObservableProperty]`/`[RelayCommand]`, no hand-written INPC).
  `AvaloniaUseCompiledBindingsByDefault=true`; no reflection bindings.
- **KubeNimbus.Core.Tests** — TUnit on Microsoft.Testing.Platform. **NEVER add
  `Microsoft.NET.Test.Sdk` to a TUnit project — it breaks discovery.** The
  runner is pinned in `global.json` (`test.runner = Microsoft.Testing.Platform`).
- Nullable enabled; async all the way (no `.Result`/`.Wait()`); DTOs are records.

## The sibling project, and what is shared with it

pgNimbus (`X:\source\pgNimbus`, normally checked out beside this repo) is the
same product for a different database, and the two must look and behave like one
family. The shared half lives in **[`shared/nimbusUi`](shared/nimbusUi/)** — a
git subtree of [nimbusUi](https://github.com/Shman4ik/nimbusUi), referenced as an
ordinary `ProjectReference`:

- `Theme/Tokens.axaml` — the palette, radii, scrollbars, Fluent resource overrides.
- `Theme/Icons.axaml` — the MDI glyphs both apps draw.
- `Theme/Theme.axaml` — the shared style classes (`card`, `layer`, `chip`,
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

> Rules **1, 2, 5, 8, 8b, 9, 11, 12 and 14 are shared with pgNimbus**, and their
> canonical statement is in [`shared/nimbusUi/DESIGN.md`](shared/nimbusUi/DESIGN.md)
> (as its rules 1, 2, 3, 5, 6, 7, 8, 9 and 12). What is kept below is the
> kubeNimbus incident behind each one — the concrete failure is the reason the
> rule is believed, and it is worth more here than a second copy of the rule.
> Change a shared rule in DESIGN.md, not here. Rules 3, 4, 6, 7, 10 and 13 are
> this app's own.
>
> Rules **15 and 16** are a third kind: they are about *matching* pgNimbus rather
> than about either app alone, so they live here (the chrome they describe is
> this app's) but a change to either is a change both apps should get.

1. **Minimalist.** Every always-visible control must be justified; default answer
   is no. Secondary actions live in a command palette (Ctrl+K) or context menus.
2. **Double-click = default action** everywhere (pod → logs/describe, deployment
   → details, context → connect); Space = quick-peek.
3. **Multi-cluster via tabs** (like pgNimbus query tabs): each tab bound to a
   kubeconfig context; drag-reorder; workspace snapshot restores tabs. Reaching
   a cluster that isn't already a tab goes through the **cluster switcher**, never
   a list control — see "The cluster switcher" below.
4. **No hardcoded Ctrl gestures** — [`Hotkeys.cs`](src/KubeNimbus.App/Hotkeys.cs)
   resolves Ctrl vs Cmd per platform; palette labels and cheat sheet derive
   from it. This includes gestures built in a loop (Ctrl/Cmd+1…9 for tab jumps
   are registered from `Hotkeys.Primary` in code-behind, not nine XAML
   `KeyBinding`s).
5. **Opening a resource/YAML never overwrites an active editor tab.**
6. **The sidebar filters and collapses, it doesn't just scroll.** A cluster's
   resource catalog (built-ins + CRDs) commonly runs past 100 kinds; the
   sidebar's filter box + collapsible sections (Config, Cluster and CRDs
   collapsed by default — `SidebarGrouping.IsExpandedByDefault`) are
   load-bearing UX, not optional polish — any new sidebar content must stay
   filterable and collapsible. There are **seven** discovery-driven sections, and
   each one past the original five was split out for the same reason: `Cluster`
   because Config had become the catalog's junk drawer, and `Argo` because
   `argoproj.io` is eight or more kinds on any cluster running Argo and they were
   all landing in CRDs, which is where the same complaint starts over. Measured
   on a bare k3s the old bucketing gave Workloads 8, Network 6 and **Config
   33** — APIServices, CSRs, ClusterRoles and the whole of flowcontrol,
   admissionregistration, apiregistration and coordination, all filed as
   "configuration", and expanded on connect. The cause was that bucketing was
   **Kind-first**: it named the kinds it wanted and dropped everything
   recognized-but-unlisted into Config. It is now **by API group** outside the
   core group (Kind still decides inside `""`, the one group that holds
   workloads, networking, storage and machinery at once), which has no such
   residue — and stops a CRD that happens to be called `Deployment` from being
   classified as a built-in workload, which the old rule did.
   The filter matches display name, **API group and short names**
   (`SidebarKindViewModel.Matches`), because the group is the only thing
   telling two same-named CRD kinds apart and "svc"/"po" is how people think.
   A pinned **Recent** section (top, max 5, session-scoped) holds the kinds
   most recently selected. Two sections carry a **synthetic** row on top of the
   discovered kinds — Helm's release browser and Argo's GitOps dashboard — and
   both are gated on evidence the cluster actually has that thing (a release
   Secret; the Application kind in discovery), because a row that opens on
   nothing is the always-visible control rule 1 says to default to no.
7. **The inspector docks along the bottom (Lens-style), not in a side sidecar.**
   The resource list fills the content area's width; opening a resource docks a
   detail/logs/exec/YAML tab under it, full-width, so logs and terminals read on
   long lines instead of wrapping in a cramped column. A draggable `GridSplitter`
   resizes the dock and any inspector tab kind can be maximized to fill the
   content area (`ClusterTabViewModel.IsInspectorMaximized`). The three dock
   states (hidden / split / maximized) are driven from `ClusterTabView`'s
   code-behind `ApplyDockState` by mutating the content grid's row heights —
   a `GridSplitter` mutates `RowDefinition.Height` directly and would fight a
   one-way height binding, which is why this is code-behind, not XAML.
8. **A click target must hit-test across its whole area, and say it is one.**
   In Avalonia a `Panel` or `Border` with a **null** `Background` does not
   hit-test where no child covers it, and a container's own `Padding` lies
   outside its content template entirely. A pointer handler on an item
   template's root panel therefore fires on the text and nowhere else — the
   row highlights on click but does nothing, which reads as "is this one click
   or two, or is it broken?". Handle taps on the **items control** and resolve
   the row from the event source (`OnSwitcherListTapped`), or give the target an
   explicit `Background="Transparent"`. Anything clickable also gets
   `Cursor="Hand"` and a pressed state — and `:pressed` is a pseudo-class only
   button-like controls set, so on a `Border` it must be a real class toggled
   from the pointer handlers (`Border.clusterTab.pressed`), never
   `Border.clusterTab:pressed`, which compiles and silently never matches.
8b. **A `ToggleButton` gets EITHER a two-way `IsChecked` binding OR a toggling
   `Command` — never both.** `ToggleButton.IsChecked` is registered
   `defaultBindingMode: TwoWay`, and `ToggleButton.OnClick()` calls `Toggle()`
   **before** `Button.OnClick()` invokes the `Command`. So a control wired with
   both flips the property twice per click and lands exactly where it started:
   a guaranteed no-op that compiles, renders, animates its checked state, and
   does nothing. This shipped three times — pod detail's **Follow** (which
   stopped the stream it was meant to start, so logs never streamed at all),
   pod detail's **Previous** (which started a live follow instead, making
   `LoadPreviousLogs` unreachable from the UI — the single most important
   CrashLoopBackOff gesture in the app), and the YAML editor's Secret
   **Reveal values**. Put the work in the generated `On<Property>Changed`
   partial; `ShowLogTimestamps`, `WrapLogLines` and `IsFleetView` are the
   correct precedent. If a command is genuinely needed (the palette, a
   screenshot fixture), give it an explicit target value rather than an
   inversion — `MainWindowViewModel.SetAdvancedView(bool)` is the pattern —
   so it cannot race the control's own toggle.
9. **Every list/panel state gets an explicit visual** — loading, empty,
   disconnected, conflict, delete-confirm — never a blank rectangle that
   looks like a bug. `ClusterTabViewModel.IsListLoading`/`IsListEmpty` is the
   pattern to extend for new list-backed views. This includes the **shell's
   own** empty state: with no kubeconfig, `MainWindowViewModel.HasContexts`
   is false and the content area explains what was searched
   (`Kubeconfig.CandidatePaths()` reports missing paths too, which is the
   whole reason it exists alongside `DiscoverPaths()`) and offers a rescan —
   because `$KUBECONFIG` is not inherited by a GUI launched from Explorer/VS,
   and "empty dropdown, dead + button" is the most likely first-run
   experience there. Any command that cannot run must be disabled
   (`AddNewTabCommand`'s `CanExecute`), never silently no-op.
10. **An inspector panel gets two rows of chrome above its content, and the tab
   strip is one of them.** The dock is ~300px by default and every stacked row
   comes straight out of the thing you opened the panel to read. Pod detail
   shipped with four — owners, containers, tab strip, per-tab toolbar, plus a
   filter box and a "Following …" caption on Logs — which is ~200px of a 300px
   dock spent before the first log line. A `TabControl` cannot host anything but
   tabs on its header row, so the pattern is `ListBox.segmented` (the strip) +
   `TabControl.headerless` (the content) sharing one `Grid` row with the selected
   tab's tools, gated by `IndexEqualsConverter` on the same index the TabControl
   binds. The TabControl stays underneath because nothing else gives *both*
   lazily-realized tab content and an index that survives a hidden tab, and
   `SelectedDetailTabIndex` (Logs=0, Env=1, Events=2, Usage=3) is depended on by
   `ClusterTabViewModel.OpenLogs` and the screenshot scenarios. A panel-level
   title row is the other thing to check for: the dock tab above already reads
   `Pod/<name>` / `Helm/<name>` / `Access/<ns>`, so a row that repeats it is a
   row spent on nothing (this is why `HelmReleaseView` and `RbacView` no longer
   have one).
11. **A form puts its label above the input, and its state in an InfoBar.** Both
   are WinUI's own patterns ([Fluent basics][fluent-basics]), and both replace
   something that had gone wrong by hand. A label *beside* its input sits in an
   `Auto` column with no gap of its own, so "Local port" ran straight into its
   own text box in the port-forward pane, and every pane that tried invented a
   different hand-tuned spacer column; `TextBlock.fieldLabel` above the control
   has nothing to collide with and takes the field's own width. State was a bare
   `Ellipse.statusDot` next to a sentence, which carries the information only
   for someone who already knows the colour code; `Border.infoBar` (+ `.success`
   / `.warn` / `.error`, severity as a bound class) states it. Two more things
   the port-forward pane settled: fields read in the direction the traffic goes
   and the status line prints (**local → pod**, it used to read pod-first), and
   a control pair where one half is always disabled is one control — Start and
   Stop are the same slot, swapped on `IsRunning`, not a live button beside a
   dead one.
12. **The command bar *is* the title bar, and nothing in the window says its own
   name.** One row of chrome at the top, not two: `MainWindow
   .ConfigureWindowChrome` sets `ExtendClientAreaToDecorationsHint` on Windows
   and macOS, and the 40px `CommandBar` carries the caption. The wordmark went
   with it — the window title and the taskbar/Alt+Tab icon already carry the
   identity, and the bar was printing the title back at itself 32px lower (the
   same argument that had already removed the glyph beside it). Four things
   about this are easy to get wrong:
   - **Roles, not `BeginMoveDrag`.** Avalonia 12 replaced
     `ExtendClientAreaChromeHints` with
     `chrome:WindowDecorationProperties.ElementRole`; `TitleBar` on the bar maps
     to Win32 `HTCAPTION`, which is what keeps dragging, double-click-to-maximize,
     the right-click window menu and Win11 Snap Layouts. Hand-rolling the drag
     reproduces one of those four and silently loses three. Every interactive
     control in the bar must then opt back in with `User`, or the caption
     swallows its clicks — the tab strip's `ScrollViewer` deliberately does
     *not*, because empty strip space is where a browser lets you grab the
     window (the cost, stated: the overflow scrollbar past ~8 clusters can't be
     dragged).
   - **On Windows the caption buttons become ours, and that is not optional.**
     Avalonia 12's Win32 backend answers an extended client area with
     `RequestedDrawnDecorations = TitleBar` *and calls `DisableCloseButton` on the
     HWND* — the system's three buttons are switched off and the app is expected
     to draw them. (Pre-12 `PreferSystemChrome` did the opposite; every sample
     online predates this.) Fluent's stock decorations template would supply them,
     but it also paints a full-width title bar panel and the window title over the
     command bar, which puts back both the second bar and the wordmark. Hence
     `CommandBarWindowDecorations` in Theme.axaml: Fluent's own button theme and
     glyphs, no title bar panel, no title text, no underlay. The `PART_CloseButton`
     /`PART_MinimizeButton`/`PART_MaximizeButton` names are load-bearing —
     `WindowDrawnDecorations.AttachCaptionButtons` finds them by name and
     subscribes `Click`, so a rename is a dead button, not a build error. macOS
     asks for no drawn decorations at all and keeps its traffic lights.
   - **The caption strip's width is not discoverable, and its *existence* is not
     constant.** `WindowDecorationMargin` reports the title bar's *height*, so the
     reserve that keeps the palette pill out from under Close is derived from the
     same `CaptionButtonWidth` resource (45) the buttons size themselves from, × 3;
     macOS's traffic lights are a constant (78) and on the *left*. Both in DIPs, so
     they survive DPI changes. But the reserve must be **recomputed, not set once**:
     in full screen there are no buttons to reserve for — Windows because
     `ComputeDecorationParts` strips every drawn part, macOS because its backend
     zeroes `ExtendedMargins` and AppKit hides the traffic lights — and a reserve
     that stayed would be a dead 135px (or 78px) hole in the bar. `WindowDecorationMargin
     .Top > 0` is the signal, correct on both platforms for those two different
     reasons, and `ApplyCaptionReserve` runs off its change notification. On macOS
     this is a state people reach on purpose: the green traffic light *is* the
     full-screen gesture.
   - **`OffScreenMargin` is not optional here.** A maximized window with an
     extended client area hangs a few pixels off every screen edge; unhonored,
     the thing clipped is now the title bar's own contents.
   - **Linux keeps its system decorations.** Extending there hands us the whole
     frame (X11 requests *all four* drawn parts, not just the title bar), and CSD
     that matches GNOME is wrong on KDE and every tiling WM. It is also gated
     behind `X11PlatformOptions.EnableDrawnDecorations`, which Avalonia marks
     experimental "used mostly for testing" — the compiler refuses it without an
     explicit suppression. We ship linux-x64/arm64; ~36px isn't worth any of that.
     `ConfigureWindowChrome` returns early and the Linux window is unchanged.
   - **Nothing here is testable in the screenshot harness**, which is the usual
     safety net: `HeadlessWindowImpl.NeedsManagedDecorations` is `false`, so the
     decorations are never built and every scenario renders the bar with no
     caption strip. Flipping that X11 option on with the platform gate forced open
     is the one way to see the real thing without a Windows box — it renders the
     buttons, their hover states and the reserve correctly, and it is how this was
     verified at all.

13. **The list gets its own search box, and it is not the sidebar's.** The sidebar
   filter narrows *kinds*; nothing narrowed the *objects*, so finding one pod in a
   namespace of two hundred meant scrolling — the job `kubectl get | grep` has always
   done, and the one gesture the list had no answer to. `ClusterTabViewModel.RowFilter`
   drives it, Ctrl/Cmd+F (`Hotkeys.FilterList`) focuses it, Esc clears it and then
   hands focus back to the rows, Enter/↓ moves to the rows. Three things are
   load-bearing:
   - **`Rows` stays the watch's own list; the grid renders `VisibleRows`.** The
     informer applies Added/Modified/Deleted against `Rows` by key, so a row hidden by
     the filter has to stay in it — remove it and the next watch event for that object
     reads as a fresh add. `VisibleRows` is mirrored from `Rows`'s own
     `CollectionChanged`, which is *why* the watch, the fleet merge, `PopulateDemoRows`
     and every screenshot fixture still write to `Rows` and know nothing about a
     filter. Appends and removes are handled incrementally; anything else rebuilds.
     **Pinned by `ClusterTabRowFilterTests`** (`tests/KubeNimbus.App.Tests`), which
     drives the real `Apply`/`ApplyFleet` and asserts on row *identity* as well as on
     what is on screen — the two ways of getting this wrong (filtering in
     `RebuildVisibleRows`, or dropping non-matching rows in the watch-apply path)
     were both written into the code and confirmed to turn the suite red before the
     tests were called done.
   - **It matches what identifies an object** — name, namespace, and cluster in fleet
     mode (`ResourceRowViewModel.Matches`) — and deliberately not the status, which
     would make "Running" match most of a healthy list.
   - **A search that matches nothing is its own state** (`IsFilterEmpty`), separate
     from `IsListEmpty`: "this namespace has no pods" and "no pod here is called that"
     send you looking for opposite problems. It names the query, says how many rows it
     filtered out of, and offers the way back. The filter is cleared when the selected
     kind changes — carrying "nginx" from Pods to ConfigMaps lands on an empty list
     that looks like a broken watch.
14. **A `DataGridCell` needs a gutter on both sides.** Fluent's cell padding is
   left-only, which is invisible while every column is left-aligned and actively
   *misleading* as soon as one isn't. The resource list's Memory column is
   right-aligned, so its "—" placeholder landed hard against Age's "5d" and the pair
   read as `—5d`, i.e. a negative age; a real value did the same (`48 MiB16d`) and the
   CPU number touched the memory sparkline. `Style Selector="DataGridCell"` sets
   `10,0,10,0` in Theme.axaml. The gutter is not free — nine columns × 10px comes out
   of a fixed width, and the first cut pushed Age off the right edge at 1280px — so
   the column `MinWidth`s were re-cut to match (Name 136, Status 140, Ready 56,
   Restarts 78, CPU 98, Memory 106, sparklines 34). Check `cluster-tab-workloads-list`
   at its rendered 1280px, which is narrower than most real windows and is where this
   fails first. A CRD's own printer columns are a *variable* number of columns on that
   same fixed width, and their answer to this is kubectl's own `priority` field rather
   than another re-cut — see "CRD printer columns". The minimums below are the layout a
   list *opens* with; since FEAT-66 they are no longer the last word, because the reader
   can drag any column and the choice is kept per kind — see "The resource grid is the
   reader's to re-cut". Nor is any of them `Width="Auto"` any more, and the measurement
   behind that has its own section: "An Auto DataGrid column ratchets, and only one grid
   can afford it".

15. **The two apps' command bars read the same left to right.** Not identical — the
   flexible middle column carries kubeNimbus's cluster tabs and pgNimbus's centred
   search pill, because tabs are this app's primary navigation and demoting them to a
   second row would put back the chrome rule 12 removed. But everything either side is
   now in the same order and drawn with the same glyphs: `☰` app menu, sidebar toggle,
   then the app's own middle, then search pill, theme, `⚙` preferences, `?`. The help
   button used to sit *before* the theme toggle here and *after* the cog there, which
   is precisely the kind of difference that makes two apps by the same author feel
   unrelated. The `☰` menu is the discoverable home for commands with no other visible
   control — everything in it is also a palette entry, and it is the route for someone
   who does not yet know the palette exists, which on a first run is everyone.
   Its **tail is the same triple in both apps** — Preferences…, Keyboard shortcuts,
   About — and that is not symmetry for its own sake: pgNimbus had About wired
   exclusively to the macOS native app menu, so on Windows and Linux there was no
   way to open it at all. The help *glyph* is shared for the same reason the order
   is (`HelpCircleIconGeometry`, now in `nimbusUi/Theme/Icons.axaml`): pgNimbus drew
   a bare `?` text button beside four `PathIcon`s, which sits on the glyph baseline
   instead of the icons' box and takes the default foreground instead of theirs.
   Every interactive control in the bar still needs
   `chrome:WindowDecorationProperties.ElementRole="User"` (rule 12) — set on the two
   `StackPanel`s here so a control added later inherits it rather than being swallowed
   by the caption.
16b. **A panel you open, use and dismiss is an `OverlayPanel`, not a window.** Shared;
   canonical text is [`DESIGN.md`](shared/nimbusUi/DESIGN.md) rule 13. The cheat sheet
   was already an overlay here and About and Preferences were windows, which is the
   inconsistency that produced the rule: two of the three items at the bottom of the ☰
   menu opened a surface in the shell's own chrome and the third opened one in the OS's.
   `Views/ShortcutsView`, `AboutView` and `PreferencesView` are the bodies;
   `MainWindowViewModel.IsShortcutsOpen` / `IsPreferencesOpen` / `IsAboutOpen` are the
   state, bound two-way and never paired with a closing command (rule 8b again).
   The preferences page **lost something real** in the move and it is worth naming:
   it used to be a non-modal window precisely so you could leave it open while trying a
   setting against a live cluster, and an overlay covers the cluster. Immediate-apply is
   what makes that affordable — the change is already made and persisted when you
   dismiss — but if a setting ever needs watching *while* it is changed, that argument
   comes back and this is the decision to revisit.
   The palette and the cluster switcher are deliberately **not** OverlayPanels: both put
   focus in a search box and drive a selection from the arrow keys, which is a different
   control, not a differently-styled one.
16. **This app has exactly one window, and that is now the rule rather than an
   accident.** It used to have three — the shell plus About and Preferences — and the
   two secondaries needed `ThemedWindowChrome.Attach` to pin `DWMWA_CAPTION_COLOR`,
   because Windows paints a title bar from the *OS's* dark-mode setting: open
   Preferences while the app is in Light and Windows is in Dark and you got a black
   caption above a white page. Rule 16b turned both into overlays, which left that file
   with no callers, so it is gone. pgNimbus still has its copy and genuinely needs it
   (a connection dialog, a crash reporter and two reference windows that cannot be
   overlays), and `DESIGN.md`'s cross-port list already tracks moving the DWM half into
   `nimbusUi` — which is where to get it back from if this app ever grows a second
   window. Adding one *without* it is the bug to remember: the black-caption-over-white
   -page failure is invisible on a machine whose OS theme happens to match the app's.
17. **A mutating action arms a strip; it never fires on the click that started it.**
   Scale, rollout restart and delete all land on one `RowActionViewModel` rendered above
   the resource list, which names the object, holds the replica box when there is one,
   and carries the in-flight / succeeded / refused states in an `infoBar` (rule 11).
   One strip for all three, because the confirm sentence, the busy state, the RBAC 403
   and the success line are the same work three times over otherwise, and three
   near-identical confirms is precisely how they drift apart. Four alternatives were
   considered and rejected, and the reasons are the rule: a **second window** is
   forbidden outright (rule 16); an **OverlayPanel** covers the very list the action is
   about, and rule 16b scopes overlays to shell-level surfaces; an **inspector dock tab**
   spends a third row of chrome inside a ~300px dock (rule 10) on a question with a
   one-word answer; and a **menu item that acts immediately** puts a destructive verb one
   twitch away from Edit YAML. The strip is present only while an action is armed, so it
   costs nothing the rest of the time (rule 1). It is docked *outside* `ContentRows`
   for the same reason the demo banner is — that grid's row indices are load-bearing for
   `ApplyDockState`. And it is a `ContentControl` + inline `DataTemplate`, not a `Border`
   with `DataContext` and `x:DataType` both set on it: `x:DataType` re-roots an element's
   **own** bindings too, so that combination compiles against the wrong type and renders
   *nothing at all*, silently — which is how the first cut of this shipped past the
   compiler and was caught only by looking at the screenshot.
18. **While the app is waiting, it says it is waiting — and it never shows a verdict it
   does not have yet.** This is rule 9 sharpened by a report from a real, distant
   cluster: clicking Pods there rendered the "No pods found" panel for several seconds
   and then filled in with pods. Nothing was slow that had to be fast; what was wrong is
   that the app answered a question it had not finished asking. Four parts, and the first
   is the one that generalizes:
   - **A state that means "I have started" must not be read as "I have finished".** The
     informer writes its `Reset` frame *before* it issues the list request, and
     `ClusterTabViewModel.Apply` used to end the loading state on whatever frame arrived
     first. So Reset cleared the flag, cleared the rows, and `IsListEmpty` went true — an
     empty-namespace verdict, delivered while the request was still in flight. The fix is
     a frame that means what the state needs: `ResourceEventType.Synced`, written after
     the last page of the initial list. Reset now turns loading back *on* (a 410-Gone
     relist is a load too), Synced turns it off, and so does the first row, because the
     list paginates and hiding page one behind a spinner until page four lands is the
     same unresponsiveness facing the other way. `WorkloadLogsTabViewModel` had the
     identical bug in `IsResolvingPods` and got the identical fix.
   - **Every wait ends, including the ones that end badly.** A watch that throws must
     clear the loading state on its way out, or a reported failure renders as a window
     that looks busy for ever. Both `catch` arms in `RestartWatch`/`StartFleetWatch` do.
   - **The waiting state names what is being waited for and shows that something is still
     happening.** "Loading Pods… in payments" over an indeterminate `ProgressBar`, laid
     out in the same shape as the empty state directly below it, so the transition from
     waiting to a verdict changes the words rather than the page. A bare "Loading…" is
     understandable and still says nothing about whether the app is stuck.
   - **This is not testable by screenshot, and that is why it survived.** `Rows`,
     `IsListEmpty` and `IsListLoading` are mutually consistent in every frame a PNG can
     capture; only the *order* of the frames differs between a correct implementation and
     the shipped one, and on a sandbox at localhost that order plays out in a few
     milliseconds. `ClusterTabLoadingStateTests` pins it, and the sandbox-gated
     `WatchPods_emits_synced_after_the_initial_list_and_after_its_objects` pins the frame
     itself against a real API server. **A distant cluster is a different product from a
     local one**, and anything gated on a round trip has to be reasoned about with a
     second of latency in it.

[fluent-basics]: https://learn.microsoft.com/en-us/windows/apps/design/basics/

## Feature deep dives (docs/engineering/)

Each feature's design rules, and the incidents behind them, live in a page of their own under [`docs/engineering/`](docs/engineering/), so a session loads only the ones it touches. **Read the page for any feature you change before changing it**, and keep it current in the same PR — the same discipline as this file.

- [Multi-pod logs (one workload, one stream)](docs/engineering/multi-pod-logs.md) — WorkloadLogsTabViewModel: selector-resolved pods, per-pod tail budget, 50-stream cap, two-stage timestamp merge.
- [Log severity is three classes, not a brush binding](docs/engineering/log-severity-classes.md) — Why severity is style classes and never a Foreground binding (the invisible-plain-line bug, twice).
- [Pod detail's Overview tab (conditions, tolerations, QoS, priority, probes)](docs/engineering/pod-overview-tab.md) — Conditions/tolerations/QoS/probes tab: index 4, condition polarity, API-server probe defaults, signature-guarded rebuild.
- [Requests and limits are text on the Usage tab](docs/engineering/requests-and-limits.md) — Usage tab's declared requests/limits: words not blanks, not gated on metrics.
- [ConfigMaps are shown, Secrets are masked](docs/engineering/configmaps-and-secrets.md) — Env tab: ConfigMap refs resolve on open, Secret refs stay masked behind an eye.
- [The sidebar is 224px and the reader can drag it](docs/engineering/sidebar-width.md) — Absolute sidebar width, GridSplitter bounds, the SidebarWidthChanged write-back.
- [macOS has a real menu bar, and the app is called kubeNimbus](docs/engineering/macos-menu-bar.md) — Application.Name, MacMenu.cs, platform-gated native menu built from CommandCatalog.
- [The theme toggle wrote a string nothing could read](docs/engineering/theme-toggle-string.md) — Stringly-typed settings must write through the same helper that reads them.
- [The cluster switcher and environment colours](docs/engineering/cluster-switcher.md) — Ctrl/Cmd+P switcher (flat list, ranking) and environment colours (biased toward production).
- [CRD printer columns](docs/engineering/crd-printer-columns.md) — additionalPrinterColumns: lazy CRD GET, JSONPath subset, ten fixed XAML slots, Tag-based column identity.
- [The resource grid is the reader's to re-cut](docs/engineering/resource-grid-resize-sort.md) — Column drag + header sort: sorts VisibleRows never Rows, maintained sort, per-kind layout in workspace.json.
- [An Auto DataGrid column ratchets, and only one grid can afford it](docs/engineering/datagrid-auto-columns.md) — Why the resource list has no Width=Auto columns (measured ratchet) and why Helm/Argo keep them.
- [Mutating workload actions (scale, rollout restart, delete)](docs/engineering/workload-actions.md) — Scale / rollout restart / delete: merge patches, scale subresource, capability from discovery.
- [Node operations (detail, cordon / uncordon, drain)](docs/engineering/node-operations.md) — Node detail, cordon/uncordon, drain: allocatable math, eviction plan table, partial-drain lifetime.
- [The exec terminal](docs/engineering/exec-terminal.md) — SvcSystems.UI.Terminal over XTerm.NET: bytes in/out, stateful UTF-8 decoder, keyboard ownership, reverse-video defect.
- [The machine's own terminal ("open a terminal on this cluster")](docs/engineering/machine-terminal.md) — TerminalLauncher: one-key overlay kubeconfig, env-inheritance trap on wt.exe/open, per-platform launch.
- [The apply preview (server-side dry run)](docs/engineering/apply-preview.md) — Server-side dry-run diff, TextDiff/LCS bounds, view modes, strict fieldValidation with pre-1.27 fallback.
- [Metrics (metrics.k8s.io)](docs/engineering/metrics.md) — metrics.k8s.io via discovery, the one polled API, UsageHistory ring and Sparkline.
- [Helm release browsing (read-only)](docs/engineering/helm-releases.md) — Reading Helm 3 release Secrets (base64+gzip) with no Helm binary; synthetic sidebar kind.
- [Argo CD (GitOps in the navigator)](docs/engineering/argo-cd.md) — Argo CD through the Kubernetes API only: sync is a top-level operation patch, sync vs health pills.
- [RBAC access review](docs/engineering/rbac-access-review.md) — SelfSubjectRulesReview, binding provenance, who-can rule scan mirroring API-server matching.
- [Multi-cluster aggregated (fleet) views](docs/engineering/fleet-views.md) — ClusterFleet/AsyncMerge: per-cluster descriptors, cluster-scoped Reset, cluster-qualified keys.
- [The status dot, and where it survives](docs/engineering/status-dot.md) — The health dot survives only beside CRD printer columns; the Helm grid is separate.
- [The meter track was invisible, and the token was the reason](docs/engineering/meter-track.md) — MeterTrackBrush: never reuse a hover token as a chart colour.
- [Sidebar labels come from the server's plural, and now actually do](docs/engineering/sidebar-plural-labels.md) — Sidebar labels re-case the server's plural.

## The demo cluster

`ClusterContext.Demo` is a built-in cluster with no cluster behind it: a dataset that
ships inside the binary, browsable with no kubeconfig, no credentials and no network.
It exists for two audiences at once. **Microsoft Store certification** requires that a
reviewer on a clean Windows machine — no kubeconfig, no Kubernetes anywhere — can see
the app function; before this, they landed on an empty state whose only instruction
was to run a script from a repo they don't have. And **anyone evaluating kubeNimbus**
can now look around before wiring up credentials, which is the one thing a Kubernetes
client cannot otherwise demonstrate. Handing out a real cluster to either group is not
an option (rule #4 forbids the app holding credentials at all), so the sample data
*is* the demo.

Six rules:

1. **A demo tab is an ordinary `ClusterContext` with a sentinel `KubeconfigPath`**
   (`ClusterContext.DemoKubeconfigPath`, `"<demo>"`; `IsDemo` reads it). A sentinel
   rather than a new record field, so `WorkspaceSettings` tab snapshots, the cluster
   switcher's name+path keying and fleet member naming all keep working untouched —
   verified against `RestoreWorkspaceAsync` (which needs one explicit branch, because
   the demo context is not in `AvailableContexts` and so can never match by name+path)
   and `ClusterSwitcherViewModel` (which needed a `Demo` group and a subtitle that
   doesn't print the sentinel as a filename). `ClusterEnvironments.Classify` reads it
   as **Development** — `demo` is a development marker, so it can never come out
   production and put a red band under a screen of invented pods.
2. **There is no `ClusterClient`, and that is the mechanism, not a detail.**
   `ClusterTabViewModel.Client` stays null for a demo tab's whole life, and every
   inspector tab takes `ClusterClient?` and derives `IsDemo` from `client is null`.
   "A demo tab never connects, never watches, never touches the network" is therefore
   something the compiler helps hold: every call site that would have talked to a
   server had to be branched before it would build. `ConnectDemo` fills in for
   discovery/namespaces/the metrics probe, and `RestartWatch`'s `if (Client is not { }
   client)` arm is the list. Do **not** reintroduce an offline `ClusterClient` pointed
   at a dead port to satisfy a constructor — the screenshot harness still has one
   (`FixtureData.CreateOfflineClient`, for scenarios that want the *failed-connection*
   paths), and it is exactly the thing the app must not copy.
3. **One dataset, not two.** `src/KubeNimbus.App/Demo/` owns it — `DemoData` (objects,
   catalog, sidebar, Helm, and the one CRD whose `additionalPrinterColumns` the demo
   list draws — `crds.json` is a real-shaped `CustomResourceDefinition`, read through
   the same `PrinterColumns.Parse` a live cluster's GET goes through), `DemoLogs`
   (canned streams), `DemoUsage` (replayed metric polls) — and `tools/Screenshot/FixtureData.cs` is now a passthrough to it. What a
   screenshot shows and what a user clicking "Explore demo cluster" sees cannot drift
   apart. The JSON is an `EmbeddedResource` with an explicit `LogicalName`
   (`Demo.<file>.json`), same reasoning as `Yaml-Mode.xshd`: the lookup must not depend
   on the assembly being called `kubeNimbus`, and a `Fixtures/` directory next to the
   exe would break the single-file NativeAOT publish. `JsonDocument` only, kept alive
   for the process lifetime (`DynamicResource` wraps `JsonElement`s that die with their
   document).
4. **Everything that can work, works through production code.** Logs go through
   `Enqueue` on a timer, so batching, trimming, filtering, the timestamp toggle and
   every placeholder state are the real ones. Usage goes through `ResourceRowViewModel
   .ApplyUsage` / `PodDetailTabViewModel.ApplyMetrics` with stamped timestamps — which
   is what those optional `at` parameters have always been for. Env resolves
   Secret/ConfigMap refs through the same cache and the same base64 decode, against
   `DemoData.ReadObject` instead of a GET. A kind the dataset has nothing for lands on
   the real "No &lt;kind&gt; found" empty state, which is most of a 100-kind catalog.
   `DemoLogs` deliberately carries **lines with no severity keyword** (nginx access
   logs, JSON, plain prints): every fixture line having one is precisely what hid the
   log pane's invisible-plain-line bug, twice — see "Log severity is three classes,
   not a brush binding".
5. **What cannot work says so, in place.** Exec, port-forward and YAML apply/delete
   need a real API server. Each renders a styled `Border.demoUnavailable` (or, for the
   YAML editor, a `demoBar` above a still-useful read-only editor) naming what it can't
   do and what to do instead, and each disables its commands via `CanExecute` — never a
   spinner that hangs, never a blank pane, never a silent no-op (UI rule 9's last
   clause). The access review is palette-gated on `IsDemo: false` for the same reason:
   its three API-server calls have no honest offline stand-in, and a palette entry that
   matches a search and then refuses to run is worse than no match.
6. **Nobody may mistake it for a real cluster.** The tab reads `Demo cluster`, the
   switcher lists it under its own "Demo (sample data, not a real cluster)" heading,
   and a `Border.demoBar` sits above the content area for the tab's entire life. That
   last one is a deliberate exception to UI rule 1, and the justification is the
   alternative: someone believing a screen full of invented pods is their own workloads.
   A notice that appears once and dismisses does not prevent that.

**Reachability.** "Explore demo cluster" is the most prominent control in the
no-kubeconfig empty state (it is the button a Store reviewer presses), plus a
Ctrl/Cmd+K entry, plus the switcher's own group. Two silent `HasContexts` gates had to
go for that to hold — `SwitcherButton.IsEnabled` and, less visibly,
`MainWindow.OpenSwitcher`'s early return — which between them made the top bar's
cluster button and Ctrl/Cmd+P dead on exactly the machine where the demo cluster is
the only cluster there is. `AddNewTabCommand.CanExecute` is now unconditionally true
for the same reason: the switcher always has at least the demo row in it.

## Settings, and what belongs in which file

There are **two** persisted files and the split is not arbitrary:

- **`settings.json`** (`KubeNimbus.Core/Settings/`, `AppSettings` + `AppSettingsStore`)
  is *preferences* — what you chose once and expect to still be true next launch:
  theme, hotkey scheme, advanced view, sidebar visibility and expanded sections,
  picked kubeconfig paths, log scrollback, metrics poll interval, delete confirmation,
  apply preview.
- **`workspace.json`** (`KubeNimbus.App/WorkspaceStore.cs`) is *session* — what the
  window looked like: open tabs, pinned and recent contexts, environment overrides.

Each tab snapshot also carries the **kind and namespace** it was showing, and the
workspace the index of the tab in front, so a restart lands where you left off instead
of on Pods in all namespaces on every tab. With nothing saved, a tab opens on the
kubeconfig context's own `namespace` (what kubectl would use), and the first launch
opens the chain's `current-context` rather than whichever context the merge listed
first. `ClusterTabViewModel.ApplyInitialView` is the one place that decides, and
`ClusterTabInitialViewTests` pins it — including that a saved namespace missing from a
namespace list that *was* read is not opened (it was deleted), while one missing because
listing was refused by RBAC is added and selected.

The test: deleting the workspace should lose your tabs and nothing else; deleting the
settings should reset your preferences and not close your clusters. Theme,
`IsAdvancedView` and `KubeconfigPaths` used to be in the workspace, on the wrong side
of that line; `App.MigrateWorkspacePreferences` moves them once, guarded on
`settings.json` not existing yet, so nobody's existing choice is lost — "the update
ate my settings" is the specific bug that guard exists to prevent.

Five rules:

1. **Every setter goes through `App.Update(s => s with { … })`**, a read-modify-write.
   Never a cached snapshot: the preferences window, the palette and an inline toggle
   can all be live at once, and writing back a snapshot taken before another one
   changed something silently reverts it.
2. **The store validates; the UI is not trusted to.** `AppSettings.Normalized()` is
   applied on both read and write, because the file is plain JSON in a user-writable
   directory: a hand-edited `MetricsPollSeconds: 0` would spin a timer as fast as the
   dispatcher allows and hammer the API server. Clamping (rather than rejecting the
   file) keeps every other setting in it.
3. **A setting nothing reads is worse than no setting.** Each one is wired to the code
   that used to hardcode it — `PodDetailTabViewModel._maxLogLines` (was a const 4000),
   both `MetricsPollInterval`s (was 15s), `RequestDeleteAsync` (the confirm step),
   `SidebarSectionViewModel`'s initial expansion. Where the read happens is a decision
   each time: the delete confirm re-reads at the moment the button is pressed (someone
   who turns it back on after a near-miss expects the *next* delete to ask), while the
   log cap is read per tab (re-trimming a live buffer would discard lines someone was
   reading).
4. **Nothing here may become a credential** (rule 4). `KubeconfigPaths` is the closest
   it comes and is paths only, re-resolved through the chain at connect time. The
   preferences page says so in the panel, which is where someone would worry about it.
5. **`AppSettingsStore.DirectoryOverride`** exists for the screenshot harness, same as
   `WorkspaceStore.DirectoryOverride` and for a stronger reason: the preferences a
   scenario touches are exactly the ones the developer running it has chosen for
   themselves.

The page itself (`PreferencesWindow` + `PreferencesViewModel`) is deliberately the same
shape as pgNimbus's — section header, one card per setting, label and explanation left,
control right, **immediate apply and no OK/Cancel** — because someone who uses both
should not learn it twice. Settings the shell already owns (`IsAdvancedView`,
`IsSidebarVisible`) are *proxied* through `MainWindowViewModel`, never duplicated, so
the page and the command bar's own toggles cannot disagree while both are on screen.

## Workload detail and namespace navigation

Double-click opens Deployments, StatefulSets and DaemonSets in
`WorkloadDetailTabViewModel`. The pane shows replica counts, controller progress,
conditions, events and a live pod list. The pod watch uses the workload selector,
including match expressions. Closing the pane cancels its requests and watch.
The workload status follows its list row; Refresh also reads the object directly.

Double-click, Enter and L open a selected pod. S opens its shell.
E opens the workload YAML. The Actions menu offers scale and rollout restart
through the existing confirmation strip. Each action retains the original row,
descriptor and cluster, even after the main list changes. Owner navigation uses
the same detail routing. Events use the object UID when available.

The namespace picker filters on input and commits only on Enter or a row click.
Ctrl/Cmd+Shift+N opens it and focuses its search field. Escape closes it.
Five recent namespaces appear first after All namespaces. `workspace.json`
persists them per kubeconfig path and context. Deleted namespaces stay out of
its results. The existing palette entries still work.

## The command catalog (shortcuts, palette, cheat sheet, docs)

`KubeNimbus.Core/Commands/` is the single source for every command and documented
gesture: `CommandCatalog` holds the descriptors, `Chord`/`CommandKey`/`ChordModifiers`
express a key combination without naming a platform, and `ShortcutDocs` renders the
whole thing as `docs/keyboard-shortcuts.md`. The App layer projects it —
`CommandBindings` turns descriptors into Avalonia gestures and resolves ids to
view-model commands, `ShortcutsViewModel` builds the F1 sheet, `CommandTip` builds
tooltips. It replaced a hand-written `Hotkeys.CheatSheet` array plus gestures typed
into four places.

Seven things worth keeping:

1. **Core stays UI-free** (rule 1), so `CommandKey` is a local enum rather than
   Avalonia's `Key` and `CommandBindings.ToKey` owns the one mapping. That is also why
   this cannot live in `nimbusUi` despite being app-neutral: the shared library
   references Avalonia, and Core may not.
2. **The gestures are properties, not `static readonly` fields.** The Ctrl/Cmd scheme
   is a user preference now, and a `KeyGesture` captured at type-initialization
   outlives the setting that produced it. `Hotkeys.cs` used to hold exactly such
   fields; the window rebuilds its bindings from `Hotkeys.Changed`, and
   `BuildKeyBindings` **clears** first — adding to the existing set would leave Ctrl+K
   working after someone chose Cmd, which reads as the preference doing nothing.
   The clearing rebuild itself is `CommandBindings.RebuildWindowBindings`, out of the
   window so it can be tested (a `MainWindow` needs a running Application); the four
   surfaces that have to follow the scheme are pinned by `HotkeySchemeTests`
   (`tests/KubeNimbus.App.Tests`) and were driven against the running app — see the
   VER-3 pass in Current status for what that showed and what it still cannot cover.
3. **The palette is a *partial* projection, deliberately.** Most of this app's rows are
   conditional — logs/exec/port-forward only while a pod row is selected, the fleet
   toggle only with more than one cluster connected — so they stay closures over the
   selected tab in `BuildPaletteItems`, because a palette entry that matches a search
   and then refuses to run is worse than no match. What they take from the catalog is
   title, icon and shortcut text. `CommandBindings`' startup check is therefore over
   `WindowBinding` only, which is narrower than pgNimbus's and says so in place.
4. **An action with no gesture is `PaletteOnly`, not `PaletteAndSheet`.** F1 is a
   *keyboard* reference: a row reading "Edit YAML — —" tells the reader nothing and
   pushes the rows that do carry a key further down. `CommandCatalogTests` pins this —
   every cheat-sheet row must have a chord or a gesture note.
5. **`ChordModifiers.Control` is literal Ctrl on every platform**, and the exec pane is
   why: `^C` and `^D` are terminal control characters, Control on macOS too, and Cmd+C
   there is Copy. A test asserts both render as "Ctrl" even under the Cmd scheme. The
   pane's Copy/Paste pair is `Control | Shift` for the far side of the same argument —
   the terminal owns plain Ctrl+C, so the clipboard has to move up a modifier, exactly
   as it does in every terminal emulator.
6. **The list has single-letter row keys, k9s's own.** L logs (a pod's, or every pod a
   workload owns), P previous logs, S shell on a pod / scale on anything with a `scale`
   subresource, F port-forward, E edit YAML, R rollout restart, Delete, and `/` to search.
   They are `CommandScope.List` rows in the catalog, matched by `ClusterTabView
   .OnGridKeyDown` through `CommandBindings.Matches`, and each resolves to the *same*
   command the context menu and the palette run — so a key can never do something the
   menu could not, and the mutating ones arm the confirm strip (UI rule 17) rather than
   acting. Bare letters are safe only because the grid is read-only and owns them;
   never make one a window binding, where it would fire while typing into a text box.
   The menu's `InputGesture` captions are display-only and have to be kept in step by
   hand. They exist because every action here used to be right-click, read the menu,
   click — three motions for the thing the app is opened to do.
7. **The docs page is a golden file.** `ShortcutDocsTests` fails on any drift;
   `KUBENIMBUS_UPDATE_DOCS=1` regenerates it. A shortcut reference that can silently
   fall behind the app is worse than none.

## The Advanced view

One global persisted boolean, default **on**, mirrored onto every cluster tab. It
governs exactly one thing: whether the sidebar shows the two sections most sessions
never open — **Cluster** (the API machinery: APIServices, CSRs, ClusterRoles and the
whole of flowcontrol, admissionregistration, apiregistration and coordination, 33 kinds
on a bare k3s) and **CRDs** (the catalog's long tail, comfortably the longest section
on any cluster running cert-manager, Argo or Istio). `SidebarGrouping.IsAdvancedSection`
is the whole classification. It keeps its place — an icon-only `ToggleButton
Classes="chip"` docked right of the sidebar's filter box, the same spot as pgNimbus's
`ShowAdvancedObjects` — because people who use both should find it where they left it,
and because that is now literally the panel it acts on.

**It used to hide a great deal more, and removing that is the point of the current
shape.** Off took the CPU/Memory columns and their sparklines, pod detail's Usage tab,
the fleet toggle, both log toolbars' Wrap/Copy/Download, YAML force-apply, the Helm and
RBAC palette entries, and a CRD's own `priority: 1` printer columns (the switch acting
as kubectl's `-o wide`). So a complaint about a crowded *sidebar* was answered by hiding
controls all over the *content area*, where nothing was crowded — and what it hid there
was mostly what somebody had deliberately gone looking for: the cluster's own usage
numbers, the one gesture that gets a log into a bug report, the only in-app resolution
to an apply conflict, and columns a CRD's author had declared. All of those are
unconditional now.

Five things are load-bearing:

- **Nothing outside the sidebar may be gated on it again.** That is the rule the rework
  exists to establish, and `SidebarAdvancedSectionTests` pins the negative half of it
  (the usage columns and the fleet toggle survive the switch going off) precisely so a
  re-gating shows up as a red test rather than as a control someone cannot find.
- **It is a display switch and nothing else.** Flipping it must never restart a watch,
  refetch anything, or lose list/inspector state.
- **Nothing it hides becomes unreachable, and that is what makes hiding safe.** The
  sidebar's own filter reaches into a hidden section — a query is a deliberate search
  for one thing, and a match that then renders nothing is the "worse than no match"
  failure the palette's rules already name — and every kind keeps its Ctrl/Cmd+K entry
  whatever the switch says. `ApplySidebarChrome` and `ApplySidebarFilter` both derive
  the gate, because the filter is one of its inputs.
- **The shell owns it; tabs carry a mirror.** `MainWindowViewModel` persists it
  (`AppSettings.IsAdvancedView`) and broadcasts; `ClusterTabViewModel` holds the copy the
  sidebar binds. `InspectorTabViewModelBase` no longer carries one at all — with the
  content area ungated, nothing read it, and a mirror nothing reads is the "setting
  nothing reads" this file forbids.
- **The kind-count badge stays on the switch.** "How much is hiding in here?" is a
  question about the catalog, so it belongs to the control that governs the catalog.
  It is pushed per section from `ApplySidebarChrome`, which is why the screenshot
  harness has to call that after building its sections by hand — a fixture that skips
  it renders the sidebar as though the switch were off however it is set.

`cluster-tab-workloads-list` and `cluster-tab-basic-sidebar` are the same fixture tab
rendered on and off, and what the pair has to show is as much the *sameness* of the
content area as the difference in the sidebar. `cluster-tab-crd-printer-columns-wide`
is gone: with every declared column always drawn it rendered identically to
`cluster-tab-crd-printer-columns`.

**The old value is deliberately not migrated.** `App.MigrateWorkspacePreferences` used
to carry `WorkspaceSettings.IsAdvancedView` across; it no longer reads it, because that
flag answered a question that no longer exists and most people never touched it — so
migrating would have opted nearly everyone into the shorter sidebar on the strength of
a default they never chose. The workspace property is kept unread so a downgrade still
finds it.

## Repository layout

```
src/KubeNimbus.Core        Engine: kubeconfig, ClusterClient (watch/logs). No UI.
src/KubeNimbus.App         Avalonia 12 desktop shell.
tests/KubeNimbus.Core.Tests  TUnit unit + integration tests; the latter skip with no cluster.
tests/KubeNimbus.App.Tests   TUnit view-model tests. No Avalonia app, no cluster, no display.
tools/Screenshot           Headless visual-verification harness. Dev-only.
design/                    Logo masters (.af) + generated SVG/masters/store/screenshots.
installer/                 Packaging inputs: WiX MSI, macOS Info.plist, .desktop, MSIX manifest.
scripts/                   Sandbox bootstrap, the icon/logo pipeline, and the installer builds.
```

Public-facing docs, each with one job — don't duplicate content between them:

| File | Audience |
|---|---|
| `README.md` | Someone deciding whether to download it. Screenshots, download/install, what it does, limitations. |
| `CONTRIBUTING.md` | Someone opening a PR. Setup, verification, PR expectations, the release procedure. |
| `SECURITY.md` | Reporting a vulnerability, plus the **security model** the app claims to hold (no persisted credentials, no telemetry, exec plugins run external programs). |
| `CHANGELOG.md` | Release history — and machine-read: the release workflow lifts the section matching a tag out of it verbatim. |
| `CODE_OF_CONDUCT.md` | Contributor Covenant 2.1, unmodified apart from the contact address. |
| `CLAUDE.md` (this file) | Whoever is changing the code. The engineering contract and the *why* behind every rule. |
| `docs/product-loop/` | The release train's live state (`TRAIN.md`), product assessment, competitor matrix and per-release history. |
| `docs/BACKLOG.md` | The long-lived evidence pool: owner-pinned Ready rows and the Inbox the release train mines — see below. |
| `docs/PRE-LAUNCH-CHECKLIST.md` | One-time: making the repo public, cutting the first release, and the Microsoft Store submission. Delete it once the launch is behind us. |

## The release train

Work is shipped as **trains**: one train is one release carrying 5–10 deliverables,
every few days. `/release-train` ([`.claude/skills/release-train/SKILL.md`](.claude/skills/release-train/SKILL.md))
runs it one step per invocation — SURVEY (repo scan + a background competitor
delta) → SELECT (fresh scored candidates, 5–10 picked, a spec each) → BUILD (one
item per step) → HARDEN (regression sweep, performance gate, polish pass, product
review) → RELEASE → RECORD — and is driven by `/loop /release-train`, normally in a
Claude Code cloud session. Three agents do the heavy lifting: `kn-implementer`
(Opus, builds one item), `kn-verifier` (Sonnet, re-runs the checks and reviews
against the rules above, with no Edit tool so it cannot quietly fix what it should
be reporting), and `kn-researcher` (the competitor delta and matrix). Its files live
in [`docs/product-loop/`](docs/product-loop/): `TRAIN.md` (the live state),
`CURRENT_STATE.md`, `COMPETITOR_MATRIX.md`, and `history/<date>-v<version>/` for
every shipped train.

It replaced `/backlog-cycle`, which shipped one owner-approved item per cycle and
never released. Five things about the train are load-bearing:

1. **The train selects its own work; the owner steers rather than gates.** The old
   loop could only take items a human had put in Ready, which kept a person in the
   loop and also meant the queue ran dry whenever that person was busy. Now the
   Ready table is a set of *forced candidates* (P0/P1 rows enter the plan unless they
   are infeasible where the train runs), the Inbox is an evidence pool, and the
   owner's levers are `TRAIN.md`'s Config and Owner notes, applied at the start of
   every step: pin, veto, pause, or change the release mode.
2. **State lives in `TRAIN.md` on the train branch, and every step pushes it.** A
   cloud container is discarded with its session, so a step that only committed
   locally did not happen, and a fresh session finds the live train by its
   `train/*` branch.
3. **Verification debt is still an item, not a footnote.** Whatever the verifier
   reports as unverifiable in its environment — no live cluster, no Windows or macOS
   box, no display — becomes its own Inbox row in the same step. This repo has
   repeatedly lost track of exactly that, and the cost is on record: three of four release
   RIDs shipped a binary that could not start, because `ci.yml` published the AOT
   output and never launched it.
4. **`MAX_FIX_ROUNDS` ends in a revert, not a stall.** An item still failing
   verification is reverted off the train branch and marked `blocked` with the
   precise finding; the train moves on without it.
5. **The next train starts from a new survey.** Items 11–20 of the last ranking are
   not a queue — the repository and the market have both moved since they were
   scored.
## App icon / logo assets

Moved to [`design/CLAUDE.md`](design/CLAUDE.md), which loads when working under `design/`. The short version: nothing in `design/*.svg` is hand-edited (the `.af` files are the art), and the base and broom are shared byte-for-byte with pgNimbus, so a change to either is a pair of PRs.

## The AOT watch/log implementation (important, non-obvious)

`KubernetesClient.Aot` (unlike the reflection client) ships **no `WatchAsync`
helper and no `WatchEventType` enum**. So `ClusterClient` issues watch and
log-follow requests directly against the client's own `Kubernetes.HttpClient`
with `HttpCompletionOption.ResponseHeadersRead`:

- Auth is reused from the client — client-cert/TLS live on the handler chain;
  bearer/exec tokens are applied by calling `Kubernetes.Credentials
  .ProcessHttpRequestAsync` on our manual request. This is what makes exec-plugin
  auth work for watches.
- Watch frames are line-delimited JSON, parsed with `System.Text.Json.JsonDocument`
  (AOT-safe) and materialized with source-generated `KubernetesJson.Deserialize`.
- The informer loop lives in `ClusterClient.PumpAsync`/`StreamWatchAsync`:
  paginated initial list (Reset + Added per item) → resumable watch →
  relist on `ERROR` frame / 410 Gone → exponential backoff with
  `connectionLost` callback on transient failures.

If you add a new **typed** watched resource, reuse the generic `WatchAsync<T>`
core; only supply the list path, a paged lister, and a
`KubernetesJson.Deserialize<T>` delegate. For **any resource kind discovered at
runtime** (CRDs included — there's no compile-time type for those), use
`ClusterClient.WatchResourceAsync(ResourceDescriptor, ...)` instead: it runs
the same engine with `DynamicResource` (a JsonElement-backed wrapper, see
`DynamicResource.cs`) as `T`. The sidebar/list view always goes through this
generic path — pods included — so there's exactly one live-list code path in
the App layer.

## Discovery, server-side apply, events, exec, port-forward

- **Discovery** (`ClusterClient.Discovery.cs`) negotiates aggregated discovery at
  `/api` and `/apis`, preferring `apidiscovery.k8s.io/v2`, then `v2beta1`.
  Current aggregated responses supply the catalog in two requests. Legacy or stale
  responses use the bounded per-group fallback (16 requests at once).
  Descriptors preserve verbs, subresources, short names and namespace scope.
  The first advertised version is the preferred version. Raw `JsonDocument`
  parsing keeps this path compatible with NativeAOT.
  `DiscoveryCache` stores only descriptors in the local app-data directory.
  Its key hashes the server URL, context, user name and kubeconfig path.
  Entries expire after six hours or a server-version change. Corrupt files are
  cache misses. Partial discovery results never replace the disk cache.
  The sidebar context menu offers **Refresh resource catalog**, which bypasses
  the cache. `DiscoveryHttpTests` covers negotiation, fallback concurrency,
  warm connections and version invalidation.
  `ConnectAsync` first reads the version to resolve exec credentials once.
  It then starts discovery, namespaces and the metrics probe together.
  After namespace resolution, the initial Pods watch starts with the known
  core/v1 descriptor. Discovery replaces the temporary sidebar entry without
  restarting the watch or clearing rows. Saved non-Pod kinds wait for discovery.
  Discovery errors leave an early Pods watch connected and show a warning.
  Restored cluster tabs also connect in parallel.
  Discovery says nothing about how a kind should be *printed*, which is why a CRD's
  own columns come from a separate GET of the CustomResourceDefinition — see "CRD
  printer columns" below.
- **Server-side apply** (`ClusterClient.Dynamic.cs`) PATCHes with
  `Content-Type: application/apply-patch+yaml`; the body is JSON (valid JSON
  is valid YAML, so the API server's apply decoder accepts it) produced by
  `YamlJson.cs`. That file uses YamlDotNet's **structural** `RepresentationModel`
  (`YamlNode`/`YamlStream`) to convert YAML ⇄ JSON — never YamlDotNet's
  attribute/reflection-based (de)serializer, which is not AOT/trim-safe and
  can't handle arbitrary CRD shapes anyway. A 409 conflict raises
  `ServerSideApplyConflictException` for the UI to offer a force-apply retry.
- **Exec** (`ClusterClient.Exec.cs`) uses `Kubernetes.MuxedStreamNamespacedPodExecAsync`
  — the one exec helper `KubernetesClient.Aot` *does* ship, because it's
  WebSocket-based rather than SPDY and needed no reflection-based transport. What the
  App layer does with those bytes is a VT emulator now; see "The exec terminal".
- **Port-forward** (`ClusterClient.PortForward.cs`) has no equivalent helper,
  so it opens a raw `WebSocketNamespacedPodPortForwardAsync` websocket per
  accepted local TCP connection (matching kubectl's own approach — the k8s
  websocket port-forward channel framing doesn't support multiplexing several
  local clients over one upstream connection) and pumps bytes with the
  channel-byte-prefix framing by hand.

## Sandbox cluster bootstrap (how tests get a real cluster)

Integration tests run against a **real local Kubernetes cluster**, not mocks.
The suite auto-discovers `./.sandbox/kubeconfig.yaml` (git-ignored — it holds
cluster CA + client certs) or `$KUBENIMBUS_TEST_KUBECONFIG`. Tests **skip
cleanly** when no cluster is reachable, so CI without one stays green.

Two things about that gate, both learned the hard way (`SandboxCluster.cs`):

- **A kubeconfig on disk is not evidence of a cluster.** `.sandbox/kubeconfig.yaml`
  outlives the container it was written for, so on a machine where Docker is simply
  not running the file still parses and still names a server nothing is listening on.
  Gating on the file's existence therefore turned "no sandbox today" into 14 failures
  and two 30-second timeouts. The gate is a **reachability probe** — one
  `GetServerVersionAsync` with a 5s budget, run once per suite run and shared by every
  test — and it catches only the ways a cluster can be *unusable from here* (nothing
  listening, TLS that cannot be established, no answer in time, a kubeconfig with no
  credentials). An authorization failure or a malformed response means a server did
  answer, so it propagates into the test that provoked it rather than becoming a
  silent skip.
- **A skipped test must be reported as skipped, not as a pass.** The gated tests used
  to `return` early, which the runner counts as success — so a run that talked to no
  cluster at all was indistinguishable from one that exercised a real API server, and
  this file's own status notes have twice recorded the first as if it were the second.
  `SandboxCluster.TryGetContextAsync` calls TUnit's `Skip.Test(reason)` instead, so the
  summary reads `succeeded: 370, skipped: 16` and names why.

**Use the script** (`scripts/sandbox-up.ps1`, or `scripts/sandbox-up.sh` on
Linux/macOS — Docker required). It starts single-node k3s in Docker, writes
`.sandbox/kubeconfig.yaml` pointed at the published host port with the context
renamed from k3s's `default`, and applies the demo workloads:

```powershell
./scripts/sandbox-up.ps1            # add -Recreate to start from scratch
./scripts/sandbox-down.ps1
```

Re-running reuses a live container and re-applies the manifests. `-Name`/`-Port`/
`-Kubeconfig` bring up a **second** cluster, which is the only way to exercise
the fleet views for real. See [`scripts/README.md`](scripts/README.md) for the
full flag table.

**Docker Desktop is not required.** `-Wsl` on both `.ps1` scripts routes every
docker call through `wsl.exe docker ...` instead — for Docker Engine installed
directly inside a WSL2 distro
([tutorial](https://learn.microsoft.com/windows/wsl/tutorials/wsl-containers)),
with no Windows Docker Desktop at all. The one thing that needed care: `docker
cp` takes a Windows host path (the manifests dir) that a WSL-side docker client
can't resolve, so `-Wsl` translates it through `wsl wslpath -u` first — every
other call only ever passes container names and in-container paths, which need
no translation. `dotnet run`/`$env:KUBECONFIG` stay exactly as below; WSL2
forwards `localhost:<port>` to Windows automatically. See
[`scripts/README.md`](scripts/README.md#docker-without-docker-desktop-wsl2).

`-InstallKubeconfig` additionally copies it to `~/.kube/config`. That matters
because `$KUBECONFIG` only reaches processes started from a shell that has it
set — an app launched from Explorer, a shortcut or Visual Studio sees nothing
and lands on the empty-state screen. The copy goes stale on `-Recreate` (new CA
and client certs), so it refuses to overwrite an existing config without
`-Force` and keeps a timestamped backup; `$KUBECONFIG` remains the
non-staling option for terminal launches.

The manifests in `scripts/manifests/` are not a demo for its own sake — each one
exists to make some app surface non-empty, and that is the bar for adding to
them: multi-container pods that log continuously (log follow, severity coloring,
container picker), env vars of every ref kind (Environment tab + Reveal), a
StatefulSet with PVCs (Storage), a CronJob firing every minute (a visibly live
watch), a whole `demo-broken` namespace of CrashLoopBackOff/ImagePullBackOff/
unschedulable/never-Ready pods (the status pills and empty/error states of UI
rule 9), three CRDs **two of which share the Kind `Widget` in different API
groups** (the sidebar's group-aware filter) whose `additionalPrinterColumns` between
them produce every column state the list can render — mixed scalar types, a
`priority: 1` column, a condition filter, a `type: date` that is not the creation
timestamp, a declared `Age`, a path that resolves to nothing, and one CRD declaring
**no** columns at all (the degradation path), RBAC subjects including a dangling
binding, a `resourceNames`-narrowed rule and a ClusterRole bound by a *RoleBinding*
(the access review, both directions), and a synthetic three-revision Helm release
(history paging — k3s's own traefik releases are real but sit at revision 1).
Metrics need nothing: k3s ships metrics-server, so `metrics.k8s.io` is live;
delete that Deployment to test the *absent*-metrics degradation path.

If a feature grows a state that nothing in the sandbox produces, add a workload
for it here rather than relying on the screenshot fixtures alone.

`kind` works equally well if you prefer it (`kind create cluster`, then
`kind get kubeconfig > .sandbox/kubeconfig.yaml`); the demo manifests apply to
any cluster with `kubectl apply -f scripts/manifests/`, minus the Helm release
seeding, which is inline in the scripts.

## Verification workflow

```powershell
# Build everything.
dotnet build KubeNimbus.slnx

# Run Core tests against the sandbox cluster (skips if none).
# `--project` is MANDATORY: under the .NET 10 Microsoft.Testing.Platform runner
# (pinned in global.json) a positional csproj prints "Specifying a project for
# 'dotnet test' should be via '--project'" and exits 0 having run NOTHING. That
# silently passed for a while in CI — if a change to the suite looks suspiciously
# green, check the invocation first.
dotnet test --project tests/KubeNimbus.Core.Tests/KubeNimbus.Core.Tests.csproj

# Run the App layer's view-model tests. Same runner, same --project rule; these
# need no cluster, no display and no Avalonia app instance. Two invocations
# rather than one over the solution so a red run names which half broke.
dotnet test --project tests/KubeNimbus.App.Tests/KubeNimbus.App.Tests.csproj

# Run the app against the sandbox during development.
$env:KUBECONFIG = ".sandbox/kubeconfig.yaml"
dotnet run --project src/KubeNimbus.App

# Headless visual check (no display, e.g. Claude Code Cloud) — see below.
dotnet run --project tools/Screenshot -- /tmp/kubenimbus-screenshots

# NativeAOT publish — THE shipping build. Verify it end-to-end on every change
# that could affect trimming/AOT (new package, new reflection, new binding).
dotnet publish src/KubeNimbus.App -c Release -r win-x64 -p:PublishAot=true -o publish/app

# And then LAUNCH what you just published. A clean publish is not a working
# binary — see "The launch check" below.
publish/app/kubeNimbus --smoke-test        # Linux: wrap in xvfb-run -a
```

On a machine without the Windows/MSVC toolchain (e.g. this repo's Linux dev
containers, Claude Code Cloud), `dotnet publish src/KubeNimbus.App -c Release
-r linux-x64 -p:PublishAot=true -o publish/app` exercises the same
IL-trimming/AOT analysis and catches the same class of problems (new
reflection, a non-trim-safe binding) even though it isn't the shipping
binary — run it after any change that could plausibly affect trimming, and
call out in the PR that the authoritative win-x64 publish still needs a
local Windows pass.

### View-model tests (`tests/KubeNimbus.App.Tests`)

The App layer had no test project until VER-5, and the gap was not an oversight so
much as an unanswered question: the code worth pinning is in `KubeNimbus.App`, and
hard rule 1 forbids moving it to Core to reach `KubeNimbus.Core.Tests`. So there is
now a second TUnit project — same runner, same `--project` rule, same "never add
`Microsoft.NET.Test.Sdk`" — referencing `KubeNimbus.App` directly.

Four things about it:

- **It starts no Avalonia application.** `ClusterTabViewModel`'s constructor, the
  watch-apply path and the `Rows`→`VisibleRows` mirror are plain
  CommunityToolkit MVVM over `ObservableCollection`; `Dispatcher.UIThread` only
  appears on the far side of a live watch, which these tests never start (`Client`
  stays null, so `RestartWatch` returns before it can). If a future test does need
  a rendered control, that is `Avalonia.Headless` and the screenshot harness's
  pattern — not a headless app instance bolted onto this one by default.
- **It drives the real methods, not a copy.** `ClusterTabViewModel.Apply` and
  `ApplyFleet` are `internal` (with `InternalsVisibleTo` in the App csproj) purely
  so the tests can post watch events the way the watch pump does. A test over a
  stand-in reproduction of the mirroring logic would pin nothing: the bug it guards
  against is one a second implementation, written from the rule, would not have.
- **It redirects both stores.** `AppSettingsStore.DirectoryOverride` and
  `WorkspaceStore.DirectoryOverride` are set to a temp directory in
  `TestObjects.RedirectStores`, same reason the screenshot harness sets them —
  a test run must not read, still less write, the files of whoever is running it.
- **The screenshot harness cannot replace it, and that is the whole argument.**
  `Rows` and `VisibleRows` agree with each other in every state a PNG can capture;
  the difference between a correct mirror and one that filters `Rows` in place only
  shows on the *next* watch event. That is not a rendering property, so it needed a
  different kind of check.

### `dotnet test --project` is broken on this machine (SDK 10.0.400-preview)

`dotnet test --project tests/KubeNimbus.Core.Tests/KubeNimbus.Core.Tests.csproj`
reports **"Zero tests ran", exit code 5**, and it does so on a clean checkout of
the checkpoint commit too — this is the local SDK
(`10.0.400-preview.0.26322.102`), not a regression in the suite. Running the
test executable directly works and is what these 137 results come from:

```powershell
tests/KubeNimbus.Core.Tests/bin/Debug/net10.0/KubeNimbus.Core.Tests.exe
```

CI pins `10.0.100` via `global.json` and still uses `--project`, so it is
unaffected — but if a local run ever looks suspiciously green *or* suspiciously
empty, check the invocation before the code. This is the second distinct way
`dotnet test` has silently run nothing in this repo; the first (a positional
csproj, exit 0) is documented under Verification workflow.

### The launch check (`--smoke-test`)

**A publish that emits no warnings is not a binary that starts, and this repo has
the receipts.** `Icon="/Assets/app.ico"` published perfectly cleanly on every RID —
same two DataGrid warnings, exit 0 — and then died before the first frame with
`FileNotFoundException: The resource /Assets/app.ico could not be found` out of
`IconTypeConverter.CreateIconFromPath` (see `WindowIcons`). Because `ci.yml`
published the AOT output and never ran it, and `release.yml` published four RIDs and
never ran any of them, **v0.1.0 shipped three release binaries that could not
launch**, and nobody found out from CI. That is what this check exists to stop, and
it is the reason "publishes cleanly" is never again allowed to stand in for "works".

`kubeNimbus --smoke-test` (`src/KubeNimbus.App/SmokeTest.cs`) starts the app the
ordinary way and exits **0 only after the main window has opened and composited a
frame**. Anything else is a distinct non-zero code: 64 no MainWindow, 65 a frame
rendered but the window is hidden or 0×0, 66 startup threw, 67 the watchdog expired.
Five things about it are deliberate:

- **It lives in the app, not beside it.** A GUI process never exits on its own, so an
  external checker needs both a way to end it and a way to see a window — and that is
  a different tool per platform (`xdotool` on X11, `MainWindowHandle` polling on
  Windows, scripted Accessibility on macOS, which a runner will not grant). One flag
  is uniform across all four shipped RIDs and adds no packages.
- **It observes the window the app already built**, from `App
  .OnFrameworkInitializationCompleted`; it never constructs one of its own. A check
  with its own startup path is a check that can pass while the real path is broken.
- **The verdict is the exit code**, not the log line. `kubeNimbus` is `WinExe`
  (GUI subsystem), so on Windows stdout only exists if the parent supplied a handle —
  the `SMOKE-OK`/`SMOKE-FAIL` lines are for reading a red job, not for deciding it.
- **The assertion happens inside `RequestAnimationFrame`, not in `Opened`.** `Opened`
  fires before layout and render, so a size check there reads a window that is
  legitimately still 0×0. Requesting an animation frame schedules a compositor tick
  and calls back after it, which is what makes "a window appeared" an actual claim.
- **The watchdog is armed in `Run`, before `StartWithClassicDesktopLifetime`** — not
  in `Attach`, which is the obvious place and is wrong. `Attach` runs inside framework
  initialization, so a hang in platform detect, `App.Initialize`'s XAML load or a
  static constructor would never arm it and would sit on the runner until the job
  timeout. It is a pool-thread `Timer` calling `Environment.Exit`, because the failure
  it has to survive is a wedged UI thread and a `DispatcherTimer` would be wedged
  with it. Verified by running with `KUBENIMBUS_SMOKE_TIMEOUT_SECONDS=1` against a
  ~1.4 s Debug start: `SMOKE-FAIL (67) no window after 1s (last stage: process
  started)`.

**Where it runs.** `ci.yml`'s `aot` job runs it on the linux-x64 output under Xvfb —
Xvfb rather than headless, so the backend under test is the X11 one a user gets.
`release.yml` runs it on **every** RID, on that RID's own runner (NativeAOT cannot
cross-compile, which is why the matrix already has one runner per RID), **before**
staging: a binary that cannot start must fail its leg rather than be archived,
checksummed and attached to a public release. The Windows leg uses `Start-Process
-Wait -PassThru` and not `&` — PowerShell does not wait for a GUI-subsystem child
invoked with the call operator, so `$LASTEXITCODE` would be meaningless and the step
would pass unconditionally. Avalonia's X11 backend dlopens exactly seven native
libraries (`libX11`, `libXext`, `libXrandr`, `libXi`, `libXcursor`, `libICE`,
`libSM`); both Linux workflows install them alongside `xvfb`.

**The check is only worth having if a broken binary fails it, so prove that, don't
assume it.** Restore `Icon="/Assets/app.ico"` on `MainWindow`, publish, and run the
check: the publish succeeds with the same two DataGrid warnings and the check exits
66 with the historical stack trace. Revert afterwards. Doing this again is cheap and
is the only thing that distinguishes this from a step that always passes.

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

### DevTools / visual inspection

`KubeNimbus.App` references `AvaloniaUI.DiagnosticsSupport` **Debug-only** and
calls `WithDeveloperTools()` under `#if DEBUG`, so the Avalonia DevTools MCP can
attach to a running Debug build and screenshot/inspect the tree. It never enters
the Release/AOT build.

### Headless screenshot harness (`tools/Screenshot`)

For environments with no display and no DevTools MCP (Claude Code Cloud
sessions, CI) — renders real Views bound to fixture ViewModels via
`Avalonia.Headless` (Skia software rendering, `UseHeadlessDrawing = false`)
and dumps PNGs. Not part of the shipping app; excluded from the App's
NativeAOT publish.

```bash
dotnet run --project tools/Screenshot -- <outputDir> [scenario-name-substring]
```

Writes one `<scenario>.<light|dark>.png` per scenario × theme to `outputDir`
(pass a scratch dir — nothing under it is committed). Omit the filter to
render every scenario in `Program.cs`'s `scenarios` array.

Key structural point: a `ClusterTabView` (or any inspector tab view) screenshot
must be hosted inside a real `MainWindow`, not a bare wrapper — `ContentControl`'s
implicit `DataTemplate` lookup only resolves `PodDetailView`/`YamlEditorView`/etc
by walking the visual tree to `MainWindow.axaml`'s `Window.DataTemplates`; a
bare `Border`/`Window` wrapper falls back to a `ToString()`-in-a-TextBlock
placeholder instead of the real view. See `HostInMainWindow` in `Program.cs`.

Fixture data (`tools/Screenshot/Fixtures/*.json` — pods, deployments, events,
a 72-kind CRD catalog spanning cert-manager/argoproj/istio/velero/keda/flux/etc
to stress-test sidebar scaling realistically) is loaded by `FixtureData.cs`
into real `DynamicResource`/`ResourceDescriptor` instances. `ClusterTabScenarios.cs`
builds fully-populated `ClusterTabViewModel`s by setting the same public
properties `ConnectAsync`/`RestartWatch`/`Apply` would, using an **offline
`ClusterClient`** (`FixtureData.CreateOfflineClient()`, pointed at
`Fixtures/kubeconfig-fake.yaml` → `https://127.0.0.1:1`, an address nothing
listens on) so ViewModel constructors that require a live `ClusterClient`
(pod detail's event refresh, exec's connect) still work — those calls just
fail fast in the background and are swallowed by the same error handling a
real lost connection already has.

Gotcha already hit once: setting `SelectedNamespace` on a fixture
`ClusterTabViewModel` fires the real `OnSelectedNamespaceChanged` → `RestartWatch()`
hook. With no `Client` wired up that only touches `IsListLoading`/`IsListEmpty`,
but if you set `SelectedNamespace` *before* manually populating `Rows`, the
empty-state flag latches `true` and never gets recomputed (production code
never hits this ordering — there, `RestartWatch`'s background pump is what
populates `Rows`). `ClusterTabScenarios.BaseTab()` recomputes `IsListEmpty`
after populating rows for exactly this reason; follow the same pattern for
new scenarios that set view-model properties directly.

When Docker is available (unlike this session — `docker version` succeeds but
`dockerd` isn't running here), prefer driving the harness against a real
k3s sandbox (see below) instead of fixtures for a final verification pass;
note in the PR which screenshots were fixture-only.

The harness is also **CI's XAML smoke test**. A build that compiles can still
fail to load XAML at runtime — a stale `avares://` URI, a missing embedded
resource, a `DataTemplate` that stops resolving — and rendering every View is
the only check that catches that without a display. `SeedContexts` in
`Program.cs` fills `MainWindowViewModel.AvailableContexts` so the command bar
reads a real context name rather than "No kubeconfig contexts"; that is a real
state, but it is not what these scenarios are about and it makes every shot
look like a failed connection.

Its PNGs upload as a CI artifact **only when the render step went red**
(`if: failure()`, `if-no-files-found: ignore`, `retention-days: 3`). That is
not stinginess about disk — see the Actions storage budget below — it is what
the artifact is for: a green render is a smoke test that passed, and nobody
has ever downloaded 58 PNGs to confirm it. `if-no-files-found` has to be
`ignore` rather than `error` precisely because the common failure is the
render throwing, which leaves the directory empty; a missing diagnostic must
not turn one red step into two.

## Release, CI and packaging

The release workflow, installers, Microsoft Store (MSIX) identity, the assembly-name coupling and the 0.5 GB Actions storage budget are in the `release` skill ([`.claude/skills/release/SKILL.md`](.claude/skills/release/SKILL.md)). Load it before cutting a release or touching `.github/workflows`, `installer/` or the packaging scripts. Two rules that must not wait for it: **every `upload-artifact` sets `retention-days`**, and **the MSIX identity in `installer/msix/Package.appxmanifest` and the MSI `UpgradeCode` are never edited**.

## History

The MVP scope and the per-pass verification log (what each pass shipped, verified and left unverified) are in [`docs/status-history.md`](docs/status-history.md). Record a new pass there, not here.

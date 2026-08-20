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

Where KubeUI is ahead and we are not: signed and notarized binaries, installers
with auto-update, winget/Store/Homebrew distribution, and schema-aware YAML
completion. Node drain and server-side dry-run were on that list and are not any
more — see "Node operations" and "The apply preview" below. None of the rest is a
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
   lazily-realized tab content and an index that survives a hidden tab — pod
   detail's Usage rides the Advanced view, and `SelectedDetailTabIndex`
   (Logs=0, Env=1, Events=2, Usage=3) is depended on by
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
   Restarts 78, CPU 98, Memory 106, sparklines 34). Check `cluster-tab-advanced-view`
   at its rendered 1280px, which is narrower than most real windows and is where this
   fails first. A CRD's own printer columns are a *variable* number of columns on that
   same fixed width, and their answer to this is kubectl's own `priority` field rather
   than another re-cut — see "CRD printer columns". The minimums below are the layout a
   list *opens* with; since FEAT-66 they are no longer the last word, because the reader
   can drag any column and the choice is kept per kind — see "The resource grid is the
   reader's to re-cut".

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

[fluent-basics]: https://learn.microsoft.com/en-us/windows/apps/design/basics/

## Multi-pod logs (one workload, one stream)

`WorkloadLogsTabViewModel` + `WorkloadLogsView` tail every pod a workload owns in a
single inspector pane, colour-keyed by pod. It is the job `stern` exists for, and the
thing that makes a *rolling deployment* readable: during a roll, the pod going away and
the pod coming up are one question, and reading them in two panes reads them in the
wrong order. Reached from the row context menu ("Logs (all pods)") and from Ctrl/Cmd+K —
no new always-visible control (UI rule 1), and no new key binding, so
`docs/keyboard-shortcuts.md` is unchanged.

Eight things are load-bearing.

1. **Which pods comes from the object's own selector, resolved by the API server.**
   `LabelSelector.ForPodsOf` (Core) reads `spec.selector` in both shapes Kubernetes uses
   — the `LabelSelector` object (`matchLabels`/`matchExpressions`: Deployment,
   StatefulSet, DaemonSet, ReplicaSet, Job) and the plain string map (Service,
   ReplicationController) — and the pane runs it as a `labelSelector` list+watch. That
   is capability from the object, never from a list of kinds, exactly as
   `WorkloadActions.SupportsRestart` is: a CRD that declares a pod selector qualifies on
   the same evidence a Deployment does, and neither is named anywhere. It also settles
   the rollout case for free — a Deployment's selector names the *app*, never the
   pod-template hash, so both ReplicaSets are in scope. Resolving pods by walking the
   owner chain instead would have selected the current ReplicaSet's pods and quietly
   lost the half of the rollout the pane exists to show. `LabelSelectorTests` pins that.
2. **An empty selector is refused, not read as "everything".** Kubernetes' own semantics
   for an empty `LabelSelector` are "select all", and honouring that here would open a
   log stream against every pod in the namespace because an object happened to declare
   `selector: {}`. `ForPodsOf` returns null instead, the capability check reads that as
   "not offered", and the menu item is simply disabled. Aptakube shipped the other
   behaviour and had to withdraw it (aptakube#227). An unknown `matchExpressions`
   operator is refused for the same reason in miniature: dropping a requirement *widens*
   a selector, so a selector whose only requirement is unreadable comes back null rather
   than matching everything.
3. **The per-pod tail is the pane's own budget divided by the pod count.**
   `PerPodTailLines(bufferLines, podCount)` = `clamp(bufferLines / podCount, 25, 200)`,
   and this is the decision to read before changing anything here. The single-pod pane
   fetches a literal `tailLines: 200`; N replicas at 200 each is N × 200 lines of
   backfill competing for one shared `LogBufferLines` cap, so past a handful of replicas
   the oldest pods' history is trimmed away before anybody can read it — a pane that
   silently drops a whole replica's backfill is worse than one that asks for less of
   each. Dividing keeps the opening burst inside the buffer. The **ceiling of 200 is
   deliberate and is a scope boundary, not a limit anyone likes**: how much history a log
   pane should ask for is its own open question (this app offers no tail/since control on
   any surface, and its window is the smallest of the comparable tools), and answering it
   here for the multi-pod case only would leave the two panes disagreeing about the same
   thing. The floor of 25 stops a large workload reducing each replica to nothing.
   `LogBufferLines` is a **per-pane** cap here, which it already was — there is one
   buffer per tab — and not a per-pod one.
4. **Concurrency is capped at 50 streams, and the cap is stated.** N pods is N long-lived
   HTTP connections against one API server; a Deployment scaled to 400 would otherwise
   open 400 of them because someone clicked a menu item. 50 is `stern`'s own
   `--max-log-requests` default. Pods past the cap are not streamed and `CapNotice` says
   so in an `infoBar` — an aggregated pane showing 50 of 120 replicas without saying
   which number it is would be a lie by omission.
5. **The merge is two-stage, and the reason for the split is the whole design.** Every
   stream is already requested with `timestamps=true`, so each line carries the server's
   RFC3339 instant. The *opening burst* — N pods each answering with their tail at once —
   is held for a 900 ms prime window and then sorted as one block; without that, a
   three-replica pane opens with pod A's hour, then pod B's hour, then pod C's, which is
   three streams shown consecutively and fails the item's acceptance criterion outright.
   After the burst, each 100 ms flush tick sorts only what arrived within it. A **true
   k-way merge was considered and rejected**: holding a line back until every other
   stream has produced something at least as new is what a finished log file allows and a
   live tail does not — one quiet replica would stall the pane for everybody, which is
   the opposite of what a tail is for. So out-of-order arrival past a tick is possible,
   and the timestamps toggle is what settles an argument about it. `AsyncMerge` was
   considered too and is *not* used: it interleaves in arrival order, which is the one
   thing this pane must not do, and the per-source failure isolation it provides is
   already had here from one task per stream.
6. **The sort is stable and carries timestamps forward.** Two pods that logged in the
   same millisecond keep their arrival order (LINQ's `OrderBy` is a stable sort;
   `Array.Sort` is not — that is why `OrderBatch` sorts an index array through `OrderBy`),
   and a line whose leading token is not a timestamp inherits the instant of the line
   before it, so a stack trace stays attached to the line it belongs to instead of being
   flung to the top of the batch. Both are pinned by `WorkloadLogsTests`, and both were
   confirmed to turn the suite red before the tests were called done.
7. **The buffer is the streams' complete record; `LogLines` is the projection.** This is
   UI rule 13's invariant in the log pane, and it fails the same way: a line that arrives
   *while* its pod chip is toggled off must still be buffered, or re-including that pod
   shows only what it says from then on and the minutes it was hidden are gone with
   nothing to indicate anything is missing. Filtering on the way in rather than on the
   way out is the mistake; it was written into the code and confirmed to turn the suite
   red. A pod that is **deleted** likewise keeps its lines — what a terminating replica
   said last is usually why the pane was opened — and a `Reset` from the informer (a
   410-Gone relist) deliberately does **not** clear the sources, because every pod still
   there arrives again as `Added` a moment later and clearing would cancel healthy
   streams and discard a buffer no reconnect can refetch.
8. **One container per pod: the one `kubectl logs` picks with no `-c`.** The chip names
   it, so what is being tailed is stated rather than assumed. Tailing *every* container of
   a pod, colour-keyed by container, is a separate and strictly smaller feature; sources
   here are keyed by pod **and** container from the start, so that becomes a change to
   which sources are created and to nothing else — not a change to the merge, the buffer,
   the legend or the view.

**The colour palette is one set for both themes**, eight mid-tone hues in
`LogSourcePalette`, cycling past eight. Same argument as the exec terminal's palette: a
colour resolved once and held (here, a brush per line) does not follow a live theme swap,
and a half-swapped palette is worse than a single one that works in both. The colour is a
hint beside a name that is always printed, not an identifier, so an honest repeat past
eight beats inventing hues nobody can tell apart. Both themes are rendered by the
screenshot harness, which is where that claim is checked rather than asserted.

**The demo cluster runs this for real** (demo rule 4): its three
`payment-service-report-generator` replicas exist precisely for this — two on the old
ReplicaSet and one on the new — and their canned streams interleave by timestamp so that
what the pane renders offline is a rolling deployment read as one stream. The pods are
found through the same `LabelSelector.Matches` a live cluster's query is rendered from,
and every line goes through the same merge, buffer and filter. Nothing about this pane is
demo-unavailable.

**Core gained `labelSelector` to make it possible.** `WatchResourceAsync` and
`ListResourceOnceAsync` take a `LabelSelector?`, and the watch engine gained an
`extraQuery` string appended to the watch request. The one trap: the selector must be
escaped identically on the list half and the watch half, or the watch reports additions
the list never seeded — `LabelSelectorQuery` is the single place that renders it.

## Log severity is three classes, not a brush binding

The log pane's severity colouring is `SelectableTextBlock.logError` / `.logWarn` /
`.logInfo`, set from three bools on `LogLineViewModel` and styled in `Styles/Theme.axaml`.
`LogSeverityToBrushConverter` is **gone**, and the reason is worth the paragraph because
this is the second time the same bug shipped:

- The first version returned `null` for the default (no keyword) case. A null
  `Foreground` is a *local* value, it beats inheritance, and Avalonia's glyph-run draw
  early-returns on a null brush — so every line without a severity keyword rendered
  invisible. Fixed by returning `AvaloniaProperty.UnsetValue`, on the stated reasoning
  that "unset means no value here, so inheritance wins".
- **It does not.** Measured on the rendered dark-theme pane, a line whose `Foreground`
  *binding* produces `UnsetValue` falls back to `TextElement.Foreground`'s own default,
  which is opaque black — not to the inherited foreground. On the dark theme's `#080808`
  card that is invisible, exactly as before. The pixels: plain lines rendered at
  `(0,0,0)`/`(7,8,8)` on an `(8,8,8)` background while `INFO` lines rendered at
  `(78,158,242)`. Replacing the default case with a bright red confirmed the binding was
  what governed those lines rather than anything above them.
- Classes have no such failure mode: an unclassified line carries **no `Foreground`
  binding at all**, so it inherits the way every other `TextBlock` in the window does.
  The three colours are the converter's own, unchanged.

Two things about how this survived so long, both of which generalize. It is invisible in
the **light** theme, whose own text is nearly black — so a light-theme screenshot of a
correct pane and of a broken one are identical. And the population it hits is exactly the
lines that carry no severity keyword: nginx access logs, `log.Print`, anything JSON, i.e.
most real output — which is why `DemoLogs` is required to keep carrying them (demo rule 4)
and why *looking at the dark screenshot* is not optional for anything that colours text.

## Pod detail's Overview tab (conditions, tolerations, QoS, priority, probes)

`PodDetails.cs` (Core) and pod detail's fifth tab render the structured half of
`kubectl describe pod`: the pod's `status.conditions`, its `spec.tolerations`, its
`spec.nodeSelector`, its QoS and priority class, and the selected container's
liveness/readiness/startup probes. All of it was previously reachable only by opening the
YAML editor and scrolling — a page of text to answer "is the readiness probe why this
never goes Ready?" — while three of the five comparable clients (the Lens/OpenLens/
FreeLens lineage and Headlamp, both confirmed against their own component source; k9s's
`d` in its own idiom) render exactly these fields unprompted. See
[`docs/research/2026-08-18-pod-workload-detail.md`](docs/research/2026-08-18-pod-workload-detail.md).

**It is structured fields, not a `describe` text clone, and that is a cost decision as
much as a design one.** kubectl's `describe` is a large Go `text/template`-shaped
formatter; none of the competitors reimplement that prose, and reproducing it here would
be a new formatter to maintain against a moving target. Reading the same `JsonElement`s
`ReadContainerSpecs` already reads costs no dependency, no reflection and nothing at AOT
time.

Eight things are load-bearing.

1. **Overview is tab index 4, appended after Usage.** `SelectedDetailTabIndex`'s existing
   values (Logs=0, Env=1, Events=2, Usage=3) are depended on by
   `ClusterTabViewModel.OpenLogs` and by the screenshot scenarios — which is exactly why
   the Usage tab was appended after Events rather than inserted where it reads best. A
   new tab goes on the end for the same reason, even though "overview" is the section
   someone would put first. Usage's `IsVisible` gate does not move it: a hidden `TabItem`
   keeps its index.
2. **No new chrome, and no picker of its own** (UI rules 1 and 10). The tab adds one
   entry to the strip that already exists and nothing to the row it shares; probes are
   container-scoped and the container strip two rows up is already their selector, which
   is the same relationship the Environment tab has with it. A section that kept showing
   the first container's probes under the second container's name would be the bug the
   log stream had before it followed the picker — `PodOverviewTests` pins that it follows.
3. **Not gated on the Advanced view.** The switch's job is "hide what you did not come
   here for"; this is what the field ships unprompted and what the item asked for as
   always-visible. Nothing here polls, fetches or watches, so it costs nothing to leave on.
4. **A pod's conditions are the opposite polarity from a node's, and unknown types get a
   third answer.** `NodeCondition.IsProblem` reads polarity off `Ready` because a node's
   other conditions are all pressure conditions; a pod's are mostly *positive* — the four
   the scheduler and kubelet set, plus `PodReadyToStartContainers` — and `DisruptionTarget`
   is the one Kubernetes defines the other way. A type on neither list comes back
   `PodConditionPolarity.Unclassified` and renders grey rather than green:
   a custom readiness gate is positive by construction but `PodResizePending` is not, and
   a false reassurance is the wrong way to be wrong for the one person reading this pane
   *because* something is wrong. `PodCondition.IsProblem` is therefore `bool?`, and an
   `Unknown` status is the same third answer.
5. **The QoS class is read, never derived.** It is a pure function of the containers'
   requests and limits and could be recomputed, but the API server has already computed it
   and the eviction path uses *its* value; a local one that disagreed would be worse than
   an empty cell. An object carrying none never went through a server, and the pane says
   so instead of inventing one.
6. **Every toleration is listed, admission's own included.** The DefaultTolerationSeconds
   plugin adds `node.kubernetes.io/not-ready` and `unreachable` (both `NoExecute`, 300s)
   to nearly every pod. They are noise right up until the moment someone is comparing one
   pod against another, at which point hiding them makes a pod that genuinely declares one
   indistinguishable from one that does not. `PodToleration.Display` is kubectl's own
   rendering, including the empty-key `op=Exists` form that tolerates everything and the
   no-effect form that must not print a dangling colon.
7. **A probe's timings are defaulted to the API server's own when the object omits them.**
   The server defaults all five on admission, so a probe missing them has never been
   through a server; printing `delay=0s timeout=1s period=10s #success=1 #failure=3` is
   what that object *would* be given and what `kubectl describe` ends up showing for it.
   Printing nothing would read as a probe with no configuration at all. The handler line
   is kubectl's shorthand too (`http-get`, `exec [...]`, `tcp-socket`, `grpc`), so a probe
   read here and one read in a terminal are visibly the same probe — and a named port is
   printed **as written**, never resolved against the container's port list, because a
   probe aimed at a port name that does not exist is precisely the failure being chased.
8. **The rebuild is signature-guarded, and the guard is on content, not on "have we
   rendered".** A watch tick on a healthy pod is almost always a status refresh that
   changes none of these fields, and rebuilding four `ItemsControl`s per tick discards
   scroll position and any half-made text selection — the same reason the Environment tab
   is guarded. Guarding on first-render instead would swallow a condition *change*, which
   is the whole reason the section exists; both mistakes were written and confirmed red
   before `PodOverviewTests` was called done.

**The demo dataset carries all of it** (demo rule 4): the report-generator pod the
pod-detail scenarios open has conditions, a node selector, a priority class, three
tolerations and three probe shapes across its two containers, and `fraud-detector` is the
opposite state — one genuinely bad `PodScheduled: False` with the scheduler's own message,
and every other section empty, which is the whole of UI rule 9 for this pane on one
object. `legacy-batch-runner` deliberately still carries none of it. The sandbox gained
the same states (`scripts/manifests/10-shop.yaml`: a `PriorityClass`, a node selector that
still schedules everywhere, a toleration, and httpGet/tcpSocket/exec probes across
shop-web's two containers) because nothing in it produced any of them before.

**Container requests and limits are the Usage tab's business, not this tab's** — see the
next section. They were already computed when this tab shipped and were reachable only
by hovering a chip, which is a different gap with its own (older, louder) evidence.

## Requests and limits are text on the Usage tab

`ContainerViewModel.CpuResourceText` / `MemoryResourceText` print what each container asks
for and what it is capped at, under the measured line and above the sparkline, on pod
detail's Usage tab. The numbers themselves are not new: `ReadContainerSpecs` has parsed
`spec.containers[].resources` into the container row since the metrics pass, and the only
place they were rendered was a hover tooltip — which is exactly what lens#4154 has been
asking for since 2021 and what FreeLens shipped a fix for after Lens left it broken. So
this is a rendering change on numbers the app already had.

Six things are load-bearing.

1. **A missing request and a missing limit are said in words, not left blank.** They are
   independently optional in Kubernetes and a container declaring neither is the
   commonest shape there is, so each half prints "no request" / "no limit" and the empty
   pair collapses to one sentence, `no request or limit set`. A blank cell there reads as
   a number this pane failed to fetch, and a zero reads as a request of nothing, which is
   a different and much worse claim (UI rule 9). `ContainerResourceTextTests` pins all
   four combinations, and the blank-instead-of-words break was written and confirmed red
   before they were called done.
2. **The percentage is offered only where there is a limit and a reading.** `Quantity
   .Percent` already returns null for a zero or absent denominator; what is added here is
   that anything above zero and under one percent prints `<1%` rather than rounding to
   `0% of limit`, which reads as "measured nothing" for the ordinary case of a container
   idling under a generous cap.
3. **The declared line does not follow the metrics gate, and that is the whole point.**
   Requests and limits come from the pod spec, so they are readable on a cluster that
   serves no `metrics.k8s.io` at all — the case where they are worth *most*, because
   nothing else on the tab has anything to say. The two "no charts" states are therefore
   `Border.infoBar` notices stacked above the content (UI rule 11) rather than full-tab
   panels shown *instead* of it, which is what they were: the old markup would have hidden
   the numbers behind exactly the condition that makes them the only thing left. Gating
   them on a measurement was the second break written and confirmed red.
4. **The tab itself stays gated on the Advanced view, and that is deliberate rather than
   an oversight.** The honest reading of the item is that requests and limits are not
   metrics data, so ungating them from *metrics* is required; ungating the whole Usage tab
   from the Advanced switch is a different change to a different rule — the switch's job
   is "hide what you did not come here for", `SelectedDetailTabIndex`'s values are
   load-bearing, and moving these numbers onto Overview instead would be redesigning the
   item rather than implementing it. If a later pass concludes requests/limits belong on
   an always-visible surface, Overview's placement section is where that argument goes.
5. **`IsCollectingUsage` exists because the two notices are now stacked, not exclusive.**
   Each has to know the other is not showing. And a cluster with no metrics API clears
   `UsageWindowCaption`: the strip's "collecting…" beside a notice saying nothing is ever
   going to be collected is a contradiction, and it is cleared from the generated
   `OnIsMetricsUnavailableChanged` partial rather than from a command (UI rule 8b).
6. **The chip's tooltip stays; the Usage card's copy of it goes.** Hovering a container
   chip still summarises usage against requests and limits from any tab, which costs
   nothing and is the quick-peek. The identical tooltip on the Usage card's *title* is
   gone — the card body now states those numbers as text two lines below, and a tooltip
   repeating the text under it is noise.

**The demo dataset carries both sides** (demo rule 4): the report-generator pod's `app`
container declares a CPU and memory limit, so the percentage renders, and its
`envoy-sidecar` declares requests only, which is the commonest real shape. The
unschedulable `fraud-detector` was already declaring no resources at all and is the third
state, rendered by `cluster-tab-pod-detail-usage-unset`. The sandbox needed no change:
`shop-web`'s two containers are already the request+limit / request-only pair, and
`40-broken.yaml`'s `bad-image` and `unschedulable` pods declare neither.

The three usage scenarios render **maximized** now. The per-container section sits below
two full-width pod-total charts, so at the dock's default ~300px it was off screen — which
is a small demonstration of the item's own complaint: numbers that exist and cannot be
seen.

## ConfigMaps are shown, Secrets are masked

Pod detail's Environment tab treats the two reference kinds differently, and the
difference is the point — they used to be one code path with one "Reveal" chip,
which was simultaneously too guarded and not guarded enough:

- A **`configMapKeyRef` resolves on open** and renders like any literal, with
  `ConfigMap/<name> · key=<k>` as the caption underneath. A ConfigMap is not
  secret; it is ordinary configuration that anyone who can read the pod can read
  anyway, and charging a click to see `LOG_LEVEL=info` bought nothing. It costs
  one GET per *distinct* ConfigMap — `PodDetailTabViewModel.ResolveConfigMapValuesAsync`
  awaits them one at a time precisely so eight keys of one object don't become
  eight parallel misses of `_secretConfigMapCache`. It is fire-and-forget from
  `RefreshEnvironment`, guarded by the same env signature, so a watch tick that
  changed nothing re-fetches nothing; a failure lands on its own row's
  `RevealError`, never on the tab.
- A **`secretKeyRef` stays masked** behind `EnvVarViewModel.MaskedValue` (a fixed
  eight dots — the *length* of a secret is worth not leaking too) with an
  eye/eye-off toggle. Nothing is fetched until it is clicked: the mask is a
  placeholder, not a hidden copy, so a secret never enters this process — or a
  screen-share — because a pane happened to be open, and an RBAC 403 on Secrets
  lands on the one row someone asked about rather than on four nobody did.
  `ToggleEnvVarCommand` keeps the fetched value and flips `IsRevealed`, so the
  eye can **hide** it again; the old chip was one-way, and a value revealed on a
  shared screen stayed there until the tab was closed.

The YAML editor's Secret "Reveal values" panel is the other half of this and is
unchanged: `data` stays base64 in the editable text, matching kubectl.

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

## The command catalog (shortcuts, palette, cheat sheet, docs)

`KubeNimbus.Core/Commands/` is the single source for every command and documented
gesture: `CommandCatalog` holds the descriptors, `Chord`/`CommandKey`/`ChordModifiers`
express a key combination without naming a platform, and `ShortcutDocs` renders the
whole thing as `docs/keyboard-shortcuts.md`. The App layer projects it —
`CommandBindings` turns descriptors into Avalonia gestures and resolves ids to
view-model commands, `ShortcutsViewModel` builds the F1 sheet, `CommandTip` builds
tooltips. It replaced a hand-written `Hotkeys.CheatSheet` array plus gestures typed
into four places.

Six things worth keeping:

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
6. **The docs page is a golden file.** `ShortcutDocsTests` fails on any drift;
   `KUBENIMBUS_UPDATE_DOCS=1` regenerates it. A shortcut reference that can silently
   fall behind the app is worse than none.

## The Advanced view

One global persisted boolean, default **off**, mirrored onto every cluster tab
and every inspector tab. It answers a complaint about the whole surface ("too
much stuff for every Kubernetes type"), not about any one control, so it is one
switch rather than a preferences page of them — the same shape as pgNimbus's
`ShowAdvancedObjects`, in the same place (an icon-only `ToggleButton
Classes="chip"` docked right of the sidebar's filter box, tooltip carrying the
explanation), because people who use both should find it where they left it.

Off hides: the CPU/Memory columns and their sparklines, pod detail's Usage tab,
the fleet toggle and Cluster column, the log toolbar's Wrap/Copy/Download, YAML
force-apply, the sidebar's kind-count badges, the Helm/RBAC palette entries, and a
CRD's own `priority: 1` printer columns — that last one is the switch acting as
kubectl's `-o wide`, which is the closest thing it has to a definition.
(The "Following &lt;container&gt;" caption used to ride the switch too; it is gone
outright — the container strip names the container it is streaming and the log
pane's own placeholder states say what the stream is doing, so it was a row of
dock height spent restating both. **The exec pane's Send button rode it and is
gone with the pane's input box** — a real terminal takes the keystroke itself, so
there is no longer a control to gate; see "The exec terminal".)
On restores today's surface exactly — it is a hide/show switch, not a second
layout, and `cluster-tab-workloads-list` / `cluster-tab-advanced-view` are the
same fixture tab rendered both ways to keep that honest.

Four things are load-bearing:

- **It is a display switch and nothing else.** Flipping it must never restart a
  watch, refetch anything, or lose list/inspector state — which is why every
  consumer is a derived property, and why the fleet toggle stays visible while
  aggregation is *on* even with the switch off (`IsFleetToggleVisible`).
  Stranding a tab in fleet mode with no way out would mean the switch had
  changed behaviour, not just visibility.
- **Nothing it hides becomes unreachable.** Everything has a Ctrl/Cmd+K entry,
  including the switch itself, which is what makes hiding by default safe.
- **The shell owns it; tabs carry a mirror.** `MainWindowViewModel` persists it
  (`WorkspaceSettings.IsAdvancedView`) and broadcasts; `ClusterTabViewModel` and
  `InspectorTabViewModelBase` hold copies so views can use compiled bindings
  against their own DataContext. It is stamped in `ClusterTabViewModel
  .AddInspectorTab` — the one funnel every inspector tab enters through — so a
  tab kind added later inherits the gate instead of shipping with it open,
  which is exactly what the YAML editor's force-apply did before that existed.
- **Adding a tab is what stamps it.** The screenshot harness therefore sets the
  flag on the *shell* after adding the tab (`HostInMainWindow`), not on the tab;
  setting it on the tab alone is silently overwritten.

## The cluster switcher and environment colours

The top bar's context `ComboBox` is gone. It failed three ways at once, and the
replacement is shaped by what every comparable tool converged on:

- **It couldn't search.** kubectx ships an fzf integration, kubeswitch keeps a
  pre-computed search index explicitly "for operators of large scale Kubernetes
  installations", k9s has `:ctx` — all of them exist because scrolling a context
  list stops working around a dozen entries, and real estates run to hundreds
  (FreeLens has a bug report about its cluster list silently capping at **63**).
- **It truncated the distinguishing part.** Managed clusters hand out names like
  `arn:aws:eks:us-east-1:481516234298:cluster/search-staging`; at 150px every one
  of those reads the same.
- **It wasn't a switcher.** It only chose what the `+` button would open, so
  reaching an already-open cluster was a different gesture entirely.

`ClusterSwitcherViewModel` (Ctrl/Cmd+P, or the top bar's cluster button) is one
ranked, fuzzy-searchable list over **both** open tabs and unopened contexts,
grouped Open / Pinned / Recent / All. Ranking is prefix > contiguous >
subsequence > cluster-name/kubeconfig-path, so `ppr` finds `payments-prod`.
Pins and recents persist in `WorkspaceSettings` (context **name** only — kubeconfig
merge semantics already make names unique, and the path would break the key when
a file moves). Notes for anyone changing it:

- **The results list is flat, deliberately.** Section titles ride on the first
  row of each group (`ClusterSwitcherItemViewModel.SectionHeader`). A nested
  ItemsControl-of-ListBoxes gives every section its own selection, and they clear
  each other's the moment they share a `SelectedItem`; flat also keeps arrow-key
  scroll-into-view working. The selection highlight therefore lives on an inner
  `Border.switcherRowBody`, not on the `ListBoxItem` — the container spans the
  group heading too, and highlighting it draws the selection around the title.
- **Never preselect the current tab.** The first Enter has to go somewhere.
- A context that is already open appears **only** under Open, never twice.
- **Row activation is handled on the ListBox, not in the item template.** See the
  hit-testing rule below — this one shipped broken once already.

**Environment colours** (`ClusterEnvironment` / `ClusterEnvironments.Classify`,
Core) are the other half. "One wrong kubectl command in the wrong context can
take down production" is the most-cited multi-cluster failure mode, and the
industry answer is uniformly colour — dev green, staging amber, prod red (kube-ps1,
kubectx wrappers, and a dedicated JetBrains "KubeContext Safety" plugin). Four
rules:

1. **The guess is biased toward production.** Over-flagging costs a red band on
   staging and one right-click to fix; under-flagging is the incident. So "prod"
   anywhere fires, and only the explicit non-production compounds (`preprod`,
   `non-prod`, …) are rescued — checked *first*, since each contains a production
   marker.
2. **Markers match whole tokens and adjacent token pairs, never substrings.**
   Substring matching reads "product-catalog" as production and "internal-tools"
   as an integration environment. The pair rule is what makes separator-spelled
   compounds work: `non-prod-eu` tokenizes to `["non","prod","eu"]`, so the rescue
   only fires if `non`+`prod` is tested as one candidate. `ClusterEnvironmentTests`
   pins every case — a change that makes a real production cluster read as
   anything else is a regression, not a tuning choice.
3. **The user always wins.** `WorkspaceSettings.EnvironmentOverrides` is applied
   in `MainWindowViewModel.EnvironmentFor`, which everything that colours a
   cluster goes through. It's reachable by right-clicking a cluster tab — a
   colour nobody can correct is a colour people learn to distrust.
4. **Production is not `ErrorBrush`.** An environment is not a failure; reusing
   the error colour would make every prod cluster look broken.

Where it shows: a dot on the switcher button, a left edge on each cluster tab, a
pill in the switcher, and a 2px band under the command bar **only** while the
selected cluster is production — the sole always-visible chrome the scheme adds
(UI rule 1), and it costs nothing the rest of the time because it isn't there.

`WorkspaceStore.DirectoryOverride` exists for the screenshot harness: scenarios
construct real `MainWindowViewModel`s, which read the workspace on construction
and write it when a cluster is pinned, so without the redirect rendering fixtures
would clobber the developer's own tabs and pins.

## Repository layout

```
src/KubeNimbus.Core        Engine: kubeconfig, ClusterClient (watch/logs). No UI.
src/KubeNimbus.App         Avalonia 12 desktop shell.
tests/KubeNimbus.Core.Tests  TUnit unit + integration tests; the latter skip with no cluster.
tests/KubeNimbus.App.Tests   TUnit view-model tests. No Avalonia app, no cluster, no display.
tools/Screenshot           Headless visual-verification harness. Dev-only.
design/                    Logo sources (SVG) + generated masters/store/screenshots.
scripts/                   Sandbox cluster bootstrap + the icon/logo pipeline.
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
| `docs/BACKLOG.md` | What is queued, and the state the backlog loop runs from — see below. |

## The backlog loop

`docs/BACKLOG.md` is the queue, and `.claude/` holds the machinery that works
through it: `/backlog-cycle` (the orchestrator, one item per run, driven by the
`/loop` skill) plus three agents — `kn-implementer` (Opus, builds it),
`kn-verifier` (Sonnet, re-runs the checks and reviews against the rules above,
with no Edit tool so it cannot quietly fix what it should be reporting), and
`kn-researcher` (competitor demand and marketing emphasis, writing dated reports
under `docs/research/`).

Three things about it are load-bearing:

1. **The loop may only take work from the Ready table, and only a human puts
   anything there.** Research proposals and newly-found work land in the Inbox
   with the priority column blank. An agent promoting its own suggestion into
   Ready would close the only loop in this arrangement that has a person in it.
2. **Verification debt is an item, not a footnote.** Whatever the verifier
   reports as unverifiable in its environment — no live cluster, no Windows or
   macOS box, no display — becomes its own Inbox row in the same cycle. This
   repo has repeatedly lost track of exactly that, and the cost is on record:
   three of four release RIDs shipped a binary that could not start, because
   `ci.yml` publishes the AOT output and has never launched it.
3. **`MAX_FIX_ROUNDS` exists so a stuck item becomes a `blocked` row with a
   precise note** rather than a fifth round of the same failure.

## App icon / logo assets

Full reference: [`design/LOGO-ASSETS.md`](design/LOGO-ASSETS.md) (pipeline,
every file, every consumer); [`design/LOGO.md`](design/LOGO.md) covers how the
mark's geometry was derived. Three rules matter here:

1. **Only `design/logo.svg` and `design/logo-dark.svg` are hand-edited.** The
   six small/micro SVGs are *generated from `logo.svg`* by
   `scripts/design/make-small-masters.py`, and everything under
   `design/masters/`, `design/store/`, `design/screenshots/` and
   `src/KubeNimbus.App/Assets/*.ico|Msix/**` is generated and checked in.
   (`design/screenshots/` comes out of `tools/Screenshot`, not the logo
   pipeline — see [`design/screenshots/README.md`](design/screenshots/README.md)
   for the scenario→file mapping.) Fix art in the SVG, then re-run the scripts,
   **in this order** — each eats the previous one's output:

   ```powershell
   python scripts/design/make-small-masters.py # design/logo-{small,micro}*.svg (only if the broom changed)
   pwsh scripts/design/make-masters.ps1        # design/masters/**
   pwsh scripts/windows/make-app-icons.ps1     # src/KubeNimbus.App/Assets/**
   pwsh scripts/windows/make-store-logos.ps1   # design/store/**  (only if the mark changed)
   ```

2. **There are three marks, not one, and that is deliberate.** `logo.svg` (full
   mark) is rendered at 32px and up; at 24px its eight helm spokes land ~1px
   apart and at 16px the helm reads as a filled circle, so `logo-small.svg`
   (24px, six rays) and `logo-micro.svg` (16px, four rays) exist as simplified
   marks in the same 1024 grid. Rendering every size from `logo.svg` is the
   specific bug this split prevents — don't "simplify" it away. **All three
   carry the broom**: dropping it at small sizes turns the taskbar icon into a
   generic ship's wheel, which is the one place identity matters most. The
   small marks use `logo.svg`'s *own* broom paths, which is only possible
   because `#brand-broom` is self-contained — so a change to the full mark's
   broom is a re-run of the generator, not a redraw.
2b. **The small marks have no disc; `app.ico` still does.** Dropping the plate
   gives the mark ~40% more pixels at 16-24px and is what the unplated
   Windows/MSIX slots want. But Windows gives the taskbar, Alt+Tab and the
   title bar a *single* `WM_SETICON` slot, so `app.ico` cannot be theme-aware
   and unplated dark line art vanishes on a dark taskbar (the default). Hence
   `logo-{small,micro}-plated.svg`, which exist for `app.ico` alone. Don't
   "unify" the two — that regression is invisible until someone looks at
   their taskbar.
3. **`Msix/**` is packaging-time-only.** The csproj marks `Assets/*.ico` as
   `AvaloniaResource` and nothing else, so the tile PNGs stay source-tree
   inputs for a future MSIX pack step and never enter the binary. `app.ico` is
   both the exe icon (`ApplicationIcon`) and the runtime window icon
   (`MainWindow.axaml`'s `Icon="/Assets/app.ico"`).

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

- **Discovery** (`ClusterClient.Discovery.cs`) walks `/api` and `/apis` with
  raw `JsonDocument` parsing (same reasoning as watch frames — no source-gen
  model needed for a shape this simple) into `ResourceDescriptor` records.
  `SidebarGrouping` (App layer) buckets each descriptor into
  Workloads/Network/Config/Storage/CRDs by Kind — an unrecognized API group
  falls through to CRDs automatically, nothing is hardcoded.
  A descriptor also carries the server's **`Subresources` and `Verbs`** for that
  kind. Subresources arrive as sibling entries in the same array
  (`deployments/scale`) in no guaranteed order, so they are collected in a first
  pass and attached in a second; they are still never browsable kinds of their
  own. This is the evidence every capability check uses — see "Mutating workload
  actions" below. `Verbs` empty means **not known**, not "none"
  (`ResourceDescriptor.AllowsVerb` answers true): descriptors built by hand — the
  well-known statics, the demo catalog, fixtures — carry none, and reading that as
  a prohibition would silently disable a feature everywhere except a live cluster.
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

## CRD printer columns

A CustomResourceDefinition declares the columns it wants a list of its objects to have
(`spec.versions[].additionalPrinterColumns`), and kubectl honours them: `kubectl get
certificates` prints READY / SECRET / AGE, not a generic status. This app printed the
same generic Status column for every one of the ~70 CRD kinds a real cluster carries,
which is the weakest surface in a client whose third hard rule is that CRDs are
first-class. `PrinterColumns.cs` + `SimpleJsonPath.cs` + `ClusterClient.PrinterColumns.cs`
(Core) read and evaluate them; `ResourceRowViewModel.PrinterCells` and
`ClusterTabView.ApplyPrinterColumns` render them.

**`ResourceStatusSummary` still owns every built-in kind, and the mechanism is the API,
not a list.** A CRD's own name is required to be exactly `<plural>.<group>` and its
group is required to be non-empty, so a kind names the object to fetch with no search —
and nothing in the core group can be a CRD at all. A built-in, an aggregated API
(`metrics.k8s.io` is not a CRD) and a user with no read access to `apiextensions.k8s.io`
all come back empty from the same GET, and an empty set is exactly today's list. So the
built-ins are not *excluded* from this; there is simply nothing to find for them.

Seven things are load-bearing:

1. **One GET per kind, lazily, cached per tab — not a list at connect.** A CRD object
   carries its whole OpenAPI schema; listing them on a cluster with cert-manager, Argo
   and Istio installed is tens of megabytes fetched to answer a question about kinds
   nobody may open. The negative answer is cached too, or reselecting a built-in kind
   would cost a 404 every time.
2. **Asking the API server for a Table was considered and rejected.** `Accept:
   application/json;as=Table` is what kubectl does and would give byte-identical columns
   for CRDs *and* built-ins — but a Table row is rendered strings with no object behind
   it, and this app's list is a **watch**, feeding the informer, the YAML editor, the row
   actions and the status pill from the object itself. It would also take the built-ins
   away from `ResourceStatusSummary`, which this change is not allowed to do.
3. **The JSONPath subset includes the condition filter, and that is the point.**
   `.status.conditions[?(@.type=="Ready")].status` is how cert-manager, Flux, KEDA *and*
   Argo all spell their Ready column, so a subset without it would blank the single
   most-wanted column on the most-installed CRDs. Supported: dotted fields, `['key']`
   (the only way to reach a key containing a dot), `[n]`, `[*]`, and `==`/`!=` filters.
   Anything else resolves to **no match**, which is the same outcome as an absent field:
   an empty cell, never an exception on a watch tick. Only the *first* match is used,
   matching the API server's own `tableconvertor` ("as we only support simple JSON path,
   we can assume to have only one result").
4. **`priority` is wired to the Advanced view.** kubectl shows `priority: 0` in the
   default table and the rest only under `-o wide`; the CRD author has therefore already
   marked which columns matter less, and this app already has one switch whose whole job
   is "show me the busier layout". That is the answer to UI rule 14's width problem, and
   it is a real one: KEDA declares **eleven** columns for a ScaledObject. Turning the
   switch re-evaluates cells over objects the rows already hold — no refetch, no watch
   restart, no lost selection, so it stays a display switch.
5. **A declared `Age` over `.metadata.creationTimestamp` is dropped**, because the
   list's own Age column *is* that column — recomputed live off the shared timer, with
   the exact timestamp as a tooltip. An `Age` pointing anywhere else is kept. Any other
   `type: date` column is re-rendered by that same timer (`PrinterColumns.DateValue`);
   without it a "Last run" or "Expires" cell would freeze until the next watch event.
6. **The generic Status and Details columns step aside when printer columns are
   present** — kubectl shows no generic status beside them, and doubling up costs width
   the list does not have. The 28px health dot stays: it is not one of kubectl's
   columns, and it is what still carries `ResourceStatusSummary`'s classification.
7. **In fleet mode the columns are the tab's own cluster's, and every row is evaluated
   against them.** A table can only have one set of headers, so they come from the
   cluster whose sidebar the kind was selected in; a member serving an older version
   with a different shape resolves to blank cells rather than to a wrong value — the
   same outcome an absent field already has.

Two implementation traps, both hit while building this:

- **The grid's printer columns are ten fixed slots declared in XAML, not columns built
  in code.** A `DataGridColumn` is outside the visual tree, so a code-built column needs
  a code-built binding — and a code-built binding is a *reflection* binding, which is
  exactly what NativeAOT will not ship. The cells are therefore
  `{Binding PrinterCells[3].Text}` compiled bindings against a fixed array of tiny
  observables (indexing an array is not itself observable, which is why each cell is an
  object rather than a string). Ten is above every real CRD surveyed; the surplus is
  dropped in declaration order.
- **A printer slot's header is a CRD author's string, and it collided.** Every
  `Apply*Columns` method used to find its columns by header text, and cert-manager calls
  one of its Certificate columns **Ready** — so the first cut renamed a slot to "Ready",
  `ApplySummaryColumns` matched it as the grid's own Ready column and hid it, and the
  CRD's most important column was silently missing from the very list this feature
  exists to fix. Only the screenshot showed it. Header matching was the wrong identifier
  and is **gone**: every column carries a `Tag` (`ResourceColumn`), the slots are
  addressed by the CRD column they currently draw, and `ClusterTabView.FixedColumns`
  still excludes them — see "The resource grid is the reader's to re-cut" below, which
  is also what made the header stop being a constant for the app's own columns.

The sandbox produces every one of these states — see `scripts/manifests/50-crds.yaml`
(the shop Widget's mixed types plus a priority-1 column and one path that resolves to
nothing, the demo Backup's condition filter and non-creationTimestamp date, and the
factory Widget deliberately declaring **none**, which is the degradation path).

## The resource grid is the reader's to re-cut

Every column in the resource list can be dragged to a new width, a header click sorts
by that column, and both are remembered per kind. `ResourceColumn` and
`ResourceRowComparer` (App layer) are the identity and the ordering, `GridLayoutStore`
+ `WorkspaceSettings.GridLayouts` are the memory, and `ClusterTabView`'s code-behind is
what connects them to the grid.

It is the answer to the 2026-08-19 audit's widest finding, and the reason it is an
answer rather than a re-cut of the numbers is arithmetic: the list has nine fixed
columns at 1280px, so widening one narrows another, and a CRD can declare eleven of its
own on top (KEDA's ScaledObject). Measured on the rendered fixture list, the Namespace
column held ~110px of the same value on every row while two pods of one ReplicaSet
ellipsised at exactly the character that told them apart. No single set of minimums
survives all of that; which column matters is a property of the question being asked,
which is why it belongs to whoever is asking.

Twelve things are load-bearing.

1. **Sorting orders `VisibleRows` and never `Rows`.** UI rule 13's invariant is that
   `Rows` is the informer's own list in arrival order, and everything that produces
   rows — the watch, the fleet merge, the demo dataset, the fixtures — writes to it
   knowing nothing about the projection. Sorting it in place would look right and break
   the informer underneath: the arrival order would be gone for good, so clearing the
   sort could never come back to it. `ClusterTabSortTests` pins that, and the break was
   written and confirmed red before the tests were called done.
2. **The DataGrid's own sorting is not used, and cannot be.** It orders the collection
   view behind `ItemsSource`, which is the list above. `ClusterTabView.OnGridSorting`
   sets `e.Handled = true` and hands the click to `ClusterTabViewModel.ToggleSort`.
3. **`CanUserSort="True"` on every column is what makes the click arrive at all.** These
   are `DataGridTemplateColumn`s with no `SortMemberPath`, and Avalonia's `ProcessSort`
   returns *before* raising `Sorting` for a column whose `CanUserSort` is false — which
   is the default for a template column. Measured, not inferred: a headless probe over a
   real grid saw zero `Sorting` events until the flag was set, and one per click after.
4. **A column is compared by what it means, not by the string it renders.** Restarts is
   a count (the cell carries "(43m ago)" with it, and "10" sorts above "9" as text), Age
   is an instant, CPU and memory are the latest sample's nanocores and bytes, Ready is
   the fraction "2/3" resolves to, and a CRD's `type: date` column is the instant behind
   the age it prints. **Ascending Age means the youngest first**, which is the opposite
   direction to the instants — the number people read is the age, not the timestamp.
5. **A row with no value sorts after the rows that have one** in ascending order, rather
   than as a zero or as an empty string above them. A pod that reports no CPU is not a
   pod using none, and a ConfigMap has no Ready to be worst at. The tie-break is the row
   key and is deliberately *not* reversed with the direction: it exists to make the
   order total, and a tie-break that flipped would make the list jump for reasons the
   sorted column cannot explain.
6. **The third click clears the sort.** Two states would leave no way back to arrival
   order, which on a live list is information — it is where the watch put a newly
   created object.
7. **The sort is maintained, not applied once.** A watch is a stream: an object created
   while the list is sorted is inserted where the sort puts it (binary search, so a tick
   on a 5000-row list costs a handful of comparisons), and a Modified that changes the
   sorted value moves the row — the CrashLoopBackOff case on a list sorted by Status.
   `RepositionRow` does nothing while the row is still between its neighbours, so an
   ordinary status refresh moves nothing under the pointer. A list that quietly stops
   being sorted seconds after it was sorted is worse than one that never was;
   that break was written and confirmed red too. The **metrics poll** is the one event
   that rewrites every row's sort key at once, and it re-orders the list *in place*
   (`ResortVisibleRows`, an insertion pass) rather than rebuilding it: a rebuild raises a
   Reset, a DataGrid answers a Reset by dropping the scroll position, and a CPU-sorted
   list that jumped back to the top every fifteen seconds would be useless for the one
   job a CPU sort has. That break was written and confirmed red as well.
8. **A CRD column is identified by the CRD author's name for it, never by slot.** The
   grid's printer columns are ten fixed positional slots, but the advanced view is this
   app's `-o wide`: turning it on brings a CRD's `priority: 1` columns into the list in
   declaration order, so every slot after the first of them draws a different column
   than it did a moment earlier. A width or a sort keyed by slot would silently move to
   a neighbour when that switch is flipped. A remembered sort by a column the kind no
   longer declares falls back to arrival order rather than shuffling the list against
   cells that are not there.
9. **A star column and an Auto column do not keep a drag in the same place**, which is
   why the stored width carries its unit. Measured on a real grid: dragging a `2*`
   column leaves it a star column and rewrites the ratio (`2*` → `2.52*`), while
   dragging an `Auto` column leaves its declared width alone and changes only what it
   displays. So a star column is remembered as its ratio — which reproduces the same
   proportional layout at any window width — and everything else as the pixels it ended
   at, restored as an absolute width. A column nobody has dragged is reset to the width
   the XAML declares, or a kind with no remembered layout would inherit the previous
   kind's.
10. **Only what actually changed during the drag is remembered.** There is no
   column-resize event in Avalonia — the DataGrid changes the width from inside the
   header's own pointer handling and tells nobody — so the gesture is bracketed instead:
   snapshot every column on pointer press, compare on release, store the difference.
   Storing every column instead would pin the Auto columns to whatever their content
   happened to measure at that moment, which is a choice nobody made.
11. **Widths and sort are one record with two writers, and neither may drop the other's
   half.** Pixels are not view-model state, so the view owns the widths; the sort is
   what orders the rows, so the view model owns it. Both go through
   `GridLayoutStore.Update`, which takes a function over the stored layout and does a
   read-modify-write **of the file** — never of a cached snapshot, exactly as
   `App.Update` does and for the same reason: the shell writes the same workspace (tabs,
   pins, environment overrides) from another path entirely.
12. **This is session state, so it is `workspace.json` and not `settings.json`.** The
   test is the one in "Settings, and what belongs in which file": deleting the workspace
   should lose how the window looked and nothing else, and a column width is exactly
   that. Keyed by `<group>/<Kind>` — not the version, because a cluster promoting a CRD
   from v1beta1 to v1 is still the same list to whoever widened its Name column, and not
   the cluster, so a width chosen for Pods holds everywhere.

**The sort indicator is drawn into the header text, and that is not laziness.** Fluent's
`:sortascending`/`:sortdescending` header pseudo-classes are set from the collection
view's sort descriptions, which stay empty here *because* the sorting is ours — so the
arrow has to be drawn rather than styled. A header template would mean a `Control` in
place of a string, which opts the header cell out of Fluent's own column-header template
(the same reason a printer column's `description` is not a header tooltip). The header
therefore stops being a constant, which is the other half of why column identity moved
to `Tag`.

**Two things this deliberately does not do.** The Helm release list is a second grid with
its own hardcoded columns and is untouched, so `FEAT-72` (its `Updated` column printing a
clipped absolute timestamp) is not subsumed by this. And nothing here adds *horizontal
scroll*: dragging redistributes the width the window has, so the fleet list's ten columns
at 1280px still clip their rightmost headers (`ENG-6`) — a reader can now trade one of
them away, which is a workaround and not the fix.

## Mutating workload actions (scale, rollout restart, delete)

The app was read-mostly until this: the only way to change a replica count was to edit
YAML, and "restart that deployment" — one click in Lens, Aptakube, k9s and Headlamp,
and the most common on-call GUI action there is — had no entry point at all.
`ClusterClient.Workloads.cs` and `WorkloadActions.cs` (Core) are the engine;
`RowActionViewModel` + the strip above the list (UI rule 17) are the surface; the row
`ContextFlyout` and the command palette are the two ways in, as the backlog item asked.

Six things are load-bearing:

1. **A restart is an annotation, not a delete loop.** `RestartWorkloadAsync` stamps
   `kubectl.kubernetes.io/restartedAt` on `spec.template.metadata.annotations` and stops
   — the controller then rolls its own pods under its own update strategy, honoring
   surge, `maxUnavailable`, partitions, PDBs and readiness gates. Deleting the pods
   ourselves would bypass every one of those and can take a whole Deployment down at
   once. The key is **kubectl's own**, deliberately: a restart from kubeNimbus and one
   from kubectl have to be the same event to whoever reads the object afterwards.
2. **Both patches are `application/merge-patch+json`, not strategic merge.** kubectl
   uses strategic merge for built-ins; a CRD answers that with a 415. For the nested
   scalar maps these two actions touch, RFC 7386 produces exactly the same object —
   merge patch recurses into objects and merges keys, so the template's labels,
   containers and other annotations all survive — and it is the one content type
   everything accepts. `WorkloadActionsTests` pins the byte-for-byte patch bodies,
   because **every failure mode here is silent**: a wrong annotation key, or a patch one
   level short (on the object's own metadata rather than the template's), is a 200 that
   rolls nothing and is indistinguishable from a dead button.
3. **Scale goes through the `scale` subresource**, like `kubectl scale`, and reads it
   before offering a number. The object's own `spec.replicas` is only the opening value
   of the box while that read is in flight — a CRD may declare a different
   `specReplicasPath`, and the subresource is the one field every scalable kind agrees
   on. The read failing (RBAC on the subresource) is stated in the strip and does not
   block the action.
4. **Capability comes from discovery and from the object, never from a list of kinds.**
   Scale is offered when the server declares a `scale` subresource for that kind
   (`WorkloadActions.SupportsScale`) — so an Argo Rollout is scalable on exactly the same
   evidence a Deployment is, and neither is named anywhere. Restart has *no* discovery
   signal at all (no subresource, no verb), so the honest test is the object: does it
   have a pod template to stamp (`HasPodTemplate`)? That is true of Deployments,
   StatefulSets, DaemonSets and a CRD that embeds a template, and false for a bare Pod —
   whose restart gesture is the delete. Delete is gated on the `delete` verb.
   In an aggregated fleet list the descriptor is the row's **own** cluster's, so the same
   CRD can be scalable on one cluster and not on another and the menu is right on both.
5. **Delete no longer detours through the YAML editor.** It used to open the object's
   manifest with that editor's confirm armed, which put an editor tab and a page of
   YAML between someone and a one-line question; it arms the same strip now, and names
   the object either way. The YAML editor keeps its own Delete for when you are already
   in there. "Confirm before deleting" is read **at the press** (same as
   `YamlEditorTabViewModel.RequestDeleteAsync`, same reason). Scale and restart do not
   consult it: it is a setting about deleting, and scale needs its input step regardless.
6. **The demo cluster arms the strip and refuses in place.** All three actions need an
   API server, so `RowActionViewModel.IsDemo` (`client is null`, as everywhere) renders
   the notice and disables Confirm — never a silent no-op. That is also why the demo
   catalog's Deployment/ReplicaSet/StatefulSet descriptors carry a `scale` subresource:
   without it the capability check would hide the action outright and the demo would
   teach that kubeNimbus cannot scale, rather than that this cluster cannot.
   `cluster-tab-demo-scale-unavailable` is that scenario, and it is the one of the four
   new screenshots that runs the real command path end to end — a fixture tab has no
   `Client`, so the commands correctly refuse there and the strip is built by hand
   (`ClusterTabScenarios.ArmRowAction`), exactly as the exec/YAML/Helm scenarios do.

**Not shipped, deliberately:** rollout *status*/history/undo, pause/resume, and scaling
from a row's inline editor. `kubectl rollout undo` needs ReplicaSet revision walking and
is its own item; the rest are backlog candidates, not omissions this pass forgot.

## Node operations (detail, cordon / uncordon, drain)

`ClusterClient.Nodes.cs` + `NodeActions.cs` + `NodeResources.cs` (Core) and
`NodeDetailTabViewModel` + `NodeDetailView` (App) are the node surface: what a node says
about itself, how much of it is already promised away, which pods are on it, and the
three actions that take it out of service and put it back. The read-only half was
half-present before this — `ResourceStatusSummary.SummarizeNode` already rendered
`Ready,SchedulingDisabled` and `IsMeteredKind` already covered `Node` — with no pane
behind it and no way to act on what it said.

Double-clicking a node opens the detail pane rather than its manifest (UI rule 2), and
the three actions land on FEAT-1's shared confirm strip (UI rule 17) from the row context
menu and the command palette. Nothing new is always visible.

### The read-only half

- **Allocatable, not capacity, is the denominator.** Capacity includes what
  `--system-reserved` and `--kube-reserved` hold back; a headroom figure computed against
  it overstates the free room by exactly that much, and it is the figure someone decides
  to drain on. Capacity is carried alongside so the gap is visible rather than lost.
- **Requested is the scheduler's own formula**, not a sum of every container:
  `max(sum(regular containers), max(init containers)) + spec.overhead`, with native
  sidecars (init containers whose `restartPolicy` is `Always`) counted into the running
  sum because they never exit. Summing init containers alongside the regular ones
  overstates any node running Jobs; ignoring them understates a node mid-startup. Both
  wrong answers are *plausible*, which is why `NodeResourcesTests` pins the formula in
  both directions. Terminal pods are excluded, as `kubectl describe node` excludes them.
- **Requested and the sum of the declared limits share one track, and the card says which
  part is which.** The "allocatable vs requested" card draws one `Controls/ResourceMeter`
  per resource — a hand-rolled `DrawingContext` control, same argument as `Sparkline`: the
  requested figure as the filled portion, the limits total as a lighter extent on the same
  axis with a 2px marker where it lands, and the absolute plus the percentage printed at
  the right of the row (`NodeResourceLineViewModel.LimitSummaryText`). Limits were computed
  by `NodeResources.Summarize` from the day the node pane shipped and were rendered
  nowhere, which is a strange thing to withhold: how far a node's limits oversubscribe it
  is one of the two questions the card exists to answer. It is **one track rather than two
  stacked bars** because the dock is ~300px (UI rule 10) — a second bar per resource
  triples the card's height to plot a second series on an axis that already carries it, and
  two bars are harder to compare than one, not easier. Four consequences worth keeping:
  - **The requested fill clamps at the track and the limit marker does not.** Limits past
    allocatable are ordinary overcommit — it is how most clusters are run — so a limit over
    100% pins its marker inside the track's right edge and draws it in the *warning* colour
    rather than in the marker colour, and the printed percentage goes warn with it. Silently
    clamping it would render an oversubscribed node as exactly full, which is a wrong answer
    stated confidently. `LimitPercentValue` is therefore deliberately unclamped; the meter
    is what decides how to draw a value past its end.
  - **The pods row has no limit, and renders as though the concept does not exist for it.**
    `Limit: null` is that line's normal case, not missing data, so there is no marker, no
    extent, no caption and no dangling separator. A row that silently differs in *shape*
    from the two above it is a bug; `NodeResourceLineTests` pins both halves.
  - **Overcommitted limits do not make the row read as tight.** `IsTight` stays
    `RequestedPercent > 90` — "the scheduler is nearly out of room", which is what a drain
    of the neighbouring node depends on — and overcommit gets its own flag on the limits
    figure alone. Colouring the whole row on it would say a normally run cluster is in
    trouble, which it is not until the pods actually use what they are allowed to.
  - **The marker takes the theme's high-contrast foreground, not a second accent.** Limits
    below requested is ordinary (the limits total sums only the containers that declare
    one), so the marker is routinely drawn *on top of* the accent fill, and accent-on-accent
    is invisible on the dark theme. The footnote under the card states all of this in one
    sentence, because a reader must not have to guess which part of the track is which.
- **A `*` column beside an `Auto` column of variable-width text gives every row a
  different bar length**, and that is what this card shipped with: the row was
  `ColumnDefinitions="70,*,Auto"` with the numbers in the `Auto` column, so the star column
  ended wherever each row's own text happened to stop. CPU, Memory and Pods print
  different-width figures, so the three tracks came out three different lengths — aligned
  at the left and ragged at the right — and bars of different lengths cannot be compared row
  to row, which is the whole job of a small-multiples chart. Every column but the track is a
  fixed width now. The trap generalizes to any `ItemsControl` whose rows mix a proportional
  visual with per-row text, and it is invisible from the code: it only shows up in a
  rendered screenshot, which is where it was reported from.
- **A condition's polarity is read off `Ready`**, the one condition Kubernetes defines as
  positive; everything else is a pressure condition, healthy when False. Reading it off a
  list of known-bad condition types instead would classify a cloud provider's or
  node-problem-detector's own condition as fine by default, which is the wrong way to be
  wrong.
- **Taints are shown even though the cordon flag is too**, because the scheduler enforces
  `spec.unschedulable` by way of the `node.kubernetes.io/unschedulable` taint. A cordoned
  node has both, and a reader shown only one of them wonders which is real.
- **"Pods on this node" is one field-selected list with an explicit Refresh**, not a
  second watch. `spec.nodeName=<node>` is server-side (the API server indexes it), and the
  precedent for a one-shot inside an inspector pane is pod detail's Events tab. The node
  object itself stays live: the pane tracks the same `ResourceRowViewModel` the list holds
  and re-reads conditions, taints and the cordon flag on every watch tick, the same way
  pod detail tracks its row.

### Cordon, and the one honest exception to "capability from discovery"

Cordon is a one-field merge patch of `spec.unschedulable`, structurally identical to
FEAT-1's `restartedAt` patch, and uncordon writes an explicit `false` rather than a JSON
`null` — a null would *remove* the field under RFC 7386, which means the same thing to the
scheduler and is not what `kubectl uncordon` leaves behind.

The capability check names the kind, and that is deliberate rather than a shortcut.
Scale has a discovery signal (a `scale` subresource) and restart has an object signal (a
pod template to stamp); cordon has **neither**. `spec.unschedulable` is a field of the core
`v1.Node` schema, discovery says nothing about it, and an uncordoned node omits the field
entirely — so "does the object have the field" answers false for exactly the nodes you
would want to cordon. `NodeActions.SupportsCordon` therefore tests the kind *and* asks
discovery the half it can answer: does this server say nodes are patchable. Drain adds the
signal there *is* one for — whether the server serves `pods/eviction` — so a cluster
without the Eviction API never sees the menu item at all.

Cordon and uncordon are two commands in **one menu slot**: the menu shows whichever the
node's current state makes meaningful. That is UI rule 11's "a control pair where one half
is always disabled is one control", settled by the port-forward pane's Start/Stop, and it
is why the menu still never shifts — exactly one of the pair is ever present.

### Drain: what it does, and what it refuses

There is no `k8s.io/kubectl/pkg/drain` to import here and `KubernetesClient.Aot` ships the
eviction primitive and no drain helper, so the loop is ours. Every *decision* it makes is
therefore pure and tested (`NodeActions.Plan`), and only the HTTP is in `ClusterClient`.
The classification is kubectl's own filter order, and each entry is here because skipping
it is a known way to break a cluster — the two marked below are the open silent-data-loss
bug in a comparable CNCF client ([headlamp#7268](https://github.com/kubernetes-sigs/headlamp/issues/7268)):

| Pod | What the drain does | Why |
|---|---|---|
| Already terminating | waited for, not evicted again | its 404 would read as a failure, and the node is not drained until it is gone |
| **Mirror (static) pod** | skipped, always | the kubelet owns it from a file on disk and recreates it seconds later |
| Succeeded / Failed | skipped | nothing is running; only a record is left |
| **DaemonSet-owned** | skipped, and named in the plan | its controller ignores cordon; `kubectl` requires `--ignore-daemonsets`, and a gate whose only possible answer is yes is worse than a sentence saying what was left behind |
| **No controller** | **refused** unless "Evict unmanaged pods" | nothing recreates it: draining destroys the workload (`kubectl --force`) |
| **`emptyDir` volume** | **refused** unless "Delete emptyDir data" | node-local storage with no copy anywhere, deleted with the pod (`kubectl --delete-emptydir-data`) |

Seven things are load-bearing:

1. **The plan is computed and shown before anything is evicted, and a plan with refusals
   does not run.** kubectl refuses the same way — it names every problem pod before it
   touches one. Half a drain that then stops on a question is worse than the question
   asked first. Ticking either option re-plans from the pod list already read, so the
   refusal it clears disappears in front of you rather than on confirm.
2. **Two options, not five.** `--ignore-daemonsets` has one possible answer and the plan
   states what it left behind instead; `--disable-eviction` bypasses PodDisruptionBudgets
   and this app will not offer that as a checkbox; `--timeout` is replaced by a drain you
   can watch and stop. What is left are the two that authorize destroying something which
   does not come back, and both are off by default.
3. **The drain streams.** `DrainNodeAsync` is an `IAsyncEnumerable<DrainProgress>` — one
   event per thing that happens — because its duration is not bounded by anything this app
   controls. A **429 from a PodDisruptionBudget is correct behaviour**, can last minutes or
   forever, and is indistinguishable from a hung window unless the pane says "blocked by a
   PodDisruptionBudget, still retrying". It gets its own per-pod row and its own colour
   (warn, not error). A 403 is separated from it deliberately: retrying will not fix that
   one, so it is recorded as failed and not asked again.
4. **The eviction loop polls, and that is a stated exception to hard rule 2.** It re-lists
   the node's pods every 2s between passes. The loop's question is "is this specific set of
   pods gone yet", which has a natural end (the set empties), it is scoped to the drain's
   own `CancellationToken`, and re-listing is also how it notices a pod that appeared
   *after* it started — a watch seeded once would not. It is what `kubectl drain`'s own
   `waitForDelete` does. This is the second documented poll in the app, after the metrics
   API.
5. **Cordon happens first, always.** Evicting from a node that still accepts work is a way
   to have the scheduler put the pod back on the same node.
6. **A partial drain is a designed state, not an accident — this is the constraint that
   cannot be engineered away.** The loop runs in the desktop app's own process: closing the
   tab or quitting stops it, leaving the node cordoned with some pods moved and some not.
   So (a) the confirm sentence says exactly that *before* anything starts, which is the one
   thing someone must know; (b) the strip cannot be dismissed while a drain runs — Cancel
   is replaced by **Stop draining**, because "Cancel" over a loop that is already evicting
   reads as undo and there is no undo; (c) stopping reports how many pods moved, that the
   node is still cordoned, and the two ways out (run it again, or uncordon and leave it as
   it is); and (d) `ClusterTabViewModel.DisposeAsync` cancels the loop explicitly rather
   than leaving a task running against a disposed client. `cluster-tab-node-drain-stopped`
   is that state rendered.
7. **One drain at a time, enforced by the single-slot strip.** `ArmRowAction` refuses to
   replace an action that is busy or draining: re-arming over a running loop would leave it
   evicting with nothing on screen reporting it. Portainer reached the same rule from the
   other direction in [portainer#4006](https://github.com/portainer/portainer/issues/4006)
   — a drain should be issued to one node at a time.

**Eviction is posted as `policy/v1`**, which the API server has served since 1.22 and which
`kubectl` itself sends; `policy/v1beta1` was removed in 1.25. A server too old for it
answers with its own message, which the strip prints verbatim rather than this app guessing
a second version to retry with. And because discovery gates the whole feature on
`pods/eviction` existing, a 404 from that endpoint can only be about the pod — which is why
it is read as "already gone", the outcome the caller wanted.

**The demo cluster plans for real and refuses to evict** (demo rules 4 and 5). The
classification is pure and the shipped dataset has pods on nodes, so the plan, the two
refusals and the whole strip render offline exactly as they would against a cluster; only
the eviction has no honest stand-in, and `RowActionViewModel.IsDemo` says so in place with
the confirm disabled. The demo catalog's Pod descriptor therefore declares an `eviction`
subresource for the same reason its Deployment declares `scale`: without it the demo would
teach that kubeNimbus cannot drain a node, rather than that *this* cluster cannot.

**Both states of the resource card are in the shipped data** (demo rule 4). `demo-worker-1`
is the ordinary shape — some pods declare limits, most do not, so the limits total lands
*below* requested and the marker sits inside the fill — and `demo-worker-2` is
overcommitted in both CPU and memory (109% of allocatable each), which is what renders the
warn marker pinned at the track's end. `redis-cache-0` and `notification-dispatcher` carry
the generous limits that produce it. The sandbox had nothing that oversubscribed a node
either, so `shop-api` in `scripts/manifests/10-shop.yaml` now declares a limit far above
its request; the scheduler never reads limits, so it schedules exactly as it did before.

**Two defects were found by looking at the rendered strip and are worth remembering.** A
compiled binding to a **method group** (`{Binding DrainPlan.Summary}` against
`string Summary()`) renders the delegate's type name — ``System.Func`1[System.String]`` — with
no error anywhere; `DrainPlan.Summary` is a property now. And `RowActionViewModel`'s target
sentence tested its namespace for `null` where `ResourceRowViewModel.Namespace` is a
non-nullable string, so every cluster-scoped object read "`Node/demo-worker-1 in `". That
second one is pre-existing and applied to deleting a PersistentVolume or a Namespace too.

**Not shipped, deliberately:** node shell (that is `kubectl debug node/`, a different
feature), `--disable-eviction`, a `--grace-period` control (the option exists in
`DrainOptions` and nothing sets it — a pod's shutdown window is a property of the app, not
of whoever is draining), multi-node drain, and node labels/taints editing. The YAML editor
already reaches all of the last one.

## The exec terminal

The exec pane renders a real VT emulator: `SvcSystems.UI.Terminal` (the Avalonia
control) over `XTerm.NET` (a headless port of xterm.js), both MIT. Before it, the pane
was a `TextBox` fed by a hand-written scrollback that *stripped* ANSI — which is fine
for `ls` and useless for everything people actually exec in for: `vi`, `top`, `mc`,
`less`, `htop`, a bash reverse-i-search. There is no addressable screen in a scrollback,
so a full-screen tool did not draw at all; it unspooled.

**The transport did not change and must not.** `ClusterClient.ExecAsync`'s WebSocket,
the channel-3 error read, the bash→sh→ash probe and its `Task.WhenAny` timeout are all
exactly as they were — see the exec bullets above and `ExecTabViewModel.ProbeAsync`'s
remarks, which are the record of two failures that cost a live debugging session each.
What changed is only what happens to the bytes at either end.

Seven things are load-bearing:

1. **Why this package, and not one of the alternatives.** The field was surveyed in
   [`docs/research/2026-08-15-terminal-libraries.md`](docs/research/2026-08-15-terminal-libraries.md):
   XtermSharp has no Avalonia renderer, VtNetCore's belongs to a dead IDE,
   `Iciclecreek.Avalonia.Terminal` bundles a PTY and is built around hosting a *local
   process*. This one's whole contract is `Feed(bytes)` in, a `UserInput` event
   carrying bytes out, `Resize(cols, rows)` — no PTY anywhere in it, which is the only
   shape that fits bytes arriving over a WebSocket from an API server. It renders with
   `DrawingContext` + `FormattedText`, the same argument as `Controls/Sparkline.cs`, and
   `grep` finds no reflection in either assembly. It is also written by KubeUI's author,
   i.e. by someone who hit this exact problem in this exact stack first.
2. **The view model owns bytes, not text.** `ExecTabViewModel` feeds decoded output in
   on a 50 ms `DispatcherTimer` tick (the same coalescing the old pane needed, and it
   matters *more* now — every feed rebuilds the viewport and invalidates the surface)
   and writes `UserInput`'s bytes straight to `StdIn`. Nothing in this app parses an
   escape sequence, encodes a key or strips a control code any more, and nothing should
   start again.
3. **Decoding is stateful, and that is not a detail.** A 4 KB socket read can end
   mid-character, and `Encoding.UTF8.GetString` per read turns that into U+FFFD
   *permanently*. Confirmed against the real engine in this pass: feeding the euro
   sign's three bytes as `GetString(b,0,1)` + `GetString(b,1,2)` renders `���`, while
   the same bytes through a retained `Decoder` render `€`. So `_decoder` is a field,
   not a local. (`TerminalControlModel.Feed(byte[])` has the per-call flaw internally,
   which is why the pane calls the `string` overload.)
4. **The keyboard belongs to the terminal while it has focus, on purpose.** The control
   marks Ctrl+&lt;letter&gt;, Tab, Esc and F1–F10 handled, so the window's own chords —
   the palette, the list filter, the cheat sheet — do not fire inside the exec pane.
   That is the trade command-catalog rule 5 already describes from the other side: `^C`
   has to reach the container, and an app that stole it would make the pane useless for
   the one thing it is for. Copy and Paste therefore move up one modifier to
   Ctrl+Shift+C/V, as they do in every terminal emulator, handled in a **Tunnel**
   handler in `ExecView` because the control's own bubble-phase mapping ignores Shift
   and would send a plain `^C`. Right-click opens a Copy/Paste/Select-all menu rather
   than `RightClickAction.CopyOrPaste`, whose paste-on-empty-selection is one stray
   click away from running the clipboard in someone's production container.
5. **The pane is one row of chrome now** (UI rule 10): status dot, status, shell box,
   reconnect — and the terminal. The input `TextBox`, its `^C`/`^D` chips and the
   Advanced-view-gated **Send** button are all gone, because a terminal that takes
   keystrokes makes a box you retype them into a row of dock height spent on nothing.
   That removes the exec pane from the Advanced view's list entirely; the F1 sheet is
   where those gestures are documented, and it now carries five exec rows rather than
   three.
6. **The palette is theme-independent, and that is a constraint rather than a taste.**
   `Styles/Theme.axaml` sets `SvcSystems.UI.TerminalColor{0,15}` (the app's own near-black
   and off-white) and lifts `{4,12}` because xterm's `#000080` blue on a dark background
   is unreadable and a colouring `ls` paints every directory with it. Everything else is
   the stock xterm-256 table, which is what a script's colours are written against. It
   does **not** follow the light/dark switch: the control caches a resolved foreground
   brush inside each `FormattedText` and only clears that cache on a font change, so a
   live theme swap would repaint the cell backgrounds and leave every glyph the old
   colour. One dark terminal in both themes beats a half-swapped one.
7. **A blank terminal is a state, not a pane** (UI rule 9). Until the first byte lands,
   `IsStatusOverlayVisible` covers the black rectangle with the status — "Connecting…",
   "No usable shell in app — tried /bin/bash, /bin/sh, /bin/ash", "Session ended" —
   and then gets out of the way, because after that the scrollback is worth more and the
   chrome row carries the state anyway. The demo cluster is unchanged: no `ClusterClient`,
   so `Border.demoUnavailable` and nothing else.

**A defect in the dependency, found here and not fixed here.** Reverse video with
*default* colours does not invert. `TerminalControlModel.CreateStyleKey` swaps the
foreground and background when `IsInverse()`, but the swapped values are the sentinels
256/257 ("default fg"/"default bg"), and `TerminalControl.ResolveColorBrush` resolves
either sentinel by the `isForeground` flag alone — so both halves land back where they
started and `ESC[7m` renders as ordinary text. Measured, not inferred: on defaults it
resolves to `fg=palette[15] bg=palette[0]`, which is exactly what un-inverted text
resolves to, while the same `ESC[7m` after an explicit `ESC[37;40m` resolves to
`fg=palette[0] bg=palette[7]` and does invert. So it is `top`'s header, `less`'s prompt,
vim's status line and mc's menu bar that render unhighlighted — the whole default-colour
case — and `cluster-tab-exec-fullscreen` shows it: the fixture emits the `ESC[7m` real
`top` emits, deliberately, so the screenshot tells the truth and starts drawing a band by
itself the day this is fixed. There is no app-side hook (`ResolveColorBrush` is private
and the render surface is a private nested class), so the fix is upstream or in a
vendored copy.

**If it goes unmaintained** — v1.1.0, one maintainer, ~35 stars — the fallback is
vendoring, and it is a real one rather than a comforting sentence: MIT, ~2 850 lines
across ten files, with the emulation proper in XTerm.NET underneath.
`shared/nimbusUi` is where it would go, since "a terminal control" can be described
without naming Kubernetes (the membership test), and pgNimbus would then have one too.
Do **not** vendor it pre-emptively: the copy stops receiving fixes the day it is made.

## The machine's own terminal ("open a terminal on this cluster")

`TerminalLauncher.cs` (Core) starts the user's own terminal with `KUBECONFIG` set and
the current context pinned to one cluster — the daily gesture people leave a GUI for,
and the one thing this app had no answer to at all. It is deliberately **not** a shell
inside the app: that needs a PTY dependency (`Porta.Pty` and its `Vanara.PInvoke` tail,
the only place this repo would ever need one), and it still would not be *your* terminal,
with your prompt, your fonts, your fzf and your kubectl plugins.

Six things are load-bearing:

1. **The context is pinned through a one-key overlay kubeconfig, and that is the whole
   design.** kubectl has no environment variable for "current context" — kubectx and
   friends work by *rewriting the file*, which this app must not do (someone's other
   terminals, their shell prompt and their next kubeNimbus session would all silently
   move with it). What kubectl does have is `KUBECONFIG` merging, and `current-context`
   comes from the **first file in the chain that sets one**. So the launcher writes
   `apiVersion` + `kind` + `current-context` and nothing else, and sets
   `KUBECONFIG=<overlay><sep><the real file>`. The real file is merged in unchanged and
   never written to by kubeNimbus. And because it is `KUBECONFIG` rather than a shell
   alias, helm, k9s, stern and kubectx all agree with kubectl about which cluster this
   is. The "real file" is the single file the context was found in
   (`ClusterContext.KubeconfigPath`) — the same one `Kubeconfig.BuildClientConfig` hands
   the in-app client, so the terminal and the tab that opened it cannot resolve a
   duplicate context name differently.
2. **Paths only, never credentials** (hard rule 4). The overlay holds a context *name*;
   there is no cluster block, no user block and therefore no token, certificate or
   exec-plugin invocation anywhere near it. `TerminalLauncherTests` asserts that
   negatively, because "we accidentally started copying kubeconfigs" is the failure that
   would never announce itself.
3. **One overlay per context, never one shared file.** `~/.config/kubeNimbus/terminal/
   context-<hash of the name>.kubeconfig`. Hashed rather than sanitized because real
   context names are ARNs and URLs (`arn:aws:eks:…:cluster/x`) and any sanitizer that
   made those into filenames would map two clusters onto one file — at which point
   opening a second terminal silently re-points the first one's next command at the
   wrong cluster, which is precisely the incident the environment colours exist for.
4. **The env-inheritance trap is why two of the three platforms do not use the obvious
   command.** Both `wt.exe` and `open` hand the request to *another process* that then
   spawns the shell — Windows Terminal's monarch/peasant model makes the new tab inside
   an already-running window, and `open` goes through LaunchServices — so the shell
   inherits **that** process's environment and not ours. A tab that looks right and is
   aimed at the wrong cluster is the one outcome this feature must not have. So:
   **Windows** starts `pwsh.exe` → `powershell.exe` → `cmd.exe` directly with the
   environment on the `ProcessStartInfo`, which still lands inside Windows Terminal
   wherever it is the default terminal application (a console-host setting, not a
   command line) and inside conhost where it is not — i.e. the item's stated fallback,
   reached by a different route. **macOS** writes a `.command` launcher script that
   exports `KUBECONFIG` and `exec "$SHELL" -l`, and opens *that* with
   `open -a Terminal`. **Linux** is the only one where the obvious thing is also the
   correct thing: `$TERMINAL`, then `xdg-terminal-exec`, then `x-terminal-emulator`,
   then the emulators, each started with **no arguments** (which every one of them reads
   as "open my default shell", and which is the only form needing no per-emulator flag
   table) and inheriting the environment normally.
5. **A missing `kubectl` warns; it never blocks.** Three reasons, and the third is the
   strongest: the terminal is useful without it (`KUBECONFIG` is what helm, k9s, stern
   and kubectx read too); kubectl may be installed a minute later; and **our PATH is not
   the terminal's PATH** — a GUI launched from Explorer, the Dock or the Store inherits
   a minimal environment, the same reason `$KUBECONFIG` never reaches it, so a probe
   miss is weak evidence about the shell that is about to open. The probe therefore also
   looks in the login-shell directories (`/usr/local/bin`, `/opt/homebrew/bin`, …), and
   the message says the PATH may be shorter here than in your shell rather than
   asserting kubectl is absent.
6. **Every outcome lands in one dismissible `infoBar` above the list**
   (`ClusterTabViewModel.TerminalNotice`, UI rules 9 and 11), and it exists because this
   command's own feedback — a window — **opens in front of the app**. Success, opened-
   without-kubectl, nothing-could-be-opened and the demo refusal all land there;
   `DescribeTerminalLaunch` is a public static so both the tests and the screenshot
   harness render the app's real words rather than a paraphrase. The no-terminal case
   prints the exact `KUBECONFIG` value in selectable text, because that is what makes
   the gesture completable by hand. Two entry points and no new always-visible control
   (UI rules 1 and 15): the ☰ menu and a Ctrl/Cmd+K entry.

**The demo cluster refuses in place**, rather than being palette-gated the way the
access review is. The difference is that this one has an honest sentence to say — its
objects ship inside the binary, so there is no kubeconfig to point anything at — where
the access review has nothing at all; and a terminal pointed at the sentinel `<demo>`
path is exactly the "never a silent no-op" the demo section's rule 5 forbids.

**Known risks, none of them verified here** (no Windows box, no macOS box, no desktop
terminal emulator in this container — see the pass note in Current status):

- A client/server emulator that does *not* forward its client's environment would open a
  terminal with no `KUBECONFIG` at all. gnome-terminal does forward it (its client sends
  `environ` over D-Bus precisely for this), which is why it is on the list, but the
  general case is emulator-specific. The success notice printing the value is the
  mitigation.
- macOS's `exec "$SHELL" -l` re-runs the login profile, so a profile that exports
  `KUBECONFIG` itself wins over the launcher script. Unavoidable without giving up the
  login shell; stated here so it is not re-diagnosed from scratch.
- **`FEAT-17` ("open this exec session in my terminal") is the seam left, not built.**
  It wants `kubectl exec -it` as the command the terminal runs, which is a *fourth*
  per-platform argument shape on top of the three above — do not bolt it onto the
  no-arguments Linux path without re-reading rule 4 above.

## The apply preview (server-side dry run)

Apply used to be blind: the editor sent the document and reported what came back.
`ClusterClient.PreviewApplyAsync` + `ResourceDiff.cs` + `TextDiff.cs` (Core) and the panel
under the editor (`ApplyPreviewViewModel`, UI rules 9 and 17) turn it into a two-step
action — ask the server what it would do, then decide. The step is a preference
(`AppSettings.PreviewApplies`, on by default), read **at the press** like the delete
confirm and for the same reason.

The panel shows the **manifest itself, with its changed lines in place** — `kubectl
diff`'s shape, `git diff`'s shape, VS Code's diff editor's shape. It shipped as a list of
field paths (`spec.template.spec.containers[worker].image`, old above new), which is
precise, compact, and not how anyone reads a manifest change; the field list is still
there as the third view mode, because it is the only one of the three that knows a
container was *inserted* rather than every container rewritten.

Twelve things are load-bearing.

1. **Both sides of the diff come from the server.** The live object is a GET; the other
   side is the `dryRun=All` response. That is the entire difference between this and
   diffing the editor's text against the object: the dry-run body has been through
   defaulting, admission webhooks and every mutating controller in the chain, so a field
   the cluster is going to add or rewrite is *in the diff*, and cannot be in a local one.
   It is also why the preview is worth a round trip rather than being computed offline.
2. **`dryRun=All` is the only value, and it is kubectl's own.** The API server runs every
   admission stage and the whole validation chain and then discards instead of
   persisting. A validating webhook's refusal, a schema violation and an RBAC 403
   therefore all arrive here having changed nothing, which is the point — `PreviewCoreAsync`
   prints the server's own sentence rather than a paraphrase.
3. **A 409 conflict during the preview is an answer, not a failure.** It raises the same
   `ServerSideApplyConflictException` a real apply does, so the conflict panel and its
   force-apply appear *before* the object moves. Force-apply is previewed too, and that
   is the case where a preview earns the most: what it changes is precisely the fields
   somebody else is managing. The confirm button says `Force apply` rather than
   `Apply changes` — the more consequential of the two applies must not be confirmed
   under the same word.
4. **Three fields are excluded and counted, not shown.** `metadata.managedFields` is the
   apply's own bookkeeping and changes on every apply including one that changes nothing
   else; `resourceVersion` and `generation` are the server's counters. Leaving them in is
   what makes `kubectl diff` hard to read. The count is printed (`1 server bookkeeping
   field hidden (…)`) because what a diff withholds has to be said out loud — a panel
   that is quietly incomplete is the exact failure this feature exists to prevent one
   level up.
5. **Lists are matched by `name` where every element on both sides has a unique one.**
   Containers, ports, env vars, volumes and volumeMounts all have that shape and it is
   Kubernetes' own merge key for them. Without it, inserting one container at the front
   reports *every* container as changed, which is the loudest noise source in a real
   deployment diff. Duplicate names, unnamed objects and scalars fall back to index
   pairing, because a wrong pairing invents changes. A pure reordering is reported as its
   own line naming both sequences: it changes no element, and for `env` it is semantic.
6. **The preview describes one exact document and dies with it.** Editing the text,
   reloading, or applying clears it. A stale diff above a live editor is worse than no
   diff — it is a wrong answer wearing the server's authority. `YamlEditorPreviewTests`
   pins that, and the break was written and confirmed red before the test was called done.
7. **The panel shares the dock with the editor, from code-behind, and the diff gets three
   quarters of it.** The preview's row is star-sized while a diff is open and `Auto` when
   it is not (or when the diff is empty, which is one sentence and two buttons). Both of
   the obvious XAML answers were tried and rendered wrong at the dock's default ~300px: an
   `Auto` row tall enough to read left a zero-height editor, and a `MinHeight` on the
   editor pushed the grid past the dock and overlapped its own rows. Mutating a
   `RowDefinition` is what `ClusterTabView.ApplyDockState` already does, for the same
   reason. The **3:1 weight is the line diff's own finding**: an even split left the
   panel's chrome row and footnote consuming its entire share, so the diff body rendered
   at *zero* height while the editor kept five lines nobody was reading — and the editor
   cannot be anything but context here, because typing in it discards the preview by rule
   6 below. Even at 3:1 the default dock shows about three lines and scrolls; the dock's
   own maximize toggle sits directly above the panel and is what a long diff wants. The
   panel itself is a `ContentControl` + inline `DataTemplate`, never a `Border` with both
   `DataContext` and `x:DataType` — UI rule 17 records what that pair renders, which is
   nothing at all, silently.
8. **The line diff is over the two server documents, not over the editor's text.** Rule 1
   is what makes the preview worth a round trip, and rendering it as text must not quietly
   give that up: `ApplyPreview` carries the live object as well as the previewed one, and
   each side is serialized by `ResourceDiff.ToDiffableYaml` — the object as YAML with the
   same three bookkeeping fields removed. `managedFields` alone is routinely a third of a
   real object and changes on every apply, so a text diff over the raw documents would
   open on the one section nobody wants to read. What was removed is still counted and
   stated by rule 4's footnote, which is the half that keeps the omission honest.
9. **`TextDiff` is in Core, pure, and bounded on purpose.** Common prefix and suffix are
   trimmed first — two serializations of nearly the same object share almost everything —
   and only the middle goes through an LCS. The table is `(n+1) × (m+1)` ints, so it is
   capped at a million cells; past that the middle is reported as one removal followed by
   one insertion and `IsApproximate` is set, which the panel *states* in the footnote.
   A diff that silently stops aligning is worse than one that admits it.
   **The trimming hides the LCS from most tests, and that is worth knowing before writing
   one**: an insert, a delete and a replace in an otherwise identical document are all
   settled by the prefix/suffix trim alone, so replacing the LCS with index pairing left
   the whole suite green. What catches it is a change at the top *and* at the bottom with
   an insert between them — a manifest with an edited label and an edited replica count,
   i.e. the ordinary case — and that is what
   `An_insert_between_two_changed_ends_leaves_the_middle_untouched` is for.
10. **Collapsing is part of the feature.** Three lines of context around each changed run,
   the skipped count stated (`10 unchanged lines`), because a Deployment serializes to ~60
   lines and a CRD to several hundred and the panel is a ~300px dock. A run of one line is
   kept rather than collapsed — a "1 unchanged line" separator is the same height as the
   line it replaces and says less. A diff with *nothing* in it produces no rows at all
   rather than one gap covering the document: the first cut rendered `56 unchanged lines`
   under "this apply would change nothing", which only the screenshot showed.
11. **Side by side is derived from the same row list, never computed a second way.** A
   removed run and the added run following it are zipped, and the shorter side gets
   fillers — the blank a deleted line has to face is the whole reason this mode is more
   than a two-column layout. Two independently built layouts can disagree about what
   changed, which is the one thing a diff may not do. Long lines are trimmed with the full
   text on the tooltip rather than wrapped: wrapping one side pushes it out of step with
   the other.
12. **The view mode is a session-scoped toggle on the tab, and no syntax highlighting.**
   `Diff / Split / Fields` is a `ListBox.segmented` sharing the panel's one chrome row (UI
   rule 10), and `YamlEditorTabViewModel.PreviewViewMode` holds it, so choosing side by
   side once survives the next apply. It is deliberately not a preference — a view toggle
   inside a pane, like the log pane's timestamps and wrap toggles. Highlighting the diff
   would mean an AvaloniaEdit instance per side and a colouring pass, which is a different
   feature; monospace plus a tinted line background is what VS Code's own inline diff
   leans on anyway, and the marker glyph carries the direction for anyone who cannot
   separate the two tints.

**The demo cluster is unchanged and needs no new refusal:** there is no `ClusterClient`,
so Apply, force-apply and the preview are all already disabled by `CanExecute` under the
editor's existing demo notice (demo rule 5).

**What is not built, deliberately:** syntax highlighting inside the diff (rule 12),
word-level highlighting within a changed line, a copy-the-diff button, a preview for the
row list's own scale/restart/delete actions (those already arm a strip that names exactly
what they do), and `--dry-run=client`, which answers a question nobody has: the client's
copy of the manifest is the text on screen.

**One known gap, and it is somebody else's row rather than an oversight.** The apply
still sends no `fieldValidation`, so the API server's default `Warn` mode silently
*prunes* a misspelled or unknown field and reports it only in a response header nothing
reads. A preview built on that shows a clean diff for exactly that typo — the edit simply
is not in the dry-run body — which is a quieter failure than no preview at all. That is
`FEAT-41` in `docs/BACKLOG.md` (one query parameter plus a graceful fallback for
pre-1.27 servers), and shipping this pass raises its value rather than lowering it.

## Metrics (metrics.k8s.io)

`ClusterClient.Metrics.cs` reads the aggregated metrics API for pod (per
container) and node usage. Three things are deliberate:

- **The API version comes from discovery**, not a hardcoded `v1beta1` — same
  rule as everywhere else: nothing about the server's API surface is assumed.
- **Absence is a first-class outcome.** No metrics-server (group missing) and a
  registered-but-dead metrics API (503/404) both raise
  `MetricsUnavailableException`; the UI hides the CPU/Memory columns instead of
  showing an error or a column full of dashes.
- **This is the one thing the app polls** (15s). The metrics API is a
  point-in-time aggregate over a ~30s window with no watch endpoint, so there is
  nothing to stream; polling is scoped to the current list's `CancellationToken`
  so it dies with the watch when the kind/namespace changes.

Quantity strings (`"100m"`, `"128Mi"`, `"12345n"`, `"129e6"`) are parsed by
`Quantity.cs` — a small AOT-safe reader, since `ResourceQuantity` from the k8s
client only covers typed models and metrics/CRD objects arrive as raw JSON.
The CPU/Memory `DataGridColumn`s are shown/hidden from `ClusterTabView`
code-behind: a `DataGridColumn` isn't in the visual tree, so it never inherits
the DataContext and cannot bind its `IsVisible`.

### Usage over time (graphs)

A single usage number can't tell a spike from a steady state, so every polled
sample also lands in a rolling window and gets drawn:

- **`UsageHistory` (Core)** is a fixed-capacity ring of
  `UsageSample(At, CpuNanocores, MemoryBytes)` — 120 samples, i.e. 30 min at the
  15s cadence. It lives in Core because it's engine state with no UI dependency
  (a CLI would want the same window), and it is **deliberately bounded and never
  persisted**: `metrics.k8s.io` has no history endpoint, so anything shown
  over time is only what this session observed. A cluster-wide time series is
  Prometheus's job, not kubeNimbus's — do not grow this into a store.
- **A missing reading is recorded as a gap, not a zero.** `ResourceRowViewModel
  .ClearUsage()` appends an all-null sample, and `Sparkline` breaks the line
  across nulls: a pod that stopped reporting must not read as a pod that went
  idle. `UsageHistoryTests` pins both that and the ring's wrap-around, because
  either bug draws a plausible-looking but wrong chart.
- **`Controls/Sparkline.cs`** is a hand-rolled `Control` (area + polyline via
  `DrawingContext`/`StreamGeometry`, auto-scaled to the series peak with 12%
  headroom). Hand-rolled on purpose: the Avalonia charting packages bring
  reflection-based binding/theming, which NativeAOT is exactly what this repo
  can't accept. No reflection, no templates.
- Series are re-published as fresh arrays per poll — a ring buffer mutated in
  place raises no change notification, and 120 doubles is cheaper than any
  observable-collection plumbing.
- Where it shows: a sparkline beside the number in the list's CPU/Memory cells,
  and pod detail's **Usage** tab (whole-pod CPU and memory charts plus a
  per-container pair). The tab is appended *after* Events so the existing
  `SelectedDetailTabIndex` values (Logs=0, Env=1, Events=2) stay stable.
- The Usage tab distinguishes its three states explicitly (UI rule 9):
  no metrics API on this cluster / samples not collected yet / charts. The
  first two look identical otherwise and lead to very different next steps.

## Helm release browsing (read-only)

`ClusterClient.Helm.cs` reads Helm 3 releases **straight off the cluster** — no
Helm binary, nothing shelled out. Helm stores each revision in a Secret of type
`helm.sh/release.v1`, whose `release` value is base64(gzip(JSON)) with
Kubernetes' own base64 on top: reading one means undoing two base64 layers and a
gzip (`TryReadReleaseRecord`). A record that doesn't unwrap is skipped, never
thrown — one broken release must not take out the list. The encoding is pinned
by `HelmReleaseTests` (no cluster needed), because getting a layer wrong fails
silently as "no releases".

In the App layer the Helm entry is a **synthetic sidebar kind**
(`SidebarGrouping.HelmReleaseDescriptor`, group `helm.sh` — no server serves
that, so it can't collide with a discovered kind). Selecting it stops the watch
and swaps the content area to the release list (`ClusterTabViewModel.IsHelmView`)
rather than starting a watch, since releases aren't an API kind. The section is
added at connect time **only when the cluster actually stores releases** (UI rule
1) — a release installed later in the session appears after a reconnect. Opening
a release docks a tab with its values, rendered manifest, notes and revision
history; double-clicking a history row loads that revision. Everything is
read-only: install/upgrade/rollback stays Helm's job.

## Argo CD (GitOps in the navigator)

`ArgoCd.cs` + `ClusterClient.ArgoCd.cs` (Core), an `Argo` sidebar section holding a
GitOps dashboard above that cluster's own Argo kinds, an Application detail pane, and
Sync / Refresh on the shared confirm strip. It is the feature Lens shipped in
2026.8 — "Argo CD in the navigator, detected automatically from the cluster, with
Sync and Refresh" — with two differences that are the point of doing it here: it is
**free** (Lens gates Argo CD behind Plus/Pro/Enterprise) and it is **quiet** (no
telemetry, and no second credential).

**The whole integration is the Kubernetes API.** Argo CD keeps Applications,
ApplicationSets and AppProjects in etcd as ordinary custom resources, so reading them
is the generic list path and both actions are merge patches of those objects that
Argo's own controller watches for. No Argo API server, no URL to paste, no `argocd`
binary, no second set of credentials — which is what makes this compatible with hard
rule 4 at all. It also means an Argo CD reachable only from inside the cluster
(the default install exposes no ingress) is fully usable from here.

Nine things are load-bearing.

1. **A sync is the object's top-level `operation` field, not a call to anything.**
   `ArgoCd.SyncPatch` writes `operation.initiatedBy` / `operation.info` /
   `operation.sync`, which is exactly what Argo's own API server writes when somebody
   presses Sync in its UI; the application controller watches for a non-null
   `operation`, runs it, and moves the outcome into `status.operationState`. A refresh
   is `argocd.argoproj.io/refresh: normal|hard`, the annotation `argocd app get
   --refresh` sets. Both are pinned byte-for-byte by `ArgoCdTests` for the same reason
   the workload patches are: **every failure here is silent**. A patch into `spec`
   instead of `operation`, or a misspelled annotation key, is a 200 from the API server
   that no controller acts on, and from the UI that is a dead button.
2. **No revision is pinned in the sync**, deliberately. Omitting it makes Argo sync to
   the Application's own `spec.source.targetRevision`, which is what the Application
   says it wants and what "Sync" has to mean. Writing one would quietly turn the action
   into "deploy something else", and it would do so without any visible difference.
3. **Both actions report a *request*, never a result.** The API server accepting the
   patch means Argo has been asked; what it then did lands in `status.operationState`
   seconds later and reaches the list through the ordinary watch. The strip says "Sync
   requested", and a refresh says out loud that Argo *clears the annotation* once it has
   re-compared, so there is deliberately nothing left on the object to look at.
4. **Sync and health are two independent states and get two pills, never one column.**
   An Application can be Synced and Degraded (Git applied cleanly, the pods crash) or
   OutOfSync and Healthy (someone changed the cluster by hand and what they left works),
   and one Status column has to pick one of the two and be wrong about the other. Where
   a single answer *is* needed — the attention ordering, `AttentionReason` — **health
   outranks sync**, which is Lens's rule and the right one: the pods are down either
   way, and reporting "out of sync" sends somebody to Git when the problem is in the
   cluster. `Progressing` is deliberately not an attention state: a rollout in flight is
   the system working, and a dashboard that flagged every deploy would be flagging
   nothing.
5. **The capability check names the kind, and that is the third honest exception**
   after `NodeActions.SupportsCordon`. Scale has a discovery signal (a `scale`
   subresource) and restart has an object signal (a pod template); a sync has neither —
   `operation` is a field of Argo's schema, discovery says nothing about it, and an
   Application that has never been synced does not carry it, so "does the object have
   the field" answers false for exactly the Applications you would want to sync.
   `ArgoCd.SupportsSync` therefore tests the kind *and* asks discovery the half it can
   answer: does this server say Applications are patchable. The **version is never
   assumed** (`ApplicationDescriptor` finds the kind in the catalog at whatever version
   the server serves), same rule as the metrics API's.
6. **The dashboard is cluster-wide and the namespace picker is disabled, by descriptor.**
   Applications live in one namespace (`argocd`) while everything they manage is spread
   across the rest, so a dashboard that followed the picker would be empty everywhere
   except the one place nobody browses. `ArgoDashboardDescriptor` is declared
   `Namespaced: false` and the picker's existing `IsEnabled` binding does the rest —
   no new binding, and no control offering a choice that changes nothing.
7. **The section is by API group; the dashboard row is by kind.** `argoproj.io` buckets
   into `Argo` through the same group rule every other section uses, so Rollouts and
   Workflows land there too — which is why the section is "Argo" and the row inside it
   is "Argo CD". The row itself is gated on the *Application kind* existing, because a
   cluster running only Rollouts has an Argo section with no Argo CD in it.
8. **The detail pane is read-only and the actions stay on the list.** Sync and Refresh
   arm the strip above the list (UI rule 17), and an action fired from inside a dock tab
   would arm a strip a maximized inspector is covering. The pane's own Reload button is a
   different thing and says so: it re-reads the object, where Refresh asks Argo to
   re-compare against Git. Managed resources carry the chevron owner chips already use,
   so the Deployment Argo calls Degraded is one click from its own manifest.
9. **Prune gets the drain's treatment.** It is the half of a sync that *deletes* —
   resources that have left Git go with it — so it is a checkbox on the strip, off by
   default, in words rather than in a menu. Terminating a running sync is **not** shipped:
   it means writing `status.operationState.phase`, which is a status-subresource patch and
   its own item.

**One rendering trap, found by looking at the screenshot.** Maximizing the inspector
works by setting the list row's height to 0, and a `Grid` does not clip its children —
the resource list and the Helm browser get away with it because a `DataGrid` clips
itself, but the dashboard's summary card is an ordinary `Border` and went on painting
straight through the maximized dock. `ClipToBounds="True"` on that row's grid is the fix,
and the trap applies to any future content in that slot that is not a `DataGrid`.

**The demo cluster runs all of it** (demo rules 4 and 5): seven Applications ship in
`Demo/Fixtures/argo-applications.json`, read through the same `ArgoCd.ReadApplication` a
live cluster's list goes through, covering every state the dashboard classifies —
including the two that are easy to get wrong, Synced-but-Degraded and an Application Argo
cannot compare at all (unreachable repository, no resources, a `ComparisonError`
condition). Only the sync and refresh requests have no honest offline stand-in, and
`RowActionViewModel.IsDemo` says so in place with the confirm disabled. **The sandbox
gained a shape rather than an installation**: `scripts/manifests/70-argocd-crds.yaml`
declares a stand-in Application CRD with Argo's own group, kind, version and printer
columns, and `71-argocd-applications.yaml` five Applications in the same states. It
deliberately omits the `status` subresource real Argo declares — with it on, `kubectl
apply` silently drops every `status` block and all five would come back Unknown/Unknown,
which is one state, not five. Delete both CRDs before installing real Argo CD; they claim
the same names.

## RBAC access review

`ClusterClient.Rbac.cs` and `ClusterClient.WhoCan.cs` answer three different
questions three different ways, and the split matters:

- **"What may I do here?"** goes to the API server's own
  `SelfSubjectRulesReview`. Never re-implement RBAC evaluation locally — a local
  evaluator silently disagrees with the server as soon as webhook authorizers,
  aggregation or impersonation are in play. When the server reports
  `incomplete`, the UI says so; a permissions list quietly missing entries is
  worse than no list.
- **"Where does this subject's access come from?"** has no server endpoint, so
  it's assembled from (Cluster)RoleBindings whose subjects match, each binding's
  role resolved to its rules. That's provenance, not an authorization decision —
  and a binding whose role is gone is still listed, since a dangling binding is
  exactly what you open this view to find.

- **"Who can do X?"** (`ClusterClient.WhoCan.cs`) is the cluster-wide direction,
  and it is the one question Kubernetes serves *no* endpoint for:
  `SelfSubjectRulesReview` only answers for the caller, `SubjectAccessReview`
  only for a subject you already named — neither enumerates subjects. So
  `WhoCanAsync` scans the RBAC objects and matches their rules, `kubectl-who-can`
  style. Four things are deliberate:
  - **It is provenance, not an authorization decision**, and the UI says so
    in-panel. A local scan cannot see webhook/node authorizers or impersonation,
    so it can both miss access and list access another authorizer denies. The
    honest counterpart is per-subject: `CheckAccessAsync` posts a real
    `SubjectAccessReview`, and the Verify button on each row is what turns a
    scanned row into the server's own verdict (which is also why the row shows a
    "denied" that contradicts the scan rather than hiding it).
  - **Rule matching mirrors the API server's** (`pkg/registry/rbac/validation`):
    verb/group/resource each match exactly or via `*`, the resource compared as
    the combined `resource/subresource`. RBAC has **no partial wildcards** —
    `pods/*` is a literal that matches nothing, and a rule for `pods` does not
    cover `pods/log`. `WhoCanMatchingTests` pins every one of those, because a
    glob implementation here would invent access that doesn't exist.
  - **A cluster-scoped query never consults RoleBindings.** A RoleBinding
    confines even a ClusterRole to its namespace, where a cluster-scoped object
    does not exist; the namespaced/cluster-scoped flag comes from *discovery*
    (`AccessQuery.ClusterScopedResource`), never guessed.
  - **A rule narrowed by `resourceNames` is kept, with the names shown.** It
    genuinely grants the verb — on those objects — so dropping it hides real
    access, and showing it unqualified overstates it.
  Rules that could not be read (403 on Roles, say) become warnings on the result:
  `WhoCanResult.IsPartial`, surfaced inline. A short list that doesn't say it's
  short is the failure mode this whole surface exists to avoid.

Entry points are command-palette only (UI rule 1): "Access review — my
permissions" always, "Access review — who can do X?" (opens the same tab
straight onto its Who-can section via `RbacTabViewModel.WhoCanTabIndex`), plus a
subject review when the selected row is a ServiceAccount (the only RBAC subject
that exists as an object — Users and Groups are just strings inside a binding).
The pane's three sections are independent: a failed `SelfSubjectRulesReview`
renders its error *inside* "My permissions" rather than blanking the TabControl,
since the other two directions don't use that call at all.

## Multi-cluster aggregated (fleet) views

`ClusterFleet.cs` + `AsyncMerge.cs` (Core) fan one resource query out across every
connected cluster and interleave the results. Four things are load-bearing:

- **Each cluster resolves its own descriptor.** `ClusterFleet.ResolveAsync` looks
  the requested `(group, kind)` up in *that member's* discovery catalog — the
  same CRD kind is routinely served at `v1beta1` on one cluster and `v1` on
  another, and reusing one cluster's descriptor elsewhere would query a path
  that doesn't exist there.
- **A `Reset` is scoped to the cluster that sent it.** Watches relist on 410
  Gone, and `ClusterTabViewModel.ApplyFleet` therefore clears only that
  cluster's rows. Treating a fleet Reset like a single-cluster one would wipe
  four healthy clusters because the fifth reconnected.
- **Partial is normal, and is always stated.** A kind missing from a cluster, or
  a cluster that can't be reached, never fails the view: the header shows
  "n of m clusters serve X" and unreachable members surface in the inline
  warning. `AsyncMerge` reports per-source failures and keeps the rest flowing
  for the same reason.
- **Rows, tab keys and metrics keys are all cluster-qualified.** The same
  namespace/name exists on every cluster in a fleet, so
  `ResourceRowViewModel.KeyFor`, `PodDetailTabViewModel.KeyFor` and
  `YamlEditorTabViewModel.KeyFor` all fold the cluster name in — otherwise the
  second cluster's pod silently reuses the first one's row and inspector tab.
  Opening a row uses **its own** cluster's client and descriptor
  (`ClusterTabViewModel.ClientFor`/`DescriptorFor`), or a YAML apply would land
  on the wrong cluster; owner-chain navigation stays pinned to the same cluster.

Why a channel-based merge (`AsyncMerge`): the sources are long-lived watch
streams that each block indefinitely, so a sequential `await foreach` over them
would starve every cluster but the first. `AsyncMergeTests` pins exactly that,
plus per-source failure isolation and teardown-on-abandon.

UI-wise this is a **toggle on the existing list**, not a new view: the sidebar,
namespace picker, filter and inspector are all unchanged, the list gains a
Cluster column (shown/hidden from code-behind, same DataGridColumn reason as the
usage columns), and the toggle only appears with more than one cluster connected
— a fleet of one is the tab you are already looking at (UI rule 1). The command
palette carries the same toggle. `MainWindowViewModel` owns the member list and
makes cluster names unique (two tabs on one context would otherwise merge into
one apparent cluster) and re-fans active aggregated watches when a tab opens or
closes, so no tab keeps watching a disposed client.

## Sandbox cluster bootstrap (how tests get a real cluster)

Integration tests run against a **real local Kubernetes cluster**, not mocks.
The suite auto-discovers `./.sandbox/kubeconfig.yaml` (git-ignored — it holds
cluster CA + client certs) or `$KUBENIMBUS_TEST_KUBECONFIG`. Tests **skip
cleanly** (return) when no cluster is reachable, so CI without one stays green.

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

## Releasing

Tag-driven, `.github/workflows/release.yml`. The procedure is written for
humans in [CONTRIBUTING.md](CONTRIBUTING.md#cutting-a-release-maintainers); the
design decisions behind it are here.

- **The version lives in exactly one place**, `<VersionPrefix>` in
  `Directory.Build.props`, and a tagged build overrides it with
  `-p:Version=<tag>`. A tag and the checked-in value disagreeing therefore
  cannot produce a mislabelled binary — the tag always wins.
- **`CHANGELOG.md` is machine-read.** The workflow lifts the section whose
  heading matches the tag (`## [0.1.0]` ↔ `v0.1.0`) and uses it verbatim as the
  release body, stopping at the next `## ` heading *or* at the link-reference
  block the file ends with. A release therefore cannot claim something the
  repository doesn't say. A missing section degrades to `--generate-notes`
  with a warning rather than failing the release.
- **NativeAOT cannot cross-compile**, which is the entire reason the build job
  is a matrix of four runners rather than four `-r` flags on one. `win-x64` is
  the shipping target; `linux-x64`, `linux-arm64` and `osx-arm64` ship too.
  `fail-fast: false` — knowing that only one RID broke is the useful outcome.
- **Every RID is launched before it is archived.** The matrix already gives each
  RID a runner of its own OS and architecture (it has to — NativeAOT cannot
  cross-compile), so each one also *runs* the binary it just built, via
  `--smoke-test`, between Publish and Stage. See "The launch check" above. This
  step is not optional polish: without it, v0.1.0 attached three binaries that
  could not start to a public release page.
- **Everything `0.x` or with a pre-release suffix ships flagged as a
  pre-release.** kubeNimbus is pre-1.0 and the release page should say so.
- **Binaries are unsigned** (no certificates), so every release body repeats
  the SmartScreen/Gatekeeper workaround. Don't drop that footer.
- `workflow_dispatch` with `dry_run: true` builds and archives all four RIDs
  without creating anything public — use it after touching the workflow.

### The app's assembly name is `kubeNimbus`, not `KubeNimbus.App`

The shipped executable is the product name, because that is what a user
downloads and pins to a taskbar. Three things are coupled to it and must move
together, or the app builds fine and dies at startup:

1. `<AssemblyName>` in `KubeNimbus.App.csproj`,
2. `App.axaml`'s `avares://kubeNimbus/Styles/Theme.axaml` — `avares://`
   authority *is* the assembly name,
3. `app.manifest`'s `assemblyIdentity name`.

The `Yaml-Mode.xshd` resource is safe: it is included with an explicit
`LogicalName`, so `GetManifestResourceStream("Yaml-Mode.xshd")` doesn't depend
on the assembly name. Root namespace and `x:Class` values are unchanged and
unaffected.

## The status dot, and where it survives

A resource row carries a health classification (`ResourceStatusSummary`) and, in most
lists, a Status column that is *already* coloured from that same classification and
also spells the word — `Running`, `CrashLoopBackOff`, `deployed`, `failed`. The 28px
dot column beside it encoded the same fact a second time and bought nothing but width,
on a list whose Name column was ellipsising object names to the point where two pods of
one ReplicaSet rendered identically.

So the dot now appears in **exactly one** case: where a CRD's own printer columns have
replaced the generic Status column (see "CRD printer columns"), because there the dot is
the last thing carrying `ResourceStatusSummary`'s verdict at all. That is the whole rule,
and it lives in `ClusterTabView.ApplySummaryColumns` as
`"" => hasPrinterColumns && ResourceStatusSummary.ShowsStatus(descriptor)`.

Two things not to get wrong when touching this:

- **The Helm release list is a second `DataGrid` with its own hardcoded columns**, not
  driven by `ApplySummaryColumns`. It kept its dot when the resource list lost one, and
  the byte-diff is what caught it — a fix that introduces the very inconsistency it was
  removing. Any future column rule has to be applied to both grids or stated as applying
  to one.
- **A dot beside a *condition* is not this pattern and must not be folded into it.** On
  pod and node Overview the dot carries polarity (`IsProblem`) while the word carries raw
  status, and they disagree exactly when it matters: on `cluster-tab-node-detail-cordoned`
  `DiskPressure  True` renders red while `Ready  True` renders green. Removing the dot
  there would delete the classification; removing the word would hide what the API said.
  The two sites that *are* still this pattern — the exec pane's dot beside "Connected
  to…" and the switcher's dot beside its environment pill — are `FEAT-73`.

## The meter track was invisible, and the token was the reason

`Controls/ResourceMeter`'s unfilled track is what shows where each row's bar *ends*, and
"three bars of different lengths cannot be compared row to row" is the stated reason the
node card's row was re-cut to fixed number columns. It was painted with
`HoverBackgroundBrush` — a **shared** token, `#80808080` at `Opacity="0.1"`, i.e. a 5%
wash meant to sit under a pointer. Measured on the rendered dark card: `srgb(14,14,14)`
on `srgb(8,8,8)`, a contrast ratio of **1.035:1**. The layout fix landed and the
comparison it was supposed to deliver was defeated one layer below it, because the shared
end could not be seen. The Pods row, which correctly has no limit and so paints no limits
extent, read as a *shorter bar* — i.e. as missing data.

`MeterTrackBrush` in `Styles/Theme.axaml` is the fix, and three things about it matter:

1. **The shared token was not retuned.** `HoverBackgroundBrush` drives every hover state
   in both Nimbus apps; the meter gets its own brush instead.
2. **It is theme-split.** A track light enough to read on the dark card is a near-black
   bar on the light one, out-weighing the accent fill it sits behind.
3. **It still does not meet WCAG's 3:1 floor for a non-text graphic, and that is a
   deliberate stop.** Achieved: dark `srgb(61,61,61)` on `srgb(8,8,8)` = **1.84:1**;
   light `srgb(210,210,210)` on `srgb(249,249,249)` = **1.44:1**. 3:1 needs roughly
   `srgb(92)` and `srgb(145)`, and at `srgb(145)` the track stops reading as an empty
   channel and starts competing with the fill. If that floor has to be met, the answer is
   an **outline** on the track rather than a heavier fill.

The general lesson, which is the third time this repo has paid for it: a colour chosen
for one job (a hover tint) silently becomes wrong when reused for another (a chart axis),
and the failure is invisible in one theme. `docs/research/2026-08-19-visual-audit.md` has
the measurements.

## Sidebar labels come from the server's plural, and now actually do

`SidebarKindViewModel.Pluralize` claimed to label rows from the server's own plural. It
did not: it used `descriptor.Plural` only to test equality with the Kind and otherwise
appended `"s"` (or `"es"` after s/x), so `NetworkPolicy` rendered as **`NetworkPolicys`**.
Every Kind ending consonant+y was affected, which on a CRD-heavy cluster is a lot of them.
It reads the plural now and re-cases it against the Kind's own capitalisation, so
`NetworkPolicy` + `networkpolicies` gives `NetworkPolicies`; a plural sharing no prefix
with the Kind falls back to the server's string as sent. A descriptor with no plural at
all (the hand-built statics, fixtures) keeps the Kind as written.

## MVP scope (phase 1 — shipped, see Current status below)

- [x] Context picker from kubeconfig (exec-plugin auth working).
- [x] Live-updating pod list (watch) — proven end-to-end in the app.
- [x] Sidebar tree (Workloads/Network/Config/Storage/CRDs via discovery),
      namespace-scoped, live list views.
- [x] Pod detail: containers, status, live log streaming (follow, container
      picker, cancel, previous-container, search/filter, ERROR/WARN/INFO
      coloring, timestamps/wrap toggles, copy/download), environment variables
      (literal + Secret/ConfigMap refs with on-demand reveal), live CPU/Mem
      usage (metrics.k8s.io, when present), events.
- [x] YAML view/edit for any resource → server-side apply; delete with confirm.
- [x] Exec into a pod container (interactive terminal) and port-forward.
- [x] Command palette (Ctrl/Cmd+K); light/dark theme.
- [x] Multi-cluster context tabs (drag-reorder, workspace-restore).
- [x] Owner-reference navigation (pod → replicaset → deployment, etc.).
- [x] pgNimbus visual design system ported (Theme.axaml, two-tone shell,
      brand-blue accent, MDI icon vectors).

**Later phases:** all shipped, including the cluster-wide "who can do X" direction
of the RBAC review. Resource metrics, session-window usage graphs, read-only Helm
release browsing, RBAC access review and multi-cluster aggregated views each have
a section above. Long-range metrics history is a **non-goal**, see "Usage over
time" above.

**Non-goals forever:** cluster provisioning, in-cluster agents, telemetry.

## Current status

**Phase-1 MVP shipped.** Core `ClusterClient` covers kubeconfig load/connect,
typed pod list+watch, cancellable log streaming, discovery (`/api` + `/apis`
walk), a generic CRD-capable list+watch (`WatchResourceAsync`/`DynamicResource`),
server-side apply with conflict surfacing, generic delete, events-for-resource,
owner-reference resolution, interactive exec and port-forward — all proven by
12 TUnit integration tests against a live k3s cluster (12/12 passing).

The Avalonia shell wears pgNimbus's design system (Theme.axaml: brand-blue
accent, two-tone Mica/AcrylicBlur shell, card/layer/pill-nav/status-dot
classes) and now has: multi-cluster drag-reorderable context tabs with
workspace persistence; a discovery-driven sidebar (Workloads/Network/
Config/Storage/CRDs — verified against real cluster CRDs, not just built-ins);
a generic namespace-scoped/all-namespaces live list; a pod detail pane
(containers, live logs, events, owner-chip navigation); a YAML editor
(AvaloniaEdit) with apply/reload/two-step-delete; exec and port-forward panes;
and a Ctrl/Cmd+K command palette. Verified end-to-end running against the
sandbox (screenshotted via the Avalonia DevTools MCP) and via NativeAOT
publish (0 new warnings beyond the known DataGrid trim warnings).

**UX polish pass (post-MVP, layout redesigned from scratch — see UI design
rules 6-8 above):** PR #2's shell mechanically ported pgNimbus's SQL-client
layout; this pass kept the visual language (color/type/iconography/materials)
but reworked the structure for a resource browser rather than a query tool:
- Sidebar gained a live filter box and collapsible sections (CRDs collapsed
  by default) — verified against a 72-kind synthetic CRD catalog
  (`tools/Screenshot/Fixtures/crd-catalog.json`) spanning cert-manager,
  argoproj, istio, velero, keda, flux, and others, since a handful of
  built-in kinds doesn't expose how the sidebar behaves on a real cluster.
- Resource list: Name/Namespace/Status trim with an ellipsis + tooltip
  instead of hard-clipping; Status renders as a color-coded pill; a pod with
  0 ready containers (CrashLoopBackOff) now reads as error, not the same
  warn as a merely-Pending pod; explicit loading/empty states
  (`IsListLoading`/`IsListEmpty`) and an inline disconnected-watch banner
  replace what used to be an undifferentiated blank rectangle.
- Inspector panel was reworked from a cramped right-side sidecar into a
  Lens-style **bottom dock**: the resource list spans the full content width and
  detail/logs/exec/YAML tabs dock beneath it, so logs and the exec terminal read
  on full-width lines instead of a narrow column. A draggable `GridSplitter`
  resizes the dock (floored so it can't collapse to a sliver) and the maximize
  toggle still fills the whole content area. Row heights for the hidden/split/
  maximized states live in `ClusterTabView.ApplyDockState` (code-behind, since a
  `GridSplitter` fights a one-way height binding). Dock tab headers show an
  active-tab highlight (`InspectorTabViewModelBase.IsActive`); Fluent's oversized
  24px `TabItem` headers were pulled down to body scale in `Theme.axaml`.
- YAML editor gained syntax highlighting (hand-written `.xshd`, AvaloniaEdit
  ships none for YAML) — see `Editing/YamlSyntaxHighlighting.cs`.
- A keyboard-shortcuts cheat sheet (F1 / the command bar's `?` button)
  surfaces Space/Enter/double-click/drag-tab, none of which had any
  discoverability before.
- Pod logs now actually auto-scroll while "Following" (that toggle only
  controlled the stream before, not the ScrollViewer); the exec terminal
  strips ANSI escape codes per chunk and caps scrollback at 200k chars
  (mirrors the existing 4000-line cap on pod logs).
- New: `tools/Screenshot`, a headless Avalonia visual-verification harness
  for environments with no display (see "Headless screenshot harness"
  above) — this pass's screenshots were fixture-driven (no Docker daemon in
  this session's environment); a live-cluster pass locally is still worth
  doing before/soon after merge to catch anything fixture data wouldn't
  surface (real CRD status shapes, real watch reconnect behavior under the
  new empty/loading states, actual terminal ANSI output from a real shell).

**Logs/events/telemetry/env-secrets pass:** closed the gaps
called out at the end of the UX polish pass — logs, events, and telemetry
were half-built or missing entirely; this pass filled them in and added
Kubernetes' other classic on-call surface (env vars/secrets):
- **Logs** (`PodDetailTabViewModel`, `LogLineViewModel`): in-buffer search/filter
  (matches against the message, not the raw line, so filtering doesn't fight
  the timestamp toggle), ERROR/WARN/INFO color coding via a lightweight text
  heuristic, a timestamps toggle (`StreamPodLogsAsync` now always requests
  `timestamps=true`; the toggle is a pure display concern — no re-stream
  needed), a wrap toggle, copy/download (Avalonia clipboard/`IStorageProvider`,
  reached via the desktop `IClassicDesktopStyleApplicationLifetime`), and a
  previous-container toggle (`StreamPodLogsAsync(..., previous: true,
  follow: false)`, a one-shot fetch, not a follow).
- **Events**: `ResourceStatusSummary` special-cases core/v1 Event so the
  generic list shows Reason/Count with Warning/Normal-driven pill color
  instead of a meaningless Status column; `SidebarGrouping.IconKeyFor` gives
  Event its own bell icon within the Config section (no new top-level
  section — the sidebar stays the five fixed sections) rather than an
  unlabeled group of the same Config icon everything else uses; double-click
  on an Event row now navigates to its `involvedObject` (via the same
  `OwnerRef`-typed resolve-and-open path owner-chip navigation already used)
  instead of opening the event's own not-very-useful YAML; pod-detail's
  Events tab gained the same Type color coding and an "open involved object"
  chevron per row.
- **Telemetry** (`ClusterClient.Metrics.cs`, new): queries `metrics.k8s.io`
  PodMetrics/NodeMetrics through the same generic `ResourceDescriptor`/
  `ListResourceOnceAsync`/`ReadResourceAsync` path every other resource kind
  uses — no bespoke parsing code. `IsMetricsApiAvailableAsync` checks the
  discovery catalog (already fetched for the sidebar) for the `metrics.k8s.io`
  group, so a cluster without metrics-server shows no CPU/Mem column/readout
  instead of erroring. The metrics API doesn't support watch, so
  `ClusterTabViewModel` and `PodDetailTabViewModel` each run their own
  20-second `DispatcherTimer` poll rather than a new watch path — CPU/Mem
  shows in the pod list (a column, metrics-gated) and pod detail (per-container
  readout next to Ready/RestartCount).
- **Env vars & Secrets**: pod detail gained an Environment tab
  (`spec.containers[].env`/`envFrom`) — literal values show inline;
  `secretKeyRef`/`configMapKeyRef` show only the reference (`Secret/name ·
  key=x`) until an explicit per-row "Reveal" fetches and decodes on demand
  (cached per Secret/ConfigMap name within the tab so revealing several keys
  from the same object doesn't refetch; RBAC/network failures surface inline,
  never crash the tab); `envFrom` sources are reference-only (no per-key
  reveal — the pod spec doesn't declare individual keys for those). The YAML
  editor gained a Secret-only "Reveal values" toggle: `data` stays base64 in
  the editable text (matching kubectl), the toggle only adds a separate
  read-only decoded-values panel computed from whatever the editor currently
  holds via the existing `YamlJson` YAML→JSON conversion — masked by default,
  nothing decoded until asked.
- **Pod-detail layout redesign** (mid-session correction, screenshot-driven):
  the first pass kept `PodDetailView`'s original fixed-width left CONTAINERS
  column and a DataGrid for Events, and at the panel's default (non-maximized)
  width that was unusable — Type/Reason/Message/Count/LastSeen had no room in
  a DataGrid, and Logs/Env/Events tab headers wrapped onto separate lines. Fix:
  the container picker moved from a fixed side column into a horizontal
  `WrapPanel`-backed `ListBox` strip above the tabs (chips: status dot, name,
  restart count, usage — Exec/port-forward buttons alongside it), which alone
  frees most of the panel's width for the tabs; Events became a card feed
  (`ItemsControl` of Border "card"s: color pill + reason, wrapped message,
  count/timestamp, "open involved object" chevron) instead of a DataGrid,
  since five columns were never going to fit an inspector-width panel and a
  scannable feed reads better for events anyway; the Environment tab's env-var
  rows are a vertical stack (name, then value/reference+Reveal button, then
  revealed value) rather than a fixed-column grid, for the same reason.
- Fixture-only this session (see below): `tools/Screenshot/Fixtures/pod-metrics.json`
  (obviously-fake usage numbers), `secret.json` (obviously-fake base64,
  flagged in the file itself), and `events.json` gained `involvedObject` on
  every entry. `pods.json`'s report-generator container gained a realistic
  `env`/`envFrom` block to exercise the new tab.
- **Not live-verified this session**: this environment's Docker daemon could
  be started (unlike the prior session), but pulling `rancher/k3s` from
  Docker Hub was blocked by this session's egress policy (confirmed via the
  agent-proxy status endpoint — a `production.cloudfront.docker.com` CONNECT
  was denied), so the sandbox recipe below still couldn't run here. Everything
  in this pass was verified via `tools/Screenshot` (both themes) plus the
  linux-x64 NativeAOT publish check; the metrics-API-*absent* degradation path
  (`IsMetricsApiAvailableAsync` returning false, hiding the CPU/Mem UI
  entirely) is exercised by construction (fixtures never set
  `IsMetricsAvailable`/never populate metrics on the default scenarios) but
  not against a real cluster either with or without metrics-server installed.
  A real-cluster pass — ideally once with metrics-server, once without — is
  still worth doing before/soon after merge.

**Usage-graphs pass:** closed the "usage graphs over time" item — see
"Usage over time (graphs)" above for the design rules. New in this pass:
`UsageHistory` (Core, bounded session-only ring + `UsageHistoryTests`),
`Controls/Sparkline.cs` (hand-rolled AOT-safe area/line chart), a sparkline
beside the number in the list's CPU/Memory cells, and pod detail's **Usage**
tab (pod-total CPU/memory charts + per-container pair, with explicit
no-metrics-server and still-collecting states). The screenshot fixtures now
replay 24 stamped poll ticks through the *real* `ApplyUsage`/`ApplyMetrics`
entry points (`ClusterTabScenarios.SeedUsage`/`SeedPodUsage`) rather than
setting chart state directly, so what renders offline is what a real poll
produces; `ApplyUsage`/`ApplyMetrics` take an optional sample timestamp for
exactly that reason (production passes none and uses now).
**Not verified this session at all:** the container had no .NET SDK and this
session's egress policy blocks every .NET install host
(`builds.dotnet.microsoft.com`, `aka.ms`, `download.visualstudio.microsoft.com`
all answer 403 through the agent proxy; only nuget.org and github.com are
reachable), so `dotnet build`, the TUnit suite, `tools/Screenshot` and the
linux-x64 NativeAOT check could none of them run. Everything in this pass is
code-reviewed only — a build + test + screenshot pass is the first thing to do
on a machine with an SDK.

**Fleet pass:** closed the last "later phase" item — multi-cluster aggregated
views, see "Multi-cluster aggregated (fleet) views" above for the rules. New:
`ClusterFleet.cs` and `AsyncMerge.cs` in Core (+ `AsyncMergeTests`), an
"All clusters" toggle and Cluster column on the existing list, cluster-qualified
row/tab/metrics keys, per-row client+descriptor resolution so an apply can't land
on the wrong cluster, and `MainWindowViewModel` ownership of the member list
(unique cluster names, re-fan on tab open/close). Screenshot scenarios
`cluster-tab-fleet-list` and `-partial` populate rows directly, since a real
aggregated watch needs several live clusters. Same verification gap as the
usage-graphs pass: no SDK in that session either, so CI (build + TUnit +
linux-x64 AOT publish) is the only thing that has looked at it.

The UX pass and the logs/events/telemetry/
env-secrets pass are both not exhaustive — there's no finish line here, just
diminishing returns; candidates for a follow-up iteration: coalescing
transition/hover animation polish, a proper win-x64 NativeAOT pass (still
only linux-x64 has ever been verified), a live k3s pass, and node-level
CPU/Mem (only pod-level shipped by the logs/events/telemetry pass;
node-level was added separately by the Helm/RBAC/metrics pass above — see
"Live CPU/memory from metrics.k8s.io").

**Sidebar navigation pass** (small, alongside the fleet pass): the two
sidebar follow-ups are closed, though not the way they were originally
phrased. *Coalescing* same-named CRD kinds into one row was rejected —
nesting rows inside a section that is already 100+ kinds deep costs more
than it buys, and the group label added earlier already tells `Backup`
(velero.io) from `Backup` (postgresql.cnpg.io). What was actually missing
is that you could not **filter** by the thing the row displays:
`SidebarKindViewModel.Matches` now matches the API group and the server's
short names as well as the display name, so "velero" or "svc" find what
you would expect. And a pinned **Recent** section (top of the sidebar, max
5, `ClusterTabViewModel.RecordRecentKind`) holds second instances of the
kinds most recently selected — session-scoped and reset on reconnect,
since the entries hold descriptor instances from the catalog being
replaced. Persisting it across restarts would need a `WorkspaceSettings`
schema change and is deliberately not done yet.

**"Who can do X" pass:** the last open roadmap item is closed — the cluster-wide
direction of the RBAC review, see "RBAC access review" above for the rules. New:
`ClusterClient.WhoCan.cs` in Core (`WhoCanAsync` rule scan, `CheckAccessAsync`
SubjectAccessReview, `AccessQuery`/`WhoCanResult`/`SubjectAccess`/`AccessDecision`)
plus `WhoCanMatchingTests` pinning the API server's matching semantics and three
sandbox-gated integration tests; a "Who can…" section in the existing access-review
pane (verb picker, kubectl-style resource box resolved through the discovery
catalog, optional object name, all-namespaces toggle, per-subject Verify) and a
palette entry that opens straight onto it; `WhoCanRowViewModel` in the App layer;
`cluster-tab-rbac-who-can{,-empty}` screenshot scenarios; and two sandbox RBAC
shapes nothing else produced (a `resourceNames`-narrowed rule, a ClusterRole bound
by a RoleBinding). Also fixed in passing: a failed `SelfSubjectRulesReview` used to
blank the whole access-review pane, and now renders inside "My permissions" only.
**Verified this session** (build, 80/80 TUnit, both-theme screenshots, linux-x64
NativeAOT publish with no new warnings beyond the known DataGrid ones). Getting an
SDK took a detour worth writing down, since the last two passes gave up at this
point: **`builds.dotnet.microsoft.com`, `aka.ms`, `dot.net` and the Launchpad PPAs
are all 403 through the agent proxy, but `archive.ubuntu.com` is not** — and Ubuntu
24.04's own `noble-updates/main` carries `dotnet-sdk-10.0` (plus
`dotnet-sdk-aot-10.0`, which is what makes the AOT publish work). So:

```bash
apt-get update && apt-get install -y dotnet-sdk-10.0 dotnet-sdk-aot-10.0
```

Blocked PPAs in `/etc/apt/sources.list.d/` (deadsnakes, ondrej, docker) fail the
`apt-get update` — move them aside first. Do **not** reach for the dotnet-install
script in this environment; it only ever hits the blocked hosts.

Still unverified: the live-cluster half. Docker's daemon starts here, but pulling
`rancher/k3s` still dies on a policy denial for Docker Hub's blob CDN
(`production.cloudfront.docker.com`, 403 on the layer fetch after the manifest
succeeds), so the sandbox can't come up and the RBAC integration tests — including
the three new who-can ones — skipped rather than ran. A real-cluster pass remains
the outstanding item.

**Public-release prep pass (v0.1.0):** the repository is now shaped for a public
audience and a tagged release. See "Releasing" above for the design rules. New:

- **Release plumbing.** `Directory.Build.props` carries the single
  `<VersionPrefix>` plus product/author/copyright/repo metadata;
  `.github/workflows/release.yml` publishes NativeAOT for win-x64, linux-x64,
  linux-arm64 and osx-arm64 on a `v*.*.*` tag, archives each with LICENSE/
  README/CHANGELOG, emits `SHA256SUMS.txt`, and creates the release with the
  matching `CHANGELOG.md` section as its body. `CHANGELOG.md` itself is new
  (Keep a Changelog, 0.1.0 covering everything shipped to date).
- **The shipped executable is now `kubeNimbus`**, not `KubeNimbus.App` — see
  the three coupled places under "Releasing" above.
- **Community health files**: `CONTRIBUTING.md`, `SECURITY.md` (which states
  the security model, not just a reporting address), `CODE_OF_CONDUCT.md`,
  issue templates that ask for cluster distribution and sandbox-reproducibility
  because those are what make a Kubernetes-client bug tractable, a PR template
  whose checklist is this file's rules, and `dependabot.yml` with Avalonia
  grouped so a bump arrives as one buildable PR rather than six.
- **README rewritten for someone deciding whether to download it**: badges,
  a screenshot gallery from `design/screenshots/` (generated — see
  `design/screenshots/README.md`), per-platform install including the unsigned-
  binary workarounds, and an explicit known-limitations section.
- **Two real bugs fixed in passing**, both of which had been quietly wrong:
  - **CI never ran the tests.** `dotnet test <csproj>` positionally is a no-op
    under the .NET 10 MTP runner — it prints a hint and exits 0. Every "green"
    CI run since the workflow landed tested nothing. Now `--project`, and
    called out in the Verification workflow section above so it can't recur.
  - The screenshot harness rendered every scenario with "No kubeconfig
    contexts" in the command bar, which reads as a failed connection in a
    README image; `SeedContexts` fixes it, and `cluster-tab-pod-detail` renders
    at 1000px so the log pane isn't clipped by the window edge.
- **CI also renders the screenshot harness now** as a XAML smoke test, and
  uploads the PNGs as an artifact — the assembly rename above is exactly the
  class of change that compiles cleanly and dies at startup.
- **Verified this session**: build, **80/80 TUnit** (with `--project`; they
  skip the cluster-gated ones — no sandbox here), all 58 screenshots in both
  themes, and the linux-x64 NativeAOT publish. Audited tree *and* git history
  for credentials — clean; every hit is obviously-synthetic fixture or sandbox
  data.
- **Still unverified**, and the first things to do on a real machine: the
  win-x64 NativeAOT publish (only linux-x64 has ever run), the macOS and Linux
  release binaries actually launching, and the live-cluster half — the sandbox
  still can't come up here (Docker Hub blob CDN blocked by egress policy).

**Core-scenario + Advanced-view pass:** the first pass driven by hand-testing
against a live cluster rather than by fixtures, and it found that the app's
central on-call scenario — open a pod, read logs, exec in, port-forward, look
at env/secrets — was partly broken end to end. See the Advanced view section
and UI rule 8b above for the two rules it added. What was wrong, and is not
any more:

- **Pod logs never streamed.** Two independent causes: the `ToggleButton`
  `IsChecked`+`Command` double-toggle (UI rule 8b) made Follow a guaranteed
  no-op and made `LoadPreviousLogs` unreachable, and `StartLogs()` was never
  called on open — so double-clicking a pod landed on a blank card with no
  message at all. Logs now start on open, the stream follows the container
  picker (it used to keep streaming the old container under the new one's
  name), and the pane has explicit states for streaming-but-silent, stopped,
  ended-with-a-reason and filter-matched-nothing-of-*n*-buffered.
- **`LogSeverityToBrushConverter` returned `null` for the default case**, which
  writes a *local* null `Foreground` that beats inheritance — and Avalonia's
  glyph-run draw early-returns on a null brush, so every line without a
  severity keyword rendered **invisible**. That is most lines: nginx access
  logs, Go `log.Print`, anything JSON. It returned `AvaloniaProperty.UnsetValue`
  after this pass. It was never caught because every fixture log line contains a
  keyword. **`UnsetValue` turned out to be the same bug one size smaller, and the
  converter is gone** — see "Log severity is three classes, not a brush binding"
  below for what actually fixed it and how it was measured.
- The severity heuristic was substring, not token, so `GET /api/v1/errors`
  coloured red; it matches whole words now.
- **Throughput**: the pump awaited one dispatcher round-trip *per line* and did
  an O(n) `ObservableCollection.Remove` per line past the 4000 cap. Lines are
  now batched on a 100 ms tick and trimmed with one `RemoveRange`. Auto-scroll
  is posted (it used to run inside `CollectionChanged`, one line behind) and
  has a scroll lock.
- Logs are horizontally scrollable and selectable; Copy/Download write the raw
  lines *with* timestamps (they wrote `DisplayText`, so a log saved with the
  timestamp toggle off had none).
- **Init and ephemeral containers were invisible entirely** — a failing init
  container could not be inspected at all. All three lists are read now, the
  chip carries the role and the live state (`CrashLoopBackOff`), and the
  default selection is the first *app* container, as `kubectl logs` does.
- `RefreshEnvironment()` ran on every watch tick and cleared `EnvironmentVars`,
  so a **revealed secret value vanished seconds later**; it is now keyed on a
  signature of the container's own env block. `fieldRef` resolves against the
  pod object we already hold, `optional: true` refs read dim rather than as
  errors, and each `envFrom` line opens the object it names.
- **Exec**: `/bin/sh` was hardcoded, so a bash-only or distroless image gave a
  connected-looking blank terminal. It now tries bash → sh → ash, decided by
  the API server's **error channel** (channel 3 — the only place a missing
  shell is reported; neither stdout nor stderr carries it). Ctrl+C / Ctrl+D /
  Tab reach the remote shell, the input box takes focus on open, and
  `ResizeAsync` finally has a caller so the PTY isn't stuck at 80×24.
  **Gotcha worth keeping**: `StreamDemuxer`'s per-channel streams do **not**
  observe a `CancellationToken`, so the shell probe times out via
  `Task.WhenAny`, not `CancelAfter` — the first live run hung on "Connecting…"
  forever because of exactly that.
- **Port-forward** offers the pod's declared ports with their names (they were
  collected and then discarded for a hardcoded 8080), copies/opens the local
  URL, locks its inputs while running, titles the tab with the port, and shows
  the kubelet's own refusal text. A forward whose last connection failed reads
  warn, not ok — the listener really is still accepting, so "stopped" would be
  a lie.
- **The list gained kubectl's columns** — Ready / Restarts (with "(43m ago)") /
  Age / a kind-specific Details — gated per kind by `ResourceStatusSummary`
  from `ClusterTabView.ApplySummaryColumns`. Age ticks off one shared timer per
  list, since no watch event makes wall-clock change.
- **Right-clicking a resource did nothing**; there is now a row `ContextFlyout`
  (Logs / Previous logs / Exec / Port-forward / Edit YAML / Delete) with the
  same six actions mirrored as palette entries, and a `PointerPressed` handler
  so the menu acts on the row it opened over rather than on the previous
  selection — which matters when the last item is Delete.
- Sidebar kinds are labelled from the server's own plural, so `Endpoints` stops
  rendering as "Endpointses" (and no CRD Kind that is already plural will).

**Verified this session, against the live k3s sandbox**: build (0 warnings),
**137/137 TUnit with 0 skipped** (so the cluster-gated tests really ran), both
byte-level repro scripts (`pftest.cs` → `HTTP/1.1 200 OK` with no junk prefix;
`yamltest.cs` → all string scalars survive), all 32 screenshot scenarios in both
themes, and a DevTools-driven pass over the running app: the Status/Ready/
Restarts/Age columns match `kubectl get pods -A` row for row across every
`demo-*` pod (`bad-image` → ImagePullBackOff, `crashloop` → CrashLoopBackOff
151 (2m ago), a finished Job pod → Completed and *not* coloured as an error),
logs stream on open with no click, switching container switches the stream,
Previous works on `demo-broken/crashloop`, a filter matching nothing says so
with the buffered count, and exec connects after correctly skipping `/bin/bash`.

**Not verified this session**, in rough priority order: the port-forward pane's
new UI end to end (Core is proven by `pftest.cs`, the pane is not), the env/
Secret reveal path against a real Secret, the row context menu and the new
palette entries by actual mouse/keyboard (they were verified by construction,
not driven), Advanced-view off/on in the *running* app rather than in the
harness, and the win-x64 NativeAOT publish — still the one build that has never
run anywhere. **Closed 2026-08-04** except win-x64 NativeAOT — see "Live-cluster
validation pass" below.

**Inspector density pass:** driven by a screenshot of the running app whose
complaint was, in three parts, "tabs too big, too much nesting, too little room
for content" — and measuring the dock proved it: pod detail spent ~200px of a
~300px dock on chrome. See UI rule 10 above for the rule this pass added; the
mechanics are `ListBox.segmented` + `TabControl.headerless` +
`Rectangle.toolSeparator` in `Theme.axaml` and the new
`Converters/IndexEqualsConverter.cs`. What changed:

- **Pod detail: four chrome rows → two.** Owner chips moved onto the container
  row (the "Owned by" label went — the chips say `ReplicaSet/x` themselves, and
  they gained a border so they read as clickable, UI rule 8); the container
  picker became a horizontally-scrolling strip rather than a `WrapPanel`, so
  eight containers cost the same height as one; the log filter box, the
  Follow/Previous/timestamp toggles, Events' refresh and Usage's window caption
  all moved onto the tab strip's row; Env's `ENV — <container>` header went
  (the strip two rows up *is* its selector).
- **Env and Events got denser inside their tabs too.** Env is name-beside-value
  on a fixed 200px name column — Auto per row would start every value at a
  different x, since Avalonia has no shared-size scope — and Reveal sits next
  to its reference instead of flung to the right edge. An event card is two
  lines, not three (count/timestamp ride the reason). Both roughly double what
  fits.
- **Helm and RBAC lost their title rows** and gained the same one-row strip.
  Their titles duplicated the dock tab (`Helm/checkout`, `Access/payments`)
  exactly. `HelmReleaseView` binds its TabControl to the strip's `SelectedIndex`
  by element reference (`#HelmTabStrip`), since that view model has no tab-index
  property; `RbacView` keeps binding both to `SelectedTabIndex`, which
  `WhoCanTabIndex` deep-links to.
- **`cluster-tab-helm-release-detail` is a new screenshot scenario**, because
  `HelmReleaseView` was the one inspector view the harness never rendered — and
  the harness is CI's only check that a view's XAML still loads. Its fixture
  drains the offline load before writing its text: the failed load's
  continuation lands on the same `RunJobs()` the capture pumps, so anything set
  before it is overwritten by "Connection refused" (which is exactly what the
  first run of that scenario rendered).

**Verified this session**: build (0 new warnings), **137/137 TUnit, 0 skipped**,
all 66 screenshots (33 scenarios × both themes), and the linux-x64 NativeAOT
publish with no new warnings beyond the known DataGrid IL2104/IL3053. The seven
generated README screenshots under `design/screenshots/` were regenerated.
**Not verified**: no live cluster here (Docker Hub's blob CDN is still blocked
by this session's egress policy), so this pass — like the layout it replaces —
has only been seen in the harness. The row heights it frees up are worth a look
in the running app, particularly a pod with many containers (the strip now
scrolls rather than wrapping) and a filter box narrowed by a small window.
And win-x64 NativeAOT remains the build that has never run anywhere.
**Closed 2026-08-04** for the running-app half — see "Live-cluster validation
pass" below; win-x64 NativeAOT is still outstanding.

**Live-cluster validation pass (2026-08-04):** a hand-driven pass against the
sandbox to close the "not verified" gaps the two passes above both flagged —
the port-forward pane, the Secret reveal path, the row context menu, and the
Advanced-view toggle had each only ever been exercised by construction or in
the headless harness, never actually clicked. Docker's daemon and the
`rancher/k3s` pull both worked this time (the egress block on prior sessions
was environment-specific, not a standing constraint), so `docker start
kubenimbus-sandbox` plus a ~45s wait for kubelet to reconcile after the
container restart was enough — no `-Recreate` needed. Method: the Avalonia
DevTools MCP attached to the running Debug build for node-targeted clicks
(`input` with `Click`/`Text`/`KeyDown` — reliable for buttons/tabs/ListBoxItems,
but the DataGrid row's double-click-to-open gesture doesn't answer to a
synthetic `Click` twice in a row, since ClickCount tracking lives in Avalonia's
input manager, not the diagnostics bridge), with computer-use driving real
mouse/keyboard for the gestures DevTools can't send (double-click, right-click)
and for resizing the window. The window was resized from the physical
3840×1600 panel down to **1920×1080** via a direct Win32 `SetWindowPos` (found
by `EnumWindows` — the title is `"kubeNimbus"` but window-name lookup by exact
string missed it, so enumerate-and-grep was the reliable path) — reviewing UI
at native 4K-wide would have hidden the layout problems a majority of users
would actually see at Full HD. None turned up at 1920×1080: sidebar, dock and
tab strip all stayed within bounds with no clipping or wrapping.

Confirmed working, each against real cluster state (not fixtures):

- **Logs start on open, no click.** Double-clicking `demo-broken/crashloop`
  populated the Logs tab immediately with its real ERROR line.
- **Container switch switches the stream.** Selecting `access-log-tailer` on
  `demo-shop/shop-web-*` replaced the `web` container's nginx access log with
  the tailer's own heartbeat/WARN/ERROR lines — not the old container's log
  under the new label.
- **Non-keyword log lines are visible**, not invisible-by-null-brush: the
  `web` container's plain nginx `[notice]`/access lines rendered in normal
  text color alongside the tailer's colour-coded severity lines.
- **Env tab, live.** `shop-web`'s `web` container showed a literal
  (`SERVICE_NAME`), a ConfigMap ref, a Secret ref, and a `fieldRef:
  status.podIP` resolved to the pod's real IP; clicking **Reveal** on the
  Secret ref decoded a real value (`sandbox-token-0000`) from the live
  `shop-credentials` Secret.
- **Exec, end to end.** Connecting to `shop-web`'s `web` container (an nginx
  image, no bash) reported "Connected to web (/bin/sh)" — the bash→sh→ash
  probe correctly skipped bash — the input box had focus without an extra
  click, and `echo hello_from_kubenimbus` round-tripped through the real
  WebSocket exec channel.
- **Port-forward, end to end** — the one surface Core-only (`pftest.cs`) had
  ever exercised. Declared port 80 offered by name/number from the pod spec,
  Start produced `Forwarding 127.0.0.1:50337 → shop-web-...:80`, and `curl
  http://127.0.0.1:50337/` returned a real `HTTP 200` from the pod's nginx.
  Stop cleanly reverted to a "Stopped." state with inputs re-enabled.
- **Row context menu** opens on the row under the cursor (not the previous
  selection) with all six actions; **Edit YAML** from it opened a real pod
  manifest with live `managedFields`, syntax highlighting, and working
  Apply/Delete.
- **Advanced view toggle**, live: CPU/Memory columns and per-container
  sparklines appeared with real polled values from metrics-server (e.g.
  `cache-0` at `1m`/`5.0 MiB`), and sidebar kind-count badges appeared next to
  every section.
- **Helm release detail**, live: `Helm/checkout` opened straight to the
  Values/Manifest/Notes/History strip with no repeated title row (the
  density-pass fix), decoded real chart values, and History showed the
  sandbox's synthetic 3-revision release (`checkout-0.2.1`/`-0.2.0`/`-0.1.0`,
  rev 3 `deployed`, 1–2 `superseded`) with real timestamps.

Build: 0 warnings. **TUnit: 137/137 passed, 0 skipped**, run via the test
`.exe` directly (`dotnet test --project` still reports "Zero tests ran" on
this SDK — see below) — 0 skipped confirms the cluster-gated tests really ran
against a live server, not just the unit-only subset.

**Not covered this pass**: RBAC access review / Who-can (unchanged since the
last pass that verified it), init/ephemeral container visibility, and the
win-x64 NativeAOT publish, which has still never run on any machine this
project has touched.

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

**Fluent form/state pass:** two surfaces reworked against [Fluent basics][fluent-basics],
adding UI rule 11 and the ConfigMap/Secret section above.

- **Port forward.** Was a six-row stack under a `PORT FORWARD` title the dock tab
  already carried, with beside-the-input labels ("Local port" touching its own
  box), a Start/Stop pair one half of which is always dead, a `0` that silently
  meant "pick one for me", and a bare status dot. Now: no title row, one field
  row reading local → pod with labels above (`TextBlock.fieldLabel`), the
  declared-port picker beside the pod-port box rather than on a row of its own,
  one button that swaps on `IsRunning`, an empty box under an `auto` placeholder
  (`LocalPortInput`, where null *is* the wire value 0), and a `Border.infoBar`
  carrying the local URL itself — selectable, copyable, openable — since reaching
  the thing you forwarded is the whole point of forwarding it. The running
  `StatusMessage` sentence is gone (the bar and the tab header said all of it);
  `StatusIsError` distinguishes "Stopped." from "Local port 8080 is already in
  use", which used to render identically.
- **Env tab.** ConfigMap refs resolve on open, Secret refs mask behind an eye —
  see the section above for the rules and the reasons.
- New screenshot scenario `cluster-tab-port-forward-idle` (the state the tab
  actually opens in, and the one nothing rendered before), and
  `cluster-tab-pod-detail-environment` now drains the auto-resolve before writing
  its fixture values, same as `HelmReleaseDetail` — the offline client's
  "connection refused" otherwise lands on the capture's own `RunJobs()`.

**Verified this session**: build (0 warnings), **137/137 TUnit, 0 skipped**
(sandbox up, so the cluster-gated tests really ran), and all 34 scenarios × both
themes rendered. **Not verified**: neither surface has been driven by hand in the
running app this session — the eye toggle against a real Secret and a real
forward's start/stop are the two worth clicking. win-x64 NativeAOT remains the
build that has never run anywhere.

**Store-readiness pass:** the app was unusable for the audience it was about to be
submitted to. A reviewer on a clean Windows machine — the Microsoft Store's own
certification scenario — landed on an empty state whose only instruction was to run
a script from a repository they do not have, with no way to reach a cluster from
inside the app and nothing to look at without one. Two halves:

- **Kubeconfig discoverability.** `Kubeconfig.CandidatePaths`/`DiscoverPaths`/
  `LoadContextsAsync` take user-supplied extra paths, reported with a `picked`
  source label; "Open kubeconfig file…" writes one through `IStorageProvider` and
  persists the **path only** in `WorkspaceSettings.KubeconfigPaths`. A pick that
  yields no contexts is deliberately not remembered — otherwise a mis-pick poisons
  every subsequent start with Rescan re-running the same failure. The empty state's
  prose leads with the picker; the `scripts/sandbox-up` hint is gone (it lives in
  CONTRIBUTING.md and the README, where a contributor is already looking).
- **The demo cluster** — see the "Demo cluster" section above for the design rules.

**Verified this session** (no live cluster; Docker's daemon isn't running here):
build with 0 new warnings, **145/145 TUnit, 0 skipped** — the cluster-gated tests
returned early rather than running, so that count is the unit-only subset — all 38
screenshot scenarios × both themes, the linux-x64 NativeAOT publish with no new
warnings beyond the known DataGrid IL2104/IL3053, and **a hand-driven pass over the
real app under Xvfb** (Xvfb + xdotool + ImageMagick `import` substitute for the
DevTools MCP on a machine with no display; this works well and is worth reaching for
again). Clicked, with no kubeconfig and `$KUBECONFIG` unset: Explore demo cluster →
pod list; double-click a pod → logs streaming with plain (keyword-free) lines
visibly rendering; Env → Secret eye toggle decoding the demo Secret, ConfigMap and
`fieldRef` resolved in place; Events; the row context menu → Port-forward and Edit
YAML, both landing on their stated demo states; a kind with no demo data → the real
empty state; Helm → release list and detail; the advanced-view toggle → usage
columns with sparklines and gap-only rows; the switcher, both with the demo tab open
(under "Open") and closed (its own "Demo" group); a restart restoring the demo tab
from the sentinel path; and the file picker itself — picking a real kubeconfig
opened its context, and deleting that file and restarting degraded to the empty
state with the path listed `missing … (picked)`, no exception.

**Found and not fixed: the Linux and macOS release binaries cannot start.** A
NativeAOT-published `kubeNimbus` dies immediately with
`FileNotFoundException: The resource /Assets/app.ico could not be found` out of
`Avalonia.Platform.StandardAssetLoader`, from `MainWindow`'s `Icon`. It is
**pre-existing** — an AOT publish of the parent commit fails identically — and it is
not a missing resource: `!AvaloniaResources` and `app.ico` are both present in the
published managed assembly, and neither normalizing the csproj glob to forward
slashes nor fully qualifying the URI as `avares://kubeNimbus/Assets/app.ico` changes
anything, so it is Avalonia's asset registration under NativeAOT rather than
anything in this repo's item groups. Both experiments were reverted. Removing the
`Icon` attribute makes the same binary start and run correctly, which is how the
demo cluster was verified under AOT (embedded `Demo.*.json` read through
`GetManifestResourceStream` survives trimming intact — sidebar, list, logs, usage
and Helm all render from the single-file binary). Worth an upstream Avalonia issue;
until then `.github/workflows/release.yml` ships three RIDs that cannot launch.
win-x64 NativeAOT still cannot be built here (`Cross-OS native compilation is not
supported`) and remains the build that has never run anywhere.

**One-bar chrome pass:** the top of the window carried two bars — the OS title bar
and our 44px command bar — where every comparable app (VS Code, Chrome, Explorer,
Lens, Aptakube) carries one. See UI rule 12 above for the rules; the change itself
is small: `ExtendClientAreaToDecorationsHint` on Windows/macOS, the `TitleBar`
decoration role on `CommandBar` with `User` on everything clickable inside it,
`OffScreenMargin` honored on the root layout, the bar down from 44px to 40px, and
the wordmark deleted. Net ~36px of vertical chrome back, which is ~12% of the
inspector dock's 300px default — roughly two more log lines, at the top of every
window, permanently.

**Verified this session**: build (0 warnings), **145/145 TUnit, 0 skipped** (no
sandbox here, so that is the unit-only subset — the cluster-gated tests returned
early), all 38 screenshot scenarios × both themes, the linux-x64 NativeAOT publish
with no new warnings beyond the known DataGrid IL2104/IL3053, and the app running
under Xvfb on the **Linux** path, which is the path this change deliberately leaves
alone. The seven generated README screenshots were regenerated.

**The caption buttons turned out to be ours to draw.** The first cut of this pass
assumed Windows would keep drawing them, the way pre-12 `PreferSystemChrome` did.
Reading Avalonia 12's Win32 backend says otherwise — an extended client area reports
`RequestedDrawnDecorations = TitleBar` and calls `DisableCloseButton` on the HWND —
so without a decorations template the window would have had **no way to close**, and
with Fluent's stock one it would have had a second title bar and the window title
painted over the command bar. `CommandBarWindowDecorations` in Theme.axaml is the
answer; see UI rule 12.

**Verified for the drawn decorations**, since neither the harness nor a plain Linux
run builds them: with `X11PlatformOptions.EnableDrawnDecorations` on and the platform
gate forced open (both reverted), the app renders the three buttons at the right of
the 40px bar, sized and hovering correctly, with the command bar's controls stopping
exactly where the caption strip starts — and no Fluent title bar or title text over
either. The wiring itself is platform-independent (`AttachCaptionButtons` looks the
`PART_*` names up and subscribes `Click`).

**macOS needs no drawn decorations, and one thing beyond that.** `Avalonia.Native`
reports `NeedsManagedDecorations = false` and `RequestedDrawnDecorations = None`, so
AppKit keeps the traffic lights and the theme above is never built there; the height
hint is forwarded to the native window (`SetExtendTitleBarHeight`), which is what
lines the lights up with a 40px bar rather than a 30px one. What it *did* need is the
full-screen case: its backend zeroes `ExtendedMargins` in full screen and the lights
go away, so a reserve fixed at construction leaves a dead 78px hole — and on macOS
the green light is the ordinary way in. `ApplyCaptionReserve` now recomputes off
`WindowDecorationMargin`, which fixes the same hole on Windows (where full screen
strips every drawn part) for free.

**Still not verified, and it is the half that matters**: no Windows or macOS machine
has run this. First things to check on a Windows box, in order: that the buttons
appear and work (close especially — the X11 experimental path hovered but did not
activate, which may be its own quirk or may not), that 3 × 45 DIPs is the right
reserve at 100% *and* 150% scaling, that dragging/double-click/Snap Layouts work from
the empty tab strip, that a maximized window isn't clipped at the top, and that Mica
still renders now that the bar is inside the extended area. On macOS: that the
switcher button clears the traffic lights at 78 DIP, that the lights sit centred in
the 40px bar, and that entering full screen with the green button collapses the
reserve rather than leaving a gap. The full-screen path is the one piece that could
not be driven even under X11 — Xvfb has no window manager, so `WindowState` changes
have nothing to honour them.

**Windows validation pass (2026-08-04):** the half above that mattered — a real
Windows box — finally ran this. Sandbox up (`docker start kubenimbus-sandbox`, no
`-Recreate` needed), build 0 warnings, **145/145 TUnit, 0 skipped** (run via the
test `.exe` directly — `dotnet test --project` is still broken on this SDK, see
below), app launched against the live cluster via the Avalonia DevTools MCP (the
dev-run process isn't Start-Menu-registered, so `computer-use` couldn't attach for
real mouse drag/Snap-Layouts gestures — that specific gap remains). Confirmed via
DevTools, structurally and functionally:

- `ExtendClientAreaToDecorationsHint=True`, `WindowDecorationMargin=0,40,0,0` — one
  40px bar, no second OS title bar underneath.
- `PART_MinimizeButton`/`PART_MaximizeButton`/`PART_CloseButton` exist at
  `Bounds 1145,0,135,40` in a 1280-wide window — exactly `3 × CaptionButtonWidth`
  (45 DIP) from the right edge, confirming the reserve math.
- **All three buttons are functionally real**, not just present: clicking Minimize
  set `WindowState=Minimized` (`IsActive` false); clicking Maximize grew `ClientSize`
  to the full physical panel (`3792×1600`) with `WindowDecorationMargin` unchanged;
  clicking Close ended the process cleanly (confirmed via `tasklist`, and the
  DevTools call itself timed out mid-request as the connection died — expected). This
  is the scenario UI rule 12 warns about directly: Windows disables the *native*
  close button under an extended client area, so a non-functional custom one would
  have shipped a window with no way to close.
- **Close → relaunch → workspace-restore** round-tripped correctly: relaunching
  reconnected to the live cluster, restored the `kubenimbus-sandbox` tab, and kept
  the Advanced-view setting — `WorkspaceSettings` persistence holds up with the new
  chrome.
- **Advanced-view toggle** (UI rule 8b — the double-toggle class of bug that shipped
  broken three times already) — one click cleanly hid CPU/Memory columns, sparklines
  and sidebar kind-count badges together; one click restored them; `IsChecked`
  landed correctly each time. No regression.
- No exceptions or errors in either session's app log.

**Not covered by this pass**: real mouse drag-to-move, double-click-to-maximize and
Win11 Snap Layouts on the caption strip (needs actual OS-level drag, which neither
DevTools synthetic input nor `computer-use` could reach for this process), 150%
DPI scaling, and multi-monitor. Also hit, and worth naming so it isn't mistaken for
an app bug: DevTools' synthetic `Click` reliably drives `Button`/`ToggleButton`
controls (used above) but returned `handled:false` against `DataGridRow` and
`ComboBoxItem` in this session, and the live-watch pod list recycles virtualized
`DataGridRow` node IDs across ticks — so a hands-on click-through of row
selection/double-click-to-open, the namespace picker, and post-redesign
port-forward/exec is still owed on a real mouse. Port-forward and exec's *last*
full live-cluster verification predates the Fluent form/state pass's visual
redesign of the port-forward pane (see the 2026-08-04 "Live-cluster validation
pass" above).

**List search + column gutter pass:** two complaints from the running app, both about
the list. See UI rules 13 and 14 above for the rules they added.

- **There was no way to search the list by name.** The sidebar's filter box narrows
  kinds, and people reasonably read it as *the* filter; nothing narrowed the objects.
  New: `RowFilter`/`VisibleRows` on `ClusterTabViewModel`, a search box in the list
  header (same shape as the sidebar's, with a "12 of 87" beside it), Ctrl/Cmd+F on the
  window, Esc/Enter in the box, `ResourceRowViewModel.Matches`, an `IsFilterEmpty`
  no-match state, and `cluster-tab-list-filtered{,-empty}` screenshot scenarios.
- **Columns ran into each other.** `48 MiB16d`, and — worse — a `—` placeholder in the
  Memory column abutting Age, which reads as a *negative age* and was reported as one.
  Root cause was Fluent's left-only `DataGridCell` padding, not the values. The gutter
  itself landed in 6a48547 at 12px; this pass cut it to 10px and re-cut the column
  minimums with it, because 12px on nine columns pushed **Age off the right edge** at
  the 1280px the harness renders (UI rule 14). Both grids in `ClusterTabView` get it,
  and so does every other DataGrid in the app.
  The fleet list still clips its rightmost headers at 1280px — ten columns do not fit
  in ~910px and horizontal scroll is the answer — but that is unchanged from before
  the gutter, checked by rendering the scenario with Fluent's padding put back.

**Verified this session**: build with 0 new warnings, **145/145 TUnit, 0 skipped**,
and all 40 scenarios × both themes rendered (the harness is the XAML smoke test).
**Not verified**: nothing has been driven by hand in the running app —
Ctrl/Cmd+F focusing the box, Esc handing focus back to the grid, and the filter
surviving a live watch tick (a Modified event on a filtered-out row must not make it
reappear) are the three worth clicking.

**Design-parity pass (settings, help system, one design language):** the complaint was
that kubeNimbus looked visibly worse than pgNimbus *even after* the shared design
system was extracted, that the two top bars did not match, and that kubeNimbus had no
settings and no help system. Four halves, and the first one explains the other three:

- **The shared library was in sync; the extraction was incomplete.** `shared/nimbusUi`
  was byte-identical in both repos (modulo CRLF) — but what had been pulled up was only
  the *shell* vocabulary (tokens, `card`/`layer`/`chip`/`searchpill`/`toolbar`/`accent`,
  scrollbars, `statusBar`, `sectionHeader`). pgNimbus's ~350 lines of **Fluent control
  retheming** stayed behind in its own `Theme.axaml`, so kubeNimbus rendered every
  `TextBox`, `ComboBox`, `NumericUpDown`, `ListBox`, `TreeView` and `DataGrid`, and had
  no `.soft`/`.danger` button family at all, as **stock Fluent** beside pgNimbus's toned
  versions. That is the whole of "looks worse", and it was invisible from inside either
  app — you only see it with the two windows side by side. Now
  `shared/nimbusUi/Theme/Controls.axaml`, consumed by both. `TabItem` stays per-app as
  before, and `TabControl.segmented` newly joins it on DESIGN.md's not-shared list:
  kubeNimbus does that job with `ListBox.segmented` + `TabControl.headerless` on
  purpose (UI rule 10).
- **The command bar now reads the same left to right as pgNimbus's** — see UI rule 15.
  New: the `☰` app menu and the sidebar toggle at the left, `⚙` preferences on the
  right, and the right cluster reordered to pgNimbus's order. The sidebar toggle is a
  real feature, not just a matching glyph: `MainWindowViewModel.IsSidebarVisible` is
  shell-owned and mirrored onto every tab like `IsAdvancedView`, and
  `ClusterTabView.ApplySidebarVisibility` collapses the **column**, not just the panel
  — hiding a Grid child leaves its column at full width, which would have left a third
  of the content area blank and the list exactly as narrow as before.
- **A settings system**, `settings.json` beside the existing workspace — see "Settings,
  and what belongs in which file" above for the split, the migration and the five
  rules. It also connects something that had been dead: the shared hotkey resolver has
  supported a Ctrl/Cmd override since extraction, but kubeNimbus never called
  `Initialize`, so the setting existed in code and was unreachable.
- **A help system**: `CommandCatalog` in Core as the single source for key bindings,
  palette titles, the F1 sheet and a generated `docs/keyboard-shortcuts.md`, plus
  `CommandTip` for tooltips that carry a live shortcut, an About window, and a cheat
  sheet rebuilt with sectioned keycap chips instead of a flat list of monospace
  strings. See "The command catalog" above for the six rules. This also fixed a latent
  bug: `Hotkeys.cs` held its gestures in `static readonly` fields, which the shared
  resolver explicitly warns against — harmless while the modifier could never change,
  a real bug the moment the scheme became a preference.

**win-x64 NativeAOT now builds *and launches* — the first time either has happened.**
It was still failing at the same `/Assets/app.ico` `FileNotFoundException` that
CLAUDE.md had recorded for linux/osx, which means the bug was never platform-specific:
**every** release RID shipped a binary that could not start. The cause is narrower than
"Avalonia asset registration under AOT": `Icon="/Assets/app.ico"` goes through
`IconTypeConverter.CreateIconFromPath`, and it is the converter's resolution of a
*relative* path that does not survive — which is why fully qualifying the URI in the
XAML attribute (tried in an earlier session, reverted) did not help. Loading the same
file by absolute `avares://` URI through `AssetLoader` in code skips the converter
entirely; that is what pgNimbus has always done for its window icons, and why its AOT
binaries start. `WindowIcons.Apply` does it here, and the `Icon=` attributes are gone
from both windows. The published binary was launched and showed a real window.

**Verified this session**: build with **0 warnings**, **155/155 TUnit, 0 skipped** (the
10 new catalog/docs tests among them), all **84** screenshots (42 scenarios × both
themes) including the two new windows, the **win-x64 NativeAOT publish** with no new
warnings beyond the known DataGrid IL2104/IL3053, and that binary launching. pgNimbus
was rebuilt and re-rendered against the moved styles (30 screenshots) and is unchanged.

**Not verified**, in rough priority order: nothing in this pass has been driven by hand
against a live cluster — the preferences page's kubeconfig add/remove, the sidebar
toggle at various window widths, Ctrl/Cmd+, and Ctrl/Cmd+B, and above all **changing
the hotkey scheme while the app is open** (the rebuild-on-`Changed` path: bindings,
cheat sheet and tooltips all have to re-render, and `BuildKeyBindings` clearing first is
the part that would fail quietly). The linux/osx NativeAOT binaries should also be
re-published to confirm the icon fix unblocks them too — the diagnosis says it will,
but only win-x64 has actually been run. And the macOS half of UI rule 15/16 (traffic
lights beside the new left cluster, DWM caption colour has no macOS equivalent) is
untested, as ever.

**Launch-check pass (VER-2):** the gap the two paragraphs above describe is now closed
mechanically rather than by remembering. CI and the release workflow **run** every
binary they publish; see "The launch check (`--smoke-test`)" under Verification
workflow for the design, and the Releasing section for where it sits in the matrix.
New: `src/KubeNimbus.App/SmokeTest.cs`, a `--smoke-test` flag on `Program.Main`, one
`SmokeTest.Attach(desktop)` call in `App.OnFrameworkInitializationCompleted`, a launch
step in `ci.yml`'s `aot` job, and three OS-conditional launch steps in `release.yml`
placed between Publish and Stage.

**The negative test is the deliverable, and it was actually run.** Restoring
`Icon="/Assets/app.ico"` on `MainWindow` and re-publishing linux-x64 AOT produced a
publish that was *indistinguishable from a healthy one* — same two DataGrid
IL2104/IL3053 warnings, exit 0 — and the launch check then failed it:

```
SMOKE-FAIL (66) startup threw System.IO.FileNotFoundException: The resource /Assets/app.ico could not be found.
   at Avalonia.Platform.StandardAssetLoader.OpenAndGetAssembly(Uri, Uri)
   at Avalonia.Markup.Xaml.Converters.IconTypeConverter.CreateIconFromPath(ITypeDescriptorContext, String)
   at KubeNimbus.App.Views.MainWindow.InitializeComponent(Boolean)
STEP EXIT=66
```

The break was reverted and `MainWindow.axaml` re-verified byte-identical to HEAD. The
watchdog was proven separately (`KUBENIMBUS_SMOKE_TIMEOUT_SECONDS=1` against a ~1.4 s
Debug start → exit 67), and a no-flag launch was confirmed unchanged: the window is
still there under `xdotool search --name kubeNimbus` and the process still waits to be
closed.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 new warnings** (one
pre-existing CS8425 in `AsyncMergeTests.cs`, untouched), **155/155 TUnit, 0 failed, 0
skipped** via `--project` (no sandbox here, so that is the unit-only subset — the
cluster-gated tests return early), all **84** screenshots (42 scenarios × both themes),
the linux-x64 NativeAOT publish with no new warnings beyond the known DataGrid pair,
and the launch check itself against that published binary — `SMOKE-OK main window
rendered at 1280x800 after 146 ms`, exit 0, under Xvfb.

**Not verified here, and it is the majority of what this pass adds**: the win-x64,
linux-arm64 and osx-arm64 legs of `release.yml` have never executed — this container is
linux-x64 only, and it has no `pwsh`, so even the Windows step's PowerShell was not
syntax-checked (the YAML around it was). Two platform assumptions are therefore
untested and are the first things to watch on the next tagged build or a
`workflow_dispatch` dry run: that a GitHub Windows runner's session lets a GUI-subsystem
process create a window at all, and that an unbundled (no `.app`) osx-arm64 binary can
open an NSWindow on a macOS runner. Both are expected to work and both would show up as
a *failed launch check* rather than a bad release, which is the right way round — but a
dry run is much cheaper than finding out on a tag. VER-1 is the item that will confirm
the three non-Windows RIDs actually start once these runners exist.

**linux-x64 is now confirmed on a real runner (VER-1, partial).** The merge of the pass
above pushed to `main`, which ran `ci.yml`, which now carries the launch check — so the
first real-runner evidence arrived as a side effect of landing it. CI run
[31902245451](https://github.com/Shman4ik/kubeNimbus/actions/runs/31902245451) at commit
`961b085`, job *NativeAOT publish (linux-x64)* on `ubuntu-latest`, step **Launch check
(linux-x64)** — conclusion `success`:

```
[smoke 0 ms] launch check starting (timeout 90s)
[smoke 793 ms] main window opened
SMOKE-OK main window rendered at 1280x800 after 794 ms
```

Two things this settles beyond "the step passes". The published linux-x64 AOT binary
**starts and composites a frame on a machine that is not this container**, which is the
half of the `WindowIcons.Apply` fix that had only ever been argued from a diagnosis; and
794 ms on a cold hosted runner (against ~100–150 ms locally) is the number to compare
future runs against before reading a slow start as a regression.

**`linux-arm64` and `osx-arm64` remain unconfirmed, and cannot be confirmed from here.**
Those legs live only in `release.yml`, which runs on a tag or a `workflow_dispatch`.
NativeAOT cannot cross-compile, so this linux-x64 container cannot build either one, and
the GitHub App token this repo's agents run under lacks `actions: write` — a dispatch
returns `403 Resource not accessible by integration`. So the remaining two thirds of
VER-1 need one of exactly two things: a human pressing **Run workflow** on `release.yml`
with `dry_run: true`, or `actions: write` granted to the integration. Until then the
release workflow still ships two RIDs on a diagnosis rather than an observation — which
is the same shape of gap that produced the v0.1.0 breakage, and the reason VER-1 is
recorded as `blocked` rather than quietly closed on one passing RID.

**Workload-actions pass (FEAT-1):** the app's first mutating actions beyond a YAML
apply/delete — scale, `rollout restart` and delete-a-pod, on the row context menu and in
the palette, each armed rather than fired. See "Mutating workload actions" above for the
six rules and UI rule 17 for the strip they all share. New: `WorkloadActions.cs` and
`ClusterClient.Workloads.cs` in Core (+ 13 `WorkloadActionsTests`), `Subresources`/`Verbs`
on `ResourceDescriptor` with the discovery parser to fill them, `RowActionViewModel` and
the strip in `ClusterTabView`, two app-local icons, two context-menu items, three palette
entries and four screenshot scenarios (`cluster-tab-row-action-{scale,restart,failed}`,
`cluster-tab-demo-scale-unavailable`).

Two things worth keeping from doing it:

- **The strip rendered as nothing, silently, and compiled clean.** A `Border` that both
  set `DataContext="{Binding PendingRowAction}"` and declared
  `x:DataType="vm:RowActionViewModel"` compiles — `x:DataType` re-roots that element's
  *own* bindings too, so the DataContext binding itself was resolved against the wrong
  type — and produces an invisible panel with no error anywhere. Only the screenshot
  showed it. It is a `ContentControl` + inline `DataTemplate` now (UI rule 17).
- **A fixture tab has no `Client`, so the three commands refuse in the harness — and are
  right to.** That is the disconnected case they must not act in. The fixture scenarios
  therefore build `RowActionViewModel` directly against the offline client, exactly as
  the exec/YAML/Helm scenarios build their inspector tabs; the demo scenario is the one
  that goes through the real command, because the demo path is designed to work without
  a client.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings** (the
pre-existing CS8425 in `AsyncMergeTests.cs` reappears only on a from-scratch test-project
build and is untouched), **168/168 TUnit, 0 failed, 0 skipped** via `--project` — the 13
new ones among them, and 0 skipped here means the unit-only subset, since the
cluster-gated tests return early with no sandbox — all **92** screenshots (46 scenarios ×
both themes), the linux-x64 NativeAOT publish with no new warnings beyond the known
DataGrid IL2104/IL3053, and the launch check on that published binary under Xvfb
(`SMOKE-OK main window rendered at 1280x800 after 442 ms`, exit 0).

**Not verified, and it is the whole live half.** No cluster came up here (Docker's blob
CDN is blocked by this session's egress policy), so **not one of these three actions has
been run against an API server**: the scale patch, the restart annotation and the delete
are all argued from the wire format and pinned by unit tests, never observed. Nor has any
of it been driven by hand in the running app — the context menu items, the palette
entries, the replica box's keyboard behaviour and the `ConfirmDeletes: false` path (which
fires the delete straight from the menu) were all verified by construction or in the
headless harness. First things to do on a machine with a sandbox: scale
`demo-shop/shop-web` up and down and watch the list follow it; restart it and confirm the
pods roll rather than all disappear at once (`kubectl get pods -w` beside it), and that
`kubectl get deploy shop-web -o yaml` shows the `restartedAt` annotation; restart twice
inside one second and confirm the second is a no-op, as it is for kubectl; and check the
403 path with a `kubectl --as` impersonated user that cannot patch.

**Row-filter regression pass (VER-5):** UI rule 13's central invariant — `Rows` is the
watch's own complete list, `VisibleRows` is the rendered projection — had no automated
check, and could not have had one: the App layer had no test project, and the rule's
code cannot move to Core. New: `tests/KubeNimbus.App.Tests` (see "View-model tests"
under Verification workflow for why it is shaped the way it is) and
`ClusterTabRowFilterTests` in it, 13 tests over the real `Apply`/`ApplyFleet`.

**The demonstration is the deliverable here, not the passing run.** A regression test
for an invariant is worth exactly what it costs to break the invariant and watch it
fail, so both wrong implementations the item names were actually written and run:

- Making `RebuildVisibleRows` filter `Rows` in place (removing non-matching rows from
  `Rows` *and* `_rowsByKey`) turned **9 of 13** red. The headline one reported
  `Expected "api-7f9, cache-0, web-1" but received "api-7f9"` — `Rows` had been cut
  down to what was on screen — and `Repeated_modifications_…` reported `Expected 3
  but found 2`, which is the resurfacing itself: the key map had lost `cache-0`, so
  the next Modified for it built a fresh row and added it back.
- Making the watch-apply path drop rows the filter no longer matches turned **5 of
  13** red, the headline one reporting `Expected "api-7f9, cache-0, web-1" but
  received "api-7f9, web-1"`.

Both were reverted and the file diffed back to its intended state. What this says about
the assertions: the ones that catch this are on `Rows`, on row *object identity* across
a Modified, and on `RowFilterSummary` ("1 of 3", which a list filtered in place prints
as "1 of 1") — asserting only on `VisibleRows` would have passed under both breaks,
because a filtered-in-place list still renders correctly until the next event.

Two smaller things the pass settled. `IsListEmpty`/`IsFilterEmpty` are pinned as three
states, not two, including the loading one — and writing that test showed a bare
`ClusterTabViewModel` reports `IsListEmpty == false`, because `RecomputeListEmpty` has
never run; that is right (a tab that has never watched anything has no list to be
empty), so the test reaches the settled-empty state through a `Reset`, the way an empty
namespace's initial sync does. And `RowFilter` clearing on a kind change *is* cheaply
reachable — through the real `SelectKindCommand`, whose `RestartWatch` returns
immediately with no client, which is the disconnected state and not a contrivance.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **1 warning, and it is the
pre-existing CS8425 in `AsyncMergeTests.cs`** — 0 new; **168/168 Core TUnit, 0 failed, 0
skipped** (no sandbox here, so that is the unit-only subset — the cluster-gated tests
return early) and **13/13 App TUnit**, both via `--project`; the break/revert runs above;
and all **92** screenshots (46 scenarios × both themes) still render, run as the XAML
smoke test rather than for anything visual — nothing here changes a pixel and no
committed PNG was touched. **Not verified**: no NativeAOT publish was run — the change
adds no package, no binding and no serialization, and the only App-code edit is two
`private` methods becoming `internal`, which the trimmer treats identically; and nothing
was driven against a live cluster, because there is nothing here to drive (these tests
replace the watch, they do not exercise it). The one thing a reviewer should weigh rather
than take on trust is that `InternalsVisibleTo` line: it is the price of testing the real
watch-apply path instead of a copy of it.

**Terminal-launch pass (FEAT-16):** "open a terminal on this cluster" — the daily
gesture people leave a GUI for, and the last thing this app made you go and do by hand.
See "The machine's own terminal" above for the six rules. New: `TerminalLauncher.cs` in
Core (+ 21 `TerminalLauncherTests`), `ClusterTabViewModel.OpenInTerminalCommand` and the
notice `infoBar` it lands in (+ 6 `ClusterTabTerminalTests`), a `CommandId.OpenTerminal`
catalog entry (palette-only, so `docs/keyboard-shortcuts.md` is unchanged), a ☰ menu
item, and two screenshot scenarios — `cluster-tab-terminal-no-kubectl` and
`cluster-tab-demo-terminal-unavailable`, the second of which runs the real command end to
end.

**The thing this pass learned, and the reason two platforms do not use the obvious
command:** `wt.exe` and `open` both hand the request to *another* process, which is what
then spawns the shell — so the shell inherits **that** process's environment rather than
the one we so carefully set. On Windows Terminal this is the monarch/peasant model
(a second tab is created inside the already-running window); on macOS it is
LaunchServices. Either way the terminal opens looking correct and pointed at whatever
cluster that process happened to start with, which is the single failure this feature
must not have. Rule 4 of that section is the answer; it is worth re-reading before
"simplifying" the Windows path back to `wt.exe`.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 new warnings** (the
one warning is the pre-existing CS8425 in `AsyncMergeTests.cs`), **190/190 Core TUnit,
0 failed, 0 skipped** and **19/19 App TUnit**, both via `--project`; all **48** scenarios
× both themes rendered (96 PNGs), including the two new ones in light and dark; the
linux-x64 NativeAOT publish with no new warnings beyond the known DataGrid
IL2104/IL3053; and `--smoke-test` on that published binary under Xvfb (`SMOKE-OK main
window rendered at 1280x800 after 522 ms`, exit 0).

**And the launcher itself was actually run on Linux**, which the unit tests deliberately
do not do: a scratch harness put a fake `xdg-terminal-exec` on PATH that records its own
environment, and the child process was confirmed to receive
`KUBECONFIG=<overlay>:<real file>` and `KUBENIMBUS_CONTEXT`. The overlay written to disk
was confirmed to contain a context name and no credential. And the merge claim the whole
design rests on was checked against an independent implementation of it — the Kubernetes
client's own `$KUBECONFIG` chain handling: with the overlay first the resolved namespace
is the pinned context's (`payments`), without it the real file's own `current-context`
wins (`staging`), and the real file is byte-identical afterwards. Removing the fake
terminal and adding a fake `kubectl` produced the other outcome, `NoTerminal` with
`KubectlMissing` false.

**Not verified, and it is most of "all three platforms"**: no Windows box and no macOS
box, so `pwsh.exe`/`powershell.exe`/`cmd.exe` starting with a visible console from a
`WinExe` parent, and `open -a Terminal <script>` running the generated `.command` and
landing in a login shell, are both argued from the platform docs and have never been
run. Nor has any *real* terminal emulator been driven — this container has none, so the
Linux path is verified against a shell script standing in for one, which proves the
environment is handed over but proves nothing about a client/server emulator forwarding
it (gnome-terminal's D-Bus `environ` forwarding is the specific thing to watch). No live
cluster either, so nothing has been checked by typing `kubectl get pods` in a window this
feature opened — which is, in the end, the acceptance criterion.

**Hotkey-scheme drive-through (VER-3):** the Ctrl/Cmd preference had never been changed
with the app running — the whole re-render path (key bindings, the F1 sheet, every
`CommandTip` tooltip) was argued from the code and from the comment on
command-catalog rule 2, and `BuildKeyBindings` clearing first was the part that would
fail with nothing on screen to say so. It was driven, under Xvfb, and **it works** —
nothing was broken and nothing needed fixing. What this pass adds is the evidence and a
regression check, because "we read the code and it looked right" is exactly what was
already true before it.

**Linux is a real test bed for this, which is not obvious.** `Nimbus.Ui.Hotkeys.Resolve`
only consults the platform for `"auto"`; an explicit `"mac"` resolves to
`KeyModifiers.Meta` everywhere, and Avalonia's X11 backend maps Mod4 (Super) onto Meta.
So `xdotool key super+k` **is** the Cmd chord here, and the scheme is fully observable
without a Mac. What Linux cannot show is whether a real macOS keyboard's Cmd reaches the
same place — that is still untested.

Driven with `Xvfb :99` + `xdotool` + `import`, against a Debug build with
`XDG_CONFIG_HOME` redirected to a scratch dir, no kubeconfig (so the shell's empty state,
then the demo cluster). Preferences → Shortcut modifier → **Cmd**, with the window open,
and then, in order:

- **Bindings re-render.** `super+k` opened the palette; `super+p` opened the switcher;
  `super+f` focused the list search box and typing `redis` filtered the demo pods to
  "1 of 6"; `super+b` collapsed the sidebar. The command bar's palette pill relabelled
  itself from `Ctrl+K` to `Cmd+K` without a restart, and the switcher's own footer hint
  to `Cmd+1…9 jump to tab` — both are set by `BuildKeyBindings`, so they double as a
  witness that it ran.
- **The F1 sheet re-renders.** Every cap redrew as `Cmd` (`Cmd P`, `Cmd F`, `Cmd R`,
  `Cmd S`, and the note `Cmd+1 … Cmd+9`) — and the exec-pane rows still read `Ctrl C`
  and `Ctrl D`, which is command-catalog rule 5 holding where it is actually read.
- **Tooltips re-render.** The sidebar toggle read `Show or hide the resource sidebar
  (Cmd+B)` and the cog `Preferences… (Cmd+,)`. So did the switcher button's
  `SwitcherTooltip`, which is *not* a `CommandTip` and has no `Hotkeys.Changed`
  subscription at all — it survives because `ToolTip.Tip` holds a `TextBlock` whose
  binding is re-evaluated each time the popup is attached. Worth knowing that it works
  for a different reason than the other three: a future tooltip that caches its text
  would not.
- **The old gesture stops working.** `ctrl+k`, `ctrl+f` and `ctrl+b` all did nothing —
  no palette, an untouched search box, an unchanged sidebar. The reverse direction was
  driven too (back to **Auto**): `super+k` went dead and `ctrl+k` came back, with the
  labels following.

**The regression check, and what it can and cannot cover.** `HotkeySchemeTests`
(`tests/KubeNimbus.App.Tests`) pins the four, and the reason it needed a small seam is
that a `MainWindow` cannot be constructed without a running Application — so the clearing
rebuild moved out of `MainWindow.BuildKeyBindings` into
`CommandBindings.RebuildWindowBindings(IList<KeyBinding>, …)`, which a test can drive
twice over one list. The window keeps the half that is genuinely its own (the commands
that act on it, the two labels). `CommandTip.Compose` gained a control-free overload for
the same reason. Both breaks were written and confirmed red before the tests were called
done, same discipline as VER-5:

- Deleting `bindings.Clear()` — **3 of 27 red**, reporting `Expected to be 16` (the list
  had doubled), `KeyModifiers.Control` still present after switching to Meta, and
  `Ctrl+1` still bound. This is the silent failure the item names, and note that it
  breaks *nothing visible*: the new chord works and every label is correct.
- Caching `Hotkeys.PrimaryLabel` in a `static readonly` field in `ShortcutsViewModel` and
  `CommandTip` — the exact trap the shared resolver's own doc-comment warns about —
  **2 of 27 red**, on the cheat-sheet caps and the tooltip text.

What it does **not** cover, and could not: that `MainWindow` and `MainWindowViewModel`
still *subscribe* to `Hotkeys.Changed` at all. Deleting either subscription leaves every
test green and every projection correct-when-rebuilt; only the drive-through above catches
it. That is a window-level and shell-view-model-level wiring fact, and pinning it needs
`Avalonia.Headless` — which the App.Tests project deliberately does not start.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings** (the
pre-existing CS8425 in `AsyncMergeTests.cs` only appears on a from-scratch test-project
build and is untouched); **190/190 Core TUnit** and **27/27 App TUnit**, 0 failed, 0
skipped, both via `--project` (no sandbox here, so the Core count is the unit-only subset
— the cluster-gated tests return early); the two break/revert runs above; all **48**
scenarios × both themes rendered (96 PNGs, as the XAML smoke test — nothing here changes
a pixel and no committed PNG was touched); the linux-x64 NativeAOT publish with no new
warnings beyond the known DataGrid IL2104/IL3053; `--smoke-test` on that published binary
under Xvfb (`SMOKE-OK main window rendered at 1280x800 after 107 ms`, exit 0); and the
**whole drive-through re-run against the refactored build**, since the first pass had
verified the code the refactor then moved.

**Not verified**: no macOS or Windows box, so the scheme has only ever been exercised
with X11's Meta standing in for Cmd — the `"auto"` branch resolving to Meta *because the
platform is macOS*, and a real Cmd keypress arriving as `KeyModifiers.Meta` on
`Avalonia.Native`, are both still argued rather than observed. No live cluster (the demo
cluster was used for the list-search half), so `Ctrl/Cmd+R`'s refresh and `Ctrl/Cmd+S`'s
YAML apply were not driven under the changed scheme; both are ordinary catalog rows in
the same rebuilt list as the four that were. And the drive-through is manual: there is no
automated Xvfb gesture test, so this evidence is a session's record, not a check that
re-runs.


**Exec terminal pass (FEAT-10):** the exec pane renders a real VT emulator instead of
stripping ANSI, so the full-screen tools people exec in for work at all. See "The exec
terminal" above for the seven rules, the upstream defect it found, and the vendoring
fallback. New: a `SvcSystems.UI.Terminal` package reference (→ `XTerm.NET/1.0.15` →
`Unicode.net`, `Wcwidth` — the graph the research predicted, exactly),
`ExecTabViewModel` rewritten around `TerminalControlModel` (bytes in on the same 50 ms
flush tick, `UserInput` bytes straight back to `StdIn`, the emulator's own cols/rows to
`ResizeAsync`), `ExecView` down to one chrome row plus the terminal, four terminal
palette resources in `Styles/Theme.axaml`, two `ExecCopy`/`ExecPaste` catalog rows
(Ctrl+Shift+C/V — the docs golden file regenerated), and two new screenshot scenarios
(`cluster-tab-exec-fullscreen`, `cluster-tab-exec-no-shell`). **Deleted**:
`Terminal/TerminalOutputBuffer.cs`, 419 lines of hand-written C0/CSI/OSC parsing whose
own doc-comment admitted it was "not a VT emulator — no addressable screen grid, no
colour attributes and no alternate buffer". Core is untouched: the WebSocket, the
channel-3 read and the bash→sh→ash probe are byte-for-byte what they were.

**`vi`, `top` and `mc` were actually run — against a local PTY, not a cluster.** There
is no live cluster in this container (Docker's daemon starts, but pulling `rancher/k3s`
still dies on a 403 from `production.cloudfront.docker.com`), so the acceptance
criterion was reached the only other way it can be: a scratch harness started `script
-q -c <program> /dev/null`, pumped its stdout through the *same* model the exec pane
feeds, wrote the control's `UserInput` bytes back to its stdin, and rendered the control
with Skia. `top` drew its full screen with columns aligned; `vim -u NONE -c 'syntax on'`
drew a YAML file with syntax colour, `~` filler and a status line, entered the alternate
buffer, and took typed input (`iHELLO` inserted, i.e. keystrokes round-tripped into a
real program); `mc` drew both panels, the box drawing, the menu bar and the F-key bar in
colour. That proves everything except the transport, which is the half that did not
change.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings** (the
pre-existing CS8425 in `AsyncMergeTests.cs` appears only on a from-scratch test-project
build and is untouched); **190/190 Core TUnit** and **27/27 App TUnit**, 0 failed, 0
skipped, both via `--project` (no sandbox, so the Core count is the unit-only subset —
the cluster-gated tests return early); all **50** scenarios × both themes rendered (100
PNGs), including the three exec ones; the linux-x64 NativeAOT publish with **no new
warnings beyond the known DataGrid IL2104/IL3053** — the new package contributes none,
which is what the research had claimed for 1.0.3 and now holds for 1.1.0 — and
`--smoke-test` on that published binary under Xvfb (`SMOKE-OK main window rendered at
1280x800 after 351 ms`, exit 0). The emulator's own behaviour was pinned by a headless
probe rather than by argument: `Ctrl+C → 0x03`, `Ctrl+D → 0x04`, `Tab → 0x09`,
`Up → ESC [ A`, `Enter → 0x0D`, every one `handled=true`; `ESC[2J ESC[H` clears and
homes, `ESC[3;10H` places text at row 3 column 10, `ESC[?1049h/l` enters and leaves the
alternate buffer and restores what was under it, and `ESC c` (what Reconnect sends)
empties the buffer. The same probe is where the reverse-video defect and the split-UTF-8
result above come from.

**Not verified, and the transport is the whole of it.** No exec session has been opened
against an API server with this pane: `ExecAsync`, the probe and `ResizeAsync` are
unchanged code, but "unchanged" is an argument, not a run. First things to do on a
machine with a sandbox: exec into `demo-shop/shop-web` and confirm the shell's prompt
arrives and `vi` opens; drag the dock splitter and confirm the remote PTY follows (the
resize is now the emulator's real geometry, and `stty size` inside the container is the
check); reconnect into a session left inside `vi` and confirm the reset lands; and
confirm Ctrl+C interrupts a `tail -f` rather than merely being marked handled. Nothing
has been driven by hand in the running app either — the pane cannot be opened without a
cluster, and the demo tab renders its unavailable notice instead — so focus-on-open, the
right-click menu and Ctrl+Shift+C/V are verified by construction and by the headless
probe, not by a mouse. And the reverse-video defect is upstream and unfixed; `top`'s
header renders unhighlighted today.

**CRD printer-columns pass (FEAT-2):** a CRD list now wears the columns the CRD itself
declares, so `kubectl get certificates` and this app's Certificate list show the same
thing. See "CRD printer columns" above for the seven rules and the two traps. New:
`PrinterColumns.cs`, `SimpleJsonPath.cs`, `ClusterClient.PrinterColumns.cs` and
`RelativeTime.cs` in Core (+ 39 `PrinterColumnTests`), `PrinterCells` on
`ResourceRowViewModel` with ten XAML-declared slot columns and
`ClusterTabView.ApplyPrinterColumns` (+ 10 `ClusterTabPrinterColumnTests`), a
cert-manager CRD and five Certificates in the demo dataset, printer columns on two of
the three sandbox CRDs, and two screenshot scenarios
(`cluster-tab-crd-printer-columns{,-wide}`). `RelativeTime` moved from beside the list
row into Core because a `type: date` column is an age too, and Core is where "format a
duration" belongs; nothing else about it changed.

**The negative half of the acceptance criterion was measured, not argued.** "Built-in
kinds are untouched" is the easiest thing here to break silently, so the whole harness
was rendered from a worktree at the parent commit and diffed byte-for-byte against this
one: **all 102 pre-existing PNGs are identical**, and the only new files are the four
this pass adds. That covers every list, inspector and shell scenario in both themes.

**The header collision is the bug worth remembering**, and only the screenshot found it.
A printer slot's header becomes a CRD author's string, and cert-manager calls one of its
Certificate columns **Ready** — which `ApplySummaryColumns` then matched as the grid's
own Ready column and hid, so the most important column on the list this feature exists
to fix was silently missing. `ClusterTabView.FixedColumns` now excludes the slots from
every header match. The same trap sits one column name away from Status, Details,
Restarts, CPU, Memory and Cluster.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**;
**229/229 Core TUnit** and **37/37 App TUnit**, 0 failed, 0 skipped, both via
`--project` (no sandbox here, so the Core count is the unit-only subset — the
cluster-gated tests return early); all **53** scenarios × both themes rendered (106
PNGs), including the two new ones; the byte-for-byte baseline diff above; the linux-x64
NativeAOT publish with no new warnings beyond the known DataGrid IL2104/IL3053; and
`--smoke-test` on that published binary under Xvfb (`SMOKE-OK main window rendered at
1280x800 after 423 ms`, exit 0).

**And `GetPrinterColumnsAsync` was actually driven over HTTP**, which the unit tests do
not do: a scratch harness stood a `HttpListener` up as an API server and connected a
real `ClusterClient` to it through a real kubeconfig. It asked for exactly
`/apis/apiextensions.k8s.io/v1/customresourcedefinitions/certificates.cert-manager.io`
and parsed the response into `[Ready, Secret, Issuer, Age]`; a **404**, a **403** and a
**500 returning HTML** each came back with zero columns and no exception; and a
core-group descriptor (`Pod`) made **no request at all**. That is the degradation
contract this feature rests on, observed rather than reasoned about.

**Not verified, and the live cluster is the whole of it.** No sandbox came up —
`dockerd` starts here but every registry is blocked by this session's egress policy
(Docker Hub's blob CDN answers 403, `registry.k8s.io` answers 403 on the manifest HEAD),
so the acceptance criterion's own wording — *a CRD-heavy cluster shows the same columns
`kubectl get` does* — has been reached against the demo dataset and a stand-in server,
never against a real API server serving a real CRD. First things to do on a machine with
a sandbox, in order: `kubectl get widgets.shop.kubenimbus.io -n demo-shop` and
`kubectl get backups` beside the app's own lists and compare column for column,
including the `-o wide` columns against the advanced view; confirm the Backup list's
"Last run" cell ticks on its own (it rides the shared age timer, which no test drives);
confirm the factory Widget — which declares no columns — still lists exactly as it did
before; and check a cluster with a real cert-manager or Flux installed, where the
condition-filter paths meet objects this repo did not write. Nothing has been driven by
hand in the running app either: the two screenshots are the whole of the visual
evidence, and a CRD with enough priority-0 columns to need horizontal scroll has not
been looked at on screen.

**One thing deliberately left**: the generated `design/screenshots/*.png` were not
regenerated. They differ from a fresh render today, but they differ at the parent commit
too — the Age column is a function of the real clock while the rest of the fixture is
pinned to `FixtureNow`, so those files drift by themselves. Nothing this pass changes
appears in any of them (the baseline diff above says so), and regenerating them would
commit a date rather than a change.

**Multi-pod logs pass (FEAT-3):** one workload's pods tail into one pane, colour-keyed
by pod — see "Multi-pod logs (one workload, one stream)" above for the eight rules, the
per-pod tail decision and the reason the merge is two-stage rather than a true k-way
one. New: `LabelSelector.cs` in Core (+ 15 `LabelSelectorTests`), a `labelSelector`
parameter on `WatchResourceAsync`/`ListResourceOnceAsync` and an `extraQuery` on the
watch engine, `WorkloadLogsTabViewModel` / `LogSourceViewModel` / `LogSourcePalette` and
`WorkloadLogsView` in the App layer (+ 12 `WorkloadLogsTests`), a row context-menu entry
and a palette entry, three demo `payment-service-report-generator` replicas across two
ReplicaSets with interleaving canned streams, and two screenshot scenarios
(`cluster-tab-workload-logs{,-filtered-empty}`).

**A second bug was fixed here because the new pane runs through the same binding**, and
it is the one recorded under "Log severity is three classes, not a brush binding":
`LogSeverityToBrushConverter`'s `UnsetValue` default case falls back to
`TextElement.Foreground`'s own opaque-black default rather than to the inherited
foreground, so every log line with no severity keyword rendered **invisible on the dark
theme** — most real output. Shipping the multi-pod pane over that converter would have
reproduced it in the new surface on day one. The converter is deleted and severity is
three style classes.

**Verified this session** — and this record is the verifier's own re-run rather than a
claim carried over, because the implementing session's report was lost to a restart
before it could be recorded: `dotnet build KubeNimbus.slnx` with **0 new warnings** (the
one warning is the pre-existing CS8425 in `AsyncMergeTests.cs`); **244/244 Core TUnit**
and **49/49 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox here, so
the Core count is the unit-only subset — the cluster-gated tests return early); all
**55** scenarios × both themes rendered (110 PNGs), including the two new ones; the
linux-x64 NativeAOT publish with no new warnings beyond the known DataGrid
IL2104/IL3053; and `--smoke-test` on that published binary under Xvfb (`SMOKE-OK main
window rendered at 1280x800 after 3718 ms`, exit 0). The acceptance criterion was
checked at two levels rather than asserted: `WorkloadLogsTests` drives the real
`Enqueue`/`Flush`/`OrderBatch` against a deliberately scrambled arrival order, and the
demo rollout scenario was read line by line off the rendered PNG in both themes — the
three replicas' lines interleave by their real RFC3339 instants rather than arriving
grouped by pod.

**Not verified, and the live half is all of it.** No cluster came up here (registry
egress is blocked in this container, as it has been in most sessions), so **not one byte
of this pane has crossed a real API server**: the `labelSelector` list+watch, the 50-pod
concurrency cap against a genuinely large ReplicaSet, a dropped pod-list watch
reconnecting, and above all an actual `kubectl rollout restart` watched through the pane
are argued from the wire format, pinned by unit tests and rendered from the demo
dataset — never observed. Nothing has been driven by hand in the running app either:
the pod chips, the follow toggle and the filter are verified by construction, by unit
test and in the headless harness, not by a mouse. And the pixel measurements quoted in
the severity section describe a *before* state whose code is now deleted, so they cannot
be re-measured from this tree; they are consistent with Avalonia's documented `UnsetValue`
semantics and with the fixed panes now rendering legibly in both themes, which is the
most that can be said from here.

**Node-operations pass (FEAT-4):** the node surface — detail plus cordon / uncordon /
drain. See "Node operations" above for the whole design, in particular the drain's
classification table and the partial-drain lifetime story, which is the constraint that
cannot be engineered away in a desktop client. New: `NodeActions.cs`, `NodeResources.cs`
and `ClusterClient.Nodes.cs` in Core (+ 32 tests across `NodeActionsTests` and
`NodeResourcesTests`), `NodeDetailTabViewModel` + `NodeDetailView` in the App layer, three
new `RowActionKind`s and the drain's options/plan/progress on the existing confirm strip,
three context-menu items and three palette entries, two app-local icons, three demo nodes
plus the five kube-system pods the drain's classification needs, and seven screenshot
scenarios (+ 14 `NodeActionTests` in `tests/KubeNimbus.App.Tests`).

**The demonstration is the deliverable, not the passing run**, same discipline as VER-5 and
VER-3. Five invariants were broken, run, and reverted:

- **Cordon patching `spec.schedulable` instead of `spec.unschedulable`** (the exact silent
  200) — **2 of 276 red**: `Expected to be equal to "{"spec":{"unschedulable":true}}"` and
  the same for the uncordon body.
- **Mirror pods no longer skipped, and `emptyDir` pods evicted without asking** — i.e.
  headlamp#7268 reproduced deliberately — **3 red**: `Expected to be equal to SkippedMirror`,
  `Expected to be equal to BlockedLocalData`, and the plan summary's `Expected to be 2`.
- **Init containers summed alongside the regular ones, and terminal pods counted** —
  **3 red**: `Expected to be within 0.0001 of 2` (the init-container floor),
  `…of 0.7` (the native sidecar), and `…of 0.5` (a node otherwise reading as full of
  finished Jobs).
- **The eviction body sent `apiVersion: v1`** — **2 red** on the byte-for-byte body.
- **`ArmRowAction` allowed a running action to be replaced** — **1 of 63 red** in the App
  suite: `Expected to be the same reference`, i.e. an eviction loop orphaned with nothing on
  screen reporting it.

**Two defects were found by looking at the rendered strip rather than by any test**, and
both are recorded in the section above: a compiled binding to a method group renders the
delegate's type name with no error anywhere, and the strip's target sentence read
"`Node/demo-worker-1 in `" for every cluster-scoped object (pre-existing — it applied to
deleting a PersistentVolume too). A third was caught the same way: "Stop draining" was drawn
*over* the still-visible Drain button, now one slot swapped on `IsPromptVisible`.

**The negative half of the demo-data change was measured, not argued.** Enlarging the shared
dataset is the one thing here that could silently rewrite a dozen committed images, so the
whole harness was rendered from a worktree at the parent commit and diffed byte for byte:
after scoping the three fixture list scenarios to the namespace they already claim to be
showing, **exactly two of the 110 pre-existing PNGs differ**, and both are intended — the
demo strip's notice now reads "Scale, restart, delete, cordon and drain …", cropped and
compared line by line. (`cluster-tab-workload-logs.dark` also flapped, and was confirmed to
flap between two renders of the *baseline* as well: its lines arrive on a timer.)

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 new warnings** (the one
warning is the pre-existing CS8425 in `AsyncMergeTests.cs`); **276/276 Core TUnit** and
**63/63 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox here, so the Core
count is the unit-only subset — the cluster-gated tests return early); the five break/revert
runs above; all **62** scenarios × both themes rendered (124 PNGs) plus the baseline diff;
the linux-x64 NativeAOT publish with no new warnings beyond the known DataGrid
IL2104/IL3053; and `--smoke-test` on that published binary under Xvfb (`SMOKE-OK main window
rendered at 1280x800 after 4535 ms`, exit 0).

**Not verified, and the live half is all of it.** No cluster came up — `dockerd` starts in
this container but the Docker Hub blob CDN answers 403 on the layer fetch
(`production.cloudfront.docker.com`), as in most sessions — so **not one byte of this has
crossed a real API server**: the cordon patch, the eviction POST, a real 429 from a
PodDisruptionBudget, a real 403 on `pods/eviction`, and the re-list loop actually watching a
node empty are all argued from the wire format, pinned by unit tests and rendered from the
demo dataset. Nothing has been driven by hand in the running app either — the pane, the
checkboxes and the Stop button are verified by construction, by unit test and in the
headless harness, not by a mouse. First things to do on a machine with a sandbox, in order:
cordon a node and confirm `kubectl get nodes` prints `SchedulingDisabled` and that nothing
schedules there; drain the k3s node's `demo-shop` workloads with `kubectl get pods -w`
beside it and confirm the pods roll rather than vanish at once; add a PodDisruptionBudget
that forbids the eviction and confirm the pane reads "blocked", stays honest and can be
stopped; confirm a static pod and the DaemonSet pods really are left behind; and check the
403 path with a `kubectl --as` impersonated user that cannot create `pods/eviction`. The
`design/screenshots/*.png` were deliberately **not** regenerated, for the reason the CRD
pass recorded: they drift by themselves because Age is a function of the real clock, so
regenerating them commits a date rather than a change.


**Apply-preview pass (FEAT-5):** the YAML editor's apply was blind — it sent the document
and reported what came back. It now asks the server what the apply would do and shows the
answer before anything changes. See "The apply preview (server-side dry run)" above for
the seven rules. New: `ResourceDiff.cs` and `PreviewApplyAsync` in Core (+ 21
`ResourceDiffTests` and 6 `ApplyPreviewHttpTests`), `ApplyPreviewViewModel` /
`DiffRowViewModel` and the panel under the editor (+ 9 `YamlEditorPreviewTests`), an
`AppSettings.PreviewApplies` preference with its own card on the preferences page, three
diff-row style classes, and two screenshot scenarios
(`cluster-tab-yaml-diff-{preview,no-change}`). No new gesture and no new always-visible
control, so `docs/keyboard-shortcuts.md` is unchanged.

**The request itself is observed rather than argued, which is new for this repo.**
`ApplyPreviewHttpTests` stands an `HttpListener` up as an API server and points a real
`ClusterClient` at it through a real kubeconfig, so `?fieldManager=kubenimbus&force=false&dryRun=All`,
the `application/apply-patch+yaml` content type, the 409, the 404-means-create and a 422
rejection are all things a test drove over HTTP. The pattern is worth reusing: several
items in `docs/BACKLOG.md`'s verification-debt section are "the wire format is argued,
never seen", and this closes that half of one of them without a cluster. What it still
cannot reach is the half that needs a real API server — defaulting, admission webhooks
and the server's own validation are precisely what the stand-in has no opinion about.

**Three breaks were written and confirmed red before the tests were called done**, same
discipline as VER-5 and VER-3. A preview that forgets `dryRun=All` — i.e. one that
silently *applies* what it claims to be previewing — turned 2 of 303 red on the query
string. Index pairing instead of the `name` merge key turned 4 red, including the
container-inserted-at-the-front case the rule exists for. A preview surviving the edit
that invalidated it turned 1 of 72 red. All three were reverted and the suites re-run.

**Two layout defects were found by looking at the rendered panel**, not by any test, and
both are in rule 7 above: an `Auto` row for the diff left the editor at zero height in
the default ~300px dock, and giving the editor a `MinHeight` instead overflowed the grid
so the header, the editor and the panel drew on top of each other. The star/`Auto`
row-height switch in `YamlEditorView.axaml.cs` is the fix.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**; **303/303
Core TUnit** and **72/72 App TUnit**, 0 failed, 0 skipped, both via `--project` (no
sandbox here, so the Core count is the unit-only subset — the cluster-gated tests return
early); the three break/revert runs above; all **64** scenarios × both themes rendered
(128 PNGs) plus a byte-for-byte baseline diff against the parent commit — of the 124
pre-existing PNGs exactly **two** differ, both `main-window-preferences.*` and both
intended (the new settings card), while `cluster-tab-workload-logs.dark` and
`cluster-tab-workload-logs-filtered-empty.light` were each confirmed to flap between two
renders of the *same* tree, which is ENG-10 and not this change; the linux-x64 NativeAOT
publish with no new warnings beyond the known DataGrid IL2104/IL3053; and `--smoke-test`
on that published binary under Xvfb (`SMOKE-OK main window rendered at 1280x800 after
448 ms`, exit 0).

**Not verified, and it is the half that needs a cluster.** No sandbox came up — `dockerd`
starts in this container but Docker Hub's blob CDN, ghcr.io and quay.io all answer 403, so
**no dry-run apply has crossed a real API server**. Everything specific to a live cluster
is therefore untouched by this evidence: that the API server accepts our apply body under
`dryRun=All` and returns the object it would store, that a defaulting or mutating webhook
shows up in the diff as this design claims (the single strongest argument for the feature,
and the one thing a stand-in cannot fake), that a real field-manager conflict raises during
the preview rather than only during the apply, and that the diff of a real Deployment is as
readable as the fixture's. Nothing has been driven by hand in the running app either: the
panel, its two buttons and the preference toggle are verified by construction, by unit test
and in the headless harness, not by a mouse. First things to do on a machine with a
sandbox, in order: edit a Deployment's image in the editor and confirm the preview names
that field and nothing else; add `resources: {}` to a container and see what the cluster
defaults into the diff; run `kubectl scale` on the same object from a terminal and then
apply from the editor, to reach the conflict path from the outside; and turn the preference
off and confirm Apply goes straight through as it did before.

**Manifest-diff pass (FEAT-58):** the apply preview's body is the manifest now, not a list
of field paths — the shape `kubectl diff`, `git diff` and VS Code's diff editor all show.
See "The apply preview (server-side dry run)" above for the five rules this added (8–12).
New: `TextDiff.cs` and `ResourceDiff.ToDiffableYaml` in Core (+ 18 `TextDiffTests`), a
`Live` object on `ApplyPreview` — which `PreviewApplyAsync` already read and discarded —
`DiffLineViewModel` / `DiffPairViewModel` and a `Diff / Split / Fields` strip on the panel's
existing chrome row (+ 7 more `YamlEditorPreviewTests`), six diff style classes in
`Styles/Theme.axaml`, and two screenshot scenarios (`cluster-tab-yaml-diff-split`, rendered
at the dock's default height on purpose, and `-fields`). No new gesture and no new
always-visible control, so `docs/keyboard-shortcuts.md` is unchanged.

**Three things were found by breaking them, and one by looking at the screenshot.** The
break/revert runs, same discipline as VER-5 and VER-3: replacing the LCS with index pairing
turned **1 of 321 red** — and the interesting part is that it turned *nothing* red until the
right test existed, because the prefix/suffix trim settles a lone insert, delete or replace
by itself (see rule 9); dropping the bookkeeping strip from `ToDiffableYaml` turned **1 of
321** and **1 of 79** red, in the Core and App suites respectively; and an off-by-one in the
collapse context turned **2 of 321** red. The screenshot found the fourth: an unchanged
document collapsed to one row reading `56 unchanged lines` underneath "this apply would
change nothing", and separately that an even split of the dock left the diff body at *zero*
height while the editor kept five lines nobody was reading — which is what the 3:1 row
weight in `YamlEditorView.axaml.cs` is for.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**; **321/321
Core TUnit** and **79/79 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox
here, so the Core count is the unit-only subset — the cluster-gated tests return early); the
four break/revert runs above; all **66** scenarios × both themes rendered (132 PNGs) plus a
byte-for-byte baseline diff against the parent commit — of the 128 pre-existing PNGs exactly
**four** differ, all of them the two `cluster-tab-yaml-diff-{preview,no-change}` pairs this
change is about, while `cluster-tab-workload-logs.{light,dark}` and
`cluster-tab-demo-pod-detail.light` were each confirmed to flap between two renders of the
*baseline itself*, which is ENG-10 and not this change; the linux-x64 NativeAOT publish with
no new warnings beyond the known DataGrid IL2104/IL3053; and `--smoke-test` on that published
binary under Xvfb (`SMOKE-OK main window rendered at 1280x800 after 1101 ms`, exit 0).

**Not verified.** The live half is unchanged from FEAT-5's and untouched by this pass — no
sandbox came up here, so no dry-run apply has crossed a real API server and the diff of a
*real* Deployment, with a real webhook's defaulting in it, has still only been argued. Two
things are this pass's own gaps rather than inherited ones: nothing has been driven by hand
in the running app, so the mode strip, the two-way `SelectedIndex` binding that carries the
mode across previews and the split view's horizontal behaviour are verified by unit test and
by rendering, not by a mouse; and the diff has only ever been rendered over fixture
documents of ~60 lines, so the collapse boundaries and the cell budget's fallback are pinned
by tests and have never been *seen* on a several-hundred-line CRD. The panel at the dock's
default ~300px shows about three lines of diff, which `cluster-tab-yaml-diff-split` records
honestly; whether that is enough, or whether the editor should give way entirely while a
preview is armed, is a judgement worth making in front of a real cluster.

**Pod-detail Overview pass (FEAT-43):** the pod's conditions, tolerations, node selector,
QoS class, priority class and each container's probes are structured sections now rather
than a trip to the YAML editor — see "Pod detail's Overview tab" above for the eight rules,
in particular the condition-polarity one, which is deliberately the *opposite* default from
the node surface's and has a third answer the node surface does not need. New:
`PodDetails.cs` in Core (+ 14 `PodDetailsTests`), an Overview tab at index 4 in
`PodDetailTabViewModel`/`PodDetailView` with `PodConditionViewModel` beside it (+ 4
`PodOverviewTests` in `tests/KubeNimbus.App.Tests`), conditions/tolerations/node
selector/QoS/priority/probes across the demo dataset's `payments` pods, the same states in
`scripts/manifests/10-shop.yaml` (which needed a `PriorityClass` — nothing in the sandbox
had one), and two screenshot scenarios. No new gesture and no new always-visible control,
so `docs/keyboard-shortcuts.md` is unchanged.

**Five breaks were written and confirmed red before the tests were called done**, same
discipline as VER-5 and VER-3. Claiming an unclassified condition type is positive — the
false-reassurance failure rule 4 exists to prevent — turned **1 of 335** red (`Expected to be
equal to Unclassified`). Dropping `op=Exists` from a toleration's rendering turned **2** red,
including the empty-key form that tolerates every node. Dropping the API server's own probe
timing defaults turned **1** red. In the App suite: stopping the probe section following the
container strip turned **1 of 83** red (`Expected to be empty`), and guarding the rebuild on
"have we rendered once" instead of on the fields' own text turned **3** red — the headline
one being `A_watch_tick_that_changes_a_condition_is_not_swallowed_by_the_rebuild_guard`,
which is the guard swallowing exactly the tick someone opened the tab for. All five were
reverted and both suites re-run.

**The negative half was measured, not argued.** Enlarging the shared demo dataset is the one
thing here that could silently rewrite committed images, so the whole harness was rendered
from a worktree at the parent commit and diffed byte for byte: of the 132 pre-existing PNGs,
**16 differ and every one of them is the new `Overview` chip in the tab strip** — the diff
bounding box on `main-window`, `main-window-about` and `cluster-tab-pod-detail-events` is a
~165×15 box at (513, 528), which is where that chip sits. `cluster-tab-workload-logs.{light,
dark}` were confirmed to flap between two renders of the *baseline itself* (ENG-10), and
`cluster-tab-demo-pod-detail` was confirmed to differ by one canned log line between a
full-harness run and a single-scenario run **on the baseline tree as well as on this one** —
its demo log replay is timer-driven, so it is the same nondeterminism and not this change.
No committed PNG under `design/screenshots/` was regenerated, for the reason the CRD and
node passes both recorded: Age is a function of the real clock, so those files drift by
themselves and regenerating them commits a date rather than a change.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 new warnings** (the one
warning is the pre-existing CS8425 in `AsyncMergeTests.cs`); **335/335 Core TUnit** and
**83/83 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox here, so the Core
count is the unit-only subset — the cluster-gated tests return early); the five break/revert
runs above; all **68** scenarios × both themes rendered (136 PNGs) plus the baseline diff;
the linux-x64 NativeAOT publish with no new warnings beyond the known DataGrid
IL2104/IL3053; and `--smoke-test` on that published binary under Xvfb (`SMOKE-OK main window
rendered at 1280x800 after 1078 ms`, exit 0).

**Not verified, and the live half is all of it.** No cluster came up — `dockerd` starts in
this container but Docker Hub's blob CDN still answers 403 on the layer fetch
(`production.cloudfront.docker.com`), as in most sessions — so **no pod this pane has
rendered has come from a real API server**. Everything specific to one is therefore
untouched by this evidence: that a real cluster's `status.conditions` carry the transition
times and messages this assumes, that the API server really does default all five probe
timings so the fallback is a fallback rather than the common case, that `status.qosClass` is
populated on every object worth reading, and above all that a *failing readiness probe* —
the acceptance criterion's real subject — reads usefully here against a pod that is actually
failing one. Nothing has been driven by hand in the running app either: the tab, its scroll
and the container strip switching the PROBES section are verified by unit test and in the
headless harness, not by a mouse. First things to do on a machine with a sandbox: open
`demo-shop/shop-web` and compare the tab against `kubectl describe pod` line by line; break
its readiness probe (point it at a path nginx does not serve) and watch `Ready` and
`ContainersReady` flip in the pane on the watch's own tick; cordon or taint the k3s node and
confirm the toleration list reads against it; and check a pod carrying a `DisruptionTarget`
condition (start a drain from the node pane and open a pod mid-eviction), which is the one
polarity in rule 4 that no fixture in this repo produces.

**Requests/limits pass (FEAT-44):** container requests and limits are visible text on pod
detail's Usage tab now, beside the current and peak readings and with the current usage as
a percentage of the limit — see "Requests and limits are text on the Usage tab" above for
the six rules, in particular the one that keeps the declared numbers out of the metrics
gate. This is a rendering change on numbers the app already parsed: `ReadContainerSpecs`
has been reading `spec.containers[].resources` since the metrics pass, and the only place
the result was rendered was a hover tooltip on the container chip. New:
`ContainerViewModel.CpuMeasuredText` / `MemoryMeasuredText` / `CpuResourceText` /
`MemoryResourceText`, `PodDetailTabViewModel.IsCollectingUsage` and its
`OnIsMetricsUnavailableChanged` partial, a restructured Usage tab (two `infoBar` notices
above the content rather than two panels instead of it), limits on the demo dataset's
report-generator `app` container, one new screenshot scenario
(`cluster-tab-pod-detail-usage-unset`), and 8 `ContainerResourceTextTests` in
`tests/KubeNimbus.App.Tests`. No new gesture, no new always-visible control, so
`docs/keyboard-shortcuts.md` is unchanged.

**Two breaks were written and confirmed red before the tests were called done**, the usual
discipline. Rendering a missing request or limit as a blank rather than in words — the UI
rule 9 failure this whole item is about in miniature — turned **2 of 91** red in the App
suite (`Expected "request 50m · no limit" but received "request 50m · limit "`). Gating the
declared line on `HasUsage`, i.e. making requests and limits follow the metrics gate after
all, turned **1 of 91** red. Both were reverted and the suite re-run.

**A third defect was found by looking at the rendered tab rather than by any test**: the
tab strip's window caption still read "collecting…" beside a notice saying this cluster
serves no metrics at all and never will. It is cleared from the changed-partial now.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**; **335/335
Core TUnit** and **91/91 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox
here, so the Core count is the unit-only subset — the cluster-gated tests return early);
the two break/revert runs above; all **69** scenarios × both themes rendered (138 PNGs);
the linux-x64 NativeAOT publish with no new warnings beyond the known DataGrid
IL2104/IL3053; and `--smoke-test` on that published binary under Xvfb (`SMOKE-OK main
window rendered at 1280x800 after 925 ms`, exit 0).

**The demo-dataset edit was measured, not argued.** The harness was rendered from a
worktree at the parent commit and diffed byte for byte: of the 136 pre-existing PNGs,
**four** differ and all four are the two usage scenarios in both themes (the new text, plus
the maximized dock). Three more flagged and are the known timer-driven flap, not this
change: `cluster-tab-demo-pod-detail.light` and `cluster-tab-workload-logs.light` were each
confirmed to differ between two renders of the *baseline itself*, and the light log shot
landed byte-identical to the baseline on a re-run; `cluster-tab-workload-logs.dark` differs
on every run but only in the interleave and scroll position of its timer-driven canned
streams — same three pods, same "24 lines", same content. That is ENG-10. The generated
`design/screenshots/*.png` were deliberately not regenerated, for the reason the CRD, node
and Overview passes all recorded: Age is a function of the real clock, so those files drift
by themselves and regenerating them commits a date rather than a change.

**Not verified, and the live half is all of it.** No cluster came up here, so no pod whose
requests and limits this pane has rendered came from a real API server — that a real
cluster's usage against a real limit lands where this arithmetic says, that a LimitRange's
defaulted requests show up here as the object's own (they will: they are written into the
spec on admission, but that is reasoning, not an observation), and that the percentage is
worth reading on a container actually approaching its cap are all untested against a
server. Nothing has been driven by hand in the running app either: the tab, its scroll at
the dock's default height and the container strip are verified by unit test and in the
headless harness, not by a mouse — and the default-height case is the one worth looking at,
since the per-container section sits below two full-width charts and the screenshots are
maximized precisely because of that. First things to do on a machine with a sandbox: open
`demo-shop/shop-web` and compare the two containers against `kubectl describe pod` and
`kubectl top pod --containers`; delete metrics-server and confirm the requests/limits stay
readable under the notice; and check a BestEffort pod from `40-broken.yaml`, which is the
"no request or limit set" line against a real object.

**Visual-audit pass (cognitive load, 2026-08-19):** a review of all 69 screenshot
scenarios in both themes, read off the rendered pixels rather than the code, followed by
the uncontroversial half of its own findings. The report is
`docs/research/2026-08-19-visual-audit.md`; the three substantive changes have sections
of their own above ("The status dot…", "The meter track was invisible…", "Sidebar labels
come from the server's plural…"). Also shipped: the YAML editor's duplicate title, the
demo tab's status bar repeating the `demoBar` verbatim, the access review's `namespace
<ns>` caption and its borderless `Verify`, and the preferences kubeconfig box that
rendered as an 88px empty rectangle with its own explanation sitting outside it. Two
condition states that no object in the repo could produce — `DisruptionTarget` (the one
type with inverted polarity) and an unclassified custom readiness gate — were added to
the demo dataset and are rendered by `cluster-tab-pod-detail-overview-disrupted`; both
branches had been reachable only in code.

**Four of the twelve planned fixes were withdrawn during implementation, and that is the
part worth keeping.** Each was a finding read off a render that was wrong about its own
cause: the YAML title row does *not* cost a row (it also holds Apply/Delete, so the gain
is horizontal); `BY CONTAINER` cannot move inside a card because it heads a list of
cards; the cheat sheet's arrow was already `→` and was misread from a downscaled image;
and the CONDITIONS card's 16px indent has no clean fix, because the dot cannot hang into
the card's 12px padding without sitting on the border. All four are filed rather than
forgotten (`ENG-27`, `ENG-28`).

**One regression was written, rendered and reverted**, and it is the argument for the
byte-diff in a sentence. Shortening the shell's `Status` so the no-kubeconfig status bar
would stop repeating the empty-state card's heading looked like removing a duplicate; the
card binds its *heading* to that same property, so the change replaced the app's own
diagnosis with "Searched 1 location(s) — see above." pointing at nothing above it. It
built cleanly and both suites stayed green. What caught it was diffing the render and
asking why a file had changed that had no business changing (`ENG-29`).

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 new warnings** (the one
warning is the pre-existing CS8425 in `AsyncMergeTests.cs`); **335/335 Core TUnit** and
**97/97 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox here, so the
Core count is the unit-only subset); and **140** PNGs rendered (70 scenarios × both
themes) with every changed file accounted for — 2 new, 2 unchanged (the only two
scenarios with no cluster tab, which is also the proof the reverted regression is fully
out), 12 whose diff is confined to the sidebar, and 124 that also show a list. The
cleanest single check is `cluster-tab-exec-fullscreen-maximized`, whose entire diff is a
16×12px box at (120, 513) — exactly the `…Policys` → `…Policies` tail.

**Not verified**: no live cluster (registry egress blocked), so everything here was read
on demo data and fixtures — which for a pass that is entirely layout, colour and text is
the right medium, but it means the CRD-heavy real-cluster cases the report calls out
(KEDA's eleven printer columns, a sidebar of 70 real CRD plurals now going through the
re-casing path) have still never been rendered. Nothing was driven by hand in the running
app. `design/screenshots/*.png` were deliberately not regenerated: Age is computed from
the real clock, so they drift by themselves.

**Resizable and sortable grid pass (FEAT-66):** the resource list's columns can be
dragged to any width and ordered by a header click, and both choices are remembered per
kind — see "The resource grid is the reader's to re-cut" above for the twelve rules,
including the two Avalonia behaviours the design turns on (a template column raises no
`Sorting` event unless it carries `CanUserSort`, and a drag rewrites a star column's
*ratio* while leaving an Auto column's declared width alone). New: `ResourceGridSort.cs`
(`ResourceColumn` ids + `ResourceRowComparer`) and `GridLayoutStore.cs` in the App layer,
`WorkspaceSettings.GridLayouts`, sort state and a sorted mirror in `ClusterTabViewModel`,
a `Tag` on every grid column with the code-behind's four `Apply*Columns` passes moved off
header matching onto it, `ApplyColumnLayout`/`ApplySortIndicator` in `ClusterTabView`, 24
new tests across `ClusterTabSortTests` and `GridLayoutStoreTests`, and one screenshot
scenario (`cluster-tab-list-sorted`). No new gesture and no new always-visible control,
so `docs/keyboard-shortcuts.md` is unchanged.

**The two Avalonia behaviours above were measured on a real grid rather than reasoned
about**, and the first cut of this design was wrong about both. A headless probe drove
actual pointer events at a real `DataGrid`: with `CanUserSort` left at its default the
`Sorting` event never fired at all (so a header click did nothing, which is also what it
did in this app before this pass), and a resize drag on a grid whose only star column was
the one being dragged moved nothing — a star column can only take width from another star
column, which is why the app's own list resizes and a two-column test grid did not. The
same probe was then pointed at the **real** `ClusterTabView` inside a real `MainWindow`:
clicking the Name header cycled ascending → descending → off with `Rows` unmoved
throughout, dragging its edge took it from `2*` to `5.6*`, switching to ConfigMaps gave
that kind its own declared widths back, and switching back to Pods restored both the
width and the Status sort. A second process started against the same workspace came up
with the list already ordered by Status and the Name column at `5.6*` — the restart half
of "the choice survives", read out of the file by production code.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 warnings**; **335/335
Core TUnit** and **121/121 App TUnit**, 0 failed, 0 skipped, both via `--project` (no
sandbox here, so the Core count is the unit-only subset — the cluster-gated tests return
early); the two break/revert runs above (sorting `Rows` instead of the projection turned
**5 of 121** red, including the informer's own order after a delete; sorting only on the
click rather than maintaining it turned **2 of 121** red, on the created-while-sorted and
the modified-while-sorted cases; rebuilding on the metrics poll instead of re-ordering in
place turned **1 of 121** red, on the Reset the DataGrid would have answered by scrolling
to the top); all **71** scenarios × both themes rendered (142 PNGs)
plus a byte-for-byte baseline diff against the parent commit — of the 140 pre-existing
PNGs **none** differ for any reason this change is responsible for. Two classes of
pre-existing nondeterminism do show up and were each pinned on the baseline tree itself:
the timer-driven log panes (`cluster-tab-workload-logs.{light,dark}` and
`cluster-tab-demo-pod-detail.dark`), which flap between two full runs of the *baseline*
and land byte-identical between a second baseline run and a second run of this tree; and
`cluster-tab-crd-printer-columns{,-wide}`, whose only difference is a demo Certificate's
`type: date` cell reading `214d` where the earlier run read `213d` — that column is
computed from the real clock, which is the same drift that keeps `design/screenshots`
from being regenerated; the linux-x64
NativeAOT publish with no new warnings beyond the known DataGrid IL2104/IL3053; and
`--smoke-test` on that published binary under Xvfb (`SMOKE-OK main window rendered at
1280x800 after 103 ms`, exit 0).

**Not verified.** No live cluster (no registry is reachable from this container), so
nothing here has ordered a list of real objects arriving from a real watch — which is the
one place rule 7 above earns its keep: a busy namespace producing Modified events several
times a second against a sorted list is a load and a jumpiness question that a fixture
cannot ask. Nothing has been driven by a real mouse either: the pointer events above are
synthetic (Avalonia headless), so the *feel* of the drag — the resize cursor appearing
over the separator, the hit zone being five pixels wide at 100% and at 150% scaling — is
unverified, and so is what a header click does on macOS with a trackpad. The persistence
is per kind and unbounded: a session that opens two hundred kinds and drags one column in
each writes two hundred entries into `workspace.json`, which nothing prunes. And the
committed `design/screenshots/*.png` were deliberately not regenerated, for the reason
every recent pass records: their Age column is a function of the real clock, so they drift
by themselves and regenerating them commits a date rather than a change.

**Argo CD pass:** GitOps in the navigator — an `Argo` sidebar section, a dashboard over
every Application on the cluster, an Application detail pane, and Sync / Refresh on the
shared confirm strip. See "Argo CD (GitOps in the navigator)" above for the nine rules,
in particular why a sync is a patch of the object's own top-level `operation` and why the
capability check names the kind. It is the feature Lens gated behind its paid tiers in
2026.8; here it is free, telemetry-free, and needs no Argo API server, no URL and no
second credential — Argo's objects are custom resources, so the whole integration is the
Kubernetes connection the app already has.

New: `ArgoCd.cs` and `ClusterClient.ArgoCd.cs` in Core (+ 29 `ArgoCdTests`),
`ArgoApplicationRowViewModel` / `ArgoApplicationTabViewModel` / `ArgoApplicationView` and
the dashboard in `ClusterTabView` (+ 16 `ArgoDashboardTests`), two `RowActionKind`s with a
prune option on the existing strip, two app-local icons, context-menu items on both the
dashboard and an ordinary Applications list, two palette entries, seven demo Applications,
a sandbox CRD *shape* plus five Applications, and three screenshot scenarios.

**Three breaks were written and confirmed red before the tests were called done**, the
usual discipline. Writing the sync into `spec` instead of the top-level `operation` — the
silent 200 that patches an Application and rolls nothing — turned **3 of 364** red. Making
sync outrank health in `AttentionReason` turned **1 of 364** red on the Synced-but-Degraded
case, which is the one the two-pill design exists for. Dropping the dashboard's attention
ordering turned **1 of 137** red in the App suite. All three were reverted and both suites
re-run.

**One defect was found by looking at the rendered pane rather than by any test**, and it
generalizes: maximizing the inspector sets the list row's height to 0, and a `Grid` does
not clip its children — the resource list and the Helm browser get away with it only
because a `DataGrid` clips itself, so the dashboard's summary card went on painting
straight through the maximized dock. `ClipToBounds="True"` is the fix and the rule for
anything else that ever occupies that slot.

**Verified this session**: `dotnet build KubeNimbus.slnx` with **0 new warnings** (the one
warning is the pre-existing CS8425 in `AsyncMergeTests.cs`); **364/364 Core TUnit** and
**137/137 App TUnit**, 0 failed, 0 skipped, both via `--project` (no sandbox here, so the
Core count is the unit-only subset — the cluster-gated tests return early); the three
break/revert runs above; all **74** scenarios × both themes rendered (148 PNGs) plus a
byte-for-byte baseline diff against the parent commit; the linux-x64 NativeAOT publish
with no new warnings beyond the known DataGrid IL2104/IL3053; and `--smoke-test` on that
published binary under Xvfb.

**The baseline diff is worth reading before the next pass touches the sidebar.** Of the
142 pre-existing PNGs, 91 differ **only in a 3px-wide vertical strip at x=309** — the
sidebar's scrollbar thumb, which got shorter because the catalog gained a section. 25 more
differ across the 288px sidebar column, which is that section rendering. Two are
`cluster-tab-demo-scale-unavailable`, where `RowActionViewModel.DemoNotice` now names the
Argo actions too, and six are the node-drain trio, where that same third line of notice
text pushes the whole strip and the list below it down. The rest are the known
timer-driven log panes (ENG-10), confirmed to flap between two renders of the baseline
itself. Nothing else moved.

**Not verified, and the live half is all of it.** No cluster came up — `dockerd` starts in
this container but the registries are blocked, as in most sessions — so **no Argo
controller has ever seen a patch this feature produced**. The sync's `operation` field, the
refresh annotation, prune actually deleting something, and a 403 on `applications` are all
argued from Argo's documented controller behaviour, pinned byte-for-byte by unit tests, and
rendered from the demo dataset. The sandbox manifests are a *shape* — a CRD with Argo's
group, kind, version and printer columns, and five Applications in fixed states — so they
prove the patch lands on the object and nothing more: nothing reconciles them, a refresh
annotation stays where it is put, and a sync writes `operation` that no controller picks
up. That is `VER-15`. Nothing has been driven by hand in the running app either: the
dashboard, the context menus, the prune checkbox and the detail pane's chevrons are
verified by unit test and by rendering, not by a mouse. And the committed
`design/screenshots/*.png` were deliberately not regenerated, for the reason every recent
pass records — their Age column is a function of the real clock, so they drift by
themselves.

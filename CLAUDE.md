# kubeNimbus — Claude working notes

Keep this file current in **every** PR, same discipline as pgNimbus. It is the
contract for how this repo is built; if a rule below changes, change it here in
the same change that breaks it.

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
with auto-update, winget/Store/Homebrew distribution, server-side dry-run,
schema-aware YAML completion and node drain. None of that is a reason to change
course; all of it is a reason not to write a comparison table yet.

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
  AvaloniaEdit for YAML), `CommunityToolkit.Mvvm` source generators
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
   filterable and collapsible. There are **six** sections, not five: `Cluster`
   was split out because Config had become the catalog's junk drawer. Measured
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
   most recently selected.
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
   fails first.

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
   catalog, sidebar, Helm), `DemoLogs` (canned streams), `DemoUsage` (replayed metric
   polls) — and `tools/Screenshot/FixtureData.cs` is now a passthrough to it. What a
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
   `LogSeverityToBrushConverter` null-brush bug, where plain lines rendered invisible.
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
  picked kubeconfig paths, log scrollback, metrics poll interval, delete confirmation.
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
   there is Copy. A test asserts both render as "Ctrl" even under the Cmd scheme.
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
the fleet toggle and Cluster column, the log toolbar's Wrap/Copy/Download, the
exec pane's Send button, YAML force-apply, the sidebar's kind-count badges, and
the Helm/RBAC palette entries. (The "Following &lt;container&gt;" caption used to
ride the switch too; it is gone outright — the container strip names the
container it is streaming and the log pane's own placeholder states say what the
stream is doing, so it was a row of dock height spent restating both.)
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
  WebSocket-based rather than SPDY and needed no reflection-based transport.
- **Port-forward** (`ClusterClient.PortForward.cs`) has no equivalent helper,
  so it opens a raw `WebSocketNamespacedPodPortForwardAsync` websocket per
  accepted local TCP connection (matching kubectl's own approach — the k8s
  websocket port-forward channel framing doesn't support multiplexing several
  local clients over one upstream connection) and pumps bytes with the
  channel-byte-prefix framing by hand.

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
groups** (the sidebar's group-aware filter), RBAC subjects including a dangling
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
  logs, Go `log.Print`, anything JSON. It returns `AvaloniaProperty.UnsetValue`
  now. It was never caught because every fixture log line contains a keyword.
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

# UI design rules

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "UI design rules" section keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

## UI design rules

> Rules **1, 2, 5, 8, 8b, 9, 11, 12, 14 and 22 are shared with pgNimbus**, and their
> canonical statement is in [`shared/nimbusUi/DESIGN.md`](../../shared/nimbusUi/DESIGN.md)
> (as its rules 1, 2, 3, 5, 6, 7, 8, 9, 12 and 21). What is kept below is the
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
   → details, service → its pods and endpoints, ingress → its routes, network policy →
   its rules, context → connect); Space = quick-peek. One exception: a row of the
   Applications list opens on one click, because that list is navigation and nothing in it
   acts on a selected-but-unopened row (`OpenApplicationsOnSingleClick`, on by default; see
   [applications-mode](applications-mode.md), rule 8).
3. **Multi-cluster via tabs** (like pgNimbus query tabs): each tab bound to a
   kubeconfig context; drag-reorder; workspace snapshot restores tabs. Reaching
   a cluster that isn't already a tab goes through the **cluster switcher**, never
   a list control — see "The cluster switcher" below.
4. **No hardcoded Ctrl gestures** — [`Hotkeys.cs`](../../src/KubeNimbus.App/Hotkeys.cs)
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
   classified as a built-in workload, which the old rule did. One CRD-installed
   group is filed with the built-ins on purpose: `gateway.networking.k8s.io`
   (Gateway API, the Kubernetes project's own successor to Ingress) is in
   Network, still by group, so its route kinds stop being more rows of CRDs —
   see [networking-detail](networking-detail.md).
   The filter matches display name, **API group and short names**
   (`SidebarKindViewModel.Matches`), because the group is the only thing
   telling two same-named CRD kinds apart and "svc"/"po" is how people think.
   A pinned **Recent** section (top, max 5) holds the kinds most recently
   selected, persisted per cluster in `workspace.json` as `<group>/<Kind>` keys and
   resolved against the next connect's catalog (ENG-5 — usage is open, look, close,
   so a session-scoped Recent was empty exactly when it would have helped; a kind the
   cluster no longer serves is dropped, and nothing is saved before the saved list has
   been read, which on a real cluster is after the early Pods list has already started).
   **Exactly one kind is drawn selected** — its own row and its Recent copy — and the
   highlight is derived from `SelectedKind` in `MarkSelectedKind`, never set by
   whoever selects (ENG-26): the palette, a restore, discovery's Pods swap and the
   screenshot harness all assign the kind directly, and a highlight only the sidebar
   command maintained is how two rows came to be lit at once. A Recent entry selects
   its canonical row, so clicking the Recent copy of the kind on screen is the same
   no-op as clicking the row. `SidebarRecentKindsTests` pins both. Two sections carry a **synthetic** row on top of the
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
   one-way height binding, which is why this is code-behind, not XAML. Esc returns a
   maximized inspector to the split (never from a text box, the YAML editor or the
   terminal, whose Esc is their own), and while it is maximized the collapsed list keeps
   focus but ignores its row keys — see
   [row-logs-and-maximized](row-logs-and-maximized.md).
8. **A click target must hit-test across its whole area, and say it is one.**
   In Avalonia a `Panel` or `Border` with a **null** `Background` does not
   hit-test where no child covers it, and a container's own `Padding` lies
   outside its content template entirely. A pointer handler on an item
   template's root panel therefore fires on the text and nowhere else — the
   row highlights on click but does nothing, which reads as "is this one click
   or two, or is it broken?". Handle taps on the **items control** and resolve
   the row from the event source (`OnSwitcherListTapped`), or give the target an
   explicit `Background="Transparent"`. The Ctrl/Cmd+K palette shipped exactly this
   (`Tapped` on the row template's `StackPanel`, so only a click on the text ran a
   command) until L1 moved it to the `ListBox`; `ux-logs-palette` clicks a row at its
   far edge, where no text is, and fails if it is put back. Anything clickable also gets
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
   (`AddNewTabCommand`'s `CanExecute`), never silently no-op. The same goes for a
   **failed connect**: it used to leave the content area blank with the reason in a
   status bar that does not wrap, and is now `ClusterTabViewModel.ConnectionFailure`,
   rendered in both modes by one `ConnectionFailureView` — see
   [connecting](connecting.md).
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
   - **The window's `MinWidth="960"` is a stated number** (ENG-35, reasoned in a comment
     on `MainWindow.axaml`): the narrowest window whose list header still holds every fixed
     control beside the 224px sidebar and whose bar still holds the caption strip. Below
     it something always-visible has to go, which is a design call, not a minimum to lower.
     The palette does not depend on it — `MaxWidth` 560 with a 16px gutter, so it follows a
     narrower window, and `palette-logs-narrow` renders it at 560px with the minimum lifted.
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
     would make "Running" match most of a healthy list. **Events add Reason, Object and
     Message**, and that is the same rule rather than an exception to it: an Event's own
     name is a generated `<object>.<hex>` nobody types, and what identifies an event to
     the person hunting for it is what happened, to what, and the sentence it logged —
     they identify an event the way a name identifies a pod. Type stays out ("Normal"
     would match most of the list). See [events-list](events-list.md).
   - **A search that matches nothing is its own state** (`IsFilterEmpty`), separate
     from `IsListEmpty`: "this namespace has no pods" and "no pod here is called that"
     send you looking for opposite problems. It names the query, says how many rows it
     filtered out of, and offers the way back. The filter is cleared when the selected
     kind changes — carrying "nginx" from Pods to ConfigMaps lands on an empty list
     that looks like a broken watch.
   - **Unhealthy only is a second narrowing through the same predicate, and it does not
     break "no status matching".** The chip beside the box (`IsUnhealthyOnly`, Ctrl+Z on
     the list) keeps rows whose computed `StatusHealth` is warn or error. It matches no
     text — it reads the verdict that colours the pill — so "Running" still matches
     nothing. Unlike the name, health changes under an object on a Modified that updates
     the row *in place*, which never reaches `Rows.CollectionChanged`; `RefreshRowVisibility`
     re-evaluates the row on every Modified, and `ClusterTabHealthFilterTests` pins both
     directions. It is a *mode* (kept across kinds, never persisted) where the text is a
     question (cleared), and its empty state is a third one. Full rules in
     [Unhealthy only](unhealthy-only.md).
14. **A `DataGridCell` needs a gutter on both sides.** Fluent's cell padding is
   left-only, which is invisible while every column is left-aligned and actively
   *misleading* as soon as one isn't. The resource list's Memory column is
   right-aligned, so its "—" placeholder landed hard against Age's "5d" and the pair
   read as `—5d`, i.e. a negative age; a real value did the same (`48 MiB16d`) and the
   CPU number touched the memory sparkline. `Style Selector="DataGridCell"` sets
   `10,0,10,0` in Theme.axaml. The gutter is not free — nine columns × 10px comes out
   of a fixed width, and the first cut pushed Age off the right edge at 1280px — so
   the column `MinWidth`s were re-cut to match (Name 136, Status 140, Ready 56,
   Restarts 78, CPU 98, Memory 106, Age 72, sparklines 34). Check `cluster-tab-workloads-list`
   at its rendered 1280px, which is narrower than most real windows and is where this
   fails first. When the columns do not fit, the grid squeezes fixed columns **from the
   right** to their minimums and only then scrolls sideways — so a minimum below what a
   header needs is a clipped header, not a scrollbar. That is why Age's minimum is its
   width (at 60 it read "Ag" in the fleet list, ENG-6) and why the fleet list's Cluster
   column is 120px regular weight, not 150 semibold; the Events list's minimums were
   re-cut the same way so each header keeps its sort arrow at 1024px (ENG-41, Namespace's
   floor raised for that list only, in `ApplySummaryColumns`). The harness asserts the
   narrow cases rather than leaving them to someone looking at a PNG
   (`tools/Screenshot/LayoutChecks.cs`: the list header's controls stay inside the window,
   the grid reaches its last column, the palette follows a narrow window). A CRD's own printer columns are a *variable* number of columns on that
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
   then the app's own middle, then search pill, theme, `⚙` preferences, `?`. This app's
   middle starts with the `Applications | Resources` mode switch (a segmented `ListBox`
   bound by index, with the `User` role like everything else in the bar); the sidebar
   toggle beside it is disabled, not hidden, in the Applications mode, so the switch never
   moves under the pointer. The help
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
   canonical text is [`DESIGN.md`](../../shared/nimbusUi/DESIGN.md) rule 13. The cheat sheet
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
   **Opening an overlay takes focus, closing gives it back** (2026-09-29, from pgNimbus,
   where a Mac's app-menu Settings left focus in the SQL editor under the scrim: Escape,
   which the panel handles at the `TopLevel` in the bubble phase, was answered by the
   editor first, and typed text landed in the document behind it). Same shared
   `OverlayPanel`, so the same fix; `UxInteractionChecks.OverlayTakesFocus`
   (scenario `ux-overlay-focus`) opens Preferences from a focused text box, types, and
   presses Escape. DESIGN.md rule 13 has the contract.
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
17. **A mutating action that destroys, disrupts or cannot be taken back arms a strip and
   never fires on the click that started it; one that can be taken back fires on the click,
   and the same strip is its result line.** Every mutating action lands on one
   `RowActionViewModel` rendered above the resource list, which names the object, holds the
   replica box when there is one, and carries the in-flight / succeeded / refused states in
   an `infoBar` (rule 11). One strip for all of them, because the confirm sentence, the busy
   state, the RBAC 403 and the success line are the same work many times over otherwise,
   and near-identical confirms are precisely how they drift apart.
   - **Asks first:** delete, drain, rollout restart (it rolls every pod), Argo's sync with
     prune (it deletes what Git no longer declares), a CronJob's run-now and its resume
     (each can start a Job straight away, and a Job's side effects do not come back).
     Scale is a form rather than a confirm: it needs a number before there is anything to
     send.
   - **Fires on the click:** cordon and uncordon (no running pod is touched, and each takes
     the other back), suspend (running Jobs carry on, and resume takes it back), Argo's sync
     without prune (it applies what Git already declares) and refresh (nothing on the
     cluster changes). Handing kubectl exec or a node shell to your own terminal fires on the click too: it is the user's own kubectl, and what it creates a delete takes back. `RowActionViewModel.FiresOnClick` is the list and
     `RowActionClickRuleTests` pins it kind by kind; `RunNow` sends the action and hides the
     question and the prompt row, so the strip is the in-flight line, then the outcome and
     Close. A refusal is that outcome too, since there is no prompt to go back to; trying
     again is the same click. A delete with "Confirm before deleting" off takes the same path,
     except on a production cluster, which always asks (see the paragraph below).
   - **An ellipsis in a label means it asks**, in menus, the palette and on buttons: "Sync",
     "Cordon", "Suspend" carry none; "Sync with prune…", "Drain…", "Restart…" do.

   The line used to be "every mutating action asks", and the owner reported the result on
   the Applications page (2026-10-07): **Sync…** opened a strip whose answer was another
   **Sync** button, a confirm for an action whose consequence is what Argo does on its own
   next reconcile. A confirm on everything trains the reader to click through it, which
   costs the confirms that matter their weight. Four alternatives to the strip were
   considered and rejected, and the reasons still hold: a **second window** is forbidden
   outright (rule 16); an **OverlayPanel** covers the very list the action is about, and
   rule 16b scopes overlays to shell-level surfaces; an **inspector dock tab** spends a
   third row of chrome inside a ~300px dock (rule 10) on a question with a one-word answer;
   and a **menu item that acts immediately with nowhere to report** puts a destructive verb
   one twitch away from Edit YAML and its 403 nowhere. The strip is present only while an
   action is armed or reporting, so it costs nothing the rest of the time (rule 1). It is
   one control, `Views/RowActionStrip`, hosted by the resource list and by the Applications
   page alike — the page's Restart and Sync use the very same strip, not a copy. It is
   docked *outside* `ContentRows` for the same reason the demo banner is — that grid's row indices are load-bearing for
   `ApplyDockState`. And it is a `ContentControl` + inline `DataTemplate`, not a `Border`
   with `DataContext` and `x:DataType` both set on it: `x:DataType` re-roots an element's
   **own** bindings too, so that combination compiles against the wrong type and renders
   *nothing at all*, silently — which is how the first cut of this shipped past the
   compiler and was caught only by looking at the screenshot.
   **The strip names the cluster, and production always asks** (security block 3, 2026-10).
   Its `Target` names the context in every view, by the name the switcher shows, and says
   "(production)" in words there, with the card's own border in the production colour; it
   used to name the cluster only in a fleet list. "Confirm before deleting" turned off skips
   the strip for a delete everywhere except a production cluster (classified or assigned),
   where it always asks — in a fleet list by the row's own cluster's environment. The YAML
   editor's own delete follows the same rule (`RowActionViewModel.DeleteNeedsConfirm`). Scale
   says "from N to M" as the box changes and warns, without blocking, about zero and a
   ten-fold jump. See [workload-actions](workload-actions.md).
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

19. **Both modes sit on the same surface and use the same table type.** The content area is
   the shell's own tone with each list, and the inspector dock, in a `card`; `layer`
   is for overlays only, which now draw on `overlayCard` over a `scrim` (it was
   Fluent's AltHigh, pure black in the dark theme, until nimbusUi's DESIGN.md rule 15
   gave every raised surface a grey that is lighter than its base). The Resources
   mode used to sit on a `layer`, so flipping the mode switch swapped the whole content
   area between a black panel and a grey one, and the owner preferred the Applications
   side. Its table type is now every grid's, in both apps: column headers small, semibold
   and dimmed like the Applications list's header row, with row rules at 10% grey — shared,
   [`DESIGN.md`](../../shared/nimbusUi/DESIGN.md) rule 14 — and, this app's own beside the rule-12
   gutter, cells at 12px with the row's name in semibold 13px. Two traps: the DataGrid's
   header paints its own AltHigh background, invisible on the old black panel and a black
   band on a card, which is why the shared style makes it `Transparent`; and a card behind a
   dock-state row needs `ClipToBounds`, because maximizing sets that row to zero height and a
   Grid does not clip.
20. **Nothing on screen repeats what is already on screen, and nothing states a fact it does
   not have.** Rule 1 applied to the chrome the design review of the surface pass found:
   - The cluster in front was printed twice in the command bar — on the switcher chip before
     the tabs and on its own highlighted tab. The switcher is a `+` after the last tab now,
     where a browser keeps "new tab" ([cluster-switcher](cluster-switcher.md)).
   - The status bar read "Connected — Kubernetes v1.31.2" for the life of every healthy tab.
     It is shown only while `ClusterTabViewModel.IsStatusWorthShowing` — anything but that
     routine line (recorded where it is written, never matched by wording), a warning, or a
     tab connected with `insecure-skip-tls-verify` or to a plain `http://` server, whose
     notice has a column of its own.
   - A cluster-scoped kind kept the namespace picker on screen, disabled, still reading the
     last kind's namespace: "Nodes  payments" looks filtered. It says "Cluster-wide" instead.
   - The Applications list's group caption shows only when two groups are on screen, and its
     Sync column and "Not in Argo CD" chip only when some application is an Argo one
     ([applications-mode](applications-mode.md)).
   - A pod's status is the same pill wherever a list names the pod (the main list, workload
     and node detail); a health dot beside a pill is the same verdict twice (status-dot.md).
   - A destructive button looks destructive and does not sit beside the primary one: the
     YAML editor's Delete is `soft danger`, across Reload from Apply.
   - The log panes' bar keeps what is read (Range, Follow, Previous, Levels, Copy, and the error/warning counts when there are any); the
     remembered display toggles, Clear and Save are in a `⋯` menu
     ([log-pane-reading](log-pane-reading.md)).
21. **A change to the UI updates the published screenshots in the same PR.** The README's
   gallery (`design/screenshots/`) and the Microsoft Store listing's set
   (`design/store/screenshots/`) are the first thing anyone judges the app by, and they
   drift silently: nothing fails when a screen they show changes. So a PR that changes
   what any of them shows — a surface, a control, a colour, a column, a label — re-renders
   the affected ones from the harness and commits them with the change, not in a follow-up.
   Each directory's README maps every file to its scenario and theme. Render them **on
   Windows**, where the interface is drawn in Segoe UI as Windows users see it (UI rule 23;
   the monospace panes used to be the reason, when they asked for Cascadia Mono or Consolas,
   and code is the bundled face everywhere now), and keep the balance each
   set states: the README hero in both themes, everything else half light and half dark.
   The Age column moving with the clock is not a UI change and is no reason to re-render.
   If a PR cannot render them (no Windows machine), it says so in its description and
   leaves a backlog issue, like any other verification debt.
22. **Every tooltip answers the pointer, and the status line keeps its whole text in one**
   (DESIGN.md rule 21, 2026-10). A `TextBlock` or panel with no `Background` is not
   hit-testable, glyphs included, so a tooltip on one never opens: the pointer lands on
   the row, card or header behind it. pgNimbus found it on its status line; the walk
   below then found 189 dead ones here on the day it was added (1,499 probes, most of
   them resource-grid cells: names, ages, the CPU and memory sparklines), in 130 of 186
   scenarios. `Nimbus.Ui.Controls.ToolTipHitTesting.Install()` in `App.Initialize` (not
   `OnFrameworkInitializationCompleted`, which the harness never reaches) is the fix, for
   every element at once: anything that gets a tooltip and has no background gets a
   transparent one as a current value, so a background set in markup or a style still
   wins. Don't write `Background="Transparent"` beside a `ToolTip.Tip`. A disabled
   control still shows none (Avalonia's choice; `ToolTip.ShowOnDisabled` opts in). The
   shell's status bar texts are `TextBlock.statusMessage`: one line, an ellipsis, and the
   whole text in a tooltip only while it is cut. They used to be clipped at the window's
   edge with no way to read the rest. `TooltipChecks` in the harness hit-tests the middle
   of every visible tooltip-bearing element in every scenario (light theme) and fails the
   run on any the pointer passes through.
23. **Fonts are settings, and no view writes a font name** (DESIGN.md rule 22, 2026-10;
   pgNimbus moved first). Three findings shaped the port:
   - **kubeNimbus was in Inter by accident.** `MainWindow.axaml` carried
     `FontFamily="{StaticResource InterFontFamily}"`, a resource nothing defines. On a
     control that reference is deferred and resolves to nothing, so it set nothing, and
     every window drew in Fluent's own `ContentControlThemeFontFamily`, which is Inter. (The
     same mistake in a *style* setter is not harmless, which is how pgNimbus ended up in the
     platform default instead; see the rule.) The reference is gone. The interface face is
     Fluent's key, set by `NimbusFonts.Apply` from `AppSettings.InterfaceFont`, and `"auto"`
     is the **system face**, as in pgNimbus: Segoe UI on Windows, San Francisco on macOS,
     the desktop's default on Linux. That changed how kubeNimbus looks everywhere, which
     pgNimbus's switch did not, and it was the owner's call: the two apps sit side by side on
     one desktop, and one family in two faces there is the drift the shared design system
     exists to stop. Inter is one choice away on the preferences page.
   - **Code had one spelling at 78 sites** (`Cascadia Mono,Consolas,monospace`, the
     terminal's with `DejaVu Sans Mono` too), and only Windows has either named face: a Mac
     got Menlo and a CI container DejaVu Sans Mono, which is why the published screenshots
     had to be rendered on Windows. 70 sites are the shared `mono` class now (appended to
     an existing `Classes`); the rest name the token where the class cannot go: the revision
     `Run` in the Applications list (`{DynamicResource MonoFont}`, spacing 0), the six app
     styles that are all code (`logSearchOption`, `logJson`, the diff gutter and text,
     `evidence`: `MonoFont` and `LetterSpacing` 0, as the class does), and **the exec
     terminal**. `TerminalControl` is a `Grid` with a `FontFamily` property of its own, not
     the inherited `TextElement` one, so neither the class's selectors (`TextBlock`,
     `TemplatedControl`) nor inheritance reach it: given the class, it drew in the interface
     face and `top`'s columns stopped lining up, which `FontChecks` caught in all seven exec
     scenarios on the first full run. `MonoFont` is the bundled JetBrains Mono NL
     unless `AppSettings.CodeFont` names an installed family. **Load order matters for the
     class**: a local `FontFamily` outranks every style, the inline stacks were local values,
     and the class is a style, so an app style that set `FontFamily` on one of those elements
     and loaded after the shared `Theme.axaml` would now win. None does; `FontChecks` fails
     the run if one starts to.
   - **The faces are applied from `App.Initialize`**, not `OnFrameworkInitializationCompleted`,
     for the reason rule 22 gives for the tooltip handler: the harness never gets a lifetime,
     and it should render what the app shows by default rather than the token file's
     fallback. Each capture re-applies them after deleting `settings.json`, since they are
     application resources and a scenario that changes one would otherwise reach every later
     capture. So the harness draws the interface in the machine's system face: DejaVu Sans in
     the Linux container, Segoe UI on Windows.
   The page lists the installed monospace families through `Nimbus.Ui.Fonts.MonospaceFonts`
   (lifted into nimbusUi from pgNimbus for this port: read through Skia, not Avalonia's
   `FontManager`, which would keep every face it opened for the life of the process), each
   row drawn in its own face; a saved family that is no longer installed stays listed and
   falls back to the bundled face. `FontChecks` in the harness reads every scenario (light
   theme): each `mono` element and the terminal draw in `MonoFont` (the class at spacing 0),
   every other text element in the interface face, `MonoFont` or `KeyCapFont`, and
   `ux-font-settings` changes both faces from the page with the window open and reads them
   back off the text. A face planted on one `TextBlock` fails the run, which was tried.
   `PreferencesFontTests` and `AppSettingsTests` hold what the page opens on and stores. Not
   checkable here: how San Francisco looks at 13px, which is release checklist row 28.

[fluent-basics]: https://learn.microsoft.com/en-us/windows/apps/design/basics/

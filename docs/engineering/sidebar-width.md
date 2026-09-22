# The sidebar is 224px and the reader can drag it

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


The catalog sidebar was a `0.65*` column, i.e. ~24% of the content area at every window
size. That is a defensible width at 1280px and absurd at 3840, where it spent over 900
pixels rendering the same ~200px of kind names — and the resource list, which is what
wants a wide window, got whatever was left. It is an absolute width now
(`AppSettings.DefaultSidebarWidth`, 224 DIPs: enough for `PodDisruptionBudgets` plus its
icon and count badge), with a `GridSplitter` in the 8px gutter the sidebar's own right
margin already left, so the handle costs no width of its own.

Five things are load-bearing.

1. **Column 0 is absolute and column 2 is star**, so a wider window goes entirely to the
   list. That is the whole reason for the change; making the sidebar a star column again
   undoes it.
2. **The bounds live on the `ColumnDefinition`, not on the splitter**, because that is
   where a GridSplitter reads them (`MinSidebarWidth` 150, `MaxSidebarWidth` 520). They
   are cleared while the sidebar is hidden — a `MinWidth` would hold the collapsed column
   open, and `ApplySidebarVisibility` collapsing the *column* rather than the panel is
   what UI rule 12's sidebar toggle has always depended on.
3. **The width is shell-owned and mirrored per tab**, exactly like `IsSidebarVisible`, and
   a drag writes back through `ClusterTabViewModel.SidebarWidthChanged` — the same shape
   as `AdvancedViewChanged`. **The write-back is the part that fails invisibly**: without
   it the column resizes, the tab's own property follows, and neither the other tabs nor
   `settings.json` ever hear about it, so the drag is silently lost on the next tab switch.
   That is not something a screenshot can show, and it was caught by a headless drag probe
   driving real pointer events at a real `ClusterTabView` — the same technique FEAT-66 used
   on the grid's own column drags, and the reason to reach for it again.
4. **It persists on `DragCompleted`, not per layout pass.** A GridSplitter rewrites the
   width continuously while the pointer moves; persisting each intermediate value would
   write the settings file dozens of times per gesture. Same bracketing as the grid's
   column drags.
5. **It is a preference, not workspace state**, and lives beside `IsSidebarVisible` in
   `settings.json` — same control, same one global value. The per-kind column widths are
   the ones in `workspace.json`, because those are keyed by what is being looked at and
   this is not. `Normalized()` clamps it for the usual reason: the file is user-writable,
   and a width past the window's own is a sidebar with no list beside it and no visible
   way back.

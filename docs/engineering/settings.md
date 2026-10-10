# Settings, and what belongs in which file

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "Settings" section keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

## Settings, and what belongs in which file

There are **two** persisted files and the split is not arbitrary:

- **`settings.json`** (`KubeNimbus.Core/Settings/`, `AppSettings` + `AppSettingsStore`)
  is *preferences* — what you chose once and expect to still be true next launch:
  theme, hotkey scheme, interface and code fonts, advanced view, sidebar visibility and expanded sections,
  picked kubeconfig paths, log scrollback, metrics poll interval, delete confirmation,
  apply preview, open logs maximized, open applications with one click, the preferred terminal, and the log panes' display toggles (timestamps,
  UTC, wrap). Those three are written by the panes themselves, not the preferences page,
  and nothing that changes *which* log lines are read — Previous, the search, the levels —
  is persisted at all; see [log-pane-reading](log-pane-reading.md).
- **`workspace.json`** (`KubeNimbus.App/WorkspaceStore.cs`) is *session* — what the
  window looked like: open tabs, pinned and recent contexts, environment overrides, the
  recent namespaces and sidebar Recent kinds per cluster, and which mode (Applications or Resources) the window was showing.

Each tab snapshot also carries the **kind and namespace** (or namespaces) it was showing, and the
workspace the index of the tab in front, so a restart lands where you left off instead
of on Pods in all namespaces on every tab. With nothing saved, a tab opens on the
kubeconfig context's own `namespace` (what kubectl would use), and the first launch
opens the chain's `current-context` rather than whichever context the merge listed
first — unless a picked folder supplied it or named it current, in which case nothing opens on
its own (B4-4, `KubeconfigChain.AutomaticFirstContext`; see
[connecting](connecting.md)). `ClusterTabViewModel.ApplyInitialView` is the one place that decides, and
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
4. **Nothing here may become a credential** (rule 4). `KubeconfigPaths` (files *or
   folders* — a folder contributes every kubeconfig in it on each search) is the closest
   it comes and is paths only, re-resolved through the chain at connect time. A folder trusts
   every kubeconfig dropped into it, so a context found only through one is listed but never
   connected to without a click (B4-4). The
   preferences page says so in the panel, which is where someone would worry about it.
5. **`AppSettingsStore.DirectoryOverride`** exists for the screenshot harness, same as
   `WorkspaceStore.DirectoryOverride` and for a stronger reason: the preferences a
   scenario touches are exactly the ones the developer running it has chosen for
   themselves. Without an override, both files — and the discovery cache and the terminal
   overlays — live under `AppDataDirectory`, which never resolves to a relative path, nor to a fixed
   name in a shared temp directory (its last-resort fallback is a fresh random directory per
   process, B4-2):
   `GetFolderPath` returns `""` for a folder that does not exist yet, and on a fresh Linux
   `HOME` that used to put the discovery cache in the current directory (ENG-39).

The page itself (`PreferencesView` + `PreferencesViewModel`) is deliberately the same
shape as pgNimbus's — tabs, then a section header, one card per setting, label and
explanation left, control right, **immediate apply and no OK/Cancel** — because someone
who uses both should not learn it twice. Settings the shell already owns (`IsAdvancedView`,
`IsSidebarVisible`) are *proxied* through `MainWindowViewModel`, never duplicated, so
the page and the command bar's own toggles cannot disagree while both are on screen.

**It is four tabs** (2026-10, following pgNimbus PR #347): General (kubeconfig files and
folders, shortcut modifier), Appearance (theme, interface and code fonts, Advanced view, sidebar), Logs and metrics,
and Changes (confirm before deleting, preview before applying). General and Appearance
come first in both apps so that the two settings both apps have, the shortcut modifier and
the theme, sit under the same tab names. A tab carries section headers only when it holds
more than one group. A new setting goes on one of the four tabs, never a fifth tab for a
single card. Three things are load-bearing:

- **The strip is nimbusUi's `TabControl.capsule`**, the full-width capsule pgNimbus's
  sidebar switch uses, moved into `shared/nimbusUi` for this (it was pgNimbus's
  `TabControl.sidebar`). Not a segmented strip with a fade, which a screenshot can catch
  half-way. This app no longer has a bare `TabItem` style: an app style loads after the
  library and would re-pad the capsule's segments, and the one it had styled only headers
  `TabControl.headerless` never draws. Since 2026-10 the capsule is drawn as a macOS
  segmented control (a tinted track, the selected segment a raised thumb in the body's
  text colour, segments padded 8,2), and its selected segment is centred: Fluent's bottom
  margin on the part named `PART_ItemsPresenter` had put 2px above it and 4px below.
- **The page is one height on every tab** (`PreferencesView.PageHeight`, applied in
  `MeasureOverride` as `min(PageHeight, available height)`). The overlay's card is centred
  and sized to its content, so a page sized by its tab moved the strip under the pointer
  on every switch. 580 fits Appearance, the tallest tab (five cards since the font
  settings); the overlay's 640 cap less its title row is just above it, so a sixth card
  there would scroll. A long kubeconfig list scrolls inside General.
- **It opens on the tab it was left on**, for the session only: `PreferencesViewModel
  .SelectedTab` is seeded from and written back to `MainWindowViewModel.PreferencesTab`,
  because the page's view model is rebuilt on every open.

`SettingsTabsTests` pins the remembered tab; the harness's `ux-preferences-tabs` check pins
the rendered half (four tabs, one height, a strip that does not move, the capsule's own
segment padding, and accessible names on a tab that was not on screen when the page loaded,
which is why `AutomationNames.NameCardControls` walks the logical tree).

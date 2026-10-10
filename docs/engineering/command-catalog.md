# The command catalog

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "The command catalog" section keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

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
   That something *asks* for the rebuild is pinned too (VER-19): the shell view model's
   subscription by `ShellHotkeySchemeTests`, the window's by the harness's
   `ux-hotkey-scheme` check, which presses Cmd+K after a scheme change on a real window.
   Deleting either subscription turns its check red; both were confirmed that way. The
   shell's handler is removed again when its window unloads (`MainWindowViewModel.Dispose`,
   ENG-16), since a static event roots every subscriber and the harness builds a shell per
   scenario; the switcher tooltip is raised from the same handler rather than surviving a
   scheme change by the accident of its popup re-binding (ENG-17).
3. **The palette is a *partial* projection, deliberately.** Most of this app's rows are
   conditional — logs/exec/port-forward only while a pod row is selected, the fleet
   toggle only with more than one cluster connected — so they stay closures over the
   selected tab in `BuildPaletteItems`, because a palette entry that matches a search
   and then refuses to run is worse than no match. What they take from the catalog is
   title, icon and shortcut text. `CommandBindings`' startup check is therefore over
   `WindowBinding` only, which is narrower than pgNimbus's and says so in place.
   **Since L1 some of those rows come from the network, and the palette stays
   synchronous anyway.** The `Logs: …` rows (every pod and Deployment/StatefulSet/
   DaemonSet in the selected tab's namespace, `ClusterTabViewModel.LogTargets.cs`) are
   filled by a *one-shot* capped list started from `CommandPaletteViewModel.Opening` —
   not a watch, because the palette is open for seconds and a second long-lived
   connection per tab for its sake is the wrong trade; not an async item source, because
   then every keystroke would await something. The source function still returns
   whatever the tab has *now* (the previous answer for the same namespace and cluster
   set, stale-while-loading, or nothing) plus **notes** — `PaletteItem`s with a null
   `Execute` that say "loading", "not allowed to list pods here" (the server's own 403
   sentence), "capped at 2,000" or "not connected". When the list lands, the tab calls
   `LogTargetsChanged` and the shell calls `Palette.Refresh()`, which re-reads the source
   *keeping the query and the highlighted row* (a keystroke still resets the highlight to
   the top match). Notes are rendered as disabled `ListBoxItem`s, can never be the
   selection, and are shown only under the `logs ` prefix or when nothing else matched —
   a settled "no pods here" does not belong under every search for "Preferences". The
   `logs ` prefix is what Ctrl/Cmd+Shift+L (`CommandId.LogsPalette`) opens the palette
   with; a prefix in the query rather than a mode flag so it is visible and Backspace
   leaves it. A log row matches on name, namespace and cluster (`PaletteItem.SearchText`)
   and never on status, for UI rule 13's reason. Every open-logs gesture — L, P, the menu,
   the palette rows — goes through `ClusterTabViewModel.OpenLogsForAsync(LogTarget)`, so
   the pane chosen and the inspector tab reused cannot differ by route. Since L2 that
   includes Shift+L and the row's logs icon, and since L3 every other list that names a
   pod — workload and node detail's pod lists, an Event about a pod, an Argo
   Application's managed workloads — through `OpenNamedLogsAsync`, which reads the object
   first so a pod that has gone, or been recreated under the same name (a UID that no
   longer matches), is stated rather than opened, and which takes the naming pane's
   cancellation so closing the pane mid-read opens nothing. The same call is where
   "open maximized" is decided (`maximized: true`, or the `OpenLogsMaximized` preference when null) — see
   [row-logs-and-maximized](row-logs-and-maximized.md).
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
   workload owns), Shift+L the same logs with the inspector maximized, P previous logs, S shell on a pod / scale on anything with a `scale`
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

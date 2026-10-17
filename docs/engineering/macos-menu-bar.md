# macOS: the menu bar, the text keys, and quitting

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.

macOS puts a menu bar at the top of the screen whether an app asks for one or not, so
the only question is whose commands it carries. Until the first pass on a real Mac
(2026-10-17, macOS 27 on Apple Silicon) it carried Avalonia's: the app menu read
"About Avalonia", had no About kubeNimbus and no Settings…, put Hide Others on ⌥⌘Q (one
modifier from quitting) and said a bare "Quit"; there was no Edit or Window menu, so ⌘M,
⌘W and ⌘T did nothing; and in the YAML editor ⌘↑ and ⌘↓ did nothing (the pass reported them
as selecting to the ends, which was its driver's doing — see "Driving it" below). All of it
is ported from pgNimbus, which had met each of these first. The pieces:

| What | Where |
|---|---|
| The app menu (About kubeNimbus, Settings… ⌘,) and the standard block's corrections | `Platform/MacAppMenu.cs`, set from `App.Initialize` |
| Cluster, Edit, View, Window, Help | `Views/MacMenu.cs`, set on the main window |
| The Edit menu's routing to the focused control | `Commands/EditCommands.cs` (+ `ExecView` as an `IEditCommandTarget`) |
| The Cocoa text keys on every TextBox and AvaloniaEdit editor | `Platform/MacTextKeys.cs`, installed from `App.Initialize` |
| Ending the process with `_exit` | `Platform/MacShutdown.cs`, from the lifetime's `Exit` and the end of `Main` |

## The app menu has to exist before the platform finishes starting

This is the cause of "About Avalonia", and it was not the missing `Name` (that was already
`Name="kubeNimbus"` in App.axaml, which is why Hide and the menu title were right).
Avalonia.Native builds its application-menu exporter in an `AfterSetup` callback
(`AvaloniaNativePlatformExtensions.UseAvaloniaNative`), which runs after
`Application.Initialize` has loaded the XAML and before `OnFrameworkInitializationCompleted`.
The exporter's constructor reads `NativeMenu.GetMenu(Application.Current)` **once**; finding
nothing, it creates its placeholder ("About Avalonia"), stores it on the application and
exports it. It never subscribes to that property afterwards (only the Dock menu's exporter
does, for `NativeDock.Menu`). kubeNimbus called `NativeMenu.SetMenu(app, …)` from the main
window's `DataContextChanged`, so it wrote to an attached property nothing read, for the life
of the process — every time, not intermittently.

So the menu is set in `App.Initialize`, macOS only, before the exporter exists. What the
exporter does watch afterwards is the *items* of the menu it exported (their Header, Gesture
and IsEnabled), so later changes are made in place: Settings…'s chord follows the hotkey scheme
through `MacAppMenu.RefreshGestures`, never by replacing the menu.

**The standard block** (Services, Hide, Hide Others, Show All, Quit) is appended by
Avalonia.Native itself (`PopulateStandardOSXMenuItems`), and two of its items are wrong as of
12.1.4. Its commands and the Services flag are internal, so it cannot be turned off and
rebuilt; its items are ordinary `NativeMenuItem`s in our menu, so `MacAppMenu.FixStandardItems`
rebinds Hide Others to ⌥⌘H and renames Quit to "Quit kubeNimbus", found by the headers Avalonia
gives them — a later Avalonia that fixes either makes it a no-op.

The app menu's About and Settings… find the main window's view model at click time: the app
menu is the application's, not the window's. "Settings…" rather than the catalog's
"Preferences…" is the platform's own word since Ventura; the chord is the catalog's.

## The window's menus

Cluster, Edit, View, Window and Help, built in `MacMenu.CreateMenuBar` on every platform (so
`MacMenuTests` can read it) and attached only on macOS. Seven rules:

1. **It is macOS-only and gated on the platform, not left to be inert.** UI rule 12 says this
   app has one row of chrome and the command bar is it; a menu bar is a second row everywhere
   except macOS, where the OS supplies the row anyway. Win32 has no native menu exporter, but
   **X11 does** — the freedesktop global-menu protocol — so an ungated menu would quietly
   appear in a GNOME or KDE panel.
2. **Every item is also reachable without it.** The ☰ menu, the palette and the key bindings
   already carry the app's commands; Edit and Window carry the platform's own verbs.
3. **Titles and gestures come from `CommandCatalog`**, and the bar is rebuilt on
   `Hotkeys.Changed` for the same reason the window's key bindings are: a
   `NativeMenuItem.Gesture` holds the modifier it was built with.
4. **A menu gesture is a key equivalent, and AppKit takes it before Avalonia sees the key.**
   Measured with real HID key events (a CGEvent tool; see "Driving it" below): with ⌘V on the
   Edit menu, the focused TextBox received the ⌘ key-down and *not* the V — the menu item's
   Click ran instead. So nothing fires twice, and everything the menu carries must route to
   where the key used to go. Two consequences follow.
5. **The Edit menu routes every verb to the focused control** (`EditCommands.Execute`): an
   `IEditCommandTarget` first (the exec terminal), then a TextBox, an AvaloniaEdit TextEditor,
   or a SelectableTextBlock (Copy and Select All); when nothing on the way takes it, the key the
   menu consumed is raised again on the focused element, so a control that answered the key
   itself (the resource grid's copy) still does. Without the routing, adding the menu would
   break copy and paste everywhere. In the exec terminal ⌘C and ⌘V do what Terminal.app does —
   the pane's own trimmed copy and filtered, bracketed paste — and Undo, Redo and Cut are
   swallowed, so a verb can never become bytes the shell reads. The Edit and Window menus'
   gestures are always ⌘, whatever the hotkey scheme: they are the platform's text keys, which
   Avalonia's text controls answer with ⌘ on a Mac under either scheme.
6. **A chord the shell owns is left off the menu** (`MacMenu.MenuGesture`). Under the Windows
   scheme on a Mac every Cmd chord becomes literal Ctrl, and a ⌃W key equivalent would close
   the tab instead of reaching the exec terminal's shell (delete word). Such an item keeps its
   title and loses its caption; the window binding still answers the chord and yields to the
   terminal (`CommandBindings.ShellOwns`).
7. **Items act through `Click` handlers, not bound `Command`s.** Half of them belong to the
   selected tab or to the window itself, and a NativeMenuItem built in code has no binding to
   keep a captured command current. The checkable items (sidebar, advanced view) and Close
   cluster tab's enabled state follow the shell through `PropertyChanged`, and are re-read when
   a menu opens (`NeedsUpdate`).

**⌘T and ⌘W** are catalog chords now (`CommandId.NewClusterTab` / `CloseClusterTab`, Cmd+T and
Cmd+W, i.e. Ctrl+T and Ctrl+W on Windows and Linux, where the exec terminal keeps them for its
shell). ⌘T opens the switcher, which focuses its search box whatever opened it. **⌘W on the last
tab leaves the empty shell and never closes the window** — a decision, stated: pgNimbus closes
the window on an empty last tab, but this app has one window and no Dock-reopen path, so closing
it would quit, and a run of ⌘W to clear the strip should not end the app. With no tab the item is
disabled and the chord does nothing.

**The Window menu** is Minimize (⌘M), Zoom (disabled when the window cannot maximize) and Bring
All to Front. No window list: AppKit only keeps one for the menu registered as
`NSApp.windowsMenu`, which Avalonia neither registers nor exposes, and this app has one window.
A known limit: Avalonia's menu bar is per key window, so while the window is minimized the bar
shows the app menu alone; the Dock icon restores it.

**The Help menu keeps AppKit's search field.** AppKit inserts a search field into any menu titled "Help";
pgNimbus drops its Help menu for that reason. kubeNimbus keeps one (Keyboard shortcuts), which is
a later call, not a blocker.

## The text keys

`MacTextKeys` is pgNimbus's class, moved over nearly verbatim, plus ⌘← and ⌘→. Tunnelled class
handlers on `TextBox` and AvaloniaEdit's `TextArea` answer the Cocoa text system's defaults —
⌘←/⌘→ line start/end, ⌘↑/⌘↓ document start/end (⇧ selecting), ⌥⌫/⌥⌦ word, ⌘⌫/⌘⌦ to the line's
ends, ⌃A/⌃E/⌃K/⌃Y/⌃D/⌃H/⌃F/⌃B/⌃N/⌃P/⌃T — each edit one undo step. Active on macOS under the Cmd
scheme only (`IsActive`); with the Windows scheme on a Mac, ⌃A is Select All and the set steps
aside.

They are **not** catalog commands: they are how text behaves on the platform, so they have no
palette row and no cheat-sheet line. What keeps them from shadowing a command is `Classify`
matching exact modifiers, and `MacTextKeysTests.No_command_chord_on_the_cmd_scheme_is_a_text_key`,
which checks every catalog chord against it. The catalog's literal-Ctrl chords are the
interesting ones: ^C and ^D belong to the exec terminal, a `TerminalControl` that is neither
TextBox nor TextArea (the test asserts that too), and Ctrl+Z (unhealthy only) is a list key the
table does not name. The exec terminal therefore keeps all its own keys.

Measured with real key events on `ce7638a`: ⌘←/⌘→ already moved to the line's ends (Avalonia's
macOS keymap binds them) and ⌘↑/⌘↓ did nothing, because that keymap has no document-start or end
on them. ⌘←/⌘→ are in the table anyway, unlike pgNimbus's, so the behaviour does not depend on the
platform keymap and the forced-on harness check (`ux-mac-text-keys`) runs the same rows on every OS.

**It is a candidate for nimbusUi and stays here for now.** It passes the membership test (it is
describable without naming Kubernetes), but it depends on AvaloniaEdit, which nimbusUi does not
reference, and the drift check makes a shared file un-mergeable until a nimbusUi PR lands first.
Lifting it is a three-repository change (nimbusUi gains it and AvaloniaEdit, pgNimbus deletes its
copy, both apps take the copy) best done in one session. `EditCommands`, `MacAppMenu` and
`MacShutdown` are the same kind of candidate. pgNimbus's word-grouped editor undo
(`EditorTypingUndo`) was not ported: it changes undo on every platform, not only the Mac.

## Mac spelling and Mac chords

Since the second pass (2026-10-17) every shortcut label on the Cmd scheme is written with Apple's
glyphs in ⌃⌥⇧⌘ order (⌘K, ⇧⌘N, ⌘1 … ⌘9, ⌃C in the exec pane), and two Mac conventions are
catalog chords on that scheme only: ⌘⌫ arms the delete strip on a selected list row (Delete
still does; a Mac keyboard sends it only as fn+⌫) and ⌘[ goes back from an application's page to
the list, beside Esc. ⌘⌫ in a text box is still MacTextKeys' delete-to-line-start, because the
delete chord is a list key. How the scheme spells a chord and where the Mac-only chords live is in
[command-catalog](command-catalog.md), item 8.

## Quitting ends the process with `_exit`

On macOS a quit — ⌘Q, the menu's Quit, a logout, or `Main` returning after the smoke test — used
to end in C's `exit()`, which runs libAvaloniaNative's static destructors; one releases a
`ComPtr<IAvnDispatcher>` through a managed MicroCom proxy after NativeAOT has torn down the main
thread's runtime state, which is a fail-fast (`SIGABRT`). Recorded on this Mac:
`exit → __cxa_finalize_ranges → ComPtr<IAvnDispatcher>::~ComPtr → MicroComVtblBase.Release →
Thread::ReversePInvokeAttachOrTrapThread → RhFailFast → abort`, from a smoke-test run whose
platform failed to start ("not able to start the RenderTimer") — its exit code 66 came out as 134.
`MacShutdown` (pgNimbus's) ends the process with `_exit(2)` from the lifetime's `Exit` event,
after every window has closed and saved, and `Program.Main` does the same on its way out, which
covers a startup that never reached the lifetime. The smoke test's verdict survives both: it hands
its own (`SmokeTest.Verdict`) to the `Exit` handler, because once that runs nothing after the
message loop does, and its watchdog ends the process through the same `_exit`.

## Driving it on a Mac

System Events' `keystroke … using {command down}` is **not** a real key press for Avalonia: every
⌘ chord sent that way arrived as an extra ⌘A (`Key.A`, `PhysicalKey.A`) before the menu saw the
real key, so "⌘V replaces the whole field" was a test artefact, reproduced identically on the
build without the Edit menu — and so was the first pass's "⌘↑ selects to the start": a ⌘A
followed by a ⌘↑ that did nothing. Real HID events from a small CGEvent tool (`CGEvent(keyboardEventSource:
virtualKey:keyDown:)`, modifier keys pressed and released around the key) behave like a keyboard.
Clicks need CGEvent too (`click at` does not reach Avalonia). The menu bar can be read with System
Events (`menu items of menu 1 of menu bar item … of menu bar 1`, attributes `AXMenuItemCmdChar`
and `AXMenuItemCmdModifiers`, where 0 is ⌘, 1 ⇧⌘, 2 ⌥⌘ and 8 none). Run the `.app` (the bare
binary has no key window for System Events to see) on an isolated profile:
`open -n kubeNimbus.app --env KUBENIMBUS_PROFILE_DIR=<fresh dir> --env KUBECONFIG=<sandbox>`.

**The `.app` bundle** is built by `scripts/macos/build-app-bundle.sh` — see "Installers" under
Releasing. `installer/macos/Info.plist.template` is where `CFBundleName`, `CFBundleIdentifier` and
`CFBundleExecutable` live, and its `CFBundleExecutable` must stay `kubeNimbus` (the assembly name).
`Application.Name` is still what the app menu reads.

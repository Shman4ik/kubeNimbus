# macOS has a real menu bar, and the app is called kubeNimbus

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


macOS puts a menu bar at the top of the screen whether an app asks for one or not, so
the only question is whose commands it carries. It carried Avalonia's placeholder — a
lone "About Avalonia" — which is why the app introduced itself to every Mac user under
the framework's name. Two halves, and both are small:

- **`Name="kubeNimbus"` on the `Application`** (App.axaml). Avalonia's macOS backend
  feeds that to `SetupApplicationName`, and AppKit titles the app menu and names its
  About, Hide and Quit items from it. The product name, not the assembly name, for the
  same reason `<AssemblyName>` is `kubeNimbus`: it is a string a user reads.
- **`Views/MacMenu.cs`**, a menu bar built in code and attached to the window and the
  application. `NativeMenu.SetMenu(Application.Current, …)` replaces the placeholder app
  menu (About + Preferences; Hide, Hide Others and Quit are AppKit's own and must not be
  added again), and `NativeMenu.SetMenu(window, …)` supplies Cluster / View / Help.

Four things about it:

1. **It is macOS-only and gated on the platform, not left to be inert.** UI rule 12 says
   this app has one row of chrome and the command bar is it; a menu bar is a second row
   everywhere except macOS, where the OS supplies the row anyway. Win32 has no native menu
   exporter, but **X11 does** — the freedesktop global-menu protocol — so an ungated menu
   would quietly appear in a GNOME or KDE panel.
2. **Every item is also reachable without it.** The ☰ menu and the palette already carry
   all of them, so a Mac gains a familiar route and no platform gains a command.
3. **Titles and gestures come from `CommandCatalog`**, and the menu is rebuilt on
   `Hotkeys.Changed` for the same reason the window's key bindings are: a
   `NativeMenuItem.Gesture` holds the modifier it was built with, so a menu built once
   keeps printing the other platform's chord after someone changes the scheme.
4. **Items act through `Click` handlers, not bound `Command`s.** Half of them belong to
   the selected tab or to the window itself, and a NativeMenuItem built in code has no
   binding to keep a captured command current — a menu rebuilt only on a scheme change
   would otherwise act on whichever cluster was selected at build time. The two checkable
   items (sidebar, advanced view) follow the shell through `PropertyChanged`, so the menu
   and the command bar's own toggles cannot disagree while both are on screen.

**The `.app` bundle exists now** and is built by `scripts/macos/build-app-bundle.sh` — see
"Installers" under Releasing. `installer/macos/Info.plist.template` is where `CFBundleName`,
`CFBundleIdentifier` and `CFBundleExecutable` live, and its `CFBundleExecutable` must stay
`kubeNimbus` (the assembly name). `Application.Name` is still what the app menu reads, so
nothing here depends on the bundle; the bundle is what gives the app a Dock icon, a
Spotlight entry and a home in /Applications. The `.tar.gz` still ships the bare binary
beside it, and that is the one this section's behaviour was verified against.

# The exec terminal

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


The exec pane renders a real VT emulator: `SvcSystems.UI.Terminal` (the Avalonia
control) over `XTerm.NET` (a headless port of xterm.js), both MIT. Before it, the pane
was a `TextBox` fed by a hand-written scrollback that *stripped* ANSI — which is fine
for `ls` and useless for everything people actually exec in for: `vi`, `top`, `mc`,
`less`, `htop`, a bash reverse-i-search. There is no addressable screen in a scrollback,
so a full-screen tool did not draw at all; it unspooled.

**The transport did not change and must not.** `ClusterClient.ExecAsync`'s WebSocket,
the channel-3 error read, the shell probe and its `Task.WhenAny` timeout are all
exactly as they were — see the exec bullets above and `ExecTabViewModel.ProbeAsync`'s
remarks, which are the record of two failures that cost a live debugging session each.
What changed is only what happens to the bytes at either end.

Seven things are load-bearing:

1. **Why this package, and not one of the alternatives.** The field was surveyed in
   [`docs/research/2026-08-15-terminal-libraries.md`](../../docs/research/2026-08-15-terminal-libraries.md):
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
   and would send a plain `^C`. That mapping — `^C` 0x03, `^D` 0x04, Tab 0x09, and
   Ctrl+Shift+C *not* 0x03 — used to be held by one session's scratch probe; the screenshot
   harness's `ux-exec-keys` check (`tools/Screenshot/KeyboardChecks.cs`) now presses the
   keys on a real rendered pane and reads the bytes off the view model's terminal model,
   and it goes red when the Tunnel handler stops requiring Shift (ENG-20, mutation-checked).
   Right-click opens a Copy/Paste/Select-all menu rather
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
   "app has no shell — tried /bin/bash, /bin/sh, /bin/ash", "Session ended" —
   and then gets out of the way, because after that the scrollback is worth more and the
   chrome row carries the state anyway. The demo cluster is unchanged: no `ClusterClient`,
   so `Border.demoUnavailable` and nothing else.

## A paste is filtered, bracketed, and asked about when it would run line by line

The terminal control's own `PasteFromClipboardAsync` hands the clipboard to the shell raw:
no bracketed-paste markers even when the shell asked for them, and every control character
included. XTerm.NET's `Terminal.Paste` does both properly, but the control never calls it. So
both paste routes — Ctrl+Shift+V and the right-click menu's Paste — read the clipboard in
`ExecView` and go through `ExecTabViewModel.Paste`, which applies `ExecPaste.Prepare`:

1. **Line endings become carriage returns**, which is what Return sends and what a shell
   reading a terminal expects.
2. **Every other control character except tab is dropped**, C1 as well as C0 — U+009B opens a
   sequence exactly as `ESC [` does. That is what makes the bracket mean anything: a clipboard
   holding `ESC[201~` would otherwise end the bracketed paste early and the rest would run as
   typed, and any other escape sequence would reach the shell's line editor as keystrokes.
3. **Bracketed when the shell asked** (`BracketedPasteMode`, set by the shell's own
   `ESC[?2004h`): bash and zsh then treat the whole paste as text and run nothing until Return.

A shell that never asks — BusyBox `sh`, which is most containers — runs each pasted line the
moment its Return arrives. So a paste of **more than one line** there is armed rather than
sent: a card over the top of the terminal says how many lines and why it is asking, with
**Paste** (focused, so Enter confirms) and **Cancel** (and Esc). It is drawn over the terminal
rather than docked above it because it costs nothing while nothing is armed (UI rule 1), a
third row of dock chrome would come out of the terminal (rule 10) and would resize the remote
PTY for the sake of a question, and the top rather than the bottom keeps the prompt the lines
would land at in view. It is rule 17's pattern — the gesture that started it never runs it —
in a pane, not the list's strip, which is about rows. One line, with or without its own Return,
is what typing it would have done and is sent at once. The armed text is prepared again on
confirm, so a shell that turned bracketed paste on meanwhile gets the markers.

`UntrustedTextTests` pins `ExecPaste`; the harness's `ux-exec-paste` pastes through the real
gesture and reads the bytes off the terminal model (filtered single line, bracketed multi-line,
armed then confirmed with Enter, armed then cancelled with Esc), and fails if the gesture is
routed back to the control's own paste. `cluster-tab-exec-paste-confirm` renders the armed card.

## Which shells, and what "no shell" means

The shells tried depend on the pod's OS (`Core/ExecShells`): `/bin/bash`, `/bin/sh`,
`/bin/ash` on Linux, `powershell` then `cmd` on a Windows node, both lists (Linux first) when
the OS cannot be told. The OS is what the scheduler goes by: the pod's `spec.os.name`, its
`kubernetes.io/os` node selector, then the node's label — read once per pane, and a node the
user may not `get` (the usual case under namespace-scoped RBAC) is "unknown", not an error.
Lens and FreeLens send `powershell` to a Windows node too; before this, the pane sent three
paths no Windows image has.

"No shell" is a verdict, not the last failure. The pane used to print whatever the last
shell tried said, so a 403 on `pods/exec` read as `stat /bin/ash: no such file or directory`
— ash being last. `ExecShells.IsMissingExecutable` matches the runtime's own sentences (runc
and containerd's `no such file or directory` / `executable file not found`, hcsshim's `The
system cannot find the file specified`), and only when **every** attempt says that is the
image declared shell-less. Otherwise the first failure that is about something else is the
one shown.

## The debug container (`kubectl debug`, in place)

A distroless or .NET chiseled image has no shell, and neither Lens nor FreeLens has an answer
(FreeLens runs `kubectl exec -- sh -c "bash || ash || sh"`, which needs `sh` itself). This
pane's answer is the one kubectl has: an ephemeral container from another image, sharing the
target's process namespace (`targetContainerName`) and network, with the target's files
reachable as `/proc/1/root`. The rules are `Core/DebugContainers` (pure, `DebugContainersTests`
byte for byte) and the HTTP is `ClusterClient.Debug.cs`; `DebugContainerLiveTests` runs the
whole thing against the sandbox with the pause image, which has no shell at all, and the
pinned default debug image.

1. **The offer appears only after a verdict of "no shell"**, in the overlay that states it, on
   an opaque `overlayCard` (the terminal is black in both themes, and a translucent `card` put
   light-theme text on it). Not on a Windows node: ephemeral containers do not exist there.
2. **The click is the confirmation.** Adding an ephemeral container is a real and permanent
   change to the pod (it cannot be removed; it goes when the pod is recreated), which is UI
   rule 17's territory. The offer is already the armed state that rule asks for: it appears
   only after a failure, names what it adds and says that it stays, so a strip asking again
   would be a second question about the same thing.
3. **The patch is kubectl's**: a strategic merge patch of `pods/{name}/ephemeralcontainers`
   adding one container named `debugger-xxxxx` (kubectl's prefix and alphabet), with
   `stdin: true` and `tty: true`. Those two are not cosmetic: without them BusyBox's default
   `sh` reads EOF and exits at once, and there is nothing left to exec into. The pane execs
   into the running container rather than attaching to it, so a reconnect is an ordinary
   reconnect.
4. **`SYS_PTRACE` first, without it when Pod Security refuses.** The target usually runs as
   its own user and the debug image as root, and without the capability the kernel refuses
   root a look at another user's `/proc/1/root`. Pod Security's `baseline` level refuses the
   capability, admission refuses the patch whole, so the retry without it is still the only
   container added (`DebugContainerAdded.CanTrace`). The connected line then says the files
   are readable only if both run as one user. A namespace enforcing `restricted` refuses a
   root image either way, and its sentence is what the pane shows; RBAC's 403 is not retried.
5. **The wait is a watch, not a poll.** `WaitForDebugContainerAsync` is a field-selected watch
   of the one pod through the informer loop, reporting each waiting reason as it changes (the
   pull is the slow part), and failing on `ErrImagePull`/`ImagePullBackOff`/`InvalidImageName`
   and the create errors rather than sitting out the back-off. Three minutes in all.
6. **A running one is reused.** Ephemeral containers cannot be removed, so a second click
   adding a second container would leave the pod carrying both. `FindReusable` opens a running
   debug container that targets the same container from the same image instead.
7. **The image is not a preference.** BusyBox 1.37 (4 MB, a shell, `wget`, `nc`, `ps`) is
   right wherever Docker Hub is reachable; an air-gapped cluster types its mirror into the
   box, once per pane. If people end up retyping it, a setting is the next step, with a line
   in `PRIVACY.md`. The default is written out in full and pinned — see "The debug image
   pin" below. A typed image is sent exactly as typed and reuse compares it exactly as typed,
   so a mirror keeps working and never matches a debugger added from the default.

### The debug image pin

`DebugContainers.DefaultImage` is
`docker.io/library/busybox:1.37@sha256:<digest of the 1.37 OCI image index>`, not
`busybox:1.37` (security block 3, B3-2). The container it starts runs as root with
`SYS_PTRACE`, in the target's process namespace, so it can read the target's memory and
environment, and `kubectl debug` has no default image — the app makes this choice for the
user, so it must not move under them. A short name does: on CRI-O with
`unqualified-search-registries` it can resolve to a registry other than Docker Hub, and a
tag on Docker Hub is mutable. The registry and repository fix where it comes from, and the
digest fixes the bytes; the tag stays only so a person reading the box sees which BusyBox it
is (the runtime ignores a tag beside a digest). The digest is the **index** (the multi-arch
list, 17 entries), so every node architecture resolves its own image from it.

**Nothing updates the pin automatically**, and Dependabot does not see a string constant.
Update it when BusyBox ships a release worth having (a CVE in the shell or `wget`, or a new
minor), and look at it at least once per release:

1. Get an anonymous pull token:
   `GET https://auth.docker.io/token?service=registry.docker.io&scope=repository:library/busybox:pull`.
2. `HEAD https://registry-1.docker.io/v2/library/busybox/manifests/<tag>` with
   `Authorization: Bearer <token>` and
   `Accept: application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json`,
   and read `Docker-Content-Digest`. Check that the response's content type is an index and
   not a single manifest, and, if you like, that the SHA-256 of the body a `GET` returns is
   the same digest.
3. Change the tag and the digest together in `DefaultImage`, and run
   `DebugContainerLiveTests` against the sandbox: it starts a debug container from the
   default and checks that the runtime's `imageID` carries the pinned digest.

The 2026-10-07 pin was read exactly that way (`Docker-Content-Digest` of `busybox:1.37`,
`application/vnd.oci.image.index.v1+json`, matching the body's own SHA-256). A debug
container added before the pin (`busybox:1.37`) is not reused by a later click, which adds
one from the pinned image; the pod carries both until it is recreated, as with any image
change.

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

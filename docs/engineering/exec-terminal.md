# The exec terminal

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


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

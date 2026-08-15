# Terminal: what to build on, and what to launch

*2026-08-15. Question asked: make the exec terminal genuinely usable, and offer to
open the machine's own terminal instead. Which open-source libraries survive this
repo's constraints?*

The constraint that decides everything here is NativeAOT with full trimming. Most
of the .NET terminal field predates it, and "no reflection" is not something a
README will tell you — so the candidates were cloned, read, and one was published
AOT-clean before being recommended.

## The field

| Library | License | State | Verdict |
|---|---|---|---|
| [**XTerm.NET**](https://github.com/tomlm/XTerm.NET) | MIT | v1.0.15, active (Jun 2026), 138 commits | **The engine.** A headless port of xterm.js — no renderer, no PTY, no UI dependency. `net6.0`, two small deps (`Unicode.net`, `Wcwidth`). |
| [**SvcSystems.UI.Terminal**](https://github.com/IvanJosipovic/SvcSystems.UI.Terminal) | MIT | v1.0.3, commits within the last day | **The control.** The Avalonia renderer over XTerm.NET, targeting `net10.0` and **Avalonia 12.1.1** — the version this app is on. |
| [**Iciclecreek.Avalonia.Terminal**](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal) | MIT | v2.0.3, Avalonia 12.0.2 | Same engine, but bundles `Porta.Pty` and is built around *hosting a local process*. Worth having in mind only for a local shell tab. |
| [**XtermSharp**](https://github.com/migueldeicaza/XtermSharp) | MIT | Cocoa and Terminal.Gui frontends only | No Avalonia renderer. Would mean writing one. |
| [**VtNetCore**](https://github.com/darrenstarr/VtNetCore) | MIT | Last real work 2018, .NET Standard 2.0 | Its Avalonia renderer belongs to AvalonStudio, which is dead. Unmaintained. |
| [AvalonStudio.TerminalEmulator](https://github.com/VitalElement/AvalonStudio.TerminalEmulator) | — | Avalonia 0.x era | Dead with its parent project. |

## Why the SvcSystems control, specifically

- **Its API is shaped for exactly our transport.** `Feed(byte[])` in, a
  `UserInput` event carrying `ReadOnlyMemory<byte>` out, `Resize(cols, rows)`.
  No PTY anywhere in the contract — which matters, because kubeNimbus's exec
  bytes arrive over a WebSocket from the API server, not from a local process.
  Every PTY-coupled library would have to be pried apart to fit that.
- **It was written by the author of [KubeUI](https://github.com/IvanJosipovic/KubeUI)**, another Avalonia
  Kubernetes desktop client. This library exists because someone hit our problem
  first.
- **It renders the way this repo already renders charts**: `DrawingContext` +
  `FormattedText` with a bounded cache, the same argument as `Sparkline.cs`.
- Scrollback, drag/double-click/triple-click selection, search over the buffer,
  mouse reporting, `OnKeyDown` with Control-modifier handling, and a themeable
  caret — all the things the current ANSI-stripping pane cannot do.
- `grep` for `System.Reflection`, `Activator.CreateInstance`, `Assembly.` and
  `dynamic` across both XTerm.NET's engine and the control: **zero hits.**

## The AOT check, run rather than assumed

A minimal Avalonia app referencing `SvcSystems.UI.Terminal` — instantiating the
model, feeding bytes through it, subscribing `UserInput`, constructing the
control — published with `PublishAot=true`, `TrimMode=full`, `linux-x64`:

```
Restored … (in 10.59 sec)
Generating native code
aottest -> …/out/
```

**Zero trim or AOT warnings**, and the resulting 12 MB native binary runs and
prints from the fed buffer. Resolved graph: `SvcSystems.UI.Terminal/1.0.3` →
`XTerm.NET/1.0.15` → `Unicode.net/2.0.0`, `Wcwidth/3.0.0`.

That is cleaner than `Avalonia.Controls.DataGrid`, which this app already ships
with known IL2104/IL3053 warnings.

**The risk, stated plainly:** v1.0.3, one maintainer, ~35 stars. The mitigation is
that it is MIT and small — 2 877 lines across ten files, with the emulation
proper living in XTerm.NET underneath. Vendoring it is a real fallback, not a
theoretical one, and `shared/nimbusUi` is where it would go, since a terminal
control can be described without naming Kubernetes.

## Opening the machine's own terminal

**No library, and none is wanted.** Every cross-platform "open a terminal" helper
in the .NET and Node ecosystems is a table of `Process.Start` heuristics; ours
would be ~60 lines and would not carry a dependency that can break the AOT
publish. The table:

| | Attempt in order |
|---|---|
| Windows | `wt.exe` (Windows Terminal) → `pwsh.exe` → `powershell.exe`, launched detached |
| macOS | `open -a Terminal <script>` — a command has to be handed over as an executable temp script; AppleScript is the alternative and is worse |
| Linux | `$TERMINAL` → `xdg-terminal-exec` (the freedesktop proposal, present on newer distros) → `x-terminal-emulator` (Debian alternatives) → probe `gnome-terminal`, `konsole`, `xfce4-terminal`, `kitty`, `alacritty`, `wezterm` |

Two different features hide behind one button, and they should be separate:

1. **A shell already pointed at this cluster** — the terminal opens with
   `KUBECONFIG` set to the tab's own kubeconfig path and the context selected.
   This is what `kubectx` exists for, it needs nothing from us but environment
   variables, and it is the one most people will use daily.
2. **This exec session, but in my terminal** — spawns
   `kubectl exec -it --kubeconfig <path> --context <ctx> -n <ns> <pod> -c <container> -- <shell>`.

Both need `kubectl` on `PATH` and must say so when it is missing rather than
opening a window that flashes and dies (UI rule 9). Both pass **paths**, never
credentials — the same discipline exec-plugin auth already follows — and both
launch an external program, which puts them in the same paragraph of
`SECURITY.md` as exec plugins.

## Proposed backlog items

| — | Replace the exec pane's ANSI stripping with `SvcSystems.UI.Terminal` | Demand: full-screen tools (`vi`, `top`, `mc`) are unusable today; every competitor has a real terminal | M | P1 | | Engine verified AOT-clean here. Keep the WebSocket transport and the bash→sh→ash probe; only the rendering and input layer change |
| — | Open the machine's default terminal, pointed at this cluster's context | Demand: the daily gesture people leave a GUI for | S | P1 | | ~60 lines of `Process.Start`, no dependency. Needs a missing-`kubectl` state |
| — | "Open this exec session in my terminal" from the exec pane and the row context menu | Marketing: the honest answer to "your terminal is not my terminal" | S | P2 | | Depends on the item above |
| — | A local shell tab inside the app, cluster context preset | Weak: the OS terminal covers it better | M | P3 | | Would add `Porta.Pty` (MIT, ConPTY + forkpty, ships a `Vanara.PInvoke` dependency) — the only place a PTY is needed at all |

## One thing found while looking, worth its own attention

[**KubeUI**](https://github.com/IvanJosipovic/KubeUI) is an actively developed, open-source, **Avalonia + .NET**
Kubernetes desktop client — multi-cluster, YAML editing, logs, console,
port-forwarding — and `CLAUDE.md`'s market analysis does not mention it. The
"nobody ships fast + open source + modern native desktop UI" framing that this
whole project rests on should be re-checked against it before the next
positioning claim is written. That is a research brief of its own, not a
footnote.

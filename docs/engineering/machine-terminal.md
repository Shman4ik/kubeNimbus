# The machine's own terminal ("open a terminal on this cluster")

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`TerminalLauncher.cs` (Core) starts the user's own terminal with `KUBECONFIG` set and
the current context pinned to one cluster — the daily gesture people leave a GUI for,
and the one thing this app had no answer to at all. It is deliberately **not** a shell
inside the app: that needs a PTY dependency (`Porta.Pty` and its `Vanara.PInvoke` tail,
the only place this repo would ever need one), and it still would not be *your* terminal,
with your prompt, your fonts, your fzf and your kubectl plugins.

Seven things are load-bearing:

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
   **Windows** starts `pwsh.exe` → `powershell.exe` → `cmd.exe` directly (each by its full
   path, rule 7) with the environment on the `ProcessStartInfo`, which still lands inside Windows Terminal
   wherever it is the default terminal application (a console-host setting, not a
   command line) and inside conhost where it is not — i.e. the item's stated fallback,
   reached by a different route. **macOS** writes a `.command` launcher script that
   exports `KUBECONFIG` and `exec "$SHELL" -l`, and opens *that* with
   `open -a <app>`: iTerm2, then Ghostty (`open -na Ghostty --args -e <script>`, because it
   runs a script given to `-e`, not one it is asked to open), then Terminal (FEAT-19). An
   application whose `.app` is in none of `/Applications`, `/Applications/Utilities`,
   `/System/Applications(/Utilities)` and `~/Applications` is not tried, so a machine
   without iTerm2 never reports a failed `open` as an opened terminal. **Linux** is the only
   one where the obvious thing is also the correct thing: `$TERMINAL`, then
   `xdg-terminal-exec`, then `x-terminal-emulator`, then the emulators, each started with
   **no arguments** (which every one of them reads as "open my default shell", and which is
   the only form needing no per-emulator flag table) and inheriting the environment
   normally. A command is a different shape on all three; see "Handing a command to the
   terminal" below.
5. **A missing `kubectl` warns for a plain terminal; it blocks a hand-off.** For a plain
   terminal there are three reasons to open it anyway, and the third is the
   strongest: the terminal is useful without it (`KUBECONFIG` is what helm, k9s, stern
   and kubectx read too); kubectl may be installed a minute later; and **our PATH is not
   the terminal's PATH** — a GUI launched from Explorer, the Dock or the Store inherits
   a minimal environment, the same reason `$KUBECONFIG` never reaches it, so a probe
   miss is weak evidence about the shell that is about to open. The probe therefore also
   looks in the login-shell directories (`/usr/local/bin`, `/opt/homebrew/bin`, …), and
   the message says the PATH may be shorter here than in your shell rather than
   asserting kubectl is absent. A hand-off is the opposite case: the terminal is handed
   *kubectl itself*, by the absolute path the probe found (rule 7), so with none found
   nothing is written or started and the outcome (`TerminalLaunchOutcome.NoKubectl`) says
   what would have run and where kubectl was looked for.
6. **Every outcome lands in one dismissible `infoBar` above the list**
   (`ClusterTabViewModel.TerminalNotice`, UI rules 9 and 11), and it exists because this
   command's own feedback — a window — **opens in front of the app**. Success, opened-
   without-kubectl, nothing-could-be-opened and the demo refusal all land there;
   `DescribeTerminalLaunch` is a public static so both the tests and the screenshot
   harness render the app's real words rather than a paraphrase. The no-terminal case
   prints the exact `KUBECONFIG` value in selectable text, because that is what makes
   the gesture completable by hand. Two entry points and no new always-visible control
   (UI rules 1 and 15): the ☰ menu and a Ctrl/Cmd+K entry.
7. **Nothing is started by a bare name (B4-1, ENG-58).** With `UseShellExecute = false`, .NET
   looks for a name with no directory in it in the app's own folder and then the *current
   directory* before `PATH`, on Windows (`CreateProcess` with no application name) and on Unix
   (`Process.ResolvePath`) alike. So a `cmd.exe` or `pwsh.exe` left in a downloads folder or a
   cloned repository the app was started from, or in the folder a portable zip was extracted
   into, would have run with `KUBECONFIG` pointed at a cluster. Every candidate is resolved to
   an absolute path first, by `TerminalLauncher.Resolve` over a `TerminalLookup` (PATH, PATHEXT
   and the system directory, passed in so the tests can fake all three):
   `cmd.exe` and `WindowsPowerShell\v1.0\powershell.exe` from `Environment.SystemDirectory`;
   `pwsh`, a bare `$TERMINAL` and the Linux emulators through `FindExecutable` over PATH, which
   skips entries that are not fully qualified; macOS's `/usr/bin/open` as it is; a `$TERMINAL`
   with a directory in it only when it is a full path that exists. A candidate that does not
   resolve is skipped, and the "nothing could be opened" notice lists it as "(not found)".
   `TerminalCandidate.Source` says which rule applies, so `Candidates` stays pure and the
   resolution is a separate step `Plan` runs when given a lookup. The credential-plugin lookup
   (`ExecPluginPath`, S1-3) closed the same hole first; this was its other half.
   `TerminalLauncherTests.APlantedShellInTheCurrentDirectoryIsNeverTheOneStarted` puts a planted
   copy behind `.` and a relative PATH entry, and turns red if `Resolve` hands back the bare name.

**The `terminal/` directory is never pruned, and that is a decision, not an
oversight (ENG-15).** One `context-<hash>.kubeconfig` (and on macOS one
`open-<hash>.command`) is written per context ever opened, including contexts that have
since left every kubeconfig, and nothing removes them. Two facts make that the right
call. The size is bounded by the distinct context names ever opened, never by the
number of launches — re-opening a context rewrites its own files in place
(`TerminalLauncherTests.RelaunchingAContextWritesTheSameFilesNotNewOnes`) — and each
overlay is about 60 bytes. And pruning is the dangerous direction: the app cannot tell
whether a terminal it opened is still running, and a terminal whose overlay has been
deleted does not fail — kubectl skips a missing `KUBECONFIG` entry and takes
`current-context` from the real file, so that terminal's next command runs against
whatever cluster was last switched to, silently. That is the wrong-context incident
rule 3 exists to prevent, reached by tidying up. Revisit only with a way to know which
overlays a live terminal still names.

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
- The macOS probe (iTerm2, Ghostty, the preference as an app name) and every Linux
  emulator's command flag are each emulator's documented form, unit-tested as plans and not
  run here: there was no macOS box and no Linux desktop. Ghostty's `--args -e` in particular
  depends on the Ghostty release.

## Handing a command to the terminal (FEAT-17, FEAT-27)

The exec pane's "Open this session in your terminal", node detail's node shell and their
two palette rows ("Exec in my terminal" on a pod, "Node shell in my terminal" on a node)
hand a `kubectl` command to the machine's terminal, with the context pinned exactly as for a
plain terminal (rules 1–3). `TerminalCommand` (Core) is the command; `TerminalHandoff` (App)
builds it per entry point and words the outcome; `TerminalLauncher.OpenAsync(context, command)`
plans and starts it. The resource list's row menu does not have them yet (its files were
another change's that round).

1. **A command is a fourth argument shape on each platform, never the plain shape with
   something bolted on** (the seam rule 4 warned about):
   - **Windows:** PowerShell 7, then Windows PowerShell, each given
     `-NoLogo -NoExit -EncodedCommand <base64 UTF-16LE>` whose script is
     `& 'C:\…\kubectl.exe' 'exec' '-it' …` — every argument a single-quoted literal (quotes
     doubled, the three typographic single quotes PowerShell also reads as one included).
     The encoding leaves the real command line nothing but base64 for .NET's quoting and
     PowerShell's parsing to agree on. `-NoExit` keeps the window open on whatever kubectl
     printed, a refusal included, and leaves a prompt on the cluster. cmd.exe is not offered
     for a command: its `/k` quoting rules do not round-trip an argument list. Verified on
     Windows 11 against the sandbox with both PowerShells (a word with two spaces, single
     quotes and `;` arrived in the container intact).
   - **macOS:** the command goes into its own script, `run-<hash of context and
     command>.command`, which exports `KUBECONFIG`, runs every word single-quoted, then
     `exec "$SHELL" -l`, and **removes itself as it starts** (`rm -f "$0"`): it is read once,
     so one is not left per command, while the overlay it names is kept like every overlay
     (ENG-15). Opened by the same `open` candidates as a plain terminal.
   - **Linux:** the per-emulator flag the plain shape avoided, from a deliberately narrow
     table (`TerminalLauncher.LinuxCommandPrefix`: `--` for gnome-terminal and ptyxis, `-e`
     for x-terminal-emulator, konsole, alacritty and xterm, `-x` for xfce4-terminal,
     terminator and mate-terminal, `start --` for wezterm, nothing for
     xdg-terminal-exec, kitty and foot), followed by
     `/bin/sh -c '"$0" "$@"; exec "${SHELL:-/bin/sh}"' <kubectl> <args…>`. The command comes
     from the shell's positional arguments, so no word of it is ever parsed by a shell, and
     the window holds a shell afterwards. tilix and lxterminal, whose `-e` takes one string,
     are refused for a command rather than given a string built here; an emulator the table
     does not know (the preference, `$TERMINAL`) is given xterm's `-e`.
2. **Every name is checked before it reaches any of those** (`TerminalCommand`), although the
   API server validated each one: namespace and container as RFC 1123 labels, pod and node
   as subdomains, the image in image-reference characters, a command word as printable ASCII
   without `"`. A value that fails is refused with the reason, and nothing is started.
   The check is what does not depend on a quoting rule being right; the quoting is there as
   well.
3. **What runs.** An exec runs the shell typed into the pane's box, else the one the session
   found, else `cmd` on a Windows node and `/bin/sh -c 'if [ -x /bin/bash ]; then exec
   /bin/bash; else exec /bin/sh; fi'` otherwise — one command, because a terminal can be
   handed only one where the pane probes three. It execs into the container the pane is in,
   a debug container included. A node shell is `kubectl debug node/<name> -it
   --image=<DebugContainers.DefaultImage>`, the pane's pinned BusyBox; see
   [node-operations](node-operations.md) for why it is a hand-off at all.
4. **Every outcome is stated where the gesture was made**: an InfoBar laid over the top of
   the exec pane's terminal (like the paste prompt, so the remote PTY is not resized), an
   InfoBar under node detail's chrome row, and the tab's own terminal notice for the palette
   rows. A fleet list does not offer the palette rows, because its rows belong to other tabs'
   clusters and this tab's context would pin the wrong one.

## The "Terminal" preference (FEAT-19)

`AppSettings.PreferredTerminal` (Preferences → General → Terminal; empty is automatic) is
tried before `$TERMINAL` and the probe list, and read from `settings.json` at each launch.
It exists because the probe cannot know every emulator, and on Linux nothing in the app
could set `$TERMINAL` for a GUI started from a launcher. It is one program, by name (searched
on PATH, rule 7) or full path, with no arguments; on macOS it is an application name or a
path to a `.app` (`iTerm2` is read as iTerm2's real bundle name, `iTerm`). On Windows it is a
shell; for a hand-off only a PowerShell is handed the command, and **Windows Terminal is
refused** with the reason (rule 4: its tab would be spawned by another process and not carry
`KUBECONFIG`; making it the default terminal application is the way to get it).
`Normalized` trims it and drops a value with a control character or past 1,024 characters.

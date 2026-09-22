# The machine's own terminal ("open a terminal on this cluster")

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`TerminalLauncher.cs` (Core) starts the user's own terminal with `KUBECONFIG` set and
the current context pinned to one cluster — the daily gesture people leave a GUI for,
and the one thing this app had no answer to at all. It is deliberately **not** a shell
inside the app: that needs a PTY dependency (`Porta.Pty` and its `Vanara.PInvoke` tail,
the only place this repo would ever need one), and it still would not be *your* terminal,
with your prompt, your fonts, your fzf and your kubectl plugins.

Six things are load-bearing:

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
   **Windows** starts `pwsh.exe` → `powershell.exe` → `cmd.exe` directly with the
   environment on the `ProcessStartInfo`, which still lands inside Windows Terminal
   wherever it is the default terminal application (a console-host setting, not a
   command line) and inside conhost where it is not — i.e. the item's stated fallback,
   reached by a different route. **macOS** writes a `.command` launcher script that
   exports `KUBECONFIG` and `exec "$SHELL" -l`, and opens *that* with
   `open -a Terminal`. **Linux** is the only one where the obvious thing is also the
   correct thing: `$TERMINAL`, then `xdg-terminal-exec`, then `x-terminal-emulator`,
   then the emulators, each started with **no arguments** (which every one of them reads
   as "open my default shell", and which is the only form needing no per-emulator flag
   table) and inheriting the environment normally.
5. **A missing `kubectl` warns; it never blocks.** Three reasons, and the third is the
   strongest: the terminal is useful without it (`KUBECONFIG` is what helm, k9s, stern
   and kubectx read too); kubectl may be installed a minute later; and **our PATH is not
   the terminal's PATH** — a GUI launched from Explorer, the Dock or the Store inherits
   a minimal environment, the same reason `$KUBECONFIG` never reaches it, so a probe
   miss is weak evidence about the shell that is about to open. The probe therefore also
   looks in the login-shell directories (`/usr/local/bin`, `/opt/homebrew/bin`, …), and
   the message says the PATH may be shorter here than in your shell rather than
   asserting kubectl is absent.
6. **Every outcome lands in one dismissible `infoBar` above the list**
   (`ClusterTabViewModel.TerminalNotice`, UI rules 9 and 11), and it exists because this
   command's own feedback — a window — **opens in front of the app**. Success, opened-
   without-kubectl, nothing-could-be-opened and the demo refusal all land there;
   `DescribeTerminalLaunch` is a public static so both the tests and the screenshot
   harness render the app's real words rather than a paraphrase. The no-terminal case
   prints the exact `KUBECONFIG` value in selectable text, because that is what makes
   the gesture completable by hand. Two entry points and no new always-visible control
   (UI rules 1 and 15): the ☰ menu and a Ctrl/Cmd+K entry.

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
- **`FEAT-17` ("open this exec session in my terminal") is the seam left, not built.**
  It wants `kubectl exec -it` as the command the terminal runs, which is a *fourth*
  per-platform argument shape on top of the three above — do not bolt it onto the
  no-arguments Linux path without re-reading rule 4 above.

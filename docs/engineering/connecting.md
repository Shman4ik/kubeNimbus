# Connecting: credential plugins, proxies, failures and reconnect

Everything between a kubeconfig entry and a working `ClusterClient`, and what the app says
when that does not work. Read this before changing `Kubeconfig.BuildClientSetupAsync`,
`ExecPluginPath`, `KubeconfigProxy`, `ClusterClient.RefreshCredentialsAsync`, the informer's
401 branch, `ConnectionReport`, `ConnectionFailureView`, the kubeconfig folder search or the
app-data paths. The research behind most of it is
[`docs/research/2026-08-18-connecting-to-a-cluster.md`](../research/2026-08-18-connecting-to-a-cluster.md).

Hard rule 4 runs through every section: **nothing read from a kubeconfig, and nothing a
credential plugin prints, is kept anywhere but inside the client built from it.** Every
connect, retry and refresh re-reads the file and re-runs the plugin.

## One place turns a kubeconfig entry into a client

`Kubeconfig.BuildClientSetupAsync` (`BuildClientConfigAsync` is the same call, minus the
proxy) does, on the thread pool, in this order:

1. reads the file through the library's own loader, so relative certificate paths still
   resolve against the file;
2. resolves an exec plugin's command (`ExecPluginPath`, below) on the parsed object;
3. reads and validates the cluster's `proxy-url` (`KubeconfigProxy`, below) — *before* any
   plugin runs, so a typo in the proxy does not cost an SSO prompt;
4. builds the configuration with `BuildConfigFromConfigObject`, which is where the plugin
   runs, inside `ExecCredentialCapture` so a failure is reported by what the plugin said.

It used to be `BuildConfigFromConfigFileAsync`, which does steps 1 and 4 and nothing in
between. There is no second path: the sync `BuildClientConfig` (tests and tooling only, never
the UI thread) goes through the same core.

## A bare plugin command is found where a login shell would find it (FEAT-49)

The client starts the plugin with `FileName = exec.command` verbatim, so `command: aws` was
looked up on *this process's* `PATH`. A GUI launched from Finder, the Dock, a launcher or the
Store does not get a login shell's `PATH`, so `aws` in `/opt/homebrew/bin` "did not exist" —
the most reported connection failure in every Kubernetes GUI's tracker.

`ExecPluginPath.Apply` rewrites the command, in memory, to the path found on `PATH` and then
in `TerminalLauncher.LoginShellDirectories` (`/usr/local/bin`, `/opt/homebrew/bin`,
`/opt/local/bin`, `~/.local/bin`, `~/bin`; none on Windows). The decisions:

- **Rewrite the command, not this process's `PATH`.** Mutating the process environment
  would change what every later child sees (the exec pane's shell, the terminal launcher's
  kubectl probe) for the sake of one kubeconfig entry.
- **The plugin's own `PATH` gains the missing directories too**, appended, unless the
  kubeconfig sets `PATH` for it. `gke-gcloud-auth-plugin` runs `gcloud` and kubelogin's CLI
  mode runs `az`; finding the plugin only for it to miss its own tool would move the bug one
  level down. The library applies exec `env` with the indexer, so an entry named `PATH`
  replaces the inherited one — verified, not assumed.
- **On Windows a bare name resolves through `PATHEXT`**, which is what makes a `.cmd`
  wrapper work (`CreateProcess` only ever appends `.exe`); a name that already carries an
  extension is looked up as exactly that name.
- **A command with a separator but not rooted resolves against the kubeconfig's folder**,
  which is client-go's rule.
- A command found nowhere is left alone, so the error is still the plugin's own "could not
  start" sentence, which names it; the failure view then says where it looked.

`ExecPluginPath.OverrideDirectories` is an `AsyncLocal` test seam — tests run in parallel and
the value must reach the pool continuation that builds the configuration.

## `proxy-url` is read by kubeNimbus, because the library drops it (FEAT-54)

`KubernetesClient.Aot`'s cluster model has no `proxy-url`, so its YAML reader discards the
field and every request went direct. `KubeconfigProxy.Read` reads it from the same file with
YamlDotNet's structural model (the AOT-safe half), and the proxy is applied to **both**
transports: the `SocketsHttpHandler` (`FirstMessageHandlerSetup`) every request goes through,
and the `ClientWebSocket` exec and port-forward open (`Kubernetes.CreateWebSocketBuilder`),
which the handler's proxy never reaches.

- Schemes: http, https, socks4, socks4a, socks5. SOCKS5 sends the host name to the proxy, so
  an EKS endpoint that only resolves on the far side of a bastion works.
- An unusable value throws `KubeconfigSetupException` naming the field. Connecting direct
  instead would send traffic where the kubeconfig said not to.
- `user:password@` in the URL becomes the proxy's credentials for that client only, and
  every displayed form goes through `KubeconfigProxy.Redact`. The proxy's own address is
  built from scheme, host and port — `GetLeftPart(Authority)` keeps the userinfo, and the
  first cut leaked it into `WebProxy.Address` that way (`KubeconfigProxyTests` caught it).
- Without `proxy-url` nothing changes: the ambient `HTTPS_PROXY` behaviour is untouched.

Tested against a loopback stand-in that plays an HTTP proxy for a `.invalid` host (RFC 6761
names resolve nowhere, so the request can only succeed through the proxy). SOCKS is not
exercised end to end — no SOCKS stand-in exists in the tests.

## A failed connect is a state of the content area (FEAT-51)

UI rule 9 names "disconnected" in its own list, and a failed connect used to leave the pane
blank with the whole message in a status bar that does not wrap. Now `ClusterTabViewModel
.ConnectAsync`'s catch builds a `ConnectionReport` and sets `ConnectionFailure`; both modes
render it with the one `ConnectionFailureView` (Resources in place of `ContentRows`, which is
hidden rather than wrapped because its row indices drive `ApplyDockState`; Applications in
place of its "Not connected").

- **The step, the cause, the exception's own text, the advice, the facts.** The step is one
  of `ConnectionReport`'s constants, in the order a connect runs them. The cause comes from
  the exception *type* (`HttpRequestException.HttpRequestError`, the socket error, the status
  code), never from matching message text — except the one case the app wrote the text
  itself (`NotAnApiServer`). Deterministic by construction.
- **The facts are read again after the failure**, not carried out of the connect: a connect
  can fail before it has a client, which is when the facts matter most. Kubeconfig file,
  context, cluster, server, user, sign-in method, proxy, and the plugin's `installHint` when
  it has one. **No credential is ever a fact** — the sign-in method is named ("bearer token
  in the kubeconfig"), a plugin's command is shown without its arguments, a proxy without its
  userinfo. `ConnectionReportTests.No_fact_ever_carries_a_credential` pins it.
- **The status bar gets the step only** ("Connection failed (reaching the API server).") —
  the same sentence on the page and in the bar is the ENG-29 mistake again.
- **Set before `IsConnecting` goes false**, so anything waiting for "Connection failed" finds
  the explanation in place. The smoke test's unreachable-cluster scenario is that thing: it
  exits **69** if the failure view is missing (proved with a build that dropped it).
- **Retry and a terminal.** Retry is `ReconnectCommand`; the terminal is "Open a terminal on
  this cluster", for running `aws sso login` against the right kubeconfig. The terminal's
  own notice is mirrored into the view, because the Applications page does not show the
  Resources list's notice bar.

## Reconnect, and a 401 is expiry (FEAT-53)

`ClusterClient` now holds its generated client in a field that `RefreshCredentialsAsync`
**replaces**: it re-reads the kubeconfig, re-runs the plugin, builds a new client and swaps it
in. Every pane, fleet member and palette source holds the `ClusterClient` object, not the
generated client, so one swap reaches all of them and none of them had to learn that a
reconnect exists — which is what made this cheap despite "every inspector tab holds a
`ClusterClient`". The replaced client is **retired, not disposed**: a log follow or an exec
session already open on it keeps running (up to four retired clients; the oldest is disposed
past that). It also covers a gap upstream leaves: a plugin that returns a client
*certificate* gets no refresh provider in the library, so such a connection used to go stale
for the life of the tab.

- **The informer treats 401 as expiry.** `PumpAsync` used to back off and retry with the same
  dead credential for ever. On a 401 it now calls `RefreshCredentialsAsync`, reports a
  `WatchConnectionException` with `CredentialsRejected`, relists, and retries — so a sign-in
  done in a terminal is picked up by the next attempt with nothing pressed.
- **Single-flight, coalesced.** Several watches failing together (the list, the metrics poll,
  the Applications reads) share one plugin run; without `force`, a refresh that finished in
  the last five seconds counts. Reconnect passes `force`.
- **`ReconnectCommand`** retries a failed connect (keeping an existing client and refreshing
  it in place, so nothing holding it is orphaned), or on a connected tab refreshes, re-reads
  `/version` and restarts the list. It is offered on the failure view, beside the list's
  warning when that warning is a lost or refused watch (`ConnectionWarningOffersReconnect`,
  reset whenever the warning changes so a later RBAC warning cannot inherit the button), in
  the ☰ menu, the macOS menu and the palette. Never on the demo cluster.
- An exec plugin that fails *mid-session* now reads by its own sentence in the watch banner
  (`Describe` used to print the bare exception type).

Tests: `ExecPluginAuthTests` (a scripted plugin that counts its runs and a loopback API
server that revokes tokens) and the sandbox-gated
`Refreshing_credentials_keeps_the_client_and_its_open_watch_working`. Not verified: a real SSO
expiry against a cloud provider.

## Exec-plugin auth has an end-to-end test (VER-31)

Hard rule 4 and the README had claimed exec-plugin auth works, and no test contained an
`exec:` kubeconfig that *succeeded*. `ExecPluginAuthTests` now covers: a plugin credential
reaching the API server; an expired `expirationTimestamp` re-running the plugin per request,
with the far-future control proving that is expiry-driven and not unconditional; a bare
command found in a login-shell directory; a missing plugin stated by name; a forced refresh;
and a 401 mid-watch recovering. The plugin is a `.cmd` on Windows and a `sh` script elsewhere,
and the server is `ScriptedApiServer` — a raw `TcpListener`, because `HttpListener` on Windows
needs an elevated URL reservation to listen on 127.0.0.1.

## Kubeconfig folders, and rescan on focus (FEAT-57)

A picked path can be a **folder**: `Kubeconfig.CandidatePaths` expands it on every search, so
a file dropped into it later is found by the next rescan. Settings still hold the path only.

- **Top level only**, because the folder people pick is `~/.kube`, whose `cache/` holds
  thousands of files.
- **Only files that say `kind: Config`** (YAML or JSON spelling), non-hidden, ≤ 1 MiB, in
  ordinal name order (first file wins on duplicate context names, deterministically).
  `~/.kube` also holds kubectx's state file and lock files, and offering those to the parser
  would be a "could not read" line on every rescan. A file that *does* say it is a
  kubeconfig and then fails to parse is reported, and costs only itself.
- **Rescan when the window regains focus**, not a `FileSystemWatcher`: a watcher on
  `~/.kube` fires on every write every tool makes there. `Kubeconfig.ChainFingerprint` stats
  the candidate files and a picked folder's entries; only a changed fingerprint reloads the
  context list. Open tabs are never touched. This also picks up
  `aws eks update-kubeconfig` in a terminal, for plain files.
- An empty folder is kept when picked (it is the ordinary start of the thing this is for),
  unlike a picked *file* that yields no contexts.

## The shell's two lines in the no-kubeconfig state (ENG-29)

The card's heading is `MainWindowViewModel.KubeconfigDiagnosis` (with the parser's message
when a file would not read); the status bar under it is `Status`, which says something shorter
and different ("No clusters: the kubeconfig could not be read."). They used to be one property
bound twice, and the obvious fix — shortening `Status` — silently replaced the card's heading
the first time it was tried. `KubeconfigShellTests` pins both halves. The preferences page shows
the diagnosis when there is one.

## Where the app writes its own files (ENG-39)

`AppDataDirectory.Roaming` (settings, workspace, terminal overlays) and `.Local` (discovery
cache) resolve `GetFolderPath(…, SpecialFolderOption.Create)`, then the XDG variable when it
is absolute, then the XDG default under home, then the temp directory — and never a relative
path. `GetFolderPath` returns `""` for a folder that does not exist yet, which on a fresh Linux
`HOME` put the discovery cache in `./kubeNimbus/discovery` of whatever directory the app was
started from.

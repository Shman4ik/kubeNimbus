# Connecting: credential plugins, proxies, failures and reconnect

Everything between a kubeconfig entry and a working `ClusterClient`, and what the app says
when that does not work. Read this before changing `Kubeconfig.BuildClientSetupAsync`,
`ClusterClient.Create`, `ApiServerCertificateValidator`, `ApiServerTransport`,
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

1. reads the file once, through `KubeconfigReader` (below) rather than the library's loader,
   and sets `FileName` so relative certificate paths still resolve against the file;
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
- **A command found nowhere is refused before anything starts** (S1-3), as an
  `ExecCredentialException` that names it; the failure view reads it as a plugin that could
  not be started and says where it looked. It used to be handed to `Process.Start` as the bare
  name, and .NET searches the app's own folder and the current directory before `PATH`, on
  Windows (`CreateProcess`) and Unix (`Process.ResolvePath`) alike — so a file called `aws`
  in whatever folder the app was started from would have run with the user's cluster in its
  environment. Go refuses the same lookup (`exec.ErrDot`).
- **A `PATH` entry that is not fully qualified is skipped** (`.`, `bin`, an empty entry), in
  `TerminalLauncher.FindExecutable`, which both the plugin lookup and the kubectl probe use,
  for the same reason. "Fully qualified" is the host's own rule, because the probe is a
  `File.Exists` on this host.

`ExecPluginPath.OverrideDirectories` is an `AsyncLocal` test seam — tests run in parallel and
the value must reach the pool continuation that builds the configuration.

## The kubeconfig is parsed by kubeNimbus, not by the client library

`KubeconfigReader` reads every kubeconfig the app loads — the context list, the connect, the
failure report — into the library's own `K8SConfiguration` model, which then goes to
`BuildConfigFromConfigObject` exactly as before. The library's loaders
(`LoadKubeConfig*`, `BuildConfigFromConfigFile*`, `BuildDefaultConfig`) are listed in
`BannedSymbols.txt`, and calling one is a build error (RS0030).

The reason is a version coupling that was invisible until it broke. `KubernetesClient.Aot`
deserializes kubeconfig through a YamlDotNet `StaticContext` that it ships *precompiled*,
against one exact YamlDotNet (16.3.0 for 19.0.2). YamlDotNet 18 added `HasParseMethod` to
`ITypeInspector`, so with 18.x in the graph that precompiled inspector no longer implements its
interface, and the first kubeconfig read throws `TypeLoadException` — no cluster reachable at
all, while the build, the NativeAOT publish and the plain launch check stay green. The bump was
merged and reverted twice (#15, then again inside #77), Dependabot was told to ignore
YamlDotNet, and our YamlDotNet was stuck wherever the client was last built. Upstream moved to
YamlDotNet 18 in its v20.0.84 tag, but that release never reached NuGet (an expired publishing
key, kubernetes-client/csharp#1872), and the next release will pin a newer version again.
Owning the ~200 lines of reading takes the client's YAML layer off every path the app runs, so
the two packages are upgraded independently.

What it reads is exactly the model's fields, with the library's semantics: unknown keys are
ignored, a plain `null`/`~`/empty value is null (so kubectl's own `clusters: null` is an empty
list) and a quoted one is text, booleans take YAML 1.1's spellings, only the first document is
read, and aliases resolve. It is built on
YamlDotNet's *event parser*, not its representation model, for one reason: the representation
model throws on a key given twice, where the old loader kept the last value — a hand-edited
kubeconfig that worked yesterday must not stop loading because the parser changed. Three
deliberate differences, all towards kubectl: an empty file is an empty configuration (the old
loader threw a `NullReferenceException`), an `as-user-extra` value written as a list (its
client-go shape, which the model cannot hold) is left out of the model instead of failing the
file — and read, with `as-uid`, into `KubeconfigDocument.Impersonations` instead (see
"Impersonation" below) — and the parse error names its line and column (YamlDotNet 18 no
longer puts them in the message). `KubeconfigReaderTests` pins each of these.

## `proxy-url` is read by kubeNimbus, because the library drops it (FEAT-54)

`KubernetesClient.Aot`'s cluster model has no `proxy-url`, so its YAML reader discards the
field and every request went direct. `KubeconfigReader` reads it in the same pass as the rest of
the file (`KubeconfigDocument.ProxyUrl`), `KubeconfigProxy` validates it, and the proxy is applied to **both**
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

## The API server's certificate is checked by kubeNimbus, not by the library (S1-1)

`KubernetesClient.Aot` — every version up to 19.0.2 and upstream main — validates a kubeconfig
that carries a `certificate-authority(-data)` in `Kubernetes.CertificateValidationCallBack`,
which returns "the chain builds to that CA" from inside its chain-errors branch and never looks
at `RemoteCertificateNameMismatch`. A cluster CA is never in the system store, so every such
kubeconfig takes that branch, and **any server certificate that CA signed was accepted for any
host name**: a kubelet's serving certificate on EKS, AKS, k3s or kubeadm with
`serverTLSBootstrap`, presented by whoever can sit on the path, received the bearer token. The
EKU check does work (a clientAuth-only certificate is refused), and the same callback guards the
WebSocket transport, so exec and port-forward were affected too.

`ClusterClient.Create` therefore replaces the library's check on both transports with
`ApiServerCertificateValidator`, kubectl's rules (Go's `crypto/tls`):

- **The name** is `tls-server-name` when set, otherwise the URL's host (IDN form, no IPv6
  brackets), matched with `X509Certificate2.MatchesHostname(allowWildcards: true,
  allowCommonName: false)`: subject alternative names only, IP entries included, never the
  common name (Go stopped reading it in 1.15).
- **With the kubeconfig's CA**, a fresh `X509Chain` with `CustomRootTrust` and exactly those
  certificates — never the system store — `RevocationMode.NoCheck` (kubectl does no
  revocation either), the serverAuth EKU in `ApplicationPolicy`, and the intermediates the
  server sent in `ExtraStore`. The chain is checked before the name, Go's order.
- **Without one**, the TLS stack's own chain verdict against the system's trust stands, and
  only the name check is ours.
- **`insecure-skip-tls-verify: true`** still accepts anything (the library's accept-all is left
  in place), and is stated: a `TLS` fact in the connection report, and a warning in the status
  bar for as long as such a tab is connected (`ClusterTabViewModel.IsTlsUnverified`). The status
  bar is the least chrome that cannot be missed: it is already the row for "something about
  this connection is worth knowing", it is on screen in both modes, it costs nothing on a
  verified tab, and the notice has its own column so no later status or watch warning can
  replace it.

**How it is installed, and why that way.** The library's `SocketsHttpHandler` is private. It
is captured through `FirstMessageHandlerSetup` (chained after the proxy's setup), and the
callback is replaced after `new Kubernetes(...)` returns, because the library assigns its own
in `InitializeFromConfig`, which runs after that hook; no request has been sent at that point.
If the handler was not captured, the connect fails rather than proceed under the library's
check. For the WebSocket, `StreamConnectAsync` calls the non-virtual `ExpectServerCertificate`
(the flawed check) and then the virtual `BuildAndConnectAsync`, so `CreateWebSocketBuilder`
always returns an `ApiServerWebSocketBuilder` whose `BuildAndConnectAsync` installs the
validator last, just before connecting. The builder also carries the proxy, the
`tls-server-name` `Host` header and the impersonation headers.

**`tls-server-name` is sent, not only checked.** The library sets `Host` to it in its own
`SendRequestRaw`, and `SocketsHttpHandler` takes SNI from `Host`; `ClusterClient.SendRequestAsync`
did not, and "worked" only because a name mismatch was ignored. `ApiServerRequestHandler`, the
`DelegatingHandler` in front of the library's handler, now sets it on every request, ours and
the generated client's alike.

**A refusal is an exception, so the report can say what was wrong.** The validator throws
`ApiServerCertificateException` (an `AuthenticationException`) instead of returning false; the
TLS stack carries it out as the inner cause of the request's `HttpRequestException`, so it is
attached to exactly the connection that failed — a value recorded on the side could not promise
that once connections are pooled. `ConnectionReport` finds it anywhere in the cause chain and
says "Setting up TLS: the API server's certificate is not valid for "10.0.0.1"; it is for
ip-10-0-0-1.ec2.internal", with advice to set `tls-server-name` (and not to turn verification
off). The watch banner's `Describe` uses the same sentence.

**The library is kept anyway.** It carries the authentication zoo — exec plugins and their
refresh, OIDC, every client-key format — and replacing it is a much larger change than
replacing one callback. The typed API is no longer used at all (pods go through the same
generic watch as every other kind), which leaves the configuration, the handler, the
credentials and the exec/port-forward WebSocket helpers as the whole of what is used.

`ApiServerTlsTests` drives every case through the real connect path — a temp kubeconfig,
`ClusterClient.ConnectAsync`, a request — against `ScriptedApiServer` serving TLS with
certificates from `TestPki`: the wrong DNS name (refused, and the server never receives a
request, so never the token), an IP SAN, `https://localhost`, `tls-server-name`, another CA,
a clientAuth-only EKU, no CA with an untrusted certificate, skip-verify, and exec and
port-forward against the wrong name. Removing the replacement in `ClusterClient.Create` turns
six of them red; that was checked.

## Impersonation is honoured (S1-8)

`as`, `as-uid`, `as-groups` and `as-user-extra` are parsed by the library into its model and
never sent, so the app acted as the base identity — with more rights than kubectl has in the
same context, which is the opposite of what a kubeconfig that impersonates was written for.
Now `KubeconfigReader` reads them in client-go's own shapes (`as-user-extra` is a list per key;
`as-uid` is not in the library's model) into `KubeconfigDocument.Impersonations`, keyed by user
name case-insensitively with the first entry winning, which is how the library finds the user
entry a context names. They are sent as `Impersonate-User`, `Impersonate-Uid`,
`Impersonate-Group` and `Impersonate-Extra-<key>` (the key percent-escaped as client-go's
`headerKeyEscape` does), over HTTP by `ApiServerRequestHandler` and over the WebSocket by
`ApiServerWebSocketBuilder`. The connection report's facts gain "Acts as", naming the identity
and never a credential.

**At most one group, and one value per extra key — or the connect is refused.** The API server
reads each `Impersonate-Group` header *line* as one group and never splits on commas. .NET's
HTTP client cannot send a header twice: it joins the values into one line
(`Impersonate-Group: a, b`, measured on this SDK), which the server would read as one group
called "a, b"; the WebSocket options cannot hold two values at all. Acting as that group would
be acting as somebody the kubeconfig did not name, so such an entry fails at "Reading the
kubeconfig" with a sentence that says why. So do groups, a UID or extras without `as`, which
client-go refuses too. Both checks run before any plugin, like the proxy's.

`ImpersonationTests` checks the headers on the wire over both transports, that nothing is sent
without `as`, both refusals and the report fact. Not run: a live check against a real API
server's impersonation authorizer (the sandbox was not up when this was built).

## A plugin's stderr is redacted before it is shown (S1-6)

`ExecCredentialCapture.Translate` replaces JWT-shaped strings (`eyJ…` segments joined by dots)
and the value after `Bearer` with `[redacted]` in what the plugin printed, before it reaches the
failure view or the watch banner. A verbose or failing plugin can echo the token it got or the
request it made, and the failure view is what people screenshot into an issue. Over-matching
costs a word of a diagnosis; under-matching costs a credential.

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

**Owner-only, and written whole (S1-4).** On Linux and macOS the directories are created 0700
(`AppDataDirectory.CreatePrivate`) and the files 0600, and `Program.Main` tightens existing
`kubeNimbus` directories on every launch (`SecureExisting`), because every profile written
before this came out 0755/0644 under umask 022. The files hold no credential, but they are not
nothing: context names (an EKS context is an ARN with the account ID in it), kubeconfig paths,
namespaces and the clusters' resource catalogs — kubectl keeps its kubeconfig 0600. The
settings, workspace and terminal-overlay writes go through `WriteAllTextAtomically` (a
temporary file beside the target, then one `File.Move(overwrite: true)`), as the discovery cache
already did: a crash mid-write used to leave a truncated `settings.json`, which loads as "no
settings", and a half-written overlay read by an open terminal's next kubectl would drop the
context it pins. The Unix-mode assertions in `AppDataDirectoryTests` skip on Windows, where the
per-user AppData folders are already private; they have not run on this Windows machine.

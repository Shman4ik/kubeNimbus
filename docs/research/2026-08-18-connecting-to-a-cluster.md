# Connecting to a cluster — kubeconfig, auth, and first-run reliability

*Research pass, 2026-08-18. Competitor demand and marketing emphasis for the one
step every user takes before any other feature matters.*

Six research reports exist under `docs/research/` and not one of them covers
connecting. `docs/BACKLOG.md` has 140 rows and no row about it either. This
report closes that gap.

The framing question is a claim kubeNimbus already makes. `CLAUDE.md` hard rule 4
says the app persists no credentials and re-resolves through the kubeconfig chain
at connect time, and that "exec-plugin auth (`aws eks get-token`,
`gke-gcloud-auth-plugin`, `azure kubelogin`) must work". `README.md` line 157
repeats it to the public: *"exec-plugin auth (EKS, GKE, AKS) resolved through the
kubeconfig at connect time"*. Nothing in this repository has ever tested that
claim — `grep` over `tests/` finds no kubeconfig with an `exec:` block anywhere —
and the competitors' issue trackers are full of the specific ways it goes wrong.

---

## What was searched

- Issue trackers, via GitHub's issue search sorted by reactions:
  [freelensapp/freelens](https://github.com/freelensapp/freelens/issues),
  [aptakube/aptakube](https://github.com/aptakube/aptakube/issues),
  [kubernetes-sigs/headlamp](https://github.com/kubernetes-sigs/headlamp/issues),
  [lensapp/lens](https://github.com/lensapp/lens/issues),
  [derailed/k9s](https://github.com/derailed/k9s/issues).
  Queries: exec plugin / PATH / not found, kubeconfig, proxy, certificate + TLS +
  self-signed, OIDC + SSO + login, accessible namespaces, default namespace,
  connection + timeout.
- Product marketing: Aptakube's landing and feature pages (via search index —
  `aptakube.com` is blocked by this session's egress proxy), Lens's docs and
  blog (`docs.k8slens.dev`, `k8slens.dev`, `lenshq.io` all blocked; reached
  through search snippets and through the Lens issues that quote them), the
  [FreeLens README](https://raw.githubusercontent.com/freelensapp/freelens/main/README.md),
  the [KubeUI README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md).
- The client library kubeNimbus actually ships:
  `KubernetesClient.Aot` 19.0.2, read both as the packaged assembly in
  `~/.nuget/packages/kubernetesclient.aot/19.0.2/` and as
  [upstream source](https://github.com/kubernetes-client/csharp/blob/master/src/KubernetesClient/KubernetesClientConfiguration.ConfigFile.cs).
  Several findings below are *measured against the shipped binary*, not inferred.
- kubeNimbus itself: `src/KubeNimbus.Core/Kubeconfig.cs`,
  `src/KubeNimbus.Core/ClusterClient.cs`,
  `src/KubeNimbus.Core/TerminalLauncher.cs`,
  `src/KubeNimbus.App/ViewModels/ClusterTabViewModel.cs`,
  `src/KubeNimbus.App/ViewModels/MainWindowViewModel.cs`,
  `src/KubeNimbus.App/Views/MainWindow.axaml`,
  `src/KubeNimbus.App/Views/ClusterTabView.axaml`.

**A caveat on numbers.** GitHub's issue-search pages render a reaction count when
sorted by reactions, and that is where the counts below come from. The fetch tool
occasionally conflates that column with the comment count, and individual issue
pages report "Reactions are currently unavailable" to it. Treat every count as
*approximately* right and the *ordering* as reliable; the argument in every case
below rests on the issue's content, not on its score.

---

## 1. What actually fails on a first connection

Ranked by weight of evidence.

### 1.1 The exec plugin cannot be found, because a GUI's PATH is shorter than a shell's

This is the single most-reported connection failure in the field, in every
tracker, for six years, and it is the same bug each time.

| Product | Issue | What the user sees |
|---|---|---|
| Lens | [lensapp/lens#2079](https://github.com/lensapp/lens/issues/2079) — closed, milestone 5.2.6 | `User Exec command "/usr/local/bin/aws" not found on host.` |
| Lens | [lensapp/lens#878](https://github.com/lensapp/lens/issues/878) | "can't open any clusters from kube config exec not found in $PATH" |
| Lens | [lensapp/lens#8109](https://github.com/lensapp/lens/issues/8109) — open | `Failed to GET 'https://127.0.0.1:52756/7c713451c33e0926551505d9d7c47470/version'` |
| FreeLens | [freelensapp/freelens#1057](https://github.com/freelensapp/freelens/issues/1057) — open, labelled bug | kubelogin not found; reporter: *"environment PATH variables are not being handled or propagated correctly"* |
| FreeLens | [freelensapp/freelens#1171](https://github.com/freelensapp/freelens/issues/1171) | cannot access AWS EKS |
| FreeLens | [freelensapp/freelens#208](https://github.com/freelensapp/freelens/issues/208) — open | GCP cluster fails after switching from OpenLens |
| Headlamp | [headlamp#1885](https://github.com/kubernetes-sigs/headlamp/issues/1885) — closed in v0.28.0 | "external auth (exec) not working (azure/kubelogin and aws-iam-authenticator)"; reporter says it blocks their migration off Lens |
| Headlamp | [headlamp#1582](https://github.com/kubernetes-sigs/headlamp/issues/1582) — open, ~18 | "Flatpak: cannot execute `aws eks get-token`" |
| Headlamp | [headlamp#358](https://github.com/kubernetes-sigs/headlamp/issues/358) — open | "[Desktop]: can't login when using aws sso authentication scheme" |
| Headlamp | [headlamp#1716](https://github.com/kubernetes-sigs/headlamp/issues/1716) — open, ~35 | "Cannot connect to AWS EKS with SSO" |
| k9s | [derailed/k9s#2070](https://github.com/derailed/k9s/issues/2070) | AKS: "The azure auth plugin has been removed" |

The mechanism is not in dispute. Electron's own ecosystem has a package whose
entire purpose is this: [`sindresorhus/fix-path`](https://github.com/sindresorhus/fix-path)
— *"Useful for Electron apps as GUI apps on macOS and Linux do not inherit the
`$PATH` defined in your dotfiles (.bashrc/.bash_profile/.zshrc/etc)"*. The
standard user workaround, repeated across all of the above, is either
`sudo ln -s /opt/homebrew/bin/aws /usr/local/bin/aws` or hardcoding
`command: /opt/homebrew/bin/aws` into the kubeconfig.

**kubeNimbus already makes this exact argument, one step short of applying it.**
`TerminalLauncher.FindKubectl`'s doc-comment says a probe miss is weak evidence
because *"a GUI launched from Explorer, the Dock or the Microsoft Store inherits a
minimal environment — the same reason `$KUBECONFIG` does not reach it"*, and
`TerminalLauncher.LoginShellDirectories` already enumerates `/usr/local/bin`,
`/opt/homebrew/bin`, `/opt/local/bin`, `~/.local/bin`, `~/bin`.
`TerminalLauncher.FindExecutable` is `public static` and takes the PATH as a
parameter. The machinery exists in Core; nothing points it at the exec plugin.
`README.md` line 133 already warns the user about `$KUBECONFIG` for the same
reason and says nothing about `aws`.

**What kubeNimbus does today**, read off the shipped client: the plugin is started
by `KubernetesClientConfiguration.CreateRunnableExternalProcess`, which sets
`process.StartInfo.FileName = config.Command` verbatim and `UseShellExecute = false`
([source](https://github.com/kubernetes-client/csharp/blob/master/src/KubernetesClient/KubernetesClientConfiguration.ConfigFile.cs)).
A bare `aws` is therefore resolved against *this process's* PATH and nothing else.

**How the field handles it.** Nobody augments PATH. FreeLens's answer is to ship
the binaries: its Flatpak *"adds wrappers for the `aws`, `doctl`,
`gke-gcloud-auth-plugin`, and `kubelogin` tools"*
([README](https://raw.githubusercontent.com/freelensapp/freelens/main/README.md)) —
which is a sandbox workaround that also happens to be the most complete answer
anyone ships. Lens exposes a kubectl binary path preference but not a general PATH.

### 1.2 An interactive exec plugin blocks the whole app

[headlamp#5148](https://github.com/kubernetes-sigs/headlamp/issues/5148) — open,
labelled **blocker**: *"Desktop app shows 'Request timed-out' when kubeconfig
contains AKS clusters with expired kubelogin tokens"*. The diagnosis in the issue
is worth reading in full, because kubeNimbus has the same shape of bug arrived at
independently: the backend authenticates *every* cluster in the kubeconfig at
startup; when an Azure AD token has expired, `kubelogin` starts a device-code flow
and blocks; the device code is printed where no user can see it; the app hangs and
then reports a generic timeout.

**kubeNimbus's version of this.** `ClusterTabViewModel.ConnectAsync` calls
`ClusterClient.Connect(Context)` **synchronously, before its first `await`**, so it
runs on the UI thread:

```csharp
var client = ClusterClient.Connect(Context);        // synchronous
var version = await client.GetServerVersionAsync();
```

`Connect` → `Kubeconfig.BuildClientConfig` → `BuildConfigFromConfigFile` →
`ExecuteExternalCommand`, which is a blocking `process.WaitForExit` bounded by
`KubernetesClientConfiguration.ExecTimeout`, **default two minutes**
(`get_ExecTimeout`/`set_ExecTimeout` confirmed present in the shipped
`KubernetesClient.Aot.dll`). And `MainWindowViewModel.RestoreWorkspaceAsync`
awaits `AddTabAsync` per snapshot, each of which awaits
`tab.ConnectCommand.ExecuteAsync(null)` — so **restored tabs authenticate serially
on the UI thread at launch**. Four EKS tabs at ~1.5 s of `aws eks get-token` each is
roughly six seconds of frozen window immediately after a ~150 ms first frame; one
expired `kubelogin` is up to two minutes of frozen window. That is headlamp#5148's
failure with a different runtime.

Two smaller details of the same code path, both already in kubeNimbus's favour and
worth *not* proposing: the client sets `CreateNoWindow = true`
(`set_CreateNoWindow` present in the shipped assembly), so kubeNimbus does not have
[headlamp#2346](https://github.com/kubernetes-sigs/headlamp/issues/2346) —
*"Terminal randomly pops up on Windows"*, also labelled blocker, closed in PR #2756.
And the client sets `spec.interactive` from `Environment.UserInteractive`, which is
true for a GUI process, so an `interactiveMode: IfAvailable` plugin will believe it
has a terminal it does not have. That is the mechanism behind the hang above.

### 1.3 The user cannot list namespaces, and the client has no way to ask for one

The most under-appreciated first-run failure, and it has the longest paper trail of
anything in this report: Lens
[#486](https://github.com/lensapp/lens/issues/486),
[#745](https://github.com/lensapp/lens/issues/745),
[#1535](https://github.com/lensapp/lens/issues/1535) (*"Manage a cluster with no
access to namespace listing nor default one"* — reporter: *"there is nothing listed
in any section"*),
[#2010](https://github.com/lensapp/lens/issues/2010),
[#2528](https://github.com/lensapp/lens/issues/2528),
[#3558](https://github.com/lensapp/lens/issues/3558) (*"Gracefully degrade features
when missing certain permissions"*),
[#4002](https://github.com/lensapp/lens/issues/4002),
[#5459](https://github.com/lensapp/lens/issues/5459). Lens shipped
**Accessible Namespaces** as a per-cluster setting in response, and FreeLens
inherited it — [freelensapp/freelens#1113](https://github.com/freelensapp/freelens/issues/1113)
asks to enter several at once, which only makes sense if the feature exists.
Aptakube has the same complaint from the other side:
[aptakube#364](https://github.com/aptakube/aptakube/issues/364), *"aptakube opens
window with `default` namespace, which we do not have rights to manage"* — the
reporter's whole ask is that the app honour `context.namespace` from the kubeconfig
instead.

**kubeNimbus today.** `Kubeconfig.LoadContextsAsync` reads
`ctx.ContextDetails?.Namespace` into `ClusterContext.Namespace` — and the only
consumer anywhere in the app is `ClusterSwitcherViewModel` line 106, which prints
it as a subtitle. `ClusterTabViewModel` opens every tab on
`SelectedNamespace = AllNamespaces` (line 123) and `RefreshNamespacesAsync` catches
the 403 into a one-line `ConnectionWarning` (line 952). The namespace `ComboBox` in
`ClusterTabView.axaml` line 238 is bound to `NamespaceOptions`, is not editable, and
after a refused list contains exactly one entry: `"All namespaces"`. So a
namespace-scoped service account — the ordinary enterprise case — connects
successfully, then gets a 403 on every list it can make, with no control anywhere in
the window that can narrow the query. This is lens#1535 exactly.

### 1.4 Credentials expire while the app is open, and the app does not notice

- [aptakube#400](https://github.com/aptakube/aptakube/issues/400) — open, feature
  request, ~7: *"Auto refresh kube tokens"*. Tokens expire after 24 h; the user has
  to go back to the home screen and press refresh, and forgets.
- [aptakube#558](https://github.com/aptakube/aptakube/issues/558) — closed:
  *"Aptakube silently displays stale cached data on read views after auth session
  expires (SDM and AWS SSO)"*. The reporter's words: read views *"continue displaying
  stale cached data indefinitely with no error, warning, or 'disconnected'
  indicator"*, while mutations correctly error.
- [aptakube#179](https://github.com/aptakube/aptakube/issues/179) — `BadCertificate`
  on Teleport session expiry.
- [aptakube#505](https://github.com/aptakube/aptakube/issues/505) — 403 after OIDC
  login.

**kubeNimbus is structurally better here and still has a real gap.** Better,
because a failed watch calls `connectionLost`, which
`ClusterTabViewModel` line 1205 turns into a visible `ConnectionWarning` — the app
does not go silently stale the way aptakube#558 describes. And because
`ExecTokenProvider` refreshes on `expirationTimestamp` with a 30 s skew
([source](https://github.com/kubernetes-client/csharp/blob/master/src/KubernetesClient/Authentication/ExecTokenProvider.cs)),
and every manual watch/log request goes through `Kubernetes.Credentials
.ProcessHttpRequestAsync`, so bearer-token refresh already works mid-session.

The gaps are three, all narrow:

1. **There is no reconnect.** `grep` finds no `ReconnectCommand` on
   `ClusterTabViewModel`. A tab holds one `Kubernetes` instance for its entire life.
   After `aws sso login` in a terminal, the only route back is closing and reopening
   the tab.
2. **An exec plugin that returns a client certificate gets no refresh provider at
   all.** Upstream installs `ExecTokenProvider` only inside
   `if (AccessToken != null)`. Certificate-returning plugins (some Rancher and
   Teleport setups) therefore go stale for the process lifetime.
3. **A 401 is retried forever with the same dead credentials.**
   `ClusterClient.WatchAsync`'s `catch (Exception ex)` reports and backs off; it
   never rebuilds the client, so the exec plugin is never re-run.

Note that rule 4 forbids the obvious-sounding fix. "Auto refresh kube tokens" must
mean *re-running the plugin*, never *caching what it returned*.

### 1.5 Proxies, bastions and private clusters

- [aptakube#69](https://github.com/aptakube/aptakube/issues/69) — *"Add support for
  proxy-url kubeconfig option"*, **closed**: Aptakube ships it.
- [freelensapp/freelens#1200](https://github.com/freelensapp/freelens/issues/1200) —
  open, bug: `proxy-url` is honoured by the app but lost in the terminal, because
  *"Lens does not use original kubeconfig, but creates its own temporary, without
  proxy-url option"*.
- [freelensapp/freelens#766](https://github.com/freelensapp/freelens/issues/766) —
  ~22 reactions, the highest-scoring connection issue found in that tracker:
  private EKS behind an SSH bastion over SOCKS5, `error: unable to upgrade
  connection: error dialing backend: dial tcp: lookup ...eks.amazonaws.com: no such
  host`, because the hostname resolves only on the bastion. Closed via PR #1700.
- [headlamp#1966](https://github.com/kubernetes-sigs/headlamp/issues/1966) — open,
  labelled **blocker**, ~18: *"It is possible to add proxy settings to use the
  software inside a corporate proxy network?"*, zero comments in four years.
- [lensapp/lens#4608](https://github.com/lensapp/lens/issues/4608) — "Can't connect
  to cluster behind a corporate proxy".
- [aptakube#347](https://github.com/aptakube/aptakube/issues/347) — Teleport proxy;
  [aptakube#188](https://github.com/aptakube/aptakube/issues/188) — "Connect through
  tunnels".

**kubeNimbus today.** `strings` over the shipped `KubernetesClient.Aot.dll` finds no
`proxy-url` / `ProxyUrl` symbol at all — every `Proxy` hit is an API-server proxy
*subresource* (`ConnectGetNamespacedPodProxyAsync` and friends). So the kubeconfig
field is silently ignored. The handler is a `SocketsHttpHandler` with no
`UseProxy=false`, so `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` from the environment
*are* honoured, including `socks5://` — but by the same argument as §1.1, a GUI
launched from Explorer or the Dock has none of those set.

### 1.6 One bad file in the chain takes out every context

[headlamp#2612](https://github.com/kubernetes-sigs/headlamp/issues/2612) — open:
*"Error setting up clusters, please load a valid kubeconfig file"* against a file
that works with kubectl and Lens.
[headlamp#3017](https://github.com/kubernetes-sigs/headlamp/issues/3017) — "error
loading kubeconfig files: error reading kubeconfig file".
[derailed/k9s#829](https://github.com/derailed/k9s/issues/829) — *"Unable to connect
if using a KUBECONFIG variable with multiple files"*, where each file works alone
with `--kubeconfig`.
[aptakube#330](https://github.com/aptakube/aptakube/issues/330) — a release that
turned merging off by default broke a team's generated-kubeconfig workflow badly
enough that they pinned an old version.

**kubeNimbus today.** `Kubeconfig.LoadContextsAsync` loops the chain and calls
`KubernetesClientConfiguration.LoadKubeConfigAsync(path)` with no per-file guard;
`MainWindowViewModel.LoadContextsAsync` catches once, at the top. One malformed or
unreadable file in a five-entry `$KUBECONFIG` therefore yields zero contexts and a
single status line. `DiscoverPaths` already filters non-existent files, so this is
specifically about files that exist and do not parse. There is precedent for the fix
in this repo: `KubeconfigCandidate` exists precisely so the empty state can say
*where* it looked rather than just "none found", and this is the same idea one level
down.

### 1.7 Things checked and found already handled

Stated so nobody proposes them:

- `insecure-skip-tls-verify` and `certificate-authority-data` are parsed and honoured
  by the shipped client (`insecure-skip-tls-verify`, `InsecureSkipTLSVerify`,
  `SkipTlsVerify` all present).
- No console window flashes on Windows during auth (`CreateNoWindow = true`),
  unlike [headlamp#2346](https://github.com/kubernetes-sigs/headlamp/issues/2346).
- Exec-plugin *token* refresh on `expirationTimestamp` already works.
- A picked kubeconfig path that yields no contexts is deliberately not remembered,
  and a picked path that has since moved is reported as `missing … (picked)` rather
  than dropped — both shipped in the store-readiness pass.
- The no-kubeconfig empty state already names every path searched, offers the file
  picker and a Rescan, and offers the demo cluster.
- The app never writes to the user's kubeconfig; `TerminalLauncher` goes out of its
  way to avoid it. Aptakube markets exactly this property (§3).

---

## 2. How the field surfaces a failed connection

The brief's framing is right, and the field bears it out: the difference between a
five-second fix and an uninstall is whether the message names the exec plugin.

**Good.** Lens's `User Exec command "/usr/local/bin/aws" not found on host.`
([lens#2079](https://github.com/lensapp/lens/issues/2079)) names the binary and its
absolute path. Every workaround in every downstream blog post is derived from that
one sentence.

**Bad, from the same product.**
[lens#8109](https://github.com/lensapp/lens/issues/8109) reports
`Failed to GET 'https://127.0.0.1:52756/7c713451c33e0926551505d9d7c47470/version'`
— Lens's own internal proxy address and a content hash, describing a failure that
happened three layers below. The user cannot act on it and the issue is still open.
Lens's diagnostics are not bad; they are *inconsistent*, because the error crosses a
proxy process boundary and sometimes survives it.

**Worse.** Headlamp's `Bad Gateway`
([headlamp#1341](https://github.com/kubernetes-sigs/headlamp/issues/1341),
[#1716](https://github.com/kubernetes-sigs/headlamp/issues/1716)) and
`Failed to get authentication information: Request timed-out`
([#5148](https://github.com/kubernetes-sigs/headlamp/issues/5148)) for what is
actually a device-code prompt waiting on a terminal nobody can see. Headlamp's own
maintainers diagnose the cause in
[#5402](https://github.com/kubernetes-sigs/headlamp/issues/5402): *"`SetupProxy`
silently falls back to Go's default transport when credential setup fails, masking
actual errors."* That is a maintainer describing the exact anti-pattern.

**Worst.** k9s prints `Unable to connect to context` for essentially every cause —
[#829](https://github.com/derailed/k9s/issues/829),
[#1619](https://github.com/derailed/k9s/issues/1619),
[#1622](https://github.com/derailed/k9s/issues/1622),
[#1717](https://github.com/derailed/k9s/issues/1717),
[#1916](https://github.com/derailed/k9s/issues/1916),
[#2070](https://github.com/derailed/k9s/issues/2070),
[#3032](https://github.com/derailed/k9s/issues/3032). Seven issues, one string.

**Nobody ships a connection doctor.** No "test this context" affordance was found in
Lens, FreeLens, Aptakube, Headlamp, KubeUI or k9s. The nearest thing is
[headlamp#5353](https://github.com/kubernetes-sigs/headlamp/issues/5353) — *"Show
actionable troubleshooting steps on 'Bad Gateway' connection error screen"* —
proposing copy-able diagnostic commands, plain-language likely causes and a link to
a troubleshooting page. It is **maintainer-authored, open and unimplemented**. Note
the authorship honestly: that makes it evidence of a recognised problem and of an
unfilled space, not evidence of user demand. The user demand is the seven k9s issues
and the string `Bad Gateway`.

**Where kubeNimbus sits.** Better than k9s and Headlamp, worse than it should be.
`ConnectAsync`'s catch sets `Status = $"Connection failed: {ex.Message}"`, and
because kubeNimbus talks to the API server directly with no proxy process in
between, that message is the exec plugin's own — something like
`external exec failed due to: An error occurred trying to start process 'aws' …`.
That is genuinely more actionable than `Bad Gateway`. But:

- It renders **only** in the bottom status bar (`MainWindow.axaml` line 292), in a
  `TextBlock` with no wrapping, in a `Grid` column beside a second status text.
- The content area shows **nothing**. `ClusterTabView.axaml` has explicit visuals for
  `IsListLoading` (601), `IsListEmpty` (606) and `IsFilterEmpty` (621), and after a
  failed connect all three are false — so the pane is the blank rectangle UI rule 9
  exists to forbid. Rule 9 names "disconnected" in its own list of states.
- There is **no retry**. No `ReconnectCommand` exists.
- The exec plugin's **stderr is discarded**. Upstream exposes it as a static event,
  `KubernetesClientConfiguration.ExecStdError` (`add_ExecStdError` /
  `remove_ExecStdError` are in the shipped assembly), and only redirects the child's
  stderr *if something is subscribed*: `RedirectStandardError = captureStdError != null`.
  Nothing in `src/` subscribes. So `aws`'s own "The config profile (x) could not be
  found", gcloud's "Reauthentication required", and kubelogin's device-code URL are
  all read by nobody.
- The plugin's **`installHint` is parsed and never shown**. `InstallHint` is on the
  model (`get_InstallHint` in the shipped assembly) and appears in none of upstream's
  exception messages. This field exists in the
  [client-go credential plugin spec](https://kubernetes.io/docs/reference/access-authn-authz/authentication/#client-go-credential-plugins)
  for precisely the five-second-fix case, and EKS and AKS kubeconfigs routinely set it.

---

## 3. What the products lead with

**Aptakube is the only product that markets this at all, and it markets it hard.**
It has a whole landing page, [aptakube.com/zero-config](https://aptakube.com/zero-config)
— *"Zero-config setup to get you started in minutes"* — whose claims are: *"It works
with your existing Kubeconfig; There is nothing to install on your clusters; They
don't need to be interconnected in any way; They can even be in different regions or
clouds"*, and *"Aptakube will never make changes to your Kubeconfig files, and is a
read-only tool from a configuration perspective"*. Its
[multi-cluster page](https://aptakube.com/multi-cluster) leads with *"Connect to
multiple clusters simultaneously, as if it was one big cluster"*. It also ships a
documented [kubeconfig extension](https://aptakube.com/docs/context-extension) — a
custom `aptakube` key on a context that sets its icon and ordering.

Two things follow. The zero-config pitch is almost word-for-word kubeNimbus's hard
rule 4 sold as a benefit, and kubeNimbus's README states it as a security property
(*"nothing is ever copied into app storage"*) rather than as a convenience. And the
context extension is a *shareable, in-kubeconfig* version of what kubeNimbus does
with heuristics plus `WorkspaceSettings.EnvironmentOverrides`: one is per-machine,
the other travels with the team's generated kubeconfig.

**Lens markets kubeconfig handling as a preference, not a headline.** "Kubeconfig
Sync" — *Preferences → Kubernetes → Manage Kubeconfigs*, with "Sync Kubeconfig
file(s)" and "Sync Kubeconfig folder(s)" — is documented and has its own bug tail
([lens#2507](https://github.com/lensapp/lens/issues/2507) real-time sync,
[#6834](https://github.com/lensapp/lens/issues/6834),
[#7846](https://github.com/lensapp/lens/issues/7846)). Per-cluster settings include
an HTTP proxy field and *"Allow untrusted Certificate Authorities"* for corporate
networks that rewrite certificates, plus Accessible Namespaces (§1.3). Lens also
blogs about bastion-host access as a differentiator
([lenshq.io/blog/lens-bastion-access](https://lenshq.io/blog/lens-bastion-access/) —
blocked here, title and framing from search).

**FreeLens** has no feature list in its README. Its only connection-related claim is
the Flatpak's bundled `aws` / `doctl` / `gke-gcloud-auth-plugin` / `kubelogin`
wrappers, i.e. the fix for §1.1 presented as packaging detail.

**Headlamp** and **KubeUI** market nothing here. KubeUI's README says only
*"Connect to Kubernetes clusters from a desktop UI"*.

**Conclusion for §3: this is invisible plumbing until it breaks — with one
exception, and the exception is the closest competitor in positioning.** For
kubeNimbus the marketing pressure is therefore weak and the demand pressure is
strong. Every proposal below is labelled accordingly, and most are demand.

---

## 4. Explicitly refused, and why

Recorded so these are not re-proposed, and so nothing below is smuggling one in.

- **Caching a token, certificate or plugin output to avoid re-running the exec
  plugin.** Refused outright by hard rule 4. This matters because
  [aptakube#400](https://github.com/aptakube/aptakube/issues/400) ("auto refresh kube
  tokens") reads like a request for it. Item ENG-C below is *re-running the plugin*,
  which is rule 4 being honoured, not bent.
- **An in-app OIDC / device-code login flow** — kubeNimbus obtaining and holding a
  token itself, the way Headlamp does in-cluster
  ([headlamp#2614](https://github.com/kubernetes-sigs/headlamp/issues/2614),
  [#5401](https://github.com/kubernetes-sigs/headlamp/issues/5401)). Same refusal:
  the app would be holding a credential. The honest alternative is to run the user's
  own plugin and *show what it says*, which is FEAT-B.
- **Bundling `aws` / `kubelogin` / `gcloud`, FreeLens-Flatpak style.** A ~62 MB
  payload is a positioning claim (`CLAUDE.md`, Mission), and a bundled `aws` would not
  share the user's SSO cache anyway, so it would fix the PATH error and produce a
  credentials error in its place.
- **Nothing found in this pass argues against any stated non-goal.** No evidence
  pushed toward cluster provisioning, in-cluster agents, telemetry or long-range
  metrics history. There is no "challenges a stated non-goal" note in this report.
- **Per-cluster "allow untrusted Certificate Authorities"** is listed as a proposal
  below only because Lens ships it and the human should see the pressure. The
  recommendation in the row is to refuse it: it duplicates a kubeconfig field the
  user can already set, and it would put a TLS-weakening switch into
  `settings.json` for a tool whose `SECURITY.md` makes claims about exactly this.

---

## Proposed backlog items

Rows for the Inbox table in `docs/BACKLOG.md`. Priority column left blank
deliberately. Nothing here has been promoted, and nothing here was validated
against a live cloud cluster — see the last row, which exists to fix that.

| — | Item | Evidence | Size | Priority | | Notes |
|---|---|---|---|---|---|---|
| — | **Connect off the UI thread, and bound the exec-plugin timeout** — *done when opening four exec-auth tabs at launch does not block the window, and a hung credential plugin is a per-tab failure with a stated cause rather than a frozen app* | Demand (bug, found here; the same shape is a competitor blocker): [headlamp#5148](https://github.com/kubernetes-sigs/headlamp/issues/5148), open and labelled blocker — eager per-cluster auth plus an interactive `kubelogin` blocks the app and reports only "Request timed-out" | S | | `ClusterTabViewModel.ConnectAsync` calls `ClusterClient.Connect` **before its first `await`**, so `BuildConfigFromConfigFile` → `ExecuteExternalCommand`'s blocking `WaitForExit` runs on the UI thread, bounded by `KubernetesClientConfiguration.ExecTimeout` (**default 2 minutes**, settable — `set_ExecTimeout` confirmed in the shipped `KubernetesClient.Aot` 19.0.2). `MainWindowViewModel.RestoreWorkspaceAsync` awaits each `AddTabAsync` in turn, so restored tabs authenticate **serially**. Directly contradicts the ~150 ms-to-first-frame headline. Fix is a `Task.Run` plus a shorter `ExecTimeout`; the timeout value is a real decision (too short breaks a legitimately slow SSO round trip). Prerequisite for FEAT-D's retry being usable |
| — | **Resolve exec-plugin binaries the way a login shell would** — *done when a kubeconfig with a bare `command: aws` connects from an app launched from Explorer, Finder or the Dock on a machine where `aws` is in `/opt/homebrew/bin`* | Demand, the strongest in this report — six years, five trackers: [lens#2079](https://github.com/lensapp/lens/issues/2079), [lens#878](https://github.com/lensapp/lens/issues/878), [freelens#1057](https://github.com/freelensapp/freelens/issues/1057), [freelens#1171](https://github.com/freelensapp/freelens/issues/1171), [freelens#208](https://github.com/freelensapp/freelens/issues/208), [headlamp#1885](https://github.com/kubernetes-sigs/headlamp/issues/1885), [headlamp#1582](https://github.com/kubernetes-sigs/headlamp/issues/1582), [headlamp#358](https://github.com/kubernetes-sigs/headlamp/issues/358), [k9s#2070](https://github.com/derailed/k9s/issues/2070). Mechanism stated by [`sindresorhus/fix-path`](https://github.com/sindresorhus/fix-path): *"GUI apps on macOS and Linux do not inherit the `$PATH` defined in your dotfiles"* | S | | **This app already makes the argument and stops one step short.** `TerminalLauncher.FindKubectl`'s doc-comment gives the reasoning verbatim; `TerminalLauncher.LoginShellDirectories` and `public static FindExecutable` are in Core and reusable unchanged. Upstream sets `StartInfo.FileName = config.Command` with `UseShellExecute = false`, so a bare `aws` is resolved against this process's PATH only. Two mechanisms, and the choice is the decision: (a) prepend `LoginShellDirectories` to the process's own `PATH` at startup — tiny, also improves `TerminalLauncher`'s probe, but mutates global state; (b) load the kubeconfig, rewrite `exec.command` to an absolute path **in memory**, and use `BuildConfigFromConfigObject` — targeted, no global mutation, more code. Neither persists anything, so no rule-4 tension. README line 157 currently claims this works |
| — | **Say what the credential plugin said** — surface the plugin's stderr and its `installHint` in the connection error | Demand: every issue in the row above is a user who had to derive the cause from a workaround blog post. Contrast the good message ([lens#2079](https://github.com/lensapp/lens/issues/2079): *"User Exec command \"/usr/local/bin/aws\" not found on host"*) with the bad one from the same product ([lens#8109](https://github.com/lensapp/lens/issues/8109): `Failed to GET 'https://127.0.0.1:52756/<hash>/version'`) | S | | Cheapest row here and pure diagnostics. `KubernetesClientConfiguration.ExecStdError` is a **static event** (`add_ExecStdError` in the shipped assembly) and upstream only redirects the child's stderr when something is subscribed — nothing in `src/` is, so `aws`'s "The config profile could not be found", gcloud's "Reauthentication required" and kubelogin's device-code URL are all discarded today. `installHint` is parsed into the model (`get_InstallHint`) and appears in none of upstream's exception strings, though it is in the [client-go credential plugin spec](https://kubernetes.io/docs/reference/access-authn-authz/authentication/#client-go-credential-plugins) for exactly this. Two cautions: the event is static and global, so it needs correlating to the connecting tab; and a plugin's stderr **can contain a token**, so it must be treated as untrusted text shown in place and never written anywhere |
| — | **A connection-failure state in the content area: what was tried, what failed, and Retry** | Demand for the diagnostics (§2: seven k9s issues sharing one string — [#829](https://github.com/derailed/k9s/issues/829), [#1619](https://github.com/derailed/k9s/issues/1619), [#1622](https://github.com/derailed/k9s/issues/1622), [#1717](https://github.com/derailed/k9s/issues/1717), [#1916](https://github.com/derailed/k9s/issues/1916), [#2070](https://github.com/derailed/k9s/issues/2070), [#3032](https://github.com/derailed/k9s/issues/3032); Headlamp's `Bad Gateway`, [#1341](https://github.com/kubernetes-sigs/headlamp/issues/1341)/[#1716](https://github.com/kubernetes-sigs/headlamp/issues/1716)). The *design* is marketing-side and unfilled: [headlamp#5353](https://github.com/kubernetes-sigs/headlamp/issues/5353) proposes exactly this and is **maintainer-authored, open, unimplemented**; no shipped product in the field has a "test this context" affordance | M | | **This is UI rule 9 not being applied to its own named state.** `ClusterTabView.axaml` has explicit visuals for `IsListLoading`, `IsListEmpty` and `IsFilterEmpty`, and after a failed connect all three are false — the pane is the blank rectangle the rule forbids, with the whole message in a non-wrapping status-bar `TextBlock`. There is no `ReconnectCommand` anywhere. Content should be the resolved facts, which the app already holds and never shows: kubeconfig file, context / cluster / user, server URL, auth method (and for exec, the command and where it resolved to). Same panel is worth showing on *success* as "connection details". Prerequisites: the row above for the message, and the first row so Retry cannot re-freeze the window. Wants an `infoBar` (UI rule 11), no new always-visible control (UI rule 1) |
| — | **Honour the context's `namespace`, and let a namespace be entered when listing them is refused** | Demand, the longest paper trail here: [lens#1535](https://github.com/lensapp/lens/issues/1535) (*"there is nothing listed in any section"*), [lens#486](https://github.com/lensapp/lens/issues/486), [lens#2010](https://github.com/lensapp/lens/issues/2010), [lens#2528](https://github.com/lensapp/lens/issues/2528), [lens#4002](https://github.com/lensapp/lens/issues/4002), [lens#5459](https://github.com/lensapp/lens/issues/5459), [lens#3558](https://github.com/lensapp/lens/issues/3558) — Lens shipped **Accessible Namespaces** as a per-cluster setting and FreeLens inherited it ([freelens#1113](https://github.com/freelensapp/freelens/issues/1113)). [aptakube#364](https://github.com/aptakube/aptakube/issues/364) is the same complaint about the same missing behaviour | S–M | | `Kubeconfig.LoadContextsAsync` already reads `ctx.ContextDetails?.Namespace` into `ClusterContext.Namespace`; the **only** consumer in the app is `ClusterSwitcherViewModel` line 106, printing it as a subtitle. Tabs open on `SelectedNamespace = AllNamespaces` and the picker (`ClusterTabView.axaml` line 238) is a non-editable `ComboBox` that holds exactly one entry after a 403. So a namespace-scoped service account connects and then 403s on everything with no control that can narrow the query. Two halves, separable: honouring `context.namespace` is small; a typed/pinned namespace list for the refused case is the Accessible-Namespaces equivalent and needs a `settings.json` shape. Note the counter-pressure — aptakube#364's reporter wanted the honouring, [aptakube#364](https://github.com/aptakube/aptakube/issues/364)'s title reads as the opposite because Aptakube ships a `default` override; keep it a preference either way |
| — | **Reconnect: re-resolve credentials through the kubeconfig without closing the tab, and treat a 401 as expiry rather than as a transient fault** | Demand: [aptakube#400](https://github.com/aptakube/aptakube/issues/400) (~7, "Auto refresh kube tokens" — user must go to the home screen and press refresh, and forgets), [aptakube#558](https://github.com/aptakube/aptakube/issues/558) (read views *"continue displaying stale cached data indefinitely with no error, warning, or 'disconnected' indicator"* after AWS SSO / StrongDM expiry), [aptakube#179](https://github.com/aptakube/aptakube/issues/179) (Teleport), [aptakube#505](https://github.com/aptakube/aptakube/issues/505) | M | | **Rule-4 tension, stated: this must mean re-running the plugin, never caching what it returned.** kubeNimbus is already ahead of aptakube#558 — `connectionLost` surfaces a warning rather than going silently stale — and bearer-token refresh already works, because `ExecTokenProvider` honours `expirationTimestamp` with a 30 s skew and every manual watch request goes through `Kubernetes.Credentials.ProcessHttpRequestAsync`. Three real gaps: no `ReconnectCommand` exists at all, so `aws sso login` in a terminal needs the tab closed and reopened; upstream installs `ExecTokenProvider` **only** when the plugin returned a token, so certificate-returning plugins never refresh; and `ClusterClient.WatchAsync`'s catch backs off forever without rebuilding the client. Rebuilding a `ClusterClient` mid-tab touches every inspector tab holding a `ClusterClient?` — that coupling is the real cost, not the auth |
| — | **Honour the kubeconfig cluster's `proxy-url`** | Demand + parity with a shipped competitor: [aptakube#69](https://github.com/aptakube/aptakube/issues/69) **closed — Aptakube ships it**; [freelens#1200](https://github.com/freelensapp/freelens/issues/1200) (open bug about losing it); [freelens#766](https://github.com/freelensapp/freelens/issues/766) (~22, the highest-scoring connection issue in that tracker — private EKS behind an SSH bastion over SOCKS5); [headlamp#1966](https://github.com/kubernetes-sigs/headlamp/issues/1966) (open, labelled blocker, ~18, zero comments in four years); [lens#4608](https://github.com/lensapp/lens/issues/4608) | M | | Confirmed missing in the dependency, not merely unwired: `strings` over the shipped `KubernetesClient.Aot.dll` finds **no** `proxy-url`/`ProxyUrl` symbol — every `Proxy` hit is an API-server proxy subresource. So kubeNimbus must read the field itself and set a proxy on the handler. Partly mitigated already: the handler is a `SocketsHttpHandler` with no `UseProxy=false`, so `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` (including `socks5://`) work — but by this report's own §1.1 argument a GUI inherits none of them, which is what makes the kubeconfig field the reliable route. Scope boundary: this is proxy-url only. Teleport ([aptakube#347](https://github.com/aptakube/aptakube/issues/347)) and tunnels ([aptakube#188](https://github.com/aptakube/aptakube/issues/188)) are not this item |
| — | **One unreadable file in the kubeconfig chain should cost that file, not every context** | Demand: [headlamp#2612](https://github.com/kubernetes-sigs/headlamp/issues/2612) (open — "Error setting up clusters, please load a valid kubeconfig file" against a file kubectl and Lens both accept), [headlamp#3017](https://github.com/kubernetes-sigs/headlamp/issues/3017), [k9s#829](https://github.com/derailed/k9s/issues/829) (multi-file `$KUBECONFIG` fails where each file works alone), [aptakube#330](https://github.com/aptakube/aptakube/issues/330) | S | | `Kubeconfig.LoadContextsAsync` loops the chain calling `LoadKubeConfigAsync(path)` with no per-file guard and `MainWindowViewModel.LoadContextsAsync` catches once at the top, so one malformed file in a five-entry `$KUBECONFIG` yields zero contexts and one status line. `DiscoverPaths` already filters files that do not *exist*, so this is specifically files that exist and do not parse. The precedent is in the same file: `KubeconfigCandidate` carries `Exists` and `Source` so the empty state can say where it looked — this is the same idea one level down, and the failure belongs in the same "Searched:" list. Deliberate care needed on kubectl's merge semantics: skipping a file must not silently change which duplicate context name wins |
| — | **"Who am I on this cluster" — `SelfSubjectReview`, beside the existing access review** | Demand, moderate and precedent-heavy rather than upvoted: `kubectl auth whoami` exists for this ([docs](https://kubernetes.io/docs/reference/kubectl/generated/kubectl_auth/kubectl_auth_whoami), [KEP-3325](https://github.com/kubernetes/enhancements/blob/master/keps/sig-auth/3325-self-subject-attributes-review-api/README.md)) and two krew plugins predate it ([rajatjindal/kubectl-whoami](https://github.com/rajatjindal/kubectl-whoami), [mollonado/kubectl-whoami](https://github.com/mollonado/kubectl-whoami)). It is the question behind [aptakube#505](https://github.com/aptakube/aptakube/issues/505) (403 after OIDC login) and [headlamp#4198](https://github.com/kubernetes-sigs/headlamp/issues/4198) (impersonation on EKS) | S | | `grep` finds no `SelfSubjectReview` in `src/`. The RBAC pane already posts `SelfSubjectRulesReview` and `SubjectAccessReview`, so this is a third call in an established shape and lands in an existing surface. It is the honest counterpart to the exec-plugin work above: "the plugin ran and the server thinks I am `arn:aws:iam::…:role/dev`" is the answer to *"I connected but everything is 403"*, and no amount of client-side reasoning can produce it. Degrades on clusters older than 1.26 / where the API is disabled — that must be a stated state, not an error (UI rule 9) |
| — | **Kubeconfig folder sync: point at a directory, pick up files added later** | **Marketing emphasis, not demand — stated plainly.** Lens markets it as a preference (*Preferences → Kubernetes → Manage Kubeconfigs*, "Sync Kubeconfig file(s)" / "Sync Kubeconfig folder(s)"), with its own bug tail ([lens#2507](https://github.com/lensapp/lens/issues/2507), [#6834](https://github.com/lensapp/lens/issues/6834), [#7846](https://github.com/lensapp/lens/issues/7846)); Aptakube has an open request for the recursive version ([aptakube#310](https://github.com/aptakube/aptakube/issues/310)) and shipped a custom kubeconfig location ([aptakube#26](https://github.com/aptakube/aptakube/issues/26), closed). No strongly-upvoted user issue asking for it was found in any tracker | M | | kubeNimbus already has most of the value: picked paths persist in `WorkspaceSettings.KubeconfigPaths`, the empty state offers a file picker, and ☰ → "Rescan kubeconfig" reloads without a restart. What is missing is a *directory* and a watcher. Two cautions. `AppSettings`/`WorkspaceSettings` hold **paths only** and must continue to (rule 4) — a directory is still a path. And a `FileSystemWatcher` on `~/.kube` is a background thread that fires on every `aws eks update-kubeconfig`, so debouncing and a rescan-on-focus alternative are worth weighing before adding one. The cheap half — accept a directory in the existing picked-paths list, expanded at rescan — may be all of it |
| — | **VER: exec-plugin auth end to end, against a scripted credential plugin** — *done when a kubeconfig whose user is an `exec:` block pointing at a local script connects, the token is refreshed when `expirationTimestamp` passes, a missing binary produces the app's stated error, and a plugin that writes to stderr has that text surfaced* | This report's premise. `CLAUDE.md` hard rule 4 and `README.md` line 157 both claim exec-plugin auth works, and `grep` over `tests/` finds **no kubeconfig with an `exec:` block anywhere** — the claim has never been executed | S | | Needs no cloud account and no credentials: a credential plugin is any program that prints `{"apiVersion":…,"kind":"ExecCredential","status":{"token":…,"expirationTimestamp":…}}`, so a shell script (or a second `dotnet` entry point, for Windows) is a complete fake. Pairs with the sandbox: k3s in Docker plus a static token the script returns gives a real end-to-end auth. This is the row that turns every other item here from argued to observed, and it is the one that should be validated first if only one is |
| — | **Decision only: per-cluster "allow untrusted certificate authorities"** — *done when the decision and its reasoning are in `CLAUDE.md`, whether or not anything is built* | Marketing/parity, weak demand: Lens ships it as a per-cluster setting for *"corporate networks that re-write certificates"*; [aptakube#528](https://github.com/aptakube/aptakube/issues/528) (MicroK8s 1.34 X.509 v1 rejection) and [headlamp#1716](https://github.com/kubernetes-sigs/headlamp/issues/1716) (`x509: certificate signed by unknown authority`) are the failures it would paper over | S | | **Recommend refusing, and the row exists so the refusal is on record rather than an omission.** kubectl's answer is `insecure-skip-tls-verify` in the kubeconfig, which the shipped client already honours (`InsecureSkipTLSVerify`/`SkipTlsVerify` confirmed present) and which the user can set themselves — so this duplicates an existing control while moving a TLS-weakening switch into `settings.json`, for a tool whose `SECURITY.md` makes claims in this exact area. No rule-4 tension (it stores no credential); the tension is with the security model the app advertises. If it is ever taken, the honest form is per-cluster, off by default, and visible in the connection-details panel from FEAT-D — never a global preference |

---

## Where no evidence was found

Recorded because a clean negative is worth as much as a positive here.

- **No product ships a "test this context" or connection-doctor affordance.** Lens,
  FreeLens, Aptakube, Headlamp, KubeUI, k9s: none. The only artefact is
  [headlamp#5353](https://github.com/kubernetes-sigs/headlamp/issues/5353), open and
  maintainer-authored.
- **No demand was found for a GUI-side kubeconfig *editor*** — adding, editing or
  deleting contexts from inside the client. Aptakube markets the opposite as a
  virtue (*"read-only tool from a configuration perspective"*), and kubeNimbus's
  `TerminalLauncher` already refuses to write to the file on the same reasoning.
- **k9s users were not found asking a GUI for anything connection-specific.** k9s's
  own connection diagnostics are the weakest in the field (§2), so the traffic runs
  the other way. The k9s comparison stays what it already is: keyboard flow, not
  connectivity.
- **No demand was found for SSH-tunnel or bastion management inside the client**
  beyond honouring `proxy-url` and the ambient proxy environment.
  [freelens#766](https://github.com/freelensapp/freelens/issues/766) and
  [aptakube#188](https://github.com/aptakube/aptakube/issues/188) are both asking the
  client to *get out of the way* of a tunnel the user already has, which is the
  architecture kubeNimbus has by default — it has no proxy process to disable.
- **Nothing found argues against a stated non-goal.**

# kubeNimbus threat model

For Anthropic's OSS Scanner. The image is built by `.oss-scanner/Dockerfile`; this file says what to look at, how
to exercise it offline, and how we rate what you find. **The properties the app claims are in `SECURITY.md`,
"Security model"**: a break in any of them is a finding, and its "Out of scope" list holds here too. `CLAUDE.md` has
the reasons behind most of the code named below.

## What this project does and where untrusted input enters

kubeNimbus is a desktop Kubernetes client (.NET 10, Avalonia). `src/KubeNimbus.Core` is the engine (KubernetesClient,
kubeconfig, list+watch, logs, exec, port-forward, server-side apply); `src/KubeNimbus.App` is the UI. The user and
their kubeconfig are trusted: a kubeconfig can run programs through exec-plugin auth by design, exactly as with
`kubectl`. What is not trusted:

- **Everything a cluster returns.** Anyone who can write some objects or some output in a cluster the user opens
  (another tenant, a CI pipeline, a compromised workload, the author of a CRD, an Argo CD Application or a Helm
  chart) may be attacking whoever opens it with broader rights. That covers object names, labels, annotations, owner
  references, Events, CRD schemas and printer columns, discovery documents, Helm release Secrets (base64 + gzip),
  Argo CD status, container logs, and the byte stream of an exec session.
- **A malicious or impersonated API server**, which can answer anything, including what a real server would refuse.
  The TLS check in `ApiServerCertificateValidator` and `ApiServerTransport` is what stands between the user's token or
  client key and that server.
- **Other local users** on Linux and macOS, who must not be able to read or plant the files the app writes
  (`AppDataDirectory`, `Settings/AppSettingsStore`, `App/WorkspaceStore`, `App/GridLayoutStore`; `PRIVACY.md` lists
  each one).

## Components that matter most / least

- Most, in `src/KubeNimbus.Core`: `ApiServerCertificateValidator`, `ApiServerTransport` (TLS and impersonation
  headers), `Kubeconfig`, `KubeconfigReader`, `KubeconfigProxy` (`proxy-url`), `ExecPluginPath` and
  `TerminalLauncher` (what gets started, and never from the current directory), `ResourceDescriptor.PathSegment` and
  every place a name taken from cluster data builds a request path, `ClusterClient.*` (Exec, PortForward, Logs in
  `ClusterClient.cs`, Helm, ArgoCd, Dynamic, Debug), `InvisibleCharacters`, `BoundedLineReader`, `YamlJson`,
  `GoJson`, `ClusterJson`, `SimpleJsonPath`, `Applications/GitCompareLink`.
- Most, in `src/KubeNimbus.App`: `ViewModels/TerminalEscapes` and `Views/ExecView` (the exec terminal: escape
  sequences, emulator replies, `ViewModels/ExecPaste`), `ViewModels/WorkloadLogsTabViewModel` (log rendering),
  `ViewModels/YamlEditorTabViewModel` (apply, dry run), `SensitiveClipboard`, and every confirm on a destructive
  action (delete, drain, rollout restart, prune sync, run-now, resume), including the production-cluster rule in
  `ClusterEnvironment`. The places that hand a URL to the OS shell: `ViewModels/ApplicationPageViewModel`,
  `ViewModels/IngressDetailTabViewModel`, `ViewModels/PortForwardTabViewModel`.
- In scope but lower: `.github/workflows` (token scope, script injection from a tag or input) and the release
  scripts under `scripts/` (downloaded tools are sha256-pinned).
- Out of scope: `src/KubeNimbus.App/Demo` (a built-in fake dataset), `tools/Screenshot`, `tests/`, `docs/`,
  `design/`, `installer/`, the sandbox scripts, and `shared/nimbusUi` (styles, window chrome and hotkeys shared with
  pgNimbus).

## How to exercise it

The image has every NuGet package restored and a k3s binary. Nothing needs the network.

```sh
dotnet test --project tests/KubeNimbus.Core.Tests/KubeNimbus.Core.Tests.csproj -c Release --no-build
dotnet test --project tests/KubeNimbus.App.Tests/KubeNimbus.App.Tests.csproj -c Release --no-build
# one class (run the test executable; --project with a positional csproj silently runs nothing):
tests/KubeNimbus.Core.Tests/bin/Release/net10.0/KubeNimbus.Core.Tests --treenode-filter "/*/*/PathSegmentTests/*"
```

- **A loopback stand-in is the usual reproducer.** `tests/KubeNimbus.Core.Tests/ScriptedApiServer.cs` is a scripted
  HTTP or HTTPS server on 127.0.0.1 that answers whatever a handler returns, and `TestPki.cs` makes the CAs and
  certificates; the `*HttpTests.cs` classes and `ApiServerTlsTests` show the shape. It can serve what a malicious
  API server would, which a real one refuses to store.
- **A real API server, when the shape of real objects matters**: `. .oss-scanner/services.sh` starts k3s with no
  agent (the API server only, on 127.0.0.1:6443, kubeconfig `/etc/rancher/k3s/k3s.yaml`); `kubectl` works against it.
  There is no node, so pods never run: logs, exec and port-forward cannot be exercised against it, only against the
  stand-in. The live tests in `tests/KubeNimbus.Core.Tests/Live` are written for the full sandbox (a node and the
  demo workloads and CRDs of `scripts/manifests`), so pointed at this server with
  `KUBENIMBUS_TEST_KUBECONFIG=/etc/rancher/k3s/k3s.yaml` most of them fail. Read them for how to drive Core against
  a real server rather than run them as a suite.
- `tests/KubeNimbus.App.Tests` are view-model tests with no display and no cluster. There is no display, so the app
  itself cannot be launched.

## How we rate severity

- **Critical**: code execution on the user's machine caused by cluster data or an API server (a log line, an exec
  session's output, an object, a Helm release, an Argo CD Application, a link); the user's token, client key or
  exec-plugin output sent to any host other than the cluster's own server or the proxy its kubeconfig names.
- **High**: a write to a cluster the user did not start, or to a different object, namespace or cluster than the one
  the UI named (a name from cluster data steering a request path, a lookup returning another object than its
  reference); TLS verification of the API server bypassed; impersonation dropped, so the app acts as the base
  identity; credential material written to app storage; input that makes the exec terminal run a command the user
  did not type (an emulator reply written back, a filtered paste that is not).
- **Medium**: a destructive action without the confirmation `SECURITY.md` promises (production clusters included);
  a Secret value revealed without the explicit toggle, or kept in clipboard history; app files readable or writable
  by another local user; a hostile object, log or response that hangs or crashes the app with modest input.
- **Low**: a freeze that needs very large input; spoofing of what the UI shows (bidi or zero-width characters,
  terminal sequences) unless it misleads a destructive confirmation, which is Medium.

Rate by what someone who can write some objects in a cluster, or answer as its API server, can make happen to a user
who opens it.

## Anything to leave alone

- `SECURITY.md`'s "Out of scope": an attacker who already controls the kubeconfig, the machine or a cluster-admin
  account; anything the cluster's own RBAC permits the user to do; vulnerabilities in Kubernetes or a workload.
- Exec-plugin auth running the command a kubeconfig names, and a kubeconfig folder added in Preferences trusting every
  file in it: both are documented and inherent to the format. How the command is *found* (`ExecPluginPath`) is in
  scope.
- `insecure-skip-tls-verify: true` in a kubeconfig disables verification on purpose, as in `kubectl`.
- What other objects carry in the clear (a Helm release's values, an Argo CD Application's inline values, a
  container's logs) is shown as `helm` and `kubectl` show it; Secret masking covers Secrets and references to them.
- The cluster-wide "who can do X?" view is a labelled local scan of RBAC objects, not an authorization decision.
- Unsigned release binaries, SmartScreen and Gatekeeper warnings.
- Vulnerabilities inside KubernetesClient, Avalonia or the .NET runtime, unless kubeNimbus uses them unsafely.
  Report those upstream.

## Reports and patches

One root cause per report. Put the reproducer in as a test that fails before the patch and passes after it, usually
against `ScriptedApiServer`; name the trust boundary it crosses and point at file and line. Patches should follow
`CLAUDE.md`: no UI types in `KubeNimbus.Core`, streaming and cancellation kept intact, and the fix in the shared
check (`ResourceDescriptor.PathSegment`, `ApiServerCertificateValidator`, `InvisibleCharacters`) rather than at one
call site when that is where it belongs.

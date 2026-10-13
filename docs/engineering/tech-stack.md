# Tech stack

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "Tech stack" section keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

## Tech stack

- **net10.0** everywhere. NativeAOT is the shipping config.
- **KubeNimbus.Core** — references ONLY the official Kubernetes client, via the
  **`KubernetesClient.Aot`** package (source-generated serialization). NEVER swap
  it for the reflection-based `KubernetesClient` — that one does not survive
  NativeAOT. Kubeconfig files are read by `KubeconfigReader`, **never** by the
  library's loaders (`LoadKubeConfig*`, `BuildConfigFromConfigFile*`,
  `BuildDefaultConfig`): those only work with the exact YamlDotNet the client was
  compiled against, which pinned ours to 16.3.0 for two months. `BannedSymbols.txt`
  makes calling one a build error — see
  [connecting](connecting.md).
- **The library's certificate validation callback is replaced, never trusted** (2026-10-07).
  Its check accepts any certificate the kubeconfig's CA signed for any host name, so
  `ClusterClient.Create` installs `ApiServerCertificateValidator` (kubectl's rules: the
  kubeconfig's CA only — every root of a bundle, where the library kept the first — plus the host
  name or `tls-server-name`) on the HTTP handler and, through
  `ApiServerWebSocketBuilder`, on exec and port-forward; `ApiServerTlsTests` are what pin it, and
  six of them go red if the replacement is removed. Kubeconfig impersonation (`as`, `as-groups`,
  …) is sent by kubeNimbus too, because the library never sends it, and a user's `tokenFile` is
  read by kubeNimbus on every connect and refresh, because the library's model has no field for it. The library is kept anyway:
  it carries the authentication zoo (exec plugins and their refresh, OIDC, every key format),
  which is worth far more than one callback. Its typed API is no longer used anywhere — every
  kind, pods included, goes through the generic JSON watch — so a new feature does not start
  using it. See [connecting](connecting.md).
- **KubeNimbus.App** — Avalonia 12 (Fluent theme, the platform's own UI face or Inter
  and the bundled JetBrains Mono NL for code — see UI rule 23 — DataGrid,
  AvaloniaEdit for YAML, `SvcSystems.UI.Terminal` over `XTerm.NET` for the exec
  pane — see "The exec terminal"), `CommunityToolkit.Mvvm` source generators
  (`[ObservableProperty]`/`[RelayCommand]`, no hand-written INPC).
  `AvaloniaUseCompiledBindingsByDefault=true`; no reflection bindings.
- **Every AvaloniaEdit `TextEditor` goes through `Editing/EditorDefaults`** (2026-09-30,
  taken from pgNimbus, which found it first): `Apply` for the YAML editor, `ApplyViewer`
  for a read-only viewer (the Helm release's values and manifest). AvaloniaEdit's link
  rendering is on by default: it draws every URL and e-mail address in pure Blue over the
  YAML highlighter's colours, and a Ctrl+click opens it. An Argo `repoURL`, an annotation
  link and a maintainer's address are values, not links, so both options are off.
  `ApplyViewer` also turns off `AllowScrollBelowDocument`, whose room below the last line
  put a scroll bar beside a values file that fits. The YAML editor keeps that room even
  while it is read-only (a deleted object, the demo cluster), because it is the working
  editor. A new `TextEditor` calls one of the two next to `YamlSyntaxHighlighting.Attach`.
  `EditorChecks` (scenarios `ux-yaml-editor-links` and `ux-helm-editor-links`) reads each
  editor's visual lines for a `VisualLineLinkText`, checks the scroll rule, and fails for
  any editor in those two views that skipped the helper.
  **#349 (2026-10-13): the Helm check failed on Windows only, and the editor was not the
  cause.** It reported "ManifestEditor drew U+202E as an invisible character", but the
  manifest editor had no visual lines at all: bounds 0×0, not effectively visible. The
  `HelmReleaseDetail` fixture builds its tab on the offline client (`127.0.0.1:1`), waited
  one second for that load to fail, then filled the tab and cleared the error. A refused
  connection to a closed loopback port fails at once on Linux and takes about two seconds on
  Windows (measured: 2058 ms, the stack retries a refused SYN), so on Windows the
  "Could not read release" error landed after the fixture had cleared it, the view hid its
  tab control, and the manifest editor was never laid out; the values editor still had the
  visual lines of its first layout, which is why only the second editor failed. The fixture
  now waits up to ten seconds and throws if the load has not ended, and `NoLinks` checks
  that the editor drew its lines before it judges the marker, so a hidden editor is named
  as one. `InvisibleCharacterGenerator` marks U+202E on Windows as it does on Linux (the
  values editor's line 8 carried its `FormattedTextElement` in the failing run).
- **Avalonia reports every build of this repo to Avalonia unless told not to, and on a machine
  signed in to Avalonia Accelerate Community it cannot be told** (#353, 2026-10-13). The
  `Avalonia` package's `Avalonia.props` adds `AvaloniaUI` to `UsedAvaloniaProducts`, which arms
  the `AvaloniaStats` target of its dependency `Avalonia.BuildServices` (11.3.2 under Avalonia
  12.1.x) before every `CoreCompile` that is not a design-time build. The task writes a record
  under `%LOCALAPPDATA%\AvaloniaUI\BuildServices` and starts a background collector that posts it
  to `https://av-build-tel-api-v1.avaloniaui.net/api/usage`. The package's README says what a
  record holds: the build time, the project name hashed (SHA-256), output type, target framework
  and RID, the Avalonia version, the Accelerate licence tier, an anonymous machine GUID and the
  machine name hashed, the IDE or CLI, the OS and architecture, and which CI system runs it; not
  source, paths or personal data. It is build time only: nothing of it is in the shipped app, so
  the app's no-telemetry statements (`README.md`, `SECURITY.md`, `PRIVACY.md`, which speak about
  the app) stay true and are unchanged.
  The documented opt-out is the environment variable `AVALONIA_TELEMETRY_OPTOUT` (the README
  says `=1`; the task tests only that the variable exists). There is no MSBuild property, so
  `Directory.Build.props` cannot carry it: every workflow that builds (`ci.yml`, `release.yml`,
  `codeql.yml`, `warm-cache.yml`) sets it in its top-level `env`, and `scripts/test.ps1` and
  `scripts/test.sh` set it before they build. **The opt-out is ignored on a machine holding an
  Accelerate Community or Trial licence.** The task's source
  ([AvaloniaUI/Avalonia.BuildServices](https://github.com/AvaloniaUI/Avalonia.BuildServices),
  `BuildTask/AvaloniaStatsTask.cs` at `a6a4257`) resolves a tier from the project's
  `AvaloniaUILicenseKey` items *and from the tickets an Avalonia tool sign-in leaves on the
  machine* (`AccelerateTierHelper`); for Community or Trial it prints "Avalonia Accelerate
  Community requires telemetry. To opt out, please upgrade to a paid tier." and sends anyway,
  and only with no licence does it respect the variable ("respect opt-out for FOSS users").
  This repository declares no licence key, so a CI runner, which has no tickets, resolves no
  tier and the opt-out holds. The owner's machine is signed in to Accelerate Community (the
  Avalonia DevTools), and a build there with the variable set still printed that line and wrote
  its record (checked 2026-10-13, `dotnet build src/KubeNimbus.App -v:n`, with and without the
  variable); that is the licence's term, and the only ways out it offers are a paid tier or not
  being signed in. Disabling the target by clearing `UsedAvaloniaProducts` would also silence
  it, and is deliberately not done: it would sidestep the licence's condition rather than use
  the opt-out Avalonia documents.
- **KubeNimbus.Core.Tests** — TUnit on Microsoft.Testing.Platform. **NEVER add
  `Microsoft.NET.Test.Sdk` to a TUnit project — it breaks discovery.** The
  runner is pinned in `global.json` (`test.runner = Microsoft.Testing.Platform`).
- Nullable enabled; async all the way (no `.Result`/`.Wait()`); DTOs are records.

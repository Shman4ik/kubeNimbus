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
- **KubeNimbus.Core.Tests** — TUnit on Microsoft.Testing.Platform. **NEVER add
  `Microsoft.NET.Test.Sdk` to a TUnit project — it breaks discovery.** The
  runner is pinned in `global.json` (`test.runner = Microsoft.Testing.Platform`).
- Nullable enabled; async all the way (no `.Result`/`.Wait()`); DTOs are records.

# kubeNimbus

A fast, open-source Kubernetes desktop client. The Kubernetes sibling of
[pgNimbus](https://github.com/Shman4ik/pgNimbus), and an alternative to Lens.

> **Why?** The 2026 Kubernetes GUI market has a gap: Lens is subscription-only
> for commercial use and heavy Electron; OpenLens is dead; FreeLens is still
> Electron; Aptakube is polished but closed/paid; Headlamp is web-first; k9s is
> a TUI. Nobody ships **fast + open source + modern native desktop UI**.
> kubeNimbus does: Aptakube's polish, NativeAOT startup speed, MIT licensed,
> Kubernetes-first.

## Status

Early foundation. Working today:

- Kubeconfig context discovery (`$KUBECONFIG` chain + `~/.kube/config`), with
  exec-plugin auth (EKS/GKE/AKS) resolved through the kubeconfig at connect time.
- A streaming `ClusterClient`: informer-style **list + watch** of pods exposed as
  `IAsyncEnumerable`, auto-reconnect with resourceVersion resume + relist on 410
  Gone, and cancellable pod-log streaming with follow mode.
- A minimal Avalonia 12 desktop shell: context picker → connected state showing a
  **live-updating pod list**.
- Verified **NativeAOT** publish end-to-end.

See [the MVP roadmap](CLAUDE.md#mvp-scope-phase-1--build-toward-this-dont-scaffold-beyond-it)
for what's next.

## Tech stack

- .NET 10, **NativeAOT** as the shipping configuration.
- `KubernetesClient.Aot` (source-generated, AOT-safe) — the only cluster
  dependency in the engine.
- [Avalonia 12](https://avaloniaui.net/) (Fluent theme, Inter, DataGrid,
  AvaloniaEdit) + CommunityToolkit.Mvvm, compiled bindings only.
- Tests: [TUnit](https://tunit.dev/) on Microsoft.Testing.Platform, run against a
  **real local cluster**.

## Architecture

- **`KubeNimbus.Core`** — the engine. Zero UI dependencies, reusable for a future
  CLI/test harness. Kubeconfig loading and the streaming `ClusterClient` live here.
- **`KubeNimbus.App`** — the Avalonia desktop shell.
- **`KubeNimbus.Core.Tests`** — TUnit integration tests against a live cluster.

Kubeconfig is the single source of truth — the app **never persists credentials**.

## Building & running

Requires the .NET 10 SDK.

```bash
dotnet build KubeNimbus.slnx
dotnet run --project src/KubeNimbus.App
```

To develop against a throwaway local cluster and run the integration tests, see
the sandbox bootstrap recipe (k3s or kind) in [CLAUDE.md](CLAUDE.md#sandbox-cluster-bootstrap-how-tests-get-a-real-cluster).

## License

[MIT](LICENSE).

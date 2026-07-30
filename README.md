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

Phase-1 MVP shipped, plus a UX pass and the first post-MVP features. Working
today:

- Kubeconfig context discovery (`$KUBECONFIG` chain + `~/.kube/config`), with
  exec-plugin auth (EKS/GKE/AKS) resolved through the kubeconfig at connect time.
- A streaming `ClusterClient`: informer-style **list + watch** exposed as
  `IAsyncEnumerable`, auto-reconnect with resourceVersion resume + relist on 410
  Gone, and cancellable log streaming with follow mode.
- **Discovery-driven sidebar** — built-ins *and* CRDs, filterable and
  collapsible; namespace-scoped live lists for any kind.
- **Pod detail**: containers, live logs, events, owner-chain navigation.
  **Exec** into a container and **port-forward**, both over websockets.
- **YAML view/edit** with server-side apply (conflicts surfaced, force-apply
  offered) and two-step delete.
- **Live CPU/memory** from `metrics.k8s.io` in the list and per container, on
  clusters that run metrics-server — with **usage graphs over time**: a
  sparkline beside each list number and a Usage tab in pod detail (whole-pod and
  per-container charts) over the session's rolling 30-minute window.
- **Helm releases**, read-only: values, rendered manifest, notes and revision
  history — read straight from release Secrets, no Helm binary.
- **RBAC access review**: your effective permissions in a namespace (via the
  API server's own `SelfSubjectRulesReview`), and where a ServiceAccount's
  access comes from.
- Multi-cluster tabs (drag-reorder, workspace restore), Ctrl/Cmd+K command
  palette, light/dark theme, and a verified **NativeAOT** publish.

Still open: multi-cluster aggregated views. Long-range metrics history is a
non-goal — kubeNimbus graphs what the session has observed and leaves the time
series to Prometheus. See [CLAUDE.md](CLAUDE.md) for the full picture.

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

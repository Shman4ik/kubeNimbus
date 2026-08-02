# Contributing to kubeNimbus

Thanks for considering it. kubeNimbus is a small project with strong opinions,
and most of them are written down — reading the two documents below before
opening a PR will save you a round trip.

- **[CLAUDE.md](CLAUDE.md)** — the engineering contract: the tech stack, the
  hard architectural rules, the UI design rules, and *why* each one exists.
  It is the single most useful thing to read before changing anything.
- **[scripts/README.md](scripts/README.md)** — how to get a throwaway cluster
  to develop against.

## Ground rules, in short

These are the ones a PR is most likely to trip over. The full list, with the
reasoning, is in CLAUDE.md.

1. **NativeAOT is the shipping configuration.** Every dependency and every code
   path must survive trimming and AOT. No reflection-based serialization, no
   reflection-based XAML bindings (`AvaloniaUseCompiledBindingsByDefault` is on),
   no dependency that needs either. A new package that breaks the AOT publish is
   a blocker, not a follow-up.
2. **`KubeNimbus.Core` has zero UI dependencies.** No `Avalonia.*`, no
   `CommunityToolkit.Mvvm` in the engine — it stays reusable for a future CLI.
3. **Streaming and cancellation everywhere.** Lists are list+watch, not polling;
   log follow honours `CancellationToken` mid-stream. The one deliberate
   exception is `metrics.k8s.io`, which has no watch endpoint.
4. **Nothing about the server's API surface is hardcoded.** Kinds, API groups
   and versions come from discovery — including CRDs and the metrics API
   version. A hardcoded `v1beta1` is a bug.
5. **No credentials are ever persisted.** Kubeconfig is the only source of
   truth; see [SECURITY.md](SECURITY.md).
6. **Every state gets an explicit visual.** Loading, empty, disconnected,
   partial, conflict, permission-denied — never a blank rectangle that reads as
   a bug. A feature that can fail must say so where the user is looking.
7. **Keep CLAUDE.md current in the same PR.** If your change breaks a rule
   written there, change the rule there too. That file is the contract, not
   documentation of the past.

## Setting up

You need the **.NET 10 SDK**. NativeAOT publishing additionally needs a native
toolchain: MSVC on Windows, `clang` + `zlib1g-dev` on Linux, Xcode command line
tools on macOS.

```bash
git clone https://github.com/Shman4ik/kubeNimbus.git
cd kubeNimbus
dotnet build KubeNimbus.slnx
```

### A cluster to develop against

Tests run against a **real cluster**, not mocks. `scripts/sandbox-up` brings up
single-node k3s in Docker preloaded with demo workloads chosen to make every UI
surface non-empty — healthy and deliberately-broken pods, CRDs (two sharing a
Kind in different API groups), a Helm release with history, RBAC subjects
including a dangling binding, PVCs, and a CronJob firing every minute so the
live watch is visibly live:

```bash
./scripts/sandbox-up.sh          # sandbox-up.ps1 on Windows
export KUBECONFIG=.sandbox/kubeconfig.yaml
dotnet run --project src/KubeNimbus.App
```

Full flag table (second cluster for the fleet views, custom port, bare cluster)
in [scripts/README.md](scripts/README.md). Tear down with `sandbox-down`.

If your change introduces a state nothing in the sandbox produces, **add a
workload to `scripts/manifests/`** rather than only testing it against fixtures.

## Verifying a change

Run everything that applies before opening a PR:

```bash
# Build.
dotnet build KubeNimbus.slnx

# Tests. Integration tests skip cleanly with no cluster, so run them with one.
# `--project` is required: passing the csproj positionally prints a hint and
# exits 0 without running a single test under the .NET 10 MTP runner.
dotnet test --project tests/KubeNimbus.Core.Tests/KubeNimbus.Core.Tests.csproj

# Headless visual check — renders real Views into PNGs, no display needed.
dotnet run --project tools/Screenshot -- /tmp/kubenimbus-screenshots

# NativeAOT publish. Run this after ANY change that could affect trimming:
# a new package, new reflection, a new binding.
dotnet publish src/KubeNimbus.App -c Release -r linux-x64 -p:PublishAot=true -o publish/app
```

The AOT publish is the one people skip and shouldn't. `win-x64` is the shipping
target; `linux-x64` runs the same trimming analysis and catches the same class
of problem, so it is a fine stand-in if you're not on Windows — say which one
you ran in the PR.

**Known-acceptable warnings:** `Avalonia.Controls.DataGrid` emits `IL2104` /
`IL3053` trim warnings. Those are expected. Any *new* trim or AOT warning from
our own code is not.

### Screenshots

`tools/Screenshot` renders real Views bound to fixture ViewModels via
`Avalonia.Headless` and writes one `<scenario>.<light|dark>.png` per scenario
per theme. Attach the before/after pair for any UI change — it is the fastest
way for a reviewer to see what you did, and it works without a display or a
cluster. Pass a scenario-name substring as a second argument to render just one.

## Pull requests

- **Branch from `main`**, one topic per PR.
- **Explain the "why" in the description**, and say what you verified — build,
  tests, screenshots, AOT publish, live cluster or not. Being honest that
  something is unverified is fine and useful; claiming it was verified when it
  wasn't is not.
- **Update CLAUDE.md** in the same PR when your change touches anything it
  describes.
- Match the surrounding code: file-scoped namespaces, nullable enabled, async
  all the way (no `.Result`/`.Wait()`), records for DTOs, and
  `[ObservableProperty]`/`[RelayCommand]` source generators rather than
  hand-written INPC. `.editorconfig` covers formatting — please don't reformat
  files you aren't otherwise changing.
- CI runs build + tests + a linux-x64 AOT publish on every PR. It must be green.

Small fixes are welcome without discussion. For anything large — a new
top-level feature, a new dependency, a structural change — **open an issue
first**. The non-goals are firm: no cluster provisioning, no in-cluster agents,
no telemetry, and no long-range metrics history (that's Prometheus's job).

## Reporting bugs

Use the issue templates. The two things that make a Kubernetes-client bug
actionable are the **cluster distribution and version** (k3s, kind, EKS, GKE,
AKS, OpenShift…) and whether it reproduces against the sandbox cluster from
`scripts/sandbox-up`. If it does, that is nearly a fix on its own.

**Do not report security vulnerabilities as issues** — see
[SECURITY.md](SECURITY.md).

## Cutting a release (maintainers)

Releases are tag-driven; `.github/workflows/release.yml` does the rest.

1. Update `CHANGELOG.md`: rename `## [Unreleased]` to
   `## [X.Y.Z] - YYYY-MM-DD` and open a fresh `Unreleased` section. The release
   workflow reads the notes for a tag straight out of this file, so the heading
   must match the tag exactly.
2. Bump `<VersionPrefix>` in `Directory.Build.props` to `X.Y.Z`.
3. Commit and merge to `main`.
4. Tag and push:

   ```bash
   git tag -a vX.Y.Z -m "kubeNimbus vX.Y.Z"
   git push origin vX.Y.Z
   ```

The workflow NativeAOT-publishes for `win-x64`, `linux-x64`, `linux-arm64` and
`osx-arm64`, archives each, generates `SHA256SUMS.txt`, and creates the GitHub
Release with the CHANGELOG section as its body. Pre-1.0 tags
(`0.x`) are published as pre-releases automatically.

Run it with `workflow_dispatch` and `dry_run: true` to build and archive
everything without creating a release — worth doing once if you've touched the
workflow.

Note that the release binaries are **unsigned**: code signing needs
certificates this project does not have. Users get a SmartScreen prompt on
Windows and a Gatekeeper quarantine on macOS; the README says so and explains
the workaround.

## License

By contributing you agree that your contributions are licensed under the
[MIT License](LICENSE), the same terms as the project.

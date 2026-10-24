# Sandbox cluster bootstrap

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "Sandbox cluster bootstrap" section keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

## Sandbox cluster bootstrap (how tests get a real cluster)

Integration tests run against a **real local Kubernetes cluster**, not mocks.
The suite auto-discovers `./.sandbox/kubeconfig.yaml` (git-ignored — it holds
cluster CA + client certs) or `$KUBENIMBUS_TEST_KUBECONFIG`. Tests **skip
cleanly** when no cluster is reachable, so CI without one stays green.

Two things about that gate, both learned the hard way (`SandboxCluster.cs`):

- **A kubeconfig on disk is not evidence of a cluster.** `.sandbox/kubeconfig.yaml`
  outlives the container it was written for, so on a machine where Docker is simply
  not running the file still parses and still names a server nothing is listening on.
  Gating on the file's existence therefore turned "no sandbox today" into 14 failures
  and two 30-second timeouts. The gate is a **reachability probe** — one
  `GetServerVersionAsync` with a 5s budget, run once per suite run and shared by every
  test — and it catches only the ways a cluster can be *unusable from here* (nothing
  listening, TLS that cannot be established, no answer in time, a kubeconfig with no
  credentials). An authorization failure or a malformed response means a server did
  answer, so it propagates into the test that provoked it rather than becoming a
  silent skip.
- **A skipped test must be reported as skipped, not as a pass.** The gated tests used
  to `return` early, which the runner counts as success — so a run that talked to no
  cluster at all was indistinguishable from one that exercised a real API server, and
  this file's own status notes have twice recorded the first as if it were the second.
  `SandboxCluster.TryGetContextAsync` calls TUnit's `Skip.Test(reason)` instead, so the
  summary reads `succeeded: 370, skipped: 16` and names why.

**The live-verification tests (`tests/KubeNimbus.Core.Tests/Live/`, the `*LiveTests`
classes) are the place a "needs a live cluster" backlog row gets paid.** They drive the
same Core methods the app does against the sandbox and assert what the *cluster* did next,
not that the request was accepted — a restart patch with the wrong key is a 200 that rolls
nothing, and only watching the pods roll catches it. Four rules, because the sandbox is
shared with other sessions running at the same time:

- **Every mutation happens in a namespace of the run's own** (`LiveCluster.Namespace`,
  `kn-live-<run id>`, removed by an `[After(Assembly)]` hook), so two runs at once — two
  worktrees, two agents — never touch each other's objects. It used to be one constant
  shared by every run, and one run's cleanup deleted the namespace another was testing in
  (#261). A run killed before its hook leaves its namespace behind; every run's namespace
  carries the label `kubenimbus.io/live-test-run`, and each run deletes any namespace with
  that label older than two hours (`LiveCluster.IsAbandoned`, pinned by
  `LiveNamespaceSweepTests`). The age is the guard that keeps one run's sweep away from a
  run still going, and the label key is new so the sweep can never reach a namespace made
  by the old code. `KUBENIMBUS_LIVE_NAMESPACE` names the namespace instead, for a session
  that works under a namespace rule of its own (the backlog bundles each mutate only
  `bundle-<letter>`); such a run skips both sweeps, so it touches nothing outside that
  namespace, and its hook deletes it as usual. A test that needs a namespace-wide object (a `LimitRange`) makes a
  second namespace with the same label and deletes it itself. Cluster-scoped objects (a
  throwaway CRD) carry the run id in their name and group, are deleted in a `finally`, and
  carry the same label with the run id as its value, so a run killed before its `finally`
  leaves nothing the sweep cannot find: `LiveCluster.SweepAbandonedCrdsAsync` deletes a labelled
  CRD older than two hours whose label is not this run's (#279).
  Reading the rest of the cluster — the demo namespaces, every CRD, the node — is fine.
- **Third-party CRDs come from cert-manager**, installed by hand once from its pinned
  release manifest (`scripts/README.md`), never by a test. The tests that need it
  (`CertManager.RequireAsync`) skip with the reason when it is absent.
- **The reference for "matches kubectl" is the API server's own `Table`** (`Accept:
  application/json;as=Table;v=v1;g=meta.k8s.io`, `LiveCluster.GetTableAsync`). It is what
  kubectl asks for and prints, so parity is checked with no kubectl binary on the machine.
- **A narrow-RBAC user is a real ServiceAccount token** (`LiveCluster.CreateNarrowUserAsync`,
  a TokenRequest and a temp kubeconfig), because the app has no impersonation to exercise
  and a real identity is where the 403s it surfaces come from.
- **The single node is cordoned only for about a second, and never drained.** A drain that
  evicts would take CoreDNS, Traefik and every other session's pods down with it; the
  drain's refusal path, the plan over the real node and evictions of the tests' own pods
  (PodDisruptionBudget 429 included) are what is run instead. The cordon tests are
  `[NotInParallel]`, skip if the node is already cordoned, and uncordon in a `finally`.

They found real disagreements on their first run, each now fixed with a no-cluster test
beside it: a strict-validation refusal arrives as HTTP **500**, not 400/422
([apply-preview](apply-preview.md)); a CRD `string` column prints an
object or array as JSON, and `\.` is how kubectl's JSONPath reaches a dotted key
([crd-printer-columns](crd-printer-columns.md)); and a follow opened
between a container's creation and its start ends at once with no lines
([multi-pod-logs](multi-pod-logs.md)). `ConnectFanOutLiveTests` found that an exec credential
plugin runs **twice** per connect, not once ([connecting](connecting.md)); that one is recorded
and not yet fixed.

**Use the script** (`scripts/sandbox-up.ps1`, or `scripts/sandbox-up.sh` on
Linux/macOS — Docker required). It starts single-node k3s in Docker, writes
`.sandbox/kubeconfig.yaml` pointed at the published host port with the context
renamed from k3s's `default`, and applies the demo workloads:

```powershell
./scripts/sandbox-up.ps1            # add -Recreate to start from scratch
./scripts/sandbox-down.ps1
```

Re-running reuses a live container and re-applies the manifests. `-Name`/`-Port`/
`-Kubeconfig` bring up a **second** cluster, which is the only way to exercise
the fleet views for real. See [`scripts/README.md`](../../scripts/README.md) for the
full flag table.

**Tearing down one sandbox never deletes another's kubeconfig** (#379). `sandbox-down`
used to fall back to `.sandbox/kubeconfig.yaml` whatever `-Name` said, and the README's own
line for the Argo sandbox (`sandbox-down.ps1 -Name kubenimbus-argo`, no `-Kubeconfig`) deleted
the running shared sandbox's file, taking the live tests' and the QA seed's cluster with it.
Both `sandbox-down` scripts now default to the file the up scripts write for that name
(`.sandbox/argo-kubeconfig.yaml` for `kubenimbus-argo`, as `argo-sandbox-up.ps1` assumes), and
delete a kubeconfig only when its `current-context` is `-Name`, which holds for every file
`sandbox-up` writes because it renames k3s's `default` context to the container name; any other
file is kept with a warning. Checked with a `docker` shim (so no real container was touched)
against scratch files in a worktree's `.sandbox/`: `-Name kubenimbus-argo` removed only
`argo-kubeconfig.yaml`; the same with `-Kubeconfig` pointed at the shared file, and
`-Name kubenimbus-sandbox-b` with no `-Kubeconfig`, kept it with the warning; the plain default
still removed `kubeconfig.yaml`; a file with no `current-context` was kept. The `.sh` twin was
checked the same way. `-Wsl` was not exercised: its keep-alive record sits beside the kubeconfig,
which for the Argo sandbox is the same `.sandbox/` directory as before.

**Bringing up a second sandbox never overwrites the shared one's kubeconfig either** (#383).
`sandbox-up` still defaulted to `.sandbox/kubeconfig.yaml` whatever `-Name` said, so
`sandbox-up.ps1 -Name kubenimbus-argo` without `-Kubeconfig` replaced the shared sandbox's file
with the Argo cluster's, and the live tests and the QA seed then talked to the wrong cluster. All
four scripts now share one rule: `.sandbox/kubeconfig.yaml` for `kubenimbus-sandbox`, and
`.sandbox/<name>-kubeconfig.yaml` for any other name, less its `kubenimbus-` prefix
(`argo-kubeconfig.yaml` for `kubenimbus-argo`, the file `argo-sandbox-up.ps1` already reads, and
`sandbox-b-kubeconfig.yaml` for `kubenimbus-sandbox-b`, where `sandbox-down` used to fall back to
the shared file and keep it with a warning). `-PrintKubeconfigPath` / `--print-kubeconfig-path`
prints the resolved path and exits before any Docker call, which is what `SandboxScriptTests`
(Core.Tests) runs: the `.ps1` pair wherever `pwsh` is on PATH, the `.sh` pair off Windows only,
where `bash` on PATH may be WSL's. With the old default restored in `sandbox-up.ps1` the test
failed for `kubenimbus-argo`, `kubenimbus-sandbox-b` and `scratch`. The `.sh` dry runs were also
checked by hand in Git Bash on Windows. A second sandbox brought up before this change wrote its
kubeconfig wherever `-Kubeconfig` said; pass the same path to tear it down.

**Docker Desktop is not required.** `-Wsl` on both `.ps1` scripts routes every
docker call through `wsl.exe --exec docker ...` instead — for Docker Engine installed
directly inside a WSL2 distro
([tutorial](https://learn.microsoft.com/windows/wsl/tutorials/wsl-containers)),
with no Windows Docker Desktop at all. The one thing that needed care: `docker
cp` takes a Windows host path (the manifests dir) that a WSL-side docker client
can't resolve, so `-Wsl` translates it through `wsl --exec wslpath -u` first — every
other call only ever passes container names and in-container paths, which need
no translation. `--exec` on every call is load-bearing: without it `wsl.exe` hands the
arguments to the distro's shell, which strips the backslashes from a Windows path
(`X:\source\kubeNimbus\scripts\manifests` reached `wslpath` as
`X:sourcekubeNimbusscriptsmanifests`, and the run died on a null `.Trim()`). `dotnet run`/`$env:KUBECONFIG` stay exactly as below; WSL2
forwards `localhost:<port>` to Windows automatically. See
[`scripts/README.md`](../../scripts/README.md#docker-without-docker-desktop-wsl2).
The up scripts' WSL keep-alive (`wsl-keepalive.json`, #370) is stopped by `sandbox-down.ps1
-Wsl` once no `kubenimbus*` container is left in the distro, and its record deleted with it
(#376); while the Argo sandbox is still there it stays. `Stop-WslKeepAlive` stops only the
process the record still names (pid and start time), so a reused pid is never killed. It was
exercised on its own against a stand-in process named `wsl` (live, stale, reused pid, no
record); the whole `-Wsl` teardown was not run, to leave the shared Docker Desktop sandbox alone.

`-InstallKubeconfig` additionally copies it to `~/.kube/config`. That matters
because `$KUBECONFIG` only reaches processes started from a shell that has it
set — an app launched from Explorer, a shortcut or Visual Studio sees nothing
and lands on the empty-state screen. The copy goes stale on `-Recreate` (new CA
and client certs), so it refuses to overwrite an existing config without
`-Force` and keeps a timestamped backup; `$KUBECONFIG` remains the
non-staling option for terminal launches.

The manifests in `scripts/manifests/` are not a demo for its own sake — each one
exists to make some app surface non-empty, and that is the bar for adding to
them: multi-container pods that log continuously (log follow, severity coloring,
container picker), env vars of every ref kind (Environment tab + Reveal), a
StatefulSet with PVCs (Storage), a CronJob firing every minute (a visibly live
watch), a whole `demo-broken` namespace of CrashLoopBackOff/ImagePullBackOff/
unschedulable/never-Ready pods (the status pills and empty/error states of UI
rule 9) — with the Service pane's states beside them (a not-ready endpoint, a typo'd
selector, an ExternalName, a selector-less service with a hand-written EndpointSlice)
and two NetworkPolicies, one of them a default deny —, three CRDs **two of which share the Kind `Widget` in different API
groups** (the sidebar's group-aware filter) whose `additionalPrinterColumns` between
them produce every column state the list can render — mixed scalar types, a
`priority: 1` column, a condition filter, a `type: date` that is not the creation
timestamp, a declared `Age`, a path that resolves to nothing, and one CRD declaring
**no** columns at all (the degradation path), RBAC subjects including a dangling
binding, a `resourceNames`-narrowed rule and a ClusterRole bound by a *RoleBinding*
(the access review, both directions), and a synthetic three-revision Helm release
(history paging — k3s's own traefik releases are real but sit at revision 1).
Metrics need nothing: k3s ships metrics-server, so `metrics.k8s.io` is live;
delete that Deployment to test the *absent*-metrics degradation path.

If a feature grows a state that nothing in the sandbox produces, add a workload
for it here rather than relying on the screenshot fixtures alone.

`kind` works equally well if you prefer it (`kind create cluster`, then
`kind get kubeconfig > .sandbox/kubeconfig.yaml`); the demo manifests apply to
any cluster with `kubectl apply -f scripts/manifests/`, minus the Helm release
seeding, which is inline in the scripts.

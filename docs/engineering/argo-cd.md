# Argo CD (GitOps in the navigator)

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`ArgoCd.cs` + `ClusterClient.ArgoCd.cs` (Core), an `Argo` sidebar section holding a
GitOps dashboard above that cluster's own Argo kinds, an Application detail pane, and
Sync / Sync with prune / Refresh on the shared action strip. It is the feature Lens shipped in
2026.8 — "Argo CD in the navigator, detected automatically from the cluster, with
Sync and Refresh" — with two differences that are the point of doing it here: it is
**free** (Lens gates Argo CD behind Plus/Pro/Enterprise) and it is **quiet** (no
telemetry, and no second credential).

**The whole integration is the Kubernetes API.** Argo CD keeps Applications,
ApplicationSets and AppProjects in etcd as ordinary custom resources, so reading them
is the generic list path and both actions are merge patches of those objects that
Argo's own controller watches for. No Argo API server, no URL to paste, no `argocd`
binary, no second set of credentials — which is what makes this compatible with hard
rule 4 at all. It also means an Argo CD reachable only from inside the cluster
(the default install exposes no ingress) is fully usable from here.

Nine things are load-bearing.

1. **A sync is the object's top-level `operation` field, not a call to anything.**
   `ArgoCd.SyncPatch` writes `operation.initiatedBy` / `operation.info` /
   `operation.sync`, which is exactly what Argo's own API server writes when somebody
   presses Sync in its UI; the application controller watches for a non-null
   `operation`, runs it, and moves the outcome into `status.operationState`. The initiator
   is the user the API server names — see "Who a sync says it came from" below. A refresh
   is `argocd.argoproj.io/refresh: normal|hard`, the annotation `argocd app get
   --refresh` sets. Both are pinned byte-for-byte by `ArgoCdTests` for the same reason
   the workload patches are: **every failure here is silent**. A patch into `spec`
   instead of `operation`, or a misspelled annotation key, is a 200 from the API server
   that no controller acts on, and from the UI that is a dead button.
2. **No revision is pinned in the sync**, deliberately. Omitting it makes Argo sync to
   the Application's own `spec.source.targetRevision`, which is what the Application
   says it wants and what "Sync" has to mean. Writing one would quietly turn the action
   into "deploy something else", and it would do so without any visible difference.
3. **Both actions report a *request*, never a result.** The API server accepting the
   patch means Argo has been asked; what it then did lands in `status.operationState`
   seconds later and reaches the list through the ordinary watch. The strip says "Sync
   requested", and a refresh says out loud that Argo *clears the annotation* once it has
   re-compared, so there is deliberately nothing left on the object to look at.
4. **Sync and health are two independent states and get two pills, never one column.**
   An Application can be Synced and Degraded (Git applied cleanly, the pods crash) or
   OutOfSync and Healthy (someone changed the cluster by hand and what they left works),
   and one Status column has to pick one of the two and be wrong about the other. Where
   a single answer *is* needed — the attention ordering, `AttentionReason` — **health
   outranks sync**, which is Lens's rule and the right one: the pods are down either
   way, and reporting "out of sync" sends somebody to Git when the problem is in the
   cluster. `Progressing` is deliberately not an attention state: a rollout in flight is
   the system working, and a dashboard that flagged every deploy would be flagging
   nothing.
5. **The capability check names the kind, and that is the third honest exception**
   after `NodeActions.SupportsCordon`. Scale has a discovery signal (a `scale`
   subresource) and restart has an object signal (a pod template); a sync has neither —
   `operation` is a field of Argo's schema, discovery says nothing about it, and an
   Application that has never been synced does not carry it, so "does the object have
   the field" answers false for exactly the Applications you would want to sync.
   `ArgoCd.SupportsSync` therefore tests the kind *and* asks discovery the half it can
   answer: does this server say Applications are patchable. The **version is never
   assumed** (`ApplicationDescriptor` finds the kind in the catalog at whatever version
   the server serves), same rule as the metrics API's.
6. **The dashboard is cluster-wide and the namespace picker is disabled, by descriptor.**
   Applications live in one namespace (`argocd`) while everything they manage is spread
   across the rest, so a dashboard that followed the picker would be empty everywhere
   except the one place nobody browses. `ArgoDashboardDescriptor` is declared
   `Namespaced: false` and the picker's existing `IsEnabled` binding does the rest —
   no new binding, and no control offering a choice that changes nothing.
7. **The section is by API group; the dashboard row is by kind.** `argoproj.io` buckets
   into `Argo` through the same group rule every other section uses, so Rollouts and
   Workflows land there too — which is why the section is "Argo" and the row inside it
   is "Argo CD". The row itself is gated on the *Application kind* existing, because a
   cluster running only Rollouts has an Argo section with no Argo CD in it.
8. **The detail pane is read-only and the actions stay on the list.** Sync and Refresh
   report on the strip above the list (UI rule 17), and an action fired from inside a dock
   tab would report on a strip a maximized inspector is covering. The pane's own Reload button is a
   different thing and says so: it re-reads the object, where Refresh asks Argo to
   re-compare against Git. Managed resources carry the chevron owner chips already use,
   so the Deployment Argo calls Degraded is one click from its own manifest.
9. **Sync fires on its click; Sync with prune asks** (2026-10-07, UI rule 17 revised). A
   sync without prune applies what Git already declares — on an auto-sync Application it is
   what Argo would do on its own next reconcile — so it is sent on the click and the strip
   is only its result line. That replaced a Sync button that opened a strip with a second
   Sync button, which the owner reported as the double click it was. Prune is the half of a
   sync that *deletes* — resources that have left Git go with it — so it is its own action,
   `RowActionKind.ArgoSyncPrune`: "Sync with prune…" on the Applications page's Sync arrow,
   in both row menus and in the palette, arming a confirm whose sentence says what it
   deletes and where it comes back from. It used to be a checkbox on the sync's strip; with
   the sync firing on its click there is no strip to hold it, and a checkbox whose unticked
   state is a one-click action is a confirm for nothing. Refresh changes nothing on the
   cluster and fires on its click too. Terminate sync… (#193) asks, like prune: it stops Argo
   partway, and what was applied stays applied — see "Terminating a sync" below.

## A sync carries the Application's own sync options

**Found by the first live run against a real controller (2026-10-11, Argo CD v3.5.4).** The
controller reads `syncOptions` and `retry` *only* from the operation (`controller/sync.go`:
`syncOp.SyncOptions.HasOption("CreateNamespace=true")` and the rest), and Argo's API server
copies them there from `spec.syncPolicy` whenever somebody presses Sync in its UI
(`server/application/application.go`, `Sync`). kubeNimbus wrote an operation without them, so
an Application that says `CreateNamespace=true` failed its sync from here with `namespaces
"…" not found`, and one that says `ServerSideApply=true`, `PruneLast=true` or `Replace=true`
was synced without it, silently. `ClusterClient.SyncArgoApplicationAsync` now reads the
Application first and `ArgoCd.SyncPatch` copies `spec.syncPolicy.syncOptions` into
`operation.sync.syncOptions` and `spec.syncPolicy.retry` into `operation.retry`, byte for byte
as Argo does. An account that may patch but not read Applications still gets its sync, without
the options it could not see. `ArgoTerminateAndSyncOptionsTests` pins the patch and the read;
`ArgoLiveTests` syncs a `CreateNamespace=true` Application on the real controller (mutation
check: with the copy removed, both the unit test and the live test fail, the live one with
phase `Failed`).

## Terminating a sync

**The patch goes to the object, not to `/status`** (#193, settled against Argo CD v3.5.4 on
2026-10-11). Argo's Application CRD declares no subresources, so
`PATCH …/applications/<name>/status` is a 404 from the real API server. What Argo's own
Terminate button and `argocd app terminate-op` do is `TerminateOperation` in
`server/application/application.go`: refuse when `operation` or `status.operationState` is
nil, set `status.operationState.phase = Terminating`, and `Update` the whole Application (no
subresource), retrying on conflict. The application controller sees the phase, stops the sync,
deletes hooks still running, and records the operation `Failed`, "Operation terminated".

kubeNimbus sends `ArgoCd.TerminatePatch`, an RFC 6902 JSON patch of the object: a `test` that
`/status/operationState/phase` is still `Running`, then a `replace` with `Terminating`. The
`test` is Argo's server-side check made atomic: a sync that finished between the click and the
request makes the API server refuse the whole patch with 422 and write nothing, which
`TerminateArgoOperationAsync` returns as false and the strip says as "No sync is running on … any
more — it finished before the request arrived. Nothing was changed." A merge patch could not be
conditional, and one sent after the sync finished would stamp a finished operation Terminating.

Where: "Terminate sync…" on the Applications page's Sync arrow (the same strip that started the
sync), in both Argo row menus and in the palette, enabled only while the phase is `Running`
(`ArgoApplication.CanTerminate`). It arms the confirm (UI rule 17), whose sentence says what
stays applied; it is not red, because nothing is deleted. Like sync, what authorises it is
Kubernetes RBAC `patch` on the Application, not Argo's `applications, sync` role.

## ApplicationSet and AppProject panes

**Double-click opens a pane for each of Argo's three kinds** (#194), from the ordinary
resource list: an Application opens the dashboard's own pane (it used to open its YAML, which
stays in the context menu), an ApplicationSet and an AppProject their own. `ArgoDetailFor`
(`ClusterTabViewModel.Argo.cs`) routes them by group and kind, beside `NetworkingDetailFor`.

- **ApplicationSet** (`ArgoCd.ReadApplicationSet`, `ArgoApplicationSetView`): Overview names
  each generator by what it iterates (`List · 2 elements`, `Git directories · apps/* · repo @
  rev`, `Matrix of git and clusters`, an unknown type by its own name), the template's
  Application name pattern, project, source and destination, the set's own health (Argo 3.x),
  and what `applicationsSync` and `preserveResourcesOnDeletion` do in words. Applications lists
  what it generated from its own `status.resources` (Argo 2.6+), worst first, each with its sync
  and health pills; on open and Reload the pane lists Applications in the set's namespace and
  lays their live state over the status, adding any Application the set owns by UID that the
  status does not list (an older Argo). When that read is refused the list says the pills are
  from the set's status. **An ApplicationSet's conditions are read by their `status`**:
  `ErrorOccurred: False` is the healthy state, so the Application pane's type-suffix rule would
  have shown every healthy set with a red condition (`ArgoCondition.Problem`).
- **AppProject** (`ArgoCd.ReadProject`, `ArgoProjectView`): one scroll of five short sections (headed, so no cards: UI rule 24) —
  source repositories (a `!` entry is a deny pattern) and Application namespaces, destinations,
  cluster-scoped and namespaced kinds, sync windows, roles. **Each empty list says what Argo
  makes of it**, from `IsGroupKindNamePermitted`, `IsSourcePermitted` and
  `IsDestinationPermitted` at v3.5.4: no source repository or destination means none is
  allowed; an empty cluster-kind allow list allows no cluster-scoped kind; an absent namespaced
  allow list allows every namespaced kind and a present but empty one allows none. A sync window
  says what it does to a manual sync — the kind kubeNimbus sends — from `SyncWindows.CanSync`.

Both are read-only with a Reload, like the Application pane. The demo cluster ships one of each
(`Demo/Fixtures/argo-objects.json`); `ArgoCdObjectsTests` and `ArgoPanesAndTerminateTests` pin
them, and the harness renders `cluster-tab-argo-applicationset-*` and
`cluster-tab-argo-appproject-detail`.

## The dashboard is a watch (#303)

The dashboard used to read Applications once when it opened, so a sync started with `kubectl`
or Argo's own UI, a health change or a new Application showed only after a reload — the one
list in the app that was not a list+watch (hard rule 2), and a sync in flight is exactly when
somebody is looking. On one cluster it is now `WatchResourceAsync` over the Application kind,
cluster-wide, folded into an `ArgoApplicationTable` (Core) and brought to the UI thread in
batches (`ClusterTabViewModel.ArgoWatch.cs`). It rides the list's own `_watchCts`, so another
kind, Refresh, the fleet toggle and closing the tab stop it as they stop any list watch, and it
resumes and relists through the same engine as every other list. It reads the cluster-wide
Applications collection through the shared `ClusterClient.Watches` (`WatchCache`, see
[aot-watch-and-discovery](aot-watch-and-discovery.md)), the same entry the Applications mode's
Argo watch reads, so opening the dashboard joins a running watch instead of listing again; Refresh
calls `Watches.Relist` on that entry first, as the Resources list does, or it would be answered
from the cache. `ArgoDashboardRefreshTests` pins that against a fake API server that counts the
Application lists: Refresh with the dashboard open is a second list (mutation-checked: without
the `Relist` call the test times out on one).

- **Loading ends at the first row or at Synced, never at Reset** (UI rule 18). A Reset in the
  middle of the stream (a relist after 410 Gone) keeps the rows on screen until the relist has
  something to show, so the grid does not blink empty.
- **A status update replaces only its own row.** An unchanged Application keeps the same
  `ArgoApplication` instance in the table, so when a batch leaves the order alone only the rows
  whose object changed are replaced, and the reader's scroll position and selection stay. An
  Application added, deleted or moved by the sort rebuilds the list; the selection follows the
  same `namespace/name` either way. `ArgoDashboardWatchTests` pins it (mutation-checked: always
  rebuilding fails the "same row object" assertion).
- **The fleet list and the demo cluster are still reads.** The fleet list's per-member watch
  belongs with the Argo fleet work (#306); the demo has no client.

Live proof, against the Argo sandbox (`Live/ArgoLiveTests`,
`The_dashboard_watch_shows_a_sync_started_with_kubectl_without_a_reload`): the watch was opened
before the Application existed, the Application arrived through it, a sync was started with a
raw merge patch writing `operation` (what `kubectl patch` sends, not kubeNimbus's own Sync), and
the table saw the phase go `(none)`, `Running`, `Succeeded` with exactly one Reset — no relist
(Argo CD v3.5.4, 2026-10-12).

## The dashboard across the fleet

**One merged list with a Cluster column, the counts summed** (owner decision on #195,
2026-10-10). The fleet toggle, hidden on the dashboard before, shows there with more than one
cluster connected. `ClusterFleet.ListArgoApplicationsAsync` resolves the Application kind on
each member's own discovery, reads every member in parallel and returns an `ArgoFleetRead`; the
seven counts are `ArgoSummary` over all of them, the header says "n of m clusters run Argo CD",
and a member that could not be read is named in the inline warning while the rest show. The
fleet list is still a read, not a watch (the one-cluster dashboard is a watch since #303, below),
so the toggle and a cluster tab opening or closing re-read it. **A fleet row acts on its own cluster**: `ClientForArgoRow` takes the member's client from
the shell's member list, and a row whose cluster has left the fleet gets no client rather than
this tab's (which would sync a namesake on the wrong cluster); the detail pane opens on the same
client. Rows with the same `namespace/name` on two clusters order by cluster, so they no longer
swap on a reload.

That property is now a test rather than a reading of the code (#306):
`ArgoFleetRoutingTests.A_fleet_rows_sync_goes_to_its_own_cluster_and_to_none_once_it_left`
reads two stub API servers through `ClusterFleet.ListArgoApplicationsAsync` (aggregated
discovery serving `argoproj.io/Application`, one `argocd/checkout` on each), syncs the west row
and sees the PATCH reach the west server only, then drops west from the members and sees the
sync arm nothing and reach no server. It went red with `ClientForArgoRow` returning the tab's own
client. **A fleet read clears only its own warning**: the reload clears the tab's warning line
only when it begins with `ArgoReadWarning` ("Could not read Argo CD Applications"), for the
fleet and the single-cluster read alike. Before, a fleet read with no failures set it to null and
erased whatever else the line carried (a reconnect notice, a dropped watch);
`…keeps_an_unrelated_warning_and_clears_its_own` went red with that restored.

## A real Argo CD in a sandbox

`scripts/argo-sandbox-up.ps1` installs Argo CD **v3.5.4** into a second, throwaway sandbox
(`sandbox-up.ps1 -Name kubenimbus-argo -Port 6551 -SkipApps`): `install.yaml` from the release
tag, SHA-256 `1feb02cc7bacf3a379da58b0ca8c6f07d18288cd04d531dabd08816375e5e010`, images pinned
by digest, no Dex, applications in any namespace on for `argo-tenant`, and a `git daemon` from
the argocd image serving the test repository. `scripts/README.md` has the detail.
`Live/ArgoLiveTests` run against it and skip everywhere else, CI included:

| Test | What a real controller was observed to do |
|---|---|
| sync, initiator, prune | ran the sync from kubeNimbus to `Succeeded`, recorded `operation.initiatedBy.username` = `"<user> (kubeNimbus)"` with the API server's username, created the `CreateNamespace=true` destination; after the Application moved to the commit that deleted `removed.yaml`, a sync without prune left that ConfigMap and a sync with prune deleted it, keeping the one still in Git |
| refresh | cleared `argocd.argoproj.io/refresh` after re-comparing |
| dashboard watch | delivered a new Application and a `kubectl`-style sync's phases (`Running`, then `Succeeded`) to the dashboard's Applications watch, with one Reset and no relist (#303) |
| deny window | held a manual sync under a deny window with `manualSync: false`, its operation message saying it is "blocked by sync window", and applied nothing |
| 403 | refused sync and terminate from a user with only get/list/watch, with the API server's own sentence (`cannot patch resource "applications"`), and nothing was written |
| terminate | `/status` patch is a 404; the object patch stopped a sync waiting on its PreSync hook, ended `Failed` / "Operation terminated", and never applied what came after the hook; a second terminate found nothing running (422) |
| Open in Argo CD | wrote `status.controllerNamespace: argocd` on a tenant Application in `argo-tenant`, which links to `https://argocd.sandbox.test/applications/argo-tenant/<name>`; left alone an Application in an unwatched namespace whose author wrote its own namespace into that field beside an `argocd-cm` of their own, which gets no link |

## Who a sync says it came from, and what authorises it

**The initiator is the user, with the tool beside it** (security block 3, B3-3).
`operation.initiatedBy.username` used to be the constant `kubenimbus`, so Argo's sync history
lost who asked; the Kubernetes audit log kept the real user, but that is not where someone
reading the Application looks. `ClusterClient.GetCurrentUsernameAsync` asks the API server who
this connection is — a `SelfSubjectReview`, what `kubectl auth whoami` sends: `POST
apis/authentication.k8s.io/v1/selfsubjectreviews` (GA in 1.28), then `v1beta1` (1.27), and
nothing when neither is served — and the sync writes `"<username> (kubeNimbus)"`, or
`"kubeNimbus"` alone when the server could not say (`ArgoCd.InitiatorFor`). Asked lazily, by
the first sync on a connection; kept only in that `ClusterClient`, and forgotten by
`RefreshCredentialsAsync`, because a refreshed credential can be a different identity. A
refusal or a timeout is not cached (the next sync asks again); "neither version is served" is
the server's settled answer and is. Only the username is used, never groups, the UID or
extras. `ArgoSyncIdentityTests` pins the request, the v1beta1 fallback, the caching and what
the patch carries; `IdentityLiveTests` reads the sandbox admin's certificate CN and a narrow
ServiceAccount's username back from a real API server and syncs a stand-in Application.

**The field is attribution, not proof.** `initiatedBy.username` is free text in the
Application's spec-level `operation`, and anyone allowed to patch the Application can write
any name there — this app, `kubectl patch`, a script. Read it as "who says they asked"; the
API server's audit log is the record of who did.

**What authorises a sync from here is Kubernetes RBAC, not Argo CD's.** The sync is a merge
patch of the Application, so the API server checks `patch` on `applications.argoproj.io` for
the user's kubeconfig identity, exactly as for `kubectl patch`. Argo CD's own project roles and
`argocd-rbac-cm` (`applications, sync, <project>/<app>`) are enforced by the Argo CD API
server, and this path never goes through it. Granting someone `patch` on Applications is
therefore granting them sync (and prune) on every Application in that namespace, whatever
their Argo role says, with this app or with kubectl alike — worth knowing before handing out
that verb. What still applies is everything the **application controller**
enforces when it runs the operation: the project's sync windows (`controller/sync.go`,
`syncWindowPreventsSync`, which calls `window.CanSync(isManual, …)`; a sync from here carries
no `initiatedBy.automated`, so it is a manual sync and a window's `manualSync` setting decides
it), and the project's source, destination and resource allow and deny lists
(`validateSyncPermissions` in the same file). Checked against argoproj/argo-cd `master` at
`eb54713` on 2026-10-07.

**Two rendering defects, both found by looking at the rendered pane rather than by any
test.** The second is the more general one: the detail pane's resource rows are two lines
each (the object, then its group and namespace), and they shipped with 16px between the
two lines of a row and 19px between consecutive rows — so the two gaps were
indistinguishable and every qualifier read as belonging to the row *below* it. **Rows have
to be separated by more than their own lines are**, or a list of them reads as one block of
running text; the row's bottom margin is what fixes it, and the trap applies to any
multi-line `ItemsControl` row in this app.

Maximizing the inspector
works by setting the list row's height to 0, and a `Grid` does not clip its children —
the resource list and the Helm browser get away with it because a `DataGrid` clips
itself, but the dashboard's summary card is an ordinary `Border` and went on painting
straight through the maximized dock. `ClipToBounds="True"` on that row's grid is the fix,
and the trap applies to any future content in that slot that is not a `DataGrid`.

**The demo cluster runs all of it** (demo rules 4 and 5): seven Applications ship in
`Demo/Fixtures/argo-applications.json`, read through the same `ArgoCd.ReadApplication` a
live cluster's list goes through, covering every state the dashboard classifies —
including the two that are easy to get wrong, Synced-but-Degraded and an Application Argo
cannot compare at all (unreachable repository, no resources, a `ComparisonError`
condition). Only the sync and refresh requests have no honest offline stand-in, and
`RowActionViewModel.IsDemo` says so in place: a sync or refresh ends on a result line saying
nothing was sent, and the prune confirm is armed with its confirm disabled. **The sandbox
gained a shape rather than an installation**: `scripts/manifests/70-argocd-crds.yaml`
declares a stand-in Application CRD with Argo's own group, kind, version and printer
columns, and `71-argocd-applications.yaml` five Applications in the same states. It
declares no `status` subresource, and neither does real Argo (its CRD carries
`subresources: {}`, checked against `manifests/crds/application-crd.yaml` on 2026-10-07; this
page used to say otherwise) — with one on, `kubectl apply` would silently drop every `status`
block and all five would come back Unknown/Unknown, which is one state, not five. Delete both
CRDs before installing real Argo CD; they claim the same names.

## Where "Open in Argo CD" goes

The Applications page's "Open in Argo CD" opens `argocd-cm`'s `data.url` in the system
browser. That ConfigMap used to be read from the Application's own namespace, and with Argo's
"applications in any namespace" a tenant who may create an Application in their namespace may
also create a ConfigMap there called `argocd-cm`, with a URL of their choosing. The namespace is
now chosen by `ArgoUi.ConfigMapNamespace`, deterministically, from what the cluster says:

1. **The claim.** Argo CD 2.5+ writes `status.controllerNamespace` on every Application it
   reconciles; before 2.5 there is no field, and Argo only reconciled Applications in its own
   namespace, so the Application's namespace is the claim then.
2. **The evidence.** Because the CRD has no status subresource, whoever writes an Application
   writes that field too, so a claim is checked against the *other* Applications in view: one
   that names a controller namespace other than its own is evidence of where Argo runs — a
   tenant gains nothing by naming somebody else's namespace, and an Application's own claim is
   never evidence for itself.
3. **The rule.** With no such evidence (the classic install, every Application in Argo's own
   namespace) the claim stands. With evidence, the claim must be in it. A claim that disagrees
   gives **no link** rather than a guess: on a cluster running two Argo instances, one
   instance's address on the other's Application would be just as wrong.

What it cannot catch, stated rather than implied: an author with nothing else in view to compare
against (the only Applications the operator can see are theirs), and one who can write in two
namespaces and names one from the other. The tooltip names the host the button opens and the
namespace the address was read from, which is the last check, and only http and https are ever
opened. `ApplicationSupportTests` pins each case.

## Sorting

The list sorts by a header click (2026-10): ascending, descending, then its default order, kept across reloads for the tab's life. How each column compares, and why a header double-click opens nothing, is in [resource-grid-resize-sort](resource-grid-resize-sort.md), "The inspector grids sort too".

**The patches against a real API server** (`Live/OneClickActionsLiveTests`, VER-61): on a
stand-in Application (the sandbox has Argo's CRDs and no controller, so nothing consumes what is
written), Refresh writes `argocd.argoproj.io/refresh: normal` or `hard` and leaves the spec as it
was; Sync writes `operation.sync.prune: false`, "Sync with prune…" `true`, both with the
initiator the API server names; and a user allowed only to read gets the server's own 403
sentence for both, with nothing written. What a running controller then does is observed by
`Live/ArgoLiveTests` against a real Argo CD (see "A real Argo CD in a sandbox").

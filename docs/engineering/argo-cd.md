# Argo CD (GitOps in the navigator)

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`ArgoCd.cs` + `ClusterClient.ArgoCd.cs` (Core), an `Argo` sidebar section holding a
GitOps dashboard above that cluster's own Argo kinds, an Application detail pane, and
Sync / Refresh on the shared confirm strip. It is the feature Lens shipped in
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
   `operation`, runs it, and moves the outcome into `status.operationState`. A refresh
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
   arm the strip above the list (UI rule 17), and an action fired from inside a dock tab
   would arm a strip a maximized inspector is covering. The pane's own Reload button is a
   different thing and says so: it re-reads the object, where Refresh asks Argo to
   re-compare against Git. Managed resources carry the chevron owner chips already use,
   so the Deployment Argo calls Degraded is one click from its own manifest.
9. **Prune gets the drain's treatment.** It is the half of a sync that *deletes* —
   resources that have left Git go with it — so it is a checkbox on the strip, off by
   default, in words rather than in a menu. Terminating a running sync is **not** shipped:
   it means writing `status.operationState.phase`, which is a status-subresource patch and
   its own item.

The Resources pane lists managed objects. L on a selected Pod or selector-bearing
workload row, and its hover logs icon, now resolve that object through the cluster
tab's shared log command. Argo's status may outlive the managed object, so a missing
Pod is stated in the connection warning. The Application summary grid still has no
logs action: its rows are Applications, not managed pods.

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
`RowActionViewModel.IsDemo` says so in place with the confirm disabled. **The sandbox
gained a shape rather than an installation**: `scripts/manifests/70-argocd-crds.yaml`
declares a stand-in Application CRD with Argo's own group, kind, version and printer
columns, and `71-argocd-applications.yaml` five Applications in the same states. It
deliberately omits the `status` subresource real Argo declares — with it on, `kubectl
apply` silently drops every `status` block and all five would come back Unknown/Unknown,
which is one state, not five. Delete both CRDs before installing real Argo CD; they claim
the same names.

# Mutating workload actions (scale, rollout restart, delete)

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


The app was read-mostly until this: the only way to change a replica count was to edit
YAML, and "restart that deployment" — one click in Lens, Aptakube, k9s and Headlamp,
and the most common on-call GUI action there is — had no entry point at all.
`ClusterClient.Workloads.cs` and `WorkloadActions.cs` (Core) are the engine;
`RowActionViewModel` + the strip above the list (UI rule 17) are the surface; the row
`ContextFlyout` and the command palette are the two ways in, as the backlog item asked.

Six things are load-bearing:

1. **A restart is an annotation, not a delete loop.** `RestartWorkloadAsync` stamps
   `kubectl.kubernetes.io/restartedAt` on `spec.template.metadata.annotations` and stops
   — the controller then rolls its own pods under its own update strategy, honoring
   surge, `maxUnavailable`, partitions, PDBs and readiness gates. Deleting the pods
   ourselves would bypass every one of those and can take a whole Deployment down at
   once. The key is **kubectl's own**, deliberately: a restart from kubeNimbus and one
   from kubectl have to be the same event to whoever reads the object afterwards.
2. **Both patches are `application/merge-patch+json`, not strategic merge.** kubectl
   uses strategic merge for built-ins; a CRD answers that with a 415. For the nested
   scalar maps these two actions touch, RFC 7386 produces exactly the same object —
   merge patch recurses into objects and merges keys, so the template's labels,
   containers and other annotations all survive — and it is the one content type
   everything accepts. `WorkloadActionsTests` pins the byte-for-byte patch bodies,
   because **every failure mode here is silent**: a wrong annotation key, or a patch one
   level short (on the object's own metadata rather than the template's), is a 200 that
   rolls nothing and is indistinguishable from a dead button.
3. **Scale goes through the `scale` subresource**, like `kubectl scale`, and reads it
   before offering a number. The object's own `spec.replicas` is only the opening value
   of the box while that read is in flight — a CRD may declare a different
   `specReplicasPath`, and the subresource is the one field every scalable kind agrees
   on. The read failing (RBAC on the subresource) is stated in the strip and does not
   block the action.
4. **Capability comes from discovery and from the object, never from a list of kinds.**
   Scale is offered when the server declares a `scale` subresource for that kind
   (`WorkloadActions.SupportsScale`) — so an Argo Rollout is scalable on exactly the same
   evidence a Deployment is, and neither is named anywhere. Restart has *no* discovery
   signal at all (no subresource, no verb), so the honest test is the object: does it
   have a pod template to stamp (`HasPodTemplate`)? That is true of Deployments,
   StatefulSets, DaemonSets and a CRD that embeds a template, and false for a bare Pod —
   whose restart gesture is the delete. Delete is gated on the `delete` verb.
   In an aggregated fleet list the descriptor is the row's **own** cluster's, so the same
   CRD can be scalable on one cluster and not on another and the menu is right on both.
5. **Delete no longer detours through the YAML editor.** It used to open the object's
   manifest with that editor's confirm armed, which put an editor tab and a page of
   YAML between someone and a one-line question; it arms the same strip now, and names
   the object either way. The YAML editor keeps its own Delete for when you are already
   in there. "Confirm before deleting" is read **at the press** (same as
   `YamlEditorTabViewModel.RequestDeleteAsync`, same reason). Scale and restart do not
   consult it: it is a setting about deleting, and scale needs its input step regardless.
   With it off, the delete goes through `RowActionViewModel.RunNow`, the same path the
   one-click actions of UI rule 17 take, so the strip is only its result line and names
   the object it deleted.
6. **The demo cluster arms the strip and refuses in place.** All three actions need an
   API server, so `RowActionViewModel.IsDemo` (`client is null`, as everywhere) renders
   the notice and disables Confirm — never a silent no-op. That is also why the demo
   catalog's Deployment/ReplicaSet/StatefulSet descriptors carry a `scale` subresource:
   without it the capability check would hide the action outright and the demo would
   teach that kubeNimbus cannot scale, rather than that this cluster cannot.
   `cluster-tab-demo-scale-unavailable` is that scenario, and it is the one of the four
   new screenshots that runs the real command path end to end — a fixture tab has no
   `Client`, so the commands correctly refuse there and the strip is built by hand
   (`ClusterTabScenarios.ArmRowAction`), exactly as the exec/YAML/Helm scenarios do.

**Not shipped, deliberately:** rollout *status*/history/undo, pause/resume, and scaling
from a row's inline editor. `kubectl rollout undo` needs ReplicaSet revision walking and
is its own item; the rest are backlog candidates, not omissions this pass forgot.

## CronJobs: run now, suspend, resume (FEAT-8)

`CronJobActions.cs` (Core) and `ClusterTabViewModel.CronJobs.cs` (App). The three land on
the same strip, from the row menu and the palette; nothing is always visible. Run now and
resume arm a confirm, because each can start a Job straight away and a Job's side effects
(a migration, a batch of mail) cannot be taken back; suspend fires on its click, because
resume takes it back and running Jobs carry on (UI rule 17, revised 2026-10-07). Six things
are load-bearing:

1. **Run now is `kubectl create job --from=cronjob/…`, byte for byte where it matters.**
   The Job is the CronJob's `jobTemplate.spec` verbatim, with the template's labels, its
   annotations plus `cronjob.kubernetes.io/instantiate: manual` (the template's own value
   wins, as in kubectl), and a controller owner reference back to the CronJob — so the
   CronJob's history limits clean it up and deleting the CronJob deletes it.
   `CronJobActionsTests` pins the body. The CronJob is **read at the moment of the run**, so
   an edit since the list last ticked is what runs.
2. **The name is the server's (`generateName: <cronjob>-manual-`).** kubectl demands one on
   the command line; a client-side random suffix would be a weaker copy of what the API
   server already does, including truncating a long prefix so the result still fits the
   63-character label value the Job controller copies it into. The success line therefore
   comes from the server's answer — which is also the only way "Open Job" can know what to
   open.
3. **The schedule is unaffected, and the confirm says only that.** Verified against k3s 1.33:
   the CronJob controller does not put a Job it did not create on `status.active` (it logs
   an `UnexpectedJob` event about it), so `concurrencyPolicy` does not count a manual run.
   An earlier draft of the sentence claimed the opposite, from reading the controller's
   source rather than a cluster.
4. **Suspend and resume are one slot, like cordon and uncordon** (UI rule 11), chosen by the
   CronJob's own `spec.suspend`. Resume writes an explicit `false` (a merge-patch `null`
   would delete the field) and its confirm names the surprise: a run the CronJob missed while
   suspended can start straight away unless `startingDeadlineSeconds` has passed — the API's
   documented behaviour.
5. **Capability is the object's evidence and discovery's**: a Job template to run
   (`HasJobTemplate`) and a creatable Job kind on the row's own cluster for run-now; the
   template and `patch` for suspend. The menu items read the selected object, which the
   watch changes in place when a suspend goes through — no `SelectedRow` change reports
   that — so `OnSelectedRowChanged` subscribes to the selected row and re-raises the
   state-dependent `Can…` properties (cordon/uncordon included, which had the same latent
   staleness). `CronJobActionTests` pins it, mutation-checked.
6. **"Open Job" is the strip's one follow-up.** After a run the confirm's slot offers the
   created Job, which opens in the workload pane — a batch Job is now one of
   `WorkloadDetailTabViewModel.Supports`' kinds, with a run line in kubectl's COMPLETIONS
   shape plus running/failed against `backoffLimit` — so "a Job's pods are reachable from
   it" is one click. The pane's row is built from the server's answer rather than a list
   row, so its run line is as of the create until Refresh; the pods list is its own live
   watch. A Job is **not** offered a rollout restart any more: its pod template is immutable
   (the stamp is a 422), the one kind `WorkloadActions.SupportsRestart` now names, argued in
   place like `NodeActions.SupportsCordon`.

The demo cluster ships one scheduled and one suspended CronJob, so both halves of the slot
render; the strip refuses in place there like every other action — a suspend with a result
line saying nothing was sent, run-now and resume with their confirm disabled.

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

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
   With it off, a delete on a cluster that is not production goes through
   `RowActionViewModel.RunNow`, the same path the one-click actions of UI rule 17 take, so
   the strip is only its result line and names the object it deleted.
   **On a production cluster a delete always asks, whatever the preference says** (security
   block 3, B3-1): the preference is a convenience for clusters where a wrong delete is cheap,
   and it used to reach production too, so the Delete key deleted at once on the one cluster
   the colours exist to protect. Production means classified or assigned by hand
   (`MainWindowViewModel.EnvironmentFor`). Both paths go through one rule,
   `RowActionViewModel.DeleteNeedsConfirm`; in an aggregated fleet list it is the **row's
   own** cluster's environment that counts (`FleetMember.Environment`, read live through
   `ClusterTabViewModel.EnvironmentOf`), never the tab's. `MutatingActionSafetyTests` pins
   both directions and the YAML editor's path, mutation-checked.

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

## Every confirm names its cluster

The strip names the context the action lands on, in every view, by the name the cluster
switcher and the tab show: "Delete Pod x in payments on prod-eu". It used to name the cluster
only in a fleet list, so an ordinary tab's confirm said nothing about where — the wrong-cluster
incident the environment colours exist to prevent. On a production cluster it also says
`(production)` in words, because a cluster assigned production by hand need not have "prod" in
its name and a colour is information only for someone who knows the code (UI rule 11), and the
strip's own card border takes the production colour (`Border.card.actionStrip.production`).
That is the least chrome that does the job: no new element and no new row (UI rules 1 and 17),
present only while an action is armed on such a cluster, and the same colour as the band under
the command bar and the tab's edge. The YAML editor's own delete confirm names the cluster and
takes the same border; the cluster tab stamps both onto the editor as it enters the dock
(`YamlEditorTabViewModel.SetCluster`). `Target` keeps the `Kind/name in ns on cluster` form for
the result lines ("Deleted Pod/x in payments on prod-eu."); the sentence below is built from its
parts.

## Scale says "from N to M"

The scale sentence reads "Scale Deployment x in payments on prod-eu from 3 replicas (2 running)
to [5]", where [5] is the replica box itself (B3-4, reshaped by FEAT-77 below); `Question`, the
strip's accessible name, carries the box's number at the end: "… from 3 replicas (2 running) to
5". N is the object's own `spec.replicas` until the `scale` subresource has been read, then that
read's answer (`SetCurrentScale`), which also supplies the running count; with no starting count
the sentence says "to" alone. The 2026-10 form of this said "— it is already at 3" when the box
still held the starting count; with the box beside "from 3 replicas … to" that reads the same
without a second phrasing. Three deterministic warnings, none of which disables the confirm
(`RowActionViewModel.ScaleWarningFor`): to 0 from anything else (every pod stops), ten times the
current count or more, and ten or more from zero. They are a line of warn text under the
sentence, except scaling a production workload to zero, which is an outage and is the warn
`infoBar`. `ScaleConfirmTests` pins the thresholds at their edges, mutation-checked.

## How the strip reads (FEAT-77)

The first strip was assembled like a form: a bare title line ("Scale Deployment/checkout-worker
in payments"), a "Replicas" label over a spin box, a grey "currently 2 set · 1 running" caption
beside it, and Scale / Cancel pushed to the window's far edge. The owner's verdict on it during
the 0.5.0 pass was "this panel needs to be more elegant". It now reads as one sentence with its
answer beside it, the same shape for every action it hosts:

- **The verb's glyph leads**, in a tinted 32px square: the accent for the reversible actions,
  red for the ones that destroy something (`RowActionViewModel.IsDestructive`: delete, drain and
  Argo's sync with prune), whose confirm button is `danger` rather than `accent` too (UI rule 20).
- **The object and the verb are one sentence**: "**Restart** Deployment **checkout-worker** in
  payments on prod-eu (production)", composed in the view from `Verb`, `TargetKind`,
  `TargetName`, `TargetPlace` and `HeadlineSuffix`, with the kind dimmed and the verb and name in
  semibold. Under it, in the dim 12px weight, the `Consequence`: what the cluster does next.
  `Question` is both as one string, for the strip's accessible name and the tests.
- **A scale's current state is part of the sentence and its box is the sentence's blank**, as
  above. The box has no label of its own because the sentence is its label; UI rule 11's
  label-above-the-input is about a form field whose label sat in an `Auto` column with no gap,
  and this box keeps a 10px margin from the words. `AutomationProperties.Name` says "Replicas"
  for a screen reader. The box is an inline of the sentence (`InlineUIContainer`), drawn
  together with the word "to" as one unit (`SuffixBeforeBlank` is the sentence without that
  word). The first cut docked the box beside the sentence, and on a production cluster with a
  long name the sentence wrapped and left "to" alone on its second line under a box centred
  beside both, so it read "(1 running) [6]" over "to". Now a wrap moves the word and the box
  together.
- **The buttons sit with the text, not across the window.** The sentence column is capped at
  900px and left-aligned, so a short sentence keeps Scale / Cancel right after it, and a long
  one wraps beside them rather than pushing them out of the card (the window's minimum width
  included). The demo notice, the drain plan, the scale warnings and the result line stack
  under the sentence in the same column.
- **The result line leads with its state**: a moving bar while anything is in flight
  (`IsWorking`, a drain included), a check on success, an alert on a refusal. An action that
  fired on its click (UI rule 17) shows the glyph and this line only.
- **Its styles are its own.** The glyph, sentence, busy bar and result-mark styles live in
  `RowActionStrip.axaml`'s `UserControl.Styles`, not in the app theme: the strip is the only
  thing that draws them.

`LayoutChecks.ActionStripReadsAsOneBlock` asserts the layout half on every armed
`cluster-tab-row-action-*` scenario (the two narrow ones at 960px included): the confirm starts
within 32px of where the text's ink ends, and a scale's box starts within 16px after the word
"to", level with it and inside the sentence. Splitting the word and the box into two inlines,
so they could wrap apart, was confirmed red. It measures the laid-out text, not the `TextBlock`'s bounds, because a
`TextBlock` stretches to its column: the first version of the check measured bounds and passed
a strip whose buttons had been moved back to the far edge. `RowActionSentenceTests` pins the
sentence. The work was built on 2026-10-05 (commit 9a7c077, never pushed) and landed with the
2026-10 backlog sweep's bundle F, reconciled with the cluster naming, the production rule and
the scale warnings above.

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

**Suspend and resume against a real API server** (`Live/OneClickActionsLiveTests`, VER-61): the
patch lands, a watch of the namespace's CronJobs reports each change as a Modified event, the
server's Table (what `kubectl get cronjobs` prints) reads `True` then `False` under SUSPEND, and a
read-only user gets the server's own 403 sentence with the CronJob unchanged.

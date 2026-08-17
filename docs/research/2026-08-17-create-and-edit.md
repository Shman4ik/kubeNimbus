# Creating and editing resources: what do people actually ask for

*2026-08-17. Question asked: section B of `docs/BACKLOG.md` carries six write-side rows with
no research behind them — `FEAT-5` (dry-run diff, P2), `FEAT-28` (plain dry-run, P2, itself
drafted from KubeUI parity alone), `FEAT-6` (multi-select + bulk delete, P2), `FEAT-12`
(apply a local file / create from template, P3), `FEAT-8` (Job/CronJob trigger-now,
suspend/resume, P2) and `FEAT-13` (column chooser, P3). Validate them: is dry-run diff
demand or demo-ware? Is create-from-scratch a thing people want in a GUI, or kubectl's job?
How does the field actually do bulk operations? Is there evidence of people losing work or
applying the wrong thing?*

**The short version, and it inverts one of the brief's own expectations.**

- **Dry-run/diff-before-apply is marketing, not demand.** No user-filed, upvoted issue asking
  for it was found in Lens, FreeLens, Aptakube or k9s. Headlamp shipped it, but the issue that
  produced it was maintainer-authored and self-assigned the same day — a roadmap decision, not
  a response to users. `FEAT-5`/`FEAT-28` should stay on the books but not rise in priority on
  this evidence; a cheaper, better-evidenced safety fix (below) is what the field's own bug
  trackers actually argue for.
- **The brief guessed FEAT-12 wrong, and says so — the evidence is stronger than expected.**
  "Create a resource that doesn't exist yet, from pasted or uploaded YAML" is shipped, as a
  **documented core feature**, by every actively developed GUI this report could reach:
  Lens (since 5.0, 2021), Aptakube (since 2022), Headlamp, and kubeNimbus's own nearest
  open-source peer, KubeUI. Even k9s — a TUI whose maintainer twice declined to build a
  dedicated create dialog — ships a working file-browse-and-apply path people use for exactly
  this. This is the single most convergent feature in the whole field, not the row most likely
  to be a kubectl-shaped luxury.
- **Bulk operations are real and understated by "bulk delete".** Every product that ships
  multi-select uses it for **restart and trigger as much as delete** — Headlamp shipped
  delete-and-restart together in one PR, k9s's own multi-select issue cites "restarting all
  deployments in a namespace" as the motivating case, and Lens carries two unresolved,
  multi-year asks for exactly that. `FEAT-6`'s title undersells what the field actually builds.
- **The sharpest concrete safety finding is not a diff feature at all.** A real, open,
  well-described Headlamp bug — apply silently drops misspelled/unknown fields because the
  request never sets `fieldValidation=Strict` — describes **kubeNimbus's own apply path**
  exactly. This repo's `ApplyYamlAsync` has the identical gap today. It costs a query
  parameter, not a UI concept, and it is proposed as its own row ahead of anything diff-shaped.
- **Two real editing-safety bug classes were found in the field, and kubeNimbus is already
  structurally immune to both** — checked against the actual code, not assumed. Recorded as a
  clean negative, the way the logs report recorded "nobody asks a desktop client to store
  logs".
- **Column chooser (`FEAT-13`) turns out to have the most-iterated history of anything in this
  report** — five shipped issues and three open follow-ons in Aptakube alone, and the
  motivating complaint in the oldest of them is the identical "Age fell off the right edge"
  problem kubeNimbus's own gutter pass (UI rule 14) already had to firefight internally.

## What was searched, and what could not be reached

**Reachable:** `github.com` HTML (issue lists, filtered/sorted search, individual issue pages),
`raw.githubusercontent.com` (READMEs), web search, and label pages.

**Blocked by this session's egress policy:** `aptakube.com` (all pages — confirmed the same
block the logs report recorded two days ago), `docs.k8slens.dev`, `reddit.com`,
`news.ycombinator.com`. `api.github.com` — **both** the search endpoint (`/search/issues`,
403) and this session's scoped token (`/repos/{owner}/{repo}/issues/…}` answers "GitHub access
to this repository is not enabled for this session," since the token is bound to kubeNimbus
only) — so nothing here comes from the API; every GitHub citation is a rendered HTML page.

**A limitation worth stating plainly, because it changes how "ranked by evidence" works in this
report versus the logs report:** reaction counts (👍) were **not retrievable** for almost every
individual issue page fetched this session — the page consistently rendered "Reactions are
currently unavailable" rather than a number, which reads like a client-side widget that a
non-JS fetch cannot see, not a domain block (title, body, state, labels, comment count and
linked PRs all rendered fine on the same pages). The logs report two days ago evidently had
working access to `api.github.com`'s search endpoint, which returns reaction counts as JSON;
this session's token does not extend to it. **So this report ranks by a different, weaker set of
signals**: shipped vs. not, issue age and persistence, comment count where visible, duplicate
count (independent people filing the same request in different repos), and whether the request
came from a user or from the maintainer themself. Where a comment count or a specific number
*did* render, it is quoted; where it did not, this report says "reactions not readable this
session" rather than inventing a plausible-looking one.

## What kubeNimbus already ships, and does not

Checked in `src/`, not assumed. Source:
[`ClusterClient.Dynamic.cs`](../../src/KubeNimbus.Core/ClusterClient.Dynamic.cs),
[`YamlEditorTabViewModel.cs`](../../src/KubeNimbus.App/ViewModels/YamlEditorTabViewModel.cs),
[`RowActionViewModel`](../../src/KubeNimbus.App/Views/ClusterTabView.axaml) and
`WorkloadActions.cs`/`ClusterClient.Workloads.cs` (FEAT-1, shipped 2026-08-16).

| Shipped | Note |
|---|---|
| YAML view/edit for any resource | AvaloniaEdit, syntax highlighting |
| Server-side apply | `PATCH application/apply-patch+yaml`, `fieldManager` set, conflict (409) raised as `ServerSideApplyConflictException` with the server's own message, force-apply retry behind Advanced view |
| Delete, two-step confirm | Editor and the row action strip both |
| Scale / `rollout restart` / delete-a-pod | `RowActionViewModel` confirm strip (FEAT-1, shipped yesterday) — the mechanism any bulk or trigger-now action would extend |
| One authoritative read per editor tab, at open only | `RefreshFromServerAsync` has exactly one call site, in the constructor — **not** a live watch feeding the editor |
| A dirty edit always outranks a late server read | `IsDirty` check in `RefreshFromServerAsync`; a `StaleNotice` says so rather than silently discarding text |
| The real server error message on a failed write | `KubernetesApiException.From` reads the API's own `Status.message` and puts it first, code second |

**Not shipped, and each is referenced below:** any dry-run or preview before apply; creating a
resource that doesn't already exist; applying a local YAML file; multi-select of any kind;
bulk delete/restart/trigger; Job/CronJob trigger-now or suspend/resume; a column chooser;
`fieldValidation` on any write (confirmed by reading `ApplyYamlAsync` — the query string is
`fieldManager` and `force` only); reading the API server's `Warning:` response header on any
write.

## The field: what each product ships, and what it leads with

| Product | Create / apply file | Dry-run / diff before apply | Multi-select bulk ops | Column chooser | Leads with (marketing) |
|---|---|---|---|---|---|
| **Lens / FreeLens** | **Yes**, core since Lens 5.0 (2021) — "+ Create resource" dock tab, paste/type YAML, template dropdown ([lens#995](https://github.com/lensapp/lens/issues/995), closed via PR #2327, milestone 5.0.0) | No user demand found; no shipped feature found | **No** — two open, years-old, unresolved asks ([lens#3771](https://github.com/lensapp/lens/issues/3771) bulk scale/restart, 2021; [lens#4095](https://github.com/lensapp/lens/issues/4095) bulk cronjob trigger, 2021) | Column width/order issues closed as fixed; no dedicated show/hide chooser found | Not marketed as a headline; it is table stakes so old it predates most of Lens's public marketing copy |
| **Aptakube** | **Yes** — [#24 "Ability to create resources"](https://github.com/aptakube/aptakube/issues/24) (2022, closed/shipped), refined by [#271](https://github.com/aptakube/aptakube/issues/271) (open, better templates) | **"Resource Diff" is bullet #3 of the README — and it is a different feature**: compares two *existing* resources side by side, not a preview of a pending apply (confirmed via [#559 "Compare two resources not working"](https://github.com/aptakube/aptakube/issues/559)). No dry-run-before-apply request found | **No dedicated multi-select found**, but heavy iteration on the adjacent live-refresh-clobbers-edit problem: [#299](https://github.com/aptakube/aptakube/issues/299) (open) | **Yes, extensively** — five shipped issues ([#34](https://github.com/aptakube/aptakube/issues/34), [#92](https://github.com/aptakube/aptakube/issues/92), [#121](https://github.com/aptakube/aptakube/issues/121), [#180](https://github.com/aptakube/aptakube/issues/180), [#261](https://github.com/aptakube/aptakube/issues/261)) plus three open follow-ons ([#343](https://github.com/aptakube/aptakube/issues/343), [#472](https://github.com/aptakube/aptakube/issues/472), [#565](https://github.com/aptakube/aptakube/issues/565)) | README: "✍️ View & modify objects", "⚖️ Resource Diff" — not "create"; creation is a shipped but unmarketed feature |
| **Headlamp** | **Yes** — "+ CREATE" button, upload a YAML file to deploy (web search snippet of `headlamp.dev` docs) | **Yes, shipped** — [#5000 "Add Server-Side Dry Run Option To YAML Editor"](https://github.com/kubernetes-sigs/headlamp/issues/5000), maintainer-authored and self-assigned the day it was opened, closed via PR #5010; a `DryRunPreviewDialog` component exists ([#6031](https://github.com/kubernetes-sigs/headlamp/issues/6031)). **And it has a real, open, unresolved gap**: [#7147](https://github.com/kubernetes-sigs/headlamp/issues/7147) — dry-run passes, apply "succeeds," and a misspelled field is silently dropped, because `fieldValidation=Strict` was never set | **Yes, shipped together** — [#2156 "Add bulk actions for resources lists"](https://github.com/kubernetes-sigs/headlamp/issues/2156) → PR #2827: **delete multiple pods and restart multiple deployments**, one selection mechanism for both | Not found | README: "Cancellable creation/update/deletion operations", "Read-write / interactive" |
| **k9s** | **A file-browser workaround, not a dialog** — `Ctrl-S` saves a resource's YAML, `:dir` browses it, `e` edits, `a` applies; a dedicated "new object" editor was asked for twice and declined both times ([#191](https://github.com/derailed/k9s/issues/191) 2019, [#2001](https://github.com/derailed/k9s/issues/2001) closed `not planned`) | No demand or shipped feature found | **Yes, shipped, by design** — `Space`/`Ctrl+Space` mark rows for bulk action; [#2190](https://github.com/derailed/k9s/issues/2190) (closed `as-designed`) is a refinement request whose own motivating example is *"restarting all deployments within a namespace"* | Not found | Tagline: "navigate, observe and manage" — editing/creating is not a marketing claim at all |
| **KubeUI** | **Yes, marketed** — "Create, inspect, edit, and apply resources as YAML", "Import YAML into the cluster" ([README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md)) | **Yes, marketed** — "Server-side dry run from edit mode before saving", "Dry run manifests against the API server" ([README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md)) — already cited for `FEAT-28` | Not found in the README | Not found in the README | Leads its YAML section with dry-run and creation together, ahead of anything about diffing |
| **kubeNimbus** | No | No | No | No (nine fixed columns) | — |

Two things this table settles before any ranking.

**Create-from-scratch is not the row the brief expected the field to contradict — it is the row
with the *least* disagreement in the whole report.** Five of six products ship it, the sixth
(k9s) explicitly considered and declined a dialog while still shipping a working path to the
same outcome, and the nearest open-source peer (KubeUI) markets it in the same breath as
dry-run. No product in this survey treats "creating things is kubectl's job" as a design
position — every one of them decided the opposite, years ago.

**"Resource Diff" on Aptakube's own README is not evidence for `FEAT-5`.** It is worth stating
precisely because it would be an easy citation to reach for and get wrong: Aptakube's
comparison feature diffs two objects that already exist (confirmed by the bug report about it
breaking), not a pending edit against the live object. The dry-run-before-apply idea has real
marketing support (Headlamp, KubeUI) but it is a different feature from the one Aptakube's
README bullet advertises.

## Demand and marketing, by question

### 1. Dry-run diff (`FEAT-5`, `FEAT-28`) — marketing, and thin marketing at that

No user-filed issue asking for a dry-run or apply-preview was found in Lens, FreeLens, Aptakube
or k9s's trackers, searched by title and by their `area/editor` (Lens) / `area: yaml editor`
(Aptakube) labels. Headlamp is the one product that ships it, and the issue that produced it
([#5000](https://github.com/kubernetes-sigs/headlamp/issues/5000)) was opened and self-assigned
by the same person the same day — a maintainer's roadmap item, not a response to a request. That
matches the pattern the logs report already found for `FEAT-36` (severity filter chips):
maintainer-authored, not user-demanded, and worth shipping on parity grounds rather than pull.

**The one piece of real-world context supporting the *idea*, if not this specific shape**:
[kubectl/kubectl#1234](https://github.com/kubernetes/kubectl/issues/1234), where a user asks
for a `--show-diff` flag on `kubectl apply` precisely because "managing multiple contexts
creates opportunities for mistakes, particularly applying YAML to the wrong cluster context."
That is the fear kubeNimbus's own environment-colour scheme already exists to reduce (see
CLAUDE.md's "cluster switcher and environment colours" section) — it argues for the existing
mitigation being sound, not specifically for a diff view.

**The stronger finding sits one level down, and it is not a diff feature.** See §4/R1 below.

### 2. Create-from-scratch / apply a local file (`FEAT-12`) — the field's strongest convergence

Every actively developed GUI surveyed ships this as a **documented, core** feature, not an
extension or plugin:

- **Lens/FreeLens**: the "+ Create resource" dock tab has existed since Lens 5.0 (2021) and is
  documented at `docs.k8slens.dev/cluster/create-resource/` (domain blocked here, but its
  existence and milestone are independently confirmed by
  [lens#995](https://github.com/lensapp/lens/issues/995), the follow-on issue that added a
  template dropdown to it, closed via PR #2327 against milestone 5.0.0).
- **Aptakube**: [#24 "Ability to create resources"](https://github.com/aptakube/aptakube/issues/24)
  (opened by the maintainer, 2022, closed/shipped) asked for exactly kubeNimbus's current gap in
  its own words — *"there is only View / Edit / Delete"* — and proposed "paste YAML, apply,
  support multiple objects at once." [#271](https://github.com/aptakube/aptakube/issues/271)
  (open) is the natural follow-on, asking for more built-in templates and custom user templates —
  the same shape Lens's #995 took.
- **Headlamp**: a "+ CREATE" button that accepts an uploaded YAML file (web search snippet of
  Headlamp's own docs; not independently re-verified against the live site, flagged as such).
- **KubeUI** (kubeNimbus's nearest open-source peer): markets it directly — "Create, inspect,
  edit, and apply resources as YAML" and "Import YAML into the cluster"
  ([README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md)).
- **k9s** is the only holdout on a *dedicated dialog*, and even it was asked twice
  ([#191](https://github.com/derailed/k9s/issues/191) 2019 — a real user, not the maintainer,
  describing wanting to avoid switching to an external terminal;
  [#2001](https://github.com/derailed/k9s/issues/2001), closed `not planned`) — and its own
  documented workaround (save-to-file via `Ctrl-S`, browse with `:dir`, edit with `e`, apply
  with `a`) is a slower version of the same feature, not a refusal of the need.

**This is the row the brief flagged as most likely to be contradicted by the field, and the
evidence says the opposite.** Nobody in this survey treats "create it with kubectl" as
sufficient; every actively developed product decided a paste/upload path belongs in the app.

**A real scope split falls out of the evidence, and it matches how the field built it in two
steps.** Every product's *base* create feature is "type or paste a manifest, apply it" — cheap,
reuses the existing YAML editor and apply path with an empty buffer and no descriptor to resolve
from (the object's own `apiVersion`/`kind` supplies that, the same way a hand-typed kind change
already works in the editor today per `YamlEditorTabViewModel`'s "no apiVersion:/kind:" guard).
*Templates* were consistently a **separate, later feature request** — Lens's own template
dropdown was issue #995, filed and shipped years after the base create feature existed; Aptakube's
#271 is making the identical ask against its own already-shipped create dialog. `FEAT-12`
currently bundles both in one row; the evidence argues for treating "paste/apply YAML, no
templates" as the S-sized first step and templates as a distinct, later M/L follow-on.

### 3 & 5. Bulk operations (`FEAT-6`) and trigger-now/suspend (`FEAT-8`) — real, and broader than "delete"

The field converges on multi-select as a mechanism for **restart and trigger at least as much as
delete**, not a delete-only convenience:

- **Headlamp shipped delete and restart together, on one selection mechanism.**
  [#2156 "Add bulk actions for resources lists"](https://github.com/kubernetes-sigs/headlamp/issues/2156)
  named both explicitly — "deleting multiple pods in a single command" and "restarting multiple
  deployments at once" — and PR #2827 shipped both from the same feature.
- **k9s's own multi-select (`Space`/`Ctrl+Space`) is used for exactly that**, confirmed by its
  own refinement request: [#2190](https://github.com/derailed/k9s/issues/2190) (closed
  `as-designed`) states its motivating use case as *"restarting all deployments within a
  namespace"* — the maintainer declined the specific select-all/deselect-all ask, not the
  underlying "mark several rows, act on all of them" mechanism, which already ships.
- **Lens has two open, multi-year, unresolved asks for exactly this pairing**:
  [#3771 "The bulk management of selected Deployments"](https://github.com/lensapp/lens/issues/3771)
  (2021, open, labelled `Priority 3` by the Lens team itself — real but not urgent even to its
  own maintainers) asks to "Scale, Restart the selected Deployments"; and
  [#4095 "Start several cronjobs at the same time (bulk trigger)"](https://github.com/lensapp/lens/issues/4095)
  (2021, open) is `FEAT-8`'s bulk-trigger case stated almost verbatim.
- **`FEAT-8` gets independent corroboration beyond the bulk case.** k9s users notice and complain
  when *single*-object cronjob triggering regresses:
  [#3284 "Manual cronjob trigger no longer available since v0.50.0"](https://github.com/derailed/k9s/issues/3284)
  (closed `not planned`, but the filing itself is evidence of reliance — you only report a
  regression in a feature you were using). Aptakube shipped the single-trigger case outright:
  [#10 "Create a job from a cronjob"](https://github.com/aptakube/aptakube/issues/10) (closed).

**What this means for scope, stated as a note rather than a rewrite of either row**: `FEAT-6`'s
own text is delete-only ("multi-select in the resource list + bulk delete"), and the evidence
argues that is narrower than what the field actually builds and what `FEAT-1` already makes
cheap here. `FEAT-1` generalized scale/restart/delete onto one `RowActionViewModel` confirm strip
precisely so a fourth action does not need its own UI concept; a multi-select selection layered
under that same strip (naming the count, not one object) would cover delete, restart *and*
CronJob trigger with one mechanism, matching what Headlamp and k9s actually ship rather than
building the delete-only slice first and a second mechanism for the rest later.

### 4. Editing safety — one real, open, concrete finding; two clean negatives

**R1 (new, proposed below) is the sharpest finding in this whole report.**
[Headlamp #7147](https://github.com/kubernetes-sigs/headlamp/issues/7147) — open, unresolved —
describes this exactly: edit a Deployment, misspell `spec.replics`, run the dry-run (which shows
nothing wrong), apply (which "succeeds"), reopen the object and the field is simply gone. Root
cause, per the issue itself: the API server's field validation defaults to `Warn` mode, which
reports pruned/unknown fields only in a `Warning:` HTTP response header — and Headlamp's `apply()`
never sets `fieldValidation=Strict` and never reads that header. **`ClusterClient.Dynamic.cs`'s
`ApplyYamlAsync` has the identical gap** — its query string is `fieldManager` and `force` only,
confirmed by reading the file. This is not hypothetical for kubeNimbus; it is the same
architecture (a server-side-apply PATCH with no field validation parameter) hitting the same
Kubernetes API default.

**Two more editing-safety bug classes were found, checked against kubeNimbus's actual code, and
kubeNimbus is already immune to both — recorded here as a clean negative rather than a proposal,
the way the logs report recorded "nobody asks a desktop client to store logs":**

- **Live updates clobbering an in-progress edit.**
  [Aptakube #299](https://github.com/aptakube/aptakube/issues/299) (open): editing a
  frequently-refreshing resource (the reporter's example is a Lease) becomes impossible because
  the editor's content is continuously replaced by server updates faster than the user can type.
  kubeNimbus's YAML editor cannot have this bug by construction: `RefreshFromServerAsync` has
  exactly **one** call site, in the tab's constructor — it is a snapshot taken at open time, not a
  live stream feeding the editor, and even that one read is guarded by `IsDirty` so a race with
  the very first read cannot discard a keystroke either (confirmed by reading
  `YamlEditorTabViewModel.cs`).
- **Editing one resource while looking at another's identity.**
  [Lens #5879 "Kube editor doesn't refresh contents when jumping between tabs"](https://github.com/lensapp/lens/issues/5879)
  — labelled `blocker`, fixed via PR #5906 in Lens 6.0 — multiple open "Edit resource" tabs
  showed identical content instead of each tab's own object, which reads as evidence of a shared
  Monaco editor instance being re-bound across tabs rather than one instance per tab.
  kubeNimbus's architecture is structurally different: every opened YAML tab is its own
  `YamlEditorTabViewModel` instance with its own `YamlText` (confirmed by reading every
  `AddInspectorTab(new YamlEditorTabViewModel(...))` call site in `ClusterTabViewModel.cs`) bound
  to its own AvaloniaEdit control in the dock's `TabControl`, not a shared editor surface —
  the same bug class cannot arise the way it did in Lens's implementation.

**A third, smaller thing already fixed here and worth naming as a positive control on the
method**: Headlamp separately had to improve its apply-failure error messaging twice
([#3676](https://github.com/kubernetes-sigs/headlamp/issues/3676),
[#3047](https://github.com/kubernetes-sigs/headlamp/issues/3047), both closed/shipped) after
shipping a generic failure message instead of the API server's own explanation. kubeNimbus's
`KubernetesApiException.From` already puts the server's `Status.message` first and the HTTP code
second (confirmed by reading `ClusterClient.cs`) — this is not a gap, it is evidence the design
already anticipated a real failure mode two other products had to learn about after shipping.

### The other read of "editing safety": wrong-cluster fear, not wrong-edit bugs

Nothing found in this pass suggests people *lose data* to a Kubernetes GUI editor at a rate
worth headline concern — the concrete bug reports found (Headlamp's field-drop, Lens's tab
bleed) are specific, fixable implementation defects, not a pattern of the editing model itself
being unsafe. The much louder fear, in both the kubectl issue above and in kubeNimbus's own
CLAUDE.md history (the cluster-switcher section's own citations), is **applying to the wrong
cluster**, which is a different problem this app already has a dedicated, marketed mitigation
for (environment colours). No new row is proposed for that; it would be redundant with a
shipped feature.

## No evidence found

Stated because these were looked for and the absence is the useful answer:

- **No evidence that a dry-run/preview UI is asked for by users**, in any of Lens, FreeLens,
  Aptakube or k9s's trackers, searched both by keyword and by the relevant editor-area label
  where one exists. The one product that ships it built it from a maintainer's own initiative,
  not a filed request.
- **No evidence that "Resource Diff" (Aptakube's own marketed feature) is the same thing as a
  dry-run diff.** It compares two already-existing objects. Do not cite Aptakube's README bullet
  #3 as evidence for `FEAT-5`; it is evidence for a different, unfiled feature (see the note
  below).
- **No evidence that create-from-scratch is treated as kubectl's job by any actively maintained
  GUI.** This is the cleanest negative-of-a-negative in the report: the brief's own hypothesis
  that the field would contradict `FEAT-12` does not hold.
- **No evidence of a column chooser causing safety problems, or any negative signal about the
  feature at all** — every issue found about it is a straightforward "let me hide/reorder/widen
  columns" request or a follow-on refinement, not a complaint about the feature once shipped.
- **No evidence of a widely-reported data-loss incident specific to a Kubernetes GUI editor.**
  Reddit and Hacker News were not reachable this session (domain-blocked), which limits this
  finding's reach — it is an absence in what could be searched, not a confirmed absence in the
  wider world, and is reported with that caveat rather than as a clean negative.
- **No evidence found for Aptakube's Multi-Namespace Selector or Headlamp's plugin story being
  connected to any of this brief's questions** — mentioned in passing in the field table and not
  pursued further, since they are out of scope for creating/editing specifically.

## An unfiled, marketed feature this report noticed but does not propose

Aptakube's actual "Resource Diff" — comparing two already-existing objects side by side, not
previewing a pending apply — is a real, marketed, apparently well-used feature (bug report
[#559](https://github.com/aptakube/aptakube/issues/559) confirms real usage: someone hit a
regression in it) that nothing in kubeNimbus's backlog currently tracks under any name, `FEAT-5`
included. No user-demand evidence for it was found beyond that one bug report, so this report
does not propose it as a row — flagging it here is a "found, not ranked" note for whoever reads
this next, not a recommendation.

## Verdict on the six rows this report was asked to validate

| Row | Verdict |
|---|---|
| `FEAT-5` (dry-run diff, P2) | **Marketing only.** Keep the row; do not raise its priority on this evidence. A cheaper, better-evidenced safety fix (R1 below) should come first |
| `FEAT-28` (plain dry-run, P2) | Same verdict as `FEAT-5` — it is that row's cheap half and inherits its evidence exactly |
| `FEAT-6` (bulk delete, P2) | **Real demand, understated scope.** Broaden to bulk scale/restart/trigger riding `FEAT-1`'s confirm strip, not delete-only |
| `FEAT-12` (apply file / create from template, P3) | **The field's strongest convergence — this report's clearest finding.** Every actively developed competitor ships it; recommend raising priority substantially and splitting "paste+apply" (S) from "template library" (M/L) |
| `FEAT-8` (Job/CronJob trigger, suspend/resume, P2) | **Confirmed, not just hypothesis.** Real open asks in Lens and Aptakube, and a regression report in k9s shows people rely on and miss it |
| `FEAT-13` (column chooser, P3) | **Confirmed, more iterated than expected.** Five shipped + three open issues in Aptakube alone; the oldest one's motivating complaint is a problem kubeNimbus has already had to fix once (UI rule 14) |

That is a priority decision and therefore a human's. The rows below are filed with the priority
column blank, as instructed.

## Proposed backlog items

Filed into the Inbox table of `docs/BACKLOG.md`, section B. IDs left as `—` for a human to
assign; `Rec` is this report's recommendation only. Every row below either amends an existing
row (stated explicitly; the existing row's own text is left untouched) or is new work with no
existing row behind it.

| — | **New.** Send `fieldValidation=Strict` on server-side apply (`ClusterClient.Dynamic.cs`'s `ApplyYamlAsync`), and surface a 4xx `Strict`-rejection the same way a 409 conflict already surfaces — *done when a misspelled/unknown field is refused with the server's own message rather than silently pruned* | **Demand (concrete, from a comparable architecture):** [headlamp#7147](https://github.com/kubernetes-sigs/headlamp/issues/7147) — open, unresolved — describes exactly this failure on the same request shape (SSA PATCH, no field validation param): dry-run passes, apply "succeeds," a misspelled field vanishes silently, because the API server's default `Warn` mode only reports pruned fields in a response header nothing reads. **Confirmed as kubeNimbus's own gap**, not a hypothetical: `ApplyYamlAsync`'s query string is `fieldManager`/`force` only | S | P1 | | **This is not a diff feature and does not need one** — it is one query parameter (`&fieldValidation=Strict`) plus reading the response the same way a 409 already is. Cheaper and more concrete than `FEAT-5`/`FEAT-28`, and arguably a prerequisite for either being trustworthy: a dry-run diff built on top of `Warn`-mode validation would show a *clean* preview for the exact typo this fixes, which is worse than no diff at all. No non-goal tension. Older API servers (pre-1.27, where server-side field validation is not GA) may reject an unrecognized query param — worth a capability check or a graceful fallback to today's behavior, not a blocking dependency |
| — | **Amends `FEAT-5` and `FEAT-28` — correct the Signal, not the scope.** Both rows' Signal should read: no user-demand evidence found in Lens/FreeLens/Aptakube/k9s trackers; Headlamp's dry-run ([#5000](https://github.com/kubernetes-sigs/headlamp/issues/5000)) was maintainer-authored and self-assigned the same day, not a response to a filed request; KubeUI's is marketed but likewise undemonstrated by any linked user issue | **Marketing only**, confirmed across a wider set of trackers than `FEAT-28`'s original citation covered. Aptakube's README "Resource Diff" bullet does **not** support this row — it is a different feature (compare two existing objects; see the note above) and should not be cited for `FEAT-5` in future | — | | | Recommend: do not raise priority on this evidence alone. The new fieldValidation row above is the higher-leverage, cheaper investment in the same problem space and should be sequenced first regardless of what happens to these two |
| — | **Amends `FEAT-12` — raise priority; split scope.** "Paste or type YAML, apply it" (no template picker) is the S-sized first step every competitor built first; a template library is a distinct, later M/L feature every competitor added as a separate follow-on request | **Demand + marketing, the strongest convergence in this report.** Shipped as a documented core feature in Lens/FreeLens (since 5.0, 2021 — [lens#995](https://github.com/lensapp/lens/issues/995)), Aptakube ([#24](https://github.com/aptakube/aptakube/issues/24), 2022), Headlamp ("+ CREATE", upload YAML), and KubeUI ("Create, inspect, edit, and apply resources as YAML", "Import YAML into the cluster" — [README](https://github.com/IvanJosipovic/KubeUI/blob/main/README.md)). k9s is the only holdout on a dialog and still ships a working file-browse-and-apply path; two of its own users asked for a dialog directly ([k9s#191](https://github.com/derailed/k9s/issues/191), [#2001](https://github.com/derailed/k9s/issues/2001)) | S (paste+apply) / M–L (templates) | P1 | | **This directly contradicts the brief's own stated expectation** ("the row I'd most expect the field to contradict") — the evidence says the opposite. Implementation note: the base case reuses the existing YAML editor and apply path against a blank buffer with no `ResourceDescriptor` to resolve up front — the object's own `apiVersion`/`kind` supplies that once typed, the same way `YamlEditorTabViewModel`'s existing "no apiVersion:/kind:" guard already anticipates an incomplete document. No non-goal tension |
| — | **Amends `FEAT-6` — broaden scope from "bulk delete" to the same multi-select riding `FEAT-1`'s existing confirm strip for delete, restart *and* CronJob trigger** | **Demand.** Every product that ships multi-select uses it for more than delete: Headlamp shipped delete-and-restart together in one PR ([#2156](https://github.com/kubernetes-sigs/headlamp/issues/2156) → PR #2827); k9s's shipped `Space`/`Ctrl+Space` multi-select cites *"restarting all deployments within a namespace"* as its own motivating case ([#2190](https://github.com/derailed/k9s/issues/2190)); Lens carries two open, multi-year unresolved asks for exactly this — [#3771](https://github.com/lensapp/lens/issues/3771) (bulk scale/restart, 2021, Lens's own `Priority 3` label) and [#4095](https://github.com/lensapp/lens/issues/4095) (bulk cronjob trigger, 2021) | M | P2 | | `FEAT-1` already generalized scale/restart/delete onto one `RowActionViewModel` strip precisely so a new action needs no new UI concept — a selection count above that same strip, rather than a delete-only dialog, is what the field actually converged on. Lens's own `Priority 3` label on #3771 is worth weighing: real demand, but not urgent even to its own maintainers |
| — | **Amends `FEAT-8` — the Signal moves from hypothesis to confirmed** | **Demand, confirmed.** [lens#4095](https://github.com/lensapp/lens/issues/4095) (open since 2021) asks for bulk cronjob trigger, which presupposes single trigger as the baseline pain point; [k9s#3284](https://github.com/derailed/k9s/issues/3284) — a *regression* report ("Manual cronjob trigger no longer available since v0.50.0," closed `not planned`) — is evidence of reliance, since people only report losing a feature they were using; Aptakube shipped the single-trigger case outright ([#10](https://github.com/aptakube/aptakube/issues/10), closed) | — | | | No scope change proposed — this is confirmation only. Pairs naturally with the bulk-actions row above once `FEAT-1`'s mechanism is extended to CronJobs |
| — | **Amends `FEAT-13` — the Signal moves from hypothesis to the best-evidenced item in this report** | **Demand, heavily iterated in the closest paid competitor.** Aptakube: five shipped issues ([#34](https://github.com/aptakube/aptakube/issues/34), [#92](https://github.com/aptakube/aptakube/issues/92), [#121](https://github.com/aptakube/aptakube/issues/121), [#180](https://github.com/aptakube/aptakube/issues/180), [#261](https://github.com/aptakube/aptakube/issues/261)) plus three open follow-ons ([#343](https://github.com/aptakube/aptakube/issues/343) more columns, [#472](https://github.com/aptakube/aptakube/issues/472) resize, [#565](https://github.com/aptakube/aptakube/issues/565) init-container count). [#92](https://github.com/aptakube/aptakube/issues/92)'s own motivating complaint — the Age column consistently pushed off-screen by wider ones — is the identical failure kubeNimbus's own UI rule 14 gutter pass already had to fix once by re-cutting column minimums, which is evidence the pressure is real here too, not just in Aptakube | — | | | Worth reading beside UI rule 14's own account of the 1280px width fight: a chooser is the general answer to a problem this repo has already patched once by hand. No scope change proposed here, just evidence |
| — | **New, weak — stated as such.** A resource-to-resource comparison view (Aptakube calls this "Resource Diff") — select two existing objects, see their spec differences | **Marketing only, and thin even there.** Aptakube markets it as README bullet #3, and one bug report ([#559](https://github.com/aptakube/aptakube/issues/559)) confirms real usage, but no user-filed feature request or upvoted issue was found asking for it in any tracker. **This is not evidence for `FEAT-5`** — it is a distinct, unfiled feature | M | P3 | | Do not conflate with `FEAT-5`/`FEAT-28` in future citations — that mistake is easy to make from the README alone (this report nearly made it) and is exactly why this row states the distinction explicitly rather than folding it into either existing row |

FEAT-12's split and the fieldValidation row are, in this report's judgment, the two rows worth
reading first: one corrects a real, evidenced gap in a stated priority (create is wanted more
than the backlog currently reflects), the other closes a concrete safety hole with a
one-parameter fix that the diff-shaped rows above it would not have caught on their own.

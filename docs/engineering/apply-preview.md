# The apply preview (server-side dry run)

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


Apply used to be blind: the editor sent the document and reported what came back.
`ClusterClient.PreviewApplyAsync` + `ResourceDiff.cs` + `TextDiff.cs` (Core) and the panel
under the editor (`ApplyPreviewViewModel`, UI rules 9 and 17) turn it into a two-step
action — ask the server what it would do, then decide. The step is a preference
(`AppSettings.PreviewApplies`, on by default), read **at the press** like the delete
confirm and for the same reason.

The panel shows the **manifest itself, with its changed lines in place** — `kubectl
diff`'s shape, `git diff`'s shape, VS Code's diff editor's shape. It shipped as a list of
field paths (`spec.template.spec.containers[worker].image`, old above new), which is
precise, compact, and not how anyone reads a manifest change; the field list is still
there as the third view mode, because it is the only one of the three that knows a
container was *inserted* rather than every container rewritten.

Thirteen things are load-bearing.

1. **Both sides of the diff come from the server.** The live object is a GET; the other
   side is the `dryRun=All` response. That is the entire difference between this and
   diffing the editor's text against the object: the dry-run body has been through
   defaulting, admission webhooks and every mutating controller in the chain, so a field
   the cluster is going to add or rewrite is *in the diff*, and cannot be in a local one.
   It is also why the preview is worth a round trip rather than being computed offline.
2. **`dryRun=All` is the only value, and it is kubectl's own.** The API server runs every
   admission stage and the whole validation chain and then discards instead of
   persisting. A validating webhook's refusal, a schema violation and an RBAC 403
   therefore all arrive here having changed nothing, which is the point — `PreviewCoreAsync`
   prints the server's own sentence rather than a paraphrase.
3. **A 409 conflict during the preview is an answer, not a failure.** It raises the same
   `ServerSideApplyConflictException` a real apply does, so the conflict panel and its
   force-apply appear *before* the object moves. Force-apply is previewed too, and that
   is the case where a preview earns the most: what it changes is precisely the fields
   somebody else is managing. The confirm button says `Force apply` rather than
   `Apply changes` — the more consequential of the two applies must not be confirmed
   under the same word.
   **A conflicting Review still shows the would-be diff** (#316, QA Y-8). The conflict says
   the apply would collide, not what it would change, and someone deciding whether to take the
   fields was deciding blind. On a conflict, `PreviewCoreAsync` runs the same dry run with
   `force=true` (`ForcedPreviewAfterConflictAsync`; still `dryRun=All`, so nothing changes) and
   opens the panel on it, its headline "Apply conflict. A force apply would take these fields
   and make N changes:", the server's conflict sentence on the line under it, and its confirm
   button `Force apply`. The separate conflict card (`ShowsConflictPanel`) stands only without
   such a panel — a conflict on a direct apply, after Cancel, or when the forced dry run fails
   too. `ApplyConflictPreviewTests` replays a 409 and a forced 200 through a real
   `ClusterClient`; it went red with the forced dry run dropped, and checks that both PATCHes
   carried `dryRun=All`. `cluster-tab-yaml-conflict-diff` renders it at the dock's default height.
   #404 (the 0.8.0 macOS pass, synthetic clicks: the first Force apply did nothing, the second
   applied) was looked for and not found in the code: the panel's `Force apply` runs
   `ConfirmPreviewCommand`, whose `CanExecute` is constant and which sends the forced apply on
   its first run (`ApplyConflictPreviewTests.The_previews_force_apply_writes_on_its_first_run`).
   One path does take two clicks by design: the separate conflict card's `Force apply`, with the
   preview on, runs the forced *dry run* and opens the panel, whose own `Force apply` then writes
   — the card and the panel's button sit in about the same place. The QA VM check in that PR's
   body settles which the pass clicked.
4. **Three fields are excluded and counted, not shown.** `metadata.managedFields` is the
   apply's own bookkeeping and changes on every apply including one that changes nothing
   else; `resourceVersion` and `generation` are the server's counters. Leaving them in is
   what makes `kubectl diff` hard to read. The count is printed (`1 server bookkeeping
   field hidden (…)`) because what a diff withholds has to be said out loud — a panel
   that is quietly incomplete is the exact failure this feature exists to prevent one
   level up.
5. **Lists are matched by `name` where every element on both sides has a unique one.**
   Containers, ports, env vars, volumes and volumeMounts all have that shape and it is
   Kubernetes' own merge key for them. Without it, inserting one container at the front
   reports *every* container as changed, which is the loudest noise source in a real
   deployment diff. Duplicate names, unnamed objects and scalars fall back to index
   pairing, because a wrong pairing invents changes. A pure reordering is reported as its
   own line naming both sequences: it changes no element, and for `env` it is semantic.
6. **The preview describes one exact document and dies with it.** Editing the text,
   reloading, or applying clears it. A stale diff above a live editor is worse than no
   diff — it is a wrong answer wearing the server's authority. `YamlEditorPreviewTests`
   pins that, and the break was written and confirmed red before the test was called done.
7. **While a diff is open it takes the editor's row, from code-behind.** The preview's row is
   star-sized while a diff is open and `Auto` when it is not (or when the diff is empty, which
   is one sentence and two buttons). Both of the obvious XAML answers were tried and rendered
   wrong at the dock's default ~300px: an `Auto` row tall enough to read left a zero-height
   editor, and a `MinHeight` on the editor pushed the grid past the dock and overlapped its
   own rows. Mutating a `RowDefinition` is what `ClusterTabView.ApplyDockState` already does,
   for the same reason. An even split left the diff body at *zero* height; the 3:1 weight that
   replaced it left, by the QA VM's measurement (#316, Y-8), "about two lines in a ~35 px area"
   with the footnote drawn over them. So since #316 the editor's row collapses while a diff is
   open (`EditorRow.IsVisible = false`, height 0) and the panel takes the whole pane: the editor
   cannot be anything but context here, because typing in it discards the preview by rule 6
   below, and Cancel gives it back. The status line under the panel takes no row while it is
   empty (`ShowsStatusMessage`), which was one more line of diff. `cluster-tab-yaml-diff-dock`
   renders the diff at the default height: about six lines, the footnote under them; the dock's
   own maximize toggle sits directly above the panel and is what a long diff wants. The
   panel itself is a `ContentControl` + inline `DataTemplate`, never a `Border` with both
   `DataContext` and `x:DataType` — UI rule 17 records what that pair renders, which is
   nothing at all, silently.
8. **The line diff is over the two server documents, not over the editor's text.** Rule 1
   is what makes the preview worth a round trip, and rendering it as text must not quietly
   give that up: `ApplyPreview` carries the live object as well as the previewed one, and
   each side is serialized by `ResourceDiff.ToDiffableYaml` — the object as YAML with the
   same three bookkeeping fields removed. `managedFields` alone is routinely a third of a
   real object and changes on every apply, so a text diff over the raw documents would
   open on the one section nobody wants to read. What was removed is still counted and
   stated by rule 4's footnote, which is the half that keeps the omission honest.
   The text ends its lines with `\n` on every platform (#284, #287): YamlDotNet writes
   `Environment.NewLine`, so on Windows every line split on `\n` kept a stray `\r`. The
   replace lives in `YamlJson.ToYamlString` itself since #287, so the editor's text is LF too,
   not only the diff's (#284 had fixed the diff path alone). A line break inside a value is
   written as a block scalar, and any YAML parser normalizes a block scalar's line breaks to
   `\n` (YAML 1.2, 5.4), so the replace changes no value: a value's `\r\n` never came back
   from the text as one on any platform, before or after. (That last part is a loss of its
   own — a ConfigMap value with CRLF line ends, opened and applied, comes back LF — and is
   listed for the backlog rather than fixed here.) `TextDiffTests` round-trips such a value
   (#284); `YamlJsonTests.The_yaml_text_ends_its_lines_with_a_line_feed_on_every_platform` pins
   the editor's text and was confirmed red on Windows with the replace taken out. The panel's screenshots cover inline maximized
   (`cluster-tab-yaml-diff-preview`), split at the dock's height and maximized
   (`cluster-tab-yaml-diff-split`, `-split-maximized`), and fields maximized and at the
   dock's height (`cluster-tab-yaml-diff-fields`, `-fields-dock`); the last two pairs were
   added for #284, so a layout regression in either combination is in front of a reviewer.
9. **`TextDiff` is in Core, pure, and bounded on purpose.** Common prefix and suffix are
   trimmed first — two serializations of nearly the same object share almost everything —
   and only the middle goes through an LCS. The table is `(n+1) × (m+1)` ints, so it is
   capped at a million cells; past that the middle is reported as one removal followed by
   one insertion and `IsApproximate` is set, which the panel *states* in the footnote.
   A diff that silently stops aligning is worse than one that admits it.
   **The trimming hides the LCS from most tests, and that is worth knowing before writing
   one**: an insert, a delete and a replace in an otherwise identical document are all
   settled by the prefix/suffix trim alone, so replacing the LCS with index pairing left
   the whole suite green. What catches it is a change at the top *and* at the bottom with
   an insert between them — a manifest with an edited label and an edited replica count,
   i.e. the ordinary case — and that is what
   `An_insert_between_two_changed_ends_leaves_the_middle_untouched` is for.
10. **Collapsing is part of the feature.** Three lines of context around each changed run,
   the skipped count stated (`10 unchanged lines`), because a Deployment serializes to ~60
   lines and a CRD to several hundred and the panel is a ~300px dock. A run of one line is
   kept rather than collapsed — a "1 unchanged line" separator is the same height as the
   line it replaces and says less. A diff with *nothing* in it produces no rows at all
   rather than one gap covering the document: the first cut rendered `56 unchanged lines`
   under "this apply would change nothing", which only the screenshot showed.
11. **Side by side is derived from the same row list, never computed a second way.** A
   removed run and the added run following it are zipped, and the shorter side gets
   fillers — the blank a deleted line has to face is the whole reason this mode is more
   than a two-column layout. Two independently built layouts can disagree about what
   changed, which is the one thing a diff may not do. Long lines are trimmed with the full
   text on the tooltip rather than wrapped: wrapping one side pushes it out of step with
   the other.
12. **The view mode is a session-scoped toggle on the tab, and no syntax highlighting.**
   `Diff / Split / Fields` is a `ListBox.segmented` sharing the panel's one chrome row (UI
   rule 10), and `YamlEditorTabViewModel.PreviewViewMode` holds it, so choosing side by
   side once survives the next apply. It is deliberately not a preference — a view toggle
   inside a pane, like the log pane's timestamps and wrap toggles. Highlighting the diff
   would mean an AvaloniaEdit instance per side and a colouring pass, which is a different
   feature; monospace plus a tinted line background is what VS Code's own inline diff
   leans on anyway, and the marker glyph carries the direction for anyone who cannot
   separate the two tints.

13. **What is sent is the editor's text minus `metadata.managedFields`, and nothing else is
   removed.** Since #270 the editor does not show the field at all by default (rule 14); the
   removal below stays, for the text a reader asked to see it in. The editor opens an object exactly as the server returned it, so its text carries
   every server-written field, and a real API server refuses an apply patch that carries
   `managedFields`: `metadata.managedFields must be nil` (HTTP 400, on the dry run and on the real
   apply alike). Apply on an opened ConfigMap was therefore broken outright, and none of the
   suite noticed, because every live test applied a manifest it had written itself, which never
   carries the field; the 0.5.1 release pass found it by pressing Apply. `ClusterClient.ApplyBody`
   removes the field once, for the preview, the apply, the force-apply and the retry without
   strict validation, since all four go through `SendApplyAsync`; kubectl does the same before
   it sends anything. Everything else stays, and each was checked against a real server rather
   than assumed: `resourceVersion` is an optimistic lock and is kept on purpose, so an object
   that changed while the editor was open is a Conflict and not a silent overwrite; `uid`,
   `creationTimestamp`, `generation`, `selfLink` and `status` are accepted in an apply body and
   are left alone. `ApplyLiveTests.An_object_read_back_from_the_server_can_be_previewed_applied_and_force_applied`
   drives open, edit, preview, apply and force-apply for a Deployment, a ConfigMap and a custom
   resource, and `ApplyPreviewHttpTests` pins the body on all three paths. Both were confirmed
   red with the removal commented out.
   **A Conflict from `resourceVersion` is not a field conflict, and a force-apply does not
   override it.** A Deployment's controller writes its status for a few seconds after almost
   any change, which moves `resourceVersion`; an Apply from a text opened before that is refused
   with "the object has been modified" whatever `force` says. The live test waits for the object to settle
   for this reason. Reload, then apply again, is the way through.

14. **`metadata.managedFields` is out of the editor's text by default (#270, the owner's
   decision of 2026-10-11).** On a Deployment it is a couple of hundred lines (`f:lastUpdateTime:
   {}`, `k:{"type":"Progressing"}` …) at the top of `metadata`, above every field the editor is
   opened to change, and `kubectl get -o yaml` and `kubectl edit` have hidden it by default
   since 1.21. `ManagedFieldsText` (Core) takes it out of the server's YAML on open, reload and
   after an apply, and keeps the block to put back at the same position; the **Managed fields**
   chip in the editor's header row shows it, and is present only when the object has the field
   (UI rule 1: it is the one place that says something was left out of the text, UI rule 20).
   It is a view toggle inside one pane, like Reveal values beside it, not a preference, so it
   persists nothing and `PRIVACY.md` is unchanged. Moving the field in or out is not an edit
   (`IsDirty` is kept) and keeps an edit made meanwhile; over text that no longer reads as one
   YAML document the chip stays as it was and says why. The work is on YamlDotNet's node tree,
   so only the key directly under the top-level `metadata` of a single document is touched —
   a ConfigMap holding a manifest keeps its own `managedFields` key. Nothing an apply sends
   changes: rule 13 already removed the field from every body, and a server-side apply without
   it leaves the server's record in place (`ApplyLiveTests` now applies the hidden text and
   checks every manager the object had is still listed afterwards, on k3s, for a Deployment, a
   ConfigMap and a custom resource). `ManagedFieldsTextTests` (Core) pins hide-then-show giving
   back the server's text byte for byte, a nested key left alone and unreadable text left as it
   is; `YamlEditorManagedFieldsTests` (App) pins the editor's half, and went red with the text
   returned unhidden. Screenshots: `cluster-tab-yaml-managed-fields-hidden`, `-shown`.
**The toolbar button says Review…, not Apply, while the preview is on (FEAT-69, the owner's
wording, 2026-10-10).** It used to read `Apply`, 100px above the panel's own `Apply changes`,
and the two did different things: the first asks the server, the second changes the object.
So the toolbar's verb is `Review…` (the ellipsis is UI rule 17's "it asks"), and the panel
keeps `Apply changes` / `Force apply`. With "Preview before applying" off the button applies
directly and reads `Apply`. `YamlEditorTabViewModel.ApplyButtonLabel` follows the preference:
read when the editor opens, refreshed by each press (which reads the setting anyway) and by
the preferences page through `NotifyPreviewPreferenceChanged`, which each editor unsubscribes
from when it closes. `YamlEditorApplyLabelTests` pins both labels and the live relabel.

**Ctrl/Cmd+S is the Apply button.** `CommandId.ApplyYaml` was in the catalog, and so on the F1 sheet and the docs page, with no handler anywhere: the 0.5.1 pass pressed it and nothing happened. `YamlEditorView` handles it in the tunnel phase (the editor would otherwise get the key first) through `CommandBindings.Matches`, so it follows the Ctrl/Cmd preference, and it runs `ApplyCommand`, which keeps every rule above (the preview when the preference is on, disabled in the demo cluster). `ux-yaml-apply-key` in the screenshot harness presses it against a real window and was confirmed red with the handler removed.

**The demo cluster is unchanged and needs no new refusal:** there is no `ClusterClient`,
so Apply, force-apply and the preview are all already disabled by `CanExecute` under the
editor's existing demo notice (demo rule 5).

**What is not built, deliberately:** syntax highlighting inside the diff (rule 12),
word-level highlighting within a changed line, a copy-the-diff button, a preview for the
row list's own scale/restart/delete actions (those already arm a strip that names exactly
what they do), and `--dry-run=client`, which answers a question nobody has: the client's
copy of the manifest is the text on screen.

**The apply asks for strict field validation, and says so when it cannot get it**
(`FEAT-41`). Without `fieldValidation=Strict` the API server runs its default `Warn`
mode, which *prunes* a misspelled or unknown field and reports it only in a response
header nothing reads: the apply answers 200 having dropped the edit, and the preview
built on the same request shows a clean diff for exactly that typo, because the field
is not in the dry-run body either. That is a quieter failure than having no preview at
all, which is why it was a prerequisite for this feature being trustworthy rather than a
nicety. Both halves of `SendApplyAsync` — the real apply and `dryRun=All` — now carry the
parameter, so the refusal arrives before the object changes. Five things are
load-bearing.

1. **A refused field is its own exception and its own panel, and neither offers a
   button.** `ServerSideApplyValidationException` is shaped like
   `ServerSideApplyConflictException` — the server's own sentence, which names the field,
   plus the raw Status — and lands in `YamlEditorTabViewModel.ValidationDetails` as an
   `infoBar` under the editor (UI rules 9 and 11). What it deliberately does *not* have is
   the conflict panel's Force apply: a 409 has something to force, and a rejected field
   has nothing but an edit. A retry control here would be a button offering to apply the
   typo.
2. **The rejection is classified from the message, because there is no other signal.**
   An unknown query parameter and an unknown manifest field are both HTTP 400.
   `LooksLikeFieldValidationRejection` matches the API server's own three wordings —
   `strict decoding error`, `unknown field`, `field not declared in schema` (apply's typed
   patch conversion) — and is checked at 400 *and* 422, since the two paths do not agree
   on the code. **A real server adds a third code: 500.** Against k3s v1.33
   (`Live/ApplyLiveTests`, VER-36) a server-side apply carrying an unknown field — top
   level of a ConfigMap, nested in a Deployment's spec, or in a custom resource — is
   refused with `HTTP 500`, `{"kind":"Status",…,"message":"failed to create typed patch
   object (ns/name; apps/v1, Kind=Deployment): .spec.replicaz: field not declared in
   schema","code":500}`, on the dry run and on the real apply alike. Classified by the
   400/422 gate it fell through to "Apply failed: … (500 Internal Server Error)" instead
   of the rejected-field panel, so a 500 is now accepted *only* with that wording
   (`ApplyRefusalStatusTests` replays the verbatim body; any other 500 stays a server
   failure).
   The same test shows the server refuses the field **in every `fieldValidation` mode** —
   `Ignore` and `Warn` too — because the typed-patch conversion runs before validation
   mode is consulted. So for a server-side apply of a typed object the "Warn prunes the
   typo" premise above does not hold on this server: the apply is refused either way, and
   the fallback's "an unknown or misspelled field is dropped rather than refused" note is
   pessimistic here. Whether it is accurate on a pre-1.27 server — the only kind that ever
   shows it — is unobserved.
3. **The strict rejection is ruled out before the parameter is suspected, and the order is
   the whole safety of the fallback.** A document carrying an unknown field literally
   named `fieldValidation` produces a 400 whose message mentions the word, and a fallback
   that read that as an old server would drop the parameter, retry, and apply the very
   typo the strict request had just caught. `A_rejected_field_named_like_the_parameter_is_not_read_as_an_old_server`
   pins it, and swapping the order was written and confirmed red.
4. **The pre-1.27 fallback is one retry, remembered per connection, and it silently loses
   strictness — which is why it is never silent in the UI.** A 400 that mentions
   `fieldValidation` and is not a strict rejection means this server (or a proxy or
   aggregated API server in front of it) decodes query parameters strictly and does not
   know this one; the apply is sent again without it so applying stays possible, and
   `ClusterClient.SupportsFieldValidation` goes false for the rest of the connection so
   the doomed first request is not repeated on every apply. From then on the server is
   back in `Warn` mode — an unknown field is pruned exactly as before this shipped — so
   `ApplyPreview.StrictValidation` carries that into the panel's footnote and a successful
   apply appends the same sentence to its status line. A degradation nobody is told about
   is the failure this whole item is about, one level up.
   **The honest half is what this cannot detect at all**: a server that *ignores* an
   unrecognized parameter rather than rejecting it answers 200 and looks identical to a
   strict one, so strictness is lost with no signal anywhere. kubectl's answer is its
   `QueryParamVerifier`, which reads the OpenAPI document for the endpoint and asks
   whether `fieldValidation` is a declared parameter; that is a second round trip and a
   second parser, and it is deliberately not built here. If a real pre-1.25 server ever
   turns out to behave that way in practice, that verifier is the fix — not a heuristic.
5. **`ApplyPreview` gained the flag rather than the panel querying the client.** The
   preview describes one exact request; whether *that* request was strict is a property of
   it, not of whatever the connection has learned since. The default is `true`, so every
   fixture and test that builds a preview by hand keeps meaning "strict".

**The line diff over real answers** (`Live/ApplyPreviewTextDiffLiveTests`, VER-34, k3s v1.33.4
with cert-manager v1.21.2): a Deployment manifest with every default left out, previewed as a
create, shows the defaults (`progressDeadlineSeconds`, `revisionHistoryLimit`, the rolling
update's `maxSurge`, `imagePullPolicy`, `terminationMessagePath`, `schedulerName`, `dnsPolicy`)
as added lines; a CertificateRequest's preview shows the `username` and `groups` cert-manager's
**mutating webhook** writes, which a dry run calls because the webhook declares no side effects;
and cert-manager's 900-line Certificate CRD, with two descriptions edited far apart and
force-previewed, diffs to exactly those two lines, collapses to two hunks of three context lines
each, and under a 100-cell budget reports `IsApproximate` with both edits still shown. What is
left of VER-34 is the panel itself in both dock heights, driven by hand.

## New YAML… (FEAT-12, the paste-or-type slice)

**New resource from YAML…** in the ☰ menu and the palette opens an empty YAML editor in the dock.
It applies through everything above — the preview, strict field validation, the conflict and
validation panels — because it is the same editor; only where it applies is new. Five things
are load-bearing.

1. **What it applies to is read from its text at every press** (`NewManifest.Read`, Core, pure):
   apiVersion, kind, metadata.name and metadata.namespace. The kind is resolved against this
   cluster's discovery catalog (the whole catalog, not the sidebar, which the Advanced view can
   shorten), with the manifest's own version, as kubectl sends it. A kind the cluster does not
   serve is refused before anything is sent, naming the group, rather than reaching the server as
   a 404 on a guessed path.
   **Closing the editor stops that read** (#342, hard rule 2): the resolver takes a
   `CancellationToken` from a source the editor cancels in `OnClosingAsync`, and the tab's
   resolver passes it to `GetResourceCatalogAsync`. A cancelled read ends the press quietly, with
   nothing sent and nothing said to a closed tab. The catalog is cached after the first read, so
   this matters for the first press on a slow or unreachable API server.
   `NewYamlTests.Closing_the_editor_cancels_the_catalog_read_its_apply_started` holds the
   discovery request in a stub server and closes the editor; it went red (the apply waited past
   the timeout) with the token dropped from the catalog read.
2. **A namespaced object without a namespace goes to the list's namespace** — the first, when the
   list shows several — else the context's, else `default` (kubectl's order), and the InfoBar over
   the empty editor says which. The manifest is sent as typed, without a namespace, to that path;
   the API server fills it in from the path. A manifest naming its own namespace is sent to *that*
   namespace's path, because the server refuses a body that disagrees with its path.
   `NewManifestLiveTests` checks both against k3s.
3. **One object per editor.** `YamlJson.ParseYamlToJson` reads only the first document, so a pasted
   multi-document file would have applied its first object and dropped the rest without a word;
   `NewManifest.Read` counts documents first and refuses more than one, and a `v1 List` too.
   `generateName` without a name is refused with its reason: a server-side apply needs a name.
4. **Each one is its own tab and never replaces an editor** (UI rule 5): its key is
   `yaml:new:<n>`, unique per tab, and it is never a preview tab. After its first apply its title
   becomes `Kind/name`, Reload and Delete come alive (they were disabled, with nothing on the server
   to act on) and the InfoBar goes; it stays a new-document editor, so editing the name and
   applying again creates a second object. **At each apply it is re-keyed to the object it
   created** (#300): the key becomes `KeyFor` of the descriptor discovery returned (the version a
   list row's key uses, not the manifest's own), so E on that object's row focuses this editor
   instead of opening a second one beside it. `NewYamlTests.After_the_apply_E_on_the_created_object_focuses_the_new_document`
   went red with the re-key taken out.

**The editor E opens refuses a second document too** (#300). It applies the object it was opened
on, and `ClusterClient.ApplyBody` reads the first document only, so a second one pasted below was
neither applied nor refused. `RefusesMultipleDocuments` counts documents (`YamlJson.CountDocuments`)
before the preview and before a direct apply and says "Nothing was applied: this text holds more
than one document (---)…", with nothing sent. `NewYamlTests.An_opened_object_refuses_a_second_pasted_document`
covers the preview on and off, and went red with the check bypassed.
5. **Not on the demo cluster, and not before a connect**: `ClusterTabViewModel.CanCreateFromYaml`
   is false with no `ClusterClient`, the ☰ item is disabled and the palette has no row.

## Open a YAML file… (FEAT-12, the local-file slice, #301)

**Open a YAML file…** in the ☰ menu (under New resource from YAML…) and the palette picks a file
with Avalonia's `StorageProvider` and opens it in the same new-document editor, so everything
above applies unchanged: the kind read from the text at the press, the namespace default, Review,
server-side apply, the re-key after the first apply. Four things are load-bearing.

1. **A multi-document file opens on its first document, and says so in place.** Of the two honest
   options — apply object by object with per-object results, or open one document and say so —
   this is the smaller: the editor already applies exactly one object (rule 3 above), and an
   object-by-object apply would need a second result surface beside the editor's own. The InfoBar
   reads "stack.yaml holds 3 documents. Only the first is open here, because an editor applies one
   object; the other 2 were not opened and nothing was applied." before anything is sent.
   `YamlJson.FirstDocument` (Core) cuts the first non-empty document out of the source by the
   YamlDotNet parser's marks, never by splitting on `---` lines (a `---` inside a block scalar is
   data), and keeps a file's leading comments. Text that does not parse opens whole, so the apply
   reports the parser's own error. `OpenYamlFileTests.A_multi_document_file_opens_on_its_first_document_and_says_so`
   went red with `LoadFromFile` setting the whole file's text; `YamlFirstDocumentTests` pins the cut.
2. **The tab is titled with the file name** until the first apply names the object, and the
   InfoBar starts "Opened from settings.yaml." for a one-document file.
3. **It reads only the file picked, once, and keeps nothing**: not the path (`PickedYamlFile`
   carries the name and the text), not a recent-files list. `PRIVACY.md` has a sentence for it
   under "What the app reads on your computer", because that section would otherwise omit a read
   the app now makes; CLAUDE.md's privacy rule 2 does not require it (nothing new is stored or
   sent, and no URL is opened).
4. **A file past 4 MiB or not UTF-8 is refused in the status line** with nothing opened: no
   object the API server stores is larger than about 1.5 MiB, and bytes that are not UTF-8 would
   otherwise open as replacement characters an apply would send. A byte-order mark is dropped.
   Not on the demo cluster and not before a connect (`CanCreateFromYaml`, as for New YAML…).

The harness renders `cluster-tab-yaml-from-file` (a three-document file). The picker itself
cannot be driven headlessly; it is a check for the QA VM.

**Not built (left for the backlog):** a template library, and applying several documents in one
go (object by object, with per-object results).

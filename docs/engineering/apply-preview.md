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

Twelve things are load-bearing.

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
7. **The panel shares the dock with the editor, from code-behind, and the diff gets three
   quarters of it.** The preview's row is star-sized while a diff is open and `Auto` when
   it is not (or when the diff is empty, which is one sentence and two buttons). Both of
   the obvious XAML answers were tried and rendered wrong at the dock's default ~300px: an
   `Auto` row tall enough to read left a zero-height editor, and a `MinHeight` on the
   editor pushed the grid past the dock and overlapped its own rows. Mutating a
   `RowDefinition` is what `ClusterTabView.ApplyDockState` already does, for the same
   reason. The **3:1 weight is the line diff's own finding**: an even split left the
   panel's chrome row and footnote consuming its entire share, so the diff body rendered
   at *zero* height while the editor kept five lines nobody was reading — and the editor
   cannot be anything but context here, because typing in it discards the preview by rule
   6 below. Even at 3:1 the default dock shows about three lines and scrolls; the dock's
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
   on the code.
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

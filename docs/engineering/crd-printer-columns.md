# CRD printer columns

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


A CustomResourceDefinition declares the columns it wants a list of its objects to have
(`spec.versions[].additionalPrinterColumns`), and kubectl honours them: `kubectl get
certificates` prints READY / SECRET / AGE, not a generic status. This app printed the
same generic Status column for every one of the ~70 CRD kinds a real cluster carries,
which is the weakest surface in a client whose third hard rule is that CRDs are
first-class. `PrinterColumns.cs` + `SimpleJsonPath.cs` + `ClusterClient.PrinterColumns.cs`
(Core) read and evaluate them; `ResourceRowViewModel.PrinterCells` and
`ClusterTabView.ApplyPrinterColumns` render them.

**`ResourceStatusSummary` still owns every built-in kind, and the mechanism is the API,
not a list.** A CRD's own name is required to be exactly `<plural>.<group>` and its
group is required to be non-empty, so a kind names the object to fetch with no search —
and nothing in the core group can be a CRD at all. A built-in, an aggregated API
(`metrics.k8s.io` is not a CRD) and a user with no read access to `apiextensions.k8s.io`
all come back empty from the same GET, and an empty set is exactly today's list. So the
built-ins are not *excluded* from this; there is simply nothing to find for them.

Seven things are load-bearing:

1. **One GET per kind, lazily, cached per tab — not a list at connect.** A CRD object
   carries its whole OpenAPI schema; listing them on a cluster with cert-manager, Argo
   and Istio installed is tens of megabytes fetched to answer a question about kinds
   nobody may open. The negative answer is cached too, or reselecting a built-in kind
   would cost a 404 every time.
2. **Asking the API server for a Table was considered and rejected.** `Accept:
   application/json;as=Table` is what kubectl does and would give byte-identical columns
   for CRDs *and* built-ins — but a Table row is rendered strings with no object behind
   it, and this app's list is a **watch**, feeding the informer, the YAML editor, the row
   actions and the status pill from the object itself. It would also take the built-ins
   away from `ResourceStatusSummary`, which this change is not allowed to do.
3. **The JSONPath subset includes the condition filter, and that is the point.**
   `.status.conditions[?(@.type=="Ready")].status` is how cert-manager, Flux, KEDA *and*
   Argo all spell their Ready column, so a subset without it would blank the single
   most-wanted column on the most-installed CRDs. Supported: dotted fields, `['key']`
   (the only way to reach a key containing a dot), `[n]`, `[*]`, and `==`/`!=` filters.
   Anything else resolves to **no match**, which is the same outcome as an absent field:
   an empty cell, never an exception on a watch tick. Only the *first* match is used,
   matching the API server's own `tableconvertor` ("as we only support simple JSON path,
   we can assume to have only one result").
4. **Every declared column is drawn, `priority: 1` included.** kubectl shows `priority: 0`
   in the default table and the rest only under `-o wide`, and this app used to spell that
   `-o wide` as the Advanced view. That gate is gone with the rework that confined the
   Advanced view to the sidebar: a column the CRD's author declared is content, and a
   reader who cannot see it has no way to know one was withheld. UI rule 14's width
   problem is real — KEDA declares **eleven** columns for a ScaledObject — and its answer
   is now FEAT-66's draggable columns, which is a lever the reader holds rather than one
   they have to find. Ten slots is still the cap; the surplus is dropped in declaration
   order.
5. **A declared `Age` over `.metadata.creationTimestamp` is dropped**, because the
   list's own Age column *is* that column — recomputed live off the shared timer, with
   the exact timestamp as a tooltip. An `Age` pointing anywhere else is kept. Any other
   `type: date` column is re-rendered by that same timer (`PrinterColumns.DateValue`);
   without it a "Last run" or "Expires" cell would freeze until the next watch event.
6. **The generic Status and Details columns step aside when printer columns are
   present** — kubectl shows no generic status beside them, and doubling up costs width
   the list does not have. The 28px health dot stays: it is not one of kubectl's
   columns, and it is what still carries `ResourceStatusSummary`'s classification.
7. **In fleet mode the columns are the tab's own cluster's, and every row is evaluated
   against them.** A table can only have one set of headers, so they come from the
   cluster whose sidebar the kind was selected in; a member serving an older version
   with a different shape resolves to blank cells rather than to a wrong value — the
   same outcome an absent field already has.

Two implementation traps, both hit while building this:

- **The grid's printer columns are ten fixed slots declared in XAML, not columns built
  in code.** A `DataGridColumn` is outside the visual tree, so a code-built column needs
  a code-built binding — and a code-built binding is a *reflection* binding, which is
  exactly what NativeAOT will not ship. The cells are therefore
  `{Binding PrinterCells[3].Text}` compiled bindings against a fixed array of tiny
  observables (indexing an array is not itself observable, which is why each cell is an
  object rather than a string). Ten is above every real CRD surveyed; the surplus is
  dropped in declaration order.
- **A printer slot's header is a CRD author's string, and it collided.** Every
  `Apply*Columns` method used to find its columns by header text, and cert-manager calls
  one of its Certificate columns **Ready** — so the first cut renamed a slot to "Ready",
  `ApplySummaryColumns` matched it as the grid's own Ready column and hid it, and the
  CRD's most important column was silently missing from the very list this feature
  exists to fix. Only the screenshot showed it. Header matching was the wrong identifier
  and is **gone**: every column carries a `Tag` (`ResourceColumn`), the slots are
  addressed by the CRD column they currently draw, and `ClusterTabView.FixedColumns`
  still excludes them — see "The resource grid is the reader's to re-cut" below, which
  is also what made the header stop being a constant for the app's own columns.

The sandbox produces every one of these states — see `scripts/manifests/50-crds.yaml`
(the shop Widget's mixed types plus a priority-1 column and one path that resolves to
nothing, the demo Backup's condition filter and non-creationTimestamp date, and the
factory Widget deliberately declaring **none**, which is the degradation path).

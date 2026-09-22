# The status dot, and where it survives

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


A resource row carries a health classification (`ResourceStatusSummary`) and, in most
lists, a Status column that is *already* coloured from that same classification and
also spells the word — `Running`, `CrashLoopBackOff`, `deployed`, `failed`. The 28px
dot column beside it encoded the same fact a second time and bought nothing but width,
on a list whose Name column was ellipsising object names to the point where two pods of
one ReplicaSet rendered identically.

So the dot now appears in **exactly one** case: where a CRD's own printer columns have
replaced the generic Status column (see "CRD printer columns"), because there the dot is
the last thing carrying `ResourceStatusSummary`'s verdict at all. That is the whole rule,
and it lives in `ClusterTabView.ApplySummaryColumns` as
`"" => hasPrinterColumns && ResourceStatusSummary.ShowsStatus(descriptor)`.

Two things not to get wrong when touching this:

- **The Helm release list is a second `DataGrid` with its own hardcoded columns**, not
  driven by `ApplySummaryColumns`. It kept its dot when the resource list lost one, and
  the byte-diff is what caught it — a fix that introduces the very inconsistency it was
  removing. Any future column rule has to be applied to both grids or stated as applying
  to one.
- **A dot beside a *condition* is not this pattern and must not be folded into it.** On
  pod and node Overview the dot carries polarity (`IsProblem`) while the word carries raw
  status, and they disagree exactly when it matters: on `cluster-tab-node-detail-cordoned`
  `DiskPressure  True` renders red while `Ready  True` renders green. Removing the dot
  there would delete the classification; removing the word would hide what the API said.
  The two sites that *are* still this pattern — the exec pane's dot beside "Connected
  to…" and the switcher's dot beside its environment pill — are `FEAT-73`.

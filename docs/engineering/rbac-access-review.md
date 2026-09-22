# RBAC access review

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`ClusterClient.Rbac.cs` and `ClusterClient.WhoCan.cs` answer three different
questions three different ways, and the split matters:

- **"What may I do here?"** goes to the API server's own
  `SelfSubjectRulesReview`. Never re-implement RBAC evaluation locally — a local
  evaluator silently disagrees with the server as soon as webhook authorizers,
  aggregation or impersonation are in play. When the server reports
  `incomplete`, the UI says so; a permissions list quietly missing entries is
  worse than no list.
- **"Where does this subject's access come from?"** has no server endpoint, so
  it's assembled from (Cluster)RoleBindings whose subjects match, each binding's
  role resolved to its rules. That's provenance, not an authorization decision —
  and a binding whose role is gone is still listed, since a dangling binding is
  exactly what you open this view to find.

- **"Who can do X?"** (`ClusterClient.WhoCan.cs`) is the cluster-wide direction,
  and it is the one question Kubernetes serves *no* endpoint for:
  `SelfSubjectRulesReview` only answers for the caller, `SubjectAccessReview`
  only for a subject you already named — neither enumerates subjects. So
  `WhoCanAsync` scans the RBAC objects and matches their rules, `kubectl-who-can`
  style. Four things are deliberate:
  - **It is provenance, not an authorization decision**, and the UI says so
    in-panel. A local scan cannot see webhook/node authorizers or impersonation,
    so it can both miss access and list access another authorizer denies. The
    honest counterpart is per-subject: `CheckAccessAsync` posts a real
    `SubjectAccessReview`, and the Verify button on each row is what turns a
    scanned row into the server's own verdict (which is also why the row shows a
    "denied" that contradicts the scan rather than hiding it).
  - **Rule matching mirrors the API server's** (`pkg/registry/rbac/validation`):
    verb/group/resource each match exactly or via `*`, the resource compared as
    the combined `resource/subresource`. RBAC has **no partial wildcards** —
    `pods/*` is a literal that matches nothing, and a rule for `pods` does not
    cover `pods/log`. `WhoCanMatchingTests` pins every one of those, because a
    glob implementation here would invent access that doesn't exist.
  - **A cluster-scoped query never consults RoleBindings.** A RoleBinding
    confines even a ClusterRole to its namespace, where a cluster-scoped object
    does not exist; the namespaced/cluster-scoped flag comes from *discovery*
    (`AccessQuery.ClusterScopedResource`), never guessed.
  - **A rule narrowed by `resourceNames` is kept, with the names shown.** It
    genuinely grants the verb — on those objects — so dropping it hides real
    access, and showing it unqualified overstates it.
  Rules that could not be read (403 on Roles, say) become warnings on the result:
  `WhoCanResult.IsPartial`, surfaced inline. A short list that doesn't say it's
  short is the failure mode this whole surface exists to avoid.

Entry points are command-palette only (UI rule 1): "Access review — my
permissions" always, "Access review — who can do X?" (opens the same tab
straight onto its Who-can section via `RbacTabViewModel.WhoCanTabIndex`), plus a
subject review when the selected row is a ServiceAccount (the only RBAC subject
that exists as an object — Users and Groups are just strings inside a binding).
The pane's three sections are independent: a failed `SelfSubjectRulesReview`
renders its error *inside* "My permissions" rather than blanking the TabControl,
since the other two directions don't use that call at all.

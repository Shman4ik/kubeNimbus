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

## Who the server says you are (FEAT-56)

My permissions opens on a **"Signed in as"** line: the username and groups the API server
authenticates this connection as, from a `SelfSubjectReview` (`kubectl auth whoami`,
`ClusterClient.ReviewSelfSubjectAsync`). It is the answer to "I connected but everything is
403" — the server thinks I am `arn:aws:iam::…:role/dev`, in these groups — and no
client-side reasoning can produce it, because only the authenticator knows what an exec
plugin's token or an OIDC login turned into. Four things are deliberate:

- **It is asked on its own, beside the rules review**, not after it: a refused or failed
  `SelfSubjectRulesReview` still shows who you are, which is when it matters most.
- **Each way it cannot answer is a sentence, not an error.** Neither v1 nor v1beta1 served (a
  server older than 1.27) is "Not available on this server"; a refusal quotes the server's
  own message; no answer says why. `SelfSubjectReviewOutcome` carries which.
- **Groups are read here and nowhere else.** RBAC bindings name groups as well as users, so
  the groups are half of the answer. They are shown in the pane, held while it is open and
  never cached on the client or written anywhere; the Argo sync, which shares the request,
  still sends the name alone. The UID and the extras are never read: extras carry
  provider-specific identifiers (an access key id, a session name) nothing here needs. See
  the remarks on `ClusterClient.Identity.cs`, and `PRIVACY.md`'s "What your cluster sees".
- **It is asked afresh on every load**, never the sync's cached username, so Refresh after an
  `aws sso login` as a different role shows the new identity.

The demo cluster has no stand-in because it has no access review: the review is
palette-gated on `IsDemo: false` (demo rule 5), and a canned identity would be exactly the
invented fact demo rule 6 exists to prevent. `SelfSubjectReviewTests` (Core, over a scripted
API server) and `RbacWhoAmITests` (App) pin it; the harness renders
`cluster-tab-rbac-whoami` and `cluster-tab-rbac-whoami-not-served`.

## Entry points

Entry points are command-palette only (UI rule 1): "Access review — my
permissions" always, "Access review — who can do X?" (opens the same tab
straight onto its Who-can section via `RbacTabViewModel.WhoCanTabIndex`), plus a
subject review when the selected row is a ServiceAccount (the only RBAC subject
that exists as an object — Users and Groups are just strings inside a binding).
The pane's three sections are independent: a failed `SelfSubjectRulesReview`
renders its error *inside* "My permissions" rather than blanking the TabControl,
since the other two directions don't use that call at all.

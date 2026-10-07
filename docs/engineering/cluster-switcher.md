# The cluster switcher and environment colours

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


The top bar's context `ComboBox` is gone. It failed three ways at once, and the
replacement is shaped by what every comparable tool converged on:

- **It couldn't search.** kubectx ships an fzf integration, kubeswitch keeps a
  pre-computed search index explicitly "for operators of large scale Kubernetes
  installations", k9s has `:ctx` — all of them exist because scrolling a context
  list stops working around a dozen entries, and real estates run to hundreds
  (FreeLens has a bug report about its cluster list silently capping at **63**).
- **It truncated the distinguishing part.** Managed clusters hand out names like
  `arn:aws:eks:us-east-1:481516234298:cluster/search-staging`; at 150px every one
  of those reads the same.
- **It wasn't a switcher.** It only chose what the `+` button would open, so
  reaching an already-open cluster was a different gesture entirely.

`ClusterSwitcherViewModel` (Ctrl/Cmd+P, or the `+` after the last cluster tab) is one
ranked, fuzzy-searchable list over **both** open tabs and unopened contexts,
grouped Open / Pinned / Recent / All. Ranking is prefix > contiguous >
subsequence > cluster-name/kubeconfig-path, so `ppr` finds `payments-prod`.
Pins and recents persist in `WorkspaceSettings` (context **name** only — kubeconfig
merge semantics already make names unique, and the path would break the key when
a file moves). Notes for anyone changing it:

- **The results list is flat, deliberately.** Section titles ride on the first
  row of each group (`ClusterSwitcherItemViewModel.SectionHeader`). A nested
  ItemsControl-of-ListBoxes gives every section its own selection, and they clear
  each other's the moment they share a `SelectedItem`; flat also keeps arrow-key
  scroll-into-view working. The selection highlight therefore lives on an inner
  `Border.switcherRowBody`, not on the `ListBoxItem` — the container spans the
  group heading too, and highlighting it draws the selection around the title.
- **Never preselect the current tab.** The first Enter has to go somewhere.
- A context that is already open appears **only** under Open, never twice.
- **Row activation is handled on the ListBox, not in the item template.** See the
  hit-testing rule below — this one shipped broken once already.

**Environment colours** (`ClusterEnvironment` / `ClusterEnvironments.Classify`,
Core) are the other half. "One wrong kubectl command in the wrong context can
take down production" is the most-cited multi-cluster failure mode, and the
industry answer is uniformly colour — dev green, staging amber, prod red (kube-ps1,
kubectx wrappers, and a dedicated JetBrains "KubeContext Safety" plugin). Four
rules:

1. **The guess is biased toward production.** Over-flagging costs a red band on
   staging and one right-click to fix; under-flagging is the incident. So "prod"
   anywhere fires, and only the explicit non-production compounds (`preprod`,
   `non-prod`, …) are rescued — checked *first*, since each contains a production
   marker.
2. **Markers match whole tokens and adjacent token pairs, never substrings.**
   Substring matching reads "product-catalog" as production and "internal-tools"
   as an integration environment. The pair rule is what makes separator-spelled
   compounds work: `non-prod-eu` tokenizes to `["non","prod","eu"]`, so the rescue
   only fires if `non`+`prod` is tested as one candidate. `ClusterEnvironmentTests`
   pins every case — a change that makes a real production cluster read as
   anything else is a regression, not a tuning choice.
3. **The user always wins.** `WorkspaceSettings.EnvironmentOverrides` is applied
   in `MainWindowViewModel.EnvironmentFor`, which everything that colours a
   cluster goes through. It's reachable by right-clicking a cluster tab — a
   colour nobody can correct is a colour people learn to distrust.
4. **Production is not `ErrorBrush`.** An environment is not a failure; reusing
   the error colour would make every prod cluster look broken.

Where it shows: a left edge on each cluster tab, a
pill in the switcher, and a 2px band under the command bar **only** while the
selected cluster is production — the sole always-visible chrome the scheme adds
(UI rule 1), and it costs nothing the rest of the time because it isn't there.

**It is not only colour any more** (security block 3, B3-1). The environment used to drive
the band and nothing else, so with "Confirm before deleting" off the Delete key deleted at
once on a production cluster too. Now a production cluster (classified or assigned here)
always confirms a delete, every armed action strip names its cluster and says
"(production)" there, and that strip's border takes the production colour — see
[workload-actions](workload-actions.md), "Every confirm names its cluster". In an aggregated
fleet list each row is judged by its own cluster's environment, which travels on
`FleetMember.Environment`.

**The switcher button is a `+` after the last tab, not a chip before the first.** The chip
named the selected cluster, which its own tab — highlighted, two inches to the right — named
as well, so the bar printed the cluster in front twice and the chip read as one more tab.
The `+` sits where a browser keeps "new tab", opens the same popup, and says "Open a cluster"
in words when no tab is open, where a bare glyph would be a guess. "Which cluster am I in?" is
answered by the highlighted tab and, for production, by the band.

`WorkspaceStore.DirectoryOverride` exists for the screenshot harness: scenarios
construct real `MainWindowViewModel`s, which read the workspace on construction
and write it when a cluster is pinned, so without the redirect rendering fixtures
would clobber the developer's own tabs and pins.

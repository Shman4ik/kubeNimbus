# Sidebar labels come from the server's plural, and now actually do

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`SidebarKindViewModel.Pluralize` claimed to label rows from the server's own plural. It
did not: it used `descriptor.Plural` only to test equality with the Kind and otherwise
appended `"s"` (or `"es"` after s/x), so `NetworkPolicy` rendered as **`NetworkPolicys`**.
Every Kind ending consonant+y was affected, which on a CRD-heavy cluster is a lot of them.
It reads the plural now and re-cases it against the Kind's own capitalisation, so
`NetworkPolicy` + `networkpolicies` gives `NetworkPolicies`; a plural sharing no prefix
with the Kind falls back to the server's string as sent. A descriptor with no plural at
all (the hand-built statics, fixtures) keeps the Kind as written.

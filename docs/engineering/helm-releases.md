# Helm release browsing (read-only)

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`ClusterClient.Helm.cs` reads Helm 3 releases **straight off the cluster** — no
Helm binary, nothing shelled out. Helm stores each revision in a Secret of type
`helm.sh/release.v1`, whose `release` value is base64(gzip(JSON)) with
Kubernetes' own base64 on top: reading one means undoing two base64 layers and a
gzip (`TryReadReleaseRecord`). A record that doesn't unwrap is skipped, never
thrown — one broken release must not take out the list. The encoding is pinned
by `HelmReleaseTests` (no cluster needed), because getting a layer wrong fails
silently as "no releases".

**The unwrap is bounded.** gzip turns a run of zeros into almost nothing — 600 MiB fits in a
Secret under 1 MiB — and opening the Helm view decodes every release Secret in scope, so one
Secret written by anyone with write access to Secrets anywhere in that scope used to cost
600 MiB of memory per open. Now a `data.release` value longer than 4 MiB of text (more than
any Secret can hold) is not decoded at all, and decompression stops at 32 MiB
(`ClusterClient.Gunzip`, which reads in chunks into a buffer that stops growing at the cap).
A record past either cap, or nested past the cluster-JSON depth limit, **is listed, not
skipped**: its name and revision come from the labels Helm puts on every release Secret, its
status is `unreadable` (a warn pill) and its description says why; opening it states the same
sentence in the tab instead of "no longer stored". A record that simply does not unwrap is
still skipped without comment, because that is a foreign object wearing the type.
`HelmReleaseTests` builds a 200 MiB bomb and pins the cap, the allocation it costs and both
sentences.

In the App layer the Helm entry is a **synthetic sidebar kind**
(`SidebarGrouping.HelmReleaseDescriptor`, group `helm.sh` — no server serves
that, so it can't collide with a discovered kind). Selecting it stops the watch
and swaps the content area to the release list (`ClusterTabViewModel.IsHelmView`)
rather than starting a watch, since releases aren't an API kind. The section is
added at connect time **only when the cluster actually stores releases** (UI rule
1) — a release installed later in the session appears after a reconnect. Opening
a release docks a tab with its values, rendered manifest, notes and revision
history; double-clicking a history row loads that revision. Everything is
read-only: install/upgrade/rollback stays Helm's job.

## Sorting

The list sorts by a header click (2026-10): ascending, descending, then its default order, kept across reloads for the tab's life. How each column compares, and why a header double-click opens nothing, is in [resource-grid-resize-sort](resource-grid-resize-sort.md), "The inspector grids sort too".

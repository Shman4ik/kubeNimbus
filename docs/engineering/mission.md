# Mission and positioning

Moved verbatim from `CLAUDE.md` on 2026-10-10 (#259), so that an agent loads it only when it
works on this area. `CLAUDE.md`'s "Mission" section keeps the rules themselves, one line each; this page
keeps the full text, the incidents and the measurements behind them. In the text below, "this
file" means `CLAUDE.md`, and "above" or "below" points at `CLAUDE.md`'s sections, most of
which now have a page of their own in this directory.

## Mission

A fast, open-source (MIT) Kubernetes desktop client — the Kubernetes sibling of
[pgNimbus](https://github.com/Shman4ik/pgNimbus). An alternative to Lens.

The 2026 Kubernetes GUI market has one crowded end and one thin one. Lens is
subscription-gated for commercial use (Mirantis moved exec/logs/shell into
proprietary code in 6.3) and a heavy Electron app; OpenLens is dead; FreeLens
(the surviving fork) is still Electron, and so is Headlamp's desktop shell;
Aptakube is fast and polished but closed and paid; k9s is a keyboard TUI.
**The one true peer is [KubeUI](https://github.com/IvanJosipovic/KubeUI)** —
MIT, Avalonia 12, .NET 10, actively released and feature-comparable; the only
other native open-source client, [Seabird](https://github.com/getseabird/seabird)
(Go/GTK4), has had no commit since August 2025. So the claim is **not** "nobody
ships open source + native". KubeUI is **not** NativeAOT and cannot cheaply
become so — it ships ReadyToRun self-contained on the reflection-based
`KubernetesClient`, and generates CRD models with Roslyn at runtime — and that
is where kubeNimbus differs measurably: **~156 ms to first window against
~645 ms, a ~62 MB payload against a 382 MiB single file** (measured head to
head, linux-x64, `docs/research/2026-08-17-kubeui-positioning.md`), plus **no
telemetry** where KubeUI's is on by default. kubeNimbus is the narrower, faster,
quieter one: Aptakube's polish, NativeAOT startup, MIT, Kubernetes-first.

It opens a cluster on its **Applications** mode — every Argo CD Application and every
workload no Application tracks, with its health and a one-line reason read from status, one
Enter from a page with the facts, the pods and the right log line — because the moment
someone opens a Kubernetes GUI in a hurry is "service X is broken". The explorer is the
**Resources** mode beside it, one click away. Deterministic rules only: the page is the same
every time, and anything that needs thinking is left to the tools built for that. See
[applications-mode](applications-mode.md).

Where KubeUI is ahead and we are not: signed and notarized binaries, auto-update,
winget/Store/Homebrew distribution, and schema-aware YAML completion. Node drain,
server-side dry-run and *installers* were on that list and are not any more — see
"Node operations", "The apply preview" and "Installers" below; what is left of the
installer gap is auto-update and a certificate, not the packages themselves. None of the rest is a
reason to change course; all of it is a reason not to write a comparison table yet.

**Headline benchmark:** ~150 ms to first frame (vs Electron's seconds) —
`--smoke-test`, which waits for a real compositor tick, reported **103–108 ms**
on a published linux-x64 binary. That is a *different event* from the ~156 ms
above and not a contradiction of it: the head-to-head figure comes from a
cross-app harness that polls for a **mapped window**, the only thing both apps
could be measured on identically, and it therefore reads high for kubeNimbus —
the comparison is deliberately the less flattering of the two. Both numbers are
recorded in `docs/research/2026-08-17-kubeui-positioning.md`. NativeAOT publish
is the *shipping* configuration, not an afterthought — every dependency choice
must be AOT/trimming-compatible from day one.

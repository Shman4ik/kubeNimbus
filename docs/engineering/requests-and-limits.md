# Requests and limits are text on the Usage tab

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`ContainerViewModel.CpuResourceText` / `MemoryResourceText` print what each container asks
for and what it is capped at, under the measured line and above the sparkline, on pod
detail's Usage tab. The numbers themselves are not new: `ReadContainerSpecs` has parsed
`spec.containers[].resources` into the container row since the metrics pass, and the only
place they were rendered was a hover tooltip — which is exactly what lens#4154 has been
asking for since 2021 and what FreeLens shipped a fix for after Lens left it broken. So
this is a rendering change on numbers the app already had.

Six things are load-bearing.

1. **A missing request and a missing limit are said in words, not left blank.** They are
   independently optional in Kubernetes and a container declaring neither is the
   commonest shape there is, so each half prints "no request" / "no limit" and the empty
   pair collapses to one sentence, `no request or limit set`. A blank cell there reads as
   a number this pane failed to fetch, and a zero reads as a request of nothing, which is
   a different and much worse claim (UI rule 9). `ContainerResourceTextTests` pins all
   four combinations, and the blank-instead-of-words break was written and confirmed red
   before they were called done.
2. **The percentage is offered only where there is a limit and a reading.** `Quantity
   .Percent` already returns null for a zero or absent denominator; what is added here is
   that anything above zero and under one percent prints `<1%` rather than rounding to
   `0% of limit`, which reads as "measured nothing" for the ordinary case of a container
   idling under a generous cap.
3. **The declared line does not follow the metrics gate, and that is the whole point.**
   Requests and limits come from the pod spec, so they are readable on a cluster that
   serves no `metrics.k8s.io` at all — the case where they are worth *most*, because
   nothing else on the tab has anything to say. The two "no charts" states are therefore
   `Border.infoBar` notices stacked above the content (UI rule 11) rather than full-tab
   panels shown *instead* of it, which is what they were: the old markup would have hidden
   the numbers behind exactly the condition that makes them the only thing left. Gating
   them on a measurement was the second break written and confirmed red.
4. **The tab is no longer gated on anything but metrics.** It used to ride the Advanced
   view as well, on the reasoning that the switch's job was "hide what you did not come
   here for" — an argument that stopped holding when the switch became the sidebar's
   alone. Requests and limits are not metrics data and were never the metrics gate's to
   withhold; both gates are gone and the tab is always in the strip. `SelectedDetailTabIndex`'s
   values (Logs=0, Env=1, Events=2, Usage=3, Overview=4) are still load-bearing, so the
   tab keeps its index whatever else changes.
5. **`IsCollectingUsage` exists because the two notices are now stacked, not exclusive.**
   Each has to know the other is not showing. And a cluster with no metrics API clears
   `UsageWindowCaption`: the strip's "collecting…" beside a notice saying nothing is ever
   going to be collected is a contradiction, and it is cleared from the generated
   `OnIsMetricsUnavailableChanged` partial rather than from a command (UI rule 8b).
6. **The chip's tooltip stays; the Usage card's copy of it goes.** Hovering a container
   chip still summarises usage against requests and limits from any tab, which costs
   nothing and is the quick-peek. The identical tooltip on the Usage card's *title* is
   gone — the card body now states those numbers as text two lines below, and a tooltip
   repeating the text under it is noise.

**The demo dataset carries both sides** (demo rule 4): the report-generator pod's `app`
container declares a CPU and memory limit, so the percentage renders, and its
`envoy-sidecar` declares requests only, which is the commonest real shape. The
unschedulable `fraud-detector` was already declaring no resources at all and is the third
state, rendered by `cluster-tab-pod-detail-usage-unset`. The sandbox needed no change:
`shop-web`'s two containers are already the request+limit / request-only pair, and
`40-broken.yaml`'s `bad-image` and `unschedulable` pods declare neither.

The three usage scenarios render **maximized** now. The per-container section sits below
two full-width pod-total charts, so at the dock's default ~300px it was off screen — which
is a small demonstration of the item's own complaint: numbers that exist and cannot be
seen.

# Visual audit: cognitive load across every screen

2026-08-19. A review pass, not a refactor. Every finding below was read off a
rendered PNG — 69 scenarios × 2 themes, produced by `tools/Screenshot` at the
commit named at the end — and each one names the file and the crop it came from
so it can be re-checked rather than re-argued. Where a number is quoted (a pixel
colour, a column edge) it was measured with ImageMagick, not estimated.

The dark theme is the one that matters here and is where most of this was found.
This repo has already shipped the same log-severity defect twice for exactly that
reason — an unclassified line rendered near-black on a near-black card, which a
light-theme screenshot of a correct pane and a broken one render identically — and
the pattern repeats below: the single worst finding in this document is legible in
light and effectively invisible in dark.

Two flappers are excluded from every finding: `cluster-tab-workload-logs.*` and
`cluster-tab-demo-pod-detail.*` replay canned streams on a timer and differ
between two renders of the same tree.

## The three questions already answered by the maintainer

Asked before the pass, answered during it, and recorded here because they change
what counts as a finding:

1. **The Namespace column stays.** The redundancy it currently shows is to be
   fixed from the other side — the namespace picker becomes a multi-select with
   checkboxes plus a "show all" button — at which point the column starts
   carrying information instead of restating the picker.
2. **The health dot goes wherever a Status column is present.**
3. **The duplicated demo sentence is to be fixed.**

Findings that these three already settle are marked *(decided)* and are not
re-argued.

---

## Cross-cutting patterns

These matter more than any single screen: each appears on three or more, so each
is one fix in `Styles/Theme.axaml` or a view base rather than N fixes in N views.

### P1 — the unfilled meter track is at 1.04:1 contrast, and the card's whole job depends on it

**Verdict: colour. Cost: one line. This is the most consequential finding in the
document.**

`NodeDetailView.axaml:94` sets `TrackBrush="{DynamicResource HoverBackgroundBrush}"`,
and that token is `#80808080` at `Opacity="0.1"` — 50 % alpha grey at 10 % opacity,
i.e. an effective 5 % wash. It is a *hover tint*, and it is being used as the
structural axis of a chart.

Measured on `cluster-tab-node-detail.dark.png`:

| Sample | Colour | |
|---|---|---|
| empty track, `p{700,767}` | `srgb(14,14,14)` | |
| card background, `p{700,745}` | `srgb(8,8,8)` | contrast **1.035:1** |

and on the light render, `cluster-tab-node-detail.light.png`: track `srgb(243,243,243)`
on card `srgb(249,249,249)` — **1.054:1**. WCAG's floor for a non-text graphical
object is 3:1. Both themes are an order of magnitude under it; dark is where it
actually disappears.

What that costs is exactly what the card was rebuilt to deliver. `CLAUDE.md`'s
node section records the fix: *"three bars of different lengths cannot be compared
row to row, which is the entire job of this card"*, and the row was re-cut to fixed
number columns so every track would share a start and an end. The layout does share
them — and the shared end is invisible, so the comparison is defeated anyway, one
layer down. Crop `500x60+340+755` of `cluster-tab-node-detail.dark.png` shows three
blue bars floating with no reference extent; the same crop of the `.light.png`
shows the grey track and reads correctly.

It gets worse on an overcommitted node. On `cluster-tab-node-detail-cordoned.dark.png`
(demo-worker-2, 109 % limits), crop `500x60+340+255`: the *limits extent* paints
nearly the full track in a mid-blue, so CPU and Memory appear to have long bars —
while the Pods row, which correctly has no limit, has only the invisible track and
therefore reads as a **shorter bar**, not as an equal bar without a marker. The one
row whose shape is documented as deliberately different (`NodeResourceLineTests`
pins it) is the one the colour makes look like missing data.

**The token must not be changed.** `HoverBackgroundBrush` is shared with pgNimbus
and drives every hover state in both apps. The meter needs a track brush of its own.

### P2 — the status bar restates what the content area already says

**Verdict: duplication. Cost: low.** *(decided — item 3)*

Two instances, both with the two copies simultaneously on screen:

- Every demo screen (15+ scenarios). `cluster-tab-node-detail.dark.png`, crop
  `930x30+330+68` and crop `700x22+0+978`: the `demoBar` and the status bar carry
  the identical 100-character sentence, *"Demo cluster — sample data that ships
  with kubeNimbus. Nothing is connected and none of these objects exist."*
- `main-window-no-kubeconfig.dark.png`: *"No kubeconfig contexts found."* as the
  card heading at y≈271 and again in the status bar at y≈787.

`cluster-tab-node-drain-running.dark.png` is the worst case — **three** demo
notices at once: the `demoBar` at y≈82, the strip's own demo `infoBar` at y≈165
(*"…the demo cluster has none"*), and the status bar at y≈987.

The `demoBar` is not the copy to remove: demo rule 6 makes it a deliberate
exception to UI rule 1, and its justification (*"someone believing a screen full
of invented pods is their own workloads"*) is about persistent, unmissable
placement. The status bar is the redundant one.

### P3 — column headers clip at the right edge at 1280 px

**Verdict: alignment/density. Cost: medium; needs a width re-cut, and on the
fleet list a real decision.**

UI rule 14 records this failing once already: *"the first cut pushed Age off the
right edge at 1280px"*. It is back, in three places:

- `cluster-tab-advanced-view.dark.png`, crop `220x290+1040+115`: the **Age header
  renders as "Ag"** with the `e` sliced by the panel edge. The values fit; only
  the header is cut. Nine columns is what the advanced view adds and what rule 14's
  re-cut was measured against — it was measured against the default view.
- `cluster-tab-fleet-list.dark.png`: **four** clipped headers — `Namespace`→"Names",
  `Ready`→"Re", `Restarts`→"Restai", `Age`→"Ag". `CLAUDE.md` already concedes this
  one (*"ten columns do not fit in ~910px and horizontal scroll is the answer"*),
  so the finding is that the conceded answer has not been built, and meanwhile the
  Namespace column is spending 55 px on a truncated header above 24 identical
  truncated values.
- `cluster-tab-helm-release-detail.dark.png`: the `Updated` column reads
  `07/20/2026 08:41:02 +00:` — the offset is cut. A full absolute timestamp is the
  widest thing that column could hold; `RelativeTime` already exists in Core and is
  what the resource list's Age uses.

### P4 — a bordered content control inside an already-bordered dock

**Verdict: noise (decorative frame). Cost: low, one style.**

The inspector dock draws a card; several panes then draw a second border around
their content inside it, so the content sits in a box within a box. Instances:
the log pane (`cluster-tab-pod-detail.dark.png`, y≈760–930), the exec terminal
(`cluster-tab-exec.dark.png`, y≈525–735), Helm values
(`cluster-tab-helm-release-detail.dark.png`, y≈525–715), the YAML editor and the
diff panel (`cluster-tab-yaml-diff-split.dark.png` — three nested boxes: dock,
editor, diff), and the preferences kubeconfig list (below, P5's instance).

Neither border groups anything the other does not. The inner one is the removable
half: the dock's card already says "this is the panel".

### P5 — a card border and a section header both doing "this is a group"

**Verdict: noise. Cost: low, but it is a design decision about the whole app.**

`cluster-tab-pod-detail-overview.dark.png` stacks four bordered cards, each with
its own uppercase header inside it: SCHEDULING, CONDITIONS, TOLERATIONS, PROBES.
`cluster-tab-node-detail-cordoned.dark.png` does the same with four more
(ALLOCATABLE VS REQUESTED, CONDITIONS, TAINTS, KUBELET). `main-window-preferences.dark.png`
does it with APPEARANCE/CLUSTERS plus a card per setting.

An uppercase small-caps header on a dim label already separates sections
unambiguously — it is what the sidebar uses, with no borders, over 100+ rows. The
border adds 1 px of outline plus ~10 px of padding per card, four times, in a dock
whose default is ~300 px.

Worth noting the counter-example in the same codebase: `main-window-shortcuts.dark.png`
draws **one** border per section around several rows, which is the border doing real
work. That is the shape to converge on.

### P6 — a coloured dot beside text that already says the same thing

**Verdict: duplication. Cost: low.** *(partly decided — item 2)*

The maintainer's decision covers the list case (dot + Status pill, on every list
screen: workloads, advanced, fleet, node, Helm, CRD). Three further instances are
*not* covered by it and each needs its own call:

- `cluster-tab-exec.dark.png` y≈505: a green dot beside *"Connected to app (/bin/sh)"*.
  This is precisely the shape UI rule 11 removed from the port-forward pane —
  *"a bare `Ellipse.statusDot` next to a sentence, which carries the information
  only for someone who already knows the colour code"* — surviving in the exec pane.
- `main-window-switcher.dark.png`: an environment dot at x≈344 and a PROD/STAGING
  pill at x≈890 on the same row, same information, 550 px apart.
- Pod/node **conditions** — see the "stays" section; these are *not* redundant and
  the screenshot proves it.

### P7 — the sidebar icon is a section marker rendered once per row

**Verdict: noise. Cost: low, but it removes a visual and so needs a call.**

`cluster-tab-workloads-list.dark.png`, crop `300x680+10+95`: Workloads is eight
identical cubes, Network six identical links, Storage five identical stacks. The
glyph is constant within a section, and the section header sits directly above it
and is always visible. It carries no per-row information and costs ~24 px of a
sidebar whose long kind names (`HorizontalPodAutoscalers`) are what run out of room.

The nuance that makes this a question rather than an obvious cut:
`cluster-tab-sidebar-recent.dark.png`, crop `300x340+10+95` — in the **Recent**
section the icons genuinely vary (cube, stack, link, sliders), because that section
mixes groups. So the icon is informative in exactly one of the six sections. Any
change has to keep it there.

---

## Per-screen findings

### The resource list — `cluster-tab-workloads-list`, `-metrics`, `-advanced-view`

The one question: *which of these objects is unhealthy, and which one is the one I
mean?* The list answers the first well and the second badly.

- **The Name column truncates while a constant-value column keeps its width.**
  `cluster-tab-workloads-list.dark.png`, crop `930x290+330+60`: the picker says
  `payments` and all eight Namespace cells say `payments`; meanwhile three pods
  render as `payment-service-report-generator-7f9c8d6b…`, `…-7f9c8d6b…` and
  `…-8c1a4f2e…` — **two of the three are indistinguishable**, because the ellipsis
  falls exactly on the discriminating suffix. On `cluster-tab-advanced-view.dark.png`
  it collapses to `payment-servi…` for all three and six of eight rows are truncated.
  *Verdict: duplication → alignment. Cost: low once the picker decision (item 1)
  lands; the column widths still need a re-cut so Name gets the slack.*
- **The list's kind label restates the sidebar selection.** The `Pods` label at
  x≈340 sits 30 px right of the sidebar row `Pods` rendered in the selected state.
  *Verdict: duplication. Cost: low. Weak finding — it is also the only thing naming
  the list when the sidebar is hidden (`IsSidebarVisible` false), so it earns its
  place in that state.*
- Age header clipped in advanced view — P3.
- Dot + Status pill — P6, *(decided)*.

### The fleet list — `cluster-tab-fleet-list`, `-partial`

- Four clipped headers — P3.
- **The cluster name is the boldest, most repeated text on screen.**
  `cluster-tab-fleet-list.dark.png`: 24 rows, three distinct clusters, each name
  repeated eight times in semibold. The single most visually prominent element
  carries about 1.5 bits. Candidate: print on change only, or a group header row.
  *Verdict: density/hierarchy. Cost: medium — it changes how the grid groups.*
- `3 of 3 clusters serve Pod` — stays, see below.

### The sidebar

- Uniform icons — P7.
- **`NetworkPolicys`.** Visible in every list screenshot's sidebar. This is a real
  code defect, not fixture data: `SidebarViewModels.cs:158` `Pluralize` reads the
  server's plural only to test equality with the Kind, then falls back to naive
  English (`kind + "s"`, or `+ "es"` after `s`/`x`). `NetworkPolicy` → `NetworkPolicys`;
  the demo catalog supplies the correct `networkpolicies` at `DemoData.cs:324` and it
  is ignored. Every Kind ending consonant+`y` is affected, which on a CRD-heavy
  cluster is a lot of them. It also contradicts `CLAUDE.md`, which claims labels come
  *"from the server's own plural"*. *Verdict: correctness. Cost: trivial (use
  `descriptor.Plural`, title-cased).*
- **Two rows can render in the selected state at once.**
  `cluster-tab-row-action-scale.dark.png`, crop `300x120+10+178`: `Deployments`
  **and** `Pods` both carry the blue selected background and blue icon, while the
  list shows Deployments. The mirror-image symptom is on
  `cluster-tab-sidebar-recent.dark.png`, crop `300x340+10+95`: `Pods` appears in
  both Recent and Workloads and only the Workloads copy lights up, so the Recent
  entry for the kind you are looking at reads as inactive.
  Both are one cause — the sidebar is a `ListBox` per section, each with its own
  independent selection. This is the exact failure `CLAUDE.md` documents for the
  cluster switcher and solved there by going flat: *"A nested ItemsControl-of-ListBoxes
  gives every section its own selection, and they clear each other's the moment they
  share a `SelectedItem`"*. *Verdict: correctness/hierarchy. Cost: medium.*

### Pod detail — `cluster-tab-pod-detail`, `-overview`, `-usage`, `-environment`, `-events`

- **123 px of chrome before the first log line.** `cluster-tab-pod-detail.dark.png`:
  dock top y≈648, dock tab row y≈662, container/owner row y≈702, tab-strip row
  y≈733, first log line y≈771. UI rule 10 counts two rows and this is compliant —
  the dock tab is the dock's, not the panel's — but at the default dock that is
  ~40 % of the pane spent before content, and it is the number to hold any new row
  against.
- **Two different value-column positions between two cards on one screen.**
  `cluster-tab-pod-detail-overview.dark.png`: SCHEDULING puts its values at x≈488,
  PROBES puts its at x≈438. Neither is wrong alone; together the eye has to re-find
  the column halfway down. *Verdict: alignment. Cost: low.*
- **Four stacked cards, four internal layouts.** Same screen: node selector renders
  as chips, tolerations as plain text lines, QoS/priority as label-value, probes as
  label + two-line value. Two of those are key/value lists rendered two ways.
  *Verdict: hierarchy/consistency. Cost: medium.*
- ~~The section header is inside the card on Overview and outside it on Usage.~~
  **Withdrawn.** `BY CONTAINER` heads a *repeating list of one card per container*, so
  it cannot sit inside a card; `SCHEDULING` heads a single card's contents. The two
  headers do different jobs and their placement follows from that.
- **The container chip and the usage card print the same numbers.** Usage tab: the
  chip reads `app 11m · 64 MiB`; the card 340 px below reads `CPU 11m · peak 12m`
  and `MEM 64 MiB · peak 69.1 MiB`. *Verdict: duplication. Cost: low. Weak — the
  chip is a picker and is present on every tab, so it is the card that is redundant
  only on this one tab.*
- **Charts are fixed height with ~190 px unused below them** at maximized
  (`cluster-tab-pod-detail-usage.dark.png`, y≈745–930) and no axis of any kind, so a
  bump cannot be located in time. Adding axes is a feature, not decluttering — noted,
  not proposed.

### Node detail — `cluster-tab-node-detail`, `-cordoned`, `-pods`

- The meter track — P1. This is the screen it costs the most on.
- **The CONDITIONS card is indented 16 px from its siblings.**
  `cluster-tab-node-detail-cordoned.dark.png`: ALLOCATABLE, TAINTS and KUBELET start
  their content at x≈359; the condition rows start at x≈375 because the status dot
  occupies the first 16 px. Four stacked cards, one of them out of line.
  *Verdict: alignment. Cost: trivial.*
- **A three-line footnote in a ~300 px dock.** Same screen, y≈335–365. `CLAUDE.md`
  describes it as *"one sentence"*; it renders as three lines and ~55 px, roughly a
  fifth of the default dock. It is genuinely load-bearing (it is the only thing
  saying which part of the track is which) — and P1 is why: if the track were
  visible, the footnote would have less to explain. *Verdict: density. Cost: low,
  but do it after P1, not before.*
- **Content clips mid-row at the default dock height.** `cluster-tab-node-detail.dark.png`
  y≈928: `MemoryPressure` is cut horizontally through the glyph by the dock edge,
  with no fade or affordance saying more is below. *Verdict: density/rule 9. Cost: low.*
- **The dock chrome restates the list row.** `Ready` pill and `<none> · v1.31.2` in
  the chrome row at y≈702 repeat the Status and Details cells of the selected row
  five lines above. *Verdict: duplication. Cost: low. Weak — the row scrolls away
  and the panel does not.*

### The drain strip — `cluster-tab-node-drain-*`

- Three simultaneous demo notices — P2.
- **~290 px of strip before the list.** `cluster-tab-node-drain-running.dark.png`:
  the node list is pushed to y≈425. Every row of it is load-bearing (confirm
  sentence, refusals, options, progress) and none of it is noise — but `Stop draining`
  occupies a 40 px row alone, right-aligned, and could share the checkbox row.
  *Verdict: density. Cost: trivial.*

### The YAML editor and apply preview — `cluster-tab-yaml-*`

- **The panel repeats the dock tab's title.**
  `cluster-tab-yaml-diff-split.dark.png`: the dock tab reads
  `Deployment/checkout-worker` at y≈462 and the panel header reads
  `Deployment/checkout-worker` again at y≈506; confirmed in light too
  (`cluster-tab-yaml-diff-preview.light.png`, y≈132 vs y≈176).
  **Correction, made while implementing this:** the first draft of this entry claimed
  the row costs ~34 px and about a quarter of the pane's content, citing UI rule 10's
  *"a row that repeats it is a row spent on nothing"*. That is wrong. Unlike
  `HelmReleaseView` and `RbacView`, whose title rows held nothing else and were deleted
  outright, this row also carries Reload, Apply and Delete — so it stays either way and
  the saving is **horizontal, not vertical**. The finding survives as ordinary
  duplication (the title takes the row's flexible column, squeezing the status texts
  that share it on a long object name); the rule-violation framing does not.
  *Verdict: duplication. Cost: trivial.*
- **Two buttons labelled Apply, 100 px apart, doing different things.** Same screen:
  `Apply` (send the document) at x≈1127 y≈505, `Apply changes` (confirm the previewed
  apply) at x≈1084 y≈606. *Verdict: hierarchy. Cost: low — needs a wording call,
  see the ranked list.*
- **`+5 −3` and "The server would make 5 changes:" say the same thing** side by side
  at y≈606. *Verdict: duplication. Cost: trivial. Weak — the counts split the total
  into adds and removes, which the sentence does not.*
- **The diff body clips mid-line** under the footnote (`cluster-tab-yaml-diff-preview.light.png`
  y≈680, a half-drawn tinted row), and in split view at the default dock the visible
  portion of a five-change diff shows **zero changes** — `4 unchanged lines`, then two
  identical rows. *Verdict: density. Cost: medium — it is really the 3:1 row weight
  being still too generous to the editor.*

### Access review — `cluster-tab-rbac-who-can`

- **`namespace payments` restates the dock tab `Access/payments`**, 40 px apart.
  *Verdict: duplication. Cost: trivial.*
- **`Verify` reads as a label.** y≈288/379/470, x≈1205: accent-coloured text with no
  border and no background. It already carries `Classes="chip"` — the cause is that
  `Button.chip` *rests* transparent, not that the class is missing — so the fix is the
  one the codebase already uses for pod detail's owner chip, which was given an explicit
  border for exactly this reason (UI rule 8). *Verdict: hierarchy. Cost: trivial.*
- The scan disclaimer stays — see below.

### Preferences — `main-window-preferences`

- **An empty bordered box with its explanation outside it.** y≈560–645: the
  kubeconfig list is an ~85 px empty rectangle, and *"No files added. kubeNimbus is
  still reading $KUBECONFIG and the default location."* sits **below** it at y≈661.
  This is UI rule 9's *"blank rectangle that looks like a bug"* almost literally —
  the app has the right sentence and puts it outside the box it explains.
  *Verdict: rule 9 / noise. Cost: trivial — move the text into the box's empty state.*
- **The Advanced-view card explains the feature in five lines**, enumerating all
  nine things the switch hides. It is the longest prose block in the app's settings.
  *Verdict: density. Cost: low. Question, not a proposal — a preference that needs a
  paragraph may be a preference that needs a better name.*

### Port forward — `cluster-tab-port-forward-idle`

Cleanest form in the app; rule 11 is visibly applied. One finding:

- **The pod port is two controls showing the same value**: a text box reading `8080`
  and a dropdown reading `8080 · http`, adjacent. *Verdict: duplication. Cost: low.
  Weak — one is free entry and one is the declared-port picker, so this is a
  "should the picker fill the box and hide itself" question, not obvious noise.*

### The cluster switcher — `main-window-switcher`

- Dot + environment pill — P6.
- **A pin icon on every row, always.** Seven always-visible controls for a rare
  action, against rule 1. *Verdict: noise. Cost: low. Question — hover-only is the
  obvious answer and costs discoverability, which is exactly the trade rule 1 exists
  to make deliberately.*
- **`config` as the last subtitle token on six of seven rows** — the kubeconfig
  filename, constant for everything in the default file. *Verdict: duplication.
  Cost: trivial. Weak — it is the thing that disambiguates when it is *not* constant.*

### Empty and loading states — `cluster-tab-empty-namespace`, `-loading`, `-disconnected`

Well done and mostly untouchable (below). One finding:

- **The column header row is drawn over an empty grid.**
  `cluster-tab-empty-namespace.dark.png` y≈128: Namespace/Name/Ready/Status/Restarts/Age
  with nothing beneath them, and the real empty state floating at y≈435.
  *Verdict: noise. Cost: low. Weak — the headers tell you what the kind would show,
  and hiding them makes the empty state jump when the first row arrives.*

### The cheat sheet — `main-window-shortcuts`

Clean. **One reported nit was withdrawn**: the "Default action" row was read off a
downscaled image as using ASCII `->` where the access review uses `→`. Cropped at 3×
it is `→` in both, and `CommandCatalog.cs:194` confirms it. No finding.

---

## Looks like noise, and stays

Each of these reads as clutter right up to the moment it stops someone drawing a
wrong conclusion. Reason given per item, with the rule that owns it.

- **The `demoBar`** (`Border.demoBar`, every demo screen). Demo rule 6 makes it an
  explicit exception to UI rule 1, and the justification is the alternative:
  *"someone believing a screen full of invented pods is their own workloads. A
  notice that appears once and dismisses does not prevent that."* P2 removes the
  **status bar's** copy, never this one.
- **The production environment band** under the command bar. The environment
  section's whole argument is that under-flagging is the incident and over-flagging
  costs a coloured band. It is also already the minimum: it exists only while the
  selected cluster is production.
- **Partial-result notices.** `3 of 3 clusters serve Pod` (fleet), `1 of 3` /
  `RowFilterSummary` (list filter), `1 server bookkeeping field hidden
  (managedFields, resourceVersion, generation)` (diff), `5 pods will be evicted ·
  2 left in place` (drain), the `CapNotice` on multi-pod logs. Every one names
  something the panel is *not* showing. A panel that is quietly incomplete is the
  failure the whole apply-preview feature exists to prevent one level up.
- **The access review's scan disclaimer** (two lines, `cluster-tab-rbac-who-can`).
  It is the difference between provenance and an authorization decision. A local
  scan cannot see webhook or node authorizers; a short list that does not say it is
  short is the exact failure that surface exists to avoid.
- **Explicit empty / loading / disconnected / filter-matched-nothing states.**
  `No Pods found` + `in kube-system`, the loading state, the disconnected banner,
  `IsFilterEmpty` naming the query and the row count. Rule 9, and *"this namespace
  has no pods" and "no pod here is called that" send you looking for opposite
  problems*.
- **`Not forwarding. Pick a port and press Start.`** The idle state of a pane whose
  blank alternative is indistinguishable from a broken one.
- **`INFO` / `WARN` / `ERROR` spelled *and* coloured** in the log panes. This is
  correct double-encoding, not duplication: the word is what survives for a reader
  who cannot separate the hues, and the repo has already shipped severity colour
  bugs twice.
- **The condition dot beside `True`/`False`** on pod and node Overview. This looks
  like P6 and is not, and `cluster-tab-node-detail-cordoned.dark.png` is the proof:
  `DiskPressure  True` renders with a **red** dot while `Ready  True` renders green.
  The dot carries polarity (`IsProblem`), the word carries raw status, and they
  disagree exactly when it matters. Removing the dot would delete the classification;
  removing the word would hide what the API actually said.
- **cert-manager's `Secret` printer column duplicating `Name`**
  (`cluster-tab-crd-printer-columns-wide`: `checkout-tls` / `checkout-tls`). Those
  are the CRD author's own `additionalPrinterColumns` and `kubectl get certificates`
  prints the same pair. Rendering them faithfully is the feature.
- **The exec terminal's own near-black palette** against the card's `#080808`.
  Deliberately theme-independent — the control caches a resolved brush per
  `FormattedText` and only clears on a font change, so a live theme swap would
  repaint cell backgrounds and leave glyphs the old colour.
- **The mutating strip naming its target in full** (`Scale Deployment/checkout-worker
  in payments`) even though the selected row says the same. It is the confirm
  sentence of a destructive action; ambiguity there is the one thing rule 17 exists
  to prevent.

---

## States nothing renders

Per the brief: a state produced by neither the demo dataset nor the fixtures has
never been looked at by anyone, and that is a finding in itself.

- **A pod condition of negative polarity.** `PodConditionPolarity` classifies
  `DisruptionTarget` as the one condition Kubernetes defines the other way, and the
  `Unclassified` (grey) answer exists for custom readiness gates and `PodResizePending`.
  No fixture and no demo object carries either, so the grey dot and the
  inverted-polarity dot on the **pod** Overview have never been rendered.
  (The **node** equivalent is covered — `DiskPressure True` renders red on
  `cluster-tab-node-detail-cordoned`.) `CLAUDE.md` already notes the gap for
  `DisruptionTarget`; the `Unclassified` case is not noted anywhere.
- **A CRD list wide enough to need horizontal scroll.** The printer-column pass
  records KEDA declaring eleven columns for a `ScaledObject`; the demo CRD declares
  four. Given P3 — four headers already clip at ten columns — the eleven-column case
  is unrendered and is where it fails hardest.
- **A sidebar section filtered to nothing.** The filter's empty state for the
  *sidebar* (as opposed to the list's `IsFilterEmpty`, which is covered by
  `cluster-tab-list-filtered-empty`) has no scenario.

Adding fixtures for these is cheap and is proposed in the ranked list rather than
reasoned about here.

---

## What shipped, and what did not

Group A was implemented in the same pass. **Four of its twelve rows did not survive
contact with the code**, and that is worth more than the eight that did — every one
of them was a finding read off a rendered image that turned out to be wrong about
its own cause. They are recorded here rather than quietly dropped.

### Shipped

| # | Fix | Effect, measured |
|---|---|---|
| A1 | `ResourceMeter` gets its own theme-split `MeterTrackBrush` instead of the 5 %-wash `HoverBackgroundBrush` | Dark track **srgb(14,14,14) → srgb(61,61,61)** on an srgb(8,8,8) card: **1.035:1 → 1.84:1**. Light **srgb(243) → srgb(210)** on srgb(249): **1.054:1 → 1.44:1**. The three tracks now visibly share a start *and* an end, so the row-to-row comparison the card exists for works; the Pods row reads as a small amount of a full track rather than as a short bar |
| A13 | The health dot shows only where a CRD's printer columns have replaced the Status column | 28 px returned to the Name column on every list. `payment-service-report-generator-8c1a4f2e…` now renders `…-8c1a4f2e91-…`, i.e. far enough to show the ReplicaSet hash. The two pods that share a ReplicaSet are still identical on screen — only FEAT-66 fixes that |
| A13b | The same dot removed from the Helm release grid | Caught by the byte-diff, not by the audit: that grid is a second `DataGrid` with its own hardcoded columns, so it kept the dot the resource list had just lost. Leaving it would have been a new inconsistency introduced by the fix |
| A3 | `Pluralize` reads the server's plural and re-cases it, instead of appending "s" | `NetworkPolicys` → `NetworkPolicies`. Every Kind ending consonant+y was affected |
| A2 | The YAML editor's duplicate title | Gone; Reload/Apply/Delete stay exactly where they were. Horizontal only — see the correction above |
| A4 | The demo tab's status bar no longer repeats the `demoBar`'s sentence | `"Demo cluster — sample data that ships with kubeNimbus. Nothing is connected and none of these objects exist."` → `"Demo cluster — sample data, no connection."` in the status bar; the banner is untouched |
| A5 | Preferences' empty kubeconfig box carries its own explanation | The sentence moved inside the 88 px box it explains, and the card got a row shorter |
| A8 | The access review's `namespace <ns>` caption | Gone; the dock tab already reads `Access/<ns>` |
| A9 | `Verify` gets a border | Reads as a control rather than as a label |
| A12 | Two condition states nothing in the repo could render | `notification-dispatcher` now carries `DisruptionTarget: True` and a custom readiness gate, and `cluster-tab-pod-detail-overview-disrupted` renders them: the inverted-polarity condition draws **red** beside `True` while `Ready True` draws green, and the unclassified type draws **grey**. Both branches were previously reachable only in code |

### Withdrawn, with the reason

| # | Claimed | What it actually was |
|---|---|---|
| A6 | The CONDITIONS card is indented 16 px from its siblings | **True but not cheaply fixable.** The dot cannot hang into the card's 12 px padding without sitting on the border, and indenting the sibling cards to match trades a within-card misalignment for a between-header one. Needs a design call → **ENG-27** |
| A7 | Overview and Usage put their section headers on different sides | **Not a finding.** `BY CONTAINER` labels a repeating list of cards and cannot go inside one; `SCHEDULING` labels a single card's contents |
| A10 | The cheat sheet uses `->` where the access review uses `→` | **Not a finding.** Misread from a downscaled image; it is `→` in both, and the catalog source confirms it |
| A11 | Fold `Stop draining` onto the checkbox row | **Deliberately not done.** ~40 px in a transient state, against touching the confirm slot's state-swapping logic that UI rule 11 documents carefully → **ENG-28** |

### And one regression, caught by the byte-diff

A4's second half — shortening the shell's `Status` so the no-kubeconfig status bar
would stop repeating the empty-state card's heading — **was implemented, rendered,
and reverted**. The two strings are not two strings: the card binds its *heading* to
the same `Status` property, so the change replaced the card's own diagnosis with
`"Searched 1 location(s) — see above."` pointing at nothing above it. Strictly worse
than the duplication it removed. Splitting them needs a second property and a decision
about the parse-failure branch, which the card also renders → **ENG-29**.

This is the case for the byte-diff discipline in one paragraph: the change built
cleanly, both suites stayed green, and the only thing that caught it was diffing the
render and asking why a file had changed that had no business changing.

## Filed rather than fixed

Group B, plus the three withdrawn rows above, went to `docs/BACKLOG.md`'s Inbox with
no priority — a human promotes, per that file's one rule. The maintainer's answers to
the two open questions are folded in:

- **FEAT-66** — a resizable and sortable grid. The chosen answer to the widest finding
  here, and it subsumes the Name truncation, the clipped `Age` header and the
  unrenderable eleven-column CRD.
- **FEAT-67** — the namespace picker becomes a multi-select with a "show all" control,
  which is what makes the Namespace column carry information rather than restate the
  picker. The column itself stays.
- **FEAT-68** the sidebar icon, **FEAT-69** two buttons called Apply, **FEAT-70** one
  grouping device per group (P4 + P5), **FEAT-71** the switcher's always-visible pin,
  **FEAT-72** the Helm timestamp, **FEAT-73** the two remaining dot-beside-text sites.
- **ENG-26** the sidebar's per-section selection, **ENG-27** / **ENG-28** / **ENG-29**
  as above. **ENG-6** was annotated rather than duplicated — it already owned the fleet
  column question, and now carries this pass's measurements.

The second open question was settled directly: the health dot **stays** on CRD lists
whose printer columns have replaced the Status column, which is what A13 implements.

## Verification

- `dotnet build KubeNimbus.slnx`: 0 errors, and the only warning is the pre-existing
  CS8425 in `AsyncMergeTests.cs`. No new warnings.
- `dotnet test --project` on both suites: **335/335** Core and **97/97** App, 0 failed,
  0 skipped. (No sandbox in this container, so the Core count is the unit-only subset —
  the cluster-gated tests return early.)
- The harness renders **140** PNGs (70 scenarios × 2 themes), up from 138.

**The byte-diff, and every changed file accounted for:**

| Bucket | Count | Why |
|---|---|---|
| New | 2 | `cluster-tab-pod-detail-overview-disrupted.{dark,light}` — the new scenario |
| Changed, diff confined to the sidebar (x ≤ 320) | 12 | `NetworkPolicys` → `NetworkPolicies` only. These are the maximized-dock scenarios, where no list column is on screen |
| Changed, diff extends into the content area | 124 | The same sidebar change plus the dot column's removal shifting every list column left; and, on the scenarios that show them, the YAML title, the RBAC caption and `Verify`, the preferences box, and the node meters |
| Unchanged | 2 | `main-window-no-kubeconfig.{dark,light}` — the only two scenarios with no cluster tab, and the confirmation that the reverted regression is fully out |

`cluster-tab-exec-fullscreen-maximized` is the cleanest single check: its diff is a
**16 × 12 px box at (120, 513)**, which is exactly the `…Policys` → `…Policies` tail
and nothing else.

The sidebar-only bucket went from 16 to 12 when A13b landed, and the four that moved
are precisely `cluster-tab-helm-releases.{dark,light}` and
`cluster-tab-helm-release-detail.{dark,light}` — the Helm grid losing its own dot. A
diff that moves an exact, predicted set of files is the check working.

`design/screenshots/*.png` were deliberately **not** regenerated, for the reason the
CRD, node and Overview passes all recorded: Age is computed from the real clock, so
those files drift between any two runs and regenerating them commits a date rather
than a change.

## Provenance

- Rendered from `5f482bb` on branch `claude/visual-audit-cognitive-load-nugkgs`,
  `dotnet run --project tools/Screenshot -c Release -- <scratch>` — 69 scenarios ×
  2 themes = 138 PNGs, all written, none failed.
- `dotnet build KubeNimbus.slnx`: 0 errors, 1 warning — the pre-existing CS8425 in
  `AsyncMergeTests.cs`. No new warnings.
- Pixel probes with `convert <png> -format "%[pixel:p{x,y}]" info:`; crops with
  `convert <png> -crop WxH+X+Y +repage`.
- No live cluster in this container (registry egress blocked), so every screen was
  read on demo data and fixtures. Nothing in this document depends on a cluster —
  it is all layout, colour and text — but the unrendered-states section is the place
  where that limit actually bites.

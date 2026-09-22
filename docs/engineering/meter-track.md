# The meter track was invisible, and the token was the reason

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


`Controls/ResourceMeter`'s unfilled track is what shows where each row's bar *ends*, and
"three bars of different lengths cannot be compared row to row" is the stated reason the
node card's row was re-cut to fixed number columns. It was painted with
`HoverBackgroundBrush` — a **shared** token, `#80808080` at `Opacity="0.1"`, i.e. a 5%
wash meant to sit under a pointer. Measured on the rendered dark card: `srgb(14,14,14)`
on `srgb(8,8,8)`, a contrast ratio of **1.035:1**. The layout fix landed and the
comparison it was supposed to deliver was defeated one layer below it, because the shared
end could not be seen. The Pods row, which correctly has no limit and so paints no limits
extent, read as a *shorter bar* — i.e. as missing data.

`MeterTrackBrush` in `Styles/Theme.axaml` is the fix, and three things about it matter:

1. **The shared token was not retuned.** `HoverBackgroundBrush` drives every hover state
   in both Nimbus apps; the meter gets its own brush instead.
2. **It is theme-split.** A track light enough to read on the dark card is a near-black
   bar on the light one, out-weighing the accent fill it sits behind.
3. **It still does not meet WCAG's 3:1 floor for a non-text graphic, and that is a
   deliberate stop.** Achieved: dark `srgb(61,61,61)` on `srgb(8,8,8)` = **1.84:1**;
   light `srgb(210,210,210)` on `srgb(249,249,249)` = **1.44:1**. 3:1 needs roughly
   `srgb(92)` and `srgb(145)`, and at `srgb(145)` the track stops reading as an empty
   channel and starts competing with the fill. If that floor has to be met, the answer is
   an **outline** on the track rather than a heavier fill.

The general lesson, which is the third time this repo has paid for it: a colour chosen
for one job (a hover tint) silently becomes wrong when reused for another (a chart axis),
and the failure is invisible in one theme. `docs/research/2026-08-19-visual-audit.md` has
the measurements.

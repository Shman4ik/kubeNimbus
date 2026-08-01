# How the kubeNimbus logo is built

> This file is about the **mark's geometry** — how `logo.svg` was derived from
> the source raster. For the asset pipeline built on top of it (which SVG feeds
> which icon size, what ships where, which scripts to re-run), see
> [`LOGO-ASSETS.md`](LOGO-ASSETS.md).

> **Status: `logo.svg` / `logo-dark.svg` are the master. The generator that
> produced their first draft is gone.** The mark was originally traced out of a
> raster by a dependency-free Node toolchain (a PNG decoder, a marching-squares
> tracer, a Schneider curve fitter, a measurement pass), then taken through
> Inkscape: every `<mask>`, `<use>`, `transform` and `var()` baked out, each
> module reduced to plain paths in the root coordinate system, and three trace
> artifacts cut out by hand (see "The flattening pass" below). That made
> re-running the generator *destructive* — it would have overwritten the
> flattening — so the generator, the source raster and the raster-diff harness
> were retired and deleted rather than left as a loaded gun. They are in this
> branch's history if they are ever wanted back.
>
> **Practical consequence: `logo.svg` is now edited by hand, in a vector
> editor.** Nothing regenerates it. What the scripts in
> [`LOGO-ASSETS.md`](LOGO-ASSETS.md) regenerate is everything *downstream* of it.

The rest of this file is why the geometry is what it is: the numbers, the helm
rebuild, the crossing, and the dead ends — the things a future edit needs to
know and cannot recover by looking at the paths.

| File | What it is |
|---|---|
| `logo.svg` | the mark, ~9.6 KB, `viewBox="0 0 1024 1024"` |
| `logo-dark.svg` | same geometry, `.ink`/`.paper` swapped |
| `logo-small.svg`, `logo-micro.svg` (+ `-dark`) | the 24px and 16px marks — separate geometry, see [`LOGO-ASSETS.md`](LOGO-ASSETS.md) Part 0 |

## The shape of the file

Three modules, each an independent reusable group:

| Group | Contents | Origin |
|---|---|---|
| `#base` | the disc and the light field it encloses | traced |
| `#mascot-helm` | the ship's helm (pgNimbus: an elephant) | rebuilt geometrically |
| `#brand-broom` | the Nimbus broom, shared across the family | traced |

Four decisions in there are load-bearing:

**Painted outermost-first in true colours, not one even-odd compound path.**
The raster is a two-tone drawing, so the obvious encoding is a single path with
`fill-rule="evenodd"`. That reproduces the image exactly and is unusable: every
shape becomes parity-dependent on every other, and the three modules cannot be
separated. Instead each contour is emitted as its own `<path>` filled with
`var(--ink)` or `var(--paper)` (its winding direction says which) and painted
outermost-first. Nesting is the only ordering constraint, and no shape in one
module nests inside a shape of another — which is exactly what makes the split
legal. Verified: identical rendering, independent groups.

**Colours are two tokens.** `--ink` / `--paper` in a `<style>` block. The dark
variant is the same bytes with the two values exchanged; nothing else differs.

**Full bleed.** The disc is exactly `cx="512" cy="512" r="512"` — it touches all
four edges. The raster had 51px of margin; the build scales everything by
`512 / R_OUT` about the disc centre *before* curve fitting, so the fit tolerance
applies at final scale and the coordinates stay round. If a layout needs
breathing room, add it outside the file (CSS padding), not inside.

**The helm is not traced.** See below.

## How it was derived (historical)

The steps below produced the first draft of `logo.svg`. They no longer run —
the tooling and the raster are deleted (see Status above) — but they are what
the numbers in the next section mean, and they are the recipe to follow if this
mark ever has to be re-derived from a fresh raster.

1. **Decode** the PNG to luminance. The source was cleanly two-tone —
   two luminance clusters at ~32 and ~240 — so a 128 threshold is unambiguous.

2. **Measure.** Rays are cast from a centroid seed; the disc
   radius is the *median* over 720 angles with an inlier filter, so the broom
   sticking out past the disc cannot drag the fit. The centre is a grid search
   minimising the residual.

3. **Trace.** Marching squares at the 128 iso-level **with linear
   interpolation on the grayscale**, not on a binary mask — that is what makes
   the contours sub-pixel and smooth instead of stair-stepped. Contours are then
   simplified (RDP, 0.3px) and fitted with cubic Béziers (Schneider, max
   deviation 0.8px). The whole image is 13 contours; the biggest, 5178 points,
   becomes 72 curves.

4. **Rebuild the helm.** Dropped from the trace and re-emitted from the measured
   radii as one arm (`<g id="helm-arm">`: spoke + handle) instanced at the
   angles in `ARM_DEG` — the 45° grid everywhere the broom does not cut, see
   "The lower spokes" below. Symmetric by construction rather than by
   hand-placed nodes. The hub uses `fill-rule="evenodd"` so its bore is a real
   hole.

5. **Composite the crossing.** The broom passes in front of the helm with a
   light gap, so `#mascot-helm` is masked by `#broom-clearance` (below).

6. **Emit** both colour variants.

### Measurements (this raster, in source pixels)

| | |
|---|---|
| disc centre / radius | 511.6, 511.6 / 460.5 (rms 0.49 over 720/720 angles) |
| ring inner radius | 327.5 (band 133 wide) |
| helm centre | 510.5, 459.0 — independently confirmed by the hub bore at 510.5, 458 |
| helm rim | inner 124.0, outer 155.5 (rms 0.91 over 333/349 angles) |
| helm handle tip | r = 214 |
| hub / bore | 45 / 23 |
| spoke & handle width | 30 |
| gap the broom keeps | 17 |
| full-bleed scale | ×1.111 |

## Why the helm is rebuilt and the rest is traced

Measured off its own connected component, the generated wheel has **7 spokes
spaced 44°–60° apart**, an eccentric rim, and only about half its handles drawn.
No amount of curve-fitting cleans that up — it is wrong, not noisy. The broom and
the disc, by contrast, are drawn deliberately and their irregularity *is* the
artwork, so they are traced verbatim.

So the rule for this mark: **trace what was drawn on purpose, rebuild what the
generator got wrong.**

### `#broom-clearance`

The mask that keeps the helm behind the broom. Two parts:

- the broom's outline **widened by the 17px gap** measured off the source;
- **plus everything behind the broom.** A bare band is not enough: the helm's
  far handle survives past it as a floating fragment. In the raster there is no
  wheel at all beyond the broom.

The far side is the broom's own outline **stepped 12 times along its normal**
(36px each), not a half-plane — the broom is curved, and a straight cut leaves a
sliver where the curve pulls away from the line.

### The lower spokes

`ARM_DEG` is `[0, 45, 90, 150, 210, 270, 315]`. Outside the broom's shadow the
arms are exactly on the 45° grid; inside it **180° is dropped and 135°/225° are
pulled in to 150°/210°**, so the wedge the broom cuts carries two spokes instead
of three.

Why those three and not others: march out from the helm centre along each grid
direction and stop at `#broom-clearance`, and the surviving arm lengths are

| 0° | 45° | 90° | 135° | 180° | 225° | 270° | 315° |
|---|---|---|---|---|---|---|---|
| full | full | 160 | 112 | 117 | full | full | full |

against a rim of 137.8–172.8. Only 135° and 180° are pure stubs — cut well
inside the rim, ending in mid-air — so they are the only two whose exact angle
is a free choice, and three of them in a 45° fan read as debris rather than as
spokes going behind something. Two, spaced 60°, read as spokes.

This is a departure from a real ship's helm, which has evenly spaced spokes; it
is taken knowingly, because the arms in question are three-quarters hidden and
what they read as matters more here than what they are.

Re-derive the table for any other mark — the broom's angle relative to the
mascot is what decides which positions are free.

## The flattening pass

Done directly in Inkscape (1.4, CLI `--actions`), not in Node. Two goals: make
the file survive tools that are not a browser, and get rid of what the trace and
the clearance mask left behind.

**Portability.** `var(--ink)` / `var(--paper)` are the single worst thing you can
put in a logo master: Inkscape logs `Ignoring CSS variable` and renders the whole
mark black, and Illustrator does the same. Colour is now two classes, `.ink` /
`.paper`, with the value repeated in a `fill` attribute — CSS wins where it is
honoured, the attribute carries everywhere else. `logo-dark.svg` is still the
same bytes with the two values exchanged.

**Flattening.** `#broom-clearance` is baked in: the 13 stroked copies of the
broom outline were `object-stroke-to-path` + `path-union`ed into one shape and
`path-difference`d out of the helm, so the helm is now one plain path with the
gap already cut. `<use>`, the `rotate()`ed arms, the stroked rim circle and the
`translate()` on the helm group are all gone the same way. Each of the three
groups is now liftable into a sibling mark by copy-paste, which is the whole
point.

**Artifacts removed** (all three visible only above ~4x zoom, all three
generation debris rather than drawing):

| Where | What it was | Fix |
|---|---|---|
| ~(363, 618) | a degenerate self-intersecting loop in the traced light field, rendering as a hairline crack across the bristle halo's tip | subpath deleted, the cusp it left healed with one curve |
| ~(465, 586) | the 150° spoke clipped by the clearance at a shallow angle, leaving a needle ~3px wide and 23px long | needle dropped; rim curve now runs straight into the spoke edge, a clean 60° corner |
| ~(403, 657) | the 210° handle's tip surviving the clearance as a detached 27×28 island | subpath deleted |
| ~(644, 466) | the rim tapering to a point where the clearance runs nearly tangent to it — the "arrowhead" on the right | both edges truncated to a 10.5px blunt end |

Note the shape of that list: one artifact is the *tracer's* (a curve-fit loop),
three are the *mask's*. A boolean cut at a shallow angle produces needles, and
`path-union` does not remove them — they are not self-intersections, they are
real slivers. If the clearance geometry is ever re-derived, re-check these four
places first.

## If a sibling mark ever needs the same treatment

The reusable parts of the retired toolchain were: the PNG decode, the
marching-squares trace with grayscale interpolation, the colour-by-winding rule,
the outermost-first ordering, the full-bleed transform and the clearance-mask
construction. The per-mark parts that had to be re-derived every time were the
disc centre and radii, the contour classification (area > 200000 → base, the
mascot's bbox → dropped and rebuilt, everything else → emblem) and the mascot
parameters.

Two warnings survive the tooling:

- **Rebuild the mascot only when it is a mechanism.** The helm is one — it has
  symmetry to enforce, and the generated raster got it wrong. pgNimbus's
  elephant is line art with no symmetry to enforce, and should be **traced, not
  rebuilt**. The helm is the exception, not the pattern.
- **The pgNimbus master is a different kind of file and none of this applies to
  it.** It is one compound path where the broom and the elephant are *negative
  space* carved out of the disc, and the white showing through is the page. It
  is already vector; do not rasterise it to feed a tracer.

## Dead ends — do not retry

Recorded so these are not re-explored.

**Redrawing the broom by hand from the raster.** Produced a plausible broom that
was visibly not the same broom. If shape fidelity matters, trace.

**Lifting the broom out of the pgNimbus master by extracting subpaths.** The
master is a single snaking contour: the broom's body is a hole in it, not an
object. Splitting it into subpaths and recombining them loses the collar and
blows out the bristle interior. What does work — if family identity ever matters
more than raster fidelity — is keeping the whole master contour as a *cutter*
inside a mask, restricted to a corridor around the broom. Note the trap: subpath
6 is the bristle **hole**, and the ink silhouette is a run of the main contour,
not the other way round. Getting that backwards deletes the bristles entirely.

**A light-disc base with a negative-space broom.** Mutually exclusive. A broom
whose body is negative space vanishes on a light disc; it needs either a dark
disc behind it or an ink outline of its own.

**OpenCV / raster tracing of a vector source.** Rasterising an SVG and running
`findContours` turns smooth Béziers into hundreds of polygon nodes. Only trace
what is genuinely a raster.

**Naive least-squares circle fit.** Returned `NaN` on this data. The robust
median-radius grid search that replaced it is what works.

**Grid searches that stop at their own boundary.** Hit three separate times
(disc centre, helm centre, rim radii — the "fit" was just the edge of the search
range). Always check the optimum is interior: widen and re-centre until it is.

**`mask` and `transform` on the same element.** The mask is resolved in the
element's own user space, so the transform moves the mask with it — the helm
nearly vanished. Put the mask on an outer `<g>` and the transform on an inner one.

**`--` inside an XML comment.** Illegal; the file silently fails to parse in a
browser. Bit once, via a `--css-variable` name mentioned in a comment.

## Deliberate departures from the raster

The mark was otherwise pixel-faithful to the raster it came from (verified with
a difference blend: black except for antialiasing hairlines).

- **7 spokes and 7 handles**, five of them on an exact 45° grid and two placed
  inside the broom's shadow — see "The lower spokes" above. The raster has 7
  irregular spokes at 44°–60° and partial handles; the rebuild is the point,
  and reverting it brings that asymmetry back.
- **Full bleed**, no margin — the raster has 51px of it.
- The **light gap around the broom is uniform** at 17px, where the generated one
  varies.

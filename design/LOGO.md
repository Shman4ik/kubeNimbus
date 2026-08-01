# How the kubeNimbus logo is built

`logo.svg` and `logo-dark.svg` are **generated**, not hand-drawn. Everything here
is reproducible from the source raster:

```bash
node design/tools/measure.js      # print every number the build hard-codes
node design/tools/build-logo.js   # rewrite logo.svg + logo-dark.svg
```

Both scripts are dependency-free Node (a PNG decoder, a contour tracer and a
curve fitter, all in `design/tools/`). Re-running `build-logo.js` reproduces the
committed files byte for byte.

| File | What it is |
|---|---|
| `Gemini_Generated_Image_bju2ipbju2ipbju2.png` | the source raster, the single source of truth for shape |
| `logo.svg` | the mark, ~9.6 KB, `viewBox="0 0 1024 1024"` |
| `logo-dark.svg` | same geometry, `--ink`/`--paper` swapped |
| `tools/png.js` | minimal PNG decoder (zlib + unfilter → luminance) |
| `tools/trace.js` | marching squares, Ramer-Douglas-Peucker, Schneider curve fit |
| `tools/measure.js` | prints the measurements; run this first for a new mark |
| `tools/build-logo.js` | emits the SVGs |
| `check.html` | side-by-side / difference comparison against the raster |

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

## Pipeline

1. **Decode** the PNG to luminance (`png.js`). The source is cleanly two-tone —
   two luminance clusters at ~32 and ~240 — so a 128 threshold is unambiguous.

2. **Measure** (`measure.js`). Rays are cast from a centroid seed; the disc
   radius is the *median* over 720 angles with an inlier filter, so the broom
   sticking out past the disc cannot drag the fit. The centre is a grid search
   minimising the residual.

3. **Trace** (`trace.js`). Marching squares at the 128 iso-level **with linear
   interpolation on the grayscale**, not on a binary mask — that is what makes
   the contours sub-pixel and smooth instead of stair-stepped. Contours are then
   simplified (RDP, 0.3px) and fitted with cubic Béziers (Schneider, max
   deviation 0.8px). The whole image is 13 contours; the biggest, 5178 points,
   becomes 72 curves.

4. **Rebuild the helm.** Dropped from the trace and re-emitted from the measured
   radii as one arm (`<g id="helm-arm">`: spoke + handle) instanced eight times
   at `rotate(45°·k)`. Symmetric by construction rather than by hand-placed
   nodes. The hub uses `fill-rule="evenodd"` so its bore is a real hole.

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

## Reusing this for pgNimbus

Generic, reuse as-is: `png.js`, `trace.js`, and in `build-logo.js` the tracing,
the colour-by-winding rule, the outermost-first ordering, the full-bleed
transform, and the mask construction.

Per-mark, must be re-derived — run `measure.js` first and copy its output into
the constants block at the top of `build-logo.js`:

- `CX`, `CY`, `R_OUT`, `R_IN`;
- the contour classification. Currently three predicates: area > 200000 → base,
  the mascot's bbox → dropped and rebuilt, everything else → emblem. For another
  mark these are different bboxes; `measure.js` prints the component list to
  pick them from.
- the mascot parameters, if its mascot also needs rebuilding. pgNimbus's
  elephant is line art, not a mechanism — it has no symmetry to enforce, so it
  should almost certainly be **traced, not rebuilt**. The helm is the exception
  here, not the pattern.

**The pgNimbus master is a different kind of file and this pipeline does not
apply to it.** `pgNimbus/design/masters/logo/logo.svg` is one compound path where
the broom and the elephant are *negative space* carved out of the disc, and the
white showing through is the page. It is already vector; do not rasterise it to
feed this pipeline.

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
median-radius grid search in `measure.js` is what works.

**Grid searches that stop at their own boundary.** Hit three separate times
(disc centre, helm centre, rim radii — the "fit" was just the edge of the search
range). Always check the optimum is interior; `measure.js` widens and re-centres
until it is.

**`mask` and `transform` on the same element.** The mask is resolved in the
element's own user space, so the transform moves the mask with it — the helm
nearly vanished. Put the mask on an outer `<g>` and the transform on an inner one.

**`--` inside an XML comment.** Illegal; the file silently fails to parse in a
browser. Bit once, via a `--css-variable` name mentioned in a comment.

## Deliberate departures from the raster

The mark is otherwise pixel-faithful (verify with `check.html`: the difference
blend is black except for antialiasing hairlines).

- **8 spokes and 8 handles**, evenly spaced. The raster has 7 irregular spokes
  and partial handles. This is the point of the rebuild; reverting it brings the
  asymmetry back.
- **Full bleed**, no margin — the raster has 51px of it.
- The **light gap around the broom is uniform** at 17px, where the generated one
  varies.

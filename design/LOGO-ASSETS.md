# kubeNimbus — logo & icon assets (engineering reference)

The technical source of truth for every logo/icon: where each file lives, what
generates it, and where it's consumed. How the mark's *geometry* was derived
from the source raster is a different question, answered by
[`LOGO.md`](LOGO.md) — this file is the plumbing on top of it.

Pipeline model: **two drawings feed everything.** The mark is drawn in
`design/logo.af` and, for 24px, again in `design/logo-small.af`; every other
file in the repo is rendered from those two. Nothing under `design/masters/`,
`design/store/` or `src/KubeNimbus.App/Assets/` is hand-edited — and, since this
pass, neither are the SVGs.

```
design/logo.af  +  design/logo-small.af     Affinity, the editable masters
  → scripts/design/dump-af.js      geometry out to JSON (run via the Affinity MCP)
  → scripts/design/af-to-svg.py            design/logo.svg + design/logo-dark.svg
  → scripts/design/af-to-small-svgs.py     design/logo-small*.svg
  → scripts/design/make-small-masters.py   design/logo-micro*.svg  (from logo.svg)
  → scripts/design/make-masters.ps1        design/masters/**
  → scripts/windows/make-app-icons.ps1     src/KubeNimbus.App/Assets/**
  → scripts/windows/make-store-logos.ps1   design/store/**
```

What this replaced, and why it is worth the two extra steps: `logo.svg` used to
be the hand-edited master. It drifted — Inkscape ids and 21 editor-namespace
attributes, the wrong class on its own light field, and a `logo-dark.svg` that
no longer matched it path for path even though both headers claimed they were
the same bytes with two values exchanged. Deriving one file from another is what
makes a claim like that checkable instead of aspirational. This is also now the
same shape as pgNimbus's pipeline, which matters because the two marks share the
broom — see [`LOGO.md`](LOGO.md) for the family rules.

The per-size hand work that a bitmap workflow would need is still done once, in
SVG: there are **three** marks, and which one feeds which size is the
load-bearing decision (see below).

- **Part 0 — Why there are three masters**
- **Part 1 — Sources** (`design/logo.af`, the only hand-drawn file)
- **Part 2 — Generated masters** (`design/masters/`)
- **Part 3 — Shipped outputs** (`src/KubeNimbus.App/Assets/`)
- **Part 4 — The scripts** (source → output mapping)
- **Part 5 — GitHub surfaces**
- **Part 6 — Full store/platform resolution reference**

---

## Part 0 — Why there are three masters

The mark is a ship's helm crossed by the Nimbus broom inside a full-bleed disc.
Rendering that one file at every size does not work, and the failure is not
subtle:

| px | full mark (`logo.svg`) | verdict |
|---|---|---|
| 48, 32 | helm rim, spokes and broom all still separate | ✅ ship it |
| 24 | eight spokes land ~1px apart; broom bristles vanish | ❌ grey blob |
| 16 | helm reads as a filled circle | ❌ unrecognisable |

So the small sizes get their own marks, in the same 1024 grid so they stay
on-brand and stay re-renderable:

- **`logo-small.svg`** (24px) — **hand-drawn**, in `design/logo-small.af`. The
  full mark's own composition: the helm at the full mark's own centre with the
  broom crossing it from the lower left, six rays instead of eight, a heavy
  rim, a hub with its bore still cut, four stub pegs, and a clearance gap of
  ~45 units (≈1.05px at 24) where the broom passes.
- **`logo-micro.svg`** (16px) — **script-derived** from `logo.svg` by
  `make-small-masters.py`: four rays on the cardinal axes, no pegs, every
  stroke wider again, the helm whole and the broom moved clear of it rather
  than cutting it.

**Why one is drawn and the other is generated.** The 24px mark was generated
too, and the output was a mark that shared its broom with the full one and
nothing else — the helm came from a parameter table and the composition
(wheel in one corner, broom in the other) was the script's, chosen by searching
for a placement at which the broom cleared the helm entirely. At 24px there is
room to say what the logo actually says, and hand-drawing it is what says it.
At 16px there is not: the mark is four rays and a broom on a 16-pixel tile, a
chopped rim reads as a rendering fault rather than as depth, and the script's
answer is still the right one. Rendering a 24px drawing at 16 was tried and is
worse than the generated micro at that size — measured, not assumed.

Two rules separate both of them from the full mark, and both are deliberate:

**No disc.** The full mark's disc is a plate, and a plate costs ~20% of the
tile in each direction at every size. At 24px that is the difference between
six readable spokes and a grey ring. The small masters are one colour on
transparency and get the whole tile instead — which is also what the unplated
Windows and MSIX icon slots want in the first place. The cost is that they no
longer carry their own background: on a surface the ink cannot survive, the
`-dark` twin is not optional, it is the asset.

**The broom is the full mark's own geometry** in both of them, not a redrawn
wedge — the same `#broom-bristles` and `#broom-handle` paths, moved and scaled.
That became possible only once the full mark's broom stopped being negative
space cut out of the light field (see [`LOGO.md`](LOGO.md)); before that there
was no handle to lift. Two concessions to the pixel grid, neither a redesign:
the paths are fattened by a stroke so the hairline gaps around the ferrule fuse
instead of turning to mud, and the ferrule notch is replaced by a plain tapered
tip. The grip slot in the handle's far end goes with it, for the same reason.

**One consequence of drawing the 24px mark by hand:** `logo-small.af` holds its
own moved and fattened copy of the broom, so a change to `logo.af`'s broom does
*not* reach it. `logo-micro.svg` picks such a change up on the next re-run of
`make-small-masters.py`; `logo-small.svg` does not until someone lifts the new
broom into the `.af`. A broom edit is already a two-repository event (see
[`LOGO.md`](LOGO.md), family rule 2) — add this to that list.

**The crossing is kept at 24px and given up at 16px**, and the boundary is
where it stops reading. The full mark cuts the helm where the broom passes in
front of it, and `logo-small.af` does the same — the rim, one spoke run and two
pegs stop at a clearance gap — because that crossing *is* the composition, and
at 24px the surviving rim arc still reads as a wheel going behind something.
At 16px it does not: the arc becomes a broken ring, which reads as a rendering
fault rather than as depth. So `logo-micro.svg` keeps the helm **whole** and
places the broom at the closest distance that leaves every spoke and rim arc
intact.

**The broom stays at every size, and that is the rule.** An earlier revision
dropped it below 32px on legibility grounds and the result was a generic
ship's-wheel icon in the taskbar — the taskbar is exactly where a user
identifies the app, and the broom is what the Nimbus family shares. It costs
real clarity at 16px (it eats a third of the tile and crowds the helm); that
price is paid deliberately. If some future size genuinely cannot carry it,
write down which and why, here.

This is the same principle as pgNimbus's per-size masters ("maintain legibility
at small sizes", Microsoft's app-icon guidance), reached with SVG instead of a
bitmap editor. **Do not "simplify" the pipeline by rendering every size from
`logo.svg`** — that is the exact bug this split exists to prevent.

---

## Part 1 — Sources: `design/logo.af`

The **only** hand-drawn art. Everything else in this document is generated,
the SVGs included.

| File | What it is |
|---|---|
| `logo.af` | **the master** — Affinity, layer tree mirroring the generated SVG one-for-one |
| `logo-small.af` | **the 24px master** — Affinity, `mascot-helm` + `brand-broom`, no `base` |
| `logo.svg` | generated: the full mark, `viewBox="0 0 1024 1024"`, flattened plain paths — see [`LOGO.md`](LOGO.md) |
| `logo-dark.svg` | generated: same bytes, `.ink`/`.paper` exchanged |
| `logo-small.svg` / `-dark` | simplified mark for 24px, no disc, transparent |
| `logo-micro.svg` / `-dark` | simplified mark for 16px, no disc, transparent |
| `logo-small-plated.svg` | the 24px mark on the full mark's disc — `app.ico` only |
| `logo-micro-plated.svg` | the 16px mark on the full mark's disc — `app.ico` only |

**Why the plated pair exists.** Windows hands the taskbar, Alt+Tab and the
title bar a single `WM_SETICON` slot, so `app.ico` cannot be theme-aware — and
unplated dark line art disappears on a dark taskbar, which is the default. So
`app.ico` keeps the disc at every size, and the disc-less masters feed the
surfaces that genuinely are theme-aware (`window-icon-{light,dark}.ico`, the
MSIX `altform-unplated` / `-lightunplated` tiles). The plate costs what a plate
costs: the mark inside it is smaller. That is the trade, not a bug — and it is
why removing the disc from the theme-aware assets is worth doing separately.

All six share one colour contract: `.ink` `#242B36` and `.paper` `#F5F7FA`
(plus `.ink-s`/`.paper-s` where the value is a stroke), with the literal value
repeated in a `fill`/`stroke` attribute so tools that ignore `<style>` still
render. A `*-dark.svg` is its light twin with the two values exchanged and
nothing else changed — and it is now *generated* rather than maintained
alongside, which is the only reason that sentence is reliably true. It was not:
the two files had diverged path for path while both headers claimed otherwise.
The small and micro masters use only `.ink` and `.ink-s`: with the disc gone
there is no paper left in them.

`logo.svg` and `logo-dark.svg` are generated from `design/logo.af` by
[`scripts/design/dump-af.js`](../scripts/design/dump-af.js) →
[`scripts/design/af-to-svg.py`](../scripts/design/af-to-svg.py) — the mark was
traced from a raster once and hand-finished, with the tracer retired, and the
flattened result now lives in the `.af` (see [`LOGO.md`](LOGO.md)). Draw in the
`.af`; do not edit the SVGs.

The **three 24px files are generated from a second hand-drawn master**,
`design/logo-small.af`, by the same two-step bridge the full mark uses:
[`dump-af.js`](../scripts/design/dump-af.js) →
[`af-to-small-svgs.py`](../scripts/design/af-to-small-svgs.py). Draw in the
`.af`; do not edit the SVGs.

```bash
# with design/logo-small.af open in Affinity, run dump-af.js through the MCP
python scripts/design/af-to-small-svgs.py
```

**The three 16px files are still script-derived** from `logo.svg` by
[`make-small-masters.py`](../scripts/design/make-small-masters.py), and that
split is deliberate — see Part 0.

The whole small-mark family used to be script-derived, and the 24px one was the
case where that stopped paying. The script invents a helm from a parameter
table, lifts the broom's real paths out of `logo.svg`, and then *searches for a
placement at which the broom clears the helm entirely* — so the mark it
produced shared its broom with the full mark and nothing else: the helm was the
script's drawing, and the composition (wheel in one corner, broom in the other)
was not the full mark's. At 24px there is enough room to say what the logo
actually says, so that size is drawn. At 16px there is not, and the script's
answer — four rays, no pegs, everything fattened — is still the right one.

---

## Part 2 — Generated masters: `design/masters/`

**Generated — do not hand-edit.** Checked in so that consumers (README, the
icon scripts, a Partner Center upload) never need Inkscape.

### `icon/` — app-icon tiles (square, full-bleed disc, transparent corners)

| File | Size | Rendered from | Feeds |
|---|---|---|---|
| `icon-16.png` | 16² | `logo-micro-plated.svg` | `app.ico` 16 |
| `icon-24.png` | 24² | `logo-small-plated.svg` | `app.ico` 24 |
| `icon-32.png` | 32² | `logo.svg` | `app.ico` 32 |
| `icon-48.png` | 48² | `logo.svg` | `app.ico` 48, MSIX 44 & 50 |
| `icon-256.png` | 256² | `logo.svg` | `app.ico` 64/128/256, MSIX 150 |
| `icon-1024.png` | 1024² | `logo.svg` | MSIX scale-200/400, store listing images |

### `window/` — in-app / unplated icons (**transparent** two-tone glyph)

The mark on transparency. The name is **the theme it is used on**, not the
colour it is drawn in.

| File | Rendered from | Feeds |
|---|---|---|
| `window-light-256.png` | `logo-dark.svg` minus the disc (dark field) | `window-icon-light.ico`, MSIX `altform-lightunplated` |
| `window-dark-256.png` | `logo.svg` minus the disc (light field) | `window-icon-dark.ico`, MSIX `altform-unplated` |
| `window-light-{24,16}.png` | `logo-small.svg` / `logo-micro.svg` | same, at those sizes |
| `window-dark-{24,16}.png` | `logo-small-dark.svg` / `logo-micro-dark.svg` | same, at those sizes |

24 and 16 get their own masters rather than a downscale of the 256 — the same
"the full mark is mud down here" rule as the icon tiles, and these small
unplated tiles are precisely what the disc-less simplified marks were drawn
for. `make-app-icons.ps1` prefers an exact-size window master when one exists
(`Resolve-WindowMaster`) and only falls back to resampling the 256.

**The colour mapping inverts between the two rows above, and that is not a
typo.** Stripping the disc from the full mark leaves its light *field* as the
glyph's body, so `logo-dark.svg` is what suits a light surface. The simplified
marks have no field at all — the glyph is the ink itself — so a light surface
wants the dark-inked `logo-small.svg` and a dark surface wants `-dark`.

### `logo/` — website / marketing

| File | Size | Used by |
|---|---|---|
| `wordmark-light.svg` / `-dark.svg` | ≈4.3:1 | horizontal lockup, **text baked to paths** |
| `wordmark-light.png` / `-dark.png` | 2× | README header `<picture>` (light/dark) |
| `social-preview.png` | 1280×640 | GitHub repo social preview (solid background) |

The wordmark is the mark at 240px beside "kubeNimbus" in Segoe UI Bold. The
text is converted to paths by Inkscape at build time — a committed SVG that
still referenced the font would render in a fallback face on any machine
without Segoe UI, GitHub's renderer included.

There is deliberately **no bare-mark PNG** here: `icon/icon-1024.png` already
*is* `logo.svg` at 1024, and a second copy of the same render under `logo/`
would only drift.

### `design/store/` — Microsoft Partner Center listing images (**generated**)

Not a source — generated by `scripts/windows/make-store-logos.ps1` from
`icon/icon-1024.png` and checked in so a Partner Center re-upload is a
copy-paste, not a script run someone forgot about. Regenerate and commit
whenever the mark changes: `BoxArt-1x1-2160x2160.png`,
`AppTileIcon-1x1-300x300.png`, `Square-1x1-{150,71}x{150,71}.png`,
`Poster-9x16-1440x2160.png`.

---

## Part 3 — Shipped outputs: `src/KubeNimbus.App/Assets/`

**Generated — do not hand-edit.** Filenames are stable so the csproj and any
future installer/MSIX manifest can reference them unchanged.

| File | Size(s) | Bg | Consumed by |
|---|---|---|---|
| `app.ico` | 16,24,32,48,64,128,256 | disc | exe icon (`ApplicationIcon` in the csproj) **and** the runtime window icon — `MainWindow.axaml` sets `Icon="/Assets/app.ico"` (taskbar, Alt+Tab — and the title bar on Linux, the one platform that still draws one; Windows and macOS extend the client area over it, see UI rule 12 in [`../CLAUDE.md`](../CLAUDE.md)) |
| `window-icon-light.ico` | 16,24,32,48,256 | transparent | **nothing right now.** Generated for the same reason pgNimbus keeps its pair: the moment a theme-aware title-bar icon is wanted, the asset exists. Windows gives title bar/taskbar/Alt+Tab a single `WM_SETICON` slot, and unplated line art loses on the taskbar in light theme — which is why the plated `app.ico` is what's actually wired up |
| `window-icon-dark.ico` | 16,24,32,48,256 | transparent | same as above |
| `Msix/Square44x44Logo.scale-{100,125,150,200,400}.png` | 44,55,66,88,176 | disc | MSIX small tile |
| `Msix/Square150x150Logo.scale-{100,125,150,200,400}.png` | 150,188,225,300,600 | disc | MSIX medium tile |
| `Msix/StoreLogo.scale-{100,125,150,200,400}.png` | 50,63,75,100,200 | disc | MSIX `Properties/Logo` |
| `Msix/Square44x44Logo.targetsize-{16,24,32,48,256}_altform-unplated.png` | 16,24,32,48,256 | transparent | taskbar/Alt+Tab/Start/install-dialog icon on dark surfaces |
| `Msix/Square44x44Logo.targetsize-{16,24,32,48,256}_altform-lightunplated.png` | 16,24,32,48,256 | transparent | same, on light surfaces |
| `Yaml-Mode.xshd` | — | n/a | *(not a logo — syntax highlighting; listed to avoid confusion)* |

Why a whole **set** per MSIX logo instead of one flat file: without a
qualifier-matched size, Windows shrinks the one file it has and adds its own
backplate around it — visible as an undersized icon on a big dark square in the
taskbar, Start, and the sideload "Install app?" dialog. The qualified filenames
alone don't do anything either: a pack step has to compile them into
`resources.pri` via `makepri` for Windows to resolve them.

> **kubeNimbus does not ship an MSIX/installer yet.** The `Msix/` set is
> generated now because it costs one script run and because the alternative —
> discovering the whole qualifier story during a first Store submission — is
> exactly what this file exists to prevent. The csproj marks only `Assets/*.ico`
> as `AvaloniaResource`, so `Msix/**` stays a source-tree, packaging-time-only
> input and never enters the binary.

---

## Part 4 — The scripts (source → output)

### `scripts/design/dump-af.js` (run through the Affinity MCP)
Dumps `design/logo.af`'s geometry to `~/Desktop/kubenimbus-logo-dump.json`.
Affinity scripts can only write to the Desktop, hence the destination. It picks
the document by **repository directory as well as filename** — pgNimbus's master
is also called `logo.af` and the two are routinely open side by side, so
matching on the filename alone dumps whichever the editor happens to list first.

### `scripts/design/af-to-svg.py` (any OS, stdlib only)
Run after `dump-af.js`. Bakes every node transform into the path data and writes
`design/logo.svg` plus `design/logo-dark.svg` (the same bytes, two values
exchanged).

```
logo.af ── dump ──► kubenimbus-logo-dump.json ──► logo.svg ──► logo-dark.svg
```

Two differences from pgNimbus's otherwise-identical copy, both load-bearing:
it **accumulates ancestor transforms** rather than reading only a leaf's own
(Affinity puts a transform wherever the edit was made — transform a group and
the group carries it, and a leaf-only reader silently emits the untransformed
geometry), and it **carries a stroke width through that transform**, because a
scaled clearance halo is a different weight and the family's 39.451 is exact.
It refuses a non-uniform scale rather than guess which width to report.

### `scripts/design/af-to-small-svgs.py` (Python 3.8+, stdlib only)
Carries `design/logo-small.af` across to the three 24px files. Run after
`dump-af.js`, **before** `make-masters.ps1`.

```
logo-small.af ─ dump-af.js ─► kubenimbus-logo-small-dump.json
                             ─► logo-small.svg + -dark + -plated
```

It reads the dump the same way `af-to-svg.py` does — ancestor transforms
accumulated, stroke widths carried through them — and adds one thing that file
does not need: the **plated** fit. The mark runs corner to corner, so the disc
version is fitted to the content's *smallest enclosing circle* rather than to
its bounding box (a box fit leaves about a fifth of the light field empty), at
97% so antialiasing cannot bleed into the ring.

### `scripts/design/make-small-masters.py` (Python 3.8+, stdlib only)
Derives the **16px** mark from `logo.svg`. Run after any change to the full
mark's broom, **before** `make-masters.ps1`.

```
logo.svg #broom-bristles + #broom-handle ──► logo-micro.svg + -dark + -plated
   + a parametric helm, fitted to the tile
```

The knobs at the bottom of the script (ray count, helm scale, fattening stroke,
clearance) were chosen by rendering candidates at actual 16 px and comparing
them, not by taste. If you change them, do that again — a value that looks
right at 1024 tells you nothing about what it does to a 16px tile. It used to
emit the 24px pair too; that pair is `logo-small.af`'s now.

### `scripts/design/make-masters.ps1` (Inkscape + System.Drawing)
Rebuilds everything in `design/masters/`. Run after editing any
`design/logo*.svg`.

```
logo-micro-plated.svg ─ render 16 ───────────► masters/icon/icon-16.png
logo-small-plated.svg ─ render 24 ───────────► masters/icon/icon-24.png
logo.svg ────── render 32,48,256,1024 ───────► masters/icon/icon-{32,48,256,1024}.png
logo-dark.svg ─ strip disc, render 256 ──────► masters/window/window-light-256.png
logo.svg ────── strip disc, render 256 ──────► masters/window/window-dark-256.png
logo-{small,micro}.svg ─ render 24,16 ───────► masters/window/window-light-{24,16}.png
logo-{small,micro}-dark.svg ─ render 24,16 ──► masters/window/window-dark-{24,16}.png
logo{,-dark}.svg + <text> ─ text→path, tight viewBox, 2× png
                                             ► masters/logo/wordmark-{light,dark}.{svg,png}
wordmark-dark.png on #242B36, 1280×640 ──────► masters/logo/social-preview.png
```

Two details worth knowing before editing it: the disc is stripped by regex
(`<circle … r="512" …/>`) rather than kept as two more hand-maintained SVGs, so
`design/logo*.svg` stays the single source of geometry — the script **throws**
if that circle isn't found. And the wordmark's viewBox is measured with
`inkscape --query-*` after the text is baked, because the text's advance width
depends on the font and cannot be hardcoded.

### `scripts/windows/make-app-icons.ps1` (Windows, System.Drawing)
Assembles the shipped icons. Copies exact-size masters verbatim, derives only
larger sizes:

```
window/window-light-{16,24,256}.png ── exact where present, else resize
                                            ──► Assets/window-icon-light.ico
window/window-dark-{16,24,256}.png  ── same  ──► Assets/window-icon-dark.ico
icon/icon-{16,24,32,48}.png ── copy (as-is) ─┐
icon/icon-256.png ── downscale → 64,128 ─────┼─► Assets/app.ico  (7 entries)
icon/icon-48.png   ── → 44,55,66 ────────────► Assets/Msix/Square44x44Logo.scale-{100,125,150}.png
icon/icon-1024.png ── → 88,176 ──────────────► Assets/Msix/Square44x44Logo.scale-{200,400}.png
icon/icon-48.png   ── → 50,63,75 ────────────► Assets/Msix/StoreLogo.scale-{100,125,150}.png
icon/icon-1024.png ── → 100,200 ─────────────► Assets/Msix/StoreLogo.scale-{200,400}.png
icon/icon-256.png  ── → 150,188,225 ─────────► Assets/Msix/Square150x150Logo.scale-{100,125,150}.png
icon/icon-1024.png ── → 300,600 ─────────────► Assets/Msix/Square150x150Logo.scale-{200,400}.png
window/window-dark-{16,24,256}.png  ── → 16,24,32,48,256 ► Assets/Msix/Square44x44Logo.targetsize-*_altform-unplated.png
window/window-light-{16,24,256}.png ── → 16,24,32,48,256 ► Assets/Msix/Square44x44Logo.targetsize-*_altform-lightunplated.png
```

(scale-200/400 fall back to the 1024 master instead of upscaling the small
48/256 master, which would blur; sizes ≤ the small master still use it — see
the script's `SmallFrom` per-logo mapping.)

### `scripts/windows/make-store-logos.ps1` (manual, upload-only)
Partner Center **Store-listing** images from `icon/icon-1024.png`: BoxArt 2160,
tile 300/150/71, 9:16 poster 1440×2160. Writes to `design/store/` by default
(checked in — re-run and commit when the mark changes) or `-OutDir` for a
one-off. Not wired into any build; the Partner Center upload is manual.

### The full refresh

```powershell
# 1. with design/logo.af AND design/logo-small.af open in Affinity, run
#    scripts/design/dump-af.js through the Affinity MCP (it dumps whichever
#    of them are open, to your Desktop)
python scripts/design/af-to-svg.py          # design/logo.svg + logo-dark.svg
python scripts/design/af-to-small-svgs.py   # design/logo-small*.svg
python scripts/design/make-small-masters.py # design/logo-micro*.svg
pwsh scripts/design/make-masters.ps1        # design/masters/**
pwsh scripts/windows/make-app-icons.ps1     # src/KubeNimbus.App/Assets/**
pwsh scripts/windows/make-store-logos.ps1   # design/store/**
```

They must run in that order — each one eats the previous one's output. The
small-masters step is only strictly needed when the broom changed, but it is
cheap and running it unconditionally is what keeps "the small icon looks like
the logo" a property you re-run rather than one you eyeball.

There is no macOS `.icns` step yet — kubeNimbus has no `.app`/`.dmg` packaging.
When it gets one, the masters already cover every iconset slot (16/32/256 exact,
the rest from `icon-1024.png`); see Part 6.

---

## Part 5 — GitHub page surfaces

**1. README header** (`README.md`, top) — the horizontal wordmark, theme-switched:

```html
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="design/masters/logo/wordmark-dark.png">
  <img src="design/masters/logo/wordmark-light.png" alt="kubeNimbus logo…" width="360">
</picture>
```

PNG rather than SVG deliberately: GitHub's `<picture>` handling of relative SVG
sources is inconsistent, and the 2× PNG is crisp at the 360px display width.
The README column is ≈980px on desktop and device-width on mobile with
`max-width:100%`, so one image covers both.

**2. Repo social preview** — the share/search card (Settings → Social preview,
*not* a file in the repo). Upload `design/masters/logo/social-preview.png`
(1280×640, solid background, well under 1 MB).

---

## Part 6 — Full store/platform resolution reference

What the pipeline produces today, and what a fuller platform presence would
add. Everything derives from vector, so unlike pgNimbus there is no upstream
size ceiling — a bigger master is one edit to `make-masters.ps1`.

**Windows exe (`app.ico`):** 16, 24, 32, 48, 64, 128, 256. *(Could add 20, 40,
96 for complete Explorer coverage.)*

**macOS (`app.icns`) — not built yet:** would need 16, 32, 64, 128, 256, 512 at
@1×/@2× → real px 16…1024. Square full-bleed, **no pre-rounding / no shadow**
(Apple masks). A Mac App Store upload additionally wants a flat 1024×1024 with
no alpha — note the mark's corners *are* transparent today, so that one needs a
flattened variant.

**MSIX / Microsoft Store tiles** — shipped: 44, 150, 50 (required), each at
scale 100/125/150/200/400%, plus Square44x44Logo's unplated
targetsize-{16,24,32,48,256} pair (dark/light) for taskbar/Start/Alt+Tab.
Optional for a richer tile set: Square71x71, Square310x310, Wide310x150,
SplashScreen 620×300 (same per-scale set each).

### Microsoft guideline compliance

Measured against Microsoft's
[app-icon-design](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design)
and [app-icon-construction](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction)
guidance:

- ✅ **Bare-minimum size set** (16/24/32/48/256) — met by `app.ico` and the
  targetsize pair. With a 256px entry present, Windows only ever scales *down*.
- ✅ **Unplated + lightunplated variants** — shipped; these are what keep the
  taskbar/Start icon from getting Windows's auto-backplate.
- ✅ **Per-size art at small sizes** — Part 0's three-master split.
- ⚠️ **Partial targetsize coverage.** Microsoft's *required* AppList list is 14
  sizes: 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256 — we ship 5
  (16/24/32/48/256). The gap bites at fractional display scales: at 125% / 150%
  the taskbar wants **30 / 36 px** and Windows scales our 48 down. Fix = extend
  `make-app-icons.ps1` to emit the intermediate targetsizes; because the source
  is vector, the 20/30 sizes should be rendered from `logo-micro`/`logo-small`
  rather than downscaled.
- ⚠️ **No plain (plated) `targetsize-N.png` files.** Microsoft lists three
  variants per size (plain, unplated, lightunplated); we ship the two unplated
  ones. Windows falls back to the `scale-*` assets for plated contexts, so
  nothing visibly breaks.
- ℹ️ **Disc tile vs transparent.** Microsoft prefers transparent-background
  icons but explicitly allows a branded plate as long as theme-aware unplated
  assets exist — which is exactly the split here: the full-bleed disc for
  plated surfaces, the disc-stripped glyph for unplated ones.

---

## Conventions

- Per `CLAUDE.md`'s "keep this file current" rule: when the layout or pipeline
  changes, update this file **and** the `## App icon / logo assets` section of
  `CLAUDE.md` in the same change.
- Nothing under `design/masters/`, `design/store/` or
  `src/KubeNimbus.App/Assets/*.ico` is hand-edited. If one of them is wrong,
  the fix is in `design/*.svg` or in a script.

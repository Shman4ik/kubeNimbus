# kubeNimbus — logo & icon assets (engineering reference)

The technical source of truth for every logo/icon: where each file lives, what
generates it, and where it's consumed. How the mark's *geometry* was derived
from the source raster is a different question, answered by
[`LOGO.md`](LOGO.md) — this file is the plumbing on top of it.

Pipeline model: kubeNimbus's mark is **vector**, so unlike pgNimbus (whose
masters are hand-drawn bitmaps and whose scripts only assemble them) the
masters here are *generated* — every raster in `design/masters/` comes out of
`scripts/design/make-masters.ps1`. The per-size hand work that pgNimbus does in
a bitmap editor is done once, in SVG: there are **three** marks, and which one
feeds which size is the load-bearing decision (see below).

- **Part 0 — Why there are three masters**
- **Part 1 — Sources** (`design/*.svg`, the only hand-edited files)
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

So the small sizes get their own marks, drawn in the same 1024 grid so they
stay on-brand and stay re-renderable:

- **`logo-small.svg`** (24px) — a bold helm (six rays, heavy rim, fat hub, six
  stub pegs) beside the broom, filling the whole tile.
- **`logo-micro.svg`** (16px) — four rays on the cardinal axes, no pegs, every
  stroke wider again, same broom.

Two rules separate them from the full mark, and both are deliberate:

**No disc.** The full mark's disc is a plate, and a plate costs ~20% of the
tile in each direction at every size. At 24px that is the difference between
six readable spokes and a grey ring. The small masters are one colour on
transparency and get the whole tile instead — which is also what the unplated
Windows and MSIX icon slots want in the first place. The cost is that they no
longer carry their own background: on a surface the ink cannot survive, the
`-dark` twin is not optional, it is the asset.

**The broom is `logo.svg`'s own geometry**, not a redrawn wedge — the same
`#broom-bristles` and `#broom-handle` paths, scaled. That became possible only
once the full mark's broom stopped being negative space cut out of the light
field (see [`LOGO.md`](LOGO.md)); before that there was no handle to lift. Two
concessions to the pixel grid, neither a redesign: the paths are fattened by a
stroke so the hairline gaps around the ferrule fuse instead of turning to mud,
and the ferrule notch is replaced by a plain tapered tip.

And one thing that is *not* carried over: the full mark cuts the helm where the
broom crosses it. The small masters keep the helm **whole** and place the broom
at the closest distance that leaves every spoke, peg and rim arc intact. At
these sizes a chopped spoke does not read as depth, it reads as a rendering
fault.

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

## Part 1 — Sources: `design/*.svg`

The **only** hand-edited art. Everything else in this document is generated.

| File | What it is |
|---|---|
| `logo.svg` | the full mark, `viewBox="0 0 1024 1024"`, flattened plain paths — see [`LOGO.md`](LOGO.md) |
| `logo-dark.svg` | same bytes, `.ink`/`.paper` exchanged |
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
nothing else changed — if you edit geometry, edit both (or regenerate the dark
twin with the same two-way swap). The small and micro masters use only `.ink`
and `.ink-s`: with the disc gone there is no paper left in them.

`logo.svg` and `logo-dark.svg` are hand-edited — traced from a raster once and
then hand-finished, with the tracer retired rather than left able to overwrite
that work (see [`LOGO.md`](LOGO.md)). Edit them in a vector editor.

The **six small/micro files are generated**, by
[`scripts/design/make-small-masters.py`](../scripts/design/make-small-masters.py),
from `logo.svg`. Do not hand-edit them; re-run the script:

```bash
python scripts/design/make-small-masters.py
```

They used to be hand-drawn approximations of the broom — a straight stroke and
a four-point wedge — because the full mark had no broom to copy: its handle was
negative space cut out of the light field, not an object. Once `#brand-broom`
became self-contained the small marks could lift the real `#broom-bristles` and
`#broom-handle` paths, and "the small icon looks like the logo" stopped being
something you eyeball and became something you re-run. The helm stays
parametric, because eight thin spokes cannot survive 16px whatever you do to
them.

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

### `scripts/design/make-small-masters.py` (Python 3.8+, stdlib only)
Derives the 24px and 16px marks from `logo.svg`. Run after any change to the
full mark's broom, **before** `make-masters.ps1`.

```
logo.svg #broom-bristles + #broom-handle ─┬─► logo-small.svg  + -dark  + -plated
   + a parametric helm, fitted to the tile └─► logo-micro.svg  + -dark  + -plated
```

The knobs at the bottom of the script (ray count, helm scale, fattening stroke,
clearance) were chosen by rendering candidates at actual 16 and 24 px and
comparing them, not by taste. If you change them, do that again — a value that
looks right at 1024 tells you nothing about what it does to a 16px tile.

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
python scripts/design/make-small-masters.py # design/logo-{small,micro}*.svg
pwsh scripts/design/make-masters.ps1        # design/masters/**
pwsh scripts/windows/make-app-icons.ps1     # src/KubeNimbus.App/Assets/**
pwsh scripts/windows/make-store-logos.ps1   # design/store/**
```

The first step is only needed when `logo.svg`'s broom changed; the other three
after any `design/logo*.svg` edit. They must run in that order — each one eats
the previous one's output.

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

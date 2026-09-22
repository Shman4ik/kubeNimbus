# kubeNimbus — logo & icon assets (engineering reference)

The technical source of truth for every logo/icon: where each file lives, what
generates it, and where it's consumed. How the mark's *geometry* was derived
from the source raster is a different question, answered by
[`LOGO.md`](LOGO.md) — this file is the plumbing on top of it.

Pipeline model: **one drawing feeds everything**, the same as pgNimbus. The mark
is drawn once in `design/logo.af`, exported to `design/logo.svg`, and every
raster in the repo is rendered from that. Nothing under `design/masters/`,
`design/store/` or `src/KubeNimbus.App/Assets/` is hand-edited, and neither is
the SVG.

```
design/logo.af                             Affinity, the editable master
  → scripts/design/dump-af.js              geometry out to JSON (run via the Affinity MCP)
  → scripts/design/af-to-svg.py            design/logo.svg
  → scripts/design/make-masters.ps1        design/masters/**
  → scripts/windows/make-app-icons.ps1     src/KubeNimbus.App/Assets/**
  → scripts/windows/make-store-logos.ps1   design/store/**
```

What the `.af` bridge replaced: `logo.svg` used to be the hand-edited master. It
drifted — Inkscape ids and 21 editor-namespace attributes, the wrong class on
its own light field, and a dark twin that no longer matched it path for path
even though both headers claimed they were the same bytes with two values
exchanged. Deriving a file from the drawing is what makes a claim like that
checkable. This is the same shape as pgNimbus's pipeline, which matters because
the two marks share the plate, the field and the broom — see
[`LOGO.md`](LOGO.md) for the family rules.

- **Part 0 — One mark at every size**
- **Part 1 — Sources** (`design/logo.af`, the only hand-drawn file)
- **Part 2 — Generated masters** (`design/masters/`)
- **Part 3 — Shipped outputs** (`src/KubeNimbus.App/Assets/`)
- **Part 4 — The scripts** (source → output mapping)
- **Part 5 — GitHub surfaces**
- **Part 6 — Full store/platform resolution reference**

---

## Part 0 — One mark at every size

The mark is a ship's helm crossed by the Nimbus broom inside a full-bleed disc,
and **every size is a render of that one file**, 16px included.

It used to be three marks. Below 32px the full mark's eight helm spokes land
about a pixel apart, so the 24px size had its own hand-drawn master
(`logo-small.af`, six rays, no disc) and the 16px size a script-generated one
(`logo-micro`, four rays), each in a transparent, a dark and a plated colourway,
plus a palette-swapped `logo-dark.svg` of the full mark to cut the transparent
window glyphs from. That was eight SVGs, a second `.af`, two generator scripts
and four extra window masters — and a broom edit had to be re-lifted into the
24px drawing by hand. They were removed (2026-09): the simplified marks looked
like a different logo at exactly the sizes people see most (taskbar, Alt+Tab,
Explorer), and pgNimbus had already shown that the modular vector mark
rasterises acceptably all the way down. The family now reads as one mark
everywhere, in both apps.

What was given up, stated so it can be re-weighed: at 16px the helm's spokes
merge and the wheel reads more as a textured disc than as eight spokes. The
broom — the part the family shares — still reads. If a size ever genuinely stops
working, the fix is a simplified *mark* fed into `make-masters.ps1`, never a
hand-painted PNG that nothing can regenerate.

**The broom stays at every size, and that is the rule.** An earlier revision
dropped it below 32px on legibility grounds and the result was a generic
ship's-wheel icon in the taskbar — the taskbar is exactly where a user
identifies the app, and the broom is what the Nimbus family shares.

---

## Part 1 — Sources: `design/logo.af`

The **only** hand-drawn art. Everything else in this document is generated,
the SVG included.

| File | What it is |
|---|---|
| `logo.af` | **the master** — Affinity, layer tree mirroring the generated SVG one-for-one |
| `logo.svg` | generated: the full mark, `viewBox="0 0 1024 1024"`, flattened plain paths — see [`LOGO.md`](LOGO.md) |

There is **no dark twin**. The plate carries the mark's own contrast, so the
one file reads on a white README, a dark README, a light taskbar and a dark one.
That is also why `app.ico` keeps the disc: Windows hands the taskbar, Alt+Tab
and the title bar a single `WM_SETICON` slot, so `app.ico` cannot be
theme-aware, and unplated dark line art would vanish on a dark taskbar (the
default).

The colour contract is `.ink` `#242B36` and `.paper` `#F5F7FA` (plus
`.ink-s`/`.paper-s` where the value is a stroke), with the literal value
repeated in a `fill`/`stroke` attribute so tools that ignore `<style>` still
render.

`logo.svg` is generated from `design/logo.af` by
[`scripts/design/dump-af.js`](../scripts/design/dump-af.js) →
[`scripts/design/af-to-svg.py`](../scripts/design/af-to-svg.py) — the mark was
traced from a raster once and hand-finished, with the tracer retired, and the
flattened result now lives in the `.af` (see [`LOGO.md`](LOGO.md)). Draw in the
`.af`; do not edit the SVG.

---

## Part 2 — Generated masters: `design/masters/`

**Generated — do not hand-edit.** Checked in so that consumers (README, the
icon scripts, a Partner Center upload) never need Inkscape.

### `icon/` — app-icon tiles (square, full-bleed disc, transparent corners)

| File | Size | Feeds |
|---|---|---|
| `icon-16.png` | 16² | `app.ico` 16 |
| `icon-24.png` | 24² | `app.ico` 24 |
| `icon-32.png` | 32² | `app.ico` 32 |
| `icon-48.png` | 48² | `app.ico` 48, MSIX 44 & 50 |
| `icon-256.png` | 256² | `app.ico` 64/128/256, MSIX 150 |
| `icon-1024.png` | 1024² | MSIX scale-200/400, store listing images |

Every one is a render of `logo.svg`.

### `window/` — theme-keyed icons (plated, byte-identical pair)

| File | Size | Feeds |
|---|---|---|
| `window-light-256.png` | 256² | `window-icon-light.ico`, MSIX `altform-lightunplated` |
| `window-dark-256.png` | 256² | `window-icon-dark.ico`, MSIX `altform-unplated` |

Both are the same render of `logo.svg` — the choice pgNimbus made first. They
used to be transparent line art (the disc stripped out of `logo.svg` and of
`logo-dark.svg`), plus separate 16 and 24px cuts from the simplified marks. Two
file names are kept only because `make-app-icons.ps1` still keys the light and
dark outputs off them. The MSIX "unplated" tiles therefore carry a plate too,
and Windows adds its own backplate around them: a plate inside a plate,
accepted deliberately for one mark everywhere.

### `logo/` — website / marketing (transparent, except the social card)

| File | Size | Used by |
|---|---|---|
| `logo.png` | 1024² | the bare mark, for a site header or anything else wanting "the logo" |
| `wordmark-light.svg` / `-dark.svg` | ≈4.3:1 | horizontal lockup, **text baked to paths** |
| `wordmark-light.png` / `-dark.png` | 2× | README header `<picture>` (light/dark) |
| `social-preview.png` | 1280×640 | GitHub repo social preview (solid background) |

The wordmark is the mark at 240px beside "kubeNimbus" in Segoe UI Bold. The
text is converted to paths by Inkscape at build time — a committed SVG that
still referenced the font would render in a fallback face on any machine
without Segoe UI, GitHub's renderer included. **Both lockups carry the same
mark**; only the type changes colour, because "kubeNimbus" set in ink is
unreadable on a dark README.

The social card is pgNimbus's layout exactly: the light-on-dark wordmark with
the one-line tagline ("Fast, open-source Kubernetes desktop client") under it,
centred as one block on the `.ink` navy. It used to be the wordmark alone, which
made the two repositories' link previews look unrelated. The tagline is kept
short enough to sit well inside the card's width — a version naming all three
platforms ran nearly edge to edge. The card is opaque because a transparent one
renders white in some clients and black in others.

### `design/store/` — Microsoft Partner Center listing images (**generated**)

Not a source — generated by `scripts/windows/make-store-logos.ps1` from
`icon/icon-1024.png` (the square tiles) and `logo/wordmark-dark.svg` (the
poster), and checked in so a Partner Center re-upload is a copy-paste, not a
script run someone forgot about. Regenerate and commit whenever the mark
changes: `BoxArt-1x1-2160x2160.png`, `AppTileIcon-1x1-300x300.png`,
`Square-1x1-{150,71}x{150,71}.png`, `Poster-9x16-1440x2160.png`. The poster
carries the same wordmark-plus-tagline layout as `social-preview.png`, not the
bare tile.

---

## Part 3 — Shipped outputs: `src/KubeNimbus.App/Assets/`

**Generated — do not hand-edit.** Filenames are stable so the csproj and any
future installer/MSIX manifest can reference them unchanged.

| File | Size(s) | Bg | Consumed by |
|---|---|---|---|
| `app.ico` | 16,24,32,48,64,128,256 | disc | exe icon (`ApplicationIcon` in the csproj) **and** the runtime window icon — `WindowIcons.cs` loads it through `AssetLoader`, deliberately not through XAML's `Icon=` (taskbar, Alt+Tab — and the title bar on Linux, the one platform that still draws one; Windows and macOS extend the client area over it, see UI rule 12 in [`../CLAUDE.md`](../CLAUDE.md)) |
| `window-icon-light.ico` | 16,24,32,48,256 | disc | **nothing right now.** Generated for the same reason pgNimbus keeps its pair: the moment a theme-aware title-bar icon is wanted, the asset exists. Windows gives title bar/taskbar/Alt+Tab a single `WM_SETICON` slot, and unplated line art loses on the taskbar in light theme — which is why the plated `app.ico` is what's actually wired up |
| `window-icon-dark.ico` | 16,24,32,48,256 | disc | same as above |
| `Msix/Square44x44Logo.scale-{100,125,150,200,400}.png` | 44,55,66,88,176 | disc | MSIX small tile |
| `Msix/Square150x150Logo.scale-{100,125,150,200,400}.png` | 150,188,225,300,600 | disc | MSIX medium tile |
| `Msix/StoreLogo.scale-{100,125,150,200,400}.png` | 50,63,75,100,200 | disc | MSIX `Properties/Logo` |
| `Msix/Square44x44Logo.targetsize-{16,24,32,48,256}_altform-unplated.png` | 16,24,32,48,256 | disc | taskbar/Alt+Tab/Start/install-dialog icon on dark surfaces |
| `Msix/Square44x44Logo.targetsize-{16,24,32,48,256}_altform-lightunplated.png` | 16,24,32,48,256 | disc | same, on light surfaces |
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
`design/logo.svg`.

Two differences from pgNimbus's otherwise-identical copy, both load-bearing:
it **accumulates ancestor transforms** rather than reading only a leaf's own
(Affinity puts a transform wherever the edit was made — transform a group and
the group carries it, and a leaf-only reader silently emits the untransformed
geometry), and it **carries a stroke width through that transform**, because a
scaled clearance halo is a different weight and the family's 39.451 is exact.
It refuses a non-uniform scale rather than guess which width to report.

### `scripts/design/make-masters.ps1` (Inkscape + System.Drawing)
Rebuilds everything in `design/masters/`. Run after any change to
`design/logo.svg`. It is pgNimbus's script with the product name and the
tagline changed, and should stay that way.

```
logo.svg ── render 16,24,32,48,256,1024 ─────► masters/icon/icon-*.png
logo.svg ── render 256, twice ───────────────► masters/window/window-{light,dark}-256.png
logo.svg ── render 1024 ─────────────────────► masters/logo/logo.png
logo.svg + <text> ─ text→path, tight viewBox, 2× png
                                             ► masters/logo/wordmark-{light,dark}.{svg,png}
wordmark-dark.svg + tagline on #242B36 ──────► masters/logo/social-preview.png  (1280×640)
```

The wordmark's viewBox is measured with `inkscape --query-*` after the text is
baked, because the text's advance width depends on the font and cannot be
hardcoded.

### `scripts/windows/make-app-icons.ps1` (Windows, System.Drawing)
Assembles the shipped icons. Copies exact-size masters verbatim, derives only
the sizes that have no master of their own, always from a larger one:

```
window/window-light-256.png ── → 16,24,32,48,256 ► Assets/window-icon-light.ico
window/window-dark-256.png  ── → 16,24,32,48,256 ► Assets/window-icon-dark.ico
icon/icon-{16,24,32,48}.png ── copy (as-is) ─┐
icon/icon-256.png ── downscale → 64,128 ─────┼─► Assets/app.ico  (7 entries)
icon/icon-48.png   ── → 44,55,66 ────────────► Assets/Msix/Square44x44Logo.scale-{100,125,150}.png
icon/icon-1024.png ── → 88,176 ──────────────► Assets/Msix/Square44x44Logo.scale-{200,400}.png
icon/icon-48.png   ── → 50,63,75 ────────────► Assets/Msix/StoreLogo.scale-{100,125,150}.png
icon/icon-1024.png ── → 100,200 ─────────────► Assets/Msix/StoreLogo.scale-{200,400}.png
icon/icon-256.png  ── → 150,188,225 ─────────► Assets/Msix/Square150x150Logo.scale-{100,125,150}.png
icon/icon-1024.png ── → 300,600 ─────────────► Assets/Msix/Square150x150Logo.scale-{200,400}.png
window/window-dark-256.png  ── → 16,24,32,48,256 ► Assets/Msix/Square44x44Logo.targetsize-*_altform-unplated.png
window/window-light-256.png ── → 16,24,32,48,256 ► Assets/Msix/Square44x44Logo.targetsize-*_altform-lightunplated.png
```

(scale-200/400 fall back to the 1024 master instead of upscaling the small
48/256 master, which would blur; sizes ≤ the small master still use it — see
the script's `SmallFrom` per-logo mapping.)

### `scripts/windows/make-store-logos.ps1` (manual, upload-only; Inkscape for the poster)
Partner Center **Store-listing** images: BoxArt 2160 and tiles 300/150/71 from
`icon/icon-1024.png`, and the 9:16 poster 1440×2160 — `wordmark-dark.svg` plus
the tagline on the `.ink` navy, the same layout as the social card. Writes to
`design/store/` by default (checked in — re-run and commit when the mark
changes) or `-OutDir` for a one-off. Not wired into any build; the Partner
Center upload is manual.

### The full refresh

```powershell
# 1. with design/logo.af open in Affinity, run scripts/design/dump-af.js
#    through the Affinity MCP (it writes to your Desktop)
python scripts/design/af-to-svg.py          # design/logo.svg
pwsh scripts/design/make-masters.ps1        # design/masters/**
pwsh scripts/windows/make-app-icons.ps1     # src/KubeNimbus.App/Assets/**
pwsh scripts/windows/make-store-logos.ps1   # design/store/**
```

They must run in that order — each one eats the previous one's output.

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
- ⚠️ **No per-size art at small sizes.** Microsoft recommends simplifying a
  mark at 16/24px; kubeNimbus renders the full mark there on purpose — see
  Part 0 for the trade.
- ⚠️ **Partial targetsize coverage.** Microsoft's *required* AppList list is 14
  sizes: 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256 — we ship 5
  (16/24/32/48/256). The gap bites at fractional display scales: at 125% / 150%
  the taskbar wants **30 / 36 px** and Windows scales our 48 down. Fix = extend
  `make-app-icons.ps1` to emit the intermediate targetsizes; because the source
  is vector, they should be rendered from `logo.svg` rather than downscaled.
- ⚠️ **No plain (plated) `targetsize-N.png` files.** Microsoft lists three
  variants per size (plain, unplated, lightunplated); we ship the two unplated
  ones. Windows falls back to the `scale-*` assets for plated contexts, so
  nothing visibly breaks.
- ℹ️ **Disc tile everywhere.** Microsoft prefers transparent-background
  icons for the unplated slots; kubeNimbus ships the plated mark there too, so
  Windows draws its own backplate around it. Accepted deliberately, the same
  as pgNimbus, for one mark everywhere.

---

## Conventions

- Per `CLAUDE.md`'s "keep this file current" rule: when the layout or pipeline
  changes, update this file **and** the `## App icon / logo assets` section of
  `CLAUDE.md` in the same change.
- Nothing under `design/masters/`, `design/store/` or
  `src/KubeNimbus.App/Assets/*.ico` is hand-edited. If one of them is wrong,
  the fix is in `design/logo.af` or in a script.

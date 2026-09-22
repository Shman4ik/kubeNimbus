# App icon / logo assets

Full reference: [`design/LOGO-ASSETS.md`](../design/LOGO-ASSETS.md) (pipeline,
every file, every consumer); [`design/LOGO.md`](../design/LOGO.md) covers how the
mark's geometry was derived. Four rules matter here:

1. **Nothing in `design/*.svg` is hand-edited; `design/logo.af` is the art.**
   Everything else under `design/`, `design/store/` and
   `src/KubeNimbus.App/Assets/*.ico|Msix/**` is generated and checked in.
   (`design/screenshots/` comes out of `tools/Screenshot`, not the logo
   pipeline — see [`design/screenshots/README.md`](../design/screenshots/README.md)
   for the scenario→file mapping.) Draw in the `.af`, then run the pipeline
   **in this order** — each step eats the previous one's output:

   ```powershell
   # with design/logo.af open in Affinity, run scripts/design/dump-af.js
   # through the Affinity MCP first.
   python scripts/design/af-to-svg.py          # design/logo.svg
   pwsh scripts/design/make-masters.ps1        # design/masters/**
   pwsh scripts/windows/make-app-icons.ps1     # src/KubeNimbus.App/Assets/**
   pwsh scripts/windows/make-store-logos.ps1   # design/store/**
   ```

   The bridge exists because `logo.svg` *was* the hand-edited master and drifted
   into Inkscape ids and 21 namespace attributes, the wrong class on its own
   light field, and a dark twin that no longer matched it path for path.
   Deriving the file from the drawing is what makes its claims checkable.

2. **One mark at every size, the same as pgNimbus.** Every icon, 16px
   included, is a render of `logo.svg`. There used to be a hand-drawn 24px mark
   (`logo-small.af`), a generated 16px one (`logo-micro`), dark and plated
   colourways of both, and a palette-swapped `logo-dark.svg`; they were removed
   (2026-09) because they made the taskbar icon look like a different logo, and
   pgNimbus had already shown the vector mark survives the downscale. Don't
   bring a per-size master back without writing down, in `LOGO-ASSETS.md` Part
   0, which size stopped reading and why — and never as a hand-painted PNG.

3. **The base and the broom are shared with pgNimbus and are not this repo's to
   change alone.** The plate (`r=512`), the light field (`r=360`) and the whole
   of `#brand-broom` — geometry, position in the 1024 grid, and its 39.451
   clearance halo — are identical in both marks, byte for byte. The mascot is the
   free variable and is deliberately *not* normalised. The four family rules
   and the evidence behind them are in [`design/LOGO.md`](../design/LOGO.md),
   duplicated verbatim in pgNimbus's copy; **a change to any of them is a change
   to both repositories and a pair of PRs**, the same discipline
   `shared/nimbusUi` already has. Verify a broom edit by rendering both marks
   and diffing a region containing broom and plate but no mascot: zero
   differing pixels, not "close". So are the marketing layouts:
   `make-masters.ps1` and `make-store-logos.ps1` are pgNimbus's scripts with the
   product name and tagline changed, so the social card and the Store poster
   read as one family — change the layout in both or in neither.

4. **Every surface keeps the plate.** Windows gives the taskbar, Alt+Tab and
   the title bar a *single* `WM_SETICON` slot, so `app.ico` cannot be
   theme-aware, and unplated dark line art vanishes on a dark taskbar (the
   default). The window masters and the MSIX "unplated" tiles are the plated
   mark too — a plate inside Windows's own backplate, accepted for one mark
   everywhere. `Msix/**` is packaging-time-only: the csproj marks
   `Assets/*.ico` as `AvaloniaResource` and nothing else, so the tile PNGs never
   enter the binary. `app.ico` is both the exe icon (`ApplicationIcon`) and the
   runtime window icon (loaded by `WindowIcons.cs` through `AssetLoader`, deliberately
   not through XAML's `Icon=` — see that file for the startup crash that caused).

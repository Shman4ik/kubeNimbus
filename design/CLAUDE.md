# App icon / logo assets

Full reference: [`design/LOGO-ASSETS.md`](../design/LOGO-ASSETS.md) (pipeline,
every file, every consumer); [`design/LOGO.md`](../design/LOGO.md) covers how the
mark's geometry was derived. Three rules matter here:

1. **Nothing in `design/*.svg` is hand-edited; the `.af` files are the art.**
   There are two of them — `design/logo.af` (the full mark) and
   `design/logo-small.af` (the 24px mark) — and everything else under
   `design/`, `design/store/` and `src/KubeNimbus.App/Assets/*.ico|Msix/**` is
   generated and checked in. (`design/screenshots/` comes out of
   `tools/Screenshot`, not the logo pipeline — see
   [`design/screenshots/README.md`](../design/screenshots/README.md) for the
   scenario→file mapping.) Draw in the `.af`, then run the pipeline **in this
   order** — each step eats the previous one's output:

   ```powershell
   # with the .af masters open in Affinity, run scripts/design/dump-af.js
   # through the Affinity MCP first; it dumps whichever of them are open.
   python scripts/design/af-to-svg.py          # design/logo.svg + logo-dark.svg
   python scripts/design/af-to-small-svgs.py   # design/logo-small*.svg
   python scripts/design/make-small-masters.py # design/logo-micro*.svg (only if the broom changed)
   pwsh scripts/design/make-masters.ps1        # design/masters/**
   pwsh scripts/windows/make-app-icons.ps1     # src/KubeNimbus.App/Assets/**
   pwsh scripts/windows/make-store-logos.ps1   # design/store/**  (only if the mark changed)
   ```

   The bridge exists because `logo.svg` *was* the hand-edited master and drifted
   into Inkscape ids and 21 namespace attributes, the wrong class on its own
   light field, and a `logo-dark.svg` that no longer matched it path for path
   although both headers claimed they were the same bytes with two values
   exchanged. Deriving one file from another is what makes a claim like that
   checkable.

1a. **The 24px mark is drawn and the 16px mark is generated, and that split is
   the decision to re-read before changing either.** Both used to be generated
   from `logo.svg` by `make-small-masters.py`, which invents a helm from a
   parameter table, lifts the broom's real paths, and then searches for a
   placement at which the broom clears the helm *entirely* — so what it produced
   shared its broom with the full mark and nothing else: the helm was the
   script's drawing and the composition (wheel in one corner, broom in the
   other) was not the full mark's. `logo-small.af` puts the full mark's own
   composition back — helm at the full mark's own centre, broom crossing it,
   rim and two pegs stopping at a ~45-unit clearance gap — with six rays instead
   of eight and everything heavier. At **16px** that does not survive: the cut
   rim becomes a broken ring rather than depth, and rendering the 24px drawing
   at 16 was measured against the generated micro and is worse. So
   `make-small-masters.py` is still the 16px generator and now emits only that
   trio.

1b. **The base and the broom are shared with pgNimbus and are not this repo's to
   change alone.** The plate (`r=512`), the light field (`r=360`) and the whole
   of `#brand-broom` — geometry, position in the 1024 grid, and its 39.451
   clearance halo — are identical in both marks, byte for byte. The mascot is the
   free variable and is deliberately *not* normalised: not its ink area, its
   bounding box, its line weight, or whether it crosses onto the plate (the
   elephant does and needs its halo to stay readable there; the helm does not).
   The four family rules and the evidence behind them are in
   [`design/LOGO.md`](../design/LOGO.md), duplicated verbatim in pgNimbus's copy;
   **a change to any of them is a change to both repositories and a pair of
   PRs**, the same discipline `shared/nimbusUi` already has. Verify a broom edit
   by rendering both marks and diffing a region containing broom and plate but
   no mascot: zero differing pixels, not "close". kubeNimbus's field was `r=380`
   and its broom ×1.013916 until that check was run for the first time.

2. **There are three marks, not one, and that is deliberate.** `logo.svg` (full
   mark) is rendered at 32px and up; at 24px its eight helm spokes land ~1px
   apart and at 16px the helm reads as a filled circle, so `logo-small.svg`
   (24px, six rays) and `logo-micro.svg` (16px, four rays) exist as simplified
   marks in the same 1024 grid. Rendering every size from `logo.svg` is the
   specific bug this split prevents — don't "simplify" it away. **All three
   carry the broom**: dropping it at small sizes turns the taskbar icon into a
   generic ship's wheel, which is the one place identity matters most. The
   small marks use `logo.svg`'s *own* broom paths, which is only possible
   because `#brand-broom` is self-contained. For `logo-micro.svg` that means a
   change to the full mark's broom is a re-run of the generator, not a redraw.
   **For `logo-small.af` it is a redraw**, and that is the price of drawing that
   size by hand: the 24px master holds its own moved and fattened copy of the
   broom, so a broom edit in `logo.af` reaches `logo-micro.svg` by itself and
   reaches `logo-small.svg` only when someone paste-replaces it in the `.af`.
   Family rule 2 (see 1b above) makes a broom edit a two-repository event
   already; add "and re-lift it into `logo-small.af`" to that list.
2b. **The small marks have no disc; `app.ico` still does.** Dropping the plate
   gives the mark ~40% more pixels at 16-24px and is what the unplated
   Windows/MSIX slots want. But Windows gives the taskbar, Alt+Tab and the
   title bar a *single* `WM_SETICON` slot, so `app.ico` cannot be theme-aware
   and unplated dark line art vanishes on a dark taskbar (the default). Hence
   `logo-{small,micro}-plated.svg`, which exist for `app.ico` alone. Don't
   "unify" the two — that regression is invisible until someone looks at
   their taskbar.
3. **`Msix/**` is packaging-time-only.** The csproj marks `Assets/*.ico` as
   `AvaloniaResource` and nothing else, so the tile PNGs stay source-tree
   inputs for a future MSIX pack step and never enter the binary. `app.ico` is
   both the exe icon (`ApplicationIcon`) and the runtime window icon
   (`MainWindow.axaml`'s `Icon="/Assets/app.ico"`).

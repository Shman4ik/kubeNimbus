#!/usr/bin/env python3
"""Derive the 16px mark from design/logo.svg.

    INPUT   design/logo.svg          (#broom-bristles, #broom-handle)

    OUTPUT  design/logo-micro.svg  + -dark   16px, no disc, transparent
            design/logo-micro-plated.svg     16px on the full mark's disc

**This used to produce the 24px pair as well, and no longer does.** The 24px
mark is hand-drawn now, in design/logo-small.af, and carried across by
scripts/design/af-to-small-svgs.py - see design/LOGO-ASSETS.md Part 0. What is
left here is the 16px master, which stays script-derived deliberately: at that
size the mark is four rays, no pegs and a broom, the tile is 16 pixels, and
nobody is going to gain anything by drawing that by hand. Everything below is
unchanged apart from the variant list at the bottom.

Why this exists (the long version is design/LOGO-ASSETS.md, Part 0):

  The small marks used to be hand-drawn approximations of the broom - a
  straight stroke and a four-point wedge - because the full mark had no broom
  to copy: its handle was negative space cut out of the light field, not an
  object.  Once logo.svg's #brand-broom became self-contained, the small marks
  could use the real paths, and "the small icon looks like the logo" stopped
  being a thing you eyeball and became a thing you re-run.

  So: geometry that must match the full mark is lifted from it; geometry that
  cannot survive the pixel grid (the helm's eight thin spokes, the ferrule
  notch) is regenerated at the size it has to live at.  The knobs at the bottom
  were chosen by rendering candidates at actual size and comparing, not by
  taste - if you change them, do that again.

Stdlib only, Python 3.8+.  Run it after any change to logo.svg's broom, then
rebuild the rasters:

    python scripts/design/make-small-masters.py
    pwsh scripts/design/make-masters.ps1
    pwsh scripts/windows/make-app-icons.ps1
"""
import io
import math
import os
import re

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DESIGN = os.path.join(REPO, "design")
SRC = os.path.join(DESIGN, "logo.svg")

INK = "#242B36"
PAPER = "#F5F7FA"


# --------------------------------------------------------------- path plumbing
def grab(path_id):
    svg = io.open(SRC, encoding="utf-8").read()
    m = re.search(r'id="%s"[^>]*\sd="([^"]+)"' % path_id, svg)
    if not m:
        raise SystemExit("logo.svg has no path id=%r - did the broom get renamed?" % path_id)
    return m.group(1)


def parse(d):
    """logo.svg's broom paths are absolute M/C/Z; L appears once we simplify."""
    toks = re.findall(r"[MCLZmclz]|-?\d+(?:\.\d+)?", d)
    out, i = [], 0
    while i < len(toks):
        t = toks[i]
        if t in "Zz":
            out.append(("Z", []))
            i += 1
        elif t in "Mm":
            out.append(("M", [float(toks[i + 1]), float(toks[i + 2])]))
            i += 3
        elif t in "Ll":
            out.append(("L", [float(toks[i + 1]), float(toks[i + 2])]))
            i += 3
        elif t in "Cc":
            out.append(("C", [float(x) for x in toks[i + 1:i + 7]]))
            i += 7
        else:                                   # implicit repeat of the last command
            prev = out[-1][0]
            n = 6 if prev == "C" else 2
            out.append((prev, [float(x) for x in toks[i:i + n]]))
            i += n
    return out


def flatten(segs, step=0.04):
    pts, cur, start = [], None, None
    for kind, c in segs:
        if kind == "M":
            cur = start = (c[0], c[1])
            pts.append(cur)
        elif kind == "L":
            cur = (c[0], c[1])
            pts.append(cur)
        elif kind == "C":
            p0 = cur
            for n in range(1, int(1 / step) + 1):
                t = n * step
                u = 1 - t
                pts.append((u**3 * p0[0] + 3*u*u*t * c[0] + 3*u*t*t * c[2] + t**3 * c[4],
                            u**3 * p0[1] + 3*u*u*t * c[1] + 3*u*t*t * c[3] + t**3 * c[5]))
            cur = (c[4], c[5])
        elif kind == "Z":
            if start:
                pts.append(start)
            cur = start
    return pts


def inside(poly, p):
    x, y = p
    hit = False
    j = len(poly) - 1
    for i in range(len(poly)):
        xi, yi = poly[i]
        xj, yj = poly[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi:
            hit = not hit
        j = i
    return hit


def simplify_handle(segs):
    """Replace the ferrule notch with a plain tapered tip.

    Those notches are ~20 units wide - half a pixel at 24px - and the fattening
    stroke turns them into lumps rather than detail.  The replacement tip pokes
    into the bristles so handle and head read as one object, which is what the
    ferrule was doing visually in the first place."""
    out, skipping = [], False
    for kind, c in segs:
        if kind == "C" and abs(c[4] - 470.5) < .01 and abs(c[5] - 654.7) < .01:
            out.append((kind, c))
            skipping = True
            continue
        if skipping:
            if kind == "C" and abs(c[4] - 455.8) < .01 and abs(c[5] - 616.3) < .01:
                out.append(("L", [383.0, 702.0]))
                out.append(("L", [366.0, 653.0]))
                out.append(("L", [455.8, 616.3]))
                skipping = False
            continue
        out.append((kind, c))
    return out


class Broom:
    """logo.svg's broom, fattened, in the full mark's own coordinates."""

    def __init__(self, half):
        self.half = half
        self.parts = []
        for pid in ("broom-bristles", "broom-handle"):
            segs = parse(grab(pid))
            if pid == "broom-handle":
                segs = simplify_handle(segs)
            self.parts.append((segs, flatten(segs)))

    def clearance(self, p):
        """Distance from p to the fattened broom; negative means inside it."""
        best = 1e9
        for _, poly in self.parts:
            if inside(poly, p):
                return -1.0
            for q in poly:
                d = math.hypot(q[0] - p[0], q[1] - p[1])
                if d < best:
                    best = d
        return best - self.half

    def points(self):
        for _, poly in self.parts:
            for q in poly:
                yield q

    def bbox(self):
        xs = [q[0] for q in self.points()]
        ys = [q[1] for q in self.points()]
        return (min(xs) - self.half, min(ys) - self.half,
                max(xs) + self.half, max(ys) + self.half)


# ---------------------------------------------------------------------- helm
def helm_geom(k, no_pegs):
    g = dict(hub=72 * k, rim=180 * k, rim_w=68 * k, ray=180 * k, ray_w=60 * k,
             peg0=214 * k, peg1=262 * k, peg_w=46 * k, outer=262 * k)
    if no_pegs:
        g["peg_w"] = 0.0
        g["peg0"] = g["peg1"] = g["outer"] = g["rim"] + g["rim_w"] / 2
    return g


def clear_run(broom, cx, cy, ang, r0, r1, half_w, gap):
    """How far a spoke can run outward from r0 before it meets the broom."""
    ca, sa = math.cos(ang), math.sin(ang)
    steps = max(8, int((r1 - r0) / 4))
    last = r0
    for i in range(steps + 1):
        r = r0 + (r1 - r0) * i / steps
        if broom.clearance((cx + r * ca, cy + r * sa)) < gap + half_w:
            break
        last = r
    return (r0, last) if last - r0 > half_w else None


def clear_arcs(broom, cx, cy, rim, half_w, gap):
    keep, run = [], None
    for i in range(721):
        a = math.radians(i * 0.5)
        ok = broom.clearance((cx + rim * math.cos(a), cy + rim * math.sin(a))) >= gap + half_w
        if ok and run is None:
            run = i * 0.5
        if not ok and run is not None:
            keep.append((run, i * 0.5))
            run = None
    if run is not None:
        keep.append((run, 360.0))
    if len(keep) > 1 and keep[0][0] == 0.0 and keep[-1][1] == 360.0:
        keep[0] = (keep[-1][0] - 360.0, keep[0][1])
        keep.pop()
    return keep


# --------------------------------------------------------------------- build
def build(n_rays, k, stroke, gap, margin, slide, no_pegs=False):
    """Lay the mark out and fit it to the 1024 grid. Returns a dict of parts."""
    broom = Broom(stroke / 2.0)
    hg = helm_geom(k, no_pegs)

    # The helm sits off the handle at the closest distance that leaves it
    # WHOLE. The full mark cuts a clearance gap into the helm where the broom
    # crosses; at 16-24px a chopped spoke reads as a rendering fault rather
    # than as depth, so here the broom is moved instead of the helm being cut.
    handle_poly = broom.parts[1][1]
    cx, cy = 510.8, 453.5                       # the full mark's own helm centre
    fx, fy = min(handle_poly, key=lambda p: math.hypot(p[0] - cx, p[1] - cy))
    nx, ny = cx - fx, cy - fy
    L = math.hypot(nx, ny)
    nx, ny = nx / L, ny / L
    base = (hg["rim"] + hg["rim_w"] / 2) + (23.0 + stroke / 2.0) + gap

    def place(extra):
        hx = fx + nx * (base + extra) + ny * slide
        hy = fy + ny * (base + extra) - nx * slide
        cross = math.degrees(math.atan2(fy - hy, fx - hx))
        phase = cross + 180.0 / n_rays          # broom dead centre of a spoke gap
        rays, pegs = [], []
        for i in range(n_rays):
            a = math.radians(phase + i * 360.0 / n_rays)
            r = clear_run(broom, hx, hy, a, 0, hg["ray"], hg["ray_w"] / 2, gap)
            if r:
                rays.append((a, r))
            if not no_pegs:
                p = clear_run(broom, hx, hy, a, hg["peg0"], hg["peg1"], hg["peg_w"] / 2, gap)
                if p:
                    pegs.append((a, p))
        arcs = clear_arcs(broom, hx, hy, hg["rim"], hg["rim_w"] / 2, gap)
        return dict(hx=hx, hy=hy, phase=phase, rays=rays, pegs=pegs, arcs=arcs)

    def whole(s):
        ok_rays = len(s["rays"]) == n_rays and all(abs(r[1] - hg["ray"]) < .5 for _, r in s["rays"])
        ok_pegs = no_pegs or (len(s["pegs"]) == n_rays and
                              all(abs(p[1] - hg["peg1"]) < .5 for _, p in s["pegs"]))
        ok_rim = len(s["arcs"]) == 1 and s["arcs"][0][1] - s["arcs"][0][0] >= 359.5
        return ok_rays and ok_pegs and ok_rim

    lo, hi = 0.0, 600.0
    if not whole(place(hi)):
        raise SystemExit("the helm never clears the broom - lower k")
    for _ in range(40):
        mid = (lo + hi) / 2
        if whole(place(mid)):
            hi = mid
        else:
            lo = mid
    st = place(hi)
    hx, hy = st["hx"], st["hy"]

    # fit helm + broom to the tile
    bx0, by0, bx1, by1 = broom.bbox()
    reach = hg["outer"] + hg["peg_w"] / 2
    x0, y0 = min(bx0, hx - reach), min(by0, hy - reach)
    x1, y1 = max(bx1, hx + reach), max(by1, hy + reach)
    s = (1024.0 - 2 * margin) / max(x1 - x0, y1 - y0)
    tx = margin + ((1024.0 - 2 * margin) - (x1 - x0) * s) / 2 - x0 * s
    ty = margin + ((1024.0 - 2 * margin) - (y1 - y0) * s) / 2 - y0 * s
    return dict(broom=broom, hg=hg, st=st, s=s, tx=tx, ty=ty, n_rays=n_rays,
                stroke=stroke, no_pegs=no_pegs)


def render(lay, plate_field=None):
    """Emit the body (everything after </style>), optionally on the disc."""
    broom, hg, st = lay["broom"], lay["hg"], lay["st"]
    s, tx, ty = lay["s"], lay["tx"], lay["ty"]

    if plate_field:
        # Shrink onto the full mark's disc. The fit is to the content's
        # SMALLEST enclosing circle, not to its bounding box: the mark runs
        # corner to corner, so a box fit would leave a fifth of the light
        # field empty and hand the small sizes a needlessly small mark.
        pts = [(q[0] * s + tx, q[1] * s + ty) for q in broom.points()]
        hxc, hyc = st["hx"] * s + tx, st["hy"] * s + ty
        hr = (hg["outer"] + hg["peg_w"] / 2) * s
        pts += [(hxc + hr * math.cos(math.radians(a)), hyc + hr * math.sin(math.radians(a)))
                for a in range(0, 360, 4)]
        ccx = sum(p[0] for p in pts) / len(pts)
        ccy = sum(p[1] for p in pts) / len(pts)
        for i in range(4000):                   # 1-centre by shrinking steps
            fp = max(pts, key=lambda p: math.hypot(p[0] - ccx, p[1] - ccy))
            step = 1.0 / (i + 2)
            ccx += (fp[0] - ccx) * step
            ccy += (fp[1] - ccy) * step
        far = max(math.hypot(p[0] - ccx, p[1] - ccy) for p in pts) + lay["stroke"] * s / 2
        z = plate_field * 0.97 / far   # 3% so antialiasing cannot bleed into the ring
        def X(v): return round((v * s + tx - ccx) * z + 512, 1)
        def Y(v): return round((v * s + ty - ccy) * z + 512, 1)
        def S(v): return round(v * s * z, 1)
    else:
        def X(v): return round(v * s + tx, 1)
        def Y(v): return round(v * s + ty, 1)
        def S(v): return round(v * s, 1)

    def polar(r, a):
        return (X(st["hx"] + r * math.cos(a)), Y(st["hy"] + r * math.sin(a)))

    L = []
    if plate_field:
        L.append('  <circle class="ink" fill="%s" cx="512" cy="512" r="512"/>' % INK)
        L.append('  <circle class="paper" fill="%s" cx="512" cy="512" r="%g"/>' % (PAPER, plate_field))
        L.append('')
    L.append('  <!-- Helm, whole: see the header note on why nothing is cut. -->')
    L.append('  <g class="ink-s" stroke="%s" stroke-linecap="butt" fill="none">' % INK)
    L.append('    <g stroke-width="%g">' % S(hg["rim_w"]))
    for a0, a1 in st["arcs"]:
        if a1 - a0 >= 359.5:
            L.append('      <circle cx="%g" cy="%g" r="%g"/>'
                     % (X(st["hx"]), Y(st["hy"]), S(hg["rim"])))
        else:
            p0, p1 = polar(hg["rim"], math.radians(a0)), polar(hg["rim"], math.radians(a1))
            L.append('      <path d="M%g %g A%g %g 0 %d 1 %g %g"/>'
                     % (p0[0], p0[1], S(hg["rim"]), S(hg["rim"]),
                        1 if (a1 - a0) > 180 else 0, p1[0], p1[1]))
    L.append('    </g>')
    L.append('    <g stroke-width="%g">' % S(hg["ray_w"]))
    for a, (r0, r1) in st["rays"]:
        L.append('      <path d="M%g %g L%g %g"/>' % (polar(r0, a) + polar(r1, a)))
    L.append('    </g>')
    if not lay["no_pegs"]:
        L.append('    <g stroke-width="%g">' % S(hg["peg_w"]))
        for a, (r0, r1) in st["pegs"]:
            L.append('      <path d="M%g %g L%g %g"/>' % (polar(r0, a) + polar(r1, a)))
        L.append('    </g>')
    L.append('  </g>')
    L.append('  <circle class="ink" fill="%s" cx="%g" cy="%g" r="%g"/>'
             % (INK, X(st["hx"]), Y(st["hy"]), S(hg["hub"])))
    L.append('')
    L.append('  <!-- The broom: logo.svg\'s own #broom-bristles and #broom-handle,')
    L.append('       fattened by a %g stroke so the hairline gaps around the ferrule' % S(lay["stroke"]))
    L.append('       fuse instead of turning to mud. -->')
    L.append('  <g class="ink ink-s" fill="%s" stroke="%s" stroke-width="%g" stroke-linejoin="round">'
             % (INK, INK, S(lay["stroke"])))
    for segs, _ in broom.parts:
        out = []
        for kind, c in segs:
            if kind == "Z":
                out.append("Z")
                continue
            out.append(kind + " ".join("%g,%g" % (X(c[j]), Y(c[j + 1]))
                                       for j in range(0, len(c), 2)))
        L.append('    <path d="%s"/>' % "".join(out))
    L.append('  </g>')
    return "\n".join(L)


def write(path, title_id, header, body, plated):
    style = ['  <style>', '    .ink   { fill: %s }' % INK, '    .ink-s { stroke: %s }' % INK]
    if plated:
        style.insert(2, '    .paper { fill: %s }' % PAPER)
    doc = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" role="img" aria-labelledby="%s">' % title_id,
           '  <title id="%s">kubeNimbus</title>' % title_id, '', header, ''] + style + ['  </style>', '', body, '</svg>']
    io.open(path, "w", encoding="utf-8", newline="\n").write("\n".join(doc) + "\n")
    print("wrote design/%s" % os.path.basename(path))


HEADER_MICRO = """  <!-- ============================================================
       kubeNimbus logo - SIMPLIFIED MICRO MASTER (16 px).

       GENERATED by scripts/design/make-small-masters.py from
       design/logo.svg. Do not hand-edit: re-run the script.

       The third and last master. Same no-disc, whole-helm,
       real-broom rules as logo-small.svg; two things go further
       because 16px is 16px:

       - FOUR rays, not six, and NO pegs. The pegs are what make
         the wheel read as a ship's helm rather than a gear, so
         dropping them is a real loss - but at 16px they land at
         1px, blur the rim into a fuzzy ring and cost the spokes
         the contrast they need. Checked against four/six-ray and
         with/without-peg variants rendered at actual size; this
         is the only one where rim, hub and spokes all still read.
       - The broom is fattened harder. At this size the handle has
         about one pixel to work with, and a confident single
         stroke beats a faithful thin one.

       The broom stays. 16px is where it costs the most - it eats
       roughly a third of the tile and crowds the helm - but a
       kubeNimbus icon without the Nimbus broom is a generic
       ship's-wheel icon, and that trade is not worth the extra
       clarity. If a size ever has to give the broom up, write down
       which and why, here.
       ============================================================ -->"""

HEADER_PLATED = """  <!-- ============================================================
       kubeNimbus logo - %s MASTER, PLATED (%d px).

       GENERATED by scripts/design/make-small-masters.py. Do not
       hand-edit: re-run the script.

       %s with the full mark's disc put back under it and
       the mark shrunk to fit the light field.

       This exists for one consumer: app.ico. Windows gives the
       taskbar, Alt+Tab and the title bar a single WM_SETICON slot,
       so that icon cannot be theme-aware - and unplated dark line
       art disappears on a dark taskbar, which is the default. So
       app.ico stays plated at every size, and the disc-less
       masters feed the surfaces that actually are theme-aware
       (window-icon-{light,dark}.ico, the MSIX altform-unplated
       and -lightunplated tiles).

       The plate costs what a plate costs: the mark here is smaller
       than in the disc-less master. That is the trade, not a bug.
       ============================================================ -->"""


def main():
    variants = [
        # name              rays   k     stroke gap  margin slide  no_pegs field
        ("logo-micro", dict(n_rays=4, k=1.55, stroke=78, gap=44, margin=10, slide=-70,
                            no_pegs=True), 440.0),
    ]
    for name, kw, field in variants:
        lay = build(**kw)
        header = HEADER_MICRO
        body = render(lay)
        write(os.path.join(DESIGN, name + ".svg"),
              name + "-title", header, body, plated=False)

        light = io.open(os.path.join(DESIGN, name + ".svg"), encoding="utf-8").read()
        io.open(os.path.join(DESIGN, name + "-dark.svg"), "w", encoding="utf-8",
                newline="\n").write(light.replace(INK, PAPER))
        print("wrote design/%s-dark.svg" % name)

        px, kind = 16, "MICRO"
        ph = HEADER_PLATED % (kind, px, name + ".svg")
        write(os.path.join(DESIGN, name + "-plated.svg"),
              name + "-plated-title", ph, render(lay, plate_field=field), plated=True)


if __name__ == "__main__":
    main()

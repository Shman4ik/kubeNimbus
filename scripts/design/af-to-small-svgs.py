#!/usr/bin/env python3
"""Writes the three 24px masters from design/logo-small.af.

    INPUT   ~/Desktop/kubenimbus-logo-small-dump.json
            (written by scripts/design/dump-af.js, run in Affinity)

    OUTPUT  design/logo-small.svg  + -dark   24px, no disc, transparent
            design/logo-small-plated.svg     24px on the full mark's disc

This is the same bridge design/logo.af has (dump-af.js -> af-to-svg.py), for
the second hand-drawn master. It replaced a *derivation*: the 24px mark used to
be invented by scripts/design/make-small-masters.py from a parameter table,
with the broom's paths lifted from logo.svg and a search for a placement at
which the broom cleared the helm entirely. The result shared its broom with the
full mark and nothing else - the helm was the script's drawing, not anyone's,
and its composition (wheel in one corner, broom in the other) was not the full
mark's. It is a drawing now, in the same 1024 grid as design/logo.af, and this
script only carries it across. Draw in the .af; these SVGs are overwritten.

    (1) open design/logo-small.af in Affinity
    (2) run scripts/design/dump-af.js through the Affinity MCP
    (3) python scripts/design/af-to-small-svgs.py

**The 16px mark is not here.** logo-micro.svg and its two siblings are still
derived from logo.svg by make-small-masters.py, deliberately: at 16px the mark
is four rays, no pegs and a broom on a 16-pixel tile, and hand-drawing that
buys nothing. Why the 24px one is worth a master of its own, and why neither is
design/logo.svg, is design/LOGO-ASSETS.md Part 0.

Stdlib only, Python 3.8+.
"""
import importlib.util
import json
import math
import os
import sys

sys.dont_write_bytecode = True          # no __pycache__ beside the scripts

INK = '#242B36'
PAPER = '#F5F7FA'

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DESIGN = os.path.join(REPO, 'design')
DESKTOP = os.path.join(os.path.expanduser('~'), 'Desktop')
DUMP = 'kubenimbus-logo-small-dump.json'

# The full mark's light field is r=360 in a 1024 grid; the plated twin fits the
# small mark inside a slightly larger circle than that, because it has no plate
# of its own to lose. Inherited from make-small-masters.py, where it was picked
# by rendering app.ico at 24 - it is the only per-size constant in this file.
PLATE_FIELD = 430.0

# af-to-svg.py owns the transform algebra: Affinity puts a transform wherever
# the edit was made, so a leaf-only reader silently emits untransformed
# geometry. Loaded rather than copied so the two bridges cannot disagree about
# what a transform means.
_spec = importlib.util.spec_from_file_location(
    'af_to_svg', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'af-to-svg.py'))
_af = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_af)
compose, uniform_scale, num, IDENTITY = _af.compose, _af.uniform_scale, _af.num, _af.IDENTITY


# ------------------------------------------------------------------ reading
def read_shapes(dump):
    """Every leaf of the dump as {group, name, subpaths, fill, stroke...}.

    Geometry comes out in the root coordinate system with every ancestor
    transform baked in, and a stroke width is carried through that transform
    rather than reported raw - a stroke is a width, not a point, so a scaled
    node reports its old weight and an exporter that trusted it would emit the
    wrong one.
    """
    spread = dump['children'][0]
    shapes = []
    for group in spread['children']:
        gxf = compose(spread.get('xf', IDENTITY), group.get('xf', IDENTITY))
        for node in group['children']:
            if not node.get('curves'):
                continue
            nxf = compose(gxf, node.get('xf', IDENTITY))
            a, b, tx, c, d, ty = nxf

            def pt(x, y, a=a, b=b, tx=tx, c=c, d=d, ty=ty):
                return (a * x + b * y + tx, c * x + d * y + ty)

            subpaths = []
            for curve in node['curves']:
                start = pt(*curve['start'])
                segs = [[*pt(s[0], s[1]), *pt(s[2], s[3]), *pt(s[4], s[5])]
                        for s in curve['segs']]
                subpaths.append((start, segs, curve['closed']))
            stroke = node.get('stroke') or {}
            shapes.append(dict(
                group=group['name'], name=node['name'], subpaths=subpaths,
                fill=bool(node.get('fill')),
                stroke_w=(stroke.get('w', 0.0) or 0.0) * uniform_scale(nxf),
                cap=stroke.get('cap', 0), join=stroke.get('join', 0)))
    if not shapes:
        raise SystemExit('dump has no drawable nodes')
    return shapes


# ------------------------------------------------------- geometry for the fit
def flatten(subpaths, step=0.05):
    """Curve points, dense enough for a smallest-enclosing-circle fit."""
    pts = []
    for start, segs, closed in subpaths:
        cur = start
        pts.append(cur)
        for s in segs:
            p0 = cur
            n = int(1 / step)
            for i in range(1, n + 1):
                t = i * step
                u = 1 - t
                pts.append((u**3 * p0[0] + 3*u*u*t * s[0] + 3*u*t*t * s[2] + t**3 * s[4],
                            u**3 * p0[1] + 3*u*u*t * s[1] + 3*u*t*t * s[3] + t**3 * s[5]))
            cur = (s[4], s[5])
        if closed:
            pts.append(start)
    return pts


def plate_transform(shapes, field):
    """Shrink the mark onto the full mark's disc, fitted to its SMALLEST
    ENCLOSING CIRCLE rather than to its bounding box.

    The mark runs corner to corner, so a box fit leaves about a fifth of the
    light field empty and hands app.ico's 24px entry a needlessly small mark.
    3% is held back so antialiasing cannot bleed into the ring.
    """
    clouds = [(flatten(s['subpaths']), s['stroke_w'] / 2.0) for s in shapes]
    pts = [p for cloud, _ in clouds for p in cloud]
    cx = sum(p[0] for p in pts) / len(pts)
    cy = sum(p[1] for p in pts) / len(pts)
    for i in range(4000):                       # 1-centre by shrinking steps
        fx, fy = max(pts, key=lambda p: math.hypot(p[0] - cx, p[1] - cy))
        step = 1.0 / (i + 2)
        cx += (fx - cx) * step
        cy += (fy - cy) * step
    far = max(max(math.hypot(p[0] - cx, p[1] - cy) for p in cloud) + half
              for cloud, half in clouds)
    z = field * 0.97 / far
    return lambda x, y: ((x - cx) * z + 512, (y - cy) * z + 512), z


# ------------------------------------------------------------------ emitting
CAPS = {0: 'butt', 1: 'square', 2: 'round'}
JOINS = {0: 'miter', 1: 'miter', 2: 'round', 3: 'bevel'}


def path_d(subpaths, xy):
    out = []
    for start, segs, closed in subpaths:
        out.append('M%s,%s' % tuple(num(v) for v in xy(*start)))
        for s in segs:
            p1, p2, p3 = xy(s[0], s[1]), xy(s[2], s[3]), xy(s[4], s[5])
            out.append('C%s,%s %s,%s %s,%s' % (num(p1[0]), num(p1[1]), num(p2[0]),
                                               num(p2[1]), num(p3[0]), num(p3[1])))
        if closed:
            out.append('Z')
    return ''.join(out)


def body(shapes, xy=None, z=1.0, field=None):
    xy = xy or (lambda x, y: (x, y))
    lines = []
    if field:
        lines.append('  <circle class="ink" fill="%s" cx="512" cy="512" r="512"/>' % INK)
        lines.append('  <circle class="paper" fill="%s" cx="512" cy="512" r="%g"/>' % (PAPER, field))
        lines.append('')
    group = None
    for s in shapes:
        if s['group'] != group:
            if group is not None:
                lines.append('  </g>')
            group = s['group']
            lines.append('  <g id="%s">' % group)
        cls = 'ink ink-s' if s['fill'] else 'ink-s'
        attrs = ['id="%s"' % s['name'], 'class="%s"' % cls,
                 'fill="%s"' % (INK if s['fill'] else 'none')]
        if s['stroke_w']:
            attrs += ['stroke="%s"' % INK,
                      'stroke-width="%g"' % round(s['stroke_w'] * z, 2),
                      'stroke-linecap="%s"' % CAPS.get(s['cap'], 'butt'),
                      'stroke-linejoin="%s"' % JOINS.get(s['join'], 'miter')]
        lines.append('    <path %s d="%s"/>' % (' '.join(attrs), path_d(s['subpaths'], xy)))
    lines.append('  </g>')
    return '\n'.join(lines)


def document(title_id, header, style, content):
    return '\n'.join([
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" role="img"'
        ' aria-labelledby="%s">' % title_id,
        '  <title id="%s">kubeNimbus</title>' % title_id, '', header, '',
        '  <style>'] + style + ['  </style>', '', content, '</svg>']) + '\n'


HEADER = """  <!-- ============================================================
       kubeNimbus logo - SIMPLIFIED SMALL MASTER (24 px).

       GENERATED from design/logo-small.af by
       scripts/design/af-to-small-svgs.py. Do not hand-edit: draw
       in the .af and re-run it.

       Not a downscale of the full mark. At 24px design/logo.svg's
       traced light field, its eight thin helm spokes and the
       broom's ferrule notch all fall under one pixel and collapse
       into mud (design/LOGO-ASSETS.md, "Why there are three
       masters"). This is the full mark's own COMPOSITION - the
       helm at the full mark's own centre, the Nimbus broom
       crossing it from the lower left, the rim and two pegs
       stopping at a clearance gap - drawn with fewer, heavier
       parts so it survives the pixel grid. Six spokes, not eight:
       at 24px eight land about a pixel apart.

       NO DISC. The full mark's disc is a plate, and a plate costs
       ~20% of the tile in each direction at every size. This
       master is one colour on transparency and gets the whole
       tile - which is what the unplated Windows and MSIX icon
       slots want anyway. It cannot carry its own background, so on
       a surface the ink will not survive, logo-small-dark.svg is
       not optional, it is the asset. The plated twin for app.ico
       is logo-small-plated.svg.

       The broom is design/logo.af's OWN geometry, fattened by a
       stroke, not a redrawn wedge - so the small mark and the full
       mark are the same broom. Its ferrule notch and the grip slot
       in the handle are dropped: both are ~20 units wide, half a
       pixel here, and the fattening turns them into lumps rather
       than detail.
       ============================================================ -->"""

HEADER_PLATED = """  <!-- ============================================================
       kubeNimbus logo - SMALL MASTER, PLATED (24 px).

       GENERATED from design/logo-small.af by
       scripts/design/af-to-small-svgs.py. Do not hand-edit.

       logo-small.svg with the full mark's disc put back under it
       and the mark shrunk to fit the light field.

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


def write(path, text):
    with open(path, 'w', encoding='utf-8', newline='\n') as fh:
        fh.write(text)
    print('wrote design/%s (%d bytes)' % (os.path.basename(path), len(text)))


def main():
    src = os.path.join(sys.argv[1] if len(sys.argv) > 1 else DESKTOP, DUMP)
    if not os.path.exists(src):
        raise SystemExit('dump not found: %s\n'
                         'Open design/logo-small.af and run scripts/design/dump-af.js '
                         'in Affinity first.' % src)
    shapes = read_shapes(json.load(open(src, encoding='utf-8')))

    flat = document('logo-small-title', HEADER,
                    ['    .ink   { fill: %s }' % INK,
                     '    .ink-s { stroke: %s }' % INK],
                    body(shapes))
    write(os.path.join(DESIGN, 'logo-small.svg'), flat)
    write(os.path.join(DESIGN, 'logo-small-dark.svg'), flat.replace(INK, PAPER))

    xy, z = plate_transform(shapes, PLATE_FIELD)
    write(os.path.join(DESIGN, 'logo-small-plated.svg'),
          document('logo-small-plated-title', HEADER_PLATED,
                   ['    .ink   { fill: %s }' % INK,
                    '    .paper { fill: %s }' % PAPER,
                    '    .ink-s { stroke: %s }' % INK],
                   body(shapes, xy, z, PLATE_FIELD)))


if __name__ == '__main__':
    main()

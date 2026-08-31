#!/usr/bin/env python3
"""Writes design/logo.svg from a design/logo.af dump.

The .af is where the mark is drawn; this SVG is the committed master that every
other asset is generated from (see design/LOGO-ASSETS.md). Keeping the bridge in
a script rather than in someone's export settings is what stops the two from
drifting -- which is exactly what happened to this mark before: logo.svg had
been round-tripped through Inkscape until its inner circle carried the wrong
class, its ids were Inkscape's, and logo-dark.svg no longer matched it path for
path.

    (1) open design/logo.af in Affinity
    (2) run scripts/design/dump-af.js through the Affinity MCP
    (3) python scripts/design/af-to-svg.py [dump.json]

This is pgNimbus's script with one behavioural difference, and it matters:
**ancestor transforms are accumulated**, not just the leaf's own. pgNimbus's
copy reads a single node's `xf`, which is correct only because every transform
in that document happens to sit on a leaf. Affinity puts a transform wherever
the edit was made -- transform a group and the group carries it -- so a
leaf-only reader silently drops it and emits the untransformed geometry. That
is a wrong file with no error, so this one walks the chain.

What it guarantees about the output, because these are the rules that make the
file liftable into a sibling mark (pgNimbus shares the broom):

  * plain <path> geometry in the root coordinate system - every node transform
    is baked into the numbers, so there is no transform/mask/use anywhere;
  * the two base circles stay <circle>, because make-masters.ps1 builds the
    transparent glyph by stripping the full-bleed one, matched on r="512";
  * colour is the two classes .ink / .paper, with the value repeated as a plain
    attribute so tools that ignore <style> still render, and so a host page can
    retheme the mark without touching the geometry.
"""
import json
import os
import sys

INK = '#242B36'
PAPER = '#F5F7FA'

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_DUMP = os.path.join(os.path.expanduser('~'), 'Desktop', 'kubenimbus-logo-dump.json')

# The one path that is paper rather than ink. Everything else about a node - its
# name, its geometry, its module - comes from the dump.
PAPER_PATHS = {'broom-grip-slot'}

HEADER = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" role="img" aria-labelledby="logo-title">
  <title id="logo-title">kubeNimbus</title>

  <!-- ============================================================
       kubeNimbus logo - flattened master.

       GENERATED from design/logo.af by scripts/design/af-to-svg.py.
       Draw in the .af; this file is overwritten, so hand edits here
       are lost the next time anyone regenerates.

       Every module is plain <path> geometry in the root coordinate
       system: no <mask>, no <use>, no transform, no CSS variables.
       That is what makes it survive Inkscape / Illustrator / Figma
       and what makes each group liftable into a sibling mark.

       #base         the plate and the light field it encloses
       #mascot-helm  the ship's helm (pgNimbus: an elephant)
       #brand-broom  the Nimbus broom, shared across the family

       THE BROOM IS SHARED WITH pgNimbus, BYTE FOR BYTE. Same size,
       same place in the 1024 grid, same 39.451 clearance halo. It
       used to be 1.4% larger here than there, because one uniform
       resize had been applied to this copy and never reconciled;
       the two are now one drawing in two files. A change to the
       broom is a change to both repositories - see design/LOGO.md.

       THE BROOM IS ALSO SELF-CONTAINED. It used to be a set of
       fragments that only read as a broom because #base's light
       field had the handle cut out of it as negative space: hide
       #base and the handle vanished, because it was never an
       object - it was a hole. Now #brand-broom carries every part
       it needs, including its own clearance halo.

       Two consequences worth knowing before editing:

       - #base's light field is a clean arc. The notch that used
         to spell the handle, and the wiggles that used to spell
         the halo around the bristles, are gone - the broom draws
         both itself. Do not "restore" them; you would be drawing
         the broom twice.
       - #mascot-helm still has the broom's clearance gap baked
         into its outline (the flattened path cannot be un-baked
         without redrawing the helm parametrically). It is
         invisible, because #broom-clearance covers it. But the
         helm alone is not yet liftable the way the broom is, so
         it is this mark's one outstanding exception to family
         rule 3 in design/LOGO.md.

       The handle is ONE closed path from ferrule to tip, not a
       shaft plus a grip: two abutting fills leave an antialiasing
       hairline right where the eye follows the stroke. The grip's
       slot is .paper punched over the .ink, so the whole handle
       reads as one dark object with a white outline, which is what
       lets it cross the dark ring and stay legible.

       Colour is two classes with the value repeated as a plain
       attribute, so tools that ignore <style> still render; CSS
       wins where it is honoured, so a host page can retheme the
       mark without touching the geometry. logo-dark.svg is this
       file with the two values exchanged, and it is generated by
       the same script rather than hand-maintained - the two had
       silently diverged path for path when it was not.
       ============================================================ -->

  <style>
    .ink     { fill: INK_COLOUR }
    .paper   { fill: PAPER_COLOUR }
    .paper-s { stroke: PAPER_COLOUR }
  </style>
""".replace('INK_COLOUR', INK).replace('PAPER_COLOUR', PAPER)

IDENTITY = [1, 0, 0, 0, 1, 0]


def compose(outer, inner):
    """outer o inner, both as Affinity's [a, b, tx, c, d, ty] row layout."""
    a1, b1, t1, c1, d1, u1 = outer
    a2, b2, t2, c2, d2, u2 = inner
    return [
        a1 * a2 + b1 * c2,
        a1 * b2 + b1 * d2,
        a1 * t2 + b1 * u2 + t1,
        c1 * a2 + d1 * c2,
        c1 * b2 + d1 * d2,
        c1 * t2 + d1 * u2 + u1,
    ]


def uniform_scale(xf):
    """The scale factor of a transform, for carrying a stroke width through it.

    A stroke is a width, not a point, so baking the geometry has to take the
    stroke with it or the halo comes out the wrong weight. Refuses anything but
    a uniform scale: a non-uniform one has no single stroke width to report, and
    guessing would produce a plausible file that is quietly wrong.
    """
    a, b, _, c, d, _ = xf
    sx = (a * a + c * c) ** 0.5
    sy = (b * b + d * d) ** 0.5
    if abs(sx - sy) > 1e-6:
        raise SystemExit('non-uniform scale (%g x %g) cannot carry a stroke width' % (sx, sy))
    return sx


def num(v):
    """Two decimals, trailing zeros trimmed - the format the master already used."""
    s = '%.2f' % v
    s = s.rstrip('0').rstrip('.')
    return '0' if s in ('-0', '') else s


def path_data(node, parent_xf):
    """The node's curves with every transform above it baked into the numbers."""
    a, b, tx, c, d, ty = compose(parent_xf, node.get('xf', IDENTITY))

    def pt(x, y):
        return (a * x + b * y + tx, c * x + d * y + ty)

    out = []
    for curve in node['curves']:
        sx, sy = pt(*curve['start'])
        out.append('M%s,%s' % (num(sx), num(sy)))
        for s in curve['segs']:
            p1, p2, p3 = pt(s[0], s[1]), pt(s[2], s[3]), pt(s[4], s[5])
            out.append('C%s,%s %s,%s %s,%s' % (num(p1[0]), num(p1[1]),
                                               num(p2[0]), num(p2[1]),
                                               num(p3[0]), num(p3[1])))
        if curve['closed']:
            out.append('Z')
    return ''.join(out)


def circle(node):
    """A base disc, kept as <circle> so make-masters.ps1 can find it by radius.

    Read off the spread base box, which already has every transform applied -
    that is what lets the field be resized in the .af without this script
    having to understand how the ellipse was edited.
    """
    x, y, w, h = node['box']
    if abs(w - h) > 0.01:
        raise SystemExit('%s is not circular: %s' % (node['name'], node['box']))
    return num(x + w / 2), num(y + h / 2), num(w / 2)


def build(dump):
    spread = dump['children'][0]
    out = [HEADER]
    for group in spread['children']:
        gxf = compose(spread.get('xf', IDENTITY), group.get('xf', IDENTITY))
        out.append('\n  <g id="%s">' % group['name'])
        for node in group['children']:
            nxf = compose(gxf, node.get('xf', IDENTITY))
            halos = node.get('children') or []
            if halos and 'clearance' in node['name']:
                weights = [h['stroke']['w'] * uniform_scale(compose(nxf, h.get('xf', IDENTITY)))
                           for h in halos]
                weight = weights[0]
                for w in weights[1:]:
                    if abs(w - weight) > 1e-4:
                        raise SystemExit('%s: halo weights differ (%g vs %g)'
                                         % (node['name'], w, weight))
                out.append('    <g id="%s" class="paper paper-s" fill="%s" stroke="%s"'
                           ' stroke-width="%g" stroke-linejoin="round" stroke-linecap="round">'
                           % (node['name'], PAPER, PAPER, round(weight, 3)))
                for halo in halos:
                    out.append('      <path d="%s"/>' % path_data(halo, nxf))
                out.append('    </g>')
            elif not node.get('curves'):
                continue
            elif node['type'] == 'ShapeNode':
                cx, cy, r = circle(node)
                is_paper = (node.get('fill') or '').upper().startswith(PAPER)
                out.append('    <circle class="%s" fill="%s" cx="%s" cy="%s" r="%s"/>'
                           % ('paper' if is_paper else 'ink', PAPER if is_paper else INK, cx, cy, r))
            else:
                is_paper = node['name'] in PAPER_PATHS
                out.append('    <path id="%s" class="%s" fill="%s" d="%s"/>'
                           % (node['name'], 'paper' if is_paper else 'ink',
                              PAPER if is_paper else INK, path_data(node, gxf)))
        out.append('  </g>')
    out.append('</svg>')
    return '\n'.join(out) + '\n'


def swap_palette(svg):
    """logo-dark.svg: the same bytes with the two colour values exchanged.

    That has always been the stated contract, and it stopped being true - the
    light file had been round-tripped through Inkscape while the dark one had
    not, so they disagreed path for path, the light one's field carried the
    wrong class, and the documented "replace two values" regeneration would
    have missed a lowercase spelling and turned the field dark. Generating it
    is what makes the contract enforceable rather than aspirational.

    Unlike pgNimbus - which dropped its dark twin, because a plated mark
    carries its own contrast - kubeNimbus still needs one: make-masters.ps1
    strips the disc from it for the light-surface window icon, and uses it for
    the dark wordmark. Both consume the mark *without* the plate, which is
    exactly the case a single colourway cannot serve.
    """
    marker = '\x00SWAP\x00'
    return svg.replace(INK, marker).replace(PAPER, INK).replace(marker, PAPER)


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DUMP
    if not os.path.exists(src):
        raise SystemExit('dump not found: %s\n'
                         'Run scripts/design/dump-af.js in Affinity first.' % src)
    svg = build(json.load(open(src, encoding='utf-8')))

    for name, text in (('logo.svg', svg), ('logo-dark.svg', swap_palette(svg))):
        dst = os.path.join(REPO, 'design', name)
        with open(dst, 'w', encoding='utf-8', newline='\n') as fh:
            fh.write(text)
        print('wrote design/%s (%d bytes)' % (name, len(text)))


if __name__ == '__main__':
    main()

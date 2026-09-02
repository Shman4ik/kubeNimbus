// Dumps the geometry of every kubeNimbus .af master to JSON, for
// scripts/design/af-to-svg.py (the full mark) and
// scripts/design/af-to-small-svgs.py (the 24 px mark).
//
// The .af files are where the marks are drawn; the SVGs beside them are the
// committed, tool-neutral copies. This is the bridge. Run it through the
// Affinity MCP (execute_script) with the masters open, then run the two Python
// scripts on the files it writes.
//
// Every master that is open is dumped and every one that is not is skipped, so
// a session that only touched one mark can run this unchanged. Documents are
// selected by the repository directory as well as the filename: pgNimbus's
// master is also called logo.af and the two are routinely open side by side, so
// picking by filename alone dumps whichever the editor happens to list first,
// which is silent and wrong.
//
// Affinity scripts can only write to the Desktop, hence the destination.
const { app } = require('/application.js');
const { SolidFill } = require('/fills.js');
const { File } = require('/fs.js');

// There is deliberately no logo-micro.af: the 16 px mark stays script-derived
// from logo.svg (scripts/design/make-small-masters.py). See design/LOGO-ASSETS.md.
const MASTERS = [
    { tail: 'kubeNimbus\\design\\logo.af',       out: 'kubenimbus-logo-dump.json' },
    { tail: 'kubeNimbus\\design\\logo-small.af', out: 'kubenimbus-logo-small-dump.json' },
];

const kids = (n) => { try { return Array.from(n.children); } catch (e) { return []; } };
const nm   = (n) => { try { return n.description ?? ''; } catch (e) { return '?'; } };
const r    = (v) => Math.round(v * 10000) / 10000;
// Transforms get more precision than coordinates, because a transform is a
// multiplier: rounding a scale to 4dp moves every point it touches, and it
// carries the clearance stroke with it (40 x 0.9863 is 39.452, not 39.451 -
// a re-run would quietly re-drift the number this pass exists to pin).
const rxf  = (v) => Math.round(v * 1e9) / 1e9;
const h2   = (v) => ('0' + Math.round(v).toString(16)).slice(-2).toUpperCase();

function hex(desc) {
    try {
        const f = desc?.fill; if (!f) return null;
        const c = SolidFill.fromFill(f).colour.getRGBA8(true, null);
        return '#' + h2(c.r) + h2(c.g) + h2(c.b) + (c.alpha < 255 ? '/' + c.alpha : '');
    } catch (e) { return null; }
}

function dumpCurve(c) {
    const segs = [];
    const s = c.getPoint(c.firstOnCurvePointIndex);
    for (const b of c.beziers)
        segs.push([r(b.c1.x), r(b.c1.y), r(b.c2.x), r(b.c2.y), r(b.end.x), r(b.end.y)]);
    return { start: [r(s.x), r(s.y)], segs, closed: c.isClosed };
}

function dumpNode(n) {
    const o = { name: nm(n), type: n[Symbol.toStringTag] };
    try { const b = n.getExactSpreadBaseBox(); if (b) o.box = [r(b.x), r(b.y), r(b.width), r(b.height)]; } catch (e) {}
    try { o.xf = Array.from(n.transform.data).map(rxf); } catch (e) {}
    try { o.visible = n.isVisible; } catch (e) {}
    const fill = hex(n.brushFillDescriptor); if (fill) o.fill = fill;
    try {
        if (n.lineWeight) o.stroke = { w: r(n.lineWeight), cap: n.lineCap.value ?? n.lineCap,
                                       join: n.lineJoin.value ?? n.lineJoin, colour: hex(n.penFillDescriptor) };
    } catch (e) {}
    try {
        const pc = n.polyCurve;
        if (pc && pc.curveCount) { o.curves = []; for (let i = 0; i < pc.curveCount; i++) o.curves.push(dumpCurve(pc.at(i))); }
    } catch (e) {}
    o.children = kids(n).map(dumpNode);
    return o;
}

let dumped = 0;
for (const m of MASTERS) {
    const doc = app.documents.all.find(d => String(d.path).endsWith(m.tail));
    if (!doc) { console.log('skipped ' + m.tail + ' (not open)'); continue; }
    const out = JSON.stringify(dumpNode(doc.rootNode));
    const dest = app.userDesktopPath + '\\' + m.out;
    const f = new File(dest, 'wb');
    f.writeStringAsUtf8(out);
    f.close();
    console.log('wrote ' + dest + ' (' + out.length + ' bytes)');
    dumped++;
}
if (!dumped) throw new Error('none of the kubeNimbus .af masters are open');

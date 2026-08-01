// Build the kubeNimbus logo from its source raster. See ../LOGO.md.
//   node design/tools/build-logo.js [source.png] [outDir]
const fs = require('fs');
const path = require('path');
const { decode } = require('./png');
const { contours, rdp, fitClosed, toPath, area, bbox } = require('./trace');

const HERE = path.join(__dirname, '..');
const SRC = process.argv[2] || path.join(HERE, 'Gemini_Generated_Image_bju2ipbju2ipbju2.png');
const OUT = process.argv[3] || HERE;
const img = decode(SRC);
const { w, h, lum } = img;

// ---------------------------------------------------------------------
// Measured off THIS raster (design/tools/measure.js prints them all).
// Re-derive every number below before pointing this at another mark.
// ---------------------------------------------------------------------
const CX = 511.6, CY = 511.6, R_OUT = 460.7, R_IN = 327.5;

// Full bleed: everything is measured in raster pixels, then mapped through T on
// the way out so the disc lands exactly on cx=cy=r=512 - the mark then runs to
// all four edges instead of sitting in 51px of margin.
const SCALE = 512 / R_OUT;
const T = ([x, y]) => [(x - CX) * SCALE + 512, (y - CY) * SCALE + 512];
const TP = pts => pts.map(T);
const s = v => +(v * SCALE).toFixed(1);

// ---- connected components -------------------------------------------
const lab = new Int32Array(w * h).fill(-1);
const comps = [];
for (let i = 0; i < w * h; i++) {
  if (lum[i] >= 128 || lab[i] >= 0) continue;
  const id = comps.length; const st = [i]; lab[i] = id;
  let n = 0, x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
  while (st.length) {
    const p = st.pop(); n++; const x = p % w, y = (p / w) | 0;
    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
    for (const q of [p - 1, p + 1, p - w, p + w]) {
      if (q < 0 || q >= w * h || Math.abs((q % w) - x) > 1) continue;
      if (lum[q] < 128 && lab[q] < 0) { lab[q] = id; st.push(q); }
    }
  }
  comps.push({ id, n, x0, y0, x1, y1 });
}
const wheelC = comps.find(c => c.n > 20000 && c.x0 > 280 && c.x1 < 700);
const ringC = comps.find(c => c.n > 200000);
const W = (x, y) => (x < 0 || y < 0 || x >= w || y >= h) ? false : lab[(y | 0) * w + (x | 0)] === wheelC.id;

// ---- fit the wheel's rim --------------------------------------------
// dark/light runs along one ray
function runs(px, py, th) {
  const dx = Math.cos(th), dy = Math.sin(th), out = [];
  let cur = null;
  for (let t = 30; t <= 260; t += 0.5) {
    const d = W(px + dx * t, py + dy * t);
    if (!cur || cur.dark !== d) { cur = { dark: d, r0: t, r1: t }; out.push(cur); }
    else cur.r1 = t;
  }
  return out;
}
// the rim = a dark run ending in [105,180] that a clear light gap precedes
function rimAt(px, py, th) {
  const rs = runs(px, py, th);
  for (let i = rs.length - 1; i > 0; i--) {
    const r = rs[i], prev = rs[i - 1];
    if (!r.dark || r.r1 < 105 || r.r1 > 180 || r.r1 - r.r0 < 8) continue;
    if (prev.dark || prev.r1 - prev.r0 < 8) continue;
    return { inner: r.r0, outer: r.r1 };
  }
  return null;
}
function rimStats(px, py) {
  const o = [], inn = [];
  for (let a = 0; a < 720; a++) {
    const v = rimAt(px, py, a * Math.PI / 360);
    if (v) { o.push(v.outer); inn.push(v.inner); }
  }
  if (o.length < 60) return null;
  const med = arr => { const s = arr.slice().sort((x, y) => x - y); return s[s.length >> 1]; };
  const mo = med(o), mi = med(inn);
  const inl = o.filter(v => Math.abs(v - mo) < 8);
  let s = 0; for (const v of inl) s += (v - mo) ** 2;
  return { outer: mo, inner: mi, rms: Math.sqrt(s / inl.length), n: inl.length, tot: o.length };
}
let C = { x: 510.5, y: 458 };
for (const step of [4, 2, 1]) {
  let best = null, moved = true, guard = 0;
  while (moved && guard++ < 6) {
    moved = false;
    for (let ox = -step * 4; ox <= step * 4; ox += step) for (let oy = -step * 4; oy <= step * 4; oy += step) {
      const s = rimStats(C.x + ox, C.y + oy); if (!s) continue;
      const key = s.rms - s.n * 0.003;
      if (!best || key < best.key) { best = { key, x: C.x + ox, y: C.y + oy, s }; moved = true; }
    }
    if (best) C = { x: best.x, y: best.y };
  }
}
const st = rimStats(C.x, C.y);
console.log('wheel centre  %s %s', C.x.toFixed(1), C.y.toFixed(1));
console.log('rim  inner=%s outer=%s  (rms %s, %d/%d angles)',
  st.inner.toFixed(1), st.outer.toFixed(1), st.rms.toFixed(2), st.n, st.tot);
const outer = { med: st.outer }, inner = { med: st.inner };
// handle tips: dark runs beyond the rim
const tips = [];
for (let a = 0; a < 720; a++) {
  const rs = runs(C.x, C.y, a * Math.PI / 360);
  for (const r of rs) if (r.dark && r.r0 > st.outer + 2) tips.push(r.r1);
}
tips.sort((a, b) => b - a);
const hmax = tips.length ? tips[Math.floor(tips.length * 0.15)] : st.outer + 55;
console.log('handle tip r=%s  (from %d samples)', hmax.toFixed(1), tips.length);

// ---- shaft (the broom where it crosses the disc), for the clearance mask ----
const lum2 = new Uint8Array(w * h).fill(255);
for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
  const i = y * w + x;
  if (lab[i] === ringC.id && Math.hypot(x - CX, y - CY) < R_IN - 2) lum2[i] = lum[i];
}
const shaftPath = contours(lum2, w, h)
  .map(p => ({ p, a: Math.abs(area(p)) })).sort((a, b) => b.a - a.a)[0];
const shaftD = toPath(fitClosed(rdp(TP(shaftPath.p), 0.45), 1.1));
console.log('shaft contour pts=%d area=%d', shaftPath.p.length, Math.round(shaftPath.a));

// The broom hides the wheel completely on its far side, so the clearance is
// not just a band: everything beyond the broom has to go, or a clipped handle
// is left floating. Fit the broom's axis (PCA over the shaft pixels near the
// wheel) and cut the half-plane past it.
const sp = [];
for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
  const i = y * w + x;
  if (lab[i] === ringC.id && Math.hypot(x - CX, y - CY) < R_IN - 2
    && Math.hypot(x - 510.5, y - 459) < 235) sp.push([x, y]);
}
let mx = 0, my = 0;
for (const [x, y] of sp) { mx += x; my += y; } mx /= sp.length; my /= sp.length;
let sxx = 0, syy = 0, sxy = 0;
for (const [x, y] of sp) { const a = x - mx, b = y - my; sxx += a * a; syy += b * b; sxy += a * b; }
const theta = 0.5 * Math.atan2(2 * sxy, sxx - syy);          // principal axis
const nx = -Math.sin(theta), ny = Math.cos(theta);           // unit normal
// The half-plane has to meet the widened outline seamlessly, so take the
// NARROWEST far edge along the broom (per slice), not the widest - otherwise a
// sliver of wheel survives between the two.
const GAP = s(17), STEP = s(36), COPIES = 12;                // shadow: ~430px, ample
const shadow = [];
for (let k = 1; k <= COPIES; k++)
  shadow.push(`        <use href="#broom-edge" transform="translate(${(nx * STEP * k).toFixed(1)} ${(ny * STEP * k).toFixed(1)})"/>`);
const AXIS_DEG = +(theta * 180 / Math.PI).toFixed(2);
console.log('broom axis %s deg through %s,%s ; shadow normal %s,%s',
  AXIS_DEG, mx.toFixed(1), my.toFixed(1), nx.toFixed(3), ny.toFixed(3));

// ---- traced base + broom (the wheel component's contours are dropped) ----
const items = contours(lum, w, h).map(pts => {
  const a = area(pts), b = bbox(pts);
  return { pts, a, abs: Math.abs(a), b, ink: a < 0 };
}).sort((x, y) => y.abs - x.abs);
const isWheel = it => it.b.x0 > 280 && it.b.x1 < 700 && it.b.y0 > 220 && it.b.y1 < 600;
const base = [], broom = [];
for (const it of items) {
  if (isWheel(it)) continue;
  (it.abs > 200000 ? base : broom).push(it);
}
function circularity(pts) {                       // about the canvas centre
  const rs = pts.map(([x, y]) => Math.hypot(x - 512, y - 512));
  const m = rs.reduce((a, v) => a + v, 0) / rs.length;
  let mx = 0; for (const r of rs) mx = Math.max(mx, Math.abs(r - m));
  return { r: m, dev: mx };
}
const el = it => {
  const pts = TP(it.pts);
  const c = circularity(pts);
  const fill = it.ink ? 'var(--ink)' : 'var(--paper)';
  if (c.dev < 1.4) return `    <circle cx="512" cy="512" r="${c.r.toFixed(1)}" fill="${fill}"/>`;
  return `    <path fill="${fill}" d="${toPath(fitClosed(rdp(pts, 0.3), 0.8))}"/>`;
};

// ---- the rebuilt wheel ----------------------------------------------
const RO = s(outer.med), RI = s(inner.med);
const RIM_MID = +((RO + RI) / 2).toFixed(1), RIM_W = +(RO - RI).toFixed(1);
const HUB = s(45), BORE = s(23), SPOKE = s(30), HAND_W = s(30), HAND_TIP = s(hmax);
const WC = T([C.x, C.y]);
// The 45deg grid, except in the broom's shadow: 180deg is dropped and its two
// neighbours are pulled in towards it (135 -> 150, 225 -> 210), so the wedge
// the broom cuts holds two spokes instead of three. Everything the eye reads as
// the wheel - 0/45/90/270/315 - stays exactly on the grid; only the arms that
// are already truncated by #broom-clearance move, and they move closer
// together, which is what stops the stubs from reading as debris. Deliberately
// not a true helm any more; see LOGO.md, "The lower spokes".
const ARM_DEG = [0, 45, 90, 150, 210, 270, 315];
const arms = ARM_DEG.map(a => `      <use href="#helm-arm" transform="rotate(${a})"/>`);

const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" role="img" aria-labelledby="t">
  <title id="t">kubeNimbus</title>

  <!-- ============================================================
       kubeNimbus logo.

       #base and #brand-broom are traced from
       design/Gemini_Generated_Image_bju2ipbju2ipbju2.png at the 128
       iso-level (sub-pixel marching squares, cubic-Bezier fit to within
       0.75px), so they are the source artwork's own geometry.

       #mascot-helm is NOT traced: the generated wheel had 7 spokes at
       44-60deg spacing and only half its handles, so it is rebuilt from
       the measured radii (rim ${RI}-${RO}, handles to ${HAND_TIP}, centre
       ${WC[0].toFixed(0)},${WC[1].toFixed(0)}) on an exact 45deg grid -
       symmetric by construction rather than by hand-placed nodes.

       Arms sit at ${ARM_DEG.join(', ')}deg. Everything outside the broom's
       shadow is on the exact 45deg grid; inside it the 180deg arm is dropped
       and its neighbours pulled together (135->150, 225->210) so the wedge the
       broom cuts carries two spokes rather than three stubs.

       The whole mark is scaled ${SCALE.toFixed(3)}x off the source so the disc
       runs edge to edge: cx=cy=r=512, no margin.

       The broom passes in front of the wheel with a light gap around it,
       so the wheel is masked by #broom-clearance: the broom's own outline
       widened by 17px, the gap measured off the source.
       ============================================================ -->
  <style>
    svg { --ink: #242B36; --paper: #F5F7FA; }
  </style>

  <defs>
    <!-- one arm: spoke + handle, pointing up -->
    <g id="helm-arm">
      <rect x="${-SPOKE / 2}" y="${-RIM_MID}" width="${SPOKE}" height="${RIM_MID - HUB + 6}" rx="${SPOKE / 2}"/>
      <rect x="${-HAND_W / 2}" y="${-HAND_TIP}" width="${HAND_W}" height="${HAND_TIP - RO + 10}" rx="${HAND_W / 2}"/>
    </g>
    <!-- the broom where it crosses the disc -->
    <path id="broom-edge" d="${shaftD}"/>

    <!-- Where the wheel must not paint: the broom itself widened by the ${GAP}px
         gap measured off the source, plus everything behind it - the broom hides
         the wheel completely on its far side, and a bare band would leave a
         clipped handle floating out there. The far side is the broom's own
         outline stepped along its normal (${AXIS_DEG}deg axis), so the cut
         follows the broom's curve instead of a straight line. -->
    <mask id="broom-clearance">
      <rect width="1024" height="1024" fill="#fff"/>
      <g fill="#000" stroke="#000" stroke-width="${GAP * 2}" stroke-linejoin="round">
        <use href="#broom-edge"/>
${shadow.join('\n')}
      </g>
    </mask>
  </defs>

  <!-- Base: the ring and the light field it encloses. -->
  <g id="base">
${base.map(el).join('\n')}
  </g>

  <!-- Product mascot: the ship's helm (pgNimbus uses an elephant here). -->
  <!-- the mask lives on the outer <g>: a transform on the same element as a
       mask would move the mask with it -->
  <g id="mascot-helm" mask="url(#broom-clearance)">
    <g fill="var(--ink)" transform="translate(${WC[0].toFixed(1)} ${WC[1].toFixed(1)})">
      <circle r="${RIM_MID}" fill="none" stroke="var(--ink)" stroke-width="${RIM_W}"/>
      <g id="helm-arms">
${arms.join('\n')}
      </g>
      <path id="helm-hub" fill-rule="evenodd"
            d="M0,-${HUB} A${HUB},${HUB} 0 1,1 0,${HUB} A${HUB},${HUB} 0 1,1 0,-${HUB} Z
               M0,-${BORE} A${BORE},${BORE} 0 1,1 0,${BORE} A${BORE},${BORE} 0 1,1 0,-${BORE} Z"/>
    </g>
  </g>

  <!-- Brand emblem: the Nimbus broom, shared across the family. -->
  <g id="brand-broom">
${broom.map(el).join('\n')}
  </g>
</svg>
`;
const TOKENS = '--ink: #242B36; --paper: #F5F7FA';
fs.writeFileSync(path.join(OUT, 'logo.svg'), svg);
fs.writeFileSync(path.join(OUT, 'logo-dark.svg'),
  svg.replace(TOKENS, '--ink: #F5F7FA; --paper: #242B36'));
console.log('\nrim mid %s width %s ; bytes %d -> %s', RIM_MID, RIM_W, svg.length, OUT);

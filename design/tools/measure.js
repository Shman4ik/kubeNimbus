// Print every number build-logo.js hard-codes, straight off the raster.
// Run this FIRST when pointing the pipeline at a different mark.
//   node design/tools/measure.js [source.png]
const path = require('path');
const { decode } = require('./png');

const SRC = process.argv[2] ||
  path.join(__dirname, '..', 'Gemini_Generated_Image_bju2ipbju2ipbju2.png');
const { w, h, lum } = decode(SRC);
const D = (x, y) => (x < 0 || y < 0 || x >= w || y >= h) ? false : lum[(y | 0) * w + (x | 0)] < 128;
console.log('source %s  %dx%d', path.basename(SRC), w, h);

// ---- 1. the outer disc ----------------------------------------------
// Cast rays and take the last dark hit per angle. Median + inlier filter, so
// anything sticking out past the disc (a broom, a tail) cannot drag the fit.
function radii(cx, cy) {
  const out = [];
  for (let a = 0; a < 720; a++) {
    const th = a * Math.PI / 360, dx = Math.cos(th), dy = Math.sin(th);
    let last = -1;
    for (let r = 100; r < Math.min(w, h) / 2; r += 0.5) if (D(cx + dx * r, cy + dy * r)) last = r;
    if (last > 0) out.push(last);
  }
  return out;
}
function score(cx, cy) {
  const r = radii(cx, cy).sort((a, b) => a - b);
  const m = r[r.length >> 1];
  const inl = r.filter(v => Math.abs(v - m) < 6);
  let s = 0; for (const v of inl) s += (v - m) ** 2;
  return { med: m, rms: Math.sqrt(s / inl.length), n: inl.length };
}
let cx = 0, cy = 0, n = 0;
for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (D(x, y)) { cx += x; cy += y; n++; }
cx /= n; cy /= n;
let C0 = { x: cx, y: cy };
for (const step of [4, 1]) {                     // widen if the optimum lands on an edge
  let best = null;
  for (let ox = -12 * step; ox <= 12 * step; ox += step)
    for (let oy = -12 * step; oy <= 12 * step; oy += step) {
      const s = score(C0.x + ox, C0.y + oy), key = s.rms - s.n * 0.01;
      if (!best || key < best.key) best = { key, x: C0.x + ox, y: C0.y + oy, s };
    }
  C0 = { x: best.x, y: best.y, s: best.s };
}
console.log('\nOUTER disc   CX=%s CY=%s R_OUT=%s   (rms %s, %d/720 inliers)',
  C0.x.toFixed(1), C0.y.toFixed(1), C0.s.med.toFixed(1), C0.s.rms.toFixed(2), C0.s.n);

// ---- 2. the ring's inner edge ---------------------------------------
const inner = [];
for (let a = 0; a < 720; a++) {
  const th = a * Math.PI / 360, dx = Math.cos(th), dy = Math.sin(th);
  for (let r = C0.s.med - 2; r > 60; r -= 0.5)
    if (!D(C0.x + dx * r, C0.y + dy * r)) { inner.push(r); break; }
}
inner.sort((a, b) => a - b);
console.log('RING inner   R_IN=%s   (band %s wide)', inner[inner.length >> 1].toFixed(1),
  (C0.s.med - inner[inner.length >> 1]).toFixed(1));

// ---- 3. dark components: which blob is which ------------------------
const lab = new Int32Array(w * h).fill(-1);
const comps = [];
for (let i = 0; i < w * h; i++) {
  if (lum[i] >= 128 || lab[i] >= 0) continue;
  const id = comps.length; const st = [i]; lab[i] = id;
  let cn = 0, x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
  while (st.length) {
    const p = st.pop(); cn++; const x = p % w, y = (p / w) | 0;
    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
    for (const q of [p - 1, p + 1, p - w, p + w]) {
      if (q < 0 || q >= w * h || Math.abs((q % w) - x) > 1) continue;
      if (lum[q] < 128 && lab[q] < 0) { lab[q] = id; st.push(q); }
    }
  }
  comps.push({ id, n: cn, x0, y0, x1, y1 });
}
comps.sort((a, b) => b.n - a.n);
console.log('\ndark components (largest first):');
for (const c of comps.slice(0, 8))
  console.log('  n=%d  bbox=[%d %d %d %d]', c.n, c.x0, c.y0, c.x1, c.y1);

// ---- 4. the mascot, isolated as its own component -------------------
const mascot = comps.find(c => c.n > 20000 && c.x0 > w * 0.25 && c.x1 < w * 0.72);
if (!mascot) { console.log('\nno mascot-sized component found'); process.exit(0); }
const M = (x, y) => (x < 0 || y < 0 || x >= w || y >= h) ? false : lab[(y | 0) * w + (x | 0)] === mascot.id;
function runs(px, py, th) {
  const dx = Math.cos(th), dy = Math.sin(th), out = [];
  let cur = null;
  for (let t = 30; t <= 260; t += 0.5) {
    const d = M(px + dx * t, py + dy * t);
    if (!cur || cur.dark !== d) { cur = { dark: d, r0: t, r1: t }; out.push(cur); } else cur.r1 = t;
  }
  return out;
}
// the rim = a thick dark run that a clear light gap precedes (spokes have none)
function rimAt(px, py, th) {
  const rs = runs(px, py, th);
  for (let i = rs.length - 1; i > 0; i--) {
    const r = rs[i], prev = rs[i - 1];
    if (!r.dark || r.r1 < 105 || r.r1 > 180 || r.r1 - r.r0 < 8) continue;
    if (prev.dark || prev.r1 - prev.r0 < 8) continue;
    return r;
  }
  return null;
}
function rimStats(px, py) {
  const o = [], inn = [];
  for (let a = 0; a < 720; a++) { const v = rimAt(px, py, a * Math.PI / 360); if (v) { o.push(v.r1); inn.push(v.r0); } }
  if (o.length < 60) return null;
  const med = arr => { const s = arr.slice().sort((x, y) => x - y); return s[s.length >> 1]; };
  const mo = med(o), inl = o.filter(v => Math.abs(v - mo) < 8);
  let s = 0; for (const v of inl) s += (v - mo) ** 2;
  return { outer: mo, inner: med(inn), rms: Math.sqrt(s / inl.length), n: inl.length, tot: o.length };
}
let C = { x: (mascot.x0 + mascot.x1) / 2, y: (mascot.y0 + mascot.y1) / 2 };
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
console.log('\nMASCOT centre %s %s', C.x.toFixed(1), C.y.toFixed(1));
console.log('  rim  inner=%s outer=%s  (rms %s, %d/%d angles)',
  st.inner.toFixed(1), st.outer.toFixed(1), st.rms.toFixed(2), st.n, st.tot);
const tips = [];
for (let a = 0; a < 720; a++)
  for (const r of runs(C.x, C.y, a * Math.PI / 360)) if (r.dark && r.r0 > st.outer + 2) tips.push(r.r1);
tips.sort((a, b) => b - a);
console.log('  handle tip r=%s', tips.length ? tips[Math.floor(tips.length * 0.15)].toFixed(1) : 'n/a');

console.log('\n  radial coverage (hub / spokes / rim / handles):');
for (let r = 6; r <= 240; r += 6) {
  let d = 0; for (let a = 0; a < 720; a++) { const th = a * Math.PI / 360; if (M(C.x + Math.cos(th) * r, C.y + Math.sin(th) * r)) d++; }
  console.log('  %s %s %s', String(r).padStart(3), (d / 720).toFixed(2), '#'.repeat(Math.round(d / 720 * 46)));
}
console.log('\n  spoke/handle angles (1 char = 2deg, 0 = east, clockwise):');
for (const r of [100, 134, 175, 200]) {
  let s = '';
  for (let a = 0; a < 360; a += 2) { const th = a * Math.PI / 180; s += M(C.x + Math.cos(th) * r, C.y + Math.sin(th) * r) ? '#' : '.'; }
  console.log('  r=%s %s', String(r).padStart(3), s);
}

// ---- 5. the light gap the overlapping emblem keeps ------------------
const ring = comps[0];
const near = [];
for (let y = mascot.y0 - 60; y <= mascot.y1 + 60; y++) for (let x = mascot.x0 - 60; x <= mascot.x1 + 60; x++)
  if (lab[y * w + x] === ring.id && Math.hypot(x - C0.x, y - C0.y) < inner[inner.length >> 1] - 2) near.push([x, y]);
let gmin = 1e9;
for (let y = mascot.y0; y <= mascot.y1; y += 2) for (let x = mascot.x0; x <= mascot.x1; x += 2) {
  if (!M(x, y)) continue;
  for (const [sx, sy] of near) { const d = (sx - x) ** 2 + (sy - y) ** 2; if (d < gmin) gmin = d; }
}
console.log('\nGAP between mascot and the overlapping emblem: %s px', Math.sqrt(gmin).toFixed(1));

const { decode } = require('./png');

// ---------- marching squares on the grayscale, at the 128 iso-level ----------
function contours(lum, w, h, level = 128) {
  const s = (x, y) => level - lum[y * w + x];       // > 0 inside (dark)
  const TAB = {
    1: [[3, 0]], 2: [[0, 1]], 3: [[3, 1]], 4: [[1, 2]], 5: [[3, 2], [1, 0]],
    6: [[0, 2]], 7: [[3, 2]], 8: [[2, 3]], 9: [[2, 0]], 10: [[0, 3], [2, 1]],
    11: [[2, 1]], 12: [[1, 3]], 13: [[1, 0]], 14: [[0, 3]]
  };
  const segs = new Map();                            // fromEdgeKey -> {to, p}
  const key = (t, x, y) => t + x + ',' + y;
  for (let y = 0; y < h - 1; y++) for (let x = 0; x < w - 1; x++) {
    const a = s(x, y), b = s(x + 1, y), c = s(x + 1, y + 1), d = s(x, y + 1);
    const ci = (a > 0 ? 1 : 0) | (b > 0 ? 2 : 0) | (c > 0 ? 4 : 0) | (d > 0 ? 8 : 0);
    const t = TAB[ci]; if (!t) continue;
    const ek = e => e === 0 ? key('H', x, y) : e === 1 ? key('V', x + 1, y)
      : e === 2 ? key('H', x, y + 1) : key('V', x, y);
    const ep = e => {
      if (e === 0) return [x + a / (a - b), y];
      if (e === 1) return [x + 1, y + b / (b - c)];
      if (e === 2) return [x + 1 - c / (c - d), y + 1];
      return [x, y + 1 - d / (d - a)];
    };
    for (const [f, to] of t) segs.set(ek(f), { to: ek(to), p: ep(f), q: ep(to) });
  }
  const out = [], used = new Set();
  for (const [k0] of segs) {
    if (used.has(k0)) continue;
    const poly = []; let k = k0;
    while (segs.has(k) && !used.has(k)) {
      used.add(k); const sg = segs.get(k); poly.push(sg.p); k = sg.to;
    }
    if (poly.length > 8) out.push(poly);
  }
  return out;
}

// ---------- Ramer-Douglas-Peucker ----------
function rdp(pts, eps) {
  if (pts.length < 3) return pts.slice();
  const keep = new Uint8Array(pts.length); keep[0] = keep[pts.length - 1] = 1;
  const st = [[0, pts.length - 1]];
  while (st.length) {
    const [i, j] = st.pop(); if (j <= i + 1) continue;
    const [x1, y1] = pts[i], [x2, y2] = pts[j];
    const dx = x2 - x1, dy = y2 - y1, L = Math.hypot(dx, dy) || 1;
    let bi = -1, bd = eps;
    for (let k = i + 1; k < j; k++) {
      const d = Math.abs((pts[k][0] - x1) * dy - (pts[k][1] - y1) * dx) / L;
      if (d > bd) { bd = d; bi = k; }
    }
    if (bi > 0) { keep[bi] = 1; st.push([i, bi], [bi, j]); }
  }
  return pts.filter((_, i) => keep[i]);
}

// ---------- Schneider cubic fitting ----------
const sub = (a, b) => [a[0] - b[0], a[1] - b[1]];
const add = (a, b) => [a[0] + b[0], a[1] + b[1]];
const mul = (a, t) => [a[0] * t, a[1] * t];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1];
const norm = a => { const l = Math.hypot(a[0], a[1]) || 1; return [a[0] / l, a[1] / l]; };
const bez = (c, t) => {
  const u = 1 - t;
  return add(add(mul(c[0], u * u * u), mul(c[1], 3 * u * u * t)),
    add(mul(c[2], 3 * u * t * t), mul(c[3], t * t * t)));
};
function chordParams(p) {
  const u = [0];
  for (let i = 1; i < p.length; i++) u.push(u[i - 1] + Math.hypot(...sub(p[i], p[i - 1])));
  const L = u[u.length - 1] || 1;
  return u.map(v => v / L);
}
function generate(p, u, t1, t2) {
  const n = p.length, A = [];
  for (let i = 0; i < n; i++) {
    const t = u[i], v = 1 - t;
    A.push([mul(t1, 3 * v * v * t), mul(t2, 3 * v * t * t)]);
  }
  let c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
  for (let i = 0; i < n; i++) {
    c00 += dot(A[i][0], A[i][0]); c01 += dot(A[i][0], A[i][1]); c11 += dot(A[i][1], A[i][1]);
    const t = u[i], v = 1 - t;
    const base = add(add(mul(p[0], v * v * v), mul(p[0], 3 * v * v * t)),
      add(mul(p[n - 1], 3 * v * t * t), mul(p[n - 1], t * t * t)));
    const tmp = sub(p[i], base);
    x0 += dot(A[i][0], tmp); x1 += dot(A[i][1], tmp);
  }
  const det = c00 * c11 - c01 * c01;
  let a1, a2;
  if (Math.abs(det) < 1e-12) {
    const seg = Math.hypot(...sub(p[n - 1], p[0])) / 3; a1 = a2 = seg;
  } else { a1 = (x0 * c11 - x1 * c01) / det; a2 = (c00 * x1 - c01 * x0) / det; }
  const segLen = Math.hypot(...sub(p[n - 1], p[0]));
  if (a1 < 1e-6 || a2 < 1e-6) { a1 = a2 = segLen / 3; }
  return [p[0], add(p[0], mul(t1, a1)), add(p[n - 1], mul(t2, a2)), p[n - 1]];
}
function maxError(p, c, u) {
  let m = 0, idx = (p.length / 2) | 0;
  for (let i = 1; i < p.length - 1; i++) {
    const d = Math.hypot(...sub(bez(c, u[i]), p[i]));
    if (d > m) { m = d; idx = i; }
  }
  return [m, idx];
}
function fitCubic(p, t1, t2, err, out) {
  if (p.length === 2) {
    const d = Math.hypot(...sub(p[1], p[0])) / 3;
    out.push([p[0], add(p[0], mul(t1, d)), add(p[1], mul(t2, d)), p[1]]); return;
  }
  let u = chordParams(p);
  let c = generate(p, u, t1, t2);
  let [m, idx] = maxError(p, c, u);
  if (m < err) { out.push(c); return; }
  if (m < err * 4) {                              // try reparameterisation
    for (let it = 0; it < 4; it++) {
      const nu = u.map((t, i) => {
        const d = sub(bez(c, t), p[i]);
        const q1 = add(add(mul(sub(c[1], c[0]), 3 * (1 - t) * (1 - t)), mul(sub(c[2], c[1]), 6 * (1 - t) * t)), mul(sub(c[3], c[2]), 3 * t * t));
        const q2 = add(mul(sub(add(c[2], mul(c[1], -2)), mul(c[0], -1)), 6 * (1 - t)), mul(sub(add(c[3], mul(c[2], -2)), mul(c[1], -1)), 6 * t));
        const den = dot(q1, q1) + dot(d, q2);
        return den === 0 ? t : t - dot(d, q1) / den;
      });
      c = generate(p, nu, t1, t2);
      [m, idx] = maxError(p, c, nu);
      u = nu;
      if (m < err) { out.push(c); return; }
    }
  }
  if (idx <= 0) idx = 1; if (idx >= p.length - 1) idx = p.length - 2;
  const tc = norm(sub(p[idx - 1], p[idx + 1]));
  fitCubic(p.slice(0, idx + 1), t1, tc, err, out);
  fitCubic(p.slice(idx), mul(tc, -1), t2, err, out);
}
function fitClosed(pts, err) {
  const p = pts.slice();
  if (Math.hypot(...sub(p[0], p[p.length - 1])) > 1e-9) p.push(p[0]);
  const t1 = norm(sub(p[1], p[p.length - 2]));
  const out = [];
  fitCubic(p, t1, mul(t1, -1), err, out);
  return out;
}
const F = v => Math.round(v * 10) / 10;
function toPath(curves) {
  let d = `M${F(curves[0][0][0])},${F(curves[0][0][1])}`;
  for (const c of curves) d += `C${F(c[1][0])},${F(c[1][1])} ${F(c[2][0])},${F(c[2][1])} ${F(c[3][0])},${F(c[3][1])}`;
  return d + 'Z';
}
function area(p) { let a = 0; for (let i = 0, j = p.length - 1; i < p.length; j = i++) a += p[j][0] * p[i][1] - p[i][0] * p[j][1]; return a / 2; }
function bbox(p) {
  let x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
  for (const [x, y] of p) { x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
  return { x0, y0, x1, y1, cx: (x0 + x1) / 2, cy: (y0 + y1) / 2 };
}
module.exports = { contours, rdp, fitClosed, toPath, area, bbox };

if (require.main === module) {
  const img = decode(process.argv[2] || 'X:/source/kubeNimbus/design/Gemini_Generated_Image_bju2ipbju2ipbju2.png');
  const cs = contours(img.lum, img.w, img.h);
  console.log('contours:', cs.length);
  cs.map(c => ({ n: c.length, a: area(c), b: bbox(c) }))
    .sort((x, y) => Math.abs(y.a) - Math.abs(x.a))
    .slice(0, 20)
    .forEach((c, i) => console.log(
      '%d  pts=%d  area=%s  bbox=[%s %s %s %s]', i, c.n, Math.round(c.a),
      c.b.x0.toFixed(0), c.b.y0.toFixed(0), c.b.x1.toFixed(0), c.b.y1.toFixed(0)));
}

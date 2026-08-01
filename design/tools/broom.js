// The Nimbus broom, lifted straight out of the pgNimbus master.
//
// design/pgnimbus-master.svg is one compound path in which the broom is
// negative space: the bristle fan, the ferrule's glint and the shaft are HOLES
// in the ink, and the collar and ferrule are ink islands. Four of those five
// parts are whole subpaths and are reused verbatim - the same Beziers the
// pgNimbus mark is drawn with, no raster round trip.
//
// The shaft is the exception and is rebuilt: in the master the elephant's hand
// grips it, so for a third of its length the channel's edges are the elephant,
// not the broom. It is measured instead - cast rays across the channel, keep
// the stations where both edges are the shaft's own, and rebuild it as a
// constant-width band on the measured centreline.
//
//   node design/tools/broom.js    # print the measurements and check the parts
const fs = require('fs');
const path = require('path');
const { parse, map, emit, points, bbox } = require('./svgpath');
const { rdp, fitClosed, toPath } = require('./trace');

const MASTER = path.join(__dirname, '..', 'pgnimbus-master.svg');

// Each subpath of the master, by index, with the bbox it must have (in master
// space, after the file's own transform is baked in). The check is the point:
// if the master is ever redrawn, this fails loudly instead of quietly emitting
// the elephant's knee as a broom handle.
const PARTS = [
  { i: 6, name: 'fan',     box: [146.7, 628.3, 385.9, 800.0] },
  { i: 5, name: 'collar',  box: [389.2, 614.3, 429.1, 685.0] },
  { i: 2, name: 'ferrule', box: [810.1, 328.3, 903.4, 407.0] },
  { i: 3, name: 'glint',   box: [847.8, 345.7, 886.9, 376.0] },
];

// The shaft: constant-width band, centreline measured off the master.
const STATION = 2;          // ray spacing along the axis
const W_MIN = 34, W_MAX = 44; // a cross-section this wide is the shaft, not the elephant
const SMOOTH = 3;           // +/- stations averaged into the centreline
const TUCK = 14;            // how far the band runs inside the fan and the ferrule

function load() {
  const src = fs.readFileSync(MASTER, 'utf8');
  const d = /<path d="([^"]+)"/.exec(src)[1];
  const t = /transform="scale\(([-\d.]+)\)\s*translate\(([-\d.]+),\s*([-\d.]+)\)"/.exec(src);
  if (!t) throw new Error('pgnimbus-master.svg: expected one scale+translate path');
  const s = +t[1], tx = +t[2], ty = +t[3];
  return map(parse(d), (x, y) => [(x + tx) * s, (y + ty) * s]);
}

const centroid = pts => {
  let A = 0, cx = 0, cy = 0;
  for (let i = 0; i < pts.length; i++) {
    const [x0, y0] = pts[i], [x1, y1] = pts[(i + 1) % pts.length];
    const f = x0 * y1 - x1 * y0;
    A += f; cx += (x0 + x1) * f; cy += (y0 + y1) * f;
  }
  return [cx / (3 * A), cy / (3 * A)];
};

// nearest crossing of the master's outer contour along +/- the axis normal
function ray(main, px, py, dx, dy) {
  let best = Infinity;
  for (let i = 0; i < main.length; i++) {
    const [x0, y0] = main[i], [x1, y1] = main[(i + 1) % main.length];
    const ex = x1 - x0, ey = y1 - y0;
    const den = dx * -ey - dy * -ex;
    if (Math.abs(den) < 1e-9) continue;
    const rx = x0 - px, ry = y0 - py;
    const t = (rx * -ey - ry * -ex) / den;
    const u = (dx * ry - dy * rx) / den;
    if (t > 0.5 && u >= 0 && u <= 1 && t < best) best = t;
  }
  return best;
}

function build(T) {
  const subs = load();
  const part = {};
  const diag = { parts: [] };
  for (const p of PARTS) {
    const b = bbox(points(subs[p.i]));
    const want = p.box, off = Math.max(Math.abs(b.x0 - want[0]), Math.abs(b.y0 - want[1]),
      Math.abs(b.x1 - want[2]), Math.abs(b.y1 - want[3]));
    if (off > 1) throw new Error(
      `pgnimbus-master.svg subpath ${p.i} is not the ${p.name}: bbox ` +
      `${[b.x0, b.y0, b.x1, b.y1].map(v => v.toFixed(1))} vs expected ${want}`);
    part[p.name] = emit(map([subs[p.i]], T));
    diag.parts.push({ name: p.name, sub: p.i, box: b, drift: off });
  }

  // ---- the shaft ------------------------------------------------------
  const main = points(subs[0], 24);
  const fanPts = points(subs[6]), ferPts = points(subs[2]);
  const A = centroid(points(subs[5])), B = centroid(ferPts);
  const L = Math.hypot(B[0] - A[0], B[1] - A[1]);
  const ux = (B[0] - A[0]) / L, uy = (B[1] - A[1]) / L, nx = -uy, ny = ux;
  const proj = ([x, y]) => (x - A[0]) * ux + (y - A[1]) * uy;

  const raw = [];
  for (let s = -140; s <= L + 80; s += STATION) {
    const px = A[0] + ux * s, py = A[1] + uy * s;
    const up = ray(main, px, py, -nx, -ny), dn = ray(main, px, py, nx, ny);
    const w = up + dn;
    if (!isFinite(w) || w < W_MIN || w > W_MAX) continue;
    raw.push({ s, off: (dn - up) / 2, w });
  }
  if (raw.length < 20) throw new Error('shaft: only ' + raw.length + ' usable cross-sections');
  const widths = raw.map(r => r.w).sort((a, b) => a - b);
  const half = widths[widths.length >> 1] / 2;

  // moving average over the measured offsets, then linear interpolation
  // between stations and a flat hold outside the measured span - the band runs
  // a little past both ends of what can be measured, and extrapolating a fit
  // there is how a shaft grows a kink nobody drew.
  const sm = raw.map((r, i) => {
    let sum = 0, n = 0;
    for (let k = Math.max(0, i - SMOOTH); k <= Math.min(raw.length - 1, i + SMOOTH); k++) {
      sum += raw[k].off; n++;
    }
    return { s: r.s, off: sum / n };
  });
  const offsetAt = s => {
    if (s <= sm[0].s) return sm[0].off;
    if (s >= sm[sm.length - 1].s) return sm[sm.length - 1].off;
    let i = 1; while (sm[i].s < s) i++;
    const a = sm[i - 1], b = sm[i];
    return a.off + (b.off - a.off) * (s - a.s) / (b.s - a.s);
  };

  const s0 = Math.max(...fanPts.map(proj)) - TUCK;      // tucked into the fan
  const s1 = Math.min(...ferPts.map(proj)) + TUCK;      // tucked into the ferrule
  const edge = sign => {
    const out = [];
    for (let s = s0; s <= s1 + 1e-9; s += 1) {
      const o = offsetAt(s) + sign * half;
      out.push(T(A[0] + ux * s + nx * o, A[1] + uy * s + ny * o));
    }
    return out;
  };
  const ring = edge(-1).concat(edge(1).reverse());
  part.shaft = toPath(fitClosed(rdp(ring, 0.3), 0.8));

  Object.assign(diag, {
    axis: { A, B, L, deg: Math.atan2(uy, ux) * 180 / Math.PI },
    width: widths[widths.length >> 1], half, stations: raw.length,
    span: [s0, s1], gaps: raw.reduce((g, r, i) => {
      if (i && r.s - raw[i - 1].s > STATION * 2) g.push([raw[i - 1].s, r.s]);
      return g;
    }, []),
  });
  return { part, diag };
}

module.exports = { build };

if (require.main === module) {
  const CX = 511.6, CY = 511.6, R_OUT = 460.7, SC = 512 / R_OUT;
  const T = (x, y) => [(x - CX) * SC + 512, (y - CY) * SC + 512];
  const { part, diag } = build(T);
  for (const p of diag.parts)
    console.log('%s  subpath %d  bbox %s  (drift %s)', p.name.padEnd(8), p.sub,
      [p.box.x0, p.box.y0, p.box.x1, p.box.y1].map(v => v.toFixed(1).padStart(6)).join(' '),
      p.drift.toFixed(2));
  console.log('\nshaft axis %s,%s -> %s,%s  (%s deg, len %s)',
    ...diag.axis.A.map(v => v.toFixed(1)), ...diag.axis.B.map(v => v.toFixed(1)),
    diag.axis.deg.toFixed(2), diag.axis.L.toFixed(1));
  console.log('width %s (half %s) from %d cross-sections; unmeasurable: %s',
    diag.width.toFixed(2), diag.half.toFixed(2), diag.stations,
    diag.gaps.map(g => g.map(v => v.toFixed(0)).join('..')).join(', ') || 'none');
  console.log('band spans s = %s .. %s', ...diag.span.map(v => v.toFixed(1)));
  for (const k of Object.keys(part)) console.log('%s: %d bytes', k.padEnd(8), part[k].length);
}

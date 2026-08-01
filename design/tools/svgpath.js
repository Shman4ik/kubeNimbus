// Minimal SVG path reader/writer: parse to subpaths, map every point through a
// transform, print again. Handles the subset the pgNimbus master uses
// (M/m, L/l, H/h, V/v, C/c, Z/z) and throws on anything else rather than
// silently dropping geometry.
function parse(d) {
  const toks = d.match(/[MmCcLlZzHhVv]|-?\d*\.?\d+(?:e-?\d+)?/g);
  let i = 0, cx = 0, cy = 0, sx = 0, sy = 0, cmd = null, cur = null;
  const subs = [];
  const num = () => +toks[i++];
  while (i < toks.length) {
    if (/[A-Za-z]/.test(toks[i])) { cmd = toks[i]; i++; }
    switch (cmd) {
      case 'M': case 'm': {
        let x = num(), y = num();
        if (cmd === 'm') { x += cx; y += cy; }
        cur = { segs: [] }; subs.push(cur);
        cx = sx = x; cy = sy = y;
        cur.segs.push({ t: 'M', p: [[x, y]] });
        cmd = cmd === 'M' ? 'L' : 'l';               // implicit lineto after moveto
        break;
      }
      case 'C': case 'c': {
        const a = [num(), num(), num(), num(), num(), num()];
        if (cmd === 'c') for (let k = 0; k < 6; k += 2) { a[k] += cx; a[k + 1] += cy; }
        cur.segs.push({ t: 'C', p: [[a[0], a[1]], [a[2], a[3]], [a[4], a[5]]] });
        cx = a[4]; cy = a[5];
        break;
      }
      case 'L': case 'l': {
        let x = num(), y = num();
        if (cmd === 'l') { x += cx; y += cy; }
        cur.segs.push({ t: 'L', p: [[x, y]] }); cx = x; cy = y;
        break;
      }
      case 'H': case 'h': { let x = num(); if (cmd === 'h') x += cx; cur.segs.push({ t: 'L', p: [[x, cy]] }); cx = x; break; }
      case 'V': case 'v': { let y = num(); if (cmd === 'v') y += cy; cur.segs.push({ t: 'L', p: [[cx, y]] }); cy = y; break; }
      case 'Z': case 'z': cur.segs.push({ t: 'Z', p: [] }); cx = sx; cy = sy; break;
      default: throw new Error('unsupported path command: ' + cmd);
    }
  }
  return subs;
}

const map = (subs, T) =>
  subs.map(s => ({ segs: s.segs.map(g => ({ t: g.t, p: g.p.map(([x, y]) => T(x, y)) })) }));

const F = v => (+v.toFixed(1)).toString();
const emit = subs => subs.map(s => s.segs.map(g =>
  g.t === 'Z' ? 'Z' : g.t + g.p.map(([x, y]) => F(x) + ',' + F(y)).join(' ')).join('')).join('');

// flatten one subpath to a polyline, N samples per cubic
function points(sub, N = 16) {
  const out = []; let cx = 0, cy = 0;
  for (const g of sub.segs) {
    if (g.t === 'M' || g.t === 'L') { [cx, cy] = g.p[0]; out.push([cx, cy]); }
    else if (g.t === 'C') {
      const [[x1, y1], [x2, y2], [x3, y3]] = g.p;
      for (let s = 1; s <= N; s++) {
        const u = s / N, v = 1 - u;
        out.push([v * v * v * cx + 3 * v * v * u * x1 + 3 * v * u * u * x2 + u * u * u * x3,
                  v * v * v * cy + 3 * v * v * u * y1 + 3 * v * u * u * y2 + u * u * u * y3]);
      }
      cx = x3; cy = y3;
    }
  }
  return out;
}

function bbox(pts) {
  const xs = pts.map(p => p[0]), ys = pts.map(p => p[1]);
  return { x0: Math.min(...xs), y0: Math.min(...ys), x1: Math.max(...xs), y1: Math.max(...ys) };
}

module.exports = { parse, map, emit, points, bbox };

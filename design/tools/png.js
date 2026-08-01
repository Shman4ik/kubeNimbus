const fs = require('fs'), zlib = require('zlib');

function decode(file) {
  const buf = fs.readFileSync(file);
  let off = 8, idat = [], ihdr = null, plte = null;
  while (off < buf.length) {
    const len = buf.readUInt32BE(off);
    const type = buf.toString('ascii', off + 4, off + 8);
    const data = buf.subarray(off + 8, off + 8 + len);
    if (type === 'IHDR') ihdr = {
      w: data.readUInt32BE(0), h: data.readUInt32BE(4),
      depth: data[8], color: data[9], interlace: data[12]
    };
    else if (type === 'IDAT') idat.push(data);
    else if (type === 'PLTE') plte = data;
    off += 12 + len;
  }
  if (ihdr.depth !== 8 || ihdr.interlace !== 0) throw new Error('unsupported ' + JSON.stringify(ihdr));
  const ch = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 }[ihdr.color];
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const { w, h } = ihdr, stride = w * ch;
  const out = Buffer.alloc(h * stride);
  let pos = 0;
  for (let y = 0; y < h; y++) {
    const filter = raw[pos++];
    const line = raw.subarray(pos, pos + stride); pos += stride;
    const cur = out.subarray(y * stride, (y + 1) * stride);
    const prev = y ? out.subarray((y - 1) * stride, y * stride) : null;
    for (let i = 0; i < stride; i++) {
      const a = i >= ch ? cur[i - ch] : 0, b = prev ? prev[i] : 0, c = (prev && i >= ch) ? prev[i - ch] : 0;
      let v = line[i];
      if (filter === 1) v += a; else if (filter === 2) v += b;
      else if (filter === 3) v += (a + b) >> 1;
      else if (filter === 4) {
        const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
        v += (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
      }
      cur[i] = v & 255;
    }
  }
  // luminance
  const lum = new Uint8Array(w * h);
  for (let i = 0; i < w * h; i++) {
    let r, g, bl;
    if (ihdr.color === 3) { const p = out[i] * 3; r = plte[p]; g = plte[p + 1]; bl = plte[p + 2]; }
    else if (ch === 1 || ch === 2) { r = g = bl = out[i * ch]; }
    else { r = out[i * ch]; g = out[i * ch + 1]; bl = out[i * ch + 2]; }
    lum[i] = (r * 77 + g * 150 + bl * 29) >> 8;
  }
  return { w, h, lum, ihdr };
}
module.exports = { decode };

if (require.main === module) {
  const img = decode(process.argv[2]);
  console.log(JSON.stringify(img.ihdr));
  const { w, h, lum } = img;
  const dark = v => v < 128;
  // ascii preview
  const S = 48;
  let s = '';
  for (let y = 0; y < S; y++) {
    for (let x = 0; x < S; x++) {
      const px = Math.floor((x + 0.5) * w / S), py = Math.floor((y + 0.5) * h / S);
      s += dark(lum[py * w + px]) ? '#' : '.';
    }
    s += '\n';
  }
  console.log(s);
  // histogram of unique-ish colors
  const hist = {};
  for (let i = 0; i < w * h; i++) { const k = lum[i] >> 4 << 4; hist[k] = (hist[k] || 0) + 1; }
  console.log(Object.entries(hist).sort((a, b) => b[1] - a[1]).slice(0, 6));
}

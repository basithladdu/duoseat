// DuoSeat encryption for browsers: the same SPAKE2 + ChaCha20 + HMAC-SHA256 as src/Secure.cs.
// Plain JS on purpose: browsers only allow WebCrypto on https, and the Wi-Fi link is http.
(function (root) {
  'use strict';
  const enc = new TextEncoder(), dec = new TextDecoder();

  // ---------------------------------------------------------------- SHA-256 / HMAC
  const K = new Uint32Array(64), H0 = new Uint32Array(8);
  (function () {
    const primes = []; for (let n = 2; primes.length < 64; n++) { let ok = true; for (const p of primes) { if (p * p > n) break; if (n % p === 0) { ok = false; break; } } if (ok) primes.push(n); }
    const frac = v => ((v - Math.floor(v)) * 4294967296) >>> 0;
    for (let i = 0; i < 64; i++) K[i] = frac(Math.cbrt(primes[i]));
    for (let i = 0; i < 8; i++) H0[i] = frac(Math.sqrt(primes[i]));
  })();

  function sha256(parts) {
    let total = 0; for (const p of parts) total += p.length;
    const h = H0.slice(), w = new Uint32Array(64), blk = new Uint8Array(64);
    let fill = 0;
    const run = () => {
      for (let i = 0; i < 16; i++) w[i] = (blk[i * 4] << 24) | (blk[i * 4 + 1] << 16) | (blk[i * 4 + 2] << 8) | blk[i * 4 + 3];
      for (let i = 16; i < 64; i++) {
        const a = w[i - 15], b = w[i - 2];
        const s0 = ((a >>> 7) | (a << 25)) ^ ((a >>> 18) | (a << 14)) ^ (a >>> 3);
        const s1 = ((b >>> 17) | (b << 15)) ^ ((b >>> 19) | (b << 13)) ^ (b >>> 10);
        w[i] = (w[i - 16] + s0 + w[i - 7] + s1) | 0;
      }
      let A = h[0], B = h[1], C = h[2], D = h[3], E = h[4], F = h[5], G = h[6], Hh = h[7];
      for (let i = 0; i < 64; i++) {
        const S1 = ((E >>> 6) | (E << 26)) ^ ((E >>> 11) | (E << 21)) ^ ((E >>> 25) | (E << 7));
        const t1 = (Hh + S1 + ((E & F) ^ (~E & G)) + K[i] + w[i]) | 0;
        const S0 = ((A >>> 2) | (A << 30)) ^ ((A >>> 13) | (A << 19)) ^ ((A >>> 22) | (A << 10));
        const t2 = (S0 + ((A & B) ^ (A & C) ^ (B & C))) | 0;
        Hh = G; G = F; F = E; E = (D + t1) | 0; D = C; C = B; B = A; A = (t1 + t2) | 0;
      }
      h[0] += A; h[1] += B; h[2] += C; h[3] += D; h[4] += E; h[5] += F; h[6] += G; h[7] += Hh;
    };
    const feed = d => { for (let i = 0; i < d.length; i++) { blk[fill++] = d[i]; if (fill === 64) { run(); fill = 0; } } };
    for (const p of parts) feed(p);
    const tail = new Uint8Array(((fill < 56) ? 56 : 120) - fill + 8); tail[0] = 0x80;
    const bits = total * 8; const dv = new DataView(tail.buffer);
    dv.setUint32(tail.length - 8, Math.floor(bits / 4294967296)); dv.setUint32(tail.length - 4, bits >>> 0);
    feed(tail);
    const out = new Uint8Array(32); const ov = new DataView(out.buffer);
    for (let i = 0; i < 8; i++) ov.setUint32(i * 4, h[i]);
    return out;
  }

  function hmacPads(key) {
    if (key.length > 64) key = sha256([key]);
    const ip = new Uint8Array(64), op = new Uint8Array(64);
    for (let i = 0; i < 64; i++) { const k = key[i] || 0; ip[i] = k ^ 0x36; op[i] = k ^ 0x5c; }
    return [ip, op];
  }
  function hmac(key, parts) { const [ip, op] = hmacPads(key); return sha256([op, sha256([ip, ...parts])]); }
  const label = (key, s) => hmac(key, [enc.encode(s)]);

  // ---------------------------------------------------------------- ChaCha20 (nonce = 4 zero bytes + 8-byte record number)
  function chacha(key, seq8, data) {
    const st = new Uint32Array(16), x = new Uint32Array(16), kv = new DataView(key.buffer, key.byteOffset, 32), sv = new DataView(seq8.buffer, seq8.byteOffset, 8);
    st[0] = 0x61707865; st[1] = 0x3320646e; st[2] = 0x79622d32; st[3] = 0x6b206574;
    for (let i = 0; i < 8; i++) st[4 + i] = kv.getUint32(i * 4, true);
    st[14] = sv.getUint32(0, true); st[15] = sv.getUint32(4, true);
    const ks = new Uint8Array(64), kw = new DataView(ks.buffer);
    const R = (a, b, c, d) => {
      x[a] += x[b]; x[d] ^= x[a]; x[d] = (x[d] << 16) | (x[d] >>> 16);
      x[c] += x[d]; x[b] ^= x[c]; x[b] = (x[b] << 12) | (x[b] >>> 20);
      x[a] += x[b]; x[d] ^= x[a]; x[d] = (x[d] << 8) | (x[d] >>> 24);
      x[c] += x[d]; x[b] ^= x[c]; x[b] = (x[b] << 7) | (x[b] >>> 25);
    };
    for (let pos = 0; pos < data.length; pos += 64) {
      x.set(st);
      for (let r = 0; r < 10; r++) { R(0, 4, 8, 12); R(1, 5, 9, 13); R(2, 6, 10, 14); R(3, 7, 11, 15); R(0, 5, 10, 15); R(1, 6, 11, 12); R(2, 7, 8, 13); R(3, 4, 9, 14); }
      for (let i = 0; i < 16; i++) kw.setUint32(i * 4, (x[i] + st[i]) >>> 0, true);
      const n = Math.min(64, data.length - pos);
      for (let i = 0; i < n; i++) data[pos + i] ^= ks[i];
      st[12]++;
    }
    return data;
  }

  function seqBytes(n) { const b = new Uint8Array(8); const v = new DataView(b.buffer); v.setUint32(0, n % 4294967296, true); v.setUint32(4, Math.floor(n / 4294967296), true); return b; }
  function same(a, b) { if (a.length !== b.length) return false; let d = 0; for (let i = 0; i < a.length; i++) d |= a[i] ^ b[i]; return d === 0; }

  function makeBox(sk, sm, rk, rm) {
    let sseq = 0, rseq = 0;
    return {
      seal(plain) {
        const seq = seqBytes(sseq++);
        const out = new Uint8Array(plain.length + 16); out.set(plain);
        chacha(sk, seq, out.subarray(0, plain.length));
        out.set(hmac(sm, [seq, out.subarray(0, plain.length)]).subarray(0, 16), plain.length);
        return out;
      },
      open(rec) {
        if (rec.length < 16) return null;
        const n = rec.length - 16, seq = seqBytes(rseq);
        if (!same(hmac(rm, [seq, rec.subarray(0, n)]).subarray(0, 16), rec.subarray(n))) return null;
        rseq++;
        return chacha(rk, seq, rec.slice(0, n));
      }
    };
  }

  // ---------------------------------------------------------------- SPAKE2 (RFC 3526 group 14)
  const P = BigInt('0xFFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD' +
    'EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED' +
    'EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F' +
    '83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B' +
    'E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA0510' +
    '15728E5A8AACAA68FFFFFFFFFFFFFFFF');
  const Q = (P - 1n) / 2n;
  const hex = b => Array.from(b, v => v.toString(16).padStart(2, '0')).join('');
  const unhex = s => { if (!/^([0-9a-f]{2})*$/i.test(s)) return null; const b = new Uint8Array(s.length / 2); for (let i = 0; i < b.length; i++) b[i] = parseInt(s.substr(i * 2, 2), 16); return b; };
  const fromBE = b => b.length ? BigInt('0x' + hex(b)) : 0n;
  const toBE = v => { const s = v.toString(16).padStart(512, '0'); return unhex(s.slice(-512)); };
  function modpow(b, e, m) { let r = 1n; b %= m; while (e > 0n) { if (e & 1n) r = r * b % m; e >>= 1n; b = b * b % m; } return r; }
  function toGroup(lbl) {
    const wide = new Uint8Array(9 * 32);
    for (let i = 0; i < 9; i++) wide.set(sha256([enc.encode(lbl), new Uint8Array([i])]), i * 32);
    const h = fromBE(wide) % P; return h * h % P;
  }
  let MN = null;

  // Player B / viewer side. kind 'p' = PIN, 'v' = watch code.
  function client(kind, secret) {
    if (!MN) MN = [toGroup('DuoSeat SPAKE2 M'), toGroup('DuoSeat SPAKE2 N')];
    const [M, N] = MN;
    const pw = sha256([enc.encode('DuoSeat2 pw ' + kind + ':' + secret)]);
    const w = fromBE(pw) % Q;
    const r = new Uint8Array(32); crypto.getRandomValues(r);
    let x = fromBE(r) % Q; if (x === 0n) x = 1n;
    const X = toBE(modpow(2n, x, P) * modpow(M, w, P) % P);
    return {
      mine: hex(X),
      // returns null for a junk answer, otherwise { confirmA, confirmB, box }
      finish(yHex) {
        const Yb = unhex(yHex || ''); if (!Yb || Yb.length !== 256) return null;
        const Y = fromBE(Yb); if (Y <= 1n || Y >= P - 1n) return null;
        const k = modpow(Y * modpow(N, Q - w, P) % P, x * 2n, P); if (k <= 1n) return null;
        const t = sha256([enc.encode('DuoSeat2'), X, Yb, toBE(k), pw]);
        return {
          confirmA: label(t, 'A confirm'), confirmB: label(t, 'B confirm'),
          box: makeBox(label(t, 'B to A key'), label(t, 'B to A mac'), label(t, 'A to B key'), label(t, 'A to B mac'))
        };
      }
    };
  }

  // A WebSocket that only talks once both sides proved they know the same PIN/code.
  //   h.open()            keys agreed, you can send
  //   h.text(str)         a message from Player A (before the handshake too: 'bad', 'off', 'locked', ...)
  //   h.binary(Uint8Array) a picture
  //   h.close()
  function connect(kind, secret, h) {
    const ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws');
    ws.binaryType = 'arraybuffer';
    let me = null, box = null;
    const api = {
      get readyState() { return box && ws.readyState === 1 ? 1 : (ws.readyState === 1 ? 0 : ws.readyState); },
      send(s) { if (box && ws.readyState === 1) { const b = enc.encode(s), m = new Uint8Array(b.length + 1); m[0] = 1; m.set(b, 1); ws.send(box.seal(m)); } },
      close() { try { ws.close(); } catch (_) {} }
    };
    ws.onopen = () => { me = client(kind, secret); ws.send('k ' + kind + ' ' + me.mine); };
    ws.onmessage = e => {
      if (!box) {
        if (typeof e.data !== 'string') return;
        const p = e.data.split(' ');
        if (p[0] === 'y' && me) {
          const f = me.finish(p[1]);
          const cA = unhex(p[2] || '');
          if (!f || !cA || !same(cA, f.confirmA)) { ws.send('c 00'); h.text && h.text('bad'); ws.close(); return; }
          ws.send('c ' + hex(f.confirmB));
          box = f.box;
          h.open && h.open();
          return;
        }
        h.text && h.text(e.data);
        return;
      }
      if (typeof e.data === 'string') return;            // plain text after the handshake is not from Player A
      const m = box.open(new Uint8Array(e.data));
      if (!m) { ws.close(); return; }
      if (m[0] === 1) h.text && h.text(dec.decode(m.subarray(1)));
      else if (m[0] === 2) h.binary && h.binary(m.subarray(1));
    };
    ws.onclose = () => { box = null; h.close && h.close(); };
    return api;
  }

  root.DuoSeal = { connect, _test: { sha256, hmac, chacha, client, makeBox, hex, unhex } };
})(typeof window !== 'undefined' ? window : globalThis);

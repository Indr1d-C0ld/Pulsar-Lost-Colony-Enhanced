'use strict';
// Photon GpBinary v1.6 (Protocol16) — codec fedele: ogni valore decodificato conserva il tipo
// originale ({t, v}) così la ricodifica produce gli stessi byte che il client si aspetta.

const T = {
  NULL: 42, DICT: 68, STRARR: 97, BYTE: 98, CUSTOM: 99, DOUBLE: 100, EVENT: 101, FLOAT: 102,
  HASH: 104, INT: 105, SHORT: 107, LONG: 108, INTARR: 110, BOOL: 111, OPRESP: 112, OPREQ: 113,
  STRING: 115, BYTEARR: 120, ARRAY: 121, OBJARR: 122,
};

class Reader {
  constructor(buf, pos = 0) { this.b = buf; this.p = pos; }
  u8() { return this.b.readUInt8(this.p++); }
  i16() { const v = this.b.readInt16BE(this.p); this.p += 2; return v; }
  i32() { const v = this.b.readInt32BE(this.p); this.p += 4; return v; }
  i64() { const v = this.b.readBigInt64BE(this.p); this.p += 8; return v; }
  f32() { const v = this.b.readFloatBE(this.p); this.p += 4; return v; }
  f64() { const v = this.b.readDoubleBE(this.p); this.p += 8; return v; }
  bytes(n) { if (n < 0 || this.p + n > this.b.length) throw new RangeError('gp16: lunghezza fuori dai limiti'); const v = Buffer.from(this.b.subarray(this.p, this.p + n)); this.p += n; return v; }
}

class Writer {
  constructor() { this.chunks = []; this.len = 0; }
  push(b) { this.chunks.push(b); this.len += b.length; return this; }
  u8(v) { return this.push(Buffer.from([v & 0xff])); }
  i16(v) { const b = Buffer.alloc(2); b.writeInt16BE(v); return this.push(b); }
  i32(v) { const b = Buffer.alloc(4); b.writeInt32BE(v); return this.push(b); }
  i64(v) { const b = Buffer.alloc(8); b.writeBigInt64BE(BigInt(v)); return this.push(b); }
  f32(v) { const b = Buffer.alloc(4); b.writeFloatBE(v); return this.push(b); }
  f64(v) { const b = Buffer.alloc(8); b.writeDoubleBE(v); return this.push(b); }
  out() { return Buffer.concat(this.chunks, this.len); }
}

// ---- costruttori di valori tipizzati
const V = {
  nul: () => ({ t: T.NULL, v: null }),
  byte: (v) => ({ t: T.BYTE, v }),
  bool: (v) => ({ t: T.BOOL, v: !!v }),
  short: (v) => ({ t: T.SHORT, v }),
  int: (v) => ({ t: T.INT, v }),
  long: (v) => ({ t: T.LONG, v: BigInt(v) }),
  str: (v) => ({ t: T.STRING, v }),
  bytes: (v) => ({ t: T.BYTEARR, v }),
  intArr: (v) => ({ t: T.INTARR, v }),
  strArr: (v) => ({ t: T.STRARR, v }),
  hash: (entries = []) => ({ t: T.HASH, v: entries }), // entries: [[chiave, valore], ...] tipizzati
};

function readValue(r, type) {
  if (type === undefined) type = r.u8();
  switch (type) {
    case 0: case T.NULL: return { t: T.NULL, v: null };
    case T.BYTE: return { t: type, v: r.u8() };
    case T.BOOL: return { t: type, v: r.u8() !== 0 };
    case T.SHORT: return { t: type, v: r.i16() };
    case T.INT: return { t: type, v: r.i32() };
    case T.LONG: return { t: type, v: r.i64() };
    case T.FLOAT: return { t: type, v: r.f32() };
    case T.DOUBLE: return { t: type, v: r.f64() };
    case T.STRING: return { t: type, v: r.bytes(r.i16() & 0xffff).toString('utf8') };
    case T.BYTEARR: return { t: type, v: r.bytes(r.i32()) };
    case T.INTARR: { const n = r.i32(); const a = new Array(n); for (let i = 0; i < n; i++) a[i] = r.i32(); return { t: type, v: a }; }
    case T.STRARR: { const n = r.i16(); const a = new Array(n); for (let i = 0; i < n; i++) a[i] = r.bytes(r.i16() & 0xffff).toString('utf8'); return { t: type, v: a }; }
    case T.OBJARR: { const n = r.i16(); const a = new Array(n); for (let i = 0; i < n; i++) a[i] = readValue(r); return { t: type, v: a }; }
    case T.HASH: { const n = r.i16(); const e = new Array(n); for (let i = 0; i < n; i++) { const k = readValue(r); e[i] = [k, readValue(r)]; } return { t: type, v: e }; }
    case T.CUSTOM: { const code = r.u8(); return { t: type, code, v: r.bytes(r.i16() & 0xffff) }; }
    case T.DICT: { const d = readDictHeader(r); d.v = readDictBody(r, d); return d; }
    case T.ARRAY: return readArray(r);
    case T.EVENT: return { t: type, v: readEvent(r) };
    case T.OPREQ: return { t: type, v: readOpRequest(r) };
    case T.OPRESP: return { t: type, v: readOpResponse(r) };
    default: throw new Error('gp16: tipo sconosciuto ' + type);
  }
}

function readDictHeader(r) {
  const d = { t: T.DICT, kt: r.u8(), vt: r.u8() };
  if (d.vt === T.DICT) d.sub = readDictHeader(r);
  else if (d.vt === T.ARRAY) { d.vat = r.u8(); if (d.vat === T.CUSTOM) d.vac = r.u8(); }
  return d;
}
function readDictBody(r, d) {
  const n = r.i16(); const e = new Array(n);
  for (let i = 0; i < n; i++) {
    const k = (d.kt === 0 || d.kt === T.NULL) ? readValue(r) : readValue(r, d.kt);
    let v;
    if (d.vt === 0 || d.vt === T.NULL) v = readValue(r);
    else if (d.vt === T.DICT) { v = { ...d.sub }; v.v = readDictBody(r, d.sub); }
    else v = readValue(r, d.vt);
    e[i] = [k, v];
  }
  return e;
}
function readArray(r) {
  const n = r.i16(); const et = r.u8(); const a = { t: T.ARRAY, et, v: new Array(n) };
  if (et === T.ARRAY) { for (let i = 0; i < n; i++) a.v[i] = readArray(r); }
  else if (et === T.CUSTOM) { a.code = r.u8(); for (let i = 0; i < n; i++) a.v[i] = r.bytes(r.i16() & 0xffff); }
  else if (et === T.DICT) { a.dict = readDictHeader(r); for (let i = 0; i < n; i++) a.v[i] = readDictBody(r, a.dict); }
  else if (et === T.BYTE) { a.v = r.bytes(n); }
  else { for (let i = 0; i < n; i++) a.v[i] = readValue(r, et).v; }
  return a;
}

function writeValue(w, val, withType = true) {
  const t = val.t;
  if (withType) w.u8(t);
  switch (t) {
    case T.NULL: break;
    case T.BYTE: w.u8(val.v); break;
    case T.BOOL: w.u8(val.v ? 1 : 0); break;
    case T.SHORT: w.i16(val.v); break;
    case T.INT: w.i32(val.v); break;
    case T.LONG: w.i64(val.v); break;
    case T.FLOAT: w.f32(val.v); break;
    case T.DOUBLE: w.f64(val.v); break;
    case T.STRING: writeStr(w, val.v); break;
    case T.BYTEARR: w.i32(val.v.length).push(val.v); break;
    case T.INTARR: w.i32(val.v.length); for (const x of val.v) w.i32(x); break;
    case T.STRARR: w.i16(val.v.length); for (const x of val.v) writeStr(w, x); break;
    case T.OBJARR: w.i16(val.v.length); for (const x of val.v) writeValue(w, x); break;
    case T.HASH: w.i16(val.v.length); for (const [k, v] of val.v) { writeValue(w, k); writeValue(w, v); } break;
    case T.CUSTOM: w.u8(val.code).i16(val.v.length).push(val.v); break;
    case T.DICT: writeDictHeader(w, val); writeDictBody(w, val, val.v); break;
    case T.ARRAY: writeArray(w, val); break;
    case T.EVENT: writeEvent(w, val.v); break;
    case T.OPREQ: writeOpRequest(w, val.v); break;
    case T.OPRESP: writeOpResponse(w, val.v); break;
    default: throw new Error('gp16: tipo non scrivibile ' + t);
  }
}
function writeStr(w, s) { const b = Buffer.from(s, 'utf8'); w.i16(b.length > 32767 ? b.length - 65536 : b.length).push(b); }
function writeDictHeader(w, d) {
  w.u8(d.kt).u8(d.vt);
  if (d.vt === T.DICT) writeDictHeader(w, d.sub);
  else if (d.vt === T.ARRAY) { w.u8(d.vat); if (d.vat === T.CUSTOM) w.u8(d.vac); }
}
function writeDictBody(w, d, entries) {
  w.i16(entries.length);
  for (const [k, v] of entries) {
    writeValue(w, k, d.kt === 0 || d.kt === T.NULL);
    if (d.vt === 0 || d.vt === T.NULL) writeValue(w, v);
    else if (d.vt === T.DICT) writeDictBody(w, d.sub, v.v);
    else writeValue(w, v, false);
  }
}
function writeArray(w, a) {
  w.i16(a.v.length).u8(a.et);
  if (a.et === T.ARRAY) { for (const x of a.v) writeArray(w, x); }
  else if (a.et === T.CUSTOM) { w.u8(a.code); for (const x of a.v) w.i16(x.length).push(x); }
  else if (a.et === T.DICT) { writeDictHeader(w, a.dict); for (const x of a.v) writeDictBody(w, a.dict, x); }
  else if (a.et === T.BYTE) w.push(a.v);
  else for (const x of a.v) writeValue(w, { t: a.et, v: x }, false);
}

// ---- tabelle parametri: Map<byte, valore tipizzato>
function readParams(r) { const n = r.i16(); const m = new Map(); for (let i = 0; i < n; i++) { const k = r.u8(); m.set(k, readValue(r)); } return m; }
function writeParams(w, m) { w.i16(m.size); for (const [k, v] of m) { w.u8(k); writeValue(w, v); } }

function readOpRequest(r) { return { code: r.u8(), params: readParams(r) }; }
function writeOpRequest(w, o) { w.u8(o.code); writeParams(w, o.params); }
function readOpResponse(r) { const code = r.u8(); const ret = r.i16(); const dbg = readValue(r); return { code, ret, dbg: dbg.v, params: readParams(r) }; }
function writeOpResponse(w, o) { w.u8(o.code).i16(o.ret || 0); writeValue(w, o.dbg ? V.str(o.dbg) : V.nul()); writeParams(w, o.params || new Map()); }
function readEvent(r) { return { code: r.u8(), params: readParams(r) }; }
function writeEvent(w, e) { w.u8(e.code); writeParams(w, e.params); }

// ---- utilità su Hashtable tipizzate
const keyOf = (k) => k.t + ':' + (Buffer.isBuffer(k.v) ? k.v.toString('hex') : String(k.v));
function hashGet(h, key) { if (!h || h.t !== T.HASH) return undefined; for (const [k, v] of h.v) if (k.v === key) return v; return undefined; }
function hashSet(h, k, v) { const id = keyOf(k); const i = h.v.findIndex(([kk]) => keyOf(kk) === id); if (i >= 0) h.v[i] = [k, v]; else h.v.push([k, v]); }
function hashDel(h, k) { const id = keyOf(k); h.v = h.v.filter(([kk]) => keyOf(kk) !== id); }
function equal(a, b) {
  if (a === b) return true;
  if (!a || !b || a.t !== b.t) return false;
  const w1 = new Writer(), w2 = new Writer(); writeValue(w1, a); writeValue(w2, b);
  return w1.out().equals(w2.out());
}

module.exports = { T, V, Reader, Writer, readValue, writeValue, readParams, writeParams, readOpRequest, writeOpRequest,
  readOpResponse, writeOpResponse, readEvent, writeEvent, hashGet, hashSet, hashDel, keyOf, equal };

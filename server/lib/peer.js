'use strict';
// Trasporto Photon su TCP: framing, init, ping, scambio chiavi Diffie-Hellman e cifratura AES.
const crypto = require('crypto');
const gp = require('./gp16');

const MSG = { INIT: 0, INIT_RESPONSE: 1, OP_REQUEST: 2, OP_RESPONSE: 3, EVENT: 4, INTERNAL_REQUEST: 6, INTERNAL_RESPONSE: 7 };
const OAKLEY_768 = Buffer.from(
  'FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD' +
  'EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A63A3620FFFFFFFFFFFFFFFF', 'hex');
const stripZeros = (b) => { let i = 0; while (i < b.length - 1 && b[i] === 0) i++; return b.subarray(i); };

class Peer {
  constructor(socket, handler, log) {
    this.sock = socket; this.handler = handler; this.log = log;
    this.buf = Buffer.alloc(0); this.key = null; this.closed = false;
    this.start = Date.now(); this.id = socket.remoteAddress + ':' + socket.remotePort;
    this.bytesIn = 0; this.bytesOut = 0; this.msgsIn = 0; this.msgsOut = 0; this.lastActivity = Date.now(); this.pings = 0;
    socket.setNoDelay(true);
    socket.on('data', (d) => this.onData(d));
    socket.on('error', () => this.close());
    socket.on('close', () => this.close());
  }

  close() {
    if (this.closed) return;
    this.closed = true; this.sock.destroy();
    try { this.handler.onClose(this); } catch (e) { this.log('errore in chiusura', e); }
  }

  onData(d) {
    this.buf = this.buf.length ? Buffer.concat([this.buf, d]) : d;
    this.bytesIn += d.length; Peer.totals.bytesIn += d.length; this.lastActivity = Date.now();
    try {
      for (;;) {
        if (this.buf.length < 1) return;
        if (this.buf[0] === 0xF0) { // ping: F0 + tempo client (4)
          if (this.buf.length < 5) return;
          const out = Buffer.alloc(9); out[0] = 0xF0;
          out.writeInt32BE((Date.now() - this.start) | 0, 1); this.buf.copy(out, 5, 1, 5);
          this.sock.write(out); this.bytesOut += 9; Peer.totals.bytesOut += 9; this.pings++; this.buf = this.buf.subarray(5); continue;
        }
        if (this.buf[0] !== 0xFB) throw new Error('intestazione TCP inattesa 0x' + this.buf[0].toString(16));
        if (this.buf.length < 7) return;
        const len = this.buf.readUInt32BE(1);
        if (len < 7 || len > 4 * 1024 * 1024) throw new Error('lunghezza frame non valida ' + len);
        if (this.buf.length < len) return;
        const frame = this.buf.subarray(0, len); this.buf = this.buf.subarray(len);
        this.msgsIn++; Peer.totals.msgsIn++;
        this.onFrame(frame[5], frame[6] !== 0, Buffer.from(frame.subarray(7)));
      }
    } catch (e) { this.log(`[${this.id}] flusso non valido: ${e.message}`); this.close(); }
  }

  onFrame(channel, reliable, body) {
    if (body.length < 2 || body[0] !== 0xF3) throw new Error('magic mancante');
    let type = body[1]; let payload = body.subarray(2);
    if (type & 0x80) {
      if (!this.key) throw new Error('messaggio cifrato senza chiave');
      const dec = crypto.createDecipheriv('aes-256-cbc', this.key, Buffer.alloc(16));
      payload = Buffer.concat([dec.update(payload), dec.final()]); type &= 0x7f;
    }
    if (type === MSG.INIT) {
      this.init = { proto: payload[0] + '.' + payload[1], sdk: payload[2], appId: payload.subarray(7, 39).toString('latin1').replace(/\0+$/, '') };
      if (payload[1] !== 6) this.log(`[${this.id}] ATTENZIONE: protocollo ${this.init.proto}, è supportato solo 1.6`);
      this.sendRaw(Buffer.from([0xF3, MSG.INIT_RESPONSE, 0x00]), 0, true);
      return;
    }
    const r = new gp.Reader(payload);
    if (type === MSG.INTERNAL_REQUEST) {
      const op = gp.readOpRequest(r);
      if (op.code === 0) return this.keyExchange(op, channel);
      throw new Error('operazione interna sconosciuta ' + op.code);
    }
    if (type === MSG.OP_REQUEST) return this.handler.onOperation(this, gp.readOpRequest(r), { channel, reliable });
    throw new Error('tipo messaggio inatteso ' + type);
  }

  keyExchange(op, channel) {
    const clientPub = op.params.get(1);
    const dh = crypto.createDiffieHellman(OAKLEY_768, Buffer.from([22]));
    const serverPub = dh.generateKeys();
    const secret = stripZeros(dh.computeSecret(stripZeros(clientPub.v)));
    const w = new gp.Writer();
    gp.writeOpResponse(w, { code: 0, ret: 0, params: new Map([[1, gp.V.bytes(serverPub)]]) });
    this.sendRaw(Buffer.concat([Buffer.from([0xF3, MSG.INTERNAL_RESPONSE]), w.out()]), channel, true);
    this.key = crypto.createHash('sha256').update(secret).digest();
  }

  sendRaw(body, channel = 0, reliable = true) {
    if (this.closed) return;
    const h = Buffer.alloc(7); h[0] = 0xFB; h.writeUInt32BE(body.length + 7, 1); h[5] = channel; h[6] = reliable ? 1 : 0;
    this.sock.write(Buffer.concat([h, body]));
    this.bytesOut += body.length + 7; this.msgsOut++; Peer.totals.bytesOut += body.length + 7; Peer.totals.msgsOut++;
  }

  sendMessage(type, payload, channel, reliable, encrypt) {
    if (encrypt && this.key) {
      const enc = crypto.createCipheriv('aes-256-cbc', this.key, Buffer.alloc(16));
      payload = Buffer.concat([enc.update(payload), enc.final()]); type |= 0x80;
    }
    this.sendRaw(Buffer.concat([Buffer.from([0xF3, type]), payload]), channel, reliable);
  }

  respond(code, ret = 0, params = new Map(), dbg = null, encrypt = false) {
    const w = new gp.Writer(); gp.writeOpResponse(w, { code, ret, dbg, params });
    this.sendMessage(MSG.OP_RESPONSE, w.out(), 0, true, encrypt);
  }

  // Un evento già serializzato può essere inviato a molti peer senza ricodificarlo.
  static encodeEvent(code, params) { const w = new gp.Writer(); gp.writeEvent(w, { code, params }); return w.out(); }
  sendEventBytes(bytes, channel = 0, reliable = true) { this.sendMessage(MSG.EVENT, bytes, channel, reliable, false); }
  sendEvent(code, params, channel = 0, reliable = true) { this.sendEventBytes(Peer.encodeEvent(code, params), channel, reliable); }
}

// contatori globali del traffico, letti dalla telemetria
Peer.totals = { bytesIn: 0, bytesOut: 0, msgsIn: 0, msgsOut: 0 };

module.exports = { Peer, MSG };

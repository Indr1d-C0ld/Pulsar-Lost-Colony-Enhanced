'use strict';
// Collaudo end-to-end: due client simulati percorrono init, scambio chiavi, auth cifrata, lobby, stanza, eventi, cache, uscita.
const net = require('net'); const crypto = require('crypto'); const assert = require('assert');
const { spawn } = require('child_process'); const path = require('path'); const fs = require('fs'); const os = require('os');
const gp = require('../lib/gp16'); const { V, T } = gp;
const PRIME = Buffer.from('FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A63A3620FFFFFFFFFFFFFFFF', 'hex');
const strip = (b) => { let i = 0; while (i < b.length - 1 && b[i] === 0) i++; return b.subarray(i); };

class Client {
  constructor(port) { this.q = []; this.waiters = []; this.buf = Buffer.alloc(0); this.sock = net.connect(port, '127.0.0.1'); this.sock.on('data', (d) => this.onData(d)); }
  onData(d) {
    this.buf = Buffer.concat([this.buf, d]);
    for (;;) {
      if (!this.buf.length) return;
      if (this.buf[0] === 0xF0) { if (this.buf.length < 9) return; this.push({ type: 'pong', client: this.buf.readInt32BE(5) }); this.buf = this.buf.subarray(9); continue; }
      if (this.buf.length < 7) return; const len = this.buf.readUInt32BE(1); if (this.buf.length < len) return;
      const f = this.buf.subarray(7, len); this.buf = this.buf.subarray(len);
      let type = f[1], pl = f.subarray(2);
      if (type & 0x80) { const dc = crypto.createDecipheriv('aes-256-cbc', this.key, Buffer.alloc(16)); pl = Buffer.concat([dc.update(pl), dc.final()]); type &= 0x7f; }
      const r = new gp.Reader(pl);
      if (type === 1) this.push({ type: 'init' });
      else if (type === 3 || type === 7) this.push({ type: type === 3 ? 'resp' : 'iresp', ...gp.readOpResponse(r) });
      else if (type === 4) this.push({ type: 'event', ...gp.readEvent(r) });
    }
  }
  push(m) { const w = this.waiters.shift(); if (w) w(m); else this.q.push(m); }
  next() { return new Promise((res, rej) => { if (this.q.length) return res(this.q.shift()); const t = setTimeout(() => rej(new Error('timeout in attesa di un messaggio')), 3000); this.waiters.push((m) => { clearTimeout(t); res(m); }); }); }
  async expect(type, code) { const m = await this.next(); assert.strictEqual(m.type, type, JSON.stringify(m, (k, v) => typeof v === 'bigint' ? String(v) : v)); if (code !== undefined) assert.strictEqual(m.code, code); return m; }
  frame(body, ch = 0, rel = 1) { const h = Buffer.alloc(7); h[0] = 0xFB; h.writeUInt32BE(body.length + 7, 1); h[5] = ch; h[6] = rel; this.sock.write(Buffer.concat([h, body])); }
  op(code, params, { internal = false, encrypt = false } = {}) {
    const w = new gp.Writer(); gp.writeOpRequest(w, { code, params: new Map(params) }); let pl = w.out(); let type = internal ? 6 : 2;
    if (encrypt) { const e = crypto.createCipheriv('aes-256-cbc', this.key, Buffer.alloc(16)); pl = Buffer.concat([e.update(pl), e.final()]); type |= 0x80; }
    this.frame(Buffer.concat([Buffer.from([0xF3, type]), pl]));
  }
  async handshake(token, want = 0) {
    const init = Buffer.alloc(41); init[0] = 0xF3; init[2] = 1; init[3] = 6; init.write('testapp', 9, 'latin1'); this.frame(init);
    await this.expect('init');
    const ping = Buffer.alloc(5); ping[0] = 0xF0; ping.writeInt32BE(1234, 1); this.sock.write(ping);
    assert.strictEqual((await this.expect('pong')).client, 1234);
    const dh = crypto.createDiffieHellman(PRIME, Buffer.from([22])); const pub = dh.generateKeys();
    this.op(0, [[1, V.bytes(pub)]], { internal: true });
    const r = await this.expect('iresp', 0); this.key = crypto.createHash('sha256').update(strip(dh.computeSecret(r.params.get(1).v))).digest();
    const p = [[224, V.str('testapp')], [220, V.str('1.2.10_test')]]; if (token) p.push([221, V.str(token)]);
    this.op(230, p, { encrypt: true });
    const a = await this.expect('resp', 230); assert.strictEqual(a.ret, want); return want === 0 ? a.params.get(221).v : a.ret;
  }
}

module.exports = { Client };
if (require.main === module) (async () => {
  const cfg = path.join(os.tmpdir(), `relay-test-${process.pid}.json`); const dataDir = path.join(os.tmpdir(), `relay-test-${process.pid}-dati`);
  const TOKEN = 'prova-segreta';
  const { hashPassword } = require('../lib/auth');
  fs.writeFileSync(cfg, JSON.stringify({ masterPort: 14530, gamePort: 14531, adminPort: 14580, dataDir, adminToken: TOKEN, motd: 'Benvenuti a bordo', adminUser: 'admin', adminPassHash: hashPassword('prova-di-collaudo') }));
  const admin = async (route, method = 'GET', body) => (await fetch('http://127.0.0.1:14580' + route, { method, headers: { 'x-admin-token': TOKEN, 'content-type': 'application/json' }, body: body ? JSON.stringify(body) : undefined })).json();
  const str177 = (ev) => { assert.strictEqual(ev.params.get(245).t, T.STRING); return ev.params.get(245).v; };
  const srv = spawn(process.execPath, [path.join(__dirname, '..', 'server.js'), cfg], { stdio: ['ignore', 'pipe', 'inherit'] });
  let out = ''; srv.stdout.on('data', (d) => { out += d; });
  await new Promise((r) => setTimeout(r, 600));
  let failed = false;
  try {
    // --- master: lobby e creazione
    const m1 = new Client(14530); const tok1 = await m1.handshake();
    m1.op(229, []); await m1.expect('resp', 229);
    const gl = await m1.expect('event', 230); assert.strictEqual(gl.params.get(222).v.length, 0); await m1.expect('event', 226);
    m1.op(227, [[255, V.str('USS Test')]]); const cr = await m1.expect('resp', 227);
    assert.strictEqual(cr.params.get(230).v, '127.0.0.1:14531');
    // --- game: A crea la stanza
    const a = new Client(14531); await a.handshake(tok1);
    const props = V.hash([[V.byte(255), V.byte(5)], [V.byte(253), V.bool(true)], [V.byte(254), V.bool(true)], [V.str('ShipName'), V.str('Enterprise')], [V.byte(250), V.strArr(['ShipName'])]]);
    a.op(227, [[255, V.str('USS Test')], [248, props], [249, V.hash([[V.byte(255), V.str('Picard')]])], [241, V.bool(true)]]);
    const ca = await a.expect('resp', 227); assert.strictEqual(ca.ret, 0); assert.strictEqual(ca.params.get(254).v, 1);
    assert.strictEqual((await a.expect('event', 255)).params.get(254).v, 1);
    assert.strictEqual(str177(await a.expect('event', 177)), 'msg|Benvenuti a bordo');
    const upd = await m1.expect('event', 229); const entry = upd.params.get(222).v[0];
    assert.strictEqual(entry[0].v, 'USS Test'); assert.strictEqual(gp.hashGet(entry[1], 'ShipName').v, 'Enterprise'); assert.strictEqual(gp.hashGet(entry[1], 252).v, 1);
    // A mette in cache un evento (istanziazione) e un RPC bufferizzato
    const inst = V.hash([[V.byte(0), V.str('NetworkPrefabs/Ship')], [V.byte(7), V.int(1001)], [V.byte(5), { t: T.OBJARR, v: [V.int(7), { t: T.FLOAT, v: 1.5 }, { t: T.CUSTOM, code: 86, v: Buffer.alloc(12, 1) }] }]]);
    a.op(253, [[244, V.byte(202)], [245, inst], [247, V.byte(4)]]);
    a.op(253, [[244, V.byte(200)], [245, V.hash([[V.byte(0), V.int(1001)], [V.byte(5), V.byte(3)]])], [247, V.byte(4)]]);
    a.op(253, [[244, V.byte(200)], [245, V.hash([[V.byte(0), V.int(2002)], [V.byte(5), V.byte(9)]])], [247, V.byte(4)]]);
    a.op(253, [[244, V.byte(205)], [245, V.hash([[V.byte(0), V.int(2002)]])], [247, V.byte(6)]].map(([k, v]) => k === 244 ? [k, V.byte(200)] : [k, v])); // rimuove l'RPC della view 2002
    // --- B entra via master
    const m2 = new Client(14530); const tok2 = await m2.handshake();
    m2.op(226, [[255, V.str('Inesistente')]]); assert.strictEqual((await m2.expect('resp', 226)).ret, 32758);
    m2.op(225, []); assert.strictEqual((await m2.expect('resp', 225)).params.get(255).v, 'USS Test');
    const b = new Client(14531); await b.handshake(tok2);
    b.op(226, [[255, V.str('USS Test')], [249, V.hash([[V.byte(255), V.str('Riker')]])]]);
    const jb = await b.expect('resp', 226); assert.strictEqual(jb.params.get(254).v, 2);
    assert.strictEqual(gp.hashGet(jb.params.get(248), 248).v, 1); assert.strictEqual(gp.hashGet(jb.params.get(248), 'ShipName').v, 'Enterprise');
    assert.strictEqual(gp.hashGet(gp.hashGet(jb.params.get(249), 1), 255).v, 'Picard');
    assert.deepStrictEqual((await b.expect('event', 255)).params.get(252).v, [1, 2]);
    const c1 = await b.expect('event', 202); assert.ok(gp.equal(c1.params.get(245), inst), 'evento in cache identico byte per byte');
    const c2 = await b.expect('event', 200); assert.strictEqual(gp.hashGet(c2.params.get(245), 0).v, 1001);
    assert.strictEqual(str177(await b.expect('event', 177)), 'msg|Benvenuti a bordo');
    assert.strictEqual((await a.expect('event', 255)).params.get(254).v, 2);
    // --- inoltro: altri, tutti, solo master, attori mirati
    b.op(253, [[244, V.byte(201)], [245, V.bytes(Buffer.from('hello'))]]);
    const e1 = await a.expect('event', 201); assert.strictEqual(e1.params.get(254).v, 2); assert.strictEqual(e1.params.get(245).v.toString(), 'hello');
    b.op(253, [[244, V.byte(7)], [246, V.byte(1)]]); await a.expect('event', 7); await b.expect('event', 7);
    b.op(253, [[244, V.byte(8)], [246, V.byte(2)]]); await a.expect('event', 8);
    a.op(253, [[244, V.byte(9)], [252, V.intArr([2])]]); await b.expect('event', 9);
    // --- proprietà: stanza e giocatore
    a.op(252, [[251, V.hash([[V.str('Sector'), V.int(42)]])], [250, V.bool(true)]]); await a.expect('resp', 252);
    const pc = await b.expect('event', 253); assert.strictEqual(pc.params.get(253).v, 0); assert.strictEqual(gp.hashGet(pc.params.get(251), 'Sector').v, 42);
    await a.expect('event', 253);
    // --- telemetria di plancia dagli eventi di stazione
    b.op(253, [[244, V.byte(177)], [245, V.str('7|core_stress=40;dmx_weapons=20;')]]); assert.strictEqual(str177(await a.expect('event', 177)), '7|core_stress=40;dmx_weapons=20;');
    b.op(253, [[244, V.byte(177)], [245, V.str('log|7|Scienza: firma decrittata')]]); await a.expect('event', 177);
    let snap = await admin('/api/status');
    assert.deepStrictEqual(snap.rooms[0].ships[0].values, { core_stress: 40, dmx_weapons: 20 }); assert.strictEqual(snap.rooms[0].journal[0].text, 'Scienza: firma decrittata');
    assert.deepStrictEqual(snap.rooms[0].actors.map((x) => x.name).sort(), ['Picard', 'Riker']);
    // --- amministrazione: token, annuncio, partita chiusa e riaperta
    assert.strictEqual((await fetch('http://127.0.0.1:14580/api/status')).status, 401);
    assert.strictEqual((await (await fetch('http://127.0.0.1:14580/api/health')).json()).ok, true);
    // accesso con utente e password: sbagliato, giusto, cookie, intestazione obbligatoria per le azioni
    const login = (user, pass) => fetch('http://127.0.0.1:14580/api/login', { method: 'POST', body: JSON.stringify({ user, pass }) });
    assert.strictEqual((await login('admin', 'sbagliata')).status, 401);
    const ok = await login('admin', 'prova-di-collaudo'); assert.strictEqual(ok.status, 200);
    const cookie = ok.headers.get('set-cookie').split(';')[0]; assert.ok(/HttpOnly/.test(ok.headers.get('set-cookie')));
    assert.strictEqual((await (await fetch('http://127.0.0.1:14580/api/me', { headers: { cookie } })).json()).user, 'admin');
    assert.strictEqual((await fetch('http://127.0.0.1:14580/api/status', { headers: { cookie } })).status, 200);
    assert.strictEqual((await fetch('http://127.0.0.1:14580/api/backup', { method: 'POST', headers: { cookie } })).status, 403, 'senza x-mel le azioni sono rifiutate');
    assert.strictEqual((await fetch('http://127.0.0.1:14580/api/backup', { method: 'POST', headers: { cookie, 'x-mel': '1' } })).status, 200);
    assert.ok(Array.isArray(await (await fetch('http://127.0.0.1:14580/api/telemetry', { headers: { cookie } })).json()));
    assert.ok((await (await fetch('http://127.0.0.1:14580/api/system', { headers: { cookie } })).json()).host);
    assert.strictEqual((await (await fetch('http://127.0.0.1:14580/api/system', { headers: { cookie } })).json()).config.adminPassHash, undefined, 'l\'hash non esce mai');
    for (let i = 0; i < 5; i++) await login('admin', 'sbagliata');
    assert.strictEqual((await login('admin', 'prova-di-collaudo')).status, 429, 'dopo troppi errori l\'indirizzo è bloccato');
    // regolazioni dal vivo e salvate su file
    assert.strictEqual((await admin('/api/settings', 'POST', { maxRooms: 3, motd: 'Benvenuti a bordo' })).config.maxRooms, 3);
    assert.strictEqual(JSON.parse(fs.readFileSync(cfg, 'utf8')).maxRooms, 3);
    // modalità manutenzione: chi è dentro resta, i nuovi accessi vengono rifiutati
    await admin('/api/settings', 'POST', { maintenance: true, maintenanceMessage: 'aggiornamento' });
    const mm = new Client(14530); assert.strictEqual(await mm.handshake(null, 32767), 32767); mm.sock.destroy();
    await admin('/api/settings', 'POST', { maintenance: false });
    assert.strictEqual((await admin('/api/announce', 'POST', { text: 'Rotta per Risa' })).reached, 2);
    assert.strictEqual(str177(await a.expect('event', 177)), 'msg|Rotta per Risa'); assert.strictEqual(str177(await b.expect('event', 177)), 'msg|Rotta per Risa');
    assert.strictEqual((await admin('/api/room?room=USS%20Test&open=0', 'POST')).ok, true);
    assert.strictEqual(gp.hashGet((await a.expect('event', 253)).params.get(251), 253).v, false); await b.expect('event', 253);
    const m3 = new Client(14530); await m3.handshake(); m3.op(226, [[255, V.str('USS Test')]]); assert.strictEqual((await m3.expect('resp', 226)).ret, 32764); m3.sock.destroy();
    assert.strictEqual((await admin('/api/status')).rooms[0].open, false);
    await admin('/api/room?room=USS%20Test&open=1', 'POST'); await a.expect('event', 253); await b.expect('event', 253);
    // --- A esce: B diventa master, la cache di A viene ripulita
    a.sock.destroy();
    const lv = await b.expect('event', 254); assert.strictEqual(lv.params.get(254).v, 1); assert.strictEqual(lv.params.get(203).v, 2);
    const st = await admin('/api/status');
    assert.strictEqual(st.rooms.length, 1); assert.strictEqual(st.rooms[0].masterClient, 2); assert.strictEqual(st.rooms[0].cachedEvents, 0);
    assert.strictEqual(st.rooms[0].actors[0].props['255'], 'Riker');
    b.sock.destroy(); await new Promise((r) => setTimeout(r, 200));
    assert.strictEqual((await admin('/api/status')).rooms.length, 0);
    // --- storico: la partita conclusa resta con equipaggio e giornale
    const ses = await admin('/api/sessions'); assert.strictEqual(ses.length, 1); assert.deepStrictEqual(ses[0].players.sort(), ['Picard', 'Riker']);
    assert.ok(ses[0].journal.some((j) => j.text === 'Server: Rotta per Risa'));
    // --- esclusione per indirizzo, poi riammissione
    assert.strictEqual((await admin('/api/ban', 'POST', { address: '127.0.0.1', reason: 'prova', minutes: 5 })).ok, true);
    const m4 = new Client(14530); assert.strictEqual(await m4.handshake(null, 32767), 32767); m4.sock.destroy();
    assert.strictEqual((await admin('/api/status')).stats.rejected, 2); // manutenzione ed esclusione
    await admin('/api/unban?address=127.0.0.1', 'POST'); const m5 = new Client(14530); await m5.handshake(); m5.sock.destroy();
    // --- statistiche dei giocatori: classifica, riepilogo globale, andamento
    const post = (o) => fetch('http://127.0.0.1:14580/api/stats', { method: 'POST', body: JSON.stringify(o) });
    await post({ id: 'a'.repeat(32), name: 'Picard', rank: 'Capitano', points: 900, stats: { 'nemici.distrutti': 12, 'salti': 30 } });
    await post({ id: 'b'.repeat(32), name: 'Riker', rank: 'Comandante', points: 700, stats: { 'nemici.distrutti': 20 } });
    assert.strictEqual((await (await post({ id: 'zz' })).json()).ok, false);
    const lb = await (await fetch('http://127.0.0.1:14580/api/leaderboard?stat=nemici.distrutti')).json(); assert.deepStrictEqual(lb.map((x) => x.name), ['Riker', 'Picard']);
    const sum = await (await fetch('http://127.0.0.1:14580/api/summary')).json();
    assert.strictEqual(sum.players, 2); assert.strictEqual(sum.totals['nemici.distrutti'], 32); assert.strictEqual(sum.best['nemici.distrutti'].name, 'Riker'); assert.strictEqual(sum.sessions, 1);
    assert.strictEqual(Object.keys((await admin('/api/players'))[0].history).length, 1);
    // --- fazione rivale persistente
    const rv = await (await fetch('http://127.0.0.1:14580/api/rival?seed=4242')).json(); assert.ok(rv.name && rv.leader); assert.strictEqual(rv.news.length, 1);
    assert.strictEqual((await (await fetch('http://127.0.0.1:14580/api/rival?seed=4242')).json()).name, rv.name, 'stesso seme, stessa fazione');
    const win = await (await fetch('http://127.0.0.1:14580/api/rival?seed=4242', { method: 'POST', body: JSON.stringify({ outcome: 'vittoria', killed: 2, leader: true }) })).json();
    assert.ok(win.ok && win.strength < rv.strength);
    const rv2 = await (await fetch('http://127.0.0.1:14580/api/rival?seed=4242&since=' + rv.now)).json(); assert.notStrictEqual(rv2.leader, rv.leader); assert.strictEqual(rv2.news.length, 1);
    assert.strictEqual((await admin('/api/rivals')).length, 1);
    // --- cronaca della galassia
    assert.deepStrictEqual(await (await fetch('http://127.0.0.1:14580/api/saga?seed=77')).json(), {});
    const sv = await (await fetch('http://127.0.0.1:14580/api/saga?seed=77', { method: 'POST', body: JSON.stringify({ onore: 3, infamia: 1, log: [{ t: 1, text: 'Primo contatto riuscito' }], pending: [] }) })).json(); assert.ok(sv.ok);
    assert.strictEqual((await (await fetch('http://127.0.0.1:14580/api/saga?seed=77')).json()).onore, 3);
    assert.strictEqual((await (await fetch('http://127.0.0.1:14580/api/saga?seed=77', { method: 'POST', body: '[1,2]' })).json()).ok, false);
    assert.strictEqual((await admin('/api/sagas'))[0].seed, 77);
    console.log('COLLAUDO SUPERATO');
  } catch (e) { failed = true; console.error('COLLAUDO FALLITO:', e.message, '\n--- log server ---\n' + out); }
  srv.kill(); await new Promise((r) => srv.on('exit', r));
  if (!failed) {
    // all'arresto i dati finiscono su disco
    const saved = JSON.parse(fs.readFileSync(path.join(dataDir, 'sessions.json'), 'utf8')); const ok = saved.length === 1 && fs.existsSync(path.join(dataDir, 'stats.json'));
    if (!ok) { failed = true; console.error('COLLAUDO FALLITO: dati non salvati all\'arresto'); } else console.log('dati salvati all\'arresto');
  }
  fs.unlinkSync(cfg); fs.rmSync(dataDir, { recursive: true, force: true }); process.exit(failed ? 1 : 0);
})();

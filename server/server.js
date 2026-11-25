#!/usr/bin/env node
'use strict';
// Melchior Subspace Relay — server locale compatibile con Photon (PUN classico) per PULSAR: Lost Colony.
// Avvio: node server.js [config.json]
// Credenziali del pannello: echo "password" | node server.js config.json --set-password admin
const net = require('net');
const http = require('http');
const fs = require('fs');
const path = require('path');
const { Peer } = require('./lib/peer');
const { Hub } = require('./lib/loadbalancing');
const { Rivals } = require('./lib/rivals');
const { Auth, hashPassword, verifyPassword } = require('./lib/auth');
const { Telemetry } = require('./lib/telemetry');

const defaults = {
  bind: '0.0.0.0',          // interfaccia di ascolto
  publicHost: '127.0.0.1',  // indirizzo che i client usano per raggiungere il game server ("auto" = quello a cui si sono collegati)
  masterPort: 4530,
  gamePort: 4531,
  adminBind: '127.0.0.1',   // il pannello resta locale se non lo cambi
  adminPort: 4580,
  adminUser: '',            // credenziali del pannello (la password è salvata solo come hash scrypt)
  adminPassHash: '',
  adminToken: '',           // facoltativo: accesso per script con l'intestazione x-admin-token
  appIds: [],               // vuoto = accetta qualsiasi AppId
  motd: '',                 // messaggio mostrato a chi entra in una partita
  maxRooms: 0,              // 0 = nessun limite
  maxPerAddress: 0,         // connessioni di gioco per indirizzo; 0 = nessun limite
  backupDays: 14,           // copie giornaliere delle statistiche da conservare
  sessionsKept: 200,        // partite concluse tenute nello storico
  maintenance: false,       // in manutenzione non entrano nuovi giocatori
  maintenanceMessage: '',
};
// regolazioni modificabili dal pannello, con il loro controllo
const EDITABLE = {
  motd: (v) => String(v).slice(0, 200), maintenanceMessage: (v) => String(v).slice(0, 120), maintenance: (v) => !!v,
  maxRooms: (v) => Math.max(0, Math.min(100, Math.floor(Number(v) || 0))), maxPerAddress: (v) => Math.max(0, Math.min(16, Math.floor(Number(v) || 0))),
  backupDays: (v) => Math.max(1, Math.min(365, Math.floor(Number(v) || 14))), sessionsKept: (v) => Math.max(10, Math.min(5000, Math.floor(Number(v) || 200))),
  appIds: (v) => (Array.isArray(v) ? v : String(v).split(',')).map((x) => String(x).trim()).filter(Boolean).slice(0, 10),
};
const cfgPath = process.argv[2] && !process.argv[2].startsWith('--') ? process.argv[2] : path.join(__dirname, 'config.json');
const fileCfg = () => (fs.existsSync(cfgPath) ? JSON.parse(fs.readFileSync(cfgPath, 'utf8')) : {});
const cfg = { ...defaults, ...fileCfg() };
function saveCfg(changes) { const f = { ...fileCfg(), ...changes }; fs.writeFileSync(cfgPath + '.tmp', JSON.stringify(f, null, 2) + '\n'); fs.renameSync(cfgPath + '.tmp', cfgPath); }

// impostazione delle credenziali da riga di comando, con la password letta da stdin
const setIdx = process.argv.indexOf('--set-password');
if (setIdx >= 0) {
  const user = process.argv[setIdx + 1] || 'admin';
  const pass = fs.readFileSync(0, 'utf8').replace(/\r?\n$/, '');
  if (pass.length < 8) { console.error('La password deve avere almeno 8 caratteri.'); process.exit(1); }
  saveCfg({ adminUser: user, adminPassHash: hashPassword(pass) });
  console.log(`Credenziali del pannello impostate per l'utente "${user}".`); process.exit(0);
}

const dataDir = cfg.dataDir || path.join(__dirname, 'data');
const logLines = [];
function log(...a) {
  const line = `${new Date().toISOString().slice(0, 19).replace('T', ' ')} ${a.join(' ')}`;
  logLines.push(line); if (logLines.length > 3000) logLines.shift();
  console.log(line);
}

// ---------------------------------------------------------------- dati persistenti
function load(name, fallback) { try { return JSON.parse(fs.readFileSync(path.join(dataDir, name), 'utf8')); } catch { return fallback; } }
function save(name, value) {
  try { fs.mkdirSync(dataDir, { recursive: true }); const f = path.join(dataDir, name); fs.writeFileSync(f + '.tmp', JSON.stringify(value)); fs.renameSync(f + '.tmp', f); }
  catch (e) { log(`[dati] ${name} non salvato:`, e.message); }
}
const statsFile = cfg.statsFile ? path.basename(cfg.statsFile) : 'stats.json';
let players = load(statsFile, {});
let sessions = load('sessions.json', []);
let sagas = load('sagas.json', {}); // cronaca di ogni galassia (archi, fama, conseguenze), scritta dall'host della partita
let users = load('users.json', {}); // registro dei giocatori visti dal server: nomi, indirizzi, accessi, note
let audit = load('audit.json', []);  // azioni dell'amministratore
const dirty = new Set();
const rivals = new Rivals(load('rivals.json', {}), () => dirty.add('rivals'), (...a) => log(...a));
function flush() {
  if (dirty.has('stats')) save(statsFile, players);
  if (dirty.has('sessions')) save('sessions.json', sessions);
  if (dirty.has('sagas')) save('sagas.json', sagas);
  if (dirty.has('rivals')) save('rivals.json', rivals.data);
  if (dirty.has('users')) save('users.json', users);
  if (dirty.has('audit')) save('audit.json', audit);
  if (dirty.has('bans')) save('bans.json', { users: [...hub.bans.users], addresses: [...hub.bans.addresses] });
  dirty.clear();
}
function addAudit(ip, action, detail) { audit.push({ t: Date.now(), ip, action, detail: String(detail || '').slice(0, 200) }); if (audit.length > 1000) audit.shift(); dirty.add('audit'); }

const hub = new Hub(cfg, log);
{ // esclusioni salvate (anche nel vecchio formato: semplici elenchi)
  const b = load('bans.json', { users: [], addresses: [] });
  for (const [map, list] of [[hub.bans.users, b.users || []], [hub.bans.addresses, b.addresses || []]])
    for (const x of list) { if (typeof x === 'string') map.set(x, { reason: '', until: 0, at: Date.now() }); else if (Array.isArray(x)) map.set(x[0], x[1]); }
}
hub.onRoomClosed = (room) => {
  sessions.push({ name: room.name, started: room.created, ended: Date.now(), durationSec: Math.round((Date.now() - room.created) / 1000),
    players: [...room.names.values()], peak: room.peak, events: room.eventCount, journal: room.journal.slice(-25) });
  if (sessions.length > cfg.sessionsKept) sessions = sessions.slice(-cfg.sessionsKept);
  dirty.add('sessions');
};
hub.onAuth = (session, address, kind) => {
  const u = users[session.userId] || (users[session.userId] = { names: [], ips: [], first: Date.now(), logins: 0, notes: '', warnings: 0 });
  u.last = Date.now(); if (kind === 'master') u.logins++; u.version = session.appVersion;
  if (address && !u.ips.includes(address)) { u.ips.push(address); if (u.ips.length > 10) u.ips.shift(); }
  dirty.add('users');
};
// i nomi arrivano con le proprietà del giocatore: li si raccoglie dalle partite in corso
setInterval(() => {
  for (const room of hub.rooms.values()) for (const a of room.actors.values()) {
    const u = users[a.userId]; const n = room.nameOf(a); if (!u || !n || n === a.userId) continue;
    if (u.names[u.names.length - 1] !== n) { u.names = u.names.filter((x) => x !== n).concat(n).slice(-8); dirty.add('users'); }
    u.last = Date.now();
  }
}, 5000).unref();
setInterval(flush, 5000).unref();
const telemetry = new Telemetry(hub, dataDir);
const auth = new Auth(cfg, log, (ip, action, detail) => addAudit(ip, action, detail));

function listen(port, handler, name) {
  const srv = net.createServer((sock) => new Peer(sock, handler, log));
  srv.on('error', (e) => { log(`[${name}] errore: ${e.message}`); process.exit(1); });
  srv.listen(port, cfg.bind, () => log(`[${name}] in ascolto su ${cfg.bind}:${port} (TCP)`));
  return srv;
}
listen(cfg.masterPort, hub.master(), 'master');
listen(cfg.gamePort, hub.game(), 'game');

// ---------------------------------------------------------------- statistiche dei giocatori
const today = () => new Date().toISOString().slice(0, 10);
function putStats(o) {
  if (!o || typeof o.id !== 'string' || !/^[0-9a-f]{16,64}$/i.test(o.id)) return false;
  const stats = {};
  for (const [k, v] of Object.entries(o.stats || {}).slice(0, 200)) if (typeof v === 'number' && isFinite(v) && k.length <= 48) stats[k] = v;
  const prev = players[o.id] || {};
  const history = { ...(prev.history || {}) }; history[today()] = Number(o.points) || 0;
  for (const d of Object.keys(history).sort().slice(0, -60)) delete history[d]; // due mesi di andamento
  players[o.id] = { name: String(o.name || 'Senza nome').slice(0, 32), rank: String(o.rank || '').slice(0, 32), points: Number(o.points) || 0, stats,
    first: prev.first || Date.now(), seen: Date.now(), history };
  dirty.add('stats'); return true;
}
function leaderboard(stat, n) {
  const val = (p) => (stat === 'points' ? p.points : (p.stats[stat] || 0));
  return Object.values(players).map((p) => ({ name: p.name, rank: p.rank, points: p.points, value: val(p), seen: p.seen }))
    .sort((a, b) => b.value - a.value).slice(0, Math.max(1, Math.min(100, n || 20)));
}
// Statistiche globali: somma di ogni voce su tutti i giocatori, più i numeri delle partite giocate sul server.
function summary() {
  const totals = {}; const best = {};
  for (const p of Object.values(players)) {
    for (const [k, v] of Object.entries(p.stats || {})) {
      totals[k] = (totals[k] || 0) + v;
      if (!best[k] || v > best[k].value) best[k] = { name: p.name, value: v };
    }
  }
  const list = Object.values(players);
  return { players: list.length, activeLastWeek: list.filter((p) => Date.now() - p.seen < 7 * 86400000).length,
    pointsTotal: list.reduce((s, p) => s + p.points, 0), sessions: sessions.length, hoursPlayed: Math.round(sessions.reduce((s, x) => s + x.durationSec, 0) / 360) / 10,
    longestSessionSec: sessions.reduce((m, x) => Math.max(m, x.durationSec), 0), totals, best };
}

// ---------------------------------------------------------------- copie di sicurezza
const backupDir = path.join(dataDir, 'backup');
function backup() { // giornaliera, solo statistiche
  try {
    const f = path.join(backupDir, `stats-${today()}.json`);
    if (!Object.keys(players).length || fs.existsSync(f)) return;
    fs.mkdirSync(backupDir, { recursive: true }); fs.writeFileSync(f, JSON.stringify(players));
    const old = fs.readdirSync(backupDir).filter((x) => /^stats-\d{4}-\d\d-\d\d\.json$/.test(x)).sort().slice(0, -Math.max(1, cfg.backupDays));
    for (const x of old) fs.unlinkSync(path.join(backupDir, x));
    log(`[dati] copia di sicurezza delle statistiche: ${path.basename(f)}`);
  } catch (e) { log('[dati] copia di sicurezza fallita:', e.message); }
}
function fullBackup() { // completa, su richiesta
  flush();
  const bundle = { made: Date.now(), players, sessions, sagas, rivals: rivals.data, users, bans: { users: [...hub.bans.users], addresses: [...hub.bans.addresses] } };
  fs.mkdirSync(backupDir, { recursive: true });
  const name = `completo-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.json`;
  fs.writeFileSync(path.join(backupDir, name), JSON.stringify(bundle));
  const old = fs.readdirSync(backupDir).filter((x) => x.startsWith('completo-')).sort().slice(0, -20);
  for (const x of old) fs.unlinkSync(path.join(backupDir, x));
  log(`[dati] copia di sicurezza completa: ${name}`); return name;
}
function backups() {
  try { return fs.readdirSync(backupDir).filter((x) => /^[a-z0-9-]+\.json$/.test(x)).map((x) => { const st = fs.statSync(path.join(backupDir, x)); return { name: x, kb: Math.round(st.size / 102.4) / 10, t: st.mtimeMs }; }).sort((a, b) => b.t - a.t); }
  catch { return []; }
}
backup(); setInterval(backup, 3600000).unref();

// ---------------------------------------------------------------- pannello e API
function body(req, cb) {
  let data = ''; let over = false;
  req.on('data', (d) => { data += d; if (data.length > 65536) { over = true; req.destroy(); } });
  req.on('end', () => { if (over) return; let o = null; try { o = data ? JSON.parse(data) : {}; } catch { o = null; } cb(o); });
}
const userRow = (id) => { const u = users[id] || {}; const ban = hub.banEntry(id, ''); return { id, ...u, banned: ban, online: [...hub.gamePeers, ...hub.masterPeers].some((p) => p.session && p.session.userId === id) }; };

http.createServer((req, res) => {
  const url = new URL(req.url, 'http://x'); const q = url.searchParams; const post = req.method === 'POST';
  const json = (o, code = 200, headers = {}) => { res.writeHead(code, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', ...headers }); res.end(JSON.stringify(o)); };
  const route = url.pathname; const ip = Auth.ip(req);
  // ---- aperte a tutti: la pagina (che chiede l'accesso da sé) e le API usate dai client di gioco
  if (route === '/') { res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store', 'x-frame-options': 'DENY', 'referrer-policy': 'no-referrer' }); return res.end(fs.readFileSync(path.join(__dirname, 'admin.html'))); }
  if (route === '/api/health') return json({ ok: true, uptimeSec: Math.round(process.uptime()), rooms: hub.rooms.size, players: hub.gamePeers.size, maintenance: !!cfg.maintenance });
  if (route === '/api/leaderboard') return json(leaderboard(q.get('stat') || 'points', Number(q.get('n'))));
  if (route === '/api/summary') return json(summary());
  if (route === '/api/rival' && !post) {
    const seed = Number(q.get('seed')); if (!Number.isInteger(seed)) return json({ error: 'seme mancante' }, 400);
    const f = rivals.get(seed); const since = Number(q.get('since')) || 0;
    return json({ name: f.name, leader: f.leader, temper: f.temper, strength: Math.round(f.strength), grudge: Math.round(f.grudge), encounters: f.encounters, defeats: f.defeats, victories: f.victories, news: rivals.unread(seed, since).slice(-5), now: Date.now() });
  }
  if (route === '/api/rival' && post) return body(req, (o) => {
    const seed = Number(q.get('seed')); if (!o || !Number.isInteger(seed)) return json({ ok: false }, 400);
    const f = rivals.report(seed, { outcome: String(o.outcome || ''), killed: Math.max(0, Math.min(20, Number(o.killed) || 0)), leader: !!o.leader });
    json({ ok: !!f, strength: f ? Math.round(f.strength) : 0 }, f ? 200 : 400);
  });
  if (route === '/api/saga' && !post) { const seed = Number(q.get('seed')); if (!Number.isInteger(seed)) return json({ error: 'seme mancante' }, 400); return json((sagas[seed] || {}).state || {}); }
  if (route === '/api/saga' && post) {
    const seed = Number(q.get('seed'));
    return body(req, (o) => {
      if (!Number.isInteger(seed) || !o || typeof o !== 'object' || Array.isArray(o) || JSON.stringify(o).length > 200000) return json({ ok: false }, 400);
      const prev = sagas[seed] || { first: Date.now() };
      if (Array.isArray(o.log) && (!prev.state || JSON.stringify(prev.state.log) !== JSON.stringify(o.log))) {
        const last = o.log[o.log.length - 1]; if (last && last.text) log(`[cronaca] galassia ${seed}: ${String(last.text).slice(0, 120)}`);
      }
      sagas[seed] = { first: prev.first, updated: Date.now(), state: o }; dirty.add('sagas'); json({ ok: true });
    });
  }
  if (route === '/api/stats' && post) return body(req, (o) => { const ok = putStats(o); json({ ok }, ok ? 200 : 400); });

  // ---- accesso
  if (route === '/api/login' && post) return body(req, (o) => {
    const r = auth.login(req, o && o.user, o && o.pass);
    return r.ok ? json({ ok: true }, 200, { 'set-cookie': r.cookie }) : json({ ok: false, error: r.error }, r.status);
  });
  if (route === '/api/logout' && post) { addAudit(ip, 'uscita', ''); return json({ ok: true }, 200, { 'set-cookie': auth.logout(req) }); }
  const who = auth.check(req);
  if (route === '/api/me') return json(who ? { ok: true, user: who.user, configured: auth.configured } : { ok: false, configured: auth.configured });
  if (!route.startsWith('/api/')) return json({ error: 'non trovato' }, 404);
  if (!who) return json({ error: 'accesso richiesto' }, 401);
  // le richieste che cambiano qualcosa devono venire dal pannello (intestazione propria) o da uno script col token
  if (post && who.user !== 'token' && req.headers['x-mel'] !== '1') return json({ error: 'richiesta non ammessa' }, 403);

  // ---- lettura
  if (!post) {
    switch (route) {
      case '/api/status': return json(hub.snapshot());
      case '/api/log': { const f = (q.get('q') || '').toLowerCase(); const lines = f ? logLines.filter((l) => l.toLowerCase().includes(f)) : logLines; return json(lines.slice(-Math.max(1, Math.min(3000, Number(q.get('n')) || 400)))); }
      case '/api/sessions': return json(sessions.slice(-Math.max(1, Math.min(500, Number(q.get('n')) || 30))).reverse());
      case '/api/sagas': return json(Object.entries(sagas).map(([seed, x]) => ({ seed: Number(seed), first: x.first, updated: x.updated, ...x.state })).sort((a, b) => b.updated - a.updated));
      case '/api/rivals': return json(rivals.all().map((f) => ({ ...f, strength: Math.round(f.strength), grudge: Math.round(f.grudge) })));
      case '/api/players': return json(Object.entries(players).map(([id, p]) => ({ id, ...p })).sort((a, b) => b.points - a.points));
      case '/api/users': return json(Object.keys(users).map(userRow).sort((a, b) => (b.last || 0) - (a.last || 0)));
      case '/api/telemetry': return json(q.get('range') === 'day' ? telemetry.coarse : telemetry.fine);
      case '/api/system': return json({ ...telemetry.system(), config: Object.fromEntries(Object.keys(defaults).filter((k) => !['adminPassHash', 'adminToken'].includes(k)).map((k) => [k, cfg[k]])), dataDir, configPath: cfgPath });
      case '/api/audit': return json(audit.slice(-300).reverse());
      case '/api/admins': return json({ sessions: auth.list(), lockouts: auth.lockouts(), me: who.user });
      case '/api/backups': return json(backups());
      case '/api/backup/file': {
        const name = q.get('name') || ''; if (!/^[a-z0-9-]+\.json$/.test(name) || !fs.existsSync(path.join(backupDir, name))) return json({ error: 'non trovato' }, 404);
        addAudit(ip, 'scaricata copia', name);
        res.writeHead(200, { 'content-type': 'application/json', 'content-disposition': `attachment; filename="${name}"` }); return res.end(fs.readFileSync(path.join(backupDir, name)));
      }
      case '/api/export': { flush(); addAudit(ip, 'esportati i dati', ''); res.writeHead(200, { 'content-type': 'application/json', 'content-disposition': `attachment; filename="subspace-relay-${today()}.json"` });
        return res.end(JSON.stringify({ made: Date.now(), players, sessions, sagas, rivals: rivals.data, users, audit }, null, 1)); }
    }
    return json({ error: 'non trovato' }, 404);
  }

  // ---- azioni (con registro)
  const done = (action, detail, out) => { addAudit(ip, action, detail); log(`[admin] ${action}${detail ? ': ' + detail : ''}`); return json({ ok: true, ...(out || {}) }); };
  const room = q.get('room') || '';
  switch (route) {
    case '/api/kick': return hub.kick(room, Number(q.get('actor'))) ? done('espulso', `${room} attore ${q.get('actor')}`) : json({ ok: false }, 404);
    case '/api/kickuser': return done('espulso giocatore', q.get('userId'), { closed: hub.kickUser(q.get('userId') || '') });
    case '/api/kickall': return done('espulsi tutti', '', { closed: hub.kickAll() });
    case '/api/ban': return body(req, (o) => {
      o = o || {}; const w = { userId: o.userId || q.get('userId') || '', address: o.address || q.get('address') || '', reason: o.reason || '', minutes: Math.max(0, Number(o.minutes) || 0) };
      if (!w.userId && !w.address) return json({ ok: false }, 400);
      hub.ban(w); dirty.add('bans'); done('escluso', `${w.userId || w.address}${w.minutes ? ` per ${w.minutes} min` : ''}${w.reason ? ' — ' + w.reason : ''}`);
    });
    case '/api/unban': { const w = { userId: q.get('userId') || '', address: q.get('address') || '' }; const ok = hub.unban(w); dirty.add('bans'); return ok ? done('riammesso', w.userId || w.address) : json({ ok: false }, 404); }
    case '/api/warn': return body(req, (o) => {
      const text = (o && o.text) || ''; if (!hub.whisper(room, Number(q.get('actor')), 'AVVISO DEL SERVER: ' + text)) return json({ ok: false }, 404);
      const r = hub.rooms.get(room); const a = r && r.actors.get(Number(q.get('actor'))); if (a && users[a.userId]) { users[a.userId].warnings = (users[a.userId].warnings || 0) + 1; dirty.add('users'); }
      done('avviso', `${room} attore ${q.get('actor')}: ${text}`);
    });
    case '/api/whisper': return body(req, (o) => (hub.whisper(room, Number(q.get('actor')), (o && o.text) || '') ? done('messaggio privato', `${room} attore ${q.get('actor')}`) : json({ ok: false }, 404)));
    case '/api/announce': return body(req, (o) => { const n = hub.announce(room, (o && o.text) || ''); done('annuncio', `${room || 'tutte le partite'}: ${(o && o.text) || ''}`, { reached: n }); });
    case '/api/room': {
      const flag = (k) => (q.has(k) ? q.get(k) === '1' : undefined);
      return hub.setRoom(room, { open: flag('open'), visible: flag('visible'), max: q.has('max') ? Number(q.get('max')) : undefined }) ? done('partita modificata', `${room} ${[...q].filter(([k]) => k !== 'room').map(([k, v]) => k + '=' + v).join(' ')}`) : json({ ok: false }, 404);
    }
    case '/api/close': return hub.closeRoom(room) ? done('partita terminata', room) : json({ ok: false }, 404);
    case '/api/user/note': return body(req, (o) => { const id = q.get('userId'); if (!users[id]) return json({ ok: false }, 404); users[id].notes = String((o && o.notes) || '').slice(0, 500); dirty.add('users'); done('nota giocatore', id); });
    case '/api/settings': return body(req, (o) => {
      if (!o || typeof o !== 'object') return json({ ok: false }, 400);
      const changes = {}; for (const [k, fix] of Object.entries(EDITABLE)) if (k in o) changes[k] = fix(o[k]);
      Object.assign(cfg, changes); saveCfg(changes); hub.cfg = cfg;
      done('regolazioni', Object.entries(changes).map(([k, v]) => `${k}=${Array.isArray(v) ? v.join(',') : v}`).join(' '), { config: changes });
    });
    case '/api/backup': return done('copia di sicurezza', '', { name: fullBackup() });
    case '/api/restart': { done('riavvio', ''); flush(); setTimeout(() => { log('[server] riavvio richiesto dal pannello'); for (const r of hub.rooms.values()) hub.onRoomClosed(r); flush(); process.exit(75); }, 500); return; }
    case '/api/password': return body(req, (o) => {
      if (!o || !auth.configured || !verifyPassword(o.old, cfg.adminPassHash)) { addAudit(ip, 'cambio password rifiutato', ''); return json({ ok: false, error: 'Password attuale errata' }, 403); }
      if (String(o.next || '').length < 8) return json({ ok: false, error: 'La nuova password deve avere almeno 8 caratteri' }, 400);
      cfg.adminPassHash = hashPassword(o.next); saveCfg({ adminPassHash: cfg.adminPassHash }); done('password cambiata', '');
    });
    case '/api/admins/revoke': return auth.revoke(q.get('id') || '') ? done('sessione revocata', q.get('id')) : json({ ok: false }, 404);
    case '/api/admins/revokeall': return done('sessioni revocate', '', { closed: auth.revokeAll() });
    case '/api/player/delete': { const id = q.get('id'); if (!players[id]) return json({ ok: false }, 404); const n = players[id].name; delete players[id]; dirty.add('stats'); return done('profilo eliminato', `${n} (${id})`); }
    case '/api/leaderboard/reset': { fullBackup(); players = {}; dirty.add('stats'); return done('classifica azzerata', 'copia di sicurezza fatta prima'); }
    case '/api/saga/delete': { const s = q.get('seed'); if (!sagas[s]) return json({ ok: false }, 404); delete sagas[s]; dirty.add('sagas'); return done('cronaca eliminata', 'galassia ' + s); }
    case '/api/rival/delete': { const s = q.get('seed'); if (!rivals.data[s]) return json({ ok: false }, 404); delete rivals.data[s]; dirty.add('rivals'); return done('fazione rivale eliminata', 'galassia ' + s); }
    case '/api/sessions/clear': { sessions = []; dirty.add('sessions'); return done('storico partite svuotato', ''); }
    case '/api/log/clear': { logLines.length = 0; return done('registro svuotato', ''); }
  }
  json({ error: 'non trovato' }, 404);
}).listen(cfg.adminPort, cfg.adminBind, () => log(`[admin] pannello su http://${cfg.adminBind}:${cfg.adminPort}/ (${auth.configured ? `accesso riservato a "${cfg.adminUser}"` : cfg.adminToken ? 'solo token' : 'SENZA PROTEZIONE'})`));

// Arresto pulito: i dati in memoria finiscono su disco prima di uscire.
for (const sig of ['SIGTERM', 'SIGINT']) {
  process.on(sig, () => {
    log(`[server] arresto (${sig})`);
    for (const room of hub.rooms.values()) hub.onRoomClosed(room);
    flush(); process.exit(0);
  });
}

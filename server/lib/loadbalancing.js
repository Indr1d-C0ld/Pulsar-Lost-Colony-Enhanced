'use strict';
// Logica LoadBalancing di Photon (Master + Game server) quanto basta a PUN classico:
// autenticazione, lobby, stanze, proprietà, inoltro eventi, cache eventi, master client.
const crypto = require('crypto');
const gp = require('./gp16');
const { Peer } = require('./peer');
const { T, V } = gp;

const OP = { AUTH_ONCE: 231, AUTH: 230, JOIN_LOBBY: 229, LEAVE_LOBBY: 228, CREATE: 227, JOIN: 226, JOIN_RANDOM: 225,
  LEAVE: 254, RAISE_EVENT: 253, SET_PROPS: 252, GET_PROPS: 251, CHANGE_GROUPS: 248, FIND_FRIENDS: 222,
  LOBBY_STATS: 221, GET_REGIONS: 220, SETTINGS: 218, GET_GAME_LIST: 217 };
const EV = { JOIN: 255, LEAVE: 254, PROPS_CHANGED: 253, GAME_LIST: 230, GAME_LIST_UPDATE: 229, APP_STATS: 226 };
const P = { ROOM_NAME: 255, ACTOR_NR: 254, TARGET_ACTOR: 253, ACTOR_LIST: 252, PROPERTIES: 251, BROADCAST: 250,
  PLAYER_PROPS: 249, GAME_PROPS: 248, CACHE: 247, RECEIVER_GROUP: 246, DATA: 245, CODE: 244, CLEANUP: 241, GROUP: 240,
  REMOVE: 239, ADD: 238, EMPTY_ROOM_TTL: 236, EXPECTED: 231, ADDRESS: 230, PEER_COUNT: 229, GAME_COUNT: 228,
  MASTER_PEER_COUNT: 227, USER_ID: 225, APP_ID: 224, GAME_LIST: 222, SECRET: 221, APP_VERSION: 220, JOIN_MODE: 215,
  MASTER_CLIENT_ID: 203, NICKNAME: 202, ROOM_FLAGS: 191 };
const GP = { MAX_PLAYERS: 255, IS_VISIBLE: 254, IS_OPEN: 253, PLAYER_COUNT: 252, REMOVED: 251, LOBBY_PROPS: 250,
  CLEANUP: 249, MASTER_CLIENT_ID: 248 };
const ERR = { OK: 0, NOT_ALLOWED: -3, INVALID_OP: -2, INTERNAL: -1, AUTH_FAILED: 32767, ALREADY_EXISTS: 32766,
  FULL: 32765, CLOSED: 32764, NO_MATCH: 32760, NOT_FOUND: 32758 };
const CACHE = { NONE: 0, MERGE: 1, REPLACE: 2, REMOVE: 3, ADD_ROOM: 4, ADD_ROOM_GLOBAL: 5, REMOVE_ROOM: 6, REMOVE_ROOM_LEFT: 7 };

const STATION_EVENT = 177; // evento delle stazioni di Melchior: stato delle navi, giornale di bordo, annunci del server
const val = (p, k, d) => { const x = p.get(k); return x === undefined || x.t === T.NULL ? d : x.v; };

class Room {
  constructor(name, hub) {
    this.name = name; this.hub = hub; this.actors = new Map(); this.nextActor = 1;
    this.props = V.hash(); this.cache = []; this.actorCache = new Map();
    this.masterId = 0; this.cleanup = true; this.created = Date.now(); this.lobbyKeys = [];
    this.reserved = 0; this.eventCount = 0;
    // telemetria di plancia letta dagli eventi di stazione, e dati per lo storico delle sessioni
    this.ships = new Map(); this.journal = []; this.peak = 0; this.names = new Map();
    this.rate = 0; this.rateCount = 0; this.rateStart = Date.now();
  }
  telemetry(msg) {
    if (msg.startsWith('msg|')) return;
    if (msg.startsWith('log|')) {
      const parts = msg.split('|'); const text = parts.slice(2).join('|').slice(0, 160);
      this.journal.push({ t: Date.now(), ship: Number(parts[1]) || 0, text }); if (this.journal.length > 200) this.journal.shift();
      return;
    }
    const bar = msg.indexOf('|'); const id = Number(msg.slice(0, bar)); if (bar <= 0 || !Number.isInteger(id)) return;
    const values = {};
    for (const part of msg.slice(bar + 1).split(';')) { const eq = part.indexOf('='); if (eq > 0) { const v = Number(part.slice(eq + 1)); if (isFinite(v)) values[part.slice(0, eq).slice(0, 32)] = v; } }
    if (Object.keys(values).length === 0) this.ships.delete(id); else if (this.ships.size < 64 || this.ships.has(id)) this.ships.set(id, { values, at: Date.now() });
  }
  tick() { const now = Date.now(); if (now - this.rateStart >= 5000) { this.rate = this.rateCount / ((now - this.rateStart) / 1000); this.rateCount = 0; this.rateStart = now; } }
  nameOf(actor) { const n = gp.hashGet(actor.props, 255); return n && typeof n.v === 'string' && n.v ? n.v : actor.userId; }
  get maxPlayers() { const m = gp.hashGet(this.props, GP.MAX_PLAYERS); return m ? m.v : 0; }
  get isOpen() { const m = gp.hashGet(this.props, GP.IS_OPEN); return m ? m.v : true; }
  get isVisible() { const m = gp.hashGet(this.props, GP.IS_VISIBLE); return m ? m.v : true; }
  get count() { return this.actors.size; }

  lobbyProps() {
    const h = V.hash();
    for (const [k, v] of this.props.v) {
      if ((k.t === T.BYTE && [GP.MAX_PLAYERS, GP.IS_OPEN].includes(k.v)) || (k.t === T.STRING && this.lobbyKeys.includes(k.v))) h.v.push([k, v]);
    }
    h.v.push([V.byte(GP.PLAYER_COUNT), V.byte(this.count)]);
    return h;
  }

  mergeProps(target, incoming) {
    for (const [k, v] of incoming.v) {
      if (k.t === T.BYTE && k.v === GP.LOBBY_PROPS && (v.t === T.STRARR || v.t === T.ARRAY)) this.lobbyKeys = Array.from(v.v);
      if (k.t === T.BYTE && k.v === GP.MASTER_CLIENT_ID && target === this.props) { if (this.actors.has(v.v)) this.masterId = v.v; continue; }
      gp.hashSet(target, k, v);
    }
  }

  broadcast(bytes, { except = null, only = null, channel = 0, reliable = true } = {}) {
    for (const a of this.actors.values()) {
      if (except && a === except) continue;
      if (only && !only.includes(a.nr)) continue;
      a.peer.sendEventBytes(bytes, channel, reliable);
    }
  }
  actorList() { return V.intArr([...this.actors.keys()]); }
}

class Hub {
  constructor(cfg, log) {
    this.cfg = cfg; this.log = log; this.rooms = new Map(); this.tokens = new Map();
    this.masterPeers = new Set(); this.gamePeers = new Set(); this.started = Date.now();
    this.stats = { eventsRelayed: 0, bytesRelayed: 0, logins: 0, rejected: 0 };
    // esclusioni: identificativo o indirizzo -> { reason, until (0 = per sempre), at }
    this.bans = { users: new Map(), addresses: new Map() }; this.onRoomClosed = null; this.onAuth = null;
    // i gettoni di sessione inutilizzati scadono dopo un giorno
    setInterval(() => { const old = Date.now() - 86400000; const live = new Set([...this.masterPeers, ...this.gamePeers].map((x) => x.session)); for (const [t, s] of this.tokens) if (s.created < old && !live.has(s)) this.tokens.delete(t); for (const r of this.rooms.values()) r.tick(); }, 5000).unref();
  }
  static addressOf(peer) { return String((peer.sock && peer.sock.remoteAddress) || '').replace(/^::ffff:/, ''); }
  banEntry(userId, address) {
    const now = Date.now();
    for (const [map, key] of [[this.bans.users, userId], [this.bans.addresses, address]]) {
      const b = key && map.get(key); if (!b) continue;
      if (b.until && b.until < now) { map.delete(key); continue; }
      return b;
    }
    return null;
  }
  banned(userId, address) { return !!this.banEntry(userId, address); }
  // Indirizzo del game server da comunicare al client. Con publicHost "auto" è lo stesso indirizzo
  // (di questa macchina) a cui il client si è già collegato: niente nomi da risolvere, niente IP da configurare.
  gameAddress(peer) {
    let host = this.cfg.publicHost;
    if (!host || host === 'auto') {
      host = (peer && peer.sock && peer.sock.localAddress) || '127.0.0.1';
      host = host.replace(/^::ffff:/, '');
      if (host.includes(':')) host = `[${host}]`;
    }
    return `${host}:${this.cfg.gamePort}`;
  }

  // ---------------------------------------------------------------- autenticazione
  authenticate(peer, op, kind) {
    const p = op.params;
    const secret = val(p, P.SECRET);
    let session = secret && this.tokens.get(secret);
    if (!session) {
      const appId = val(p, P.APP_ID, '');
      if (this.cfg.appIds.length && !this.cfg.appIds.includes(appId)) return peer.respond(op.code, ERR.AUTH_FAILED, new Map(), 'AppId non ammesso', true);
      session = { userId: val(p, P.USER_ID) || crypto.randomUUID(), appVersion: val(p, P.APP_VERSION, ''), token: crypto.randomBytes(24).toString('base64'), created: Date.now() };
      this.tokens.set(session.token, session); this.stats.logins++;
    }
    const address = Hub.addressOf(peer);
    const ban = this.banEntry(session.userId, address);
    if (ban) {
      this.stats.rejected++; this.log(`[${kind}] rifiutato ${session.userId} da ${address}: escluso dal server${ban.reason ? ' (' + ban.reason + ')' : ''}`);
      return peer.respond(op.code, ERR.AUTH_FAILED, new Map(), 'Accesso negato da questo server' + (ban.reason ? ': ' + ban.reason : ''), true);
    }
    if (this.cfg.maintenance && !peer.session) {
      this.stats.rejected++; this.log(`[${kind}] rifiutato ${session.userId}: server in manutenzione`);
      return peer.respond(op.code, ERR.AUTH_FAILED, new Map(), 'Server in manutenzione' + (this.cfg.maintenanceMessage ? ': ' + this.cfg.maintenanceMessage : ''), true);
    }
    if (this.cfg.maxPerAddress > 0 && kind === 'game' && [...this.gamePeers].filter((x) => x !== peer && Hub.addressOf(x) === address).length >= this.cfg.maxPerAddress) {
      this.stats.rejected++; return peer.respond(op.code, ERR.AUTH_FAILED, new Map(), 'Troppe connessioni dallo stesso indirizzo', true);
    }

    peer.session = session; peer.authed = true; peer.kind = kind;
    if (this.onAuth) try { this.onAuth(session, address, kind); } catch { /* registro facoltativo */ }
    this.log(`[${kind}] autenticato ${session.userId} versione "${session.appVersion}" da ${peer.id}`);
    const out = new Map([[P.SECRET, V.str(session.token)], [P.USER_ID, V.str(session.userId)]]);
    peer.respond(op.code, ERR.OK, out, null, true);
  }

  // ---------------------------------------------------------------- master server
  master() {
    const hub = this;
    return {
      onClose(peer) { hub.masterPeers.delete(peer); },
      onOperation(peer, op) {
        const p = op.params;
        if (op.code === OP.AUTH || op.code === OP.AUTH_ONCE) { hub.masterPeers.add(peer); return hub.authenticate(peer, op, 'master'); }
        if (!peer.authed) return peer.respond(op.code, ERR.NOT_ALLOWED, new Map(), 'Non autenticato');
        switch (op.code) {
          case OP.JOIN_LOBBY: peer.inLobby = true; peer.respond(op.code); hub.sendGameList(peer); hub.sendAppStats(peer); return;
          case OP.LEAVE_LOBBY: peer.inLobby = false; return peer.respond(op.code);
          case OP.GET_GAME_LIST: peer.respond(op.code); return hub.sendGameList(peer);
          case OP.SETTINGS: case OP.LOBBY_STATS: return peer.respond(op.code);
          case OP.FIND_FRIENDS: return peer.respond(op.code, ERR.OK, new Map([[1, { t: T.ARRAY, et: T.BOOL, v: [] }], [2, V.strArr([])]]));
          case OP.CREATE: {
            let name = val(p, P.ROOM_NAME);
            if (!name) name = crypto.randomUUID();
            if (hub.rooms.has(name)) return peer.respond(op.code, ERR.ALREADY_EXISTS, new Map(), 'Esiste già una partita con questo nome');
            if (hub.cfg.maxRooms > 0 && hub.rooms.size >= hub.cfg.maxRooms) return peer.respond(op.code, ERR.NOT_ALLOWED, new Map(), 'Il server ha raggiunto il numero massimo di partite');
            return hub.redirect(peer, op.code, name);
          }
          case OP.JOIN: {
            const name = val(p, P.ROOM_NAME); const mode = val(p, P.JOIN_MODE, 0); const room = hub.rooms.get(name);
            if (!room) { if (mode === 1) return hub.redirect(peer, op.code, name); return peer.respond(op.code, ERR.NOT_FOUND, new Map(), 'La partita non esiste'); }
            const e = hub.joinable(room); if (e) return peer.respond(op.code, e[0], new Map(), e[1]);
            return hub.redirect(peer, op.code, name);
          }
          case OP.JOIN_RANDOM: {
            const want = p.get(P.GAME_PROPS);
            for (const room of hub.rooms.values()) {
              if (!room.isVisible || hub.joinable(room)) continue;
              if (want && want.t === T.HASH && !want.v.every(([k, v]) => gp.equal(gp.hashGet(room.props, k.v), v))) continue;
              return hub.redirect(peer, op.code, room.name);
            }
            return peer.respond(op.code, ERR.NO_MATCH, new Map(), 'Nessuna partita disponibile');
          }
          default: hub.log(`[master] operazione non gestita ${op.code}`); return peer.respond(op.code, ERR.INVALID_OP, new Map(), 'Operazione sconosciuta');
        }
      },
    };
  }
  joinable(room) {
    if (!room.isOpen) return [ERR.CLOSED, 'Partita chiusa'];
    if (room.maxPlayers > 0 && room.count + room.reserved >= room.maxPlayers) return [ERR.FULL, 'Partita piena'];
    return null;
  }
  redirect(peer, code, name) {
    peer.respond(code, ERR.OK, new Map([[P.ROOM_NAME, V.str(name)], [P.ADDRESS, V.str(this.gameAddress(peer))], [P.SECRET, V.str(peer.session.token)]]));
  }
  gameListHash(all = true, names = null) {
    const h = V.hash();
    for (const room of this.rooms.values()) {
      if (names && !names.includes(room.name)) continue;
      if (room.isVisible) h.v.push([V.str(room.name), room.lobbyProps()]);
    }
    return h;
  }
  sendGameList(peer) { peer.sendEvent(EV.GAME_LIST, new Map([[P.GAME_LIST, this.gameListHash()]])); }
  sendAppStats(peer) {
    peer.sendEvent(EV.APP_STATS, new Map([[P.PEER_COUNT, V.int(this.gamePeers.size)], [P.GAME_COUNT, V.int(this.rooms.size)], [P.MASTER_PEER_COUNT, V.int(this.masterPeers.size)]]));
  }
  lobbyUpdate(room, removed = false) {
    const entry = removed || !room.isVisible ? V.hash([[V.byte(GP.REMOVED), V.bool(true)]]) : room.lobbyProps();
    const bytes = Peer.encodeEvent(EV.GAME_LIST_UPDATE, new Map([[P.GAME_LIST, V.hash([[V.str(room.name), entry]])]]));
    for (const peer of this.masterPeers) if (peer.inLobby) peer.sendEventBytes(bytes);
  }

  // ---------------------------------------------------------------- game server
  game() {
    const hub = this;
    return {
      onClose(peer) { hub.gamePeers.delete(peer); if (peer.room) hub.leave(peer); },
      onOperation(peer, op, meta) {
        if (op.code === OP.AUTH || op.code === OP.AUTH_ONCE) { hub.gamePeers.add(peer); return hub.authenticate(peer, op, 'game'); }
        if (!peer.authed) return peer.respond(op.code, ERR.NOT_ALLOWED, new Map(), 'Non autenticato');
        switch (op.code) {
          case OP.CREATE: case OP.JOIN: return hub.enter(peer, op);
          case OP.RAISE_EVENT: return hub.raise(peer, op, meta);
          case OP.SET_PROPS: return hub.setProps(peer, op);
          case OP.LEAVE: if (peer.room) hub.leave(peer); return peer.respond(op.code);
          case OP.CHANGE_GROUPS: return hub.changeGroups(peer, op);
          case OP.GET_PROPS: return peer.respond(op.code, ERR.OK, new Map(peer.room ? [[P.GAME_PROPS, peer.room.props]] : []));
          default: hub.log(`[game] operazione non gestita ${op.code}`); return peer.respond(op.code, ERR.INVALID_OP, new Map(), 'Operazione sconosciuta');
        }
      },
    };
  }

  enter(peer, op) {
    const p = op.params; const name = val(p, P.ROOM_NAME); const mode = val(p, P.JOIN_MODE, 0);
    if (peer.room) return peer.respond(op.code, ERR.NOT_ALLOWED, new Map(), 'Già in una partita');
    let room = this.rooms.get(name); let created = false;
    if (op.code === OP.CREATE && room) return peer.respond(op.code, ERR.ALREADY_EXISTS, new Map(), 'Esiste già una partita con questo nome');
    if (!room) {
      if (op.code === OP.JOIN && mode !== 1) return peer.respond(op.code, ERR.NOT_FOUND, new Map(), 'La partita non esiste');
      room = new Room(name || crypto.randomUUID(), this); created = true;
      const gpIn = p.get(P.GAME_PROPS); if (gpIn && gpIn.t === T.HASH) room.mergeProps(room.props, gpIn);
      const flags = val(p, P.ROOM_FLAGS);
      room.cleanup = flags !== undefined ? (flags & 2) !== 0 : val(p, P.CLEANUP, true);
      room.broadcastToAll = flags !== undefined ? (flags & 32) !== 0 : true;
      this.rooms.set(room.name, room);
    } else { const e = this.joinable(room); if (e) return peer.respond(op.code, e[0], new Map(), e[1]); }

    const nr = room.nextActor++;
    const actor = { nr, peer, props: V.hash(), groups: new Set(), userId: peer.session.userId };
    const pp = p.get(P.PLAYER_PROPS); if (pp && pp.t === T.HASH) room.mergeProps(actor.props, pp);
    peer.room = room; peer.actor = actor; room.actors.set(nr, actor);
    if (!room.masterId) room.masterId = nr;
    room.peak = Math.max(room.peak, room.count); room.names.set(actor.userId, room.nameOf(actor));
    this.log(`[game] ${created ? 'creata' : 'ingresso in'} "${room.name}" — attore ${nr} (${room.count} presenti)`);

    const others = V.hash();
    for (const a of room.actors.values()) if (a !== actor) others.v.push([V.int(a.nr), a.props]);
    const gameProps = V.hash([...room.props.v]);
    gp.hashSet(gameProps, V.byte(GP.MASTER_CLIENT_ID), V.int(room.masterId));
    peer.respond(op.code, ERR.OK, new Map([[P.ROOM_NAME, V.str(room.name)], [P.ACTOR_NR, V.int(nr)], [P.GAME_PROPS, gameProps],
      [P.PLAYER_PROPS, others], [P.ACTOR_LIST, room.actorList()], [P.MASTER_CLIENT_ID, V.int(room.masterId)]]));
    room.broadcast(Peer.encodeEvent(EV.JOIN, new Map([[P.ACTOR_NR, V.int(nr)], [P.PLAYER_PROPS, actor.props], [P.ACTOR_LIST, room.actorList()]])));
    for (const ev of room.cache) peer.sendEventBytes(ev.bytes);
    for (const [, c] of room.actorCache) for (const ev of c.values()) peer.sendEventBytes(ev.bytes);
    this.lobbyUpdate(room);
    if (this.cfg.motd) peer.sendEvent(STATION_EVENT, new Map([[P.ACTOR_NR, V.int(0)], [P.DATA, V.str('msg|' + String(this.cfg.motd).slice(0, 200))]]));
  }

  leave(peer) {
    const room = peer.room; const actor = peer.actor; if (!room || !actor) return;
    peer.room = null; peer.actor = null; room.actors.delete(actor.nr);
    if (room.cleanup) { room.cache = room.cache.filter((e) => e.actor !== actor.nr || e.global); room.actorCache.delete(actor.nr); }
    if (room.count === 0) {
      this.rooms.delete(room.name); this.lobbyUpdate(room, true); this.log(`[game] chiusa "${room.name}"`);
      if (this.onRoomClosed) try { this.onRoomClosed(room); } catch (e) { this.log('[game] storico non aggiornato:', e.message); }
      return;
    }
    if (room.masterId === actor.nr) room.masterId = Math.min(...room.actors.keys());
    room.broadcast(Peer.encodeEvent(EV.LEAVE, new Map([[P.ACTOR_NR, V.int(actor.nr)], [P.ACTOR_LIST, room.actorList()], [P.MASTER_CLIENT_ID, V.int(room.masterId)]])));
    this.log(`[game] attore ${actor.nr} ha lasciato "${room.name}" (master ora ${room.masterId})`);
    this.lobbyUpdate(room);
  }

  raise(peer, op, meta) {
    const room = peer.room; if (!room) return peer.respond(op.code, ERR.NOT_ALLOWED, new Map(), 'Non in partita');
    const p = op.params; const actor = peer.actor;
    const code = val(p, P.CODE, 0); const data = p.get(P.DATA);
    const caching = val(p, P.CACHE, CACHE.NONE); const targets = val(p, P.ACTOR_LIST); const group = val(p, P.GROUP, 0);
    const receivers = val(p, P.RECEIVER_GROUP, 0);

    if (caching === CACHE.REMOVE_ROOM) {
      room.cache = room.cache.filter((e) => {
        if (code !== 0 && e.code !== code) return true;
        if (targets && targets.length && !targets.includes(e.actor)) return true;
        if (data && data.t === T.HASH && data.v.length) {
          if (!e.data || e.data.t !== T.HASH) return true;
          if (!data.v.every(([k, v]) => gp.equal(gp.hashGet(e.data, k.v), v))) return true;
        }
        return false;
      });
      return;
    }
    if (caching === CACHE.REMOVE_ROOM_LEFT) { room.cache = room.cache.filter((e) => room.actors.has(e.actor)); return; }

    const params = new Map([[P.ACTOR_NR, V.int(actor.nr)]]); if (data) params.set(P.DATA, data);
    const bytes = Peer.encodeEvent(code, params);
    this.stats.eventsRelayed++; this.stats.bytesRelayed += bytes.length; room.eventCount++; room.rateCount++;
    if (code === STATION_EVENT && data && data.t === T.STRING) { try { room.telemetry(data.v); } catch { /* telemetria facoltativa */ } }

    if (caching === CACHE.ADD_ROOM || caching === CACHE.ADD_ROOM_GLOBAL) {
      room.cache.push({ code, data, actor: actor.nr, bytes, global: caching === CACHE.ADD_ROOM_GLOBAL });
    } else if (caching === CACHE.MERGE || caching === CACHE.REPLACE || caching === CACHE.REMOVE) {
      let c = room.actorCache.get(actor.nr); if (!c) room.actorCache.set(actor.nr, c = new Map());
      if (caching === CACHE.REMOVE) c.delete(code); else c.set(code, { bytes });
    }

    const opt = { channel: meta.channel, reliable: meta.reliable };
    if (targets && targets.length) return room.broadcast(bytes, { ...opt, only: targets });
    if (receivers === 2) { const m = room.actors.get(room.masterId); if (m) m.peer.sendEventBytes(bytes, opt.channel, opt.reliable); return; }
    for (const a of room.actors.values()) {
      if (a === actor && receivers !== 1) continue;
      if (group !== 0 && a !== actor && !a.groups.has(group) && !a.allGroups) continue;
      a.peer.sendEventBytes(bytes, opt.channel, opt.reliable);
    }
  }

  setProps(peer, op) {
    const room = peer.room; if (!room) return peer.respond(op.code, ERR.NOT_ALLOWED, new Map(), 'Non in partita');
    const p = op.params; const props = p.get(P.PROPERTIES); const targetNr = val(p, P.ACTOR_NR, 0);
    if (!props || props.t !== T.HASH) return peer.respond(op.code, ERR.INVALID_OP, new Map(), 'Proprietà mancanti');
    const target = targetNr ? (room.actors.get(targetNr) || {}).props : room.props;
    if (!target) return peer.respond(op.code, ERR.INVALID_OP, new Map(), 'Attore inesistente');
    const expected = p.get(P.EXPECTED);
    if (expected && expected.t === T.HASH) {
      for (const [k, v] of expected.v) {
        const cur = (!targetNr && k.t === T.BYTE && k.v === GP.MASTER_CLIENT_ID) ? V.int(room.masterId) : gp.hashGet(target, k.v);
        if (!gp.equal(cur || V.nul(), v)) return peer.respond(op.code, ERR.INVALID_OP, new Map(), 'Valore atteso non corrispondente');
      }
    }
    const oldMaster = room.masterId;
    room.mergeProps(target, props);
    if (targetNr && room.actors.has(targetNr)) { const ta = room.actors.get(targetNr); room.names.set(ta.userId, room.nameOf(ta)); }
    peer.respond(op.code);
    if (val(p, P.BROADCAST, true) !== false) {
      const params = new Map([[P.ACTOR_NR, V.int(peer.actor.nr)], [P.TARGET_ACTOR, V.int(targetNr)], [P.PROPERTIES, props]]);
      room.broadcast(Peer.encodeEvent(EV.PROPS_CHANGED, params), { except: expected || room.broadcastToAll ? null : peer.actor });
    }
    if (!targetNr) { this.lobbyUpdate(room); if (oldMaster !== room.masterId) this.log(`[game] "${room.name}": master client ora ${room.masterId}`); }
  }

  changeGroups(peer, op) {
    const a = peer.actor; if (!a) return;
    const rem = op.params.get(P.REMOVE), add = op.params.get(P.ADD);
    if (rem) { if (!rem.v || rem.v.length === 0) { a.groups.clear(); a.allGroups = false; } else for (const g of rem.v) a.groups.delete(g); }
    if (add) { if (!add.v || add.v.length === 0) a.allGroups = true; else for (const g of add.v) a.groups.add(g); }
  }

  // ---------------------------------------------------------------- amministrazione
  kick(roomName, actorNr) { const r = this.rooms.get(roomName); const a = r && r.actors.get(actorNr); if (!a) return false; a.peer.close(); return true; }
  // Esclude un giocatore (per identificativo o per indirizzo) e chiude le sue connessioni aperte.
  ban({ userId, address, reason = '', minutes = 0 }) {
    const entry = { reason: String(reason).slice(0, 120), until: minutes > 0 ? Date.now() + minutes * 60000 : 0, at: Date.now() };
    if (userId) this.bans.users.set(userId, entry); if (address) this.bans.addresses.set(address, entry);
    let closed = 0;
    for (const peer of [...this.gamePeers, ...this.masterPeers]) {
      if (peer.session && this.banned(peer.session.userId, Hub.addressOf(peer))) { peer.close(); closed++; }
    }
    this.log(`[admin] escluso ${userId || address} (${closed} connessioni chiuse)`); return true;
  }
  // Espelle tutte le connessioni di un giocatore (lobby e partite).
  kickUser(userId) {
    let n = 0; for (const peer of [...this.gamePeers, ...this.masterPeers]) if (peer.session && peer.session.userId === userId) { peer.close(); n++; }
    return n;
  }
  kickAll() { let n = 0; for (const peer of [...this.gamePeers, ...this.masterPeers]) { peer.close(); n++; } return n; }
  // Messaggio privato a un solo giocatore di una partita.
  whisper(roomName, actorNr, text) {
    const room = this.rooms.get(roomName); const a = room && room.actors.get(actorNr); if (!a) return false;
    text = String(text || '').replace(/[|\r\n]/g, ' ').trim().slice(0, 200); if (!text) return false;
    a.peer.sendEvent(STATION_EVENT, new Map([[P.ACTOR_NR, V.int(0)], [P.DATA, V.str('msg|' + text)]]));
    room.journal.push({ t: Date.now(), ship: -1, text: `Server a ${room.nameOf(a)}: ${text}` }); return true;
  }
  unban({ userId, address }) { const a = userId ? this.bans.users.delete(userId) : false; const b = address ? this.bans.addresses.delete(address) : false; return a || b; }
  // Messaggio del server ai giocatori di una partita (o di tutte): compare a schermo e nel giornale di bordo.
  announce(roomName, text) {
    text = String(text || '').replace(/[|\r\n]/g, ' ').trim().slice(0, 200); if (!text) return 0;
    const bytes = Peer.encodeEvent(STATION_EVENT, new Map([[P.ACTOR_NR, V.int(0)], [P.DATA, V.str('msg|' + text)]]));
    let n = 0;
    for (const room of this.rooms.values()) {
      if (roomName && room.name !== roomName) continue;
      room.broadcast(bytes); n += room.count; room.journal.push({ t: Date.now(), ship: -1, text: 'Server: ' + text });
    }
    this.log(`[admin] annuncio a ${n} giocatori: ${text}`); return n;
  }
  // Apre, chiude, nasconde una partita o ne cambia la capienza, avvisando i giocatori e la lobby.
  setRoom(roomName, { open, visible, max }) {
    const room = this.rooms.get(roomName); if (!room) return false;
    const props = V.hash();
    if (open !== undefined) props.v.push([V.byte(GP.IS_OPEN), V.bool(!!open)]);
    if (visible !== undefined) props.v.push([V.byte(GP.IS_VISIBLE), V.bool(!!visible)]);
    if (max !== undefined && Number.isInteger(max) && max >= 0 && max <= 255) props.v.push([V.byte(GP.MAX_PLAYERS), V.byte(max)]);
    if (!props.v.length) return false;
    room.mergeProps(room.props, props);
    room.broadcast(Peer.encodeEvent(EV.PROPS_CHANGED, new Map([[P.ACTOR_NR, V.int(0)], [P.TARGET_ACTOR, V.int(0)], [P.PROPERTIES, props]])));
    this.lobbyUpdate(room); this.log(`[admin] "${room.name}": ${room.isOpen ? 'aperta' : 'chiusa'}, ${room.isVisible ? 'visibile' : 'nascosta'}, capienza ${room.maxPlayers || 'libera'}`);
    return true;
  }
  closeRoom(roomName) { const room = this.rooms.get(roomName); if (!room) return false; this.log(`[admin] partita "${room.name}" terminata`); for (const a of [...room.actors.values()]) a.peer.close(); return true; }
  snapshot() {
    const plain = (h) => Object.fromEntries(h.v.map(([k, v]) => [String(k.v), Buffer.isBuffer(v.v) ? `<${v.v.length} byte>` : typeof v.v === 'bigint' ? v.v.toString() : (v.v !== null && typeof v.v === 'object' ? '<composto>' : v.v)]));
    return {
      uptimeSec: Math.round((Date.now() - this.started) / 1000), masterPeers: this.masterPeers.size, gamePeers: this.gamePeers.size, stats: this.stats,
      rooms: [...this.rooms.values()].map((r) => ({ name: r.name, masterClient: r.masterId, maxPlayers: r.maxPlayers, open: r.isOpen, visible: r.isVisible,
        ageSec: Math.round((Date.now() - r.created) / 1000), cachedEvents: r.cache.length, eventsRelayed: r.eventCount, eventsPerSec: Math.round(r.rate * 10) / 10,
        peak: r.peak, props: plain(r.props),
        ships: [...r.ships.entries()].map(([id, sh]) => ({ id, ageSec: Math.round((Date.now() - sh.at) / 1000), values: sh.values })),
        journal: r.journal.slice(-40),
        actors: [...r.actors.values()].map((a) => ({ nr: a.nr, name: r.nameOf(a), userId: a.userId, address: a.peer.id, ip: Hub.addressOf(a.peer), connectedSec: Math.round((Date.now() - a.peer.start) / 1000), version: a.peer.session ? a.peer.session.appVersion : '',
          bytesIn: a.peer.bytesIn, bytesOut: a.peer.bytesOut, msgsIn: a.peer.msgsIn, msgsOut: a.peer.msgsOut, idleSec: Math.round((Date.now() - a.peer.lastActivity) / 1000), isMaster: a.nr === r.masterId, props: plain(a.props) })) })),
      bans: { users: [...this.bans.users].map(([id, b]) => ({ id, ...b })), addresses: [...this.bans.addresses].map(([id, b]) => ({ id, ...b })) },
      lobby: [...this.masterPeers].map((p) => ({ address: Hub.addressOf(p), userId: p.session ? p.session.userId : '', version: p.session ? p.session.appVersion : '', connectedSec: Math.round((Date.now() - p.start) / 1000), inLobby: !!p.inLobby })),
      maintenance: !!this.cfg.maintenance,
    };
  }
}

module.exports = { Hub, OP, EV, P, GP, ERR, CACHE };

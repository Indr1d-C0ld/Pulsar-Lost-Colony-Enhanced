'use strict';
// Accesso al pannello di amministrazione: utente e password (scrypt), sessioni in cookie HttpOnly,
// blocco progressivo dopo tentativi falliti, registro delle azioni dell'amministratore.
const crypto = require('crypto');

const COOKIE = 'mel_admin';
const IDLE_MS = 2 * 3600 * 1000, MAX_MS = 12 * 3600 * 1000;

function hashPassword(pass) {
  const salt = crypto.randomBytes(16);
  const hash = crypto.scryptSync(String(pass), salt, 64, { N: 16384, r: 8, p: 1 });
  return `scrypt$${salt.toString('hex')}$${hash.toString('hex')}`;
}
function verifyPassword(pass, stored) {
  const [kind, saltHex, hashHex] = String(stored || '').split('$');
  if (kind !== 'scrypt' || !saltHex || !hashHex) return false;
  const want = Buffer.from(hashHex, 'hex');
  const got = crypto.scryptSync(String(pass), Buffer.from(saltHex, 'hex'), want.length, { N: 16384, r: 8, p: 1 });
  return crypto.timingSafeEqual(got, want);
}
const same = (a, b) => { const x = Buffer.from(String(a)), y = Buffer.from(String(b)); return x.length === y.length && crypto.timingSafeEqual(x, y); };

class Auth {
  constructor(cfg, log, audit) {
    this.cfg = cfg; this.log = log; this.audit = audit;
    this.sessions = new Map(); this.failures = new Map(); // ip -> { count, until }
  }
  get configured() { return !!(this.cfg.adminUser && this.cfg.adminPassHash); }

  static cookies(req) {
    const out = {}; for (const part of String(req.headers.cookie || '').split(';')) { const i = part.indexOf('='); if (i > 0) out[part.slice(0, i).trim()] = decodeURIComponent(part.slice(i + 1).trim()); }
    return out;
  }
  static ip(req) { return String(req.socket.remoteAddress || '').replace(/^::ffff:/, ''); }

  // Sessione valida per questa richiesta (cookie o token per gli script), altrimenti null.
  check(req) {
    const tok = req.headers['x-admin-token'];
    if (tok && this.cfg.adminToken && same(tok, this.cfg.adminToken)) return { user: 'token', ip: Auth.ip(req) };
    if (!this.configured && !this.cfg.adminToken) return { user: 'locale', ip: Auth.ip(req) }; // nessuna protezione configurata (solo prove)
    const id = Auth.cookies(req)[COOKIE]; const s = id && this.sessions.get(id);
    if (!s) return null;
    const now = Date.now();
    if (now - s.last > IDLE_MS || now - s.created > MAX_MS) { this.sessions.delete(id); return null; }
    s.last = now; return s;
  }

  locked(ip) { const f = this.failures.get(ip); return f && f.until > Date.now() ? Math.ceil((f.until - Date.now()) / 1000) : 0; }

  login(req, user, pass) {
    const ip = Auth.ip(req); const wait = this.locked(ip);
    if (wait) return { ok: false, error: `Troppi tentativi: riprova tra ${wait} s`, status: 429 };
    const good = this.configured && same(String(user || ''), this.cfg.adminUser) && verifyPassword(pass, this.cfg.adminPassHash);
    if (!good) {
      const f = this.failures.get(ip) || { count: 0, until: 0 }; f.count++;
      if (f.count >= 5) f.until = Date.now() + Math.min(3600, 30 * 2 ** (f.count - 5)) * 1000;
      this.failures.set(ip, f);
      this.log(`[admin] accesso negato da ${ip} (utente "${String(user || '').slice(0, 32)}", tentativo ${f.count})`);
      this.audit(ip, 'accesso negato', `utente "${String(user || '').slice(0, 32)}"`);
      return { ok: false, error: 'Credenziali non valide', status: 401 };
    }
    this.failures.delete(ip);
    const id = crypto.randomBytes(32).toString('hex');
    this.sessions.set(id, { id: id.slice(0, 8), user: this.cfg.adminUser, ip, created: Date.now(), last: Date.now(), agent: String(req.headers['user-agent'] || '').slice(0, 120) });
    this.log(`[admin] accesso di ${this.cfg.adminUser} da ${ip}`); this.audit(ip, 'accesso', '');
    return { ok: true, cookie: `${COOKIE}=${id}; HttpOnly; SameSite=Strict; Path=/; Max-Age=${MAX_MS / 1000}` };
  }

  logout(req) { const id = Auth.cookies(req)[COOKIE]; if (id) this.sessions.delete(id); return `${COOKIE}=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0`; }
  list() { return [...this.sessions.values()].map(({ id, user, ip, created, last, agent }) => ({ id, user, ip, created, last, agent })); }
  revoke(shortId) { for (const [k, s] of this.sessions) if (s.id === shortId) { this.sessions.delete(k); return true; } return false; }
  revokeAll() { const n = this.sessions.size; this.sessions.clear(); return n; }
  lockouts() { const now = Date.now(); return [...this.failures].map(([ip, f]) => ({ ip, count: f.count, lockedSec: f.until > now ? Math.ceil((f.until - now) / 1000) : 0 })); }
}

module.exports = { Auth, hashPassword, verifyPassword };

'use strict';
// Fazioni rivali persistenti: ogni galassia (seme) ha una forza ostile con un nome e un capo che ricorda gli scontri.
// Cresce col tempo reale anche quando nessuno gioca, si indebolisce quando l'equipaggio la sconfigge,
// e tiene una cronaca che il gioco mostra al rientro ("mentre eravate via...").
const crypto = require('crypto');

const PREFIX = ['Corsari', 'Legione', 'Sindacato', 'Ordine', 'Flotta', 'Clan', 'Collettivo', 'Compagnia', 'Lega', 'Fratellanza'];
const SUFFIX = ['del Vuoto', 'di Ceneri', 'della Nebulosa Nera', 'del Sole Spento', 'di Ferro', 'dell\'Ultima Luce', 'dei Cento Soli', 'di Vega Oscura', 'del Margine', 'della Lunga Notte'];
const TITLES = ['Ammiraglio', 'Comandante', 'Arconte', 'Pretore', 'Capitano', 'Sovrano', 'Margravio'];
const NAMES = ['Kor', 'Vessa', 'Draxen', 'Ilyra', 'Tal Sorn', 'Mercer', 'Azhar', 'Rhovan', 'Selk', 'Ondine', 'Varek', 'Thessaly', 'Brannoc', 'Ysolde', 'Kaldor'];
const TEMPER = ['vendicativo', 'calcolatore', 'spietato', 'd\'onore', 'imprevedibile', 'avido'];

function seeded(seed) { let x = (Number(seed) >>> 0) || 1; return () => { x ^= x << 13; x >>>= 0; x ^= x >> 17; x ^= x << 5; x >>>= 0; return x / 4294967296; }; }
const pick = (r, a) => a[Math.floor(r() * a.length)];

class Rivals {
  constructor(data, save, log) { this.data = data || {}; this.save = save; this.log = log; }

  create(seed) {
    const r = seeded(seed);
    return { seed, name: `${pick(r, PREFIX)} ${pick(r, SUFFIX)}`, leader: `${pick(r, TITLES)} ${pick(r, NAMES)}`, temper: pick(r, TEMPER),
      strength: 30, grudge: 0, encounters: 0, defeats: 0, victories: 0, leaderDefeats: 0, created: Date.now(), lastTick: Date.now(), lastSeen: 0, news: [], history: [] };
  }

  // Il tempo passa anche senza giocatori: la forza cresce di 4 punti al giorno, fino a 100.
  advance(f) {
    const days = (Date.now() - f.lastTick) / 86400000; if (days < 0.01) return;
    const before = f.strength; f.strength = Math.min(100, f.strength + days * 4); f.lastTick = Date.now();
    if (Math.floor(before / 10) < Math.floor(f.strength / 10)) this.news(f, `${f.name} ha reclutato nuove navi: la loro forza è ora ${Math.round(f.strength)}.`);
  }
  news(f, text) { f.news.push({ t: Date.now(), text }); if (f.news.length > 30) f.news.shift(); }

  get(seed) {
    const key = String(seed); let f = this.data[key];
    if (!f) { f = this.data[key] = this.create(seed); this.news(f, `Rapporti di intelligence: una nuova potenza ostile, ${f.name}, guidata da ${f.leader}.`); this.log(`[rivali] nuova fazione per la galassia ${key}: ${f.name}`); }
    this.advance(f); this.save(); return f;
  }

  // Esito di uno scontro riferito dall'host: "vittoria" (navi rivali distrutte) o "ritirata" (l'equipaggio è fuggito).
  report(seed, { outcome, killed = 0, leader = false }) {
    const f = this.get(seed); f.encounters++; f.lastSeen = Date.now();
    if (outcome === 'vittoria') {
      f.defeats++; f.strength = Math.max(5, f.strength - 6 - killed * 2); f.grudge = Math.min(100, f.grudge + 12);
      if (leader) {
        f.leaderDefeats++; const old = f.leader; const r = seeded(f.seed + f.leaderDefeats * 7919);
        f.leader = `${pick(r, TITLES)} ${pick(r, NAMES)}`;
        this.news(f, `${old} è caduto. ${f.leader} ha preso il comando di ${f.name} e giura vendetta.`);
      } else this.news(f, `Scontro vinto contro ${f.name}: ${killed} navi distrutte.`);
    } else if (outcome === 'ritirata') {
      f.victories++; f.strength = Math.min(100, f.strength + 4); f.grudge = Math.max(0, f.grudge - 5);
      this.news(f, `${f.name} rivendica la ritirata dell'equipaggio. Il loro prestigio cresce.`);
    } else return null;
    f.history.push({ t: Date.now(), outcome, killed, leader: !!leader }); if (f.history.length > 100) f.history.shift();
    this.log(`[rivali] ${f.name}: ${outcome} (forza ${Math.round(f.strength)}, rancore ${Math.round(f.grudge)})`);
    this.save(); return f;
  }

  // Notizie non ancora lette dall'equipaggio: le consegna una volta sola.
  unread(seed, since) { const f = this.get(seed); return f.news.filter((n) => n.t > since); }
  all() { for (const f of Object.values(this.data)) this.advance(f); return Object.values(this.data); }
}

module.exports = { Rivals };

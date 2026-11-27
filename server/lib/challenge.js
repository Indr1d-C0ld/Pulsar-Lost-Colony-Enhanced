'use strict';
// Sfida della settimana: la stessa per tutti gli equipaggi che giocano su questo server. Il tipo e il traguardo
// dipendono solo dal numero della settimana; i progressi arrivano dal gioco (host di ogni partita) e formano la
// classifica della settimana.
const TYPES = [
  { key: 'nemesi.abbattute', label: 'Abbattere o catturare {n} personaggi ricorrenti', min: 2, max: 3 },
  { key: 'esplorazione.scoperte', label: 'Esplorare {n} luoghi e registrarli nell\'Atlante', min: 4, max: 6 },
  { key: 'contratti.riusciti', label: 'Portare a termine {n} contratti', min: 4, max: 6 },
  { key: 'crisi.risolte', label: 'Risolvere una crisi galattica', min: 1, max: 1 },
  { key: 'incontri.risolti', label: 'Risolvere {n} incontri', min: 10, max: 15 },
  { key: 'commercio.consegne', label: 'Completare {n} consegne', min: 3, max: 4 },
  { key: 'direttore.crisi.superate', label: 'Sopravvivere a {n} combattimenti', min: 8, max: 12 },
  { key: 'comando.rese', label: 'Ottenere la resa di {n} navi nemiche', min: 2, max: 3 },
];

function weekOf(t = Date.now()) { return Math.floor((t / 86400000 + 3) / 7); } // settimane dal lunedì 5 gennaio 1970
function current(t = Date.now()) {
  const week = weekOf(t); let h = (week * 2654435761) >>> 0;
  const type = TYPES[h % TYPES.length]; h = (h * 1103515245 + 12345) >>> 0;
  const target = type.min + (h % (type.max - type.min + 1));
  const ends = (week + 1) * 7 * 86400000 - 3 * 86400000;
  return { week, key: type.key, label: type.label.replace('{n}', target), target, ends };
}

class Challenge {
  constructor(data, markDirty) { this.data = data || {}; this.markDirty = markDirty; }
  info() {
    const c = current(); const w = this.data[c.week] || { entries: {} };
    const board = Object.values(w.entries).sort((a, b) => (b.done - a.done) || (b.progress - a.progress) || (a.t - b.t)).slice(0, 10);
    return { ...c, board };
  }
  report(seed, o) {
    const c = current(); if (Number(o.week) !== c.week || String(o.key) !== c.key) return false;
    const w = this.data[c.week] || (this.data[c.week] = { entries: {} });
    const prev = w.entries[seed]; const progress = Math.max(0, Math.min(c.target, Math.floor(Number(o.progress) || 0)));
    const ship = String(o.ship || 'equipaggio').slice(0, 40);
    if (!prev || progress > prev.progress || ship !== prev.ship) {
      w.entries[seed] = { seed, ship, progress, done: progress >= c.target, t: prev && prev.done ? prev.t : Date.now() };
      // si tengono solo le ultime otto settimane
      for (const k of Object.keys(this.data)) if (Number(k) < c.week - 8) delete this.data[k];
      this.markDirty();
    }
    return true;
  }
}
module.exports = { Challenge, current, weekOf, TYPES };

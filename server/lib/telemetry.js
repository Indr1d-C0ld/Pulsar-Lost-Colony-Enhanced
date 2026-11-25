'use strict';
// Telemetria del server: un campione ogni 5 secondi (ultima ora) e uno al minuto (ultime 24 ore).
// Rete (byte e messaggi), eventi inoltrati, connessioni, partite, giocatori, memoria, CPU, ritardo del ciclo di eventi, carico del sistema.
const os = require('os');
const fs = require('fs');
const { monitorEventLoopDelay } = require('perf_hooks');
const { Peer } = require('./peer');

class Telemetry {
  constructor(hub, dataDir) {
    this.hub = hub; this.dataDir = dataDir; this.fine = []; this.coarse = []; this.started = Date.now();
    this.lag = monitorEventLoopDelay({ resolution: 20 }); this.lag.enable();
    this.prev = { t: Date.now(), cpu: process.cpuUsage(), ...Peer.totals, events: hub.stats.eventsRelayed };
    this.peaks = { players: 0, rooms: 0, eventsPerSec: 0, bytesPerSec: 0 };
    setInterval(() => this.sample(), 5000).unref();
  }

  sample() {
    const now = Date.now(), dt = Math.max(0.001, (now - this.prev.t) / 1000), cpu = process.cpuUsage(this.prev.cpu), mem = process.memoryUsage();
    const t = Peer.totals, h = this.hub;
    const players = [...h.rooms.values()].reduce((n, r) => n + r.count, 0);
    const s = {
      t: now,
      inKBs: Math.round((t.bytesIn - this.prev.bytesIn) / dt / 102.4) / 10, outKBs: Math.round((t.bytesOut - this.prev.bytesOut) / dt / 102.4) / 10,
      msgsIn: Math.round((t.msgsIn - this.prev.msgsIn) / dt), msgsOut: Math.round((t.msgsOut - this.prev.msgsOut) / dt),
      events: Math.round((h.stats.eventsRelayed - this.prev.events) / dt * 10) / 10,
      rooms: h.rooms.size, players, lobby: h.masterPeers.size, conns: h.masterPeers.size + h.gamePeers.size,
      cpu: Math.round((cpu.user + cpu.system) / 1000 / (dt * 1000) * 1000) / 10, rssMB: Math.round(mem.rss / 1048576), heapMB: Math.round(mem.heapUsed / 1048576),
      lagMs: Math.round(this.lag.mean / 1e5) / 10, lagMaxMs: Math.round(this.lag.max / 1e5) / 10, load: Math.round(os.loadavg()[0] * 100) / 100,
    };
    this.lag.reset();
    this.prev = { t: now, cpu: process.cpuUsage(), ...t, events: h.stats.eventsRelayed };
    this.fine.push(s); if (this.fine.length > 720) this.fine.shift();
    const p = this.peaks; p.players = Math.max(p.players, players); p.rooms = Math.max(p.rooms, s.rooms); p.eventsPerSec = Math.max(p.eventsPerSec, s.events); p.bytesPerSec = Math.max(p.bytesPerSec, s.inKBs + s.outKBs);
    // ogni minuto un campione medio per la vista sulle 24 ore
    const last = this.coarse[this.coarse.length - 1];
    if (!last || now - last.t >= 60000) {
      const win = this.fine.filter((x) => now - x.t < 60000); const avg = (k) => Math.round(win.reduce((a, x) => a + x[k], 0) / win.length * 10) / 10;
      this.coarse.push({ t: now, inKBs: avg('inKBs'), outKBs: avg('outKBs'), events: avg('events'), players: Math.max(...win.map((x) => x.players)), cpu: avg('cpu'), rssMB: avg('rssMB') });
      if (this.coarse.length > 1440) this.coarse.shift();
    }
  }

  system() {
    let disk = null;
    try { const st = fs.statfsSync(this.dataDir); disk = { totalGB: Math.round(st.blocks * st.bsize / 1e8) / 10, freeGB: Math.round(st.bavail * st.bsize / 1e8) / 10 }; } catch { disk = null; }
    let dataKB = 0; try { for (const f of fs.readdirSync(this.dataDir)) { try { dataKB += fs.statSync(require('path').join(this.dataDir, f)).size; } catch { /* file sparito */ } } } catch { /* cartella assente */ }
    const mem = process.memoryUsage();
    return {
      host: os.hostname(), platform: `${os.type()} ${os.release()} (${os.arch()})`, cpus: os.cpus().length, cpuModel: (os.cpus()[0] || {}).model || '',
      memTotalMB: Math.round(os.totalmem() / 1048576), memFreeMB: Math.round(os.freemem() / 1048576), load: os.loadavg().map((x) => Math.round(x * 100) / 100),
      osUptimeSec: Math.round(os.uptime()), node: process.version, pid: process.pid, uptimeSec: Math.round(process.uptime()),
      rssMB: Math.round(mem.rss / 1048576), heapMB: Math.round(mem.heapUsed / 1048576), heapTotalMB: Math.round(mem.heapTotal / 1048576),
      disk, dataKB: Math.round(dataKB / 1024), totals: { ...Peer.totals }, peaks: this.peaks,
      interfaces: Object.entries(os.networkInterfaces()).flatMap(([n, l]) => (l || []).filter((x) => !x.internal).map((x) => `${n} ${x.address}`)),
    };
  }
}

module.exports = { Telemetry };

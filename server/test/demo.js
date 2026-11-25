// Popola un relay locale di prova con una partita, telemetria e giornale, per vedere il pannello.
const { Client } = require(process.argv[2] + '/test/smoke.js'); const gp = require(process.argv[2] + '/lib/gp16'); const { V } = gp;
(async () => {
  const m = new Client(24530); const tk = await m.handshake(); m.op(227, [[255, V.str('USS Melchior')]]); await m.expect('resp', 227);
  const a = new Client(24531); await a.handshake(tk);
  a.op(227, [[255, V.str('USS Melchior')], [248, V.hash([[V.byte(255), V.byte(5)]])], [249, V.hash([[V.byte(255), V.str('Picard')]])]]); await a.expect('resp', 227);
  const m2 = new Client(24530); const tk2 = await m2.handshake(); const b = new Client(24531); await b.handshake(tk2);
  b.op(226, [[255, V.str('USS Melchior')], [249, V.hash([[V.byte(255), V.str('La Forge')]])]]); await b.expect('resp', 226);
  const ev = (c, s) => c.op(253, [[244, V.byte(177)], [245, V.str(s)]]);
  ev(a, '32|core_stress=68;cpx_ini=72;cpx_dir=1;cpx_proto=3;scx_band=2;scx_eff=81;wpx_doc=1;wpx_q=64;wpx_aim=1;wpx_sub=2;scx_tgt=34;scx_scan=88;plx_band=2;plx_pos=100;eng_overdrive=40;dmx_shields=38;dmx_engines=71;dmx_t1=3;');
  ev(a, '33|dmx_weapons=82;dmx_shields=20;');
  for (const t of ['Scienza: sistemi del bersaglio identificati', 'Armi: fuoco mirato su armi', 'Ingegneria: derivazione d\'emergenza su motori', 'Bersaglio: armi fuori uso']) ev(b, 'log|32|' + t);
  setInterval(() => ev(a, '32|core_stress=68;cpx_ini=72;cpx_dir=1;cpx_proto=3;scx_band=2;scx_eff=81;wpx_doc=1;wpx_q=64;wpx_aim=1;wpx_sub=2;scx_tgt=34;scx_scan=88;plx_band=2;plx_pos=100;eng_overdrive=40;dmx_shields=38;dmx_engines=71;dmx_t1=3;'), 2000);
  const post = (o) => fetch('http://127.0.0.1:24580/api/stats', { method: 'POST', body: JSON.stringify(o) });
  await post({ id: 'a'.repeat(32), name: 'Picard', rank: 'Capitano', points: 12840, stats: { 'nemici.distrutti': 41, salti: 120, 'scienza.decrittazioni': 9 } });
  await post({ id: 'b'.repeat(32), name: 'La Forge', rank: 'Tenente comandante', points: 3310, stats: { 'nemici.distrutti': 17, 'ingegneria.riparazioni': 33 } });
  console.log('pronto');
})();

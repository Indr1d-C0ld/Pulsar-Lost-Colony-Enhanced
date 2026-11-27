// IA nemica: tattiche per le navi avversarie e per gli equipaggi ostili a bordo e a terra.
// Tutto gira sull'host, perché solo l'host decide cosa fanno i nemici; i client ricevono i risultati dalla rete.
//
// Navi
// - Ruoli: ogni nave ostile riceve un ruolo (assaltatore, fiancheggiatore, cecchino) che decide la distanza di
//   combattimento, il punto da cui attacca (i fiancheggiatori cercano la poppa del bersaglio, lontano dai cannoni
//   di prua) e quanto rischia prima di ripiegare.
// - Ripiegamento: con lo scafo malridotto una nave rompe il contatto, lascia ricaricare gli scudi e torna
//   all'attacco; chi ripiega può chiamare rinforzi. I droni e i personaggi ricorrenti non ripiegano.
// - Formazione: le navi di uno stesso gruppo attaccano da angoli diversi invece di ammucchiarsi.
// - Mira: le torrette nemiche calcolano un'intercettazione vera (velocità e accelerazione del bersaglio) e sparano
//   più a lungo prima di surriscaldarsi; i nemici usano il potenziamento per schivare.
// Equipaggi a piedi (abbordaggi, banditi, basi a terra)
// - Pensano più spesso (fino a tre volte al secondo), mirano meglio, concentrano il fuoco sul bersaglio più ferito
//   e, quando sono feriti, ripiegano verso i compagni.
// - Torrette a terra e creature più attente e più rapide.
// Il livello tattico (0-3) dipende dalle regole della galassia, dalla grande minaccia e dalla potenza dell'equipaggio.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Melchior
{
    public static class EnemyAI
    {
        public static bool Enabled = true;
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }

        // ---- livello tattico
        static int level = 1; static float nextLevel;
        public static int Level
        {
            get
            {
                if (Time.time < nextLevel) return level; nextLevel = Time.time + 5f;
                int l = 1;
                try { if (Rules.Mode == "spietata") l++; else if (Rules.Mode == "tranquilla") l--; } catch { }
                try { if (Saga.Loaded && Saga.State.threat != null && Saga.State.threat.stage >= 3) l++; } catch { }
                try { var me = Stations.MyShip; if (me != null && Director.Power(me, out _, out _) >= 3f) l++; } catch { }
                return level = Math.Max(0, Math.Min(3, l));
            }
        }

        // ---- stato per nave
        public sealed class ShipMind { public string role = "assaltatore"; public bool retreating, called; public float since, angle; public int group, prio = -1; public bool fearless, routed, announced; public float routedAt; public string style = ""; }
        static readonly Dictionary<int, ShipMind> minds = new();
        static readonly string[] Roles = { "assaltatore", "fiancheggiatore", "cecchino" };
        public static int Retreats, Returns, Reinforcements; static long nPrio, nAttack, nAim, nFire, nThink, nMove, nRegroup;

        static int MyTeam() { try { return Stations.MyShip?.TeamID ?? 0; } catch { return 0; } }
        static bool Hostile(PLShipInfoBase s)
        {
            try { var me = Stations.MyShip; return s != null && me != null && s.ShipID != me.ShipID && !s.HasBeenDestroyed && s.TeamID != me.TeamID && !Traffic.Peaceful(s, me); } catch { return false; }
        }
        static bool Recurring(PLShipInfoBase s) { try { var n = (string)s.ShipName; return Saga.Loaded && Saga.State.figures.Any(f => f.alive && n.Contains(f.nick)); } catch { return false; } }

        static ShipMind Mind(PLShipInfoBase s)
        {
            if (minds.TryGetValue(s.ShipID, out var m)) return m;
            int k = s.ShipID * 7919 + 13; bool drone = false; try { drone = s.IsDrone; } catch { }
            m = new ShipMind { role = drone ? (k % 3 == 0 ? "fiancheggiatore" : "assaltatore") : Roles[Math.Abs(k) % 3], angle = (Math.Abs(k) % 360) * Mathf.Deg2Rad, fearless = drone || Recurring(s) };
            // un gruppo di nemici si divide gli angoli d'attacco
            int n = minds.Count(x => !x.Value.retreating); m.group = n; m.angle += n * 2.1f;
            // i personaggi ricorrenti combattono secondo i loro tratti; i gregari proteggono il capo
            try
            {
                var f = Nemesis.Engaged;
                if (f != null && Nemesis.FlagshipId == s.ShipID)
                {
                    var t = f.traits;
                    m.role = t.Contains("velocita") ? "fiancheggiatore" : t.Contains("contromisure") || t.Contains("prudenza") ? "cecchino" : "assaltatore";
                    m.style = t.Contains("azzardo") || t.Contains("ferocia") ? "temerario" : t.Contains("prudenza") ? "prudente" : "";
                    m.fearless = !t.Contains("prudenza");
                }
                else if (f != null && ((string)s.ShipName).Contains("Gregario")) { m.role = "fiancheggiatore"; m.style = "scorta"; }
            }
            catch { }
            minds[s.ShipID] = m; return m;
        }
        public static string RoleOf(int shipID) => minds.TryGetValue(shipID, out var m) ? m.role + (m.retreating ? " (ripiega)" : "") : "";

        static float HullFrac(PLShipInfoBase s) { try { var st = s.MyStats; return st.HullMax > 0 ? st.HullCurrent / st.HullMax : 1f; } catch { return 1f; } }
        static float ShieldFrac(PLShipInfoBase s) { try { var st = s.MyStats; return st.ShieldsMax > 0 ? st.ShieldsCurrent / st.ShieldsMax : 1f; } catch { return 1f; } }

        // Soglia di ripiegamento per ruolo (frazione di scafo); -1 = non ripiega mai.
        static float RetreatAt(PLShipInfoBase s, ShipMind m)
        {
            if (m.fearless || Level == 0 || m.style == "temerario") return -1f;
            if (m.style == "prudente") return 0.5f;
            return m.role switch { "cecchino" => 0.4f, "fiancheggiatore" => 0.3f, _ => 0.22f };
        }

        // Scelta della priorità di volo: decide quando ripiegare e quando tornare.
        internal static void OnPriority(PLFlightAI ai, ref int result)
        {
            if (!Enabled || !Host) return;
            var s = ai.MyShipInfo; if (!Hostile(s)) return; nPrio++;
            var m = Mind(s); m.prio = result;
            if (m.retreating)
            {
                bool healed = ShieldFrac(s) > 0.7f && Time.time - m.since > 12f;
                bool timeout = Time.time - m.since > 60f;
                if (!m.routed && (healed || timeout || HullFrac(s) <= 0.05f))
                {
                    m.retreating = false; Returns++;
                    Log(s, $"{Name(s)} ha ricaricato gli scudi e torna all'attacco"); Talents.NpcXp(s, 8, "ha saputo ripiegare");
                    return;
                }
                if (result is 20 or 23 or 27) Force(ai, ref result, 30);
                return;
            }
            // i piloti delle navi con equipaggio tengono le distanze (23): assaltatori e fiancheggiatori vanno all'attacco vero
            if (result == 23 && m.role != "cecchino" && Level >= 1 && s.TargetShip_ForAI != null) Force(ai, ref result, 20);
            float at = RetreatAt(s, m);
            if (at > 0 && (result is 20 or 23) && HullFrac(s) < at && ShieldFrac(s) < 0.3f)
            {
                m.retreating = true; m.since = Time.time; Retreats++;
                Log(s, $"{Name(s)} rompe il contatto: scafo danneggiato, ripiega per ricaricare gli scudi");
                if (!m.called && Level >= 2 && rng.NextDouble() < 0.3) { m.called = true; CallHelp(s); }
                Force(ai, ref result, 30);
            }
        }
        static System.Reflection.PropertyInfo prioProp;
        static void Force(PLFlightAI ai, ref int result, int value)
        {
            result = value;
            try { prioProp ??= typeof(PLFlightAI_Base).GetProperty("CurrentPriorityType"); prioProp.SetValue(ai, Enum.ToObject(prioProp.PropertyType, value)); } catch { }
        }
        static string Name(PLShipInfoBase s) { try { return "«" + (string)s.ShipName + "»"; } catch { return "La nave nemica"; } }
        static void Log(PLShipInfoBase s, string text) { try { Stations.LogEvent(Stations.MyShip.ShipID, "Tattico: " + text); } catch { } Plugin.L.LogInfo("[ia] " + text); }

        static float lastHelp;
        static void CallHelp(PLShipInfoBase s)
        {
            if (Time.time - lastHelp < 90f) return; lastHelp = Time.time;
            try
            {
                var me = Stations.MyShip; float p = Director.Power(me, out _, out _);
                var r = Director.Spawn(me, Director.PickShip(p, -1), Director.Modifiers(p), "Rinforzi");
                if (r != null) { Reinforcements++; Director.Notify("RINFORZI NEMICI", $"{Name(s)} ha chiamato aiuto: una nave nemica esce dalla curvatura."); }
            }
            catch { }
        }

        // Manovra d'attacco: corregge la rotta nativa verso un punto tattico attorno al bersaglio.
        internal static void OnAttack(PLFlightAI_Attack ai)
        {
            if (!Enabled || !Host) return;
            try
            {
                var s = ai.MyShipInfo; if (!Hostile(s)) return;
                var m = Mind(s); if (m.retreating) return;
                var t = s.TargetShip_ForAI; if (t == null || t.HasBeenDestroyed) return;
                var ctl = s.MyShipControl; if (ctl == null) return; nAttack++;
                Vector3 nat = ctl.AI_TargetVel; float mag = nat.magnitude; if (mag < 0.01f) return;
                Vector3 P = s.ExteriorTransformCached.position, T = t.ExteriorTransformCached.position;
                Vector3 F = t.ExteriorTransformCached.forward, U = t.ExteriorTransformCached.up;
                float dist = m.role switch { "cecchino" => 1300f, "fiancheggiatore" => 520f, _ => 330f };
                // punto d'attacco: i fiancheggiatori dietro il bersaglio, gli altri sul loro angolo del gruppo
                Vector3 baseDir = m.role == "fiancheggiatore" ? -F : Quaternion.AngleAxis(m.angle * Mathf.Rad2Deg, U) * F;
                Vector3 side = Vector3.Cross(baseDir, U).normalized;
                Vector3 want = T + (baseDir.normalized * Mathf.Cos(Time.time * 0.15f + m.angle) + side * 0.5f * Mathf.Sin(Time.time * 0.15f + m.angle)).normalized * dist;
                Vector3 toWant = want - P; float d = toWant.magnitude; if (d < 1f) return;
                float w = 0.25f + 0.12f * Level; // quanto la tattica pesa sulla manovra nativa
                if (d < 150f) w *= d / 150f;      // arrivati al punto, si lascia lavorare la manovra nativa
                Vector3 dir = Vector3.Slerp(nat / mag, toWant / d, w);
                ai.SetTargetVel(dir * mag);
            }
            catch { }
        }

        // Mira delle torrette nemiche: intercettazione con velocità e accelerazione stimate.
        static readonly Dictionary<int, (Vector3 v, Vector3 a, float t)> motion = new();
        static Vector3 Accel(PLShipInfoBase t, Vector3 v)
        {
            float now = Time.time;
            if (motion.TryGetValue(t.ShipID, out var mo) && now - mo.t > 0.05f)
            {
                var a = (v - mo.v) / (now - mo.t); a = Vector3.Lerp(mo.a, a, 0.3f);
                motion[t.ShipID] = (v, a, now); return a;
            }
            if (!motion.ContainsKey(t.ShipID)) motion[t.ShipID] = (v, Vector3.zero, now);
            return motion[t.ShipID].a;
        }
        internal static void OnAim(PLTurret tu, ref Vector3 result)
        {
            if (!Enabled || !Host || Level == 0) return;
            try
            {
                var s = tu.ShipStats?.Ship; if (!Hostile(s)) return;
                var t = tu.Targeted_SpaceTarget?.TryCast<PLShipInfoBase>(); if (t == null || t.HasBeenDestroyed) return;
                float speed = tu.m_ProjSpeed; if (speed < 50f || speed > 20000f) return; nAim++;
                Vector3 from = tu.TurretInstance != null ? tu.TurretInstance.transform.position : s.ExteriorTransformCached.position;
                Vector3 T = t.ExteriorTransformCached.position;
                Vector3 vt = t.ExteriorRigidbody != null ? t.ExteriorRigidbody.velocity : Vector3.zero;
                Vector3 vs = s.ExteriorRigidbody != null ? s.ExteriorRigidbody.velocity : Vector3.zero;
                Vector3 a = Accel(t, vt), rv = vt - vs;
                float time = (T - from).magnitude / speed;
                for (int i = 0; i < 4; i++) { Vector3 p = T + rv * time + 0.5f * a * time * time; time = (p - from).magnitude / speed; }
                Vector3 aim = (T + rv * time + 0.5f * a * time * time - from).normalized;
                // un po' d'errore umano, minore ai livelli alti
                float err = ((3 - Level) * 0.006f + 0.004f) * Math.Max(0.3f, 1.1f - 0.07f * (Talents.CaptainOf(s)?.level ?? 1)) + (Research.Has("disturbatori") ? 0.014f : 0f); // capitani esperti mirano meglio
                aim = (aim + new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * err).normalized;
                result = Vector3.Slerp(result, aim, 0.5f + 0.15f * Level);
            }
            catch { }
        }
        internal static void OnShouldFire(PLTurret tu, float heatOffset, float gen, ref bool result)
        {
            if (result || !Enabled || !Host || Level == 0) return;
            try
            {
                var s = tu.ShipStats?.Ship; if (!Hostile(s) || tu.IsOverheated) return;
                float limit = 0.6f + 0.1f * Level; // spara più a lungo prima di fermarsi a raffreddare
                if (tu.Heat + heatOffset < limit - gen) { result = true; nFire++; }
            }
            catch { }
        }

        // ---- a piedi
        static float Think => Level switch { 0 => 1f, 1 => 0.6f, 2 => 0.4f, _ => 0.3f };
        // la squadra si legge dalla pedina: PLPlayer.TeamID è un intero offuscato e non si confronta direttamente
        static bool EnemyBot(PLPlayer p) { try { return p != null && p.IsBot && p.MyPawn != null && p.MyPawn.GetTeamID() != MyTeam(); } catch { return false; } }
        internal static void OnPriorities(PLPlayer p)
        {
            if (!Enabled || !Host || !EnemyBot(p)) return;
            try { float now = Time.time; if (p.LastTickAITime >= now) { p.LastTickAITime = now - 1f + Think + (float)rng.NextDouble() * 0.15f; nThink++; } } catch { }
        }
        internal static void OnMove(PLBotController c)
        {
            if (!Enabled || !Host || Level == 0) return;
            try
            {
                var pl = c.MyPawn?.GetPlayer(); if (!EnemyBot(pl)) return;
                float k = 1f - 0.22f * Level; nMove++; c.RandomSmoothOffset_X *= k; c.RandomSmoothOffset_Y *= k;
            }
            catch { }
        }
        // Ripiegamento verso i compagni quando si è feriti.
        internal static void OnFindInvader(PLBot bot)
        {
            if (!Enabled || !Host || Level == 0) return;
            try
            {
                var pl = bot.PlayerOwner; if (!EnemyBot(pl)) return; var pawn = pl.MyPawn; if (pawn == null) return;
                float hp = pawn.MaxHealth > 0 ? pawn.Health / pawn.MaxHealth : 1f; if (hp > 0.3f) return;
                PLPawn mate = null; float best = 1e9f;
                foreach (var o in PLGameStatic.Instance.AllPawns)
                {
                    if (o == null || o.Pointer == pawn.Pointer || o.IsDead || o.GetTeamID() != pawn.GetTeamID() || o.MyInterior?.Pointer != pawn.MyInterior?.Pointer) continue;
                    float d = (o.transform.position - pawn.transform.position).sqrMagnitude; if (d < best && d > 4f) { best = d; mate = o; }
                }
                if (mate == null) return;
                var dest = mate.transform.position + (pawn.transform.position - mate.transform.position).normalized * 1.5f;
                bot.AI_TargetPos = dest; bot.AI_TargetPos_Raw = dest; bot.EnablePathing = true; nRegroup++;
            }
            catch { }
        }

        static float nextTick; static int lastSector = -1;
        static readonly HashSet<IntPtr> tuned = new();
        public static void Tick()
        {
            if (!Enabled || !Host || Stations.MyShip == null || Time.time < nextTick) return;
            nextTick = Time.time + 0.5f;
            try
            {
                int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { }
                if (sec != lastSector) { lastSector = sec; minds.Clear(); motion.Clear(); tuned.Clear(); seen.Clear(); peak = 0; }
                int lvl = Level;
                // navi: schivate e aggressività
                var hostile = new List<PLShipInfoBase>();
                foreach (var kv in PLEncounterManager.Instance.AllShips)
                {
                    var s = kv.Value; if (!Hostile(s)) continue; var m = Mind(s); hostile.Add(s);
                    try { s.m_ConsiderBoostActions = lvl >= 1; var c = s.MyShipControl; if (c != null) c.EnableBoostAI = lvl >= 1; } catch { }
                    try { var f = s.MyFlightAI; if (f != null) f.DefensiveMode_TickAttack = m.role == "cecchino"; } catch { }
                }
                Morale(sec, hostile);
                // a piedi: fuoco concentrato sul bersaglio più ferito, per stanza
                var pawns = PLGameStatic.Instance.AllPawns; int myTeam = MyTeam();
                var prey = new List<PLPawn>(); var hunters = new List<PLPawn>();
                foreach (var p in pawns) { if (p == null || p.IsDead) continue; if (p.GetTeamID() == myTeam) prey.Add(p); else if (EnemyBot(p.GetPlayer())) hunters.Add(p); }
                foreach (var h in hunters)
                {
                    var bot = h.GetPlayer()?.MyBot; if (bot == null) continue;
                    if (rng.NextDouble() > 0.35 + 0.2 * lvl) continue; // non tutti seguono l'ordine
                    PLPawn pick = null; float score = 1e9f;
                    foreach (var q in prey)
                    {
                        if (q.MyInterior?.Pointer != h.MyInterior?.Pointer) continue;
                        float d = (q.transform.position - h.transform.position).magnitude; if (d > 40f) continue;
                        float frac = q.MaxHealth > 0 ? q.Health / q.MaxHealth : 1f; float sc = frac * 30f + d;
                        if (sc < score) { score = sc; pick = q; }
                    }
                    if (pick != null) bot.HighPriorityTarget = pick;
                }
                // torrette a terra e creature: più attente
                if (lvl >= 1)
                    foreach (var ct in PLGameStatic.Instance.AllCombatTargets)
                    {
                        if (ct == null || tuned.Contains(ct.Pointer)) continue; tuned.Add(ct.Pointer);
                        var gt = ct.TryCast<PLGroundTurret>(); if (gt != null) { gt.FireRatePerSecond *= 1f + 0.15f * lvl; gt.TargetLockOnRange *= 1f + 0.1f * lvl; continue; }
                        var cr = ct.TryCast<PLCreature>(); if (cr != null) cr.m_Alertness *= 1f + 0.2f * lvl;
                    }
            }
            catch (Exception e) { Plugin.L.LogWarning("IA nemica: " + e.Message); }
        }

        public static string Hurt(int id, float frac)
        {
            var s = PLEncounterManager.Instance.GetShipFromID(id); if (s == null) return "nave non trovata";
            var me = Stations.MyShip; var tur = me.MyStats.GetRegularTurretAtIndex(0) ?? me.MyStats.GetMainTurret(); var st = s.MyStats;
            st.TakeShieldDamage(st.ShieldsCurrent + 1f, EDamageType.E_ENERGY, 1f, 1f, tur);
            float dmg = st.HullCurrent - st.HullMax * frac; if (dmg > 0) st.TakeHullDamage(dmg, EDamageType.E_ENERGY, me, tur);
            return $"nave {id}: scafo {frac:P0}, scudi a zero";
        }
        // ---- mira ai sottosistemi: assaltatori sugli scudi, fiancheggiatori sui motori, cecchini sulle armi
        public static int PickSub(PLShipInfoBase atk, PLShipInfoBase victim)
        {
            if (!Enabled || atk == null || victim == null || Level == 0) return 0;
            try
            {
                var me = Stations.MyShip; if (me == null || victim.ShipID != me.ShipID || !Hostile(atk)) return 0;
                if (!minds.TryGetValue(atk.ShipID, out var m)) return 0;
                if (rng.NextDouble() > 0.25 + 0.15 * Level) return 0;
                string sys = m.role switch { "fiancheggiatore" => "engines", "cecchino" => "weapons", _ => "shields" };
                // ai livelli alti si mira al sistema già più danneggiato, per finirlo
                if (Level >= 3 && rng.NextDouble() < 0.4) sys = Stations.SysIds.OrderByDescending(x => Stations.Get(me.ShipID, "dmx_" + x)).First();
                if (!m.announced) { m.announced = true; Log(atk, $"{Name(atk)} concentra il fuoco sui nostri {SysLabel(sys)}"); }
                return Array.IndexOf(Stations.SysIds, sys) + 1;
            }
            catch { return 0; }
        }
        static string SysLabel(string s) => s switch { "shields" => "generatori degli scudi", "weapons" => "sistemi d'arma", "engines" => "motori", "sensors" => "sensori", "warp" => "nucleo di curvatura", _ => "supporto vitale" };

        // ---- morale del gruppo nemico: chi perde metà delle navi si sbanda, l'ultima nave malridotta chiede la resa
        static int peak, lost, offeredSector = -1, routedSector = -1; static readonly HashSet<int> seen = new();
        public static int Routs, Surrenders;
        static void Morale(int sec, List<PLShipInfoBase> hostile)
        {
            foreach (var s in hostile) seen.Add(s.ShipID);
            int alive = hostile.Count; peak = Math.Max(peak, alive);
            lost = seen.Count(id => { var x = PLEncounterManager.Instance.GetShipFromID(id); return x == null || x.HasBeenDestroyed; });
            if (alive == 0) { if (lost > 0) { peak = 0; seen.Clear(); } return; }
            var brave = hostile.Where(s => !Mind(s).fearless).ToList(); if (brave.Count == 0) return;
            // sbandamento
            if (routedSector != sec && peak >= 3 && lost * 2 >= peak && Level >= 1 && rng.NextDouble() < 0.5)
            {
                routedSector = sec; Routs++;
                foreach (var s in brave) { var m = Mind(s); m.routed = true; m.retreating = true; m.since = Time.time; m.routedAt = Time.time; }
                Director.Notify("IL NEMICO SI SBANDA", $"Abbiamo abbattuto {lost} navi su {peak}: le altre rompono la formazione e cercano la fuga in curvatura.");
                global::Melchior.Morale.Change(2, null);
            }
            foreach (var s in brave)
            {
                var m = Mind(s);
                if (m.routed && Time.time - m.routedAt > 25f && !s.HasBeenDestroyed) { m.routed = false; try { Talents.NpcXp(s, 30, "è sfuggito alla «" + (string)Stations.MyShip.ShipName + "»"); s.Ship_WarpOutNow(); Log(s, $"{Name(s)} fugge in curvatura"); } catch { } }
            }
            // resa spontanea dell'ultima nave
            if (offeredSector != sec && alive == 1 && brave.Count == 1 && HullFrac(brave[0]) < 0.3f && !Mind(brave[0]).routed)
            {
                offeredSector = sec;
                if (rng.NextDouble() < (Research.Has("resa") ? 0.8 : 0.6) + 0.06 * Talents.Ours("ascendente") && Encounters.Start("resa_offerta", true, null, "", brave[0].ShipID.ToString())) Surrenders++;
            }
        }
        public static void Scuttle(PLShipInfoBase t)
        {
            try { var me = Stations.MyShip; var tur = me.MyStats.GetRegularTurretAtIndex(0) ?? me.MyStats.GetMainTurret(); t.MyStats.TakeHullDamage(t.MyStats.HullCurrent + 10f, EDamageType.E_ENERGY, me, tur); t.DestroySelfIfDead(me); } catch { }
        }
        public static PLShipInfoBase Ship(string id) { try { var s = PLEncounterManager.Instance.GetShipFromID(int.Parse(id)); return s == null || s.HasBeenDestroyed ? null : s; } catch { return null; } }
        public static void LetGo(PLShipInfoBase s) { try { var m = Mind(s); m.retreating = true; m.since = Time.time; m.routed = true; m.routedAt = Time.time - 20f; } catch { } }

        public static string Bots()
        {
            var sb = new System.Text.StringBuilder($"ora {Time.time:0.00} reale {Time.realtimeSinceStartup:0.00} server {PLServer.Instance?.GetEstimatedServerMs() / 1000.0:0.00}");
            foreach (var p in PLServer.Instance.AllPlayers) { if (p == null || !p.IsBot) continue; sb.Append($"\n      {(string)p.GetPlayerName()} squadra {p.MyPawn?.GetTeamID()} nemico {EnemyBot(p)} ultimo pensiero {p.LastTickAITime:0.00}"); }
            return sb.ToString();
        }
        public static string Describe()
        {
            var sb = new System.Text.StringBuilder($"IA tattica {(Enabled ? "attiva" : "spenta")}, livello {Level}; ripiegamenti {Retreats}, ritorni {Returns}, rinforzi {Reinforcements}, sbandamenti {Routs}, rese offerte {Surrenders}; chiamate: priorità {nPrio}, manovre {nAttack}, mira {nAim}, fuoco extra {nFire}, pensieri {nThink}, passi {nMove}, ripieghi a piedi {nRegroup}");
            foreach (var kv in minds) sb.Append($"\n      nave {kv.Key}: {kv.Value.role}{(kv.Value.retreating ? ", ripiega" : "")}, priorità {kv.Value.prio}");
            return sb.ToString();
        }
    }

    [HarmonyPatch(typeof(PLFlightAI), nameof(PLFlightAI.SelectTopPriority), new[] { typeof(Behave.Runtime.Tree), typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>) })]
    static class EnemyAIPriority { static void Postfix(PLFlightAI __instance, ref int __result) => EnemyAI.OnPriority(__instance, ref __result); }

    [HarmonyPatch(typeof(PLFlightAI_Attack), nameof(PLFlightAI_Attack.TickDefaultAttackAction))]
    static class EnemyAIAttack { static void Postfix(PLFlightAI_Attack __instance) => EnemyAI.OnAttack(__instance); }

    [HarmonyPatch(typeof(PLTurret), nameof(PLTurret.GetToTargetNormalized))]
    static class EnemyAIAim { static void Postfix(PLTurret __instance, ref Vector3 __result) => EnemyAI.OnAim(__instance, ref __result); }

    [HarmonyPatch(typeof(PLTurret), nameof(PLTurret.ShouldAIFire))]
    static class EnemyAIFire { static void Postfix(PLTurret __instance, float heatOffset, float heatGeneratedOnFire, ref bool __result) => EnemyAI.OnShouldFire(__instance, heatOffset, heatGeneratedOnFire, ref __result); }

    [HarmonyPatch(typeof(PLPlayer), nameof(PLPlayer.UpdateAIPriorities))]
    static class EnemyAIThink { static void Postfix(PLPlayer __instance) => EnemyAI.OnPriorities(__instance); }

    [HarmonyPatch(typeof(PLBotController), nameof(PLBotController.HandleMovement))]
    static class EnemyAIMove { static void Prefix(PLBotController __instance) => EnemyAI.OnMove(__instance); }

    [HarmonyPatch(typeof(PLBot), nameof(PLBot.TickFindInvaderAction))]
    static class EnemyAIRegroup { static void Postfix(PLBot __instance) => EnemyAI.OnFindInvader(__instance); }
}

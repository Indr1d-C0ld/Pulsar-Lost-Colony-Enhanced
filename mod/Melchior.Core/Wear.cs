// Usura e manutenzione. Sette sistemi della nave hanno uno stato da 0 a 100%, che si consuma con ciò che li sollecita
// davvero: il nucleo con il carico e la sovralimentazione, gli scudi con i colpi assorbiti, motori e armi con il
// combattimento e le manovre evasive, la curvatura a ogni salto, i sensori nelle nebulose e nelle tempeste, il supporto
// vitale con le persone a bordo. Sopra l'80% non si nota nulla; sotto, le prestazioni calano piano; sotto il 25% arrivano
// i guasti spontanei; a zero il sistema è in avaria. La qualità dei componenti (rarità) e la competenza dell'ingegnere
// rallentano il consumo.
// Manutenzione: con i ricambi della stiva (la merce della borsa) quando non si combatte; revisione completa in porto
// a pagamento; l'ingegnere bot la fa da sé quando la nave è tranquilla, e i tecnici in viaggio danno una mano.
// Simmetria: anche le navi NPC hanno il loro stato (i predoni sono malandati, le pattuglie in ordine), con gli stessi
// effetti sulle prestazioni.
// Gli effetti si applicano nel calcolo delle statistiche della nave su ogni macchina (lo stato viaggia con GalaxyView).
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Melchior
{
    public class WearState { public Dictionary<string, float> cond = new(); public int lastJump = -1, maint, overhauls, failures, parts, broken; }

    public static class Wear
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        public static WearState W => Saga.State.wear ??= new WearState();
        public static readonly string[] Systems = { "nucleo", "scudi", "motori", "curvatura", "armi", "sensori", "vitale" };
        public static string Name(string s) => s switch { "nucleo" => "Nucleo", "scudi" => "Generatore degli scudi", "motori" => "Propulsori", "curvatura" => "Motore a curvatura", "armi" => "Torrette", "sensori" => "Sensori", _ => "Supporto vitale" };
        // per i guasti: il sistema dei danni di bordo corrispondente (Stations.SysIds)
        static string DamageSys(string s) => s switch { "nucleo" => "warp", "scudi" => "shields", "motori" => "engines", "curvatura" => "warp", "armi" => "weapons", "sensori" => "sensors", _ => "life" };

        public static float Cond(string s) => Host || !PhotonNetwork.inRoom ? (W.cond.TryGetValue(s, out var v) ? v : 100f) : (GalaxyView.Current?.wear?.cond is Dictionary<string, float> d && d.TryGetValue(s, out var c) ? c : 100f);
        // efficienza: piena sopra l'80%, cala piano fino al 30%, poi più in fretta; in avaria a metà
        public static float Eff(float c) => c >= 80f ? 1f : c >= 30f ? 0.88f + 0.12f * (c - 30f) / 50f : c > 0f ? 0.70f + 0.18f * c / 30f : 0.5f;

        // ---------------------------------------------------------------- effetti (su ogni macchina)
        public static void Apply(PLShipStats s)
        {
            try
            {
                var ship = s.Ship; if (ship == null) return; var me = Stations.MyShip;
                if (me != null && ship.ShipID == me.ShipID)
                {
                    float n = Eff(Cond("nucleo")), sh = Eff(Cond("scudi")), mo = Eff(Cond("motori")), cu = Eff(Cond("curvatura")), ar = Eff(Cond("armi")), se = Eff(Cond("sensori")), vi = Eff(Cond("vitale"));
                    if (n < 1f) { s.ReactorOutputMax *= n; s.ReactorHeatOutputFactor *= 2f - n; }
                    if (sh < 1f) { s.ShieldsMax *= sh; s.ShieldsChargeRate *= sh; }
                    if (mo < 1f) { s.ThrustOutputMax *= mo; s.InertiaThrustOutputMax *= mo; s.ManeuverThrustOutputMax *= mo; }
                    if (cu < 1f) s.WarpChargeRate *= cu;
                    if (ar < 1f) { s.TurretDamageFactor *= ar; s.TurretCoolingSpeedFactor *= ar; }
                    if (se < 1f) s.EMDetection *= se;
                    if (vi < 1f) s.OxygenRefillRate *= vi;
                    return;
                }
                // navi NPC: uno stato proprio, stabile per ogni nave, che dipende dal mestiere
                float c = NpcCond(ship); if (c >= 80f) return; float e = Eff(c);
                s.ReactorOutputMax *= e; s.ShieldsMax *= Mathf.Lerp(e, 1f, 0.4f); s.ThrustOutputMax *= e; s.TurretDamageFactor *= Mathf.Lerp(e, 1f, 0.3f);
            }
            catch { }
        }
        public static float NpcCond(PLShipInfoBase s)
        {
            int h = s.ShipID * 7919 + 17; var r = new System.Random(h);
            string job = Traffic.JobOf(s.ShipID);
            return job switch
            {
                "relitto" => 5f + r.Next(20), "predoni" or "recuperatore" or "profughi" => 35f + r.Next(45), "pattuglia" or "sentinella" or "alleato" => 80f + r.Next(20),
                "mercante" or "corriere" => 55f + r.Next(45), _ => 60f + r.Next(40),
            };
        }
        public static string NpcLabel(PLShipInfoBase s) { float c = NpcCond(s); return c >= 85 ? "tenuta come un gioiello" : c >= 65 ? "in buono stato" : c >= 40 ? "malandata" : c >= 20 ? "a pezzi" : "un rottame"; }

        // ---------------------------------------------------------------- consumo
        static float nextTick, nextFail; static float shieldDmg;
        public static void OnShieldHit(PLShipInfoBase s, float dmg) { var me = Stations.MyShip; if (me != null && s != null && s.ShipID == me.ShipID && dmg > 0) shieldDmg += dmg; }
        static void Use(string s, float amount)
        {
            if (amount <= 0) return; var w = W; float c = w.cond.TryGetValue(s, out var v) ? v : 100f;
            float before = c; c = Math.Max(0f, c - amount * Slow(s)); w.cond[s] = c;
            if (before > 25f && c <= 25f) Stations.LogEvent(Stations.MyShip.ShipID, $"Ingegneria: {Name(s).ToLowerInvariant()} al {c:0}%. Serve manutenzione, o cominceranno i guasti.");
            if (before > 0f && c <= 0f) { w.broken++; Director.Notify("AVARIA", $"{Name(s)} in avaria: funziona a metà finché non lo ripariamo. Servono ricambi o un porto."); }
        }
        // rallentano il consumo: la qualità dei componenti montati e l'esperienza dell'ingegnere
        static float Slow(string s)
        {
            float f = 1f;
            var o = Saga.State.officers.FirstOrDefault(x => x.alive && x.cls == 4); if (o != null) { f *= 1f - 0.03f * CrewMind.Level(o, "riparazioni"); if (o.trait == "meticoloso") f *= 0.85f; }
            try { Director.Power(Stations.MyShip, out _, out float tier); f *= 1f - 0.05f * Math.Min(6f, tier); } catch { } // componenti di qualità si consumano meno
            return Math.Max(0.35f, f);
        }
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                var me = Stations.MyShip; var w = W; float now = Time.time; int j = Obscured.Jumps();
                foreach (var s in Systems) if (!w.cond.ContainsKey(s)) w.cond[s] = 100f;
                // a ogni salto
                if (w.lastJump < 0) w.lastJump = j;
                if (j != w.lastJump)
                {
                    int n = Math.Min(3, j - w.lastJump); w.lastJump = j;
                    for (int i = 0; i < n; i++)
                    {
                        Use("curvatura", 2.2f); Use("nucleo", 1.0f); Use("scudi", 0.4f); Use("motori", 0.5f); Use("armi", 0.3f);
                        Use("sensori", 0.4f + (Environments.Here.Length > 0 ? 0.8f : 0f)); Use("vitale", 0.3f + 0.1f * (Logistics.Crew() + Passengers.Aboard));
                    }
                    Saga.MarkDirty(); GalaxyView.Dirty();
                }
                if (now < nextTick) return; nextTick = now + 10f;
                // nel tempo: battaglia, sovralimentazione, manovre evasive, colpi agli scudi
                bool fight = false; try { var t = me.TargetShip; fight = me.m_AlertLevel >= 2 || (t != null && !t.HasBeenDestroyed && t.HostileShips.Contains(me.ShipID)); } catch { }
                if (fight) { Use("armi", 0.35f); Use("motori", 0.25f); Use("nucleo", 0.2f); }
                float od = Stations.Get(me.ShipID, "eng_overdrive"); if (od > 0) Use("nucleo", 0.25f * od / 50f);
                if (Stations.Get(me.ShipID, "plx_evade") > 0) Use("motori", 0.5f);
                if (shieldDmg > 0) { float sm = 1f; try { sm = Math.Max(1f, me.MyStats.ShieldsMax); } catch { } Use("scudi", 3f * shieldDmg / sm); shieldDmg = 0f; }
                Failures(now);
                SelfService(fight);
                GalaxyView.Dirty();
            }
            catch (Exception e) { Plugin.L.LogWarning("Usura: " + e.Message); nextTick = Time.time + 30f; }
        }
        // sotto il 25% i sistemi si guastano da soli, ogni tanto
        static void Failures(float now)
        {
            if (now < nextFail) return; nextFail = now + 45f + rng.Next(45);
            var w = W; var me = Stations.MyShip;
            foreach (var s in Systems)
            {
                float c = w.cond[s]; if (c > 25f) continue;
                if (rng.NextDouble() > (c <= 0f ? 0.6 : 0.25 + (25f - c) / 50f)) continue;
                string k = "dmx_" + DamageSys(s); Stations.Set(me.ShipID, k, Math.Min(90f, Stations.Get(me.ShipID, k) + 15f + rng.Next(15)));
                w.failures++; Service.Add("manutenzione.guasti", 1);
                Stations.LogEvent(me.ShipID, rng.Next(3) switch { 0 => $"Guasto: {Name(s).ToLowerInvariant()} perde colpi, l'usura presenta il conto.", 1 => $"Scintille dal pannello: {Name(s).ToLowerInvariant()} in difficoltà. È solo usura, per ora.", _ => $"Un relè cede: {Name(s).ToLowerInvariant()} si spegne per un attimo e riparte zoppicando." });
                break; // un guasto alla volta
            }
        }
        // l'ingegnere bot (o i tecnici in viaggio) fanno manutenzione da soli quando la nave è tranquilla
        static float selfAcc;
        static void SelfService(bool fight)
        {
            if (fight || CrewMind.GravityNow > 0) return;
            bool bot = false; try { var p = PLServer.Instance.GetCachedFriendlyPlayerOfClass(4, Stations.MyShip); bot = p != null && p.IsBot; } catch { }
            float rate = (bot ? 1.2f : 0f) + (Passengers.Has("tecnici") ? 0.8f : 0f); if (rate <= 0f) return;
            var w = W; var worst = Systems.OrderBy(x => w.cond[x]).First(); if (w.cond[worst] >= 98f) return;
            // il lavoro di fino consuma ricambi: uno ogni 15 punti
            selfAcc += rate; if (selfAcc >= 15f) { if (Parts(1) == 0) { selfAcc = 15f; return; } selfAcc -= 15f; w.parts++; Service.Add("manutenzione.ricambi", 1); }
            w.cond[worst] = Math.Min(100f, w.cond[worst] + rate);
        }

        // ---------------------------------------------------------------- manutenzione e revisione
        static int Parts(int n) { n = Math.Min(n, Market.Have("ricambi")); if (n > 0) Market.Take("ricambi", n); return n; }
        public static int PartsFor(string s) { float c = Cond(s); return Math.Max(1, (int)Math.Ceiling((100f - c) / 20f)); }
        public static int OverhaulCost() { var w = W; float missing = Systems.Sum(s => 100f - (w.cond.TryGetValue(s, out var c) ? c : 100f)); return (int)(missing * 9f * (Ports.Of(PLServer.GetCurrentSector()?.ID ?? -1)?.prosperity >= 70 ? 1.2f : 1f)); }
        public static string Maintain(string s)
        {
            var w = W; if (!Systems.Contains(s)) return ""; float c = w.cond[s]; if (c >= 99f) return $"{Name(s)} è già in perfetto ordine.";
            bool fight = false; try { var me = Stations.MyShip; var t = me.TargetShip; fight = me.m_AlertLevel >= 2 || (t != null && !t.HasBeenDestroyed && t.HostileShips.Contains(me.ShipID)); } catch { }
            if (fight) return "In battaglia non si smonta niente: prima si vince, poi si ripara.";
            int need = PartsFor(s); int got = Parts(need); if (got == 0) return "Non abbiamo ricambi in stiva: si comprano in borsa, nei porti.";
            float gain = got * 20f * (1f + 0.05f * Officers.Skill(4, false)); w.cond[s] = Math.Min(100f, c + gain); w.maint++; w.parts += got;
            Service.Add("manutenzione.interventi", 1); Service.Add("manutenzione.ricambi", got); Feats.Wear("manutenzione"); Officers.Credit(4, 1);
            Saga.MarkDirty(); GalaxyView.Dirty();
            return $"Manutenzione: {Name(s).ToLowerInvariant()} dal {c:0} al {w.cond[s]:0}% ({got} ricambi).";
        }
        public static string Overhaul()
        {
            if (!Shipyard.InPort()) return "La revisione completa si fa in un cantiere di porto.";
            int cost = OverhaulCost(); if (cost <= 0) return "La nave è già in perfetto ordine.";
            int have = 0; try { have = PLServer.Instance.CurrentCrewCredits; } catch { } if (have < cost) return $"La revisione costa {cost} crediti: non li abbiamo.";
            try { PLServer.Instance.CurrentCrewCredits = have - cost; } catch { }
            var w = W; bool limped = Systems.Any(s => w.cond[s] <= 0f); foreach (var s in Systems) w.cond[s] = 100f; w.overhauls++;
            Service.Add("manutenzione.revisioni", 1); Feats.Wear(limped ? "zoppicando" : "revisione"); Saga.MarkDirty(); GalaxyView.Dirty();
            return $"Revisione completa in cantiere: {cost} crediti, e la nave torna come nuova.";
        }

        // ---------------------------------------------------------------- pannello (scheda Manutenzione dell'Ingegneria)
        public class Panel { public Dictionary<string, float> cond = new(); public int parts, cost, maint, failures; public bool port; }
        public static Panel Snapshot()
        {
            var w = W; var p = new Panel { parts = Market.Have("ricambi"), maint = w.maint, failures = w.failures };
            foreach (var s in Systems) p.cond[s] = w.cond.TryGetValue(s, out var c) ? c : 100f;
            try { p.port = Shipyard.InPort(); p.cost = OverhaulCost(); } catch { }
            return p;
        }
        public static void Request(string op, string arg)
        {
            if (Host) { var t = op == "revisione" ? Overhaul() : Maintain(arg); if (t.Length > 0) Stations.Announce("MANUTENZIONE", t); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|manutenzione|{op}|{arg}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var q = msg.Split('|'); if (q.Length >= 4 && Host) Request(q[2], q[3]); }

        public static string Describe() { var w = W; int al = -1; string tg = "-"; try { al = Stations.MyShip.m_AlertLevel; var t = Stations.MyShip.TargetShip; tg = t == null ? "-" : $"{(string)t.ShipName} ostile {t.HostileShips.Contains(Stations.MyShip.ShipID)}"; } catch { } return "usura: " + string.Join(", ", Systems.Select(s => $"{s} {(w.cond.TryGetValue(s, out var c) ? c : 100f):0}%")) + $"; ricambi {Market.Have("ricambi")}, manutenzioni {w.maint}, revisioni {w.overhauls}, guasti {w.failures}, avarie {w.broken}; allarme {al}, bersaglio {tg}"; }
        public static string Force(string s, float v) { if (s == "tutti") foreach (var x in Systems) W.cond[x] = v; else if (Systems.Contains(s)) W.cond[s] = v; Saga.MarkDirty(); GalaxyView.Dirty(); try { Stations.MyShip.MyStats.CalculateStats(); } catch { } return Describe(); }
    }
}

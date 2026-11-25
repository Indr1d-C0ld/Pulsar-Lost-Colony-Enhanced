// Ufficiali automatici: quando una stazione della nave è affidata a un bot, l'host ne simula le decisioni
// sui nostri sottosistemi (nucleo, rete EPS, armonica, dottrina e mira, ugelli, ordini). Le regole sono semplici
// e hanno un margine d'errore regolabile: un bot fa il suo lavoro, ma un giocatore attento fa meglio.
// Tutto in config/melchior.stations.json, sezione "Bots".
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class BotsConfig
    {
        public bool Enabled = true;
        public float Skill = 0.75f;          // probabilità di riuscita delle procedure manuali (spurgo, allineamento, decrittazione)
        public int ThinkSeconds = 3;         // ogni quanto un bot riconsidera la situazione
        public bool Engineer = true, Scientist = true, Weapons = true, Pilot = true, Captain = true;
        public bool Log = true;              // scrive nel log le decisioni dei bot
    }

    static class StationBots
    {
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static readonly Dictionary<string, long> ready = new();
        static readonly Random rng = new();
        static long next;

        static bool Ready(string key) => !ready.TryGetValue(key, out var t) || clock.ElapsedMilliseconds >= t;
        static void Wait(string key, int seconds) => ready[key] = clock.ElapsedMilliseconds + seconds * 1000L;
        static bool Lucky(float p) => rng.NextDouble() < p;
        static void Say(string who, string what)
        {
            if (Stations.Cfg.Bots.Log) Plugin.L.LogInfo($"[bot] {who}: {what}");
            // le regolazioni di routine non finiscono nel giornale di bordo
            if (what.StartsWith("rete EPS") || what.StartsWith("sovralimentazione") || what.StartsWith("mira")) return;
            var me = Stations.MyShip; if (me != null) Stations.LogEvent(me.ShipID, who + ": " + what);
        }

        static bool IsBotAt(PLShipInfo ship, int cls)
        {
            try { var p = PLServer.Instance.GetCachedFriendlyPlayerOfClass(cls, ship); return p != null && p.IsBot; } catch { return false; }
        }

        public static void Tick()
        {
            var b = Stations.Cfg.Bots; long now = clock.ElapsedMilliseconds;
            if (!b.Enabled || now < next) return;
            next = now + Math.Max(1, b.ThinkSeconds) * 1000L;
            try
            {
                if (!PhotonNetwork.inRoom || !PhotonNetwork.isMasterClient || PLServer.Instance == null) return;
                var ship = Stations.MyShip; if (ship == null || ship.MyStats == null) return;
                int id = ship.ShipID; int alert = ship.m_AlertLevel; var target = ship.TargetShip;
                bool fight = alert >= 2 || (target != null && !target.HasBeenDestroyed);
                if (b.Engineer && IsBotAt(ship, 4)) Engineer(id, fight);
                if (b.Scientist && IsBotAt(ship, 2)) Scientist(id, fight);
                if (b.Weapons && IsBotAt(ship, 3)) Weapons(id, fight, target);
                if (b.Pilot && IsBotAt(ship, 1)) Pilot(ship, id, fight);
                // il capitano spende iniziativa solo con un nemico davvero ingaggiato, non per il solo stato d'allarme
                if (b.Captain && IsBotAt(ship, 0)) Captain(id, target != null && !target.HasBeenDestroyed);
            }
            catch (Exception e) { Plugin.L.LogWarning("Ufficiali automatici: " + e.Message); next = now + 15000; }
        }

        static float G(int id, string k) => Stations.Get(id, k);

        static void Engineer(int id, bool fight)
        {
            var k = Stations.Cfg.Core; var b = Stations.Cfg.Bots; float stress = G(id, "core_stress");
            if (G(id, "core_lock") > 0) return;
            // carico: in combattimento spinge il nucleo finché lo stress lo permette
            float want = fight ? (stress < 45 ? 50 : stress < 70 ? 20 : 0) : 0;
            if (Math.Abs(G(id, "eng_overdrive") - want) > 0.5f) { Stations.TrySet(id, "eng_overdrive", want); Say("Ingegneria", $"sovralimentazione a {want:0}"); }
            // rete EPS: scudi e armi in combattimento, a spese di curvatura e supporto vitale
            var plan = fight ? new Dictionary<string, float> { ["eps_warp"] = -1, ["eps_life"] = -1, ["eps_shields"] = 1, ["eps_weapons"] = 1 } : new Dictionary<string, float>();
            foreach (var c in Stations.Cfg.Controls.Where(c => c.Group == "eps").OrderBy(c => plan.TryGetValue(c.Id, out var v) ? v : 0f))
            {
                float v = plan.TryGetValue(c.Id, out var pv) ? pv : 0f;
                if (Math.Abs(G(id, c.Id) - v) > 0.01f && Stations.TrySet(id, c.Id, v) == null) Say("Ingegneria", $"rete EPS: {c.Label} a {v:+0;-0;0}");
            }
            // squadre danni: la A sul sistema più colpito, la B sul secondo (o sullo stesso se è l'unico)
            if (Stations.Cfg.Damage.Enabled)
            {
                var hurt = Enumerable.Range(0, Stations.SysIds.Length).Select(i => (i, d: G(id, "dmx_" + Stations.SysIds[i]))).Where(x => x.d > 3f).OrderByDescending(x => x.d).ToList();
                int a = hurt.Count > 0 ? hurt[0].i + 1 : 0, bb = hurt.Count > 1 ? hurt[1].i + 1 : a;
                if ((int)G(id, "dmx_t1") != a) { Stations.Set(id, "dmx_t1", a); if (a > 0) Say("Ingegneria", "squadra A su " + Stations.SysNames[a - 1].ToLowerInvariant()); }
                if ((int)G(id, "dmx_t2") != bb) { Stations.Set(id, "dmx_t2", bb); if (bb > 0) Say("Ingegneria", "squadra B su " + Stations.SysNames[bb - 1].ToLowerInvariant()); }
                if (fight && hurt.Count > 0 && hurt[0].d >= 70f && stress < 60 && G(id, "dmx_byp") <= 0 && Ready("bypass"))
                {
                    Wait("bypass", Stations.Cfg.Damage.BypassCooldown + 5);
                    Stations.Set(id, "dmx_bypsys", hurt[0].i + 1); Stations.Set(id, "dmx_byp", Stations.Cfg.Damage.BypassSeconds);
                    Stations.Set(id, "core_stress", Math.Min(100f, stress + Stations.Cfg.Damage.BypassStress)); Say("Ingegneria", "derivazione d'emergenza su " + Stations.SysNames[hurt[0].i].ToLowerInvariant());
                }
            }
            // procedure
            if (stress >= k.WarnAt && Ready("vent"))
            {
                Wait("vent", k.VentCooldown + 6);
                if (Lucky(b.Skill)) { Stations.Set(id, "core_stress", Math.Max(0f, stress - k.VentRelief)); Stations.Initiative(id, Stations.Cfg.Captain.GainPerSuccess); Say("Ingegneria", "spurgo del plasma riuscito"); }
                else { Stations.Set(id, "core_stress", Math.Min(100f, stress + k.VentFailPenalty)); Say("Ingegneria", "spurgo del plasma fallito"); }
            }
            else if (fight && stress < 50 && G(id, "core_align") <= 0 && Ready("align"))
            {
                Wait("align", k.AlignCooldown + 20);
                if (Lucky(b.Skill * 0.8f)) { Stations.Set(id, "core_align", k.AlignSeconds); Stations.Initiative(id, Stations.Cfg.Captain.GainPerSuccess); Say("Ingegneria", "matrice allineata"); }
                else { Stations.Set(id, "core_stress", Math.Min(100f, stress + k.AlignFailPenalty)); Say("Ingegneria", "allineamento mancato"); }
            }
        }

        static void Scientist(int id, bool fight)
        {
            var k = Stations.Cfg.Science; var b = Stations.Cfg.Bots;
            bool wantScan = G(id, "scx_tgt") > 0;
            if (Stations.Cfg.Sensors.Enabled && (G(id, "scx_scanon") > 0) != wantScan) { Stations.Set(id, "scx_scanon", wantScan ? 1 : 0); if (wantScan) Say("Scienza", "scansione del bersaglio avviata"); }
            if (!fight) return;
            int band = (int)G(id, "scx_band"); float eff = G(id, "scx_eff");
            if ((band == 0 || eff < 35) && Ready("rotate"))
            {
                Wait("rotate", k.RotateCooldown + 3);
                int nb = band == 0 ? 1 + rng.Next(3) : 1 + (band + rng.Next(2)) % 3;
                Stations.Set(id, "scx_band", nb); Stations.Set(id, "scx_eff", 100); Stations.Set(id, "scx_dip", k.RotateDipSeconds);
                Say("Scienza", "armonica ruotata sulla banda " + nb);
            }
            if (G(id, "scx_sol") <= 0 && Ready("decrypt"))
            {
                Wait("decrypt", k.DecryptCooldown + 35);
                if (Lucky(b.Skill * 0.7f)) { Stations.Set(id, "scx_sol", k.SolutionSeconds); Stations.Initiative(id, Stations.Cfg.Captain.GainPerSuccess * 2); Say("Scienza", "firma nemica decrittata"); }
                else Say("Scienza", "decrittazione fallita");
            }
        }

        static void Weapons(int id, bool fight, PLShipInfoBase target)
        {
            var w = Stations.Cfg.Weapons;
            if (!fight) return;
            if ((int)G(id, "wpx_doc") == 0) { Stations.Set(id, "wpx_doc", 1); Stations.Set(id, "wpx_q", 0); Say("Armi", "dottrina PRECISA"); }
            // mira: prima gli scudi del bersaglio, poi lo scafo
            int aim = 0;
            // con un margine: passa allo scafo sotto il 12% di scudi nemici, torna agli scudi solo sopra il 40%
            try
            {
                var ts = target?.MyStats; int cur = (int)G(id, "wpx_aim");
                if (ts != null && ts.ShieldsMax > 1f) { float r = ts.ShieldsCurrent / ts.ShieldsMax; aim = cur == 2 ? (r > 0.40f ? 1 : 2) : (r > 0.12f ? 1 : 2); }
            }
            catch { }
            if ((int)G(id, "wpx_aim") != aim) { Stations.Set(id, "wpx_aim", aim); Say("Armi", "mira su " + (aim == 1 ? "scudi" : aim == 2 ? "scafo" : "misto")); }
            // tiro mirato: appena la scansione lo permette, prima le armi nemiche, poi scudi e motori
            if (Stations.Cfg.Damage.Enabled && G(id, "scx_tgt") > 0 && G(id, "scx_scan") >= Stations.Cfg.Sensors.SubsystemAt)
            {
                int tid = (int)G(id, "scx_tgt") - 1, want = 0;
                foreach (int sys in new[] { 2, 1, 3 }) if (G(tid, "dmx_" + Stations.SysIds[sys - 1]) < 100f) { want = sys; break; }
                if ((int)G(id, "wpx_sub") != want) { Stations.Set(id, "wpx_sub", want); Say("Armi", want > 0 ? "fuoco mirato su " + Stations.SysNames[want - 1].ToLowerInvariant() : "fuoco sull'intera nave"); }
            }
            // raffica: solo se il nucleo regge e i condensatori sono pronti
            if (G(id, "wpx_jam") <= 0 && G(id, "wpx_burst") <= 0 && G(id, "core_stress") < 60 && Ready("burst"))
            {
                Wait("burst", w.BurstCooldown + 8);
                float lvl = 65 + rng.Next(40); // ogni tanto esagera
                if (lvl > w.OverloadAt) { Stations.Set(id, "wpx_jam", w.JamSeconds); Stations.Set(id, "core_stress", Math.Min(100f, G(id, "core_stress") + w.CoreStressPerBurst)); Say("Armi", "condensatori saltati"); }
                else
                {
                    float pct = (float)Math.Round(w.BurstMaxDamage * lvl / 100f);
                    Stations.Set(id, "wpx_burstp", pct); Stations.Set(id, "wpx_burst", w.BurstSeconds);
                    Stations.Set(id, "core_stress", Math.Min(100f, G(id, "core_stress") + w.CoreStressPerBurst * lvl / 100f));
                    if (lvl >= 80) Stations.Initiative(id, Stations.Cfg.Captain.GainPerSuccess);
                    Say("Armi", $"raffica al {lvl:0}%");
                }
            }
        }

        static void Pilot(PLShipInfo ship, int id, bool fight)
        {
            var k = Stations.Cfg.Pilot; float trim = G(id, "plx_trim");
            if (trim == 0 && fight) { Stations.Set(id, "plx_trim", 0.01f); return; } // prende in mano gli ugelli
            if (Math.Abs(trim) > 22) { float t = trim + (trim > 0 ? -k.TrimStep : k.TrimStep); Stations.Set(id, "plx_trim", Math.Abs(t) < 0.01f ? 0.01f : t); }
            // fascia d'ingaggio: adotta quella in cui la nave già si trova, e la rivede di rado
            if (Stations.Cfg.Engage.Enabled)
            {
                int cur = Stations.BandOf(Stations.TargetDistance(ship)), band = (int)G(id, "plx_band");
                if (cur == 0) { if (band != 0) { Stations.Set(id, "plx_band", 0); Stations.Set(id, "plx_pos", 0); } }
                else if (band != cur && Ready("band")) { Wait("band", 25); Stations.Set(id, "plx_pos", 0); Stations.Set(id, "plx_band", cur); Say("Pilota", "fascia d'ingaggio " + Stations.BandNames[cur - 1].ToLowerInvariant()); }
            }
            if (fight)
            {
                var st = ship.MyStats; bool hurt = st.ShieldsMax > 1f && st.ShieldsCurrent / st.ShieldsMax < 0.3f;
                if (hurt && G(id, "plx_evade") <= 0 && Ready("evade")) { Wait("evade", k.EvadeCooldown + 2); Stations.Set(id, "plx_evade", k.EvadeSeconds); Say("Pilota", "manovra evasiva"); }
            }
            else if (G(id, "plx_route") <= 0 && Ready("route"))
            {
                Wait("route", 90); float q = 35 + rng.Next(45);
                Stations.Set(id, "plx_routeq", q); Stations.Set(id, "plx_route", k.RouteSeconds); Say("Pilota", $"rotta calcolata al {q:0}%");
            }
        }

        static void Captain(int id, bool fight)
        {
            var c = Stations.Cfg.Captain; float ini = G(id, "cpx_ini"); if (ini <= 0) { ini = c.Start; Stations.Set(id, "cpx_ini", ini); }
            int dir = (int)G(id, "cpx_dir");
            // protocollo squadre danni finché ci sono sistemi malridotti
            if (Stations.Cfg.Protocols.Enabled)
            {
                bool need = Stations.SysIds.Any(x => G(id, "dmx_" + x) >= 30f); int pr = (int)G(id, "cpx_proto");
                if (need != ((pr & 1) != 0) && Ready("proto")) { Wait("proto", 20); Stations.Set(id, "cpx_proto", need ? pr | 1 : pr & ~1); Say("Capitano", "protocollo squadre danni " + (need ? "in vigore" : "revocato")); }
            }
            if (!fight) { if (dir != 0) { Stations.Set(id, "cpx_dir", 0); Say("Capitano", "ordine revocato"); } return; }
            if (G(id, "cpx_tired") > 0) return;
            if (G(id, "core_stress") >= 80 && ini >= c.InterventionCost && Ready("int"))
            { Wait("int", 15); Stations.Set(id, "core_stress", Math.Max(0f, G(id, "core_stress") - c.EmergencyRelief)); Stations.Set(id, "cpx_ini", Math.Max(0.01f, ini - c.InterventionCost)); Say("Capitano", "energia d'emergenza al nucleo"); return; }
            if (dir == 0 && ini > 35) { Stations.Set(id, "cpx_dir", 1); Say("Capitano", "ordine BATTAGLIA"); }
            else if (dir != 0 && ini < 8) { Stations.Set(id, "cpx_dir", 0); Say("Capitano", "ordine revocato per risparmiare l'equipaggio"); }
        }
    }
}

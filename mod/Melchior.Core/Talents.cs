// Talenti: la progressione che conta davvero, con le stesse regole per tutti.
// - Ogni postazione ha quattro talenti, ciascuno fino al terzo grado. I punti si guadagnano con i gradi di servizio
//   (uno per grado) e con le specializzazioni (uno ogni tre livelli, che crescono usando la postazione).
// - Un talento agisce quando chi lo possiede siede a quella postazione: il giocatore, oppure l'ufficiale bot, che ha
//   talenti in proporzione al suo grado e al suo tratto.
// - Simmetria: anche ogni capitano NPC (nemici, pattuglie, traffico, personaggi ricorrenti) ha nome, livello,
//   esperienza e talenti scelti con le stesse regole; i suoi talenti pesano sui danni, sulla difesa e sugli
//   abbordaggi esattamente come i nostri. Chi sopravvive a uno scontro, o vince, guadagna esperienza e torna più forte.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Melchior
{
    public class NpcCaptain { public string name = "", ship = "", style = "", job = ""; public int level = 1, xp, met, st = -1, wealth, born = -1; public bool alive = true, retired; public Dictionary<string, int> talents = new(); public List<string> life = new(); }

    public static class Talents
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }

        public sealed class Def { public string id = "", name = "", effect = ""; public int cls; }
        public static readonly Def[] Defs =
        {
            // Comando
            new() { id = "carisma", cls = 0, name = "Carisma", effect = "il morale sale del 20% in più per grado" },
            new() { id = "negoziatore", cls = 0, name = "Negoziatore", effect = "merci vendute +3% e compensi dei contratti +5% per grado" },
            new() { id = "ascendente", cls = 0, name = "Ascendente", effect = "i nemici si arrendono più volentieri (+6% per grado)" },
            new() { id = "stratega", cls = 0, name = "Stratega", effect = "imboscate meno frequenti (-8% per grado)" },
            // Timone
            new() { id = "evasione", cls = 1, name = "Evasione", effect = "danni subiti allo scafo -3% per grado" },
            new() { id = "contrabbandiere", cls = 1, name = "Contrabbandiere", effect = "dogane e ispezioni -15% per grado" },
            new() { id = "navigatore", cls = 1, name = "Navigatore", effect = "viveri consumati -10% per grado" },
            new() { id = "inseguitore", cls = 1, name = "Inseguitore", effect = "teletrasporto d'abbordaggio fino a +1 km per grado" },
            // Scienza
            new() { id = "ricercatore", cls = 2, name = "Ricercatore", effect = "dati di ricerca +25% per grado" },
            new() { id = "xenologo", cls = 2, name = "Xenologo", effect = "studio delle specie aliene +20% per grado" },
            new() { id = "analista", cls = 2, name = "Analista", effect = "danni ai punti deboli +5% per grado" },
            new() { id = "sondatore", cls = 2, name = "Sondatore", effect = "sonde -20% di costo per grado" },
            // Armi
            new() { id = "artigliere", cls = 3, name = "Artigliere", effect = "danni allo scafo nemico +4% per grado" },
            new() { id = "sfondatore", cls = 3, name = "Sfondatore", effect = "danni agli scudi nemici +4% per grado" },
            new() { id = "arrembaggio", cls = 3, name = "Arrembaggio", effect = "le squadre d'abbordaggio sabotano e saccheggiano di più" },
            new() { id = "sentinella", cls = 3, name = "Sentinella", effect = "gli incursori a bordo sabotano il 30% in meno per grado" },
            // Ingegneria
            new() { id = "corazza", cls = 4, name = "Corazza", effect = "danni subiti allo scafo -3% per grado" },
            new() { id = "economo", cls = 4, name = "Economo", effect = "paghe degli ufficiali -8% per grado" },
            new() { id = "schermi", cls = 4, name = "Schermi rinforzati", effect = "danni subiti agli scudi -3% per grado" },
            new() { id = "meccanico", cls = 4, name = "Meccanico", effect = "guasti ai sistemi -10% per grado" },
        };
        public static Def D(string id) => Defs.FirstOrDefault(d => d.id == id);
        public const int MaxRank = 3;

        // ---- giocatore locale: punti e scelte (salvati nello stato di servizio)
        public static Dictionary<string, int> Mine => Service.Me.Talents ??= new Dictionary<string, int>();
        public static int Earned()
        {
            int rankIdx = Array.IndexOf(Service.Cfg.Ranks, Service.Rank()); if (rankIdx < 0) rankIdx = 0;
            int spec = Service.Cfg.Specs.Sum(s => Math.Min(10, Service.Spec(s).level));
            return 1 + rankIdx + spec / 3;
        }
        public static int Spent() => Mine.Values.Sum();
        public static string Learn(string id)
        {
            var d = D(id); if (d == null) return "talento sconosciuto";
            int r = Mine.GetValueOrDefault(id); if (r >= MaxRank) return "già al massimo";
            if (Spent() >= Earned()) return "nessun punto talento libero";
            Mine[id] = r + 1; Service.Touch(); Broadcast(true); Feats.Talent(r + 1);
            return $"{d.name} al grado {r + 1}";
        }
        public static string Unlearn() { Mine.Clear(); Service.Touch(); Broadcast(true); return "talenti azzerati"; }

        // ---- rete: ogni giocatore comunica all'host i suoi talenti
        static readonly Dictionary<int, Dictionary<string, int>> remote = new();
        static float nextSend;
        static void Broadcast(bool now = false)
        {
            if (!now && Time.time < nextSend) return; nextSend = Time.time + 30f;
            try
            {
                var me = PLNetworkManager.Instance?.LocalPlayer; if (me == null) return;
                string csv = string.Join(",", Mine.Select(kv => kv.Key + ":" + kv.Value));
                if (Host) { remote[me.GetPlayerID()] = new Dictionary<string, int>(Mine); return; }
                PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|talenti|{me.GetPlayerID()}|{csv}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient });
            }
            catch { }
        }
        public static void OnRequest(string msg)
        {
            var p = msg.Split('|'); if (p.Length < 4 || !Host || !int.TryParse(p[2], out int id)) return;
            var d = new Dictionary<string, int>();
            foreach (var kv in p[3].Split(',', StringSplitOptions.RemoveEmptyEntries)) { var x = kv.Split(':'); if (x.Length == 2 && D(x[0]) != null && int.TryParse(x[1], out int r)) d[x[0]] = Math.Clamp(r, 0, MaxRank); }
            remote[id] = d;
        }

        // ---- ufficiali bot: talenti dal grado e dal tratto (stessa scala dei giocatori)
        static Dictionary<string, int> ForOfficer(Officer o)
        {
            var d = new Dictionary<string, int>(); if (o == null) return d;
            int pts = 1 + o.rank * 2; var pool = Defs.Where(x => x.cls == o.cls).ToList();
            int seed = (o.first + o.last).GetHashCode() & 0x7fffffff;
            for (int i = 0; i < pts; i++) { var t = pool[(seed / (i + 1) + i) % pool.Count]; if (d.GetValueOrDefault(t.id) < MaxRank) d[t.id] = d.GetValueOrDefault(t.id) + 1; }
            return d;
        }

        // ---- capitani NPC: nome, livello, talenti con le stesse regole
        static List<NpcCaptain> Captains => Saga.State.captains ??= new List<NpcCaptain>();
        static readonly Dictionary<int, NpcCaptain> byShip = new(); static int lastSector = -1;
        static readonly string[] Firsts = { "Ryan", "Mara", "Ivo", "Selene", "Bastian", "Nadia", "Orso", "Tamsin", "Hugo", "Lea", "Corvin", "Greta", "Dario", "Zara", "Teo", "Kira", "Yuri", "Vera" };
        static readonly string[] Lasts = { "Holt", "Vance", "Moreau", "Kessler", "Ilves", "Duarte", "Okafor", "Sato", "Navarro", "Brandt", "Castelli", "Lindqvist", "Barros", "Kowal", "Albani", "Ferri" };
        public static NpcCaptain CaptainOf(PLShipInfoBase s, string jobHint = null)
        {
            if (s == null) return null; var me = Stations.MyShip; if (me != null && s.ShipID == me.ShipID) return null;
            if (Derelicts.IsWreck(s.ShipID) || Traffic.JobOf(s.ShipID) == "relitto") return null; // un relitto non ha più nessuno al comando
            if (byShip.TryGetValue(s.ShipID, out var c)) return c;
            string ship = ""; try { ship = (string)s.ShipName; } catch { }
            c = Captains.FirstOrDefault(x => x.alive && x.ship == ship && ship.Length > 0);
            if (c == null)
            {
                int k = (s.ShipID * 7919 + ship.GetHashCode()) & 0x7fffffff;
                float p = 1f; try { p = Director.Power(me, out _, out _); } catch { }
                var fig = Nemesis.Engaged != null && Nemesis.FlagshipId == s.ShipID ? Nemesis.Engaged : null;
                bool civil = jobHint != null ? jobHint is not ("predoni" or "pattuglia" or "cacciatore" or "incursore" or "alleato" or "sentinella" or "pirata") : Traffic.RoleOf(s.ShipID) is string tr && tr.Length > 0 && !tr.Contains("predoni") && !tr.Contains("pattuglia");
                int lvl = fig != null ? 2 + fig.rank * 2 : Math.Clamp(1 + (int)(p / 3f) + k % 3 - 1 + (EnemyAI.Level - 1) + Obscured.Jumps() / 25 - (civil ? 1 : 0), 1, 10);
                // la nave racconta il suo capitano: modificatori → stile e livello
                int mods = 0; try { mods = s.GetModifiers(); } catch { }
                bool Has(EShipModifierType t) => (mods & (1 << (int)t)) != 0;
                if (Has(EShipModifierType.ELITE)) lvl += 2; if (Has(EShipModifierType.LEGENDARY)) lvl += 3;
                if (Has(EShipModifierType.NEGLECTED) || Has(EShipModifierType.VULNERABLE)) lvl -= 1;
                lvl = Math.Clamp(lvl, 1, 10);
                int st = Has(EShipModifierType.ARMORED) ? 3 : Has(EShipModifierType.SWIFT) || Has(EShipModifierType.SILENT) ? 1 : Has(EShipModifierType.BRUTAL) ? 0 : Has(EShipModifierType.RECKLESS) ? 2 : -1;
                if (civil && st is 0 or 2 or -1) st = k % 2 == 0 ? 1 : 3; // un mercante non va all'arrembaggio
                string job = fig != null ? "pirata" : jobHint ?? (Traffic.RoleOf(s.ShipID) is string rr && rr.StartsWith("traffico: ") ? rr.Substring(10) : "predoni");
                c = new NpcCaptain { name = fig != null ? fig.Full : $"{Firsts[k % Firsts.Length]} {Lasts[(k / 7) % Lasts.Length]}", ship = ship, level = lvl, st = st, job = job, born = Obscured.Jumps() };
                Train(c, k);
                if (fig != null || Traffic.IsKnown(ship) || job is "predoni" or "cacciatore" or "pattuglia" || rng.NextDouble() < 0.3) { Captains.Add(c); if (Captains.Count > 80) { var old = Captains.FirstOrDefault(x => !x.alive) ?? Captains[0]; Captains.Remove(old); } Saga.MarkDirty(); }
            }
            c.met++; byShip[s.ShipID] = c; return c;
        }
        // talenti dei capitani NPC: un punto per livello, scelti secondo il loro modo di combattere
        static void Train(NpcCaptain c, int seed)
        {
            if (c.st < 0) c.st = seed % 4;
            string[] style = c.st switch { 0 => new[] { "artigliere", "sfondatore", "corazza" }, 1 => new[] { "evasione", "schermi", "analista" }, 2 => new[] { "arrembaggio", "artigliere", "evasione" }, _ => new[] { "corazza", "schermi", "artigliere" } };
            c.style = c.st switch { 0 => "aggressivo", 1 => "sfuggente", 2 => "arrembatore", _ => "corazzato" };
            c.talents.Clear(); int pts = c.level;
            for (int i = 0; i < pts; i++) { var t = style[i % style.Length]; if (c.talents.GetValueOrDefault(t) < MaxRank) c.talents[t] = c.talents.GetValueOrDefault(t) + 1; }
        }
        // capitani noti per nome (equipaggi concorrenti, personaggi): stesse regole, livello e stile dati
        public static NpcCaptain Register(string name, string ship, int level, int st, string job = "rivale")
        {
            var c = Captains.FirstOrDefault(x => x.ship == ship && x.name == name);
            if (c == null) { c = new NpcCaptain { name = name, ship = ship, level = Math.Clamp(level, 1, 10), st = st, job = job, born = Obscured.Jumps() }; Train(c, name.GetHashCode() & 0x7fffffff); Captains.Add(c); Saga.MarkDirty(); }
            return c;
        }
        // esperienza dei capitani NPC: chi sopravvive o vince sale di livello e torna più forte
        public static void NpcXp(PLShipInfoBase s, int xp, string why)
        {
            var c = CaptainOf(s); if (c == null) return; c.xp += xp;
            bool up = false;
            while (c.xp >= 40 * c.level && c.level < 10) { c.xp -= 40 * c.level; c.level++; up = true; }
            if (up)
            {
                Train(c, (c.name.GetHashCode() & 0x7fffffff));
                if (!Captains.Contains(c) && c.ship.Length > 0) Captains.Add(c);
                Saga.MarkDirty();
                if (c.level >= 5) News.Post("dicerie", $"Il nome di {c.name}, capitano della «{c.ship}», corre nei porti: ora è al livello {c.level} ({why}).");
            }
        }

        // ---- grado effettivo di un talento per una nave
        static readonly Dictionary<string, float> cache = new(); static float cacheAt;
        public static int R(PLShipInfoBase ship, string id)
        {
            if (ship == null) return 0;
            var me = Stations.MyShip;
            if (me != null && ship.ShipID == me.ShipID) return Ours(id);
            if (!Host) return 0;
            var c = CaptainOf(ship); return c?.talents.GetValueOrDefault(id) ?? 0;
        }
        public static int Ours(string id)
        {
            var d = D(id); if (d == null) return 0;
            if (Time.time - cacheAt > 2f) { cache.Clear(); cacheAt = Time.time; }
            if (cache.TryGetValue(id, out var v)) return (int)v;
            int r = 0;
            try
            {
                var p = PLServer.Instance.GetCachedFriendlyPlayerOfClass(d.cls, Stations.MyShip);
                if (p != null)
                {
                    if (p.IsBot) r = ForOfficer(Saga.State.officers.FirstOrDefault(o => o.alive && o.cls == d.cls && o.injuredUntil <= Obscured.Jumps())).GetValueOrDefault(id);
                    else if (remote.TryGetValue(p.GetPlayerID(), out var t)) r = t.GetValueOrDefault(id);
                    else if (p == PLNetworkManager.Instance?.LocalPlayer) r = Mine.GetValueOrDefault(id);
                }
            }
            catch { }
            cache[id] = r; return r;
        }
        public static float Mul(string id, float per) => 1f + Ours(id) * per; // moltiplicatore per la nostra nave

        public static void Tick()
        {
            try
            {
                Broadcast();
                int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { }
                if (sec != lastSector) { lastSector = sec; byShip.Clear(); }
                if (Host) foreach (var kv in byShip.ToList()) { PLShipInfoBase s = null; try { s = PLEncounterManager.Instance.GetShipFromID(kv.Key); } catch { } if (s != null && s.HasBeenDestroyed) { bool rival = Traffic.IsRival(s); try { Traffic.NoteFoe((string)s.ShipName); } catch { } try { var me2 = Stations.MyShip; if (me2 != null && (s.HostileShips.Contains(me2.ShipID) || me2.HostileShips.Contains(s.ShipID))) { int sf = s.FactionID; Leaders.OnDeed(sf == 4 || s.IsInfected ? "infetti" : "nemico", sf); } } catch { } if (rival) Traffic.RivalDown(s); else if (Captains.Contains(kv.Value)) { kv.Value.alive = false; Saga.MarkDirty(); } Wreck(s, kv.Value); byShip.Remove(kv.Key); } }
            }
            catch { }
        }

        // il relitto di un veterano ha componenti migliori
        static void Wreck(PLShipInfoBase s, NpcCaptain c)
        {
            try
            {
                var me = Stations.MyShip; if (me == null || c.level < 3) return;
                bool foe = s.HostileShips.Contains(me.ShipID) || me.HostileShips.Contains(s.ShipID); if (foe && c.level >= 5) Feats.VeteranDown(c.level); if (foe) Life(c, "nave abbattuta dalla nostra"); if (!foe || rng.NextDouble() > 0.15 + 0.07 * c.level + Loot.Mine("recupero")) return;
                var got = Loot.Grant(me, 1 + c.level / 4, 2 + c.level / 3, "relitto di un veterano");
                if (got != null) Stations.LogEvent(me.ShipID, $"Dal relitto della «{c.ship}» (cap. {c.name}, liv. {c.level}) recuperiamo {got.Name}");
            }
            catch { }
        }
        public static List<NpcCaptain> Known => Captains;
        public static void Life(NpcCaptain c, string what) { c.life.Add($"salto {Obscured.Jumps()}: {what}"); if (c.life.Count > 6) c.life.RemoveAt(0); Saga.MarkDirty(); }
        // un pirata già incontrato, vivo, che può tornare a farsi vedere
        public static NpcCaptain KnownPirate() { var l = Captains.Where(c => c.alive && !c.retired && c.job is "predoni" or "pirata" && c.met > 0 && c.ship.Length > 0 && !c.name.Contains("«")).ToList(); return l.Count == 0 ? null : l[rng.Next(l.Count)]; }
        public static string Line(PLShipInfoBase s) { var c = Host ? CaptainOf(s) : null; return c == null ? "" : $"cap. {c.name}, liv. {c.level} ({c.style})"; }
        public static string Describe()
        {
            var sb = new System.Text.StringBuilder($"punti {Spent()}/{Earned()}; nostri: {string.Join(", ", Defs.Where(d => Ours(d.id) > 0).Select(d => d.id + " " + Ours(d.id)))}");
            foreach (var kv in byShip) sb.Append($"\n      nave {kv.Key}: {kv.Value.name} liv. {kv.Value.level} {kv.Value.style} xp {kv.Value.xp} [{string.Join(",", kv.Value.talents.Select(t => t.Key + t.Value))}]");
            return sb.ToString();
        }
    }
}

// Personaggi ricorrenti: pirati, trafficanti e comandanti ribelli con nome, soprannome, grado e tratti, che ricordano
// ogni incontro con l'equipaggio. Chi viene abbattuto può salvarsi in una capsula e tornare sfregiato e più forte;
// chi muore lascia un erede che giura vendetta; chi mette in fuga l'equipaggio se ne vanta nei porti.
// Fra un incontro e l'altro si muovono per la galassia, compiono imprese (raccontate dal notiziario) e salgono di grado.
// Le taglie sulla bacheca indicano l'ultima posizione nota: arrivati in quel settore, il bersaglio si fa vedere.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class Figure
    {
        public string first = "", nick = "", last = "", role = "pirata", heir = "", memory = "nuovo";
        public int faction = -1, rank = 1, grudge, met, fled, scars, deeds, sector = -1, memSector = -1;
        public List<string> traits = new();
        public bool alive = true;
        public string Full => $"{first} «{nick}» {last}";
    }

    public static class Nemesis
    {
        static readonly System.Random rng = new();
        static readonly string[] Firsts = { "Kestrel", "Mara", "Teodor", "Sable", "Ines", "Corvin", "Jax", "Oriana", "Bastian", "Nadia", "Ulf", "Selene", "Dario", "Tamsin", "Hugo", "Zara", "Livia", "Rook" };
        static readonly string[] Lasts = { "Vane", "Quill", "Brask", "Ndiaye", "Valdrada", "Hale", "Moretti", "Kell", "Roe", "Voss", "Karsk", "Okafor", "Ferro", "Strand", "Vex", "Ashby", "Marchetti", "Dray" };
        static readonly string[] Nicks = { "Cenere", "Ossidiana", "Lama", "Corvo", "Brace", "Tempesta", "Sciacallo", "Spettro", "Falce", "Gelo", "Ruggine", "Vipera", "Eclisse", "Scheggia", "Zanna", "Mezzanotte" };
        static readonly string[] ScarNicks = { "Cicatrice", "Mezzafaccia", "Fenice", "Due Volte", "Ferro", "Brace Viva", "Ritorno", "Fantasma" };
        public static readonly Dictionary<string, (string label, EShipModifierType? mod)> Traits = new()
        {
            ["corazza"] = ("corazzatura pesante", EShipModifierType.ARMORED),
            ["ferocia"] = ("ferocia", EShipModifierType.BRUTAL),
            ["velocita"] = ("motori truccati", EShipModifierType.SWIFT),
            ["azzardo"] = ("gusto dell'azzardo", EShipModifierType.RECKLESS),
            ["contromisure"] = ("contromisure antimissile", EShipModifierType.MISSILE_DEFENSE_SYSTEM),
            ["prudenza"] = ("capsula sempre pronta", null),
            ["rancore"] = ("memoria lunga", null),
            ["scorta"] = ("gregari fedeli", null),
        };
        public static string RoleLabel(Figure f) => f.role switch { "trafficante" => "trafficante", "ribelle" => $"comandante ribelle ({GalaxyWar.Name(f.faction)})", _ => "pirata" };

        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static List<Figure> All => Saga.State.figures ??= new List<Figure>();
        public static IEnumerable<Figure> Alive => All.Where(f => f.alive);
        static int Jumps() { try { return Obscured.Jumps(); } catch { return 0; } }
        static int CurSector() { try { return PLServer.GetCurrentSector()?.ID ?? -1; } catch { return -1; } }
        static string OurShip() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }

        // incontro in corso
        static Figure engaged; static int flagship = -1, engagedSector = -1; static readonly List<int> escorts = new(); static bool hunting;
        static float nextLife = 120f; static int lastJumps = -1;
        public static Figure Engaged => engaged;

        public static Figure Find(string full) => All.FirstOrDefault(f => f.Full == full);

        // ---- nascita di un personaggio
        static Figure Create(string heirOf = null, int grudge = 0)
        {
            var used = All.Select(x => x.last).ToHashSet();
            var f = new Figure
            {
                first = Firsts[rng.Next(Firsts.Length)], last = Lasts[rng.Next(Lasts.Length)],
                nick = Nicks[rng.Next(Nicks.Length)], rank = 1 + (rng.NextDouble() < 0.3 ? 1 : 0), grudge = grudge, heir = heirOf ?? "",
            };
            var lasts = Lasts.Where(x => !used.Contains(x)).ToList(); if (lasts.Count > 0) f.last = lasts[rng.Next(lasts.Count)];
            var usedNicks = All.Where(x => x.alive).Select(x => x.nick).ToHashSet(); var nicks = Nicks.Where(x => !usedNicks.Contains(x)).ToList(); if (nicks.Count > 0) f.nick = nicks[rng.Next(nicks.Count)];
            double r = rng.NextDouble();
            f.role = r < 0.55 ? "pirata" : r < 0.8 ? "trafficante" : "ribelle";
            if (f.role == "ribelle") f.faction = GalaxyWar.Factions.Where(x => x != 4).ElementAt(rng.Next(4));
            f.traits = Traits.Keys.OrderBy(_ => rng.Next()).Take(2).ToList();
            if (heirOf != null) { f.memory = "erede"; if (!f.traits.Contains("rancore")) f.traits[1] = "rancore"; }
            f.sector = RandomSector(CurSector(), 2, 7);
            All.Add(f); Saga.MarkDirty();
            Plugin.L.LogInfo($"[nemesi] nuovo personaggio: {f.Full}, {RoleLabel(f)}, grado {f.rank}, tratti {string.Join("/", f.traits)}");
            return f;
        }

        // Un settore "aperto" a una certa distanza in salti dal settore indicato.
        static int RandomSector(int from, int minJumps, int maxJumps)
        {
            try
            {
                var start = PLServer.GetSectorWithID(from); var all = new List<PLSectorInfo>();
                foreach (var kv in PLGlobal.Instance.Galaxy.AllSectorInfos) { var s = kv.Value; if (s?.MySPI != null && s.VisualIndication == ESectorVisualIndication.NONE && s.MissionSpecificID == -1 && s.ID != from) all.Add(s); }
                if (all.Count == 0) return -1;
                if (start != null)
                    for (int tries = 0; tries < 30; tries++)
                    {
                        var s = all[rng.Next(all.Count)]; int d = Distance(start, s);
                        if (d >= minJumps && d <= maxJumps) return s.ID;
                    }
                return all[rng.Next(all.Count)].ID;
            }
            catch { return -1; }
        }

        public static int Distance(PLSectorInfo a, PLSectorInfo b)
        {
            try { var p = PLGlobal.Instance.Galaxy.GetPathToSector(a, b); return p == null ? 99 : Math.Max(0, p.Count - 1); } catch { return 99; }
        }
        public static int JumpsTo(int sector)
        {
            try { return Distance(PLServer.GetCurrentSector(), PLServer.GetSectorWithID(sector)); } catch { return 99; }
        }

        // ---- frasi di saluto, dalla memoria dell'ultimo incontro
        static string Hail(Figure f, bool hunt)
        {
            string at = f.memSector >= 0 ? $" nel settore {f.memSector}" : "";
            var opts = f.memory switch
            {
                "erede" => new[] { $"Avete ucciso {f.heir}. Io c'ero, quel giorno. Adesso tocca a voi.", $"{f.heir} mi ha insegnato tutto. Anche a non dimenticare." },
                "sfregiato" => new[] { $"Mi avete lasciato un ricordo{at}. Sono venuto a restituirvelo.", "Mi chiamano così per colpa vostra. Guardatemi bene, è l'ultima faccia che vedrete." },
                "vinto" => new[] { $"Scappate anche stavolta? Come{at}?", "La «" + OurShip() + "». Che piacere: l'ultima volta siete spariti in curvatura così in fretta." },
                "fuggito" => new[] { "L'ultima volta mi è andata male. Ho avuto tempo di prepararmi.", "Ho cambiato nave, capitano. E anche umore." },
                _ => new[] { $"Il mio nome è {f.first} {f.last}, ma nei porti mi chiamano «{f.nick}». Ricordatevelo.", "Bella nave. Sarà ancora più bella nel mio hangar.", "Niente di personale, capitano. Non ancora." },
            };
            string line = opts[rng.Next(opts.Length)];
            if (hunt && rng.NextDouble() < 0.6) line = "Ho saputo della taglia sulla mia testa. Venite a riscuoterla. " + line;
            return line;
        }

        // ---- comparsa (dal direttore, da una taglia o dalla console)
        public static bool Appear(PLShipInfo ship, float power, Figure f = null, bool hunt = false)
        {
            if (!Host || engaged != null || ship == null || InWarp()) return false;
            if (f == null)
            {
                var pool = Alive.Where(x => !IsHunted(x)).ToList(); if (pool.Count == 0) return false;
                double tot = pool.Sum(W), r = rng.NextDouble() * tot; f = pool[^1];
                foreach (var x in pool) { r -= W(x); if (r <= 0) { f = x; break; } }
            }
            int mods = 0; foreach (var t in f.traits) if (Traits.TryGetValue(t, out var d) && d.mod != null) mods |= 1 << (int)d.mod.Value;
            if (f.rank >= 3) mods |= 1 << (int)EShipModifierType.ELITE;
            float p = power + f.rank * 1.5f + (f.role == "ribelle" ? 2 : 0);
            var flag = Director.Spawn(ship, FlagType(f, p), mods | Director.Modifiers(p), f.Full); if (flag == null) return false;
            engaged = f; hunting = hunt; flagship = flag.ShipID; flagHull = 1f; engagedSector = CurSector(); escorts.Clear();
            int n = (f.rank >= 2 ? 1 : 0) + (f.traits.Contains("scorta") ? 1 : 0) + (f.role == "ribelle" ? 1 : 0);
            for (int i = 0; i < Math.Min(3, n); i++) { var e = Director.Spawn(ship, Director.PickShip(power, -1), Director.Modifiers(power), $"Gregario di {f.last}"); if (e != null) escorts.Add(e.ShipID); }
            f.met++; Saga.MarkDirty();
            Director.Notify("COMUNICAZIONE IN ARRIVO", $"{f.Full}: «{Hail(f, hunt)}»");
            Plugin.L.LogInfo($"[nemesi] {f.Full} compare (grado {f.rank}, {escorts.Count} gregari, taglia {hunt})");
            Service.Add("nemesi.incontri", 1);
            return true;
        }
        // Ammiraglia adatta al ruolo: navi con equipaggio, più grandi con il grado; i ribelli volano con navi della loro fazione.
        static readonly string[] Pirate = { "E_OUTRIDER", "E_BOUNTY_HUNTER_01", "E_DESTROYER", "E_WDCRUISER", "E_ANNIHILATOR" };
        static readonly Dictionary<int, string> RebelHeavy = new() { [0] = "E_DESTROYER", [1] = "E_CARRIER", [2] = "E_WDCRUISER", [5] = "E_POLYTECH_SHIP" };
        static EShipType FlagType(Figure f, float p)
        {
            int t = Math.Clamp((int)(p / 4f) - (f.role == "trafficante" ? 1 : 0), 0, Pirate.Length - 1);
            string name = f.role == "ribelle" && t >= 2 && t < 4 && RebelHeavy.TryGetValue(f.faction, out var h) ? h : Pirate[t];
            return Enum.TryParse<EShipType>(name, out var x) ? x : Director.PickShip(p, 1);
        }
        static double W(Figure f) => (1 + f.grudge * (f.traits.Contains("rancore") ? 2 : 1)) * (0.6 + f.rank * 0.4);
        static bool IsHunted(Figure f) { var c = Saga.State.contract; return c != null && c.kind == "taglia" && c.target == f.Full; }

        // ---- esito dell'incontro
        static float flagHull = 1f;
        static void Track()
        {
            if (engaged == null) return;
            var f = engaged; int sector = CurSector(); bool leaving = InWarp() || sector != engagedSector;
            PLShipInfoBase s = null; try { s = PLEncounterManager.Instance.GetShipFromID(flagship); } catch { }
            bool dead = s != null && s.HasBeenDestroyed;
            try { if (s != null && s.MyStats != null) { if (s.MyStats.HullCurrent <= 0f) dead = true; else flagHull = s.MyStats.HullCurrent / Math.Max(1f, s.MyStats.HullMax); } } catch { }
            if (s == null && !leaving && flagHull <= 0.2f) dead = true; // sparita mentre stava per esplodere
            if (dead && !leaving) { Defeated(f); End(); return; }
            if (leaving) { Won(f); End(); return; }
            if (s == null) End(); // sparita per altri motivi: nessun esito
        }
        static void End() { engaged = null; flagship = -1; escorts.Clear(); hunting = false; Saga.MarkDirty(); }

        // L'equipaggio è saltato via con il personaggio ancora in piedi.
        static void Won(Figure f)
        {
            f.fled++; f.memory = "vinto"; f.memSector = engagedSector; f.grudge = Math.Max(0, f.grudge - 1);
            if (f.fled % 2 == 0 && f.rank < 5) f.rank++;
            News.Post("taglie", $"{f.Full} si vanta nei porti di aver messo in fuga la «{OurShip()}» nel settore {engagedSector}.");
            Stations.LogEvent(Stations.MyShip?.ShipID ?? 0, $"Ci siamo sganciati da {f.Full}.");
            if (hunting) Contracts.OnHunt(false, f);
        }

        static void Defeated(Figure f)
        {
            double survive = Math.Min(0.75, 0.12 + 0.1 * f.rank + (f.traits.Contains("prudenza") ? 0.3 : 0));
            if (f.scars >= 2) survive = 0; // alla terza volta non c'è capsula che tenga
            var ship = Stations.MyShip; string tail;
            if (rng.NextDouble() < survive)
            {
                string old = f.nick; f.scars++; f.rank = Math.Min(5, f.rank + 1); f.grudge += f.traits.Contains("rancore") ? 6 : 3;
                f.nick = ScarNicks[rng.Next(ScarNicks.Length)]; f.memory = "sfregiato"; f.memSector = engagedSector; f.sector = RandomSector(engagedSector, 3, 8);
                Director.Notify("RAPPORTO DI PLANCIA", $"L'ammiraglia di {f.first} {f.last} esplode, ma una capsula di salvataggio entra in curvatura prima che possiamo agganciarla.");
                News.Post("taglie", $"{f.first} «{old}» {f.last} ce l'ha fatta: nei porti ora si fa chiamare «{f.nick}» e cerca la «{OurShip()}».");
                tail = "una capsula ha raggiunto la curvatura";
                var got = Loot.Grant(ship, 2, 5, "nemesi"); if (got != null) Director.Notify("RITROVAMENTO", "Tra i rottami della sua ammiraglia: " + got.Name + ".");
            }
            else
            {
                f.alive = false; f.memory = "abbattuto"; f.memSector = engagedSector;
                Director.Notify("RAPPORTO DI PLANCIA", $"L'ammiraglia di {f.Full} è distrutta. Nessuna capsula. È finita.");
                News.Post("taglie", $"La carriera di {f.Full} finisce nel settore {engagedSector}: ad abbatterne l'ammiraglia è stata la «{OurShip()}».", alsoBanner: false);
                var got = Loot.Grant(ship, f.rank >= 4 ? 5 : 4, f.rank >= 4 ? 7 : 6, "nemesi"); if (got != null) Director.Notify("RITROVAMENTO", "Nella stiva dell'ammiraglia: " + got.Name + ".");
                Saga.State.onore += 1; Service.Add("nemesi.abbattute", 1);
                // chi era al suo fianco non dimentica
                if (rng.NextDouble() < 0.7) { var h = Create(f.Full, 4 + f.rank); News.Post("taglie", $"{h.Full} raccoglie l'eredità di {f.first} {f.last} e giura vendetta contro la «{OurShip()}»."); }
                tail = "ammiraglia distrutta, nessuna capsula";
            }
            Factions.OnFigureDown(f);
            if (hunting) Contracts.OnHunt(true, f);
            Saga.Chronicle($"Scontro con {f.first} {f.last}: {tail}.");
        }

        // ---- vita fra un incontro e l'altro
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                Track();
                // taglia accettata: arrivati nel settore indicato, il bersaglio si fa vedere
                var c = Saga.State.contract;
                if (c != null && c.kind == "taglia" && engaged == null)
                {
                    var f = Find(c.target);
                    if (f == null || !f.alive) Contracts.OnHunt(false, null, "il bersaglio non è più nella galassia");
                    else if (Jumps() > c.expires) Contracts.OnHunt(false, f, "la pista si è raffreddata");
                    else if (CurSector() == c.sector && !InWarp() && Appear(Stations.MyShip, Director.Power(Stations.MyShip, out _, out _), f, true)) { }
                }
                int j = Jumps(); bool jumped = lastJumps >= 0 && j != lastJumps; lastJumps = j;
                if (!jumped && UnityEngine.Time.time < nextLife) return;
                nextLife = UnityEngine.Time.time + 200f + rng.Next(120);
                Life(1);
            }
            catch (Exception e) { Plugin.L.LogWarning("Nemesi: " + e.Message); }
        }
        static bool InWarp() { try { return Stations.MyShip.InWarp; } catch { return false; } }

        // Un passo di vita per tutti i personaggi; durante l'assenza dell'equipaggio se ne fanno diversi.
        static void Life(int steps, bool quiet = false)
        {
            int news = 0;
            for (int s = 0; s < steps; s++)
            {
                bool first = !All.Any();
                while (Alive.Count() < 4) { var f = Create(); if (!quiet && !first) News.Post("dicerie", $"Un nome nuovo gira nei porti: {f.Full}, {RoleLabel(f)}."); }
                foreach (var f in Alive.ToList())
                {
                    if (f == engaged) continue;
                    var c = Saga.State.contract; bool hunted = IsHunted(f);
                    // spostamento: un settore alla volta, verso un vicino aperto
                    if (rng.NextDouble() < (hunted ? 0.25 : 0.4))
                    {
                        try
                        {
                            var at = PLServer.GetSectorWithID(f.sector); var nb = at?.Neighbors;
                            if (nb != null && nb.Count > 0)
                            {
                                var n = nb[rng.Next(nb.Count)];
                                if (n?.MySPI != null && n.VisualIndication == ESectorVisualIndication.NONE && n.MissionSpecificID == -1 && n.ID != CurSector())
                                {
                                    f.sector = n.ID;
                                    if (hunted) { c.sector = n.ID; Contracts.Touch(); Director.Notify("BACHECA", $"Nuovo avvistamento di {f.Full}: ora è nel settore {n.ID}, a {JumpsTo(n.ID)} salti."); Contracts.Plot(n.ID); }
                                }
                            }
                        }
                        catch { }
                    }
                    // imprese: raccontate dal notiziario, ogni tanto fanno salire di grado
                    if (rng.NextDouble() < 0.18 && news < 2)
                    {
                        f.deeds++; news++;
                        if (f.deeds % 4 == 0 && f.rank < 5) f.rank++;
                        if (!quiet || news <= 2) News.Post("taglie", Deed(f));
                    }
                }
            }
            Saga.MarkDirty();
        }

        static string Deed(Figure f)
        {
            string near = $"nel settore {f.sector}";
            var opts = f.role switch
            {
                "trafficante" => new[] { $"{f.Full} ha venduto armi a entrambe le parti del fronte {near}.", $"Un carico rubato all'Accademia è passato per le mani di {f.Full}, {near}.", $"{f.Full} ha comprato il silenzio di una pattuglia {near}." },
                "ribelle" => new[] { $"{f.Full} ha attaccato un posto di guardia della {GalaxyWar.Name(f.faction)} {near}.", $"Altri equipaggi della {GalaxyWar.Name(f.faction)} hanno disertato per seguire {f.Full}.", $"{f.Full} ha dichiarato guerra personale alla {GalaxyWar.Name(f.faction)}." },
                _ => new[] { $"{f.Full} ha assaltato un mercantile {near}. Nessuno ha risposto al segnale di soccorso.", $"I predoni di {f.Full} hanno taglieggiato una stazione mineraria {near}.", $"{f.Full} ha preso un ostaggio importante {near} e chiede un riscatto." },
            };
            return opts[rng.Next(opts.Length)] + (f.deeds % 4 == 0 && f.rank > 1 ? $" Ormai è di grado {f.rank}." : "");
        }

        public static void Offline(double hours)
        {
            if (!Host || hours < 0.5) return;
            try { Life((int)Math.Clamp(hours * 2, 1, 12), quiet: true); } catch (Exception e) { Plugin.L.LogWarning("Nemesi (assenza): " + e.Message); }
        }

        // I predoni che distruggono un convoglio a volte hanno un capo con un nome.
        public static string Blame()
        {
            var p = Alive.Where(f => f.role == "pirata").ToList(); if (p.Count == 0 || rng.NextDouble() < 0.4) return null;
            var f = p[rng.Next(p.Count)]; f.deeds++; Saga.MarkDirty(); return f.Full;
        }

        public static string Stars(int rank) => new string('*', rank) + new string('.', 5 - rank);
        public static string TraitText(Figure f) => string.Join(", ", f.traits.Select(t => Traits.TryGetValue(t, out var d) ? d.label : t));

        // ---- console
        public static string Describe() => !All.Any() ? "nessun personaggio" : string.Join("\n      ", All.OrderByDescending(f => f.alive).ThenByDescending(f => f.rank).Select(f =>
            $"{(f.alive ? "" : "† ")}{f.Full,-30} {RoleLabel(f)}, grado {f.rank}, rancore {f.grudge}, incontri {f.met}, fughe nostre {f.fled}, cicatrici {f.scars}, settore {f.sector} ({JumpsTo(f.sector)} salti), tratti {TraitText(f)}, memoria {f.memory}").Prepend("personaggi:"));
        public static string Force(string name)
        {
            var f = name == null ? null : Alive.FirstOrDefault(x => x.Full.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (Alive.Count() == 0) Life(1, true);
            return Appear(Stations.MyShip, Director.Power(Stations.MyShip, out _, out _), f) ? $"compare {engaged.Full}" : "nessuno può comparire adesso";
        }
        public static string ForceLife(int n) { Life(n); return Describe(); }
    }
}

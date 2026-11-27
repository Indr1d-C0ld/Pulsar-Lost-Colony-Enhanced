// Atti di fondazione e basi dell'equipaggio (alla maniera di Ultima Online, nello spazio).
// Un atto di fondazione si compra all'ufficio del catasto dei porti o si ottiene come ricompensa (campagne, covi,
// luoghi leggendari, grande minaccia). Con un atto, in un settore aperto adatto, si fonda una base di quel tipo:
//   miniera (rendita, doppia vicino ad asteroidi o giacimenti), emporio (rendita e commerci propri), laboratorio
//   (ricerca: frammenti di mappa, freno alla grande minaccia), cantiere (riparazioni e migliorie scontate),
//   fortezza (protegge la regione), faro (rapporti automatici sui settori vicini).
// Regole: niente basi in porti, missioni, covi, crisi, cuori della minaccia o settori di altre basi; nello spazio di una
// fazione serve il permesso (gratis se amica, a pagamento se neutrale, negato se ostile); niente basi nello spazio infetto.
// Ogni base ha livello (1-3), difese (0-3), scorte (calano a ogni salto, si ripristinano visitandola o con consegne),
// manutenzione e una rendita. Può essere assalita: si corre a difenderla, altrimenti decidono le sue difese.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class Deed { public string kind = "", source = ""; public long t; }
    public class Base
    {
        public string kind = "", name = ""; public int sector = -1, level = 1, defense, supply = 100, state, buildUntil = -1, raidUntil = -1, debt; // stato: 0 cantiere, 1 operativa
        public long earned; public bool spawned; public int colonists;
    }

    public static class Bases
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        public sealed class Kind { public string id = "", name = "", desc = "", brief = ""; public int price; }
        public static readonly Kind[] Kinds =
        {
            new() { id = "miniera", name = "Avamposto minerario", brief = "rendita", price = 6000, desc = "rendita a ogni salto, doppia in un campo di asteroidi o vicino a un giacimento" },
            new() { id = "emporio", name = "Emporio commerciale", brief = "commerci", price = 7000, desc = "rendita e commerci propri, di più in territorio amico e sulle rotte dei convogli" },
            new() { id = "laboratorio", name = "Laboratorio di ricerca", brief = "ricerca", price = 8000, desc = "frammenti di mappa più frequenti e un freno alla grande minaccia, il doppio in una nebulosa" },
            new() { id = "cantiere", name = "Cantiere privato", brief = "riparazioni", price = 9000, desc = "riparazioni complete e migliorie della nave scontate" },
            new() { id = "fortezza", name = "Fortezza", brief = "protezione", price = 10000, desc = "imboscate e cacciatori più rari nella regione, basi vicine più difese" },
            new() { id = "faro", name = "Faro di comunicazione", brief = "rapporti", price = 6000, desc = "rapporti automatici sui settori vicini a ogni arrivo nella regione" },
        };
        public static Kind K(string id) => Kinds.FirstOrDefault(k => k.id == id);
        // coloni portati dai passeggeri: scorte e, ogni dieci, un livello (fino al terzo)
        public static void Colonists(Base b, int n)
        {
            b.colonists += n; b.supply = Math.Min(100, b.supply + 5 * n);
            if (b.level < 3 && b.colonists >= 10 * b.level) { b.level++; Feats.BaseLevel(b.level); Director.Notify("BASE", $"Con i nuovi coloni {b.name} cresce: livello {b.level}."); }
            Saga.MarkDirty(); GalaxyView.Dirty();
        }
        public const int MaxBases = 6, BuildCost = 1500, BuildJumps = 3;
        static List<Base> All => Saga.State.bases ??= new List<Base>();
        static List<Deed> Deeds => Saga.State.deeds ??= new List<Deed>();
        public static IEnumerable<Base> List => Host ? All : Enumerable.Empty<Base>();
        static int Jumps() => Obscured.Jumps();
        static int CurSector() { try { return PLServer.GetCurrentSector()?.ID ?? -1; } catch { return -1; } }
        static string Ship() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }
        public static Base At(int sector) => All.FirstOrDefault(b => b.sector == sector);
        public static Base Here() => At(CurSector());
        static int Dist(int sector) => Nemesis.JumpsTo(sector);
        public static IEnumerable<Base> Near(string kind, int jumps) => All.Where(b => b.state == 1 && b.kind == kind && Dist(b.sector) <= jumps);

        // ---- atti
        public static void GiveDeed(string kind, string source)
        {
            if (!Host || !Saga.Loaded || K(kind) == null) return;
            Deeds.Add(new Deed { kind = kind, source = source, t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }); Saga.MarkDirty(); GalaxyView.Dirty();
            Director.Notify("ATTO DI FONDAZIONE", $"Abbiamo un atto di fondazione: {K(kind).name.ToLowerInvariant()} ({source}). Si usa dalla scheda Basi dell'Ingegneria, nel settore che scegliamo.");
        }
        public static string RandomKind() => Kinds[rng.Next(Kinds.Length)].id;
        public static Dictionary<string, int> DeedCounts() => Deeds.GroupBy(d => d.kind).ToDictionary(g => g.Key, g => g.Count());

        // ---- fondazione: si può qui? (e a che prezzo)
        // Settori liberi: spazio aperto o fenomeni naturali (nebulose, campi di asteroidi, cimiteri di navi...).
        static readonly ESectorVisualIndication[] NaturalKinds = { ESectorVisualIndication.NONE, ESectorVisualIndication.NEBULA, ESectorVisualIndication.GREEN_NEBULA, ESectorVisualIndication.ASTEROID_FIELD, ESectorVisualIndication.MOLTEN_ASTEROID, ESectorVisualIndication.GRAVEYARD, ESectorVisualIndication.SPACE_JELLIES, ESectorVisualIndication.PURPLE_STORM_01 };
        static bool Natural(ESectorVisualIndication v) => Array.IndexOf(NaturalKinds, v) >= 0;
        static ESectorVisualIndication Vis(int sector) { try { return PLServer.GetSectorWithID(sector).VisualIndication; } catch { return ESectorVisualIndication.NONE; } }
        static bool Asteroids(int sector) => Vis(sector) is ESectorVisualIndication.ASTEROID_FIELD or ESectorVisualIndication.MOLTEN_ASTEROID;
        static bool Nebula(int sector) => Vis(sector) is ESectorVisualIndication.NEBULA or ESectorVisualIndication.GREEN_NEBULA or ESectorVisualIndication.PURPLE_STORM_01;
        public static (bool ok, string why, int cost) CanFound(string kind)
        {
            if (!Deeds.Any(d => d.kind == kind)) return (false, "non abbiamo un atto di questo tipo", 0);
            if (All.Count >= MaxBases) return (false, $"abbiamo già {MaxBases} basi", 0);
            PLSectorInfo s = null; try { s = PLServer.GetCurrentSector(); } catch { }
            bool warp = false; try { warp = Stations.MyShip.InWarp; } catch { }
            if (s == null || warp) return (false, "dobbiamo essere fermi in un settore", 0);
            if (!Natural(s.VisualIndication)) return (false, "qui c'è già un porto o un luogo speciale", 0);
            if (s.MissionSpecificID != -1) return (false, "è il settore di una missione", 0);
            if (At(s.ID) != null) return (false, "qui c'è già una nostra base", 0);
            if (Saga.State.lairs.Any(l => l.sector == s.ID)) return (false, "è il settore di un covo pirata", 0);
            if (Crises.Cur != null && Crises.Cur.sector == s.ID) return (false, "c'è una crisi in corso proprio qui", 0);
            if (Saga.State.threat != null && Saga.State.threat.heart == s.ID) return (false, "è il cuore della grande minaccia", 0);
            if (Wonders.Sector == s.ID) return (false, "questa stella sta per esplodere, o ci passa una cometa", 0);
            int f = s.MySPI?.Faction ?? -1;
            if (f == 4) return (false, "è spazio infetto", 0);
            int cost = BuildCost;
            if (f >= 0 && Array.IndexOf(Diplomacy.F, f) >= 0)
            {
                int lvl = Factions.Level(f);
                if (lvl <= -2) return (false, $"la {GalaxyWar.Name(f)} non ci concede di costruire nel suo spazio", 0);
                if (lvl <= 0) cost += 2000; // licenza
            }
            int have = 0; try { have = PLServer.Instance.CurrentCrewCredits; } catch { }
            if (have < cost) return (false, $"servono {cost} crediti per materiali e licenze", cost);
            return (true, "", cost);
        }

        public static string Found(string kind)
        {
            var (ok, why, cost) = CanFound(kind); if (!ok) { Stations.Announce("BASI", "Non possiamo fondare qui: " + why + "."); return why; }
            var s = PLServer.GetCurrentSector(); try { PLServer.Instance.CurrentCrewCredits -= cost; } catch { return "crediti"; }
            var d = Deeds.First(x => x.kind == kind); Deeds.Remove(d);
            var b = new Base { kind = kind, sector = s.ID, name = $"{K(kind).name} {Places.Name(s.ID)}", state = 0, buildUntil = Jumps() + BuildJumps };
            All.Add(b); Saga.State.basesFounded++; Feats.BaseFounded(All.Count); Saga.MarkDirty(); GalaxyView.Dirty();
            int f = s.MySPI?.Faction ?? -1; if (f >= 0 && Array.IndexOf(Diplomacy.F, f) >= 0 && cost > BuildCost) Factions.Change(f, 2, null);
            Director.Notify("PRIMA PIETRA", $"Posiamo la prima pietra: {b.name}, nel settore {Places.Of(s.ID)}. Il cantiere durerà {BuildJumps} salti. Spesa: {cost} crediti.");
            News.Post("flotta", $"La «{Ship()}» fonda {b.name} nel settore {Places.Of(s.ID)}.");
            Saga.Chronicle($"Fondata {b.name}."); Calling.Add("coloni", 3); Service.Add("basi.fondate", 1);
            if (Saga.State.basesFounded == 1) Trophies.Add("La prima pietra", "base");
            return b.name;
        }

        // ---- crescita (solo sul posto)
        public static int UpgradeCost(Base b) => b.level >= 3 ? -1 : 4000 * b.level;
        public static int DefenseCost(Base b) => b.defense >= 3 ? -1 : 3000 * (b.defense + 1);
        public static string Upgrade(bool defense)
        {
            var b = Here(); if (b == null) { Stations.Announce("BASI", "Bisogna essere alla base per migliorarla."); return "lontano"; }
            if (b.state != 1) { Stations.Announce("BASI", "La base è ancora in costruzione."); return "cantiere"; }
            int cost = defense ? DefenseCost(b) : UpgradeCost(b); if (cost < 0) { Stations.Announce("BASI", "Già al massimo."); return "massimo"; }
            int have = 0; try { have = PLServer.Instance.CurrentCrewCredits; } catch { }
            if (have < cost) { Stations.Announce("BASI", $"Servono {cost} crediti."); return "crediti"; }
            try { PLServer.Instance.CurrentCrewCredits = have - cost; } catch { return "crediti"; }
            if (defense) b.defense++; else { b.level++; Feats.BaseLevel(b.level); }
            Saga.MarkDirty(); GalaxyView.Dirty(); Calling.Add("coloni");
            Director.Notify("BASI", defense ? $"{b.name}: difese al livello {b.defense}. Torrette, scudi e una guarnigione più numerosa." : $"{b.name} cresce al livello {b.level}: rende di più, e ne parla tutta la regione.");
            if (!defense && b.level == 3) { Trophies.Add($"{b.name}, al massimo splendore", "base"); Legacy.Record("base", $"ha fondato {b.name} e l'ha resa grande"); }
            return "ok";
        }

        // ---- rendite e protezioni (usate dagli altri sistemi)
        static int Income(Base b)
        {
            if (b.state != 1 || b.supply <= 0) return 0;
            int seed = 0; try { seed = (int)PLServer.Instance.GalaxySeed; } catch { }
            double v = b.kind switch
            {
                "miniera" => 220 * b.level * ((Asteroids(b.sector) || Environments.EnvOf(seed, b.sector)?.kind == "asteroidi" || Exploration.PoiOf(seed, b.sector)?.kind == "giacimento") ? 2 : 1),
                "emporio" => 160 * b.level * (Factions.Level(OwnerOf(b.sector)) >= 1 ? 1.5 : 1) + Fleets.At(b.sector).Count * 120,
                _ => 0,
            };
            if (b.supply < 30) v *= 0.5;
            return (int)(v * Calling.Bonus("coloni"));
        }
        static int OwnerOf(int sector) { try { return PLServer.GetSectorWithID(sector)?.MySPI?.Faction ?? -1; } catch { return -1; } }
        public static float Protection() { var f = Near("fortezza", 1).ToList(); return f.Count == 0 ? 1f : Math.Max(0.3f, 1f - 0.2f * f.Max(b => b.level)); }
        public static float ResearchBonus() => 1f + 0.15f * Near("laboratorio", 6).Sum(b => b.level * (Nebula(b.sector) ? 2 : 1)); // in una nebulosa il laboratorio rende il doppio
        public static Base OwnShipyardHere() { var b = Here(); return b != null && b.kind == "cantiere" && b.state == 1 ? b : null; }

        // ---- a ogni salto: costruzione, scorte, rendite, manutenzione, assalti
        static int lastJumps = -1, lastSector = -1, report;
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                int j = Jumps(), sec = CurSector(); bool warp = false; try { warp = Stations.MyShip.InWarp; } catch { }
                if (sec != lastSector && !warp) { lastSector = sec; var b = At(sec); if (b != null) Arrive(b); }
                Defend(sec, warp);
                if (lastJumps < 0) { lastJumps = j; return; }
                if (j == lastJumps) return;
                lastJumps = j; int income = 0, upkeep = 0;
                foreach (var b in All.ToList())
                {
                    if (b.state == 0 && j >= b.buildUntil) { b.state = 1; b.supply = 100; Director.Notify("BASI", $"{b.name} è operativa."); Saga.Chronicle($"{b.name} è operativa."); }
                    if (b.state != 1) continue;
                    if (b.kind == "laboratorio") Threat.Add(-0.3f * b.level);
                    if (b.sector != sec) b.supply = Math.Max(0, b.supply - 4); // la base dove siamo è appena stata rifornita
                    int inc = Income(b); income += inc; b.earned += inc;
                    upkeep += 80 * b.level + 40 * b.defense;
                    // assalti
                    if (b.raidUntil < 0) { if (rng.NextDouble() < RaidChance(b)) StartRaid(b); }
                    else if (j > b.raidUntil && !b.spawned) ResolveRaid(b);
                }
                int net = income - upkeep;
                if (net != 0) try { PLServer.Instance.CurrentCrewCredits = Math.Max(0, PLServer.Instance.CurrentCrewCredits + net); } catch { }
                if (All.Any(b => b.state == 1) && ++report % 5 == 0) Stations.LogEvent(Stations.MyShip.ShipID, $"Rendiconto delle basi: rendite +{income}, manutenzione -{upkeep} crediti");
                Saga.MarkDirty(); GalaxyView.Dirty();
            }
            catch (Exception e) { Plugin.L.LogWarning("Basi: " + e.Message); }
        }

        static void Arrive(Base b)
        {
            if (b.state == 1) { b.supply = 100; Morale.Change(3, null); Logistics.Refill(); }
            if (b.kind == "cantiere" && b.state == 1) { try { var st = Stations.MyShip.MyStats; st.HullCurrent = st.HullMax; } catch { } foreach (var sy in Stations.SysIds) Stations.Set(Stations.MyShip.ShipID, "dmx_" + sy, 0.01f); }
            if (b.kind == "fortezza" && b.state == 1) Wanted.Clear(5 * b.level);
            Director.Notify("BASI", b.state == 0 ? $"Siamo al cantiere di {b.name}: i lavori finiranno al salto {b.buildUntil}." : $"Siamo a {b.name}. Scorte ripristinate, l'equipaggio scende a sgranchirsi le gambe." + (b.kind == "cantiere" ? " Il nostro cantiere rimette a nuovo la nave." : ""));
            if (b.state == 1 && rng.NextDouble() < 0.5) Encounters.StartOnboard("base_visita");
            Saga.MarkDirty(); GalaxyView.Dirty();
        }

        // ---- assalti alle basi
        static double RaidChance(Base b)
        {
            double c = 0.02;
            c += 0.03 * Saga.State.lairs.Count(l => Nemesis.Distance(PLServer.GetSectorWithID(l.sector), PLServer.GetSectorWithID(b.sector)) <= 4);
            int f = OwnerOf(b.sector); if (f >= 0 && Array.IndexOf(Diplomacy.F, f) >= 0 && Factions.Level(f) <= -2) c += 0.05;
            if (Saga.State.threat != null && Saga.State.threat.stage >= 3) c += 0.03;
            if (b.supply <= 0) c *= 2;
            if (All.Any(x => x.kind == "fortezza" && x.state == 1 && x != b && Nemesis.Distance(PLServer.GetSectorWithID(x.sector), PLServer.GetSectorWithID(b.sector)) <= 3)) c *= 0.6;
            return c * Rules.Heat;
        }
        static void StartRaid(Base b)
        {
            b.raidUntil = Jumps() + 5; b.spawned = false; Saga.MarkDirty();
            string who = Saga.State.threat != null && Saga.State.threat.stage >= 3 && rng.NextDouble() < 0.4 ? "navi infette" : "predoni";
            Director.Notify("BASE SOTTO ATTACCO", $"{b.name} chiede aiuto: {who} in vista. Abbiamo cinque salti. La base può resistere da sola solo con buone difese (difese {b.defense}/3).");
            News.Post("flotta", $"{b.name}, base della «{Ship()}», è sotto attacco.");
        }
        static readonly List<int> foes = new(); static int foeSector = -1;
        static void Defend(int sec, bool warp)
        {
            var b = All.FirstOrDefault(x => x.raidUntil >= 0 && x.sector == sec); if (b == null) { if (foes.Count > 0 && (sec != foeSector || warp)) { foes.Clear(); foreach (var x in All.Where(x => x.spawned)) x.spawned = false; } return; }
            if (warp) return;
            if (!b.spawned)
            {
                var ship = Stations.MyShip; float p = Director.Power(ship, out _, out _); foes.Clear(); foeSector = sec;
                int n = Math.Max(1, 2 + b.level - b.defense / 2);
                for (int i = 0; i < n; i++) { var s = Director.Spawn(ship, Director.PickShip(p, i == 0 ? 0 : -1), Director.Modifiers(p), "Assalitore"); if (s != null) foes.Add(s.ShipID); }
                if (foes.Count == 0) return; b.spawned = true;
                Director.Notify("BASE SOTTO ATTACCO", $"Arriviamo a {b.name}: {foes.Count} navi stanno attaccando la base. Le difese della base si preparano a combattere con noi.");
                return;
            }
            foes.RemoveAll(id => { PLShipInfoBase s = null; try { s = PLEncounterManager.Instance.GetShipFromID(id); } catch { } return s == null || s.HasBeenDestroyed || (s.MyStats != null && s.MyStats.HullCurrent <= 0f); });
            if (foes.Count > 0) return;
            b.raidUntil = -1; b.spawned = false; Saga.MarkDirty(); GalaxyView.Dirty();
            Morale.Change(5, null); Saga.State.onore += 1; Competitors.Renown(20); var got = Loot.Grant(Stations.MyShip, 2, 4, "difesa della base");
            Director.Notify("BASI", $"{b.name} è salva." + (got != null ? $" I coloni ci regalano {got.Name}." : ""));
            Saga.Chronicle($"Difesa di {b.name}.");
        }
        static void ResolveRaid(Base b)
        {
            b.raidUntil = -1;
            double hold = 0.3 + 0.18 * b.defense + (All.Any(x => x.kind == "fortezza" && x.state == 1 && x != b) ? 0.1 : 0);
            if (rng.NextDouble() < hold) { Director.Notify("BASI", $"{b.name} ha respinto l'assalto da sola: le difese hanno retto."); return; }
            if (b.level <= 1)
            {
                All.Remove(b); Morale.Change(-6, null);
                Director.Notify("BASE PERDUTA", $"{b.name} è caduta. Non resta che un campo di rottami.");
                News.Post("flotta", $"{b.name} è stata distrutta in assenza della sua nave."); Saga.Chronicle($"{b.name} è caduta.");
            }
            else { b.level--; b.supply = Math.Max(0, b.supply - 40); Morale.Change(-3, null); Director.Notify("BASI", $"{b.name} è stata saccheggiata: torna al livello {b.level}."); }
            Saga.MarkDirty(); GalaxyView.Dirty();
        }

        // Una supernova o la caduta di un settore possono distruggere una base.
        public static void Destroy(int sector, string why)
        {
            var b = At(sector); if (b == null) return; All.Remove(b); Saga.MarkDirty(); GalaxyView.Dirty();
            Director.Notify("BASE PERDUTA", $"{b.name} è perduta: {why}."); Saga.Chronicle($"{b.name} è perduta: {why}.");
        }

        // Rifornimento consegnato con un contratto.
        public static void Supply(int sector) { var b = At(sector); if (b == null) return; b.supply = 100; b.level = Math.Min(3, b.level); Saga.MarkDirty(); GalaxyView.Dirty(); Director.Notify("BASI", $"Rifornimenti consegnati a {b.name}: le scorte sono al massimo."); }
        public static Base NeedsSupply() => All.Where(b => b.state == 1 && b.supply < 40).OrderBy(b => b.supply).FirstOrDefault();

        // ---- per il pannello
        public class View { public string kind = "", name = "", line = ""; public int sector, jumps, state, level, defense, supply; public bool here, raid; }
        static string Net(Base b) { int n = Income(b) - (80 * b.level + 40 * b.defense); return (n >= 0 ? "+" : "") + n + " cr/salto"; }
        public static List<View> Views() => All.Select(b => new View
        {
            kind = b.kind, name = b.name, sector = b.sector, jumps = Dist(b.sector), state = b.state, level = b.level, defense = b.defense, supply = b.supply, here = b.sector == CurSector(), raid = b.raidUntil >= 0,
            line = b.state == 0 ? $"in costruzione fino al salto {b.buildUntil}" : $"liv. {b.level} · difese {b.defense} · scorte {b.supply}% · " + Net(b) + (b.kind == "laboratorio" ? " · ricerca" : b.kind == "fortezza" ? " · protezione" : b.kind == "faro" ? " · rapporti" : b.kind == "cantiere" ? " · riparazioni" : ""),
        }).ToList();

        // ---- richieste dal pannello (anche da un client)
        public static void Request(string op, string arg = "")
        {
            if (Host) { Execute(op, arg); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|basi|{op}|{arg}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var p = msg.Split('|'); if (p.Length >= 4 && Host) Execute(p[2], p[3]); }
        static void Execute(string op, string arg)
        {
            switch (op)
            {
                case "fonda": Found(arg); break;
                case "migliora": Upgrade(false); break;
                case "difese": Upgrade(true); break;
                case "rotta": if (int.TryParse(arg, out var s)) { Course.Plot(s); Director.Notify("ROTTA", $"Rotta tracciata verso il settore {Places.Of(s)}."); } break;
            }
        }
        public class Panel { public Dictionary<string, int> deeds = new(); public List<View> bases = new(); public string hint = "", here = ""; public int upCost = -1, defCost = -1, credits; public Dictionary<string, string> canHere = new(); }
        public static Panel Snapshot()
        {
            var p = new Panel { deeds = DeedCounts(), bases = Views() };
            foreach (var k in Kinds) { var (ok, why, cost) = CanFound(k.id); p.canHere[k.id] = ok ? $"si può fondare qui: {cost} crediti" : why; }
            var h = Here(); if (h != null) { p.here = $"{h.name}: " + (h.state == 0 ? $"in costruzione fino al salto {h.buildUntil}" : $"livello {h.level}, difese {h.defense}, scorte {h.supply}%"); p.upCost = h.state == 1 ? UpgradeCost(h) : -1; p.defCost = h.state == 1 ? DefenseCost(h) : -1; try { p.credits = PLServer.Instance.CurrentCrewCredits; } catch { } }
            return p;
        }

        public static string Describe() => $"atti: {string.Join(", ", DeedCounts().Select(x => x.Key + " " + x.Value))}; basi: " + (All.Count == 0 ? "nessuna" : string.Join("; ", All.Select(b => $"{b.name} (settore {b.sector}) stato {b.state} liv {b.level} dif {b.defense} scorte {b.supply} rendita {Income(b)}{(b.raidUntil >= 0 ? " ASSALTO" : "")}")));
        public static string ForceRaid() { var b = All.FirstOrDefault(x => x.state == 1); if (b == null) return "nessuna base operativa"; StartRaid(b); return Describe(); }
        public static string ForceBuild() { foreach (var b in All) b.buildUntil = Jumps(); lastJumps = -1; return "cantieri conclusi al prossimo salto"; }
    }
}

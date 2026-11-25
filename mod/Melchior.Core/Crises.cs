// Crisi galattiche: eventi lunghi, in tre fasi scandite dai salti, che cambiano la galassia intorno all'equipaggio.
// - epidemia: gli Infetti dilagano da un focolaio; si contengono con i contratti di soccorso e bonificando il focolaio.
// - guerra: due fazioni confinanti entrano in guerra aperta; l'equipaggio può schierarsi e decidere chi vince.
// - anomalia: un segnale sconosciuto in un settore lontano; tutte le fazioni ci mandano spedizioni, chi arriva prima vince.
// - blocco: i predoni strangolano le rotte commerciali; la Gilda paga chi le riapre.
// Ogni crisi ha un esito: se l'equipaggio la risolve ci sono ricompense e riconoscenza, altrimenti la galassia ne porta i segni.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class CrisisState
    {
        public string kind = "", title = "";
        public int stage, next, progress, sector = -1, a = -1, b = -1, pa, pb, started;
        public bool spawned;
    }

    public static class Crises
    {
        static readonly System.Random rng = new();
        public static readonly Dictionary<string, (string name, string color)> Kinds = new()
        {
            ["epidemia"] = ("Epidemia", "#9BE35B"), ["guerra"] = ("Guerra aperta", "#FF8073"), ["anomalia"] = ("Corsa all'anomalia", "#5EE0D6"), ["blocco"] = ("Blocco delle rotte", "#FFD75E"),
        };
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        public static CrisisState Cur => Saga.State.crisis;
        static int Jumps() { try { return Obscured.Jumps(); } catch { return 0; } }
        static int CurSector() { try { return PLServer.GetCurrentSector()?.ID ?? -1; } catch { return -1; } }
        static float Power() => Stations.MyShip == null ? 1f : Director.Power(Stations.MyShip, out _, out _);
        static long nextTick; static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static readonly List<int> guards = new(); static int guardSector = -1;

        // prossima crisi: memorizzata come salto (nello stato della cronaca, voce "crises" con "next:<salto>")
        static int NextAt
        {
            get { var e = Saga.State.crises.FirstOrDefault(x => x.StartsWith("next:")); return e != null && int.TryParse(e[5..], out int v) ? v : -1; }
            set { Saga.State.crises.RemoveAll(x => x.StartsWith("next:")); Saga.State.crises.Add("next:" + value); Saga.MarkDirty(); }
        }

        public static void Tick()
        {
            long now = clock.ElapsedMilliseconds; if (now < nextTick) return; nextTick = now + 2000;
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                TrackGuards();
                var c = Cur; int j = Jumps();
                if (c == null)
                {
                    if (NextAt < 0) NextAt = j + rng.Next(4, 8);
                    if (j >= NextAt) Start(null);
                    return;
                }
                // arrivo nel settore della crisi
                if (c.sector >= 0 && CurSector() == c.sector && !c.spawned && !InWarp()) Arrive(c);
                if (j >= c.next)
                {
                    if (c.stage >= 2) { Resolve(false); return; }
                    c.stage++; c.next = j + rng.Next(4, 7); Saga.MarkDirty();
                    Stage(c);
                }
                // richieste legate alla crisi sulla bacheca
                if (!Saga.State.board.Any(o => o.crisis == c.kind) && rng.NextDouble() < 0.25) PostOffer(c);
            }
            catch (Exception e) { Plugin.L.LogWarning("Crisi: " + e.Message); nextTick = now + 15000; }
        }
        static bool InWarp() { try { return Stations.MyShip.InWarp; } catch { return false; } }

        static List<PLSectorInfo> Open() { var l = new List<PLSectorInfo>(); try { foreach (var kv in PLGlobal.Instance.Galaxy.AllSectorInfos) { var s = kv.Value; if (s?.MySPI != null && s.VisualIndication == ESectorVisualIndication.NONE && s.MissionSpecificID == -1 && !s.LockedToFaction) l.Add(s); } } catch { } return l; }

        public static string Start(string kind)
        {
            if (Cur != null) return "c'è già una crisi in corso";
            var kinds = Kinds.Keys.Where(k => kind == null ? !Saga.State.crises.Contains("last:" + k) : k == kind).ToList(); if (kinds.Count == 0) kinds = Kinds.Keys.ToList();
            kind = kinds[rng.Next(kinds.Count)];
            var c = new CrisisState { kind = kind, started = Jumps(), next = Jumps() + rng.Next(4, 7) };
            var open = Open(); int cur = CurSector(); var here = PLServer.GetCurrentSector();
            switch (kind)
            {
                case "epidemia":
                {
                    // focolaio: un settore aperto che confina con gli Infetti, a qualche salto
                    var cand = open.Where(s => s.MySPI.Faction != 4 && s.ID != cur && s.Neighbors != null && s.Neighbors.ToArray().Any(n => n?.MySPI != null && n.MySPI.Faction == 4)).ToList();
                    if (cand.Count == 0) cand = open.Where(s => s.ID != cur).ToList(); if (cand.Count == 0) return "nessun settore adatto";
                    var near = cand.Where(s => here == null || Nemesis.Distance(here, s) is >= 2 and <= 6).ToList(); if (near.Count > 0) cand = near;
                    c.sector = cand[rng.Next(cand.Count)].ID; c.a = 4; c.title = $"Epidemia nel settore {c.sector}";
                    break;
                }
                case "guerra":
                {
                    // le due fazioni (non infette) con il fronte comune più lungo
                    var pairs = new Dictionary<(int, int), int>();
                    foreach (var s in open) { int f = s.MySPI.Faction; if (Array.IndexOf(GalaxyWar.Factions, f) < 0 || f == 4) continue; var nb = s.Neighbors; if (nb == null) continue; for (int i = 0; i < nb.Count; i++) { int g = nb[i]?.MySPI?.Faction ?? -1; if (g != f && g >= 0 && g != 4 && Array.IndexOf(GalaxyWar.Factions, g) >= 0) { var k = f < g ? (f, g) : (g, f); pairs[k] = pairs.GetValueOrDefault(k) + 1; } } }
                    if (pairs.Count == 0) return "nessun fronte fra due fazioni";
                    var top = pairs.OrderByDescending(x => x.Value).Take(2).ToList(); var p = top[rng.Next(top.Count)].Key;
                    c.a = p.Item1; c.b = p.Item2; c.title = $"Guerra aperta: {GalaxyWar.Name(c.a)} contro {GalaxyWar.Name(c.b)}";
                    break;
                }
                case "anomalia":
                {
                    var cand = open.Where(s => s.ID != cur && (here == null || Nemesis.Distance(here, s) is >= 3 and <= 6)).ToList(); if (cand.Count == 0) cand = open.Where(s => s.ID != cur).ToList(); if (cand.Count == 0) return "nessun settore adatto";
                    c.sector = cand[rng.Next(cand.Count)].ID; c.title = $"Segnale sconosciuto dal settore {c.sector}";
                    break;
                }
                default: c.title = "Blocco delle rotte commerciali"; break;
            }
            Saga.State.crisis = c; Saga.MarkDirty();
            Stage(c);
            Saga.Chronicle("Inizia una crisi: " + c.title + ".");
            Service.Add("crisi.iniziate", 1);
            Plugin.L.LogInfo($"[crisi] {c.kind}: {c.title}");
            return c.title;
        }

        // Testo di ogni fase, per notiziario e annuncio.
        static void Stage(CrisisState c)
        {
            string A = GalaxyWar.Name(c.a), B = GalaxyWar.Name(c.b);
            string[] t = c.kind switch
            {
                "epidemia" => new[]
                {
                    $"Primi casi di infezione nel settore {c.sector}: le colonie chiedono medici e scorte. Il Comando raccomanda di bonificare il focolaio.",
                    $"L'infezione si allarga dal settore {c.sector}. Gli Infetti avanzano sul fronte e le colonie vicine chiedono di essere evacuate.",
                    $"Ultimo appello: se il focolaio del settore {c.sector} non viene contenuto, l'infezione diventerà endemica.",
                },
                "guerra" => new[]
                {
                    $"{A} e {B} rompono la tregua: incidenti armati lungo tutto il confine. Entrambe cercano equipaggi a contratto.",
                    $"La guerra fra {A} e {B} si estende. Chi aiuta una delle due se ne guadagna la riconoscenza, e l'ostilità dell'altra.",
                    $"Ultima fase della guerra fra {A} e {B}: le flotte si preparano allo scontro decisivo.",
                },
                "anomalia" => new[]
                {
                    $"Dal settore {c.sector} arriva un segnale che nessuno sa decifrare. Le fazioni preparano spedizioni: chi arriva prima se lo prende.",
                    $"Le prime spedizioni sono in viaggio verso il settore {c.sector}. Il segnale si fa più forte.",
                    $"Una flotta di fazione è a un salto dal settore {c.sector}. È l'ultima occasione per arrivare prima.",
                },
                _ => new[]
                {
                    "I predoni hanno chiuso le rotte commerciali: i convogli restano nei porti. La Gilda dei Mercanti paga bene chi riapre le rotte.",
                    "Il blocco si stringe: scarseggiano carburante e pezzi di ricambio. La Gilda alza i compensi.",
                    "Ultimo avviso della Gilda: se il blocco non si spezza, molte colonie resteranno senza rifornimenti.",
                },
            };
            string text = t[Math.Clamp(c.stage, 0, 2)];
            News.Post(c.kind == "guerra" ? "fronte" : c.kind == "anomalia" ? "scienza" : c.kind == "blocco" ? "commercio" : "flotta", text);
            Director.Notify("CRISI · " + Kinds[c.kind].name.ToUpperInvariant() + $" · FASE {c.stage + 1}/3", text);
            if (c.sector >= 0) Contracts.Plot(c.sector);
        }

        // Effetti sulla guerra fra fazioni (chiamato a ogni passo di guerra).
        public static float WarBias(int faction)
        {
            var c = Cur; if (c == null) return 1f;
            if (c.kind == "epidemia" && faction == 4) return 1.6f + c.stage * 0.4f;
            if (c.kind == "guerra" && (faction == c.a || faction == c.b)) return 1.8f;
            return 1f;
        }
        public static int RaiderBonus => Cur?.kind == "blocco" ? 2 : 0;

        // Arrivo nel settore della crisi: focolaio da bonificare o guardiani dell'anomalia.
        static void Arrive(CrisisState c)
        {
            var ship = Stations.MyShip; float p = Power(); guards.Clear(); guardSector = CurSector();
            if (c.kind == "epidemia")
            {
                var types = new[] { EShipType.E_INFECTED_FIGHTER, EShipType.E_INFECTED_FIGHTER_HEAVY, EShipType.E_INFECTED_CARRIER };
                int n = 2 + c.stage + (p >= 10 ? 1 : 0);
                for (int i = 0; i < n; i++) { var s = Director.Spawn(ship, types[Math.Min(types.Length - 1, i == 0 ? (int)(p / 7) : rng.Next(2))], Director.Modifiers(p), "Focolaio " + (i + 1)); if (s != null) guards.Add(s.ShipID); }
                Director.Notify("FOCOLAIO", $"Il focolaio è qui: {guards.Count} navi infette proteggono la colonia. Distruggetele per contenere l'epidemia.");
            }
            else if (c.kind == "anomalia")
            {
                int f = GalaxyWar.Factions.Where(x => x != 4).ElementAt(rng.Next(4));
                int n = 1 + c.stage + (p >= 10 ? 1 : 0);
                for (int i = 0; i < n; i++) { var s = Director.Spawn(ship, Director.PickShip(p, i == 0 ? 0 : -1), Director.Modifiers(p), $"Spedizione {GalaxyWar.Name(f)}"); if (s != null) guards.Add(s.ShipID); }
                Director.Notify("ANOMALIA", c.stage == 0 ? "Siamo i primi! La Scienza registra la fonte del segnale... e una spedizione di fazione esce dalla curvatura dietro di noi." : $"La sorgente del segnale è qui, ma la {GalaxyWar.Name(f)} ci ha preceduti: le loro navi ci intimano di andarcene.");
            }
            else return;
            c.spawned = true; Saga.MarkDirty();
            if (guards.Count == 0) { c.spawned = false; return; }
        }

        static void TrackGuards()
        {
            if (guards.Count == 0) return;
            if (CurSector() != guardSector || InWarp()) { guards.Clear(); if (Cur != null) { Cur.spawned = false; Saga.MarkDirty(); } return; } // l'equipaggio si è ritirato: al prossimo arrivo ci riprova
            guards.RemoveAll(id => { PLShipInfoBase s = null; try { s = PLEncounterManager.Instance.GetShipFromID(id); } catch { } return s == null || s.HasBeenDestroyed || (s.MyStats != null && s.MyStats.HullCurrent <= 0f); });
            if (guards.Count == 0 && InWarp()) return;
            if (guards.Count == 0 && Cur != null)
            {
                if (Cur.kind == "anomalia")
                {
                    var got = Loot.Grant(Stations.MyShip, 5, 7, "anomalia");
                    Director.Notify("ANOMALIA", "La Scienza raggiunge la sorgente: un manufatto di origine sconosciuta." + (got != null ? $" Lo recuperiamo: {got.Name}." : ""));
                }
                Contribute(100, "settore bonificato");
            }
        }

        // ---- contributi dell'equipaggio
        public static void Contribute(int pts, string why, int side = -1)
        {
            var c = Cur; if (c == null) return;
            if (c.kind == "guerra") { if (side == c.a) c.pa += pts; else if (side == c.b) c.pb += pts; else return; }
            else c.progress += pts;
            Saga.MarkDirty();
            Plugin.L.LogInfo($"[crisi] contributo {pts} ({why}): {Progress(c)}");
            if (c.kind == "guerra" ? Math.Max(c.pa, c.pb) >= 100 : c.progress >= 100) Resolve(true);
        }

        public static void OnKill(int faction)
        {
            var c = Cur; if (c == null) return;
            if (c.kind == "epidemia" && faction == 4) Contribute(4, "nave infetta distrutta");
            if (c.kind == "guerra" && (faction == c.a || faction == c.b)) Contribute(6, "nave nemica distrutta", faction == c.a ? c.b : c.a);
        }
        public static void OnRaidersDown() { if (Cur?.kind == "blocco") Contribute(40, "predoni dispersi"); }

        public static void OnContract(ContractOffer o, bool success)
        {
            var c = Cur; if (c == null || o.crisis != c.kind || !success) return;
            if (c.kind == "guerra") Contribute(40, "contratto di guerra", o.side); else Contribute(35, "contratto della crisi");
        }

        // Le richieste della crisi sulla bacheca.
        static void PostOffer(CrisisState c)
        {
            int[] fam; string issuer; int side = -1;
            switch (c.kind)
            {
                case "epidemia": fam = new[] { 1, 7, 8 }; issuer = rng.Next(2) == 0 ? "Comando di Flotta" : GalaxyWar.Name(0); break;
                case "guerra": fam = new[] { 0, 8, 3 }; side = rng.Next(2) == 0 ? c.a : c.b; issuer = GalaxyWar.Name(side); break;
                case "blocco": fam = new[] { 3, 0 }; issuer = "Gilda dei Mercanti"; break;
                default: return;
            }
            Contracts.PostCrisis(Power(), fam[rng.Next(fam.Length)], issuer, c.kind, side);
        }

        static void Resolve(bool won)
        {
            var c = Cur; if (c == null) return;
            string A = GalaxyWar.Name(c.a), B = GalaxyWar.Name(c.b), text;
            var ship = Stations.MyShip;
            switch (c.kind)
            {
                case "epidemia":
                    if (won) { GalaxyWar.Shift(4, -40); foreach (var f in GalaxyWar.Factions) if (f != 4) Factions.Change(f, 6, null); text = $"L'epidemia del settore {c.sector} è contenuta. Le colonie ringraziano l'equipaggio della «{Ship()}»."; }
                    else { int n = GalaxyWar.Spread(4, c.sector, 3); text = $"L'epidemia non è stata fermata: gli Infetti si prendono {n} settori intorno al focolaio."; }
                    break;
                case "guerra":
                {
                    int winner = c.pa == c.pb ? (GalaxyWar.Mom(c.a) >= GalaxyWar.Mom(c.b) ? c.a : c.b) : c.pa > c.pb ? c.a : c.b, loser = winner == c.a ? c.b : c.a;
                    int n = GalaxyWar.Spread(winner, -1, 3, loser);
                    bool helped = Math.Max(c.pa, c.pb) > 0; won = helped;
                    if (helped) { int side = c.pa >= c.pb ? c.a : c.b, other = side == c.a ? c.b : c.a; Factions.Change(side, 15, $"Avete combattuto per la {GalaxyWar.Name(side)}"); Factions.Change(other, -15, $"Avete combattuto contro la {GalaxyWar.Name(other)}"); }
                    text = $"La guerra finisce: vince la {GalaxyWar.Name(winner)}, che strappa {n} settori alla {GalaxyWar.Name(loser)}." + (helped ? $" Il contributo della «{Ship()}» non è passato inosservato." : "");
                    break;
                }
                case "anomalia":
                    if (won) text = $"Il segreto del settore {c.sector} è nelle mani della «{Ship()}». Nei porti non si parla d'altro.";
                    else { int f = GalaxyWar.Factions.Where(x => x != 4).ElementAt(rng.Next(4)); GalaxyWar.Shift(f, 15); text = $"La {GalaxyWar.Name(f)} arriva per prima al settore {c.sector} e si prende la sorgente del segnale. Le sue flotte ne escono rafforzate."; }
                    break;
                default:
                    if (won) { Saga.State.trust["Gilda dei Mercanti"] = Contracts.Trust("Gilda dei Mercanti") + 3; text = "Il blocco è spezzato: i convogli tornano a viaggiare. La Gilda dei Mercanti non dimentica chi l'ha aiutata."; }
                    else text = "Il blocco tiene: le colonie di confine restano senza rifornimenti per settimane. I prezzi salgono ovunque.";
                    break;
            }
            if (won && c.kind != "anomalia") { var got = Loot.Grant(ship, 4, 6, "crisi"); if (got != null) Director.Notify("RICOMPENSA", "Per il vostro ruolo nella crisi: " + got.Name + "."); }
            if (won) Saga.State.onore += 2;
            News.Post(c.kind == "guerra" ? "fronte" : "flotta", text);
            Director.Notify(won ? "CRISI RISOLTA" : "CRISI CONCLUSA", text);
            Saga.Chronicle($"{c.title}: {(won ? "risolta" : "conclusa senza di noi")}.");
            Service.Add(won ? "crisi.risolte" : "crisi.perse", 1);
            Saga.State.crises.RemoveAll(x => x.StartsWith("last:")); Saga.State.crises.Add("last:" + c.kind);
            Saga.State.crises.Add($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}|{c.kind}|{(won ? 1 : 0)}|{c.title}");
            while (Saga.State.crises.Count(x => x.Contains('|')) > 12) Saga.State.crises.Remove(Saga.State.crises.First(x => x.Contains('|')));
            Saga.State.board.RemoveAll(o => o.crisis == c.kind);
            Saga.State.crisis = null; guards.Clear();
            NextAt = Jumps() + rng.Next(8, 14);
            Saga.MarkDirty();
        }
        static string Ship() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }

        // Durante l'assenza: la crisi in corso va avanti di una fase ogni due ore circa.
        public static void Offline(double hours)
        {
            if (!Host || Cur == null || hours < 2) return;
            int steps = Math.Min(3, (int)(hours / 2));
            for (int i = 0; i < steps && Cur != null; i++) { if (Cur.stage >= 2) Resolve(false); else { Cur.stage++; Cur.next = Jumps() + rng.Next(4, 7); Stage(Cur); } }
        }

        public static string Progress(CrisisState c) => c.kind == "guerra" ? $"{GalaxyWar.Name(c.a)} {Math.Min(100, c.pa)}% · {GalaxyWar.Name(c.b)} {Math.Min(100, c.pb)}%" : $"{Math.Min(100, c.progress)}%";
        public static string Hint(CrisisState c) => c.kind switch
        {
            "epidemia" => $"Bonificare il focolaio nel settore {c.sector}, distruggere navi infette, contratti di soccorso.",
            "guerra" => "Schierarsi: contratti per una delle due fazioni, navi dell'altra abbattute.",
            "anomalia" => $"Raggiungere il settore {c.sector} prima delle fazioni e tenere il campo.",
            _ => "Contratti della Gilda, flotte di predoni disperse.",
        };

        public static string Describe()
        {
            var c = Cur; int na = NextAt;
            if (c == null) return $"nessuna crisi; la prossima al salto {na} (ora {Jumps()})";
            return $"{c.kind}: {c.title}; fase {c.stage + 1}/3, prossima al salto {c.next} (ora {Jumps()}); avanzamento {Progress(c)}; settore {c.sector}; guardiani {guards.Count}";
        }
        public static string ForceStage() { var c = Cur; if (c == null) return "nessuna crisi"; c.next = Jumps(); nextTick = 0; return "la fase avanzerà subito"; }
        public static string ForceEnd(bool won) { if (Cur == null) return "nessuna crisi"; if (won) Contribute(200, "console", Cur.kind == "guerra" ? Cur.a : -1); else Resolve(false); return "crisi chiusa"; }
    }
}

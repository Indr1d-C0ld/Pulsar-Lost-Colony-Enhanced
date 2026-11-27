// La Grande Minaccia: un arco lungo per ogni galassia. Una minaccia con un nome cresce piano col tempo giocato e con le
// vittorie degli Infetti, e attraversa quattro fasi di segnali sempre più gravi (dicerie, settori oscurati, Infetti più
// aggressivi). All'ultima si rivela il suo cuore: rotta tracciata e un'ammiraglia da abbattere. L'equipaggio la frena
// risolvendo le epidemie, distruggendo navi infette, con la ricerca della Scienza e con le scoperte. Se raggiunge il
// culmine senza essere fermata, la catastrofe lascia il segno e la minaccia rinasce, più debole, da capo.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class ThreatState { public string name = ""; public float level; public int stage, heart = -1, defeated, catastrophes; public bool spawned; }

    public static class Threat
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static readonly string[] Names = { "la Marea Grigia", "il Divoratore di Stelle", "il Coro del Vuoto", "l'Ombra di Teth", "la Fame Antica", "il Sonno che Cammina" };
        public static ThreatState T { get { var s = Saga.State; if (s.threat == null || s.threat.name.Length == 0) { s.threat = new ThreatState { name = Names[rng.Next(Names.Length)], level = 5 }; Saga.MarkDirty(); } return s.threat; } }
        static string Cap(string s) => char.ToUpper(s[0]) + s[1..];
        static string Ship() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }
        static double lastPlayed = -1;

        public static void Add(float d, string why = null)
        {
            if (!Host || !Saga.Loaded) return;
            var t = T; t.level = Math.Clamp(t.level + d, 0f, 100f); Saga.MarkDirty(); GalaxyView.Dirty();
            if (d <= -5 && why != null) Stations.LogEvent(Stations.MyShip?.ShipID ?? 0, $"{Cap(t.name)} arretra: {why}");
        }

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                var t = T; var st = Saga.State;
                // cresce di circa un punto ogni dieci minuti di gioco
                if (lastPlayed < 0) lastPlayed = st.playedSeconds;
                if (st.playedSeconds - lastPlayed >= 600) { lastPlayed = st.playedSeconds; Add(Rules.ThreatGrowth * (Research.Has("minaccia") ? 0.75f : 1f)); }
                int want = t.level >= 80 ? 4 : t.level >= 60 ? 3 : t.level >= 40 ? 2 : t.level >= 20 ? 1 : 0;
                if (want > t.stage) { t.stage = want; Stage(t); }
                else if (want < t.stage - 1) { t.stage = want; t.heart = -1; t.spawned = false; Course.Clear(); News.Post("scienza", $"{Cap(t.name)} perde forza: i segnali si affievoliscono."); Saga.MarkDirty(); }
                if (t.level >= 100) Catastrophe(t);
                Heart(t);
            }
            catch (Exception e) { Plugin.L.LogWarning("Minaccia: " + e.Message); }
        }

        static void Stage(ThreatState t)
        {
            string N = Cap(t.name), text;
            switch (t.stage)
            {
                case 1: text = $"Nei porti si parla di navi spente che tornano a muoversi, di colonie che smettono di rispondere. Qualcuno le chiama «{t.name}»."; News.Post("dicerie", text); break;
                case 2:
                {
                    int n = GalaxyWar.Spread(4, -1, 2);
                    text = $"{N} si allarga: {n} settori diventano bui, uno dopo l'altro. L'Accademia chiede campioni e dati a chiunque passi di là."; News.Post("scienza", text); break;
                }
                case 3:
                    GalaxyWar.Shift(4, 20);
                    text = $"{N} spinge gli Infetti oltre ogni fronte. Le fazioni parlano di tregua per fare argine; molte colonie evacuano."; News.Post("fronte", text);
                    foreach (var a in Diplomacy.F) foreach (var b in Diplomacy.F) if (a < b) Diplomacy.Change(a, b, 6, null);
                    break;
                default:
                {
                    var cur = PLServer.GetCurrentSector(); var cand = new List<PLSectorInfo>();
                    foreach (var kv in PLGlobal.Instance.Galaxy.AllSectorInfos) { var s = kv.Value; if (s?.MySPI != null && s.MissionSpecificID == -1 && s.VisualIndication == ESectorVisualIndication.NONE && (cur == null || s.ID != cur.ID)) cand.Add(s); }
                    var far = cand.Where(s => cur == null || Nemesis.Distance(cur, s) is >= 4 and <= 8).ToList(); if (far.Count == 0) far = cand;
                    if (far.Count > 0) { var h = far[rng.Next(far.Count)]; t.heart = h.ID; t.spawned = false; Contracts.Plot(h.ID); }
                    text = $"Abbiamo trovato il cuore {Of(t.name)}: settore {Places.Of(t.heart)}, a {Nemesis.JumpsTo(t.heart)} salti. Rotta tracciata. Se non lo fermiamo, la galassia non sarà più la stessa.";
                    News.Post("fronte", $"Il cuore {Of(t.name)} è stato localizzato. Si cerca un equipaggio abbastanza folle da andarci.");
                    break;
                }
            }
            Director.Notify("LA GRANDE MINACCIA · FASE " + t.stage + "/4", text);
            Saga.Chronicle($"{N}: fase {t.stage} di 4.");
            Saga.MarkDirty(); GalaxyView.Dirty();
        }
        static string Of(string n) => n.StartsWith("la ") ? "della " + n[3..] : n.StartsWith("il ") ? "del " + n[3..] : n.StartsWith("l'") ? "dell'" + n[2..] : "di " + n;

        // Il cuore: un'ammiraglia infetta e la sua scorta. Abbatterla è la vittoria.
        static readonly List<int> guards = new(); static int boss = -1, bossSector = -1;
        static void Heart(ThreatState t)
        {
            if (t.stage < 4 || t.heart < 0) return;
            var ship = Stations.MyShip; bool warp = false; try { warp = ship.InWarp; } catch { }
            int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { }
            if (!t.spawned && sec == t.heart && !warp)
            {
                float p = Director.Power(ship, out _, out _);
                // l'ammiraglia infetta originale non si lascia creare da qui: il cuore è una portaerei infetta d'élite, corazzata
                var b = Director.Spawn(ship, EShipType.E_INFECTED_CARRIER, Director.Modifiers(p + 8) | 1 << (int)EShipModifierType.ELITE | 1 << (int)EShipModifierType.ARMORED, "Cuore " + Of(t.name));
                if (b == null) return;
                boss = b.ShipID; bossSector = sec; guards.Clear(); guards.Add(b.ShipID); t.spawned = true; Saga.MarkDirty();
                for (int i = 0; i < 3 + (p >= 10 ? 1 : 0); i++) { var e = Director.Spawn(ship, i < 2 ? EShipType.E_INFECTED_FIGHTER_HEAVY : EShipType.E_INFECTED_FIGHTER, Director.Modifiers(p), "Spora"); if (e != null) guards.Add(e.ShipID); }
                Director.Notify("IL CUORE", $"Eccolo. {Cap(t.name)} ha un corpo, e ci ha visti. Tutto l'equipaggio ai posti di combattimento.");
                return;
            }
            if (!t.spawned || boss < 0) return;
            if (sec != bossSector || warp) { t.spawned = false; boss = -1; guards.Clear(); Saga.MarkDirty(); return; } // ritirata: il cuore aspetta
            PLShipInfoBase s = null; try { s = PLEncounterManager.Instance.GetShipFromID(boss); } catch { }
            bool dead = s == null || s.HasBeenDestroyed; try { if (!dead && s.MyStats != null && s.MyStats.HullCurrent <= 0f) dead = true; } catch { }
            if (!dead) return;
            Victory(t);
        }

        static void Victory(ThreatState t)
        {
            var ship = Stations.MyShip; string N = Cap(t.name);
            var got = Loot.Grant(ship, 7, 7, "grande minaccia");
            try { PLServer.Instance.CurrentCrewCredits += 10000; } catch { }
            Saga.State.onore += 8; Morale.Change(25, "Abbiamo salvato la galassia"); Competitors.Renown(200);
            GalaxyWar.Shift(4, -40);
            Director.Notify("VITTORIA", $"Abbiamo sconfitto {t.name}. Il cuore si spegne, e con lui il silenzio che stava inghiottendo la galassia. 10000 crediti di ricompense dalle fazioni" + (got != null ? $" e {got.Name}" : "") + ".");
            News.Post("fronte", $"La «{Ship()}» ha sconfitto {t.name}. In ogni porto si brinda all'equipaggio che ha salvato la galassia.");
            Saga.Chronicle($"Vittoria contro {t.name}: la galassia è salva.");
            Service.Add("minaccia.sconfitte", 1);
            t.defeated++; Course.Clear(); boss = -1; guards.Clear(); Bases.GiveDeed("fortezza", "dono riconoscente delle fazioni"); Trophies.Add($"Frammento del cuore {Of(t.name)}", "grande minaccia"); Legacy.Record("minaccia", $"ha sconfitto {t.name}");
            // tra molto tempo, qualcos'altro si sveglierà
            var used = new HashSet<string> { t.name };
            Saga.State.threat = new ThreatState { name = Names.Where(n => !used.Contains(n)).ElementAt(rng.Next(Names.Length - 1)), level = 0, defeated = t.defeated, catastrophes = t.catastrophes };
            Saga.MarkDirty(); GalaxyView.Dirty();
        }

        static void Catastrophe(ThreatState t)
        {
            string N = Cap(t.name);
            int n = GalaxyWar.Spread(4, t.heart, 6);
            foreach (var f in Diplomacy.F) GalaxyWar.Shift(f, -10);
            Morale.Change(-10, "La galassia brucia");
            Director.Notify("CATASTROFE", $"{N} ha raggiunto il culmine: {n} settori cadono in una notte. Le flotte delle fazioni sono in rotta.");
            News.Post("fronte", $"{N} ha inghiottito {n} settori. Nessuno è riuscito a fermarne l'avanzata.");
            Saga.Chronicle($"{N}: catastrofe, {n} settori perduti.");
            t.catastrophes++; t.level = 30; t.stage = 1; t.heart = -1; t.spawned = false; Course.Clear(); Saga.MarkDirty(); GalaxyView.Dirty();
        }

        public static string Summary() { var t = T; return $"{Cap(t.name)} {t.level:0}%" + (t.stage >= 4 && t.heart >= 0 ? $" · cuore nel settore {Places.Of(t.heart)}" : t.stage > 0 ? $" · fase {t.stage}/4" : ""); }
        public static string Describe() { var t = T; return $"{t.name}: livello {t.level:0.0}, fase {t.stage}, cuore {t.heart}, comparso {t.spawned}, sconfitte {t.defeated}, catastrofi {t.catastrophes}"; }
        public static string Set(float v) { T.level = v; Saga.MarkDirty(); return Describe(); }
        public static string HeartHere() { var t = T; t.stage = 4; t.level = Math.Max(t.level, 80); t.heart = PLServer.GetCurrentSector().ID; t.spawned = false; Saga.MarkDirty(); return Describe(); }
    }
}

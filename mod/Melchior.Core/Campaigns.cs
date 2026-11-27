// Campagne delle fazioni: chi conquista l'alleanza di una fazione viene convocato per una campagna in tre atti con la
// sua storia. Primo atto: una battaglia in un settore indicato (rotta tracciata). Secondo atto: un dilemma (incontro).
// Terzo atto: un ammiraglio nemico da abbattere oltre il confine. La vittoria vale un'onorificenza: titolo, trofeo,
// reputazione che non scende più sotto l'alleanza e le navi di quella fazione sempre amiche (reputazione nativa alta).
// Una campagna lasciata a metà per troppi salti viene affidata ad altri.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class CampaignState { public int f = -1, act, sector = -1, deadline; public bool spawned; }

    public static class Campaigns
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        public sealed class Story { public string title = "", brief = "", act1 = "", foe1 = "", dilemma = "", act3 = "", boss = "", honor = "", win = ""; }
        public static readonly Dictionary<int, Story> Stories = new()
        {
            [0] = new Story { title = "Operazione Bastione", brief = "Il Comando della Colonial Union vi convoca: le colonie di frontiera cadono una dopo l'altra e serve una nave che non abbia paura di stare in prima linea.", act1 = "difendere la colonia di frontiera", foe1 = "Incursori", dilemma = "Tra i prigionieri dell'ultima battaglia c'è un ufficiale nemico che offre la posizione del suo comandante in cambio della libertà. Il Comando vorrebbe processarlo.", act3 = "colpire il comandante degli incursori nella sua base", boss = "Comandante degli incursori", honor = "Stella del Bastione", win = "Le colonie di frontiera sono di nuovo al sicuro. Il Comando della Colonial Union vi conferisce la Stella del Bastione." },
            [1] = new Story { title = "Il Codice dei Gentiluomini", brief = "Un Commodoro dell'Alliance of Gentlemen vi riceve in alta uniforme: un cimelio di famiglia è stato rubato da una banda di rinnegati, e l'onore dell'Alliance esige che venga recuperato da mani esterne e discrete.", act1 = "intercettare i ladri del cimelio", foe1 = "Rinnegati", dilemma = "Il cimelio è stato ritrovato, ma il capo dei rinnegati è il figlio diseredato del Commodoro. Vuole solo parlargli. Il Commodoro non vuole vederlo.", act3 = "affrontare il protettore dei rinnegati, un ammiraglio corrotto", boss = "Ammiraglio corrotto", honor = "Croce dei Gentiluomini", win = "Il cimelio torna al suo posto e un nome torna pulito. L'Alliance of Gentlemen vi nomina Cavalieri della Croce dei Gentiluomini." },
            [2] = new Story { title = "Acquisizione Ostile", brief = "Un dirigente della W.D. Corporation vi propone un incarico riservato: un prototipo è stato sottratto da una società concorrente, e la W.D. paga molto bene la discrezione.", act1 = "colpire il convoglio che trasporta il prototipo", foe1 = "Sicurezza aziendale", dilemma = "Nei dati del prototipo c'è la prova che la W.D. ha sperimentato su una colonia ignara. Potete consegnarli, cancellarli o farli trapelare.", act3 = "eliminare la nave ammiraglia della concorrenza", boss = "Ammiraglia della concorrenza", honor = "Sigillo del Consiglio W.D.", win = "La concorrenza è fuori dal mercato. Il Consiglio della W.D. vi concede il suo Sigillo: le sue porte vi saranno sempre aperte." },
            [5] = new Story { title = "Il Progetto Aurora", brief = "La Polytechnic Federation sta per accendere Aurora, un esperimento che potrebbe dare energia a cento colonie. Qualcuno vuole impedirlo a ogni costo.", act1 = "proteggere la stazione di ricerca dai sabotatori", foe1 = "Sabotatori", dilemma = "I sabotatori catturati sostengono che Aurora, se accesa, distruggerà il sistema. I calcoli della Scienza sono ambigui.", act3 = "fermare la nave dei sabotatori prima dell'accensione", boss = "Nave dei sabotatori", honor = "Medaglia Aurora", win = "Aurora si accende e cento colonie hanno energia. La Polytechnic Federation vi consegna la Medaglia Aurora." },
        };

        static CampaignState C => Saga.State.campaign;
        static int Jumps() => Obscured.Jumps();
        public static bool Honored(int f) => Saga.State.honors.Contains(f);
        public static bool Active => C != null && C.f >= 0;

        // Fazione alleata che può convocare l'equipaggio (per l'incontro della convocazione).
        public static int Candidate() => Active ? -1 : Diplomacy.F.Where(f => Stories.ContainsKey(f) && !Honored(f) && Factions.Level(f) >= 2 && !Saga.State.campaignsFailed.Contains(f)).DefaultIfEmpty(-1).First();

        static PLSectorInfo Target(int faction, int min, int max)
        {
            var cur = PLServer.GetCurrentSector(); if (cur == null) return null;
            var all = new List<PLSectorInfo>(); foreach (var kv in PLGlobal.Instance.Galaxy.AllSectorInfos) { var s = kv.Value; if (s?.MySPI != null && s.VisualIndication == ESectorVisualIndication.NONE && s.MissionSpecificID == -1 && s.ID != cur.ID) all.Add(s); }
            var near = all.Where(s => Nemesis.Distance(cur, s) is var d && d >= min && d <= max).ToList();
            var own = near.Where(s => s.MySPI.Faction == faction).ToList();
            var pool = own.Count > 0 ? own : near.Count > 0 ? near : all; return pool.Count == 0 ? null : pool[rng.Next(pool.Count)];
        }

        public static void Begin(int f)
        {
            var t = Target(f, 2, 5); if (t == null) return;
            Saga.State.campaign = new CampaignState { f = f, act = 1, sector = t.ID, deadline = Jumps() + 15 };
            Contracts.Plot(t.ID); Saga.MarkDirty(); GalaxyView.Dirty();
            Director.Notify("CAMPAGNA · " + Stories[f].title.ToUpperInvariant(), $"Primo atto: {Stories[f].act1}, nel settore {Places.Of(t.ID)}, a {Nemesis.JumpsTo(t.ID)} salti. Rotta tracciata.");
            Saga.Chronicle($"Inizia la campagna «{Stories[f].title}» per la {GalaxyWar.Name(f)}.");
        }

        // Secondo atto risolto (dall'incontro): si passa all'ultimo atto.
        public static void Act3()
        {
            var c = C; if (c == null) return; var en = Diplomacy.Enemies(c.f).FirstOrDefault(); var t = Target(en >= 0 && Diplomacy.Enemies(c.f).Any() ? en : c.f, 3, 6); if (t == null) return;
            c.act = 3; c.sector = t.ID; c.spawned = false; c.deadline = Jumps() + 15; Contracts.Plot(t.ID); Saga.MarkDirty(); GalaxyView.Dirty();
            Director.Notify("CAMPAGNA · ULTIMO ATTO", $"{Cap(Stories[c.f].act3)}: settore {Places.Of(t.ID)}, a {Nemesis.JumpsTo(t.ID)} salti. Rotta tracciata.");
        }
        static string Cap(string s) => char.ToUpper(s[0]) + s[1..];

        static readonly List<int> foes = new(); static int boss = -1, foeSector = -1;
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            var c = C; if (c == null || c.f < 0) return;
            try
            {
                var ship = Stations.MyShip; bool warp = false; try { warp = ship.InWarp; } catch { }
                int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { }
                if (c.act != 2 && Jumps() > c.deadline && !c.spawned) { Fail(c, "troppo tempo è passato"); return; }
                if ((c.act == 1 || c.act == 3) && !c.spawned && sec == c.sector && !warp)
                {
                    float p = Director.Power(ship, out _, out _); var st = Stories[c.f]; foes.Clear(); boss = -1; foeSector = sec;
                    if (c.act == 1) { for (int i = 0; i < 2 + (p >= 8 ? 1 : 0); i++) { var s = Director.Spawn(ship, Director.PickShip(p, i == 0 ? 0 : -1), Director.Modifiers(p), st.foe1); if (s != null) foes.Add(s.ShipID); } }
                    else
                    {
                        var heavy = new[] { "E_DESTROYER", "E_WDCRUISER", "E_CARRIER", "E_ANNIHILATOR" }; var bt = Enum.TryParse<EShipType>(heavy[Math.Clamp((int)(p / 5f), 0, 3)], out var x) ? x : Director.PickShip(p + 4, 1);
                        var b = Director.Spawn(ship, bt, Director.Modifiers(p + 5) | 1 << (int)EShipModifierType.ELITE, st.boss); if (b != null) { boss = b.ShipID; foes.Add(b.ShipID); }
                        for (int i = 0; i < 2; i++) { var s = Director.Spawn(ship, Director.PickShip(p, -1), Director.Modifiers(p), "Scorta"); if (s != null) foes.Add(s.ShipID); }
                    }
                    if (foes.Count == 0) return;
                    c.spawned = true; Saga.MarkDirty();
                    Director.Notify("CAMPAGNA", c.act == 1 ? $"Siamo arrivati: {st.act1}. {foes.Count} navi nemiche in avvicinamento." : $"{st.boss} esce dalla curvatura con la sua scorta. È il momento.");
                    return;
                }
                if (!c.spawned || foes.Count == 0) return;
                if (sec != foeSector || warp) { foes.Clear(); c.spawned = false; Saga.MarkDirty(); return; } // ritirata: si potrà tornare
                foes.RemoveAll(id => { PLShipInfoBase s = null; try { s = PLEncounterManager.Instance.GetShipFromID(id); } catch { } return s == null || s.HasBeenDestroyed || (s.MyStats != null && s.MyStats.HullCurrent <= 0f); });
                bool done = c.act == 1 ? foes.Count == 0 : !foes.Contains(boss);
                if (!done) return;
                foes.Clear(); c.spawned = false;
                if (c.act == 1)
                {
                    c.act = 2; Saga.MarkDirty(); Course.Clear(); Factions.Change(c.f, 5, null);
                    Director.Notify("CAMPAGNA", "Primo atto concluso. Il comando ci farà sapere come proseguire.");
                    Saga.Later("incontro", 1, 2, "campagna_dilemma", "", c.f);
                }
                else Win(c);
            }
            catch (Exception e) { Plugin.L.LogWarning("Campagne: " + e.Message); }
        }

        static void Win(CampaignState c)
        {
            var st = Stories[c.f]; var ship = Stations.MyShip;
            Saga.State.honors.Add(c.f); Saga.State.campaign = null; Course.Clear();
            Factions.Change(c.f, 30, null); var got = Loot.Grant(ship, 6, 7, "campagna");
            try { PLServer.Instance.CurrentCrewCredits += 6000; } catch { }
            Trophies.Add(st.honor, "campagna della " + GalaxyWar.Name(c.f)); Bases.GiveDeed("fortezza", $"concessione della {GalaxyWar.Name(c.f)}"); Legacy.Record("onorificenza", $"ha ricevuto la {st.honor} dalla {GalaxyWar.Name(c.f)}"); Competitors.Renown(120); Morale.Change(12, null); Saga.State.onore += 4;
            Director.Notify("ONORIFICENZA", st.win + " 6000 crediti" + (got != null ? $" e {got.Name}" : "") + ".");
            News.Post("fronte", $"La {GalaxyWar.Name(c.f)} conferisce alla «{(string)ship.ShipName}» la {st.honor}.");
            Saga.Chronicle($"Campagna «{st.title}» conclusa: {st.honor}.");
            Service.Add("campagne.vinte", 1); Saga.MarkDirty(); GalaxyView.Dirty();
        }

        static void Fail(CampaignState c, string why)
        {
            var st = Stories[c.f];
            Director.Notify("CAMPAGNA", $"La {GalaxyWar.Name(c.f)} affida «{st.title}» ad altri: {why}.");
            Factions.Change(c.f, -8, null); Saga.State.campaignsFailed.Add(c.f); Saga.State.campaign = null; Course.Clear(); Saga.MarkDirty(); GalaxyView.Dirty();
        }

        // Il dilemma può chiudere la campagna in anticipo (per scelta dell'equipaggio).
        public static void Abort(string why) { var c = C; if (c == null) return; var st = Stories[c.f]; Saga.State.campaignsFailed.Add(c.f); Saga.State.campaign = null; Course.Clear(); Saga.Chronicle($"Campagna «{st.title}» interrotta: {why}."); Saga.MarkDirty(); GalaxyView.Dirty(); }

        public static string Summary() { var c = C; if (c == null || c.f < 0) return ""; return $"Campagna «{Stories[c.f].title}»: atto {c.act}/3" + (c.act != 2 ? $", settore {Places.Of(c.sector)} ({Nemesis.JumpsTo(c.sector)} salti)" : ""); }
        public static string Describe() => (Summary().Length > 0 ? Summary() : "nessuna campagna") + $"; onorificenze: {string.Join(", ", Saga.State.honors.Select(f => Stories[f].honor).DefaultIfEmpty("nessuna"))}";
        public static string ForceHere() { var c = C; if (c == null) return "nessuna campagna"; c.sector = PLServer.GetCurrentSector().ID; c.spawned = false; return Describe(); }
    }
}

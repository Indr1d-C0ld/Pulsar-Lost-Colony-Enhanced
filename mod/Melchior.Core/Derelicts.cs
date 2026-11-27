// Relitti alla deriva: dopo una battaglia resta lo scafo spento di una nave abbattuta; nei cimiteri di navi ce ne
// sono diversi. Chiamando il relitto si sceglie come esplorarlo (squadra d'ingegneria, scansione scientifica,
// assalto rapido): bottino, merci, dati di ricerca, registri di bordo, a volte un sopravvissuto da arruolare,
// a volte una trappola. I recuperatori NPC che arrivano dopo le battaglie se lo prendono, se arriviamo tardi.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Melchior
{
    public static class Derelicts
    {
        static readonly System.Random rng = new();
        public sealed class Wreck { public int id; public string name = "", from = ""; public float born, claimAt; public bool explored; }
        static readonly List<Wreck> wrecks = new(); static int sector = -1; static float seedAt;
        public static Wreck Pending;
        public static int Explored, Lost;

        public static bool IsWreck(int shipId) => wrecks.Any(w => w.id == shipId);
        static PLShipInfoBase Ship(int id) { try { var s = PLEncounterManager.Instance.GetShipFromID(id); return s == null || s.HasBeenDestroyed ? null : s; } catch { return null; } }

        public static void Spawn(string from)
        {
            var t = new[] { EShipType.E_ABANDONED_SHIP_DESTROYER, EShipType.E_ABANDONED_SHIP_INTREPID, EShipType.E_ABANDONED_SHIP_CARRIER }[rng.Next(3)];
            string name = from.Length > 0 ? $"Relitto della «{from}»" : "Relitto senza nome";
            var s = Traffic.SpawnNeutral(t, name); if (s == null) return;
            wrecks.Add(new Wreck { id = s.ShipID, name = name, from = from, born = Time.time, claimAt = Time.time + 90f + rng.Next(90) });
            Stations.LogEvent(Stations.MyShip.ShipID, $"Sensori: {name} alla deriva, sistemi spenti. Si può chiamare per esplorarlo.");
        }

        public static void Tick()
        {
            if (!Traffic.Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { }
                bool warp = false; try { warp = Stations.MyShip.InWarp; } catch { }
                if (sec != sector) { sector = sec; wrecks.Clear(); Pending = null; seedAt = Time.time + 10f; return; }
                if (seedAt > 0f && Time.time > seedAt && !warp)
                {
                    seedAt = 0f;
                    // nei cimiteri di navi e fra i resti delle supernove ci sono sempre relitti
                    try { var s = PLServer.GetSectorWithID(sec); if (s != null && (s.VisualIndication == ESectorVisualIndication.GRAVEYARD || Wonders.Remnant(sec))) for (int i = 0; i < 2 + rng.Next(2); i++) Spawn(""); } catch { }
                }
                // gli sciacalli si prendono i relitti che nessuno esplora
                foreach (var w in wrecks.Where(w => !w.explored && Time.time > w.claimAt).ToList())
                {
                    if (!Traffic.HasSalvagers()) { w.claimAt = Time.time + 30f; continue; }
                    w.explored = true; Lost++; var s = Ship(w.id); try { s?.Ship_WarpOutNow(); } catch { }
                    Stations.LogEvent(Stations.MyShip.ShipID, $"I recuperatori hanno agganciato {w.name} e lo trascinano via. Peccato.");
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Relitti: " + e.Message); }
        }

        // ---- esplorazione (dalla chiamata al relitto)
        public static string Hail(PLShipInfoBase t)
        {
            var w = wrecks.FirstOrDefault(x => x.id == t.ShipID); if (w == null) return null;
            if (w.explored) return $"{w.name}: silenzio. Qualcuno è già passato di qui.";
            Pending = w; if (!Encounters.Start("relitto", true)) { Pending = null; return "C'è già qualcosa di cui occuparsi."; }
            return $"{w.name}: nessuna risposta, solo il ronzio dei sistemi d'emergenza. Il pannello del capitano attende ordini.";
        }
        public static string Explore(string how, Func<int, int, string> loot, Action<int> cr)
        {
            var w = Pending; Pending = null; if (w == null) return "Il relitto non c'è più.";
            w.explored = true; Explored++; Feats.Derelict();
            var me = Stations.MyShip; var bits = new List<string>(); double roll = rng.NextDouble();
            double trap = how switch { "ingegneria" => 0.12, "scienza" => 0.06, _ => 0.25 }, rich = how == "assalto" ? 1.3 : 1.0;
            if (roll < trap)
            {
                var o = Saga.State.officers.Where(x => x.alive && x.cls >= 1).OrderBy(_ => rng.Next()).FirstOrDefault();
                if (o != null) Officers.Wound(o, 2);
                Morale.Change(-2, null);
                return $"Una trappola: il reattore del relitto era instabile, o qualcuno aveva lasciato un regalo. La squadra rientra di corsa" + (o != null ? $", e {o.first} {o.last} finisce in infermeria." : ".");
            }
            // bottino: componenti, merci, crediti, dati, registri, sopravvissuti
            string got = loot(1, how == "assalto" ? 4 : 3); if (got.Length > 0) bits.Add(got);
            if (rng.NextDouble() < 0.5 * rich) { var g = Market.Goods[rng.Next(Market.Goods.Length)]; int n = Market.Add(g.id, 3 + rng.Next(6)); if (n > 0) bits.Add($"{n} casse di {Market.Low(g.name)}"); }
            if (rng.NextDouble() < 0.6) { int c = (int)((200 + rng.Next(800)) * rich); cr(c); }
            if (how == "scienza" || rng.NextDouble() < 0.3) { int d = 4 + rng.Next(8); Research.Add(d, "registri del relitto"); bits.Add($"{d} dati di ricerca"); }
            if (rng.NextDouble() < 0.35) { News.Rumor(); bits.Add("un registro di bordo con notizie fresche (nel notiziario)"); }
            if (rng.NextDouble() < 0.12)
            {
                Feats.Survivor();
                var o = Saga.State.officers.Where(x => x.alive && x.cls >= 1).OrderBy(x => x.rank).FirstOrDefault();
                string first = Officers.RandomFirst(), last = Officers.RandomLast();
                if (o != null && o.rank == 0) { var n = Officers.Hire(o.cls, first, last, "veterano", 2); bits.Add($"un sopravvissuto: {first} {last}, ufficiale esperto, che prende servizio {Officers.At(n.cls)}"); }
                else { Morale.Change(3, null); Saga.State.onore += 1; bits.Add($"un sopravvissuto, {first} {last}, che porteremo al primo porto"); }
            }
            return bits.Count == 0 ? $"{w.name} era già stato spolpato: solo polvere e silenzio." : $"Nel {w.name.Substring(0, 1).ToLower() + w.name.Substring(1)} troviamo: " + string.Join(", ", bits) + ".";
        }
        public static string Describe() => $"relitti nel settore {wrecks.Count} (esplorati {wrecks.Count(w => w.explored)}); totale esplorati {Explored}, persi {Lost}";
        public static string Force() { Spawn("Prova"); return Describe(); }
    }
}

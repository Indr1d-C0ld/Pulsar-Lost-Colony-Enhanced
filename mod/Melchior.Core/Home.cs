// Porto di casa: a un attracco l'equipaggio può adottare quel porto come casa (incontro). Tornarci significa riposo
// (morale), riparazioni gratuite e ufficiali rimessi in piedi. La casa ha una prosperità (0-100) che cresce con le
// consegne dirette lì, con i contributi e con le difese riuscite; a ogni livello offre di più (sconti al cantiere,
// una decima a ogni ritorno, veterani pronti a imbarcarsi). Ogni tanto la casa viene minacciata e chiama aiuto:
// difenderla vale molto, lasciarla sola la impoverisce.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class HomeState { public int sector = -1, prosperity = 20, threatUntil = -1, nextThreat = -1, defended, lost; public string name = ""; public bool spawned; }

    public static class Home
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        public static HomeState H => Saga.State.home;
        public static bool Has => H != null && H.sector >= 0;
        public static int Level => !Has ? 0 : H.prosperity >= 80 ? 3 : H.prosperity >= 55 ? 2 : H.prosperity >= 30 ? 1 : 0;
        public static bool AtHome() { try { return Has && PLServer.GetCurrentSector()?.ID == H.sector; } catch { return false; } }
        static string Clean(string n) => System.Text.RegularExpressions.Regex.Replace(n ?? "", @"\s*\(\d+\)$", "");

        public static void Adopt()
        {
            var s = PLServer.GetCurrentSector(); if (s == null) return;
            Saga.State.home = new HomeState { sector = s.ID, name = Clean(s.Name).Length > 0 ? Clean(s.Name) : $"il porto di {Places.Name(s.ID)}", nextThreat = Obscured.Jumps() + rng.Next(12, 20) };
            Saga.MarkDirty(); GalaxyView.Dirty();
            Saga.Chronicle($"{H.name} diventa il nostro porto di casa.");
            News.Post("flotta", $"La «{(string)Stations.MyShip.ShipName}» sceglie {H.name} come porto di casa. In banchina c'è chi già ci saluta per nome.");
        }

        public static void Prosper(int d, string why = null)
        {
            if (!Host || !Has) return; int before = Level; H.prosperity = Math.Clamp(H.prosperity + d, 0, 100); Saga.MarkDirty(); GalaxyView.Dirty();
            if (Level > before) Director.Notify("PORTO DI CASA", $"{H.name} cresce: prosperità {H.prosperity}%." + Level switch { 1 => " Il cantiere di casa ci fa lo sconto.", 2 => " A ogni ritorno la colonia ci riserva una decima.", _ => " I veterani del porto fanno la fila per imbarcarsi con noi." } + (why != null ? " " + why + "." : ""));
            if (Level == 3 && before < 3) { Trophies.Add($"Porto di casa prospero: {H.name}", "porto di casa"); Legacy.Record("casa", $"ha fatto di {H.name} un porto prospero"); }
        }

        static int lastSector = -1;
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null || !Has) return;
            try
            {
                var ship = Stations.MyShip; bool warp = false; try { warp = ship.InWarp; } catch { }
                int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { }
                int j = Obscured.Jumps();
                if (sec != lastSector && !warp)
                {
                    lastSector = sec;
                    if (sec == H.sector && H.threatUntil < 0) Welcome(ship);
                }
                // minaccia alla casa
                if (H.threatUntil < 0 && j >= H.nextThreat && sec != H.sector)
                {
                    H.threatUntil = j + 6; H.spawned = false; Saga.MarkDirty(); Contracts.Plot(H.sector);
                    Director.Notify("PORTO DI CASA", $"Messaggio da {H.name}: predoni in vista del porto. Chiedono aiuto: abbiamo {6} salti. Rotta tracciata.");
                    News.Post("flotta", $"{H.name} è sotto attacco. Si aspetta la «{(string)ship.ShipName}».");
                }
                if (H.threatUntil >= 0)
                {
                    if (sec == H.sector && !warp && !H.spawned) Defend(ship);
                    else if (H.spawned) TrackDefense(sec, warp);
                    else if (j > H.threatUntil) Lose();
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Porto di casa: " + e.Message); }
        }

        static void Welcome(PLShipInfo ship)
        {
            Morale.Change(8, "Siamo a casa"); Saga.State.sinceRest = 0;
            try { var st = ship.MyStats; st.HullCurrent = Math.Min(st.HullMax, st.HullCurrent + st.HullMax * 0.4f); } catch { }
            foreach (var sy in Stations.SysIds) Stations.Set(ship.ShipID, "dmx_" + sy, 0.01f);
            foreach (var o in Saga.State.officers.Where(o => o.alive && o.injuredUntil >= 0)) o.injuredUntil = -1;
            string extra = "";
            if (Level >= 2) { int cr = 300 + H.prosperity * 15; try { PLServer.Instance.CurrentCrewCredits += cr; } catch { } extra = $" La colonia ci riserva una decima di {cr} crediti."; }
            Director.Notify("BENTORNATI A CASA", $"{H.name} ci accoglie: riparazioni gratuite, sistemi rimessi a posto, infermeria vuota.{extra}");
        }

        static readonly List<int> foes = new(); static int foeSector = -1;
        static void Defend(PLShipInfo ship)
        {
            float p = Director.Power(ship, out _, out _); foes.Clear(); foeSector = H.sector;
            for (int i = 0; i < 2 + (p >= 8 ? 1 : 0) + (p >= 14 ? 1 : 0); i++) { var s = Director.Spawn(ship, Director.PickShip(p, i == 0 ? 0 : -1), Director.Modifiers(p), "Predone all'assalto"); if (s != null) foes.Add(s.ShipID); }
            if (foes.Count == 0) return;
            H.spawned = true; Saga.MarkDirty();
            Director.Notify("PORTO DI CASA", $"Arriviamo appena in tempo: {foes.Count} navi predone stanno attaccando {H.name}.");
        }
        static void TrackDefense(int sec, bool warp)
        {
            if (sec != foeSector || warp) { foes.Clear(); H.spawned = false; Saga.MarkDirty(); return; }
            foes.RemoveAll(id => { PLShipInfoBase s = null; try { s = PLEncounterManager.Instance.GetShipFromID(id); } catch { } return s == null || s.HasBeenDestroyed || (s.MyStats != null && s.MyStats.HullCurrent <= 0f); });
            if (foes.Count > 0) return;
            H.threatUntil = -1; H.spawned = false; H.defended++; H.nextThreat = Obscured.Jumps() + rng.Next(15, 25); Course.Clear();
            Prosper(15, "La colonia ricorda chi l'ha difesa"); Morale.Change(10, "Abbiamo difeso casa"); Saga.State.onore += 2; Competitors.Renown(30);
            var got = Loot.Grant(Stations.MyShip, 3, 5, "porto di casa");
            Director.Notify("PORTO DI CASA", $"{H.name} è salva. La banchina è piena di gente che applaude." + (got != null ? $" Ci regalano {got.Name}." : ""));
            News.Post("flotta", $"La «{(string)Stations.MyShip.ShipName}» respinge l'assalto a {H.name}.");
            Saga.Chronicle($"Difesa del porto di casa: {H.name} è salva.");
            Saga.MarkDirty();
        }
        static void Lose()
        {
            H.threatUntil = -1; H.lost++; H.nextThreat = Obscured.Jumps() + rng.Next(15, 25); Course.Clear();
            Prosper(-25); Morale.Change(-10, "Non siamo arrivati in tempo a difendere casa");
            Director.Notify("PORTO DI CASA", $"{H.name} è stata saccheggiata mentre eravamo lontani. Ci vorrà tempo per rimettersi in piedi.");
            News.Post("flotta", $"{H.name} saccheggiata dai predoni. La sua nave non è arrivata in tempo.");
            Saga.Chronicle($"{H.name} saccheggiata in nostra assenza."); Saga.MarkDirty();
        }

        public static string Summary() => !Has ? "" : $"Casa: {H.name} {H.prosperity}%" + (H.threatUntil >= 0 ? " · sotto attacco!" : "");
        public static string Describe() => !Has ? "nessun porto di casa" : $"{H.name} (settore {Places.Of(H.sector)}): prosperità {H.prosperity} (livello {Level}), difese {H.defended}, saccheggi {H.lost}, prossima minaccia al salto {H.nextThreat}{(H.threatUntil >= 0 ? $", sotto attacco fino al salto {H.threatUntil}" : "")}";
        public static string ForceThreat() { if (!Has) return "nessuna casa"; H.nextThreat = Obscured.Jumps(); return "minaccia in arrivo"; }
    }
}

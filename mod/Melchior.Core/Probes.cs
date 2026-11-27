// Sonde di ricognizione: la Scienza lancia una sonda (150 crediti) che esamina i settori vicini e riferisce che cosa c'è:
// fazione, ambiente, luoghi da esplorare, flotte in transito, covi, personaggi ricorrenti, crisi, porti, luoghi
// leggendari, il cuore della minaccia. Da ogni riga del rapporto si può tracciare la rotta. Gira sull'host; il rapporto
// viaggia con GalaxyView e compare nella scheda Sonde della Scienza.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class ProbeRow { public int sector, jumps = 1; public string text = ""; public bool interesting; }

    public static class Probes
    {
        public static int Cost => (int)(150 * (1f - 0.2f * Talents.Ours("sondatore")) * (1f - Math.Min(0.7f, Loot.Mine("sonde"))));
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static List<ProbeRow> last = new(); static float ready; static int fromSector = -1;
        public static List<ProbeRow> Rows => Host ? last : GalaxyView.Current?.probe ?? new List<ProbeRow>();
        public static int From => Host ? fromSector : GalaxyView.Current?.probeFrom ?? -1;

        public static void Request(string op, int sector = -1)
        {
            if (Host) { Execute(op, sector); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|sonda|{op}|{sector}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var p = msg.Split('|'); if (p.Length >= 4 && Host) Execute(p[2], int.TryParse(p[3], out var s) ? s : -1); }

        static void Execute(string op, int sector)
        {
            if (op == "rotta") { if (sector >= 0) { Course.Plot(sector); Director.Notify("ROTTA", $"Rotta tracciata verso il settore {Places.Of(sector)}."); } return; }
            if (op != "lancia" || !Saga.Loaded) return;
            float now = UnityEngine.Time.time; if (now < ready) { Stations.Announce("SONDE", $"Il lanciasonde si sta ricaricando: {ready - now:0} secondi."); return; }
            int have = 0; try { have = PLServer.Instance.CurrentCrewCredits; } catch { }
            if (!Fabrication.TakeProbe()) // le sonde fabbricate a bordo partono gratis
            {
                if (have < Cost) { Stations.Announce("SONDE", $"Una sonda costa {Cost} crediti."); return; }
                try { PLServer.Instance.CurrentCrewCredits = have - Cost; } catch { return; }
            }
            ready = now + 60f; Launch(); Service.Add("scienza.sonde", 1);
        }

        static void Launch()
        {
            var cur = PLServer.GetCurrentSector(); if (cur?.Neighbors == null) return;
            int seed = (int)PLServer.Instance.GalaxySeed; var rows = new List<ProbeRow>(); var st = Saga.State;
            var nb = Enumerable.Range(0, cur.Neighbors.Count).Select(i => cur.Neighbors[i]).Where(n => n != null).OrderBy(n => (n.Position - cur.Position).sqrMagnitude).Take(8).ToList();
            foreach (var n in nb)
            {
                var bits = new List<string> { $"«{Places.Name(n.ID)}»" }; bool hot = false;
                int f = n.MySPI?.Faction ?? -1; if (f >= 0 && f != 6) bits.Add(f == 3 ? "Fluffy" : Factions.Abbr(f).Length > 0 ? Factions.Abbr(f) : GalaxyWar.Name(f));
                if (n.VisualIndication != ESectorVisualIndication.NONE) bits.Add("porto");
                var env = Environments.EnvOf(seed, n.ID); if (env != null && n.VisualIndication == ESectorVisualIndication.NONE) bits.Add(env.Value.name);
                var poi = Exploration.PoiOf(seed, n.ID); if (poi != null && n.VisualIndication == ESectorVisualIndication.NONE && !st.explored.Contains(n.ID)) { bits.Add(poi.Value.name); hot = true; }
                foreach (var fl in Fleets.At(n.ID)) { bits.Add(fl); hot = true; }
                foreach (var l in st.lairs.Where(x => x.sector == n.ID)) { bits.Add($"covo: {l.name}"); hot = true; }
                foreach (var g in Nemesis.Alive.Where(x => x.sector == n.ID)) { bits.Add(g.Full); hot = true; }
                if (Crises.Cur != null && Crises.Cur.sector == n.ID) { bits.Add("crisi: " + Crises.Cur.title.ToLowerInvariant()); hot = true; }
                if (st.legend != null && st.legend.sector == n.ID) { bits.Add(st.legend.name); hot = true; }
                if (st.threat != null && st.threat.heart == n.ID && st.threat.stage >= 4) { bits.Add("il cuore della minaccia"); hot = true; }
                if (Home.Has && Home.H.sector == n.ID) bits.Add("casa");
                var ob = Bases.At(n.ID); if (ob != null) bits.Add("nostra base: " + ob.name + (ob.raidUntil >= 0 ? " (SOTTO ATTACCO)" : ""));
                if (Wonders.Sector == n.ID && Wonders.Kind is "supernova" or "cometa") { bits.Add(Wonders.Kind == "cometa" ? "la cometa errante" : "la stella che sta per esplodere"); hot = true; }
                rows.Add(new ProbeRow { sector = n.ID, text = bits.Count <= 1 ? bits[0] + " · niente di rilevante" : string.Join(" · ", bits), interesting = hot });
            }
            last = rows; fromSector = cur.ID; GalaxyView.Dirty(); Research.Add(2);
            int hotCount = rows.Count(r => r.interesting);
            Director.Notify("SONDE", $"La sonda ha esaminato {rows.Count} settori vicini: {(hotCount == 0 ? "niente di particolare" : hotCount == 1 ? "uno merita una visita" : $"{hotCount} meritano una visita")}. Il rapporto è nella scheda Sonde della Scienza.");
        }

        // Faro di comunicazione: a ogni arrivo entro tre salti da un nostro faro, un rapporto gratuito.
        static int lastAuto = -1;
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { } bool warp = false; try { warp = Stations.MyShip.InWarp; } catch { }
            if (sec < 0 || sec == lastAuto || warp) return; lastAuto = sec;
            if (Bases.Near("faro", 3).Any()) { Launch(); }
        }

        public static string Describe() => last.Count == 0 ? "nessun rapporto" : string.Join("\n      ", last.Select(r => $"{r.sector}: {r.text}").Prepend($"rapporto dal settore {fromSector}:"));
        public static string Force() { ready = 0; try { PLServer.Instance.CurrentCrewCredits += Cost; } catch { } Execute("lancia", -1); return Describe(); }
    }
}

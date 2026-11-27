// Cantiere navale: migliorie permanenti della nave, in tre livelli, acquistabili solo in porto dalla scheda Cantiere
// dell'Ingegneria. Si salvano con la galassia; i livelli viaggiano con GalaxyView, perché scafo e scudi vanno ricalcolati
// su ogni macchina (aggancio a PLShipStats.CalculateStats, come gli affissi del bottino).
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public static class Shipyard
    {
        public sealed class Def { public string id = "", name = "", effect = ""; }
        public static readonly Def[] Defs =
        {
            new() { id = "scafo", name = "Scafo rinforzato", effect = "scafo +8% per livello" },
            new() { id = "scudi", name = "Generatori schermati", effect = "scudi +8% per livello" },
            new() { id = "stiva", name = "Stiva schermata", effect = "meno ispezioni e meno predoni attirati dal carico" },
            new() { id = "sensori", name = "Sensori a lungo raggio", effect = "rilevano luoghi e ambienti nei settori vicini" },
            new() { id = "motori", name = "Motori ottimizzati", effect = "asteroidi, pozzi gravitazionali e plasma pesano meno" },
            new() { id = "infermeria", name = "Infermeria", effect = "ufficiali feriti più di rado e guariti prima" },
            new() { id = "alloggi", name = "Alloggi migliori", effect = "il morale regge meglio i lunghi viaggi" },
        };
        public static readonly int[] Cost = { 3000, 7000, 14000 };
        public static int CostOf(int level) => (int)(Cost[level] * (Species.Accord("kethri") ? 0.85f : 1f) * (Home.AtHome() && Home.Level >= 1 ? 0.9f : 1f) * (Bases.OwnShipyardHere() is Base yb ? 1f - 0.1f - 0.05f * yb.level : 1f)); // prezzi da soci per chi ha un accordo con i Kethri
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static Dictionary<string, int> U => Saga.State.upgrades ??= new Dictionary<string, int>();

        public static int Level(string id)
        {
            if (Host) return Saga.Loaded && U.TryGetValue(id, out var v) ? v : 0;
            var r = GalaxyView.Current?.upgrades; return r != null && r.TryGetValue(id, out var w) ? w : 0;
        }
        public static float Less(string id, float per = 0.25f) => Math.Max(0.1f, 1f - Level(id) * per);

        public static bool InPort() { try { var s = PLServer.GetCurrentSector(); return s != null && (s.VisualIndication != ESectorVisualIndication.NONE || Bases.OwnShipyardHere() != null) && !Stations.MyShip.InWarp; } catch { return false; } }

        // Scafo e scudi: dopo tutti gli altri calcoli.
        public static void Apply(PLShipStats s)
        {
            try
            {
                var me = Stations.MyShip; if (me == null || s.Ship == null || s.Ship.ShipID != me.ShipID) return;
                int h = Level("scafo"), sh = Level("scudi");
                if (h > 0) s.HullMax *= 1f + 0.08f * h;
                if (Species.Accord("ossidiani")) s.HullMax *= 1.05f; // piastre di cristallo degli Ossidiani
                if (sh > 0) s.ShieldsMax *= 1f + 0.08f * sh;
            }
            catch { }
        }

        public static void Request(string id)
        {
            if (Host) { Execute(id); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|cantiere|{id}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var p = msg.Split('|'); if (p.Length >= 3 && Host) Execute(p[2]); }

        static void Execute(string id)
        {
            var d = Defs.FirstOrDefault(x => x.id == id); if (d == null || !Saga.Loaded) return;
            int lvl = Level(id);
            if (lvl >= 3) { Stations.Announce("CANTIERE", $"{d.name}: già al livello massimo."); return; }
            if (!InPort()) { Stations.Announce("CANTIERE", "Il cantiere lavora solo in porto: attraccate a una stazione."); return; }
            int cost = CostOf(lvl), have = 0; try { have = PLServer.Instance.CurrentCrewCredits; } catch { }
            if (have < cost) { Stations.Announce("CANTIERE", $"Servono {cost} crediti per {d.name.ToLowerInvariant()} (livello {lvl + 1})."); return; }
            try { PLServer.Instance.CurrentCrewCredits = have - cost; } catch { return; }
            U[id] = lvl + 1; Saga.MarkDirty(); GalaxyView.Dirty();
            Director.Notify("CANTIERE", $"{d.name} al livello {lvl + 1}: {d.effect}. Spesa: {cost} crediti.");
            Saga.Chronicle($"Cantiere: {d.name.ToLowerInvariant()} al livello {lvl + 1}.");
            Service.Add("cantiere.migliorie", 1); Morale.Change(2, null);
            if (U.Values.Sum() == Defs.Length * 3) Trophies.Add("Nave completamente rimodernata", "cantiere navale");
        }

        // Sensori a lungo raggio: all'arrivo, luoghi (e dal livello 2 ambienti) nei settori vicini.
        static int lastSector = -1;
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null || Level("sensori") == 0) return;
            try
            {
                var cur = PLServer.GetCurrentSector(); if (cur == null || cur.ID == lastSector || Stations.MyShip.InWarp) return;
                lastSector = cur.ID; int seed = (int)PLServer.Instance.GalaxySeed; var nb = cur.Neighbors; if (nb == null) return;
                var found = new List<string>();
                for (int i = 0; i < nb.Count && found.Count < 3; i++)
                {
                    var n = nb[i]; if (n == null || n.VisualIndication != ESectorVisualIndication.NONE) continue;
                    var p = Exploration.PoiOf(seed, n.ID); if (p != null && !Saga.State.explored.Contains(n.ID)) found.Add($"{p.Value.name} (settore {Places.Of(n.ID)})");
                    if (Level("sensori") >= 2) { var e = Environments.EnvOf(seed, n.ID); if (e != null && found.Count < 3) found.Add($"{e.Value.name} (settore {Places.Of(n.ID)})"); }
                }
                if (found.Count > 0) Director.Notify("SENSORI A LUNGO RAGGIO", "Nei settori vicini: " + string.Join("; ", found) + ".");
            }
            catch (Exception e) { Plugin.L.LogWarning("Cantiere: " + e.Message); }
        }

        public static string Describe() => string.Join(", ", Defs.Select(d => $"{d.id} {Level(d.id)}")) + (InPort() ? " (in porto)" : "");
        public static string Force(string id, int lvl) { U[id] = Math.Clamp(lvl, 0, 3); Saga.MarkDirty(); GalaxyView.Dirty(); return Describe(); }
    }
}

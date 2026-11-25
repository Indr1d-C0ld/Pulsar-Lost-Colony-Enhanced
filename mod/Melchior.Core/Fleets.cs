// Flotte con un'agenda: convogli mercantili che viaggiano fra settori lontani, pattuglie delle fazioni sul fronte e
// predoni che danno la caccia ai convogli. Sono navi persistenti native (PLPersistantShipInfo) spostate da un settore
// all'altro assegnando il settore, come fa il gioco con i suoi rivali. Quando due flotte si incontrano lontano
// dall'equipaggio lo scontro si risolve da solo; quando una flotta entra nel settore dell'equipaggio, la nave compare.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace Melchior
{
    public class FleetInfo { public string name = "", kind = ""; public int type, faction, dest = -1, legs; }

    public static class Fleets
    {
        static readonly System.Random rng = new();
        static readonly Dictionary<string, PLPersistantShipInfo> live = new(); // nome -> nave persistente
        static float nextStep = 60f; static int lastJumps = -1, lastSeed = int.MinValue;
        static readonly string[] Convoys = { "Speranza di Aldebaran", "Carovana Meridiana", "Stella del Mattino", "Pellegrina", "Seme di Arcadia", "Lanterna", "Madre Terra", "Fortuna di Kepler", "Sale di Vesta", "Via Lattea" };
        static readonly string[] Raiders = { "Artiglio Nero", "Sciacallo", "Corvo Rosso", "Dente di Lupo", "Vipera", "Iena" };
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static List<FleetInfo> State => Saga.State.fleets ??= new List<FleetInfo>();

        static List<PLSectorInfo> Sectors() { var l = new List<PLSectorInfo>(); try { foreach (var kv in PLGlobal.Instance.Galaxy.AllSectorInfos) if (kv.Value != null) l.Add(kv.Value); } catch { } return l; }
        static PLSectorInfo Sector(int id) { try { return PLServer.GetSectorWithID(id); } catch { return null; } }
        static bool Alive(PLShipInfoBase s) { try { return s != null && s.gameObject != null && !s.HasBeenDestroyed; } catch { return false; } }

        static string Label(FleetInfo f) => f.kind switch { "convoglio" => $"il convoglio «{f.name}»", "pattuglia" => $"la pattuglia {f.name}", _ => $"i predoni dell'«{f.name}»" };

        // Dopo un caricamento le navi persistenti esistono già: si ritrovano per nome.
        static void Relink()
        {
            live.Clear();
            var psis = PLServer.Instance.AllPSIs;
            foreach (var f in State.ToList())
            {
                PLPersistantShipInfo found = null;
                for (int i = 0; i < psis.Count; i++) { var p = psis[i]; if (p != null && p.ShipName == f.name && !p.IsShipDestroyed) { found = p; break; } }
                if (found != null) live[f.name] = found; else State.Remove(f);
            }
        }

        static PLSectorInfo RandomOpenSector(PLSectorInfo far = null, int faction = -99)
        {
            var cur = PLServer.GetCurrentSector();
            var l = Sectors().Where(s => s.MySPI != null && s.VisualIndication == ESectorVisualIndication.NONE && s.MissionSpecificID == -1 && (cur == null || s.ID != cur.ID) && (faction == -99 || s.MySPI.Faction == faction)).ToList();
            if (far != null) l = l.OrderByDescending(s => (s.Position - far.Position).sqrMagnitude).Take(Math.Max(1, l.Count / 4)).ToList();
            return l.Count == 0 ? null : l[rng.Next(l.Count)];
        }

        static void Create(string kind)
        {
            var used = State.Select(x => x.name).ToHashSet();
            FleetInfo f;
            if (kind == "convoglio") f = new FleetInfo { kind = kind, type = 11, faction = 0, name = Convoys.FirstOrDefault(n => !used.Contains(n)) ?? "Convoglio " + rng.Next(100, 999) };
            else if (kind == "pattuglia") { int fac = new[] { 0, 1, 2 }[rng.Next(3)]; f = new FleetInfo { kind = kind, type = fac == 2 ? 3 : 14, faction = fac, name = $"{(fac == 2 ? "W.D." : fac == 0 ? "C.U." : "A.O.G.")}-{rng.Next(10, 99)}" }; }
            else f = new FleetInfo { kind = "predoni", type = 9, faction = 1, name = Raiders.FirstOrDefault(n => !used.Contains(n)) ?? "Predone " + rng.Next(10, 99) };
            var start = RandomOpenSector(null, kind == "pattuglia" ? f.faction : -99) ?? RandomOpenSector(); if (start == null) return;
            var psi = new PLPersistantShipInfo((EShipType)f.type, f.faction, start, 0, false, false, false, -1, -1);
            psi.ShipName = f.name; if (f.kind == "predoni") psi.ForcedHostile = true;
            PLServer.Instance.AllPSIs.Add(psi);
            f.dest = (RandomOpenSector(start) ?? start).ID;
            State.Add(f); live[f.name] = psi; Saga.MarkDirty();
            Plugin.L.LogInfo($"[flotte] nuova: {Label(f)} nel settore {start.ID}, diretta al {f.dest}");
            if (f.kind == "convoglio") News.Post("commercio", $"Partito {Label(f)}: rotta lunga verso il settore {f.dest}.");
        }

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            int seed = 0; try { seed = (int)PLServer.Instance.GalaxySeed; } catch { }
            if (seed != lastSeed) { lastSeed = seed; Relink(); }
            int j = 0; try { j = (int)PLServer.Instance.NumJumps; } catch { }
            bool jumped = lastJumps >= 0 && j != lastJumps; lastJumps = j;
            // le navi delle flotte in vista portano il loro nome (il gioco ne darebbe uno generico)
            foreach (var (name, p) in live) try { if (p != null && Alive(p.ShipInstance)) Director.Names[p.ShipInstance.ShipID] = name; } catch { }
            if (!jumped && UnityEngine.Time.time < nextStep) return;
            nextStep = UnityEngine.Time.time + 90f + rng.Next(60);
            try { Step(); } catch (Exception e) { Plugin.L.LogWarning("Flotte: " + e.Message); }
        }

        public static string Step()
        {
            bool warp = false; try { warp = Stations.MyShip.InWarp; } catch { }
            var cur = PLServer.GetCurrentSector(); var notes = new List<string>();
            // popolazione: 3 convogli, 2 pattuglie, 2 gruppi di predoni
            foreach (var (kind, want) in new[] { ("convoglio", 3), ("pattuglia", 2), ("predoni", 2) })
                if (State.Count(x => x.kind == kind) < want) Create(kind);
            foreach (var f in State.ToList())
            {
                if (!live.TryGetValue(f.name, out var psi) || psi == null) { State.Remove(f); continue; }
                if (psi.IsShipDestroyed) { State.Remove(f); live.Remove(f.name); notes.Add($"{Label(f)} non risponde più."); if (f.kind == "convoglio") News.Post("commercio", $"Perso {Label(f)}."); continue; }
                if (Alive(psi.ShipInstance)) continue; // è nel settore dell'equipaggio: si muove da sola
                var at = psi.MyCurrentSector; if (at == null) continue;
                // i predoni puntano al convoglio più vicino
                if (f.kind == "predoni")
                {
                    var prey = State.Where(x => x.kind == "convoglio" && live.ContainsKey(x.name) && live[x.name].MyCurrentSector != null).OrderBy(x => (live[x.name].MyCurrentSector.Position - at.Position).sqrMagnitude).FirstOrDefault();
                    if (prey != null) f.dest = live[prey.name].MyCurrentSector.ID;
                }
                var dest = Sector(f.dest);
                if (dest == null || dest.ID == at.ID) { dest = RandomOpenSector(at, f.kind == "pattuglia" ? f.faction : -99) ?? RandomOpenSector(at); if (dest == null) continue; f.dest = dest.ID; f.legs++; if (f.kind == "convoglio" && f.legs % 2 == 0) News.Post("commercio", $"{Label(f)} è arrivato a destinazione e riparte verso il settore {f.dest}."); }
                List<PLSectorInfo> path = null; try { path = PLGlobal.Instance.Galaxy.GetPathToSector(at, dest)?.ToArray().ToList(); } catch { }
                if (path == null || path.Count < 2) { f.dest = -1; continue; }
                var next = path[1];
                bool intoCrew = cur != null && next.ID == cur.ID;
                if (intoCrew && (warp || rng.NextDouble() < 0.5)) continue; // non tutti entrano nel settore dell'equipaggio
                psi.MyCurrentSector = next;
                if (intoCrew)
                {
                    try { psi.CreateShipInstance(PLEncounterManager.Instance.GetCPEI()); } catch (Exception e) { Plugin.L.LogWarning("Flotte: comparsa fallita: " + e.Message); }
                    Director.Notify("CONTATTO", f.kind == "predoni" ? $"I predoni dell'«{f.name}» escono dalla curvatura nel nostro settore." : $"{char.ToUpper(Label(f)[0]) + Label(f)[1..]} entra nel nostro settore.");
                }
            }
            Encounters(cur);
            Saga.MarkDirty();
            return $"{State.Count} flotte: " + string.Join("; ", State.Select(f => $"{f.kind} {f.name} @{(live.TryGetValue(f.name, out var p) ? p.MyCurrentSector?.ID : -1)}→{f.dest}"));
        }

        // Flotte nello stesso settore, lontano dall'equipaggio: lo scontro si risolve da solo.
        static void Encounters(PLSectorInfo cur)
        {
            var bySector = State.Where(f => live.ContainsKey(f.name) && live[f.name].MyCurrentSector != null && !Alive(live[f.name].ShipInstance))
                .GroupBy(f => live[f.name].MyCurrentSector.ID).Where(g => g.Count() > 1 && (cur == null || g.Key != cur.ID));
            foreach (var g in bySector.ToList())
            {
                var raiders = g.Where(x => x.kind == "predoni").ToList(); var convoys = g.Where(x => x.kind == "convoglio").ToList(); var patrols = g.Where(x => x.kind == "pattuglia").ToList();
                if (raiders.Count > 0 && patrols.Count > 0)
                {
                    var r = raiders[0]; Remove(r);
                    News.Post("flotta", $"La pattuglia {patrols[0].name} ha intercettato e distrutto i predoni dell'«{r.name}» nel settore {g.Key}.");
                }
                else if (raiders.Count > 0 && convoys.Count > 0)
                {
                    var c = convoys[0]; Remove(c);
                    News.Post("commercio", $"Il convoglio «{c.name}» è stato assaltato dai predoni dell'«{raiders[0].name}» nel settore {g.Key}. Nessun superstite.");
                    if (Stations.MyShip != null) Contracts.PostLead(Director.Power(Stations.MyShip, out _, out _), 0); // taglia sui predoni
                }
            }
        }

        static void Remove(FleetInfo f)
        {
            State.Remove(f);
            if (live.TryGetValue(f.name, out var p)) { try { PLServer.Instance.AllPSIs.Remove(p); } catch { } live.Remove(f.name); }
        }

        // Il gioco ripulisce le navi persistenti di certi settori all'arrivo: le nostre flotte vengono rimesse al loro posto.
        internal static List<PLPersistantShipInfo> Protect(PLSectorInfo sector)
        {
            var keep = new List<PLPersistantShipInfo>(); if (sector == null) return keep;
            foreach (var p in live.Values) if (p != null && !p.IsShipDestroyed && p.MyCurrentSector != null && p.MyCurrentSector.ID == sector.ID) keep.Add(p);
            return keep;
        }
        internal static void Restore(List<PLPersistantShipInfo> keep)
        {
            var list = PLServer.Instance?.AllPSIs; if (list == null) return;
            foreach (var p in keep) if (!list.Contains(p)) list.Add(p);
        }

        // Solo per le prove: porta la prima flotta (o quella del tipo indicato) nel settore accanto e la dirige qui.
        public static string Approach(string kind)
        {
            var cur = PLServer.GetCurrentSector(); if (cur?.Neighbors == null || cur.Neighbors.Count == 0) return "nessun settore vicino";
            var f = State.FirstOrDefault(x => kind == null || x.kind == kind); if (f == null || !live.TryGetValue(f.name, out var p)) return "nessuna flotta";
            p.MyCurrentSector = cur.Neighbors[0]; f.dest = cur.ID; return $"{Label(f)} ora nel settore {cur.Neighbors[0].ID}, diretta qui ({cur.ID})";
        }

        public static string Describe() => State.Count == 0 ? "nessuna flotta" : string.Join("\n      ", State.Select(f => $"{f.kind,-10} {f.name,-24} settore {(live.TryGetValue(f.name, out var p) ? p.MyCurrentSector?.ID : -1)} → {f.dest}{(live.TryGetValue(f.name, out var q) && Alive(q.ShipInstance) ? " (in vista)" : "")}").Prepend("flotte:"));
    }

    [HarmonyPatch(typeof(PLPersistantEncounterInstance), nameof(PLPersistantEncounterInstance.ClearPSIs))]
    static class FleetsClearPSIs
    {
        static void Prefix(PLSectorInfo currentSector, out List<PLPersistantShipInfo> __state) { __state = null; try { __state = Fleets.Protect(currentSector); } catch { } }
        static void Postfix(List<PLPersistantShipInfo> __state) { if (__state != null && __state.Count > 0) try { Fleets.Restore(__state); } catch { } }
    }
}

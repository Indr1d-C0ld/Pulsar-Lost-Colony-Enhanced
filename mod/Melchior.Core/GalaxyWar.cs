// Guerra fra fazioni: durante la partita (e, in proporzione, mentre nessuno gioca) le fazioni si contendono i settori
// di confine. Il cambio usa i campi e il canale di sincronizzazione nativi del gioco: la fazione del settore
// (MySPI.Faction), ServerSetChanged (la mappa dei client si aggiorna entro una decina di secondi) e il salvataggio.
// Non si toccano mai il settore corrente, la destinazione del salto, i settori di missione, i negozi e i luoghi speciali.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class WarState
    {
        public Dictionary<string, float> momentum = new();   // slancio per fazione (0-100)
        public int offensive = -1, offensiveTicks;            // fazione in offensiva
        public Dictionary<string, int> counts = new();        // settori per fazione (per il pannello del server)
        public int conquests;
    }

    public static class GalaxyWar
    {
        public static readonly int[] Factions = { 0, 1, 2, 4, 5 }; // C.U., A.O.G., W.D., Infetti, Polytechnic (Fluffy resta fuori)
        static readonly System.Random rng = new();
        static float nextStep = 120f; static int lastJumps = -1;
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static WarState W => Saga.State.war ??= new WarState();

        public static string Name(int f)
        {
            try { var l = PLGlobal.Instance.Galaxy.AllFactions; for (int i = 0; i < l.Count; i++) if (l[i] != null && l[i].FactionID == f && !string.IsNullOrWhiteSpace(l[i].m_FactionName)) return l[i].m_FactionName; } catch { }
            return f switch { 0 => "Colonial Union", 1 => "Alliance of Gentlemen", 2 => "W.D. Corporation", 4 => "Infetti", 5 => "Polytechnic Federation", _ => "nessuno" };
        }
        static string SectorName(PLSectorInfo s) { string n = s.Name; return string.IsNullOrEmpty(n) || int.TryParse(n, out _) ? $"il settore {s.ID}" : $"il settore {s.ID} «{n}»"; }
        static float Mom(int f) => W.momentum.TryGetValue(f.ToString(), out var m) ? m : 50f;
        static void AddMom(int f, float d) => W.momentum[f.ToString()] = Math.Clamp(Mom(f) + d, 5f, 95f);

        static List<PLSectorInfo> Sectors()
        {
            var list = new List<PLSectorInfo>();
            try { foreach (var kv in PLGlobal.Instance.Galaxy.AllSectorInfos) if (kv.Value != null) list.Add(kv.Value); } catch { }
            return list;
        }

        // Un settore può cambiare padrone solo se è "spazio aperto" e lontano dall'equipaggio.
        static bool Contestable(PLSectorInfo s, PLSectorInfo cur, PLSectorInfo target)
        {
            try
            {
                if (s == null || s.MySPI == null || s.LockedToFaction || s.MissionSpecificID != -1 || s.VisualIndication != ESectorVisualIndication.NONE) return false;
                if (cur != null && s.ID == cur.ID) return false;
                if (target != null && s.ID == target.ID) return false;
                return true;
            }
            catch { return false; }
        }

        // Un passo di guerra: ogni fazione sul fronte prova a strappare un settore a un vicino.
        static List<string> Step(bool quiet)
        {
            var events = new List<string>(); var w = W;
            var cur = PLServer.GetCurrentSector(); PLSectorInfo target = null; try { target = PLServer.GetTargetWarpSector(); } catch { }
            var all = Sectors(); if (all.Count == 0) return events;
            // fronte: (attaccante, settore conteso, difensore)
            var front = new List<(int atk, PLSectorInfo s, int def)>();
            foreach (var s in all)
            {
                if (!Contestable(s, cur, target)) continue;
                int def = s.MySPI.Faction; if (def == 3 || def == 6) continue;
                var nb = s.Neighbors; if (nb == null) continue;
                for (int i = 0; i < nb.Count; i++)
                {
                    var n = nb[i]; if (n?.MySPI == null) continue; int atk = n.MySPI.Faction;
                    if (atk != def && Array.IndexOf(Factions, atk) >= 0) front.Add((atk, s, def));
                }
            }
            if (front.Count == 0) return events;
            // offensive occasionali
            if (w.offensiveTicks > 0) { w.offensiveTicks--; if (w.offensiveTicks == 0) { events.Add($"Fronte: l'offensiva ({Name(w.offensive)}) perde slancio."); w.offensive = -1; } }
            else if (rng.NextDouble() < 0.08)
            {
                var atks = front.Select(x => x.atk).Distinct().ToList(); w.offensive = atks[rng.Next(atks.Count)]; w.offensiveTicks = 5; AddMom(w.offensive, 15);
                events.Add($"Offensiva lungo il fronte: {Name(w.offensive)} attacca su più settori.");
            }
            int attempts = 1 + (w.offensive >= 0 ? 2 : 0);
            for (int k = 0; k < attempts; k++)
            {
                // l'attaccante si sceglie in proporzione allo slancio
                var byAtk = front.GroupBy(x => x.atk).ToList(); double tot = byAtk.Sum(g => Mom(g.Key) * (g.Key == w.offensive ? 2 : 1)), r = rng.NextDouble() * tot;
                var grp = byAtk[^1]; foreach (var g in byAtk) { r -= Mom(g.Key) * (g.Key == w.offensive ? 2 : 1); if (r <= 0) { grp = g; break; } }
                var pick = grp.ElementAt(rng.Next(grp.Count()));
                double chance = Math.Clamp(0.5 + (Mom(pick.atk) - (pick.def >= 0 ? Mom(pick.def) : 30)) / 160.0, 0.15, 0.85);
                if (rng.NextDouble() > chance) { AddMom(pick.atk, -0.5f); continue; }
                Conquer(pick.s, pick.atk);
                AddMom(pick.atk, 1.5f); if (pick.def >= 0) AddMom(pick.def, -1.5f);
                w.conquests++;
                events.Add(pick.def >= 0 && pick.def != 6 ? $"Avanzata ({Name(pick.atk)}): conquistato {SectorName(pick.s)}, finora sotto {Name(pick.def)}." : $"Avanzata ({Name(pick.atk)}): occupato {SectorName(pick.s)}, finora senza padrone.");
                front.RemoveAll(x => x.s.ID == pick.s.ID);
                if (front.Count == 0) break;
            }
            // lo slancio torna lentamente verso l'equilibrio
            foreach (var f in Factions) AddMom(f, (50f - Mom(f)) * 0.03f);
            w.counts = all.Where(s => s.MySPI != null).GroupBy(s => s.MySPI.Faction).ToDictionary(g => Name(g.Key), g => g.Count());
            Saga.MarkDirty();
            return events;
        }

        static void Conquer(PLSectorInfo s, int faction)
        {
            s.MySPI.Faction = faction;
            s.ServerSetChanged(); // il delta nativo della mappa aggiorna i client
            // l'incontro del settore si rigenera con la nuova fazione, ma solo dove l'equipaggio non è ancora stato
            if (!s.Visited) try { PLEncounterManager.Instance.ClearPersistantEncounterInstanceForSector(s.ID); } catch { }
        }

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            int j = 0; try { j = (int)PLServer.Instance.NumJumps; } catch { }
            bool jumped = lastJumps >= 0 && j != lastJumps; lastJumps = j;
            if (!jumped && UnityEngine.Time.time < nextStep) return;
            nextStep = UnityEngine.Time.time + 150f + rng.Next(90);
            try
            {
                var ev = Step(false);
                foreach (var e in ev.Take(3)) News.Post("fronte", e);
                if (ev.Count > 0) Service.Add("galassia.passi.di.guerra", 1);
            }
            catch (Exception e) { Plugin.L.LogWarning("Guerra fra fazioni: " + e.Message); }
        }

        // Mentre nessuno giocava: diversi passi di guerra in un colpo solo, con un bilancio.
        public static void Offline(double hours)
        {
            if (!Host || hours < 0.5) return;
            try
            {
                var before = Sectors().Where(s => s.MySPI != null).GroupBy(s => s.MySPI.Faction).ToDictionary(g => g.Key, g => g.Count());
                int steps = (int)Math.Clamp(hours * 6, 1, 40), happened = 0;
                for (int i = 0; i < steps; i++) happened += Step(true).Count;
                var after = Sectors().Where(s => s.MySPI != null).GroupBy(s => s.MySPI.Faction).ToDictionary(g => g.Key, g => g.Count());
                var diff = Factions.Select(f => (f, d: (after.TryGetValue(f, out var a) ? a : 0) - (before.TryGetValue(f, out var b) ? b : 0))).Where(x => x.d != 0).OrderByDescending(x => x.d).ToList();
                if (diff.Count > 0) News.Post("fronte", "Mentre eravate via il fronte si è mosso: " + string.Join(", ", diff.Select(x => $"{Name(x.f)} {x.d:+0;-0} settori")) + ".", alsoBanner: false);
            }
            catch (Exception e) { Plugin.L.LogWarning("Guerra fra fazioni (assenza): " + e.Message); }
        }

        // Un contratto portato a termine per una fazione le dà slancio (e uno mancato glielo toglie).
        public static void Support(string issuer, bool success)
        {
            if (!Host) return;
            foreach (var f in Factions) if (Name(f) == issuer)
                {
                    AddMom(f, success ? 4f : -2f);
                    if (success) News.Post("fronte", $"Il vostro lavoro per {issuer} pesa sul fronte: le sue flotte guadagnano slancio.");
                }
        }

        public static string Describe()
        {
            var w = W; return $"slancio: {string.Join(", ", Factions.Select(f => $"{Name(f)} {Mom(f):0}"))} · offensiva {(w.offensive >= 0 ? Name(w.offensive) + " " + w.offensiveTicks : "nessuna")} · conquiste {w.conquests} · settori {string.Join(", ", w.counts.Select(x => x.Key + " " + x.Value))}";
        }

        // Solo per le prove: cambia la fazione di un settore scelto.
        public static string Flip(int sectorId, int faction)
        {
            var s = PLServer.GetSectorWithID(sectorId); if (s?.MySPI == null) return "settore non trovato";
            var cur = PLServer.GetCurrentSector(); if (cur != null && cur.ID == sectorId) return "è il settore corrente: non si tocca";
            int before = s.MySPI.Faction; Conquer(s, faction); return $"settore {sectorId}: {Name(before)} -> {Name(faction)}";
        }

        public static string ForceStep() { var ev = Step(false); foreach (var e in ev) News.Post("fronte", e); return ev.Count == 0 ? "nessun cambiamento" : string.Join(" | ", ev); }
    }
}

// Il traditore a bordo: dopo un po' di salti uno degli ufficiali (alle stazioni dei bot) comincia a lavorare in segreto
// per una fazione ostile o per un equipaggio concorrente. Ci sono guasti inspiegabili nei momenti peggiori, e le
// imboscate si fanno più frequenti. Ogni sabotaggio lascia un indizio; con abbastanza indizi parte un'indagine
// (incontro) che restringe i sospetti fino all'accusa. Indovinare vale un trofeo e molto morale; sbagliare costa
// un ufficiale innocente, e il traditore resta al suo posto.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class MoleState { public string last = "", masters = ""; public int clues, next = -1, caught, wrong; public bool active; public List<string> suspects = new(); }

    public static class Mole
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static MoleState M => Saga.State.mole ??= new MoleState();
        public static bool Active => Host && Saga.Loaded && M.active;
        static float nextSabotage = 300f;

        static List<Officer> Crew() => Saga.State.officers.Where(o => o.alive).ToList();
        public static Officer Traitor() => Crew().FirstOrDefault(o => o.last == M.last);

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                var m = M; int j = Obscured.Jumps();
                if (m.next < 0) { m.next = j + rng.Next(12, 22); Saga.MarkDirty(); }
                if (!m.active)
                {
                    if (j >= m.next && Crew().Count >= 3)
                    {
                        var crew = Crew(); var t = crew[rng.Next(crew.Count)];
                        var foes = Diplomacy.F.Where(f => Factions.Level(f) <= -1).Select(GalaxyWar.Name).ToList();
                        m.active = true; m.last = t.last; m.clues = 0; m.suspects = new List<string>();
                        m.masters = foes.Count > 0 && rng.NextDouble() < 0.6 ? "la " + foes[rng.Next(foes.Count)] : "un equipaggio concorrente";
                        Saga.MarkDirty(); Plugin.L.LogInfo($"[traditore] {t.first} {t.last} lavora per {m.masters}");
                    }
                    return;
                }
                if (Traitor() == null) { m.active = false; m.next = j + rng.Next(25, 40); Saga.MarkDirty(); return; } // il traditore se n'è andato da sé
                // sabotaggi: di rado, e solo fuori dalla curvatura
                if (UnityEngine.Time.time < nextSabotage) return;
                nextSabotage = UnityEngine.Time.time + 240f + rng.Next(240);
                bool warp = false; try { warp = Stations.MyShip.InWarp; } catch { }
                if (warp || rng.NextDouble() > 0.45) return;
                var id = Stations.MyShip.ShipID; var sy = Stations.SysIds[rng.Next(Stations.SysIds.Length)];
                Stations.Set(id, "dmx_" + sy, Math.Min(90f, Stations.Get(id, "dmx_" + sy) + 25));
                m.clues++; Saga.MarkDirty();
                Director.Notify("GUASTO INSPIEGABILE", m.clues == 1 ? $"Un guasto ai {Label(sy)} senza nessuna causa apparente. L'Ingegneria non ci capisce niente." : m.clues == 2 ? $"Ancora un guasto ai {Label(sy)}, troppo preciso per essere un caso. Qualcuno, a bordo, sta sabotando la nave." : $"Un altro sabotaggio ai {Label(sy)}. Gli indizi si accumulano.");
                if (m.clues >= 2 && !Saga.State.pending.Any(x => x.kind == "incontro" && x.name == "indagine")) Saga.Later("incontro", 0, 1, "indagine");
            }
            catch (Exception e) { Plugin.L.LogWarning("Traditore: " + e.Message); }
        }
        static string Label(string sys) => sys switch { "shields" => "generatori degli scudi", "weapons" => "sistemi d'arma", "engines" => "motori", "sensors" => "sensori", "warp" => "nucleo di curvatura", _ => "sistemi di supporto vitale" };

        // Sospettati: il traditore e qualcun altro; con più indizi i sospetti si restringono.
        public static List<Officer> Suspects()
        {
            var t = Traitor(); if (t == null) return new List<Officer>();
            var others = Crew().Where(o => o.last != t.last).OrderBy(o => (o.last.GetHashCode() ^ M.clues) & 0xffff).ToList();
            int n = M.clues >= 5 ? 0 : M.clues >= 3 ? 1 : 2;
            return others.Take(n).Append(t).OrderBy(o => o.last).ToList();
        }

        public static string Accuse(Officer o)
        {
            var m = M; var t = Traitor(); if (o == null || t == null) return "";
            if (o.last == t.last)
            {
                m.active = false; m.caught++; m.next = Obscured.Jumps() + rng.Next(30, 50); Officers.Replace(t); Saga.MarkDirty();
                Morale.Change(10, "Il traditore è stato smascherato"); Trophies.Add($"Smascherato il traditore: {t.first} {t.last}", "indagine");
                News.Post("dicerie", $"A bordo della «{(string)Stations.MyShip.ShipName}» è stato arrestato un ufficiale che lavorava per {m.masters}.");
                Saga.Chronicle($"Smascherato il traditore: {t.first} {t.last}, al soldo di {m.masters}.");
                return $"{t.first} {t.last} crolla al primo interrogatorio: lavorava per {m.masters}. Al primo porto lascia la nave in manette.";
            }
            m.wrong++; m.clues = Math.Max(0, m.clues - 1); Officers.Replace(o); Saga.MarkDirty();
            if (m.clues >= 2) Saga.Later("incontro", 2, 4, "indagine");
            Morale.Change(-10, "Abbiamo accusato un innocente");
            return $"{o.first} {o.last} era innocente. Lascia la nave senza una parola, e i sabotaggi continuano.";
        }

        public static string Describe() { var m = M; return m.active ? $"traditore attivo: {m.last} per {m.masters}, indizi {m.clues}, sospettati {string.Join(", ", Suspects().Select(o => o.last))}" : $"nessun traditore; il prossimo dal salto {m.next}; smascherati {m.caught}, accuse sbagliate {m.wrong}"; }
        public static string Force() { var m = M; m.next = Obscured.Jumps(); Tick(); if (m.active) { m.clues = 2; nextSabotage = 0; } return Describe(); }
    }
}

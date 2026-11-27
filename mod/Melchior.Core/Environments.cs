// Ambienti stellari: circa un settore su tre ha condizioni proprie, fisse per galassia e settore. Agiscono finché la nave
// resta nel settore (fuori dalla curvatura) e le stazioni giuste le attenuano:
// - nebulosa ionica: sensori disturbati (Scienza), ma le imboscate tardano ad arrivare;
// - campo di asteroidi: colpi allo scafo (Pilota); l'Ingegneria raccoglie minerali;
// - radiazioni stellari: supporto vitale danneggiato se gli scudi sono bassi (Ingegneria);
// - tempesta di plasma: scudi che si scaricano e stress del nucleo (Scienza);
// - pozzo gravitazionale: motori e curvatura sotto sforzo (Pilota);
// - corrente subspaziale: motori e curvatura si riprendono;
// - quiete assoluta: l'equipaggio riposa, piccole riparazioni, il direttore concede più calma.
using System;
using System.Linq;

namespace Melchior
{
    public static class Environments
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static readonly (string kind, double w, string name, string text, string station)[] Kinds =
        {
            ("nebulosa", 3, "nebulosa ionica", "Siamo dentro una nebulosa ionica: i sensori vedono poco, ma anche chi ci cerca.", "Scienza"),
            ("asteroidi", 3, "campo di asteroidi", "Campo di asteroidi: piccoli impatti sullo scafo finché restiamo qui. Con l'Ingegneria si raccoglie qualche minerale.", "Pilota"),
            ("radiazioni", 2, "radiazioni stellari", "Una stella instabile inonda il settore di radiazioni: con gli scudi bassi ne soffre il supporto vitale.", "Ingegneria"),
            ("plasma", 2, "tempesta di plasma", "Tempesta di plasma: gli scudi si scaricano e il nucleo lavora sotto stress.", "Scienza"),
            ("gravita", 1.5, "pozzo gravitazionale", "Un pozzo gravitazionale tira la nave: motori e curvatura sotto sforzo.", "Pilota"),
            ("corrente", 1.2, "corrente subspaziale", "Una corrente subspaziale avvolge la nave: motori e curvatura si riprendono da soli.", ""),
            ("quiete", 1.2, "quiete assoluta", "Quiete assoluta: niente rotte, niente segnali. L'equipaggio respira.", ""),
        };

        public static (string kind, string name, string text, string station)? EnvOf(int seed, int sector)
        {
            if (Wonders.Remnant(sector)) return ("radiazioni", "resti di supernova", "I resti della supernova inondano il settore di radiazioni dure: con gli scudi bassi ne soffre il supporto vitale.", "Ingegneria");
            var r = new System.Random(unchecked(seed * 17 + sector * 104729 + 3));
            if (r.NextDouble() > 0.33) return null;
            double tot = Kinds.Sum(k => k.w), x = r.NextDouble() * tot; var pick = Kinds[^1];
            foreach (var k in Kinds) { x -= k.w; if (x <= 0) { pick = k; break; } }
            return (pick.kind, pick.name, pick.text, pick.station);
        }

        static int Seed() { try { return (int)PLServer.Instance.GalaxySeed; } catch { return 0; } }
        static int lastSector = -1, ticks; static float nextTick, announceAt = -1; static string here = "";
        public static string Here => Host ? here : GalaxyView.Current?.env ?? "";
        static (string kind, string name, string text, string station)? Cur() { try { var s = PLServer.GetCurrentSector(); return s == null || s.VisualIndication != ESectorVisualIndication.NONE ? null : EnvOf(Seed(), s.ID); } catch { return null; } }

        static bool Manned(int cls) { try { return PLServer.Instance.GetCachedFriendlyPlayerOfClass(cls, Stations.MyShip) != null; } catch { return false; } }
        static float G(int id, string k) => Stations.Get(id, k);
        static void Push(int id, string sys, float pts, float cap = 60f) { string k = "dmx_" + sys; float v = G(id, k); if (pts > 0 && v >= cap) return; Stations.Set(id, k, Math.Clamp(v + pts, 0.01f, pts > 0 ? cap : 90f)); }

        // Il direttore lascia più calma nella nebulosa e nella quiete.
        public static float CalmFactor() { var e = Cur(); return e?.kind switch { "nebulosa" => 1.3f, "quiete" => 1.5f, "plasma" => 0.9f, _ => 1f }; }

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                var ship = Stations.MyShip; int id = ship.ShipID; bool warp = false; try { warp = ship.InWarp; } catch { }
                int sec = -1; try { sec = PLServer.GetCurrentSector()?.ID ?? -1; } catch { }
                if (warp) return;
                var e = Cur();
                if (sec != lastSector) { lastSector = sec; ticks = 0; here = e?.name ?? ""; GalaxyView.Dirty(); if (e != null) announceAt = UnityEngine.Time.time + 3f; nextTick = UnityEngine.Time.time + 25f; }
                if (e == null) return;
                if (announceAt > 0 && UnityEngine.Time.time >= announceAt)
                {
                    announceAt = -1;
                    Codex.Phenomenon(e.Value.kind);
                    Director.Notify("AMBIENTE · " + e.Value.name.ToUpperInvariant(), e.Value.text + (e.Value.station.Length > 0 ? $" ({e.Value.station}: attenua gli effetti.)" : ""));
                    if (e.Value.kind == "quiete") Morale.Change(2, null);
                }
                if (UnityEngine.Time.time < nextTick) return;
                nextTick = UnityEngine.Time.time + 20f; ticks++;
                var st = ship.MyStats;
                switch (e.Value.kind)
                {
                    case "nebulosa": Push(id, "sensors", Manned(2) ? 2 : 5, Manned(2) ? 30 : 50); break;
                    case "asteroidi":
                        if (rng.NextDouble() < (Manned(1) ? 0.1 : 0.3) * Shipyard.Less("motori")) { float d = st.HullMax * 0.015f; st.HullCurrent = Math.Max(1f, st.HullCurrent - d); Stations.LogEvent(id, $"Impatto di un asteroide: scafo -{d:0}"); }
                        if (Manned(4) && rng.NextDouble() < 0.25) { int cr = 60 + rng.Next(80); try { PLServer.Instance.CurrentCrewCredits += cr; } catch { } Stations.LogEvent(id, $"Ingegneria: minerali raccolti dagli asteroidi, +{cr} crediti"); }
                        break;
                    case "radiazioni":
                    {
                        float sh = st.ShieldsMax > 0 ? st.ShieldsCurrent / st.ShieldsMax : 0;
                        if (sh < 0.6f) Push(id, "life", Manned(4) ? 2 : 5);
                        break;
                    }
                    case "plasma":
                        st.ShieldsCurrent = Math.Max(0f, st.ShieldsCurrent - st.ShieldsMax * (Manned(2) ? 0.04f : 0.1f) * Shipyard.Less("motori"));
                        Stations.Set(id, "core_stress", Math.Min(85f, G(id, "core_stress") + (Manned(2) ? 2 : 4)));
                        break;
                    case "gravita": if (rng.NextDouble() < Shipyard.Less("motori")) { Push(id, "engines", Manned(1) ? 2 : 4); Push(id, "warp", Manned(1) ? 1 : 3); } break;
                    case "corrente": Push(id, "engines", -6); Push(id, "warp", -6); break;
                    case "quiete":
                        foreach (var sy in Stations.SysIds) Push(id, sy, -3);
                        if (ticks % 3 == 0) Morale.Change(1, null);
                        break;
                }
            }
            catch (Exception ex) { Plugin.L.LogWarning("Ambienti: " + ex.Message); nextTick = UnityEngine.Time.time + 60f; }
        }

        public static string Describe() { var s = PLServer.GetCurrentSector(); var e = Cur(); return $"settore {s?.ID}: {(e == null ? "nessun ambiente particolare" : e.Value.name + " — " + e.Value.text)}"; }
    }
}

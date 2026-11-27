// Spirito dell'equipaggio e fama nei porti.
// - Morale (0-100): sale con vittorie, scelte onorevoli, contratti onorati e licenze a terra (attracco a una stazione);
//   scende con fughe, contratti mancati, scelte crudeli, danni gravi e lunghi viaggi senza approdo.
//   Alto: l'iniziativa del capitano cresce più in fretta e l'equipaggio ripara da sé i piccoli guasti.
//   Basso: manutenzione trascurata; molto basso: rischio di ammutinamento a bordo (un incontro da gestire).
// - Fama: un titolo che riassume onore e infamia, citato da chi vi incontra.
using System;
using System.Linq;

namespace Melchior
{
    public static class Morale
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static int lastSector = -1, lastJumps = -1, lastMutiny = -99; static float nextEffect = 120f, portAt = -1; static bool hurtLogged;

        public static int Value => Host ? Saga.State.morale : GalaxyView.Current?.morale ?? 60;
        public static string Level(int m) => m >= 80 ? "Alle stelle" : m >= 60 ? "Buono" : m >= 40 ? "Stanco" : m >= 20 ? "Malumore" : "Sull'orlo della rivolta";
        public static float InitiativeFactor => Value >= 80 ? 1.5f : Value >= 60 ? 1.15f : Value >= 40 ? 1f : 0.7f;

        public static void Change(int d, string why)
        {
            if (!Host || !Saga.Loaded || d == 0) return;
            if (d > 0) d = (int)Math.Round(d * Talents.Mul("carisma", 0.2f) * (1f + Loot.Mine("morale") + PawnLoot.Crew("carisma")));
            if (d < 0 && Species.Accord("velnari")) d = d / 2; // i consiglieri dei Vel'nari
            var s = Saga.State; int before = s.morale; s.morale = Math.Clamp(s.morale + d, 0, 100);
            Saga.MarkDirty(); GalaxyView.Dirty();
            if (Level(before) != Level(s.morale))
                Director.Notify("EQUIPAGGIO", $"Morale: {Level(s.morale).ToLowerInvariant()}." + (why != null ? " " + why + "." : "") + (s.morale >= 80 ? " L'iniziativa cresce in fretta e i piccoli guasti si riparano da soli." : s.morale < 20 ? " Si parla apertamente di rivolta." : s.morale < 40 ? " La manutenzione comincia a risentirne." : ""));
        }

        // Fama nei porti, da onore e infamia.
        public static string Title()
        {
            int o = Saga.State.onore, i = Saga.State.infamia;
            if (o >= 15 && i >= 15) return "Canaglie di buon cuore";
            if (o >= 30) return "Leggende della Frontiera"; if (i >= 30) return "Flagello del Margine";
            if (o >= 15) return "Eroi della Frontiera"; if (i >= 15) return "Predoni senza bandiera";
            if (o >= 6) return "Equipaggio rispettato"; if (i >= 6) return "Gente da evitare";
            return "Sconosciuti";
        }
        public static string Fame() => Host ? Title() : GalaxyView.Current?.fame ?? "";

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                var ship = Stations.MyShip; var sec = PLServer.GetCurrentSector(); int id = sec?.ID ?? -1;
                bool warp = false; try { warp = ship.InWarp; } catch { }
                // licenza a terra: attracco a una stazione, un negozio, un luogo abitato
                if (id >= 0 && id != lastSector && !warp)
                {
                    if (lastSector >= 0 && sec.VisualIndication != ESectorVisualIndication.NONE) { Change(6 + 2 * Shipyard.Level("alloggi"), "Licenza a terra per tutti"); Saga.State.sinceRest = 0; if (rng.NextDouble() < 0.6) portAt = UnityEngine.Time.time + 10f; }
                    lastSector = id;
                }
                if (portAt > 0 && UnityEngine.Time.time >= portAt) { portAt = -1; Encounters.StartPort(); }
                int j = Obscured.Jumps();
                if (lastJumps >= 0 && j != lastJumps) { Saga.State.sinceRest++; if (Saga.State.sinceRest > 6 + 3 * Shipyard.Level("alloggi") && Saga.State.morale > 35) Change(-1, "Troppi salti senza toccare un porto"); Saga.MarkDirty(); }
                lastJumps = j;
                // danni gravi
                try { var st = ship.MyStats; bool low = st.HullCurrent < st.HullMax * 0.3f; if (low && !hurtLogged) { hurtLogged = true; Change(-3, "Lo scafo è a pezzi"); } else if (!low && st.HullCurrent > st.HullMax * 0.6f) hurtLogged = false; } catch { }
                if (UnityEngine.Time.time < nextEffect) return;
                nextEffect = UnityEngine.Time.time + 120f + rng.Next(60);
                int m = Saga.State.morale;
                if (m >= 80)
                {
                    bool fixedAny = false;
                    foreach (var sy in Stations.SysIds) { float d = Stations.Get(ship.ShipID, "dmx_" + sy); if (d > 1) { Stations.Set(ship.ShipID, "dmx_" + sy, Math.Max(0.01f, d - 10)); fixedAny = true; } }
                    if (fixedAny) Stations.LogEvent(ship.ShipID, "Equipaggio di buon umore: piccoli guasti riparati senza che nessuno lo chiedesse");
                }
                else if (m < 40 && rng.NextDouble() < (m < 20 ? 0.6 : 0.35))
                {
                    var sy = Stations.SysIds[rng.Next(Stations.SysIds.Length)];
                    Stations.Set(ship.ShipID, "dmx_" + sy, Math.Min(90f, Stations.Get(ship.ShipID, "dmx_" + sy) + 12));
                    Stations.LogEvent(ship.ShipID, "Malumore a bordo: manutenzione trascurata");
                }
                if (m < 15 && j - lastMutiny >= 8 && Encounters.Start("rivolta")) lastMutiny = j;
            }
            catch (Exception e) { Plugin.L.LogWarning("Morale: " + e.Message); }
        }

        public static string Describe() => $"morale {Saga.State.morale} ({Level(Saga.State.morale)}), salti senza porto {Saga.State.sinceRest}, fama: {Title()} (onore {Saga.State.onore}, infamia {Saga.State.infamia})";
    }
}

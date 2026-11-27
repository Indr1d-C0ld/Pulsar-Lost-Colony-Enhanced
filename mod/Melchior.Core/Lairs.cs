// Covi dei pirati: i pirati e i trafficanti di grado alto fondano un covo in un settore e lo fortificano col tempo.
// I covi rendono più frequenti gli assalti ai convogli. Sulla bacheca compaiono contratti di assalto (rotta tracciata):
// all'arrivo ci sono le difese del covo e, se il capo è in zona, il capo in persona. Espugnarlo vale un grosso bottino,
// toglie il covo dalla galassia e fa perdere un grado al suo capo.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class Lair { public string owner = "", name = ""; public int sector = -1, strength = 1; }

    public static class Lairs
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static List<Lair> All => Saga.State.lairs ??= new List<Lair>();
        public static IEnumerable<Lair> List => All;
        static readonly string[] Names = { "il Nido", "la Tana", "il Porto Franco", "la Rocca", "il Ricovero", "l'Approdo Nero", "la Fortezza di Ruggine", "il Covo delle Ombre" };
        static float next = 240f; static int lastJumps = -1;
        static string Ship() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                Track();
                int j = Obscured.Jumps(); bool jumped = lastJumps >= 0 && j != lastJumps; lastJumps = j;
                if (!jumped && UnityEngine.Time.time < next) return;
                next = UnityEngine.Time.time + 300f + rng.Next(180);
                // i covi dei capi spariti passano di mano o si svuotano
                foreach (var l in All.ToList()) { var f = Nemesis.Find(l.owner); if (f == null || !f.alive) { All.Remove(l); News.Post("taglie", $"Nel settore {Places.Of(l.sector)} non c'è più nessuno a difendere {l.name}: il suo capo è uscito di scena."); } }
                // nuovi covi e fortificazioni
                foreach (var f in Nemesis.Alive.Where(x => x.role != "ribelle" && x.rank >= 3).ToList())
                {
                    var l = All.FirstOrDefault(x => x.owner == f.Full);
                    if (l == null && rng.NextDouble() < 0.4 && f.sector >= 0)
                    {
                        var used = All.Select(x => x.name).ToHashSet(); var names = Names.Where(n => !used.Contains(n)).ToList(); if (names.Count == 0) names = Names.ToList();
                        l = new Lair { owner = f.Full, sector = f.sector, name = names[rng.Next(names.Count)], strength = 1 }; All.Add(l);
                        News.Post("taglie", $"{f.Full} ha messo su un covo nel settore {Places.Of(l.sector)}: nei porti lo chiamano {l.name}. I convogli della zona cambiano rotta.");
                    }
                    else if (l != null && l.strength < 5 && rng.NextDouble() < 0.3) { l.strength++; if (l.strength == 3) News.Post("taglie", $"Nuove difese nel settore {Places.Of(l.sector)}: {f.Full} fortifica {l.name}."); }
                }
                Saga.MarkDirty(); GalaxyView.Dirty();
            }
            catch (Exception e) { Plugin.L.LogWarning("Covi: " + e.Message); }
        }
        public static string Cap(string s) => char.ToUpper(s[0]) + s[1..];
        // I covi attivi rendono più frequenti gli assalti dei predoni ai convogli.
        public static float RaidFactor => 1f + Math.Min(1f, All.Sum(l => l.strength) * 0.1f);

        // ---- assalto (contratto "covo" accettato: Saga.State.contract.kind == "covo")
        static readonly List<int> foes = new(); static int foeSector = -1; static bool fighting;
        static void Track()
        {
            var c = Saga.State.contract; if (c == null || c.kind != "covo") { fighting = false; return; }
            var l = All.FirstOrDefault(x => x.name == c.target);
            if (l == null) { Contracts.EndLair(false, "il covo non esiste più"); return; }
            var ship = Stations.MyShip; bool warp = false; try { warp = ship.InWarp; } catch { }
            int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { }
            if (Obscured.Jumps() > c.expires && !fighting) { Contracts.EndLair(false, "troppo tempo è passato"); return; }
            if (!fighting && sec == l.sector && !warp)
            {
                float p = Director.Power(ship, out _, out _); foes.Clear(); foeSector = sec;
                int n = 1 + l.strength; for (int i = 0; i < Math.Min(5, n); i++) { var s = Director.Spawn(ship, Director.PickShip(p + l.strength, i == 0 ? 1 : 0), Director.Modifiers(p + l.strength), "Difese del covo"); if (s != null) foes.Add(s.ShipID); }
                var f = Nemesis.Find(l.owner);
                bool boss = f != null && f.alive && f.sector == l.sector && Nemesis.Engaged == null;
                if (boss) Nemesis.Appear(ship, p, f, false);
                if (foes.Count == 0) return;
                fighting = true;
                Director.Notify("ASSALTO AL COVO", $"{Cap(l.name)}: {foes.Count} navi di difesa ci vengono incontro" + (boss ? $", e {f.Full} è in casa." : "."));
                return;
            }
            if (!fighting) return;
            if (sec != foeSector || warp) { fighting = false; foes.Clear(); return; } // ritirata: si può tornare finché il contratto è valido
            foes.RemoveAll(id => { PLShipInfoBase s = null; try { s = PLEncounterManager.Instance.GetShipFromID(id); } catch { } return s == null || s.HasBeenDestroyed || (s.MyStats != null && s.MyStats.HullCurrent <= 0f); });
            if (foes.Count > 0) return;
            fighting = false; All.Remove(l); Saga.MarkDirty(); GalaxyView.Dirty();
            var owner = Nemesis.Find(l.owner); if (owner != null && owner.alive && owner.rank > 1) { owner.rank--; owner.grudge += 3; }
            var got = Loot.Grant(ship, 3 + Math.Min(2, l.strength / 2), 5 + Math.Min(2, l.strength / 2), "covo");
            int cr = 1000 + l.strength * 600; try { PLServer.Instance.CurrentCrewCredits += cr; } catch { }
            Director.Notify("COVO ESPUGNATO", $"Abbiamo espugnato {l.name}. Nei magazzini: {cr} crediti" + (got != null ? $" e {got.Name}" : "") + ".");
            News.Post("taglie", $"La «{Ship()}» ha espugnato {l.name}, il covo di {l.owner}.");
            Trophies.Add($"Chiavi {Of(l.name)}", "covo espugnato"); if (rng.NextDouble() < 0.35) Bases.GiveDeed(Bases.RandomKind(), "trovato fra i documenti del covo");
            Contracts.EndLair(true, null);
        }
        static string Of(string n) => n.StartsWith("la ") ? "della " + n[3..] : n.StartsWith("il ") ? "del " + n[3..] : n.StartsWith("l'") ? "dell'" + n[2..] : "di " + n;

        public static string Describe() => All.Count == 0 ? "nessun covo" : string.Join("; ", All.Select(l => $"{l.name} di {l.owner}, settore {Places.Of(l.sector)}, forza {l.strength}"));
        public static string Force()
        {
            var f = Nemesis.Alive.Where(x => x.role != "ribelle").OrderByDescending(x => x.rank).FirstOrDefault(); if (f == null) return "nessun personaggio";
            var l = new Lair { owner = f.Full, sector = PLServer.GetCurrentSector().ID, name = Names[rng.Next(Names.Length)], strength = 2 }; All.Add(l); Saga.MarkDirty(); return Describe();
        }
    }
}

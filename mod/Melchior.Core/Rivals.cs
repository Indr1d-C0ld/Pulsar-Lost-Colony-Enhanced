// Fazione rivale persistente: vive sul server (Balthasar), una per galassia. L'host ne legge nome, capo, forza e rancore,
// mostra all'equipaggio le notizie arrivate nel frattempo, la fa comparire come evento del direttore
// e riferisce al server l'esito di ogni scontro (vittoria se le sue navi vengono distrutte, ritirata se l'equipaggio salta via).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BepInEx;

namespace Melchior
{
    public class RivalNews { public long t; public string text = ""; }
    public class RivalInfo
    {
        public string name = "", leader = "", temper = ""; public int strength, grudge, encounters, defeats, victories; public long now;
        public List<RivalNews> news = new();
    }

    public static class RivalForce
    {
        static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
        static readonly JsonSerializerOptions json = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static readonly System.Random rng = new();
        static RivalInfo cur; static int curSeed = int.MinValue; static long nextFetch, nextCheck; static bool busy;
        static readonly Queue<string> pending = new(), newsQueue = new();
        static Dictionary<string, long> seen = new(); // seme -> ultima notizia mostrata
        static readonly List<int> squad = new(); static int leaderShip = -1, squadSector = -1, killed; static bool leaderDown;
        public static RivalInfo Current => cur;

        static string SeenPath => Path.Combine(Paths.ConfigPath, Plugin.PerInstance("melchior.rivals.json"));

        static int Seed() { try { return (int)PLServer.Instance.GalaxySeed; } catch { return int.MinValue; } }

        public static void Tick()
        {
            long now = clock.ElapsedMilliseconds; if (now < nextCheck) return; nextCheck = now + 3000;
            try
            {
                lock (pending) while (pending.Count > 0) Director.Notify("RAPPORTI DI INTELLIGENCE", pending.Dequeue());
                lock (pending) while (newsQueue.Count > 0) News.Post("rivali", newsQueue.Dequeue());
                if (!PhotonNetwork.inRoom || !PhotonNetwork.isMasterClient || PLServer.Instance == null || Stations.MyShip == null) return;
                int seed = Seed(); if (seed == int.MinValue) return;
                if (seed != curSeed) { curSeed = seed; cur = null; nextFetch = 0; squad.Clear(); }
                if (now >= nextFetch && !busy) { nextFetch = now + 600000; Fetch(seed); }
                Track();
            }
            catch (Exception e) { Plugin.L.LogWarning("Fazione rivale: " + e.Message); }
        }

        static void Fetch(int seed)
        {
            if (seen.Count == 0) try { seen = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(SeenPath)) ?? new(); } catch { seen = new(); }
            long since = seen.TryGetValue(seed.ToString(), out var s) ? s : 0;
            string url = $"{Service.Base}/api/rival?seed={seed}&since={since}"; busy = true;
            Task.Run(async () =>
            {
                try
                {
                    var info = JsonSerializer.Deserialize<RivalInfo>(await http.GetStringAsync(url), json);
                    if (info == null) return;
                    cur = info;
                    lock (pending) foreach (var n in info.news) { pending.Enqueue(n.text); newsQueue.Enqueue(n.text); }
                    if (info.news.Count > 0) { seen[seed.ToString()] = info.news.Max(n => n.t); try { File.WriteAllText(SeenPath, JsonSerializer.Serialize(seen)); } catch { } }
                    Plugin.L.LogInfo($"[rivali] {info.name}, guidati da {info.leader}: forza {info.strength}, rancore {info.grudge}");
                }
                catch (Exception e) { Plugin.L.LogWarning("Fazione rivale non raggiungibile: " + e.Message); }
                finally { busy = false; }
            });
        }

        static void Report(string outcome)
        {
            int seed = curSeed; string body = JsonSerializer.Serialize(new { outcome, killed, leader = leaderDown });
            string url = $"{Service.Base}/api/rival?seed={seed}";
            Task.Run(async () =>
            {
                try { await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json")); nextFetch = 0; }
                catch (Exception e) { Plugin.L.LogWarning("Esito dello scontro non inviato: " + e.Message); }
            });
            Plugin.L.LogInfo($"[rivali] esito {outcome}: {killed} navi distrutte{(leaderDown ? ", capo abbattuto" : "")}");
        }

        // Una squadra della fazione: più numerosa con la forza, guidata dal capo in persona quando il rancore è alto.
        public static bool Spawn(PLShipInfo ship, float power)
        {
            if (cur == null || squad.Count > 0) return false;
            float f = cur.strength / 100f;
            int count = 1 + (cur.strength >= 40 ? 1 : 0) + (cur.strength >= 75 ? 1 : 0) + (power >= 12 ? 1 : 0);
            bool withLeader = cur.grudge >= 35 || cur.encounters >= 3;
            string tag = cur.name.Split(' ')[0];
            killed = 0; leaderDown = false; leaderShip = -1;
            for (int i = 0; i < count; i++)
            {
                bool lead = withLeader && i == 0;
                var type = Director.PickShip(power + f * 6f, lead ? 1 : -rng.Next(2));
                int mods = Director.Modifiers(power + f * 8f) | (lead ? 1 << (int)EShipModifierType.ELITE : 0);
                var s = Director.Spawn(ship, type, mods, lead ? cur.leader : $"{tag} {ToRoman(i + 1 + cur.encounters)}");
                if (s == null) continue;
                squad.Add(s.ShipID); if (lead) leaderShip = s.ShipID;
            }
            if (squad.Count == 0) return false;
            try { squadSector = PLServer.GetCurrentSector().ID; } catch { squadSector = -1; }
            string who = leaderShip >= 0 ? $"{cur.leader} in persona guida {squad.Count} navi di {cur.name}" : $"{squad.Count} navi di {cur.name}";
            Director.Notify("ALLARME DI PROSSIMITÀ", $"{who}. Ci stanno dando la caccia.");
            return true;
        }

        static void Track()
        {
            if (squad.Count == 0) return;
            int sector = -1; try { sector = PLServer.GetCurrentSector().ID; } catch { }
            bool warp = false; try { warp = Stations.MyShip.InWarp; } catch { }
            if (warp) { squad.Clear(); Report("ritirata"); return; } // le navi spariscono col salto: non sono state abbattute
            var alive = new List<int>();
            foreach (int id in squad)
            {
                PLShipInfoBase s = null; try { s = PLEncounterManager.Instance.GetShipFromID(id); } catch { }
                bool dead = s == null || s.HasBeenDestroyed; try { if (!dead && s.MyStats != null && s.MyStats.HullCurrent <= 0f) dead = true; } catch { }
                if (!dead) alive.Add(id);
                else if (sector == squadSector) { killed++; if (id == leaderShip) leaderDown = true; }
            }
            if (sector != squadSector && alive.Count > 0) { squad.Clear(); Report("ritirata"); return; }
            squad.Clear(); squad.AddRange(alive);
            if (alive.Count == 0)
            {
                var got = Loot.Grant(Stations.MyShip, leaderDown ? 5 : 2, leaderDown ? 6 : 5, leaderDown ? "capo rivale" : "rivali");
                if (got != null) Director.Notify("RITROVAMENTO", (leaderDown ? "Tra i rottami dell'ammiraglia nemica: " : "Recuperato dai rottami della squadra rivale: ") + got.Name + ".");
                Report("vittoria"); Service.Add("rivali.vittorie", 1); Director.Notify("RAPPORTO DI PLANCIA", leaderDown ? "Il capo nemico è stato abbattuto. La sua flotta perderà terreno, ma qualcuno ne prenderà il posto." : $"Squadra di {cur?.name} distrutta."); }
        }

        static string ToRoman(int n) { string[] r = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" }; return n >= 1 && n <= 10 ? r[n] : n.ToString(); }

        public static string Describe() => cur == null ? (busy ? "in attesa del server" : "nessuna fazione letta") : $"{cur.name}, capo {cur.leader} ({cur.temper}), forza {cur.strength}, rancore {cur.grudge}, scontri {cur.encounters}, squadra attiva {squad.Count}";
    }
}

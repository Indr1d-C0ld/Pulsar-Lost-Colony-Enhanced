// Annali della flotta: le imprese maggiori di ogni equipaggio (minaccia sconfitta, luoghi leggendari, onorificenze,
// accordi con le specie, grandi nemici abbattuti, una casa resa prospera) vengono registrate per sempre su Balthasar.
// Nelle galassie nuove quegli equipaggi diventano leggenda: se ne parla nei porti, e si trovano i relitti delle loro navi.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Melchior
{
    public class LegacyEntry { public long t; public long seed; public string ship = "", deed = "", kind = ""; }

    public static class Legacy
    {
        static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
        static readonly JsonSerializerOptions json = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static List<LegacyEntry> others = new(); static int fetchedSeed = int.MinValue; static bool busy;
        public static IReadOnlyList<LegacyEntry> Others => others;
        public static bool Any => others.Count > 0;
        static int Seed() { try { return (int)PLServer.Instance.GalaxySeed; } catch { return 0; } }

        public static void Record(string kind, string deed)
        {
            if (!Host || !Saga.Loaded) return;
            int seed = Seed(); string ship = "equipaggio"; try { ship = (string)Stations.MyShip.ShipName; } catch { }
            string body = JsonSerializer.Serialize(new { ship, deed, kind });
            Task.Run(async () => { try { await http.PostAsync($"{Service.Base}/api/legacy?seed={seed}", new StringContent(body, Encoding.UTF8, "application/json")); } catch (Exception e) { Plugin.L.LogWarning("Annali non aggiornati: " + e.Message); } });
            Plugin.L.LogInfo($"[annali] {ship}: {deed}");
        }

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || busy) return;
            int seed = Seed(); if (seed == fetchedSeed) return;
            fetchedSeed = seed; busy = true;
            Task.Run(async () =>
            {
                try
                {
                    var all = JsonSerializer.Deserialize<List<LegacyEntry>>(await http.GetStringAsync($"{Service.Base}/api/legacy"), json) ?? new();
                    others = all.Where(x => x.seed != seed).ToList();
                    Plugin.L.LogInfo($"[annali] {others.Count} imprese di altri equipaggi");
                }
                catch (Exception e) { Plugin.L.LogWarning("Annali non letti: " + e.Message); fetchedSeed = int.MinValue; }
                finally { busy = false; }
            });
        }

        public static LegacyEntry Pick() => others.Count == 0 ? null : others[rng.Next(others.Count)];
        public static string Describe() => others.Count == 0 ? "nessuna leggenda da altre galassie" : string.Join("\n      ", others.TakeLast(10).Select(x => $"«{x.ship}»: {x.deed}").Prepend($"leggende: {others.Count}"));
    }
}

// Sfida della settimana: Balthasar propone la stessa sfida a tutti gli equipaggi della rete di casa (dipende solo dal
// numero della settimana). L'host segue i progressi dai contatori dello stato di servizio, li invia al server e, a
// traguardo raggiunto, riceve il premio. La classifica della settimana si vede nel Giornale e nel pannello di Balthasar.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Melchior
{
    public class ChallengeState { public int week = -1, progress; public string key = ""; public bool done; }

    public static class Challenge
    {
        class Info { public int week, target; public string key = "", label = ""; public long ends; public List<Row> board = new(); }
        class Row { public long seed; public string ship = ""; public int progress; public bool done; }
        static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
        static readonly JsonSerializerOptions json = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static Info info; static float nextFetch, nextPost; static bool busy, pending;
        static ChallengeState S => Saga.State.challenge ??= new ChallengeState();

        // Ogni contatore dello stato di servizio passa di qui (Service.Add).
        public static void OnStat(string id, double v)
        {
            if (info == null || !Host || !Saga.Loaded || v <= 0 || id != info.key || S.week != info.week || S.done) return;
            S.progress = Math.Min(info.target, S.progress + (int)Math.Round(v)); pending = true; Saga.MarkDirty(); GalaxyView.Dirty();
            if (S.progress >= info.target) Complete();
        }

        static void Complete()
        {
            S.done = true; var ship = Stations.MyShip;
            var got = Loot.Grant(ship, 4, 6, "sfida della settimana"); try { PLServer.Instance.CurrentCrewCredits += 2500; } catch { }
            Competitors.Renown(50); Morale.Change(5, null);
            Trophies.Add($"Sfida della settimana dal {DateTimeOffset.FromUnixTimeMilliseconds(info.ends).AddDays(-7).ToLocalTime():dd/MM/yyyy}: {info.label.ToLowerInvariant()}", "sfida della settimana");
            Director.Notify("SFIDA DELLA SETTIMANA", $"Traguardo raggiunto: {info.label.ToLowerInvariant()}. 2500 crediti dal circolo dei liberi equipaggi" + (got != null ? $" e {got.Name}" : "") + ".");
            Saga.Chronicle($"Sfida della settimana completata: {info.label.ToLowerInvariant()}.");
        }

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            float now = UnityEngine.Time.time;
            if (now >= nextFetch && !busy) { nextFetch = now + 600f; Fetch(); }
            if (pending && now >= nextPost && !busy && info != null) { pending = false; nextPost = now + 10f; Post(); }
        }

        static void Fetch()
        {
            busy = true;
            Task.Run(async () =>
            {
                try
                {
                    var i = JsonSerializer.Deserialize<Info>(await http.GetStringAsync($"{Service.Base}/api/challenge"), json);
                    if (i != null && i.key.Length > 0) { info = i; if (S.week != i.week || S.key != i.key) { S.week = i.week; S.key = i.key; S.progress = 0; S.done = false; Saga.MarkDirty(); } pending = true; GalaxyView.Dirty(); }
                }
                catch (Exception e) { Plugin.L.LogWarning("Sfida della settimana non letta: " + e.Message); }
                finally { busy = false; }
            });
        }

        static void Post()
        {
            int seed = 0; try { seed = (int)PLServer.Instance.GalaxySeed; } catch { }
            string ship = "equipaggio"; try { ship = (string)Stations.MyShip.ShipName; } catch { }
            string body = JsonSerializer.Serialize(new { week = S.week, key = S.key, progress = S.progress, ship }); busy = true;
            Task.Run(async () =>
            {
                try { await http.PostAsync($"{Service.Base}/api/challenge?seed={seed}", new StringContent(body, Encoding.UTF8, "application/json")); nextFetch = 0; }
                catch (Exception e) { Plugin.L.LogWarning("Sfida della settimana non inviata: " + e.Message); pending = true; }
                finally { busy = false; }
            });
        }

        public static string Line()
        {
            if (!Host) return GalaxyView.Current?.challenge ?? "";
            if (info == null) return "";
            string lead = info.board.Count > 0 ? $" · in testa: «{info.board[0].ship}» {info.board[0].progress}/{info.target}" : "";
            return $"{info.label} {Math.Min(S.progress, info.target)}/{info.target}" + (S.done ? " · completata" : lead);
        }
        public static string Describe() => info == null ? "sfida non ancora letta dal server" : $"settimana {info.week}: {info.label} ({info.key}), progresso {S.progress}/{info.target}{(S.done ? " completata" : "")}; classifica: {string.Join(", ", info.board.Select(r => $"{r.ship} {r.progress}"))}";
    }
}

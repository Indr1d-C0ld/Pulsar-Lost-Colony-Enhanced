// Stato di servizio: statistiche personali, gradi, specializzazioni e classifiche globali.
// Le statistiche si accumulano in config/melchior.service.json; ogni minuto vengono inviate al Subspace Relay
// (porta di amministrazione), che tiene le classifiche di tutti i giocatori della rete.
// Pesi, gradi e specializzazioni sono in config/melchior.ranking.json.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace Melchior
{
    public class StatDef { public string Id = "", Label = "", Group = ""; public double Points; public string Unit = ""; }
    public class RankDef { public string Name = ""; public double Points; }
    public class SpecDef { public string Name = ""; public int Class; public Dictionary<string, double> Weights = new(); public string[] Titles = Array.Empty<string>(); public double PerLevel = 100; }

    public class RankingConfig
    {
        public int AdminPort = 4580;
        public int SyncSeconds = 60;
        public StatDef[] Stats =
        {
            new() { Id = "tempo.totale", Label = "Tempo in servizio", Group = "Servizio", Unit = "ore", Points = 0.01 },
            new() { Id = "sessioni", Label = "Missioni avviate", Group = "Servizio", Points = 5 },
            new() { Id = "salti", Label = "Salti a curvatura", Group = "Navigazione", Points = 4 },
            new() { Id = "salti.ciechi", Label = "Salti alla cieca", Group = "Navigazione", Points = 15 },
            new() { Id = "nemici", Label = "Navi nemiche distrutte", Group = "Combattimento", Points = 25 },
            new() { Id = "danni.scafo", Label = "Danni allo scafo subiti", Group = "Combattimento", Points = 0.002 },
            new() { Id = "danni.scudi", Label = "Danni assorbiti dagli scudi", Group = "Combattimento", Points = 0.004 },
            new() { Id = "navi.perse", Label = "Navi perdute", Group = "Combattimento", Points = -50 },
            new() { Id = "riparazioni", Label = "Scafo riparato", Group = "Ingegneria", Points = 0.004 },
            new() { Id = "ingegneria.spurghi", Label = "Spurghi del plasma riusciti", Group = "Ingegneria", Points = 3 },
            new() { Id = "ingegneria.allineamenti", Label = "Riallineamenti riusciti", Group = "Ingegneria", Points = 4 },
            new() { Id = "ingegneria.arresti", Label = "Arresti di sicurezza subiti", Group = "Ingegneria", Points = -5 },
            new() { Id = "scienza.rotazioni", Label = "Rotazioni dell'armonica", Group = "Scienza", Points = 1 },
            new() { Id = "scienza.impulsi", Label = "Impulsi attivi emessi", Group = "Scienza", Points = 1 },
            new() { Id = "scienza.decrittazioni", Label = "Firme nemiche decrittate", Group = "Scienza", Points = 8 },
            new() { Id = "armi.raffiche", Label = "Raffiche a sovraccarico", Group = "Tattica", Points = 2 },
            new() { Id = "armi.inceppamenti", Label = "Condensatori saltati", Group = "Tattica", Points = -2 },
            new() { Id = "pilota.manovre", Label = "Manovre di volo eseguite", Group = "Navigazione", Points = 1 },
            new() { Id = "pilota.rotte", Label = "Rotte di curvatura calcolate", Group = "Navigazione", Points = 3 },
            new() { Id = "comando.ordini", Label = "Ordini di plancia impartiti", Group = "Stazioni", Points = 1 },
            new() { Id = "comando.interventi", Label = "Interventi del capitano", Group = "Stazioni", Points = 3 },
            new() { Id = "missioni", Label = "Incarichi completati", Group = "Carriera", Points = 40 },
            new() { Id = "crediti.spesi", Label = "Crediti spesi", Group = "Carriera", Points = 0.002 },
            new() { Id = "caos.max", Label = "Caos massimo affrontato", Group = "Carriera", Points = 30 },
            new() { Id = "obiettivi", Label = "Obiettivi raggiunti", Group = "Carriera", Points = 60 },
            new() { Id = "rarita.trovati", Label = "Componenti di grado a bordo", Group = "Collezione", Points = 10 },
            new() { Id = "rarita.max", Label = "Grado più alto trovato", Group = "Collezione", Points = 40 },
            new() { Id = "stazioni.regolazioni", Label = "Regolazioni di stazione", Group = "Stazioni", Points = 0.5 },
            new() { Id = "stazioni.assetti", Label = "Assetti di bordo ordinati", Group = "Stazioni", Points = 2 },
            new() { Id = "allarme.rosso", Label = "Tempo in allarme rosso", Group = "Stazioni", Unit = "ore", Points = 0.02 },
            new() { Id = "classe.0", Label = "Tempo da Capitano", Group = "Ruoli", Unit = "ore" },
            new() { Id = "classe.1", Label = "Tempo da Pilota", Group = "Ruoli", Unit = "ore" },
            new() { Id = "classe.2", Label = "Tempo da Scienziato", Group = "Ruoli", Unit = "ore" },
            new() { Id = "classe.3", Label = "Tempo da Specialista d'armi", Group = "Ruoli", Unit = "ore" },
            new() { Id = "classe.4", Label = "Tempo da Ingegnere", Group = "Ruoli", Unit = "ore" },
        };
        public RankDef[] Ranks =
        {
            new() { Name = "Recluta", Points = 0 }, new() { Name = "Cadetto", Points = 100 }, new() { Name = "Guardiamarina", Points = 300 },
            new() { Name = "Sottotenente", Points = 700 }, new() { Name = "Tenente", Points = 1500 }, new() { Name = "Tenente comandante", Points = 3000 },
            new() { Name = "Comandante", Points = 6000 }, new() { Name = "Capitano", Points = 12000 }, new() { Name = "Capitano di flotta", Points = 20000 },
            new() { Name = "Commodoro", Points = 32000 }, new() { Name = "Contrammiraglio", Points = 50000 }, new() { Name = "Viceammiraglio", Points = 80000 },
            new() { Name = "Ammiraglio", Points = 130000 }, new() { Name = "Ammiraglio di flotta", Points = 220000 },
        };
        // Specializzazioni: un livello ogni PerLevel punti, calcolati con i pesi indicati (le ore valgono 3600 secondi).
        public SpecDef[] Specs =
        {
            new() { Name = "Comando", Class = 0, Weights = new() { ["classe.0"] = 0.02, ["stazioni.assetti"] = 3, ["missioni"] = 20, ["comando.ordini"] = 2, ["comando.interventi"] = 5 }, Titles = new[] { "Aspirante", "Primo ufficiale", "Comandante di nave", "Stratega", "Leggenda del ponte" } },
            new() { Name = "Timone", Class = 1, Weights = new() { ["classe.1"] = 0.02, ["salti"] = 3, ["salti.ciechi"] = 10, ["pilota.manovre"] = 2, ["pilota.rotte"] = 5 }, Titles = new[] { "Allievo", "Timoniere", "Pilota scelto", "Asso", "Maestro di rotta" } },
            new() { Name = "Scienza", Class = 2, Weights = new() { ["classe.2"] = 0.02, ["danni.scudi"] = 0.005, ["scienza.decrittazioni"] = 12, ["scienza.rotazioni"] = 2, ["scienza.impulsi"] = 2 }, Titles = new[] { "Assistente", "Analista", "Ufficiale scientifico", "Luminare", "Mente di flotta" } },
            new() { Name = "Tattica", Class = 3, Weights = new() { ["classe.3"] = 0.02, ["nemici"] = 15, ["allarme.rosso"] = 0.01, ["armi.raffiche"] = 4 }, Titles = new[] { "Servente", "Artigliere", "Ufficiale tattico", "Cecchino stellare", "Flagello dei pirati" } },
            new() { Name = "Ingegneria", Class = 4, Weights = new() { ["classe.4"] = 0.02, ["riparazioni"] = 0.01, ["stazioni.regolazioni"] = 1, ["ingegneria.spurghi"] = 6, ["ingegneria.allineamenti"] = 8 }, Titles = new[] { "Apprendista", "Macchinista", "Capo ingegnere", "Mago dei motori", "Artefice di miracoli" } },
        };
    }

    public class ServiceProfile { public string Id = Guid.NewGuid().ToString("N"); public string Name = ""; public Dictionary<string, double> Stats = new(); }

    public class BoardRow { public string name { get; set; } = ""; public string rank { get; set; } = ""; public double points { get; set; } public double value { get; set; } }

    public static class Service
    {
        public static RankingConfig Cfg = new(); public static ServiceProfile Me = new();
        public static List<BoardRow> Board = new(); public static string BoardStat = "points"; public static string BoardStatus = "in attesa del relay";
        static string savePath = ""; static bool dirty; static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(4) };
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static long nextPoll, nextSync = 8000, nextSave;
        static int lastJumps = -1, lastBlind = -1, lastKills = -1, lastSpent = -1; static float lastHull = -1, lastShields = -1; static bool wasInRoom;
        static readonly JsonSerializerOptions json = new()
        {
            IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static void Load(string dir)
        {
            string cfg = Path.Combine(dir, "melchior.ranking.json"); savePath = Path.Combine(dir, Plugin.PerInstance("melchior.service.json"));
            try
            {
                if (File.Exists(cfg)) Cfg = JsonSerializer.Deserialize<RankingConfig>(File.ReadAllText(cfg), json) ?? new RankingConfig();
                else File.WriteAllText(cfg, JsonSerializer.Serialize(Cfg, json));
                if (File.Exists(savePath)) Me = JsonSerializer.Deserialize<ServiceProfile>(File.ReadAllText(savePath), json) ?? new ServiceProfile();
                else dirty = true;
            }
            catch (Exception e) { Plugin.L.LogError("Stato di servizio non caricato: " + e.Message); }
            Plugin.L.LogInfo($"Stato di servizio: {Cfg.Stats.Length} statistiche, {Cfg.Ranks.Length} gradi, {Cfg.Specs.Length} specializzazioni. Grado attuale: {Rank().Name} ({Points():0} punti).");
        }

        public static double Get(string id) => Me.Stats.TryGetValue(id, out var v) ? v : 0;
        public static void Add(string id, double v) { if (v == 0) return; Me.Stats[id] = Get(id) + v; dirty = true; }
        public static void Max(string id, double v) { if (v > Get(id)) { Me.Stats[id] = v; dirty = true; } }

        public static double Points() => Math.Max(0, Cfg.Stats.Sum(s => s.Points * Get(s.Id)));
        public static RankDef Rank(double? pts = null) { double p = pts ?? Points(); return Cfg.Ranks.Where(r => r.Points <= p).OrderBy(r => r.Points).LastOrDefault() ?? Cfg.Ranks[0]; }
        public static RankDef NextRank() { double p = Points(); return Cfg.Ranks.Where(r => r.Points > p).OrderBy(r => r.Points).FirstOrDefault(); }
        public static (int level, string title, double frac) Spec(SpecDef s)
        {
            double p = s.Weights.Sum(w => w.Value * Get(w.Key)); int lvl = (int)(p / s.PerLevel);
            string t = s.Titles.Length == 0 ? "" : s.Titles[Math.Min(s.Titles.Length - 1, lvl / 5)];
            return (lvl, t, p / s.PerLevel - lvl);
        }
        public static string Format(StatDef s) { double v = Get(s.Id); return s.Unit == "ore" ? (v / 3600).ToString("0.0") + " h" : v.ToString(v >= 100 ? "0" : "0.#"); }

        public static void Tick()
        {
            long now = clock.ElapsedMilliseconds;
            if (now >= nextPoll) { nextPoll = now + 1000; try { Poll(); } catch { } }
            if (dirty && now >= nextSave) { nextSave = now + 15000; Save(); }
            if (now >= nextSync) { nextSync = now + Math.Max(10, Cfg.SyncSeconds) * 1000L; Sync(); }
        }

        static int Delta(ref int last, int cur) { int d = last >= 0 && cur > last ? cur - last : 0; last = cur; return d; }

        // Una volta al secondo: tempo di servizio e differenze dei contatori del gioco.
        static void Poll()
        {
            bool inRoom = PhotonNetwork.inRoom; var srv = PLServer.Instance; var me = PLNetworkManager.Instance?.LocalPlayer; var ship = me?.StartingShip;
            if (inRoom && !wasInRoom) { Add("sessioni", 1); lastJumps = lastBlind = lastKills = lastSpent = -1; lastHull = lastShields = -1; }
            wasInRoom = inRoom;
            if (!inRoom || srv == null || ship == null) return;
            if (string.IsNullOrEmpty(Me.Name) || Me.Name != me.GetPlayerName(false)) { Me.Name = me.GetPlayerName(false) ?? Me.Name; dirty = true; }
            // il nome viaggia anche nelle proprietà Photon: così il pannello del server mostra chi è collegato
            try { if (!string.IsNullOrEmpty(Me.Name) && PhotonNetwork.playerName != Me.Name) PhotonNetwork.playerName = Me.Name; } catch { }
            Add("tempo.totale", 1);
            int cls = me.GetClassID(); if (cls >= 0 && cls <= 4) Add("classe." + cls, 1);
            if (ship.m_AlertLevel >= 2) Add("allarme.rosso", 1);
            Add("salti", Delta(ref lastJumps, (int)srv.NumJumps));
            Add("salti.ciechi", Delta(ref lastBlind, (int)srv.BlindJumpCount));
            Add("nemici", Delta(ref lastKills, (int)srv.EnemyKills));
            Add("crediti.spesi", Delta(ref lastSpent, (int)srv.CreditsSpent_InRun));
            Max("caos.max", (float)srv.ChaosLevel);
            var st = ship.MyStats;
            if (st != null)
            {
                float hull = st.HullCurrent, sh = st.ShieldsCurrent;
                if (lastHull >= 0) { if (hull < lastHull) Add("danni.scafo", lastHull - hull); else if (hull > lastHull && hull - lastHull < st.HullMax * 0.5f) Add("riparazioni", hull - lastHull); }
                if (lastShields >= 0 && sh < lastShields) Add("danni.scudi", lastShields - sh);
                lastHull = hull; lastShields = sh;
            }
            Me.Stats["obiettivi"] = Achievements.DoneCount;
        }

        public static void Save()
        {
            try { File.WriteAllText(savePath, JsonSerializer.Serialize(Me, json)); dirty = false; }
            catch (Exception e) { Plugin.L.LogError("Stato di servizio non salvato: " + e.Message); }
        }

        internal static string Base => $"http://{Plugin.RelayAddress}:{Cfg.AdminPort}";

        // Invio al relay e lettura della classifica, fuori dal thread del gioco.
        public static void Sync()
        {
            if (!Plugin.RelayEnabled.Value) { BoardStatus = "relay disattivato"; return; }
            double pts = Points();
            string body = JsonSerializer.Serialize(new { id = Me.Id, name = string.IsNullOrEmpty(Me.Name) ? "Senza nome" : Me.Name, points = pts, rank = Rank(pts).Name, stats = Me.Stats });
            string url = Base, stat = BoardStat;
            Task.Run(async () =>
            {
                try
                {
                    await http.PostAsync(url + "/api/stats", new StringContent(body, Encoding.UTF8, "application/json"));
                    string txt = await http.GetStringAsync(url + "/api/leaderboard?n=12&stat=" + Uri.EscapeDataString(stat));
                    var rows = JsonSerializer.Deserialize<List<BoardRow>>(txt) ?? new();
                    Board = rows; BoardStatus = $"aggiornata alle {DateTime.Now:HH:mm:ss}";
                }
                catch (Exception e) { BoardStatus = "relay non raggiungibile (" + e.GetType().Name + ")"; }
            });
        }
    }

    [HarmonyPatch(typeof(PLServer), nameof(PLServer.MissionCompletedClient))]
    static class SvcMission { static void Postfix() => Service.Add("missioni", 1); }

    [HarmonyPatch(typeof(PLServer), nameof(PLServer.OnPlayerShipDestroyed))]
    static class SvcShipLost { static void Postfix() => Service.Add("navi.perse", 1); }

    // Scheda personale e classifica: F10 (o comando di console "svc").
    public static class ServicePanel
    {
        public static bool Show; public static int Tab;
        public static void Tick() { try { if (Input.GetKeyDown(KeyCode.F10)) Show = !Show; } catch { } }

        public static void Draw()
        {
            if (!Show) return;
            var T = MelchiorHud.Title; var B = MelchiorHud.Body; var S = MelchiorHud.Small;
            float w = 1040, h = 640, x = (Screen.width - w) / 2, y = (Screen.height - h) / 2;
            GUI.Box(new Rect(x, y, w, h), "", MelchiorHud.Box);
            double pts = Service.Points(); var rank = Service.Rank(pts); var next = Service.NextRank();
            GUI.Label(new Rect(x + 24, y + 12, 700, 30), $"<color=#F2A65A>STATO DI SERVIZIO</color>   {(string.IsNullOrEmpty(Service.Me.Name) ? "" : Service.Me.Name)}", T);
            GUI.Label(new Rect(x + 24, y + 44, 700, 26), $"<color=#9db4ff>{rank.Name}</color>   {pts:0} punti di servizio" + (next == null ? "   grado massimo" : $"   <color=#8b90a0>prossimo grado: {next.Name} a {next.Points:0}</color>"), B);
            // statistiche per gruppo, su due colonne
            float cx = x + 24, cy = y + 84; string group = "";
            foreach (var s in Service.Cfg.Stats)
            {
                if (s.Group != group) { if (cy > y + h - 90) { cx += 330; cy = y + 84; } group = s.Group; GUI.Label(new Rect(cx, cy, 300, 20), "<color=#C58AF9>" + group.ToUpperInvariant() + "</color>", S); cy += 20; }
                GUI.Label(new Rect(cx, cy, 220, 20), "<color=#8b90a0>" + s.Label + "</color>", S);
                GUI.Label(new Rect(cx + 224, cy, 90, 20), Service.Format(s), S); cy += 19;
                if (cy > y + h - 40) { cx += 330; cy = y + 84; }
            }
            // specializzazioni e classifica
            float rx = x + 700, ry = y + 84;
            GUI.Label(new Rect(rx, ry, 320, 20), "<color=#C58AF9>SPECIALIZZAZIONI</color>", S); ry += 22;
            foreach (var sp in Service.Cfg.Specs)
            {
                var (lvl, title, frac) = Service.Spec(sp);
                GUI.Label(new Rect(rx, ry, 320, 20), $"{sp.Name}  <color=#9db4ff>liv. {lvl}</color>  <color=#8b90a0>{title}  ({frac * 100:0}%)</color>", S); ry += 19;
            }
            ry += 14;
            GUI.Label(new Rect(rx, ry, 320, 20), $"<color=#C58AF9>CLASSIFICA DELLA RETE</color>  <color=#8b90a0>{Service.BoardStatus}</color>", S); ry += 22;
            int pos = 1;
            foreach (var r in Service.Board.Take(12))
            {
                GUI.Label(new Rect(rx, ry, 320, 20), $"{pos++,2}. {r.name}  <color=#9db4ff>{r.rank}</color>  <color=#8b90a0>{r.value:0}</color>", S); ry += 19;
            }
            if (Service.Board.Count == 0) GUI.Label(new Rect(rx, ry, 320, 20), "<color=#8b90a0>Nessun dato ancora.</color>", S);
        }
    }
}

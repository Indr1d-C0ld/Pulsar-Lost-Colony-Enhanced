// Bacheca dei contratti: al posto di missioni imposte, il capitano sceglie fra offerte con committente, compenso,
// rischio e scadenza in salti. La fiducia di ogni committente cresce con i contratti riusciti e migliora le offerte.
// La bacheca vive sull'host (salvata con la cronaca della galassia) e viene inviata agli altri giocatori.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Melchior
{
    public class ContractOffer
    {
        public int id, mission, reward, tier, expires;
        public string issuer = "", color = "#F2A65A", family = "", title = "", desc = "";
        public bool urgent;
    }

    public static class Contracts
    {
        public const int Size = 4;
        static readonly System.Random rng = new();
        static readonly JsonSerializerOptions json = new() { IncludeFields = true };
        static List<ContractOffer> remote = new(); // copia ricevuta dall'host (sui client)
        static long nextTick, nextSend; static int lastJumps = -1; static string lastSent = "";
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static readonly (string name, string color)[] Independents = { ("Gilda dei Mercanti", "#FFD75E"), ("Comando di Flotta", "#9DB4FF"), ("Accademia delle Scienze", "#5EE0D6") };

        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        public static List<ContractOffer> Board => Host ? Saga.State.board : remote;
        public static ContractOffer Active => Saga.State.contract;
        public static int Trust(string issuer) => Saga.State.trust.TryGetValue(issuer, out var t) ? t : 0;
        static int Jumps() { try { return (int)PLServer.Instance.NumJumps; } catch { return 0; } }

        // Committenti: le fazioni della galassia (tranne gli infetti) più alcuni indipendenti.
        static List<(string name, string color)> Issuers()
        {
            var list = new List<(string, string)>();
            try
            {
                var g = PLGlobal.Instance.Galaxy; var f = g.AllFactions;
                for (int i = 0; i < f.Count; i++)
                {
                    var x = f[i]; if (x == null || x.FactionID == 4 || x.FactionID == 6) continue; // niente infetti, niente "sconosciuti"
                    string n = GalaxyWar.Name(x.FactionID); if (string.IsNullOrEmpty(n) || n == "nessuno") continue;
                    var c = g.GetFactionColorForID(x.FactionID); list.Add((n, "#" + UnityEngine.ColorUtility.ToHtmlStringRGB(c)));
                }
            }
            catch { }
            list.AddRange(Independents);
            return list;
        }

        static ContractOffer Make(float power, bool urgent, int family = -1)
        {
            int tier = Math.Clamp((int)(power / 4f) + rng.Next(-1, 2), 0, 4);
            int id = Missions.PickId(tier, family); if (id < 0) return null;
            var pmd = Missions.Data(id); if (pmd == null) return null;
            var issuers = Issuers(); var who = issuers[rng.Next(issuers.Count)];
            double mult = (0.8 + rng.NextDouble() * 0.7) * (1 + Math.Clamp(Trust(who.name), -5, 10) * 0.05) * (urgent ? 1.3 : 1);
            return new ContractOffer
            {
                id = rng.Next(1, int.MaxValue), mission = id, tier = tier, urgent = urgent, issuer = who.name, color = who.color,
                family = Missions.Label(Missions.FamilyOf(id)), title = pmd.Name, desc = pmd.Desc,
                reward = (int)Math.Round(pmd.Reward_Credits * mult / 50) * 50, expires = Jumps() + (urgent ? 2 : rng.Next(3, 7)),
            };
        }

        public static void Tick()
        {
            long now = clock.ElapsedMilliseconds; if (now < nextTick) return; nextTick = now + 3000;
            try
            {
                if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
                var b = Saga.State.board; int j = Jumps(); bool changed = false;
                if (b.RemoveAll(o => o.expires < j) > 0) changed = true;
                if (j != lastJumps || b.Count == 0)
                {
                    lastJumps = j; float p = Director.Power(Stations.MyShip, out _, out _);
                    while (b.Count(o => !o.urgent) < Size - 1 && b.Count < Size) { var o = Make(p, false); if (o == null) break; b.Add(o); changed = true; }
                }
                if (changed) Saga.MarkDirty();
                string payload = JsonSerializer.Serialize(new { board = b, contract = Saga.State.contract }, json);
                if (payload != lastSent || now >= nextSend) { lastSent = payload; nextSend = now + 20000; Send("board|" + payload); }
            }
            catch (Exception e) { Plugin.L.LogWarning("Bacheca: " + e.Message); nextTick = now + 15000; }
        }

        static void Send(string msg)
        {
            try { if (PhotonNetwork.inRoom) PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)msg, true, new RaiseEventOptions { Receivers = ReceiverGroup.Others }); } catch { }
        }

        class Wire { public List<ContractOffer> board = new(); public ContractOffer contract; }
        public static ContractOffer RemoteActive;
        public static void Receive(string msg)
        {
            try { var w = JsonSerializer.Deserialize<Wire>(msg[6..], json); if (w != null) { remote = w.board ?? new(); RemoteActive = w.contract; Panels.RefreshAll(); } } catch { }
        }
        public static ContractOffer Current => Host ? Saga.State.contract : RemoteActive;

        // Il direttore pubblica una richiesta urgente invece di imporre la missione.
        public static bool PostUrgent(float power)
        {
            if (!Saga.Loaded || Missions.AnyActive() || Saga.State.board.Count(o => o.urgent) > 0) return false;
            var o = Make(power, true); if (o == null) return false;
            var b = Saga.State.board; if (b.Count >= Size) b.RemoveAt(b.FindIndex(x => !x.urgent));
            b.Insert(0, o); Saga.MarkDirty(); lastSent = "";
            Director.Notify("BACHECA", $"Richiesta urgente da {o.issuer}: {o.title}. Compenso {o.reward} crediti, scade fra {o.expires - Jumps()} salti.");
            News.Post(o.family == "Caccia" ? "taglie" : "flotta", $"{o.issuer} cerca un equipaggio con urgenza: {o.title}.");
            return true;
        }

        // Una diceria fondata del notiziario diventa un'offerta della famiglia indicata.
        public static bool PostLead(float power, int family)
        {
            if (!Saga.Loaded || Saga.State.board.Count >= Size) return false;
            var o = Make(power, false, family); if (o == null) return false;
            Saga.State.board.Add(o); Saga.MarkDirty(); lastSent = ""; return true;
        }

        // Dal pannello: l'host esegue, un client inoltra all'host.
        public static void Request(string op, int offerId)
        {
            if (Host) { Execute(op, offerId); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|bacheca|{op}|{offerId}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var p = msg.Split('|'); if (p.Length >= 4 && Host && int.TryParse(p[3], out int id)) Execute(p[2], id); }

        static void Execute(string op, int offerId)
        {
            var b = Saga.State.board; var o = b.FirstOrDefault(x => x.id == offerId); if (o == null) return;
            if (op == "rifiuta") { b.Remove(o); Saga.MarkDirty(); lastSent = ""; Stations.Announce("BACHECA", $"Offerta di {o.issuer} rifiutata."); return; }
            if (op != "accetta") return;
            if (Missions.AnyActive()) { Stations.Announce("BACHECA", "C'è già un incarico in corso: portalo a termine o abbandonalo."); return; }
            if (!Missions.Start(o.mission)) { Stations.Announce("BACHECA", "Il contratto non può partire adesso."); return; }
            b.Remove(o); Saga.State.contract = o; Saga.MarkDirty(); lastSent = "";
            Director.Notify("CONTRATTO ACCETTATO", $"{o.title} per conto di {o.issuer}: {o.reward} crediti alla consegna.");
            Service.Add("contratti.accettati", 1);
        }

        // Esito di una missione: se era un contratto, compenso pattuito e fiducia del committente.
        public static string OnOutcome(int missionId, bool success)
        {
            var c = Saga.State.contract; if (c == null || c.mission != missionId) return null;
            Saga.State.contract = null; var pmd = Missions.Data(missionId);
            int extra = Math.Max(0, c.reward - (pmd?.Reward_Credits ?? 0));
            var tr = Saga.State.trust; tr[c.issuer] = Math.Clamp(Trust(c.issuer) + (success ? 1 : -2), -10, 20);
            if (success && extra > 0) try { PLServer.Instance.CurrentCrewCredits += extra; } catch { }
            Service.Add(success ? "contratti.riusciti" : "contratti.falliti", 1);
            GalaxyWar.Support(c.issuer, success);
            News.Post("flotta", success ? $"{c.issuer} conferma: «{c.title}» è stato portato a termine." : $"{c.issuer} lamenta un contratto mancato: «{c.title}».");
            return success ? $"Contratto con {c.issuer} onorato: {c.reward} crediti, fiducia {Trust(c.issuer):+0;-0;0}." : $"Contratto con {c.issuer} mancato: la fiducia scende a {Trust(c.issuer):+0;-0;0}.";
        }

        // Il capitano automatico accetta l'offerta più conveniente quando non c'è niente in corso.
        public static void BotAccept()
        {
            if (!Host || Missions.AnyActive() || Saga.State.board.Count == 0) return;
            var o = Saga.State.board.OrderByDescending(x => x.urgent).ThenByDescending(x => x.reward).First();
            Execute("accetta", o.id);
        }

        public static int JumpsLeft(ContractOffer o) => o.expires - Jumps();
    }
}

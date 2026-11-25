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
        public int id, mission, reward, tier, expires, sector = -1, side = -1;
        public string issuer = "", color = "#F2A65A", family = "", title = "", desc = "";
        public string kind = "", target = "", crisis = ""; // kind "taglia": caccia a un personaggio ricorrente (target) nell'ultimo settore noto
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
        static int Jumps() { try { return Obscured.Jumps(); } catch { return 0; } }

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
                    string n = GalaxyWar.Name(x.FactionID); if (string.IsNullOrEmpty(n) || n == "nessuno" || Factions.Level(x.FactionID) < -1) continue; // chi vi considera nemici non vi offre lavoro
                    var c = g.GetFactionColorForID(x.FactionID); list.Add((n, "#" + UnityEngine.ColorUtility.ToHtmlStringRGB(c)));
                }
            }
            catch { }
            list.AddRange(Independents);
            return list;
        }

        static ContractOffer Make(float power, bool urgent, int family = -1, string issuer = null)
        {
            int tier = Math.Clamp((int)(power / 4f) + rng.Next(-1, 2), 0, 4);
            int id = Missions.PickId(tier, family); if (id < 0) return null;
            var pmd = Missions.Data(id); if (pmd == null) return null;
            var issuers = Issuers(); var who = issuers[rng.Next(issuers.Count)];
            if (issuer != null) who = issuers.FirstOrDefault(x => x.name == issuer) is var w && w.name != null ? w : (issuer, "#F2A65A");
            double mult = (0.8 + rng.NextDouble() * 0.7) * (1 + Math.Clamp(Trust(who.name), -5, 10) * 0.05) * (1 + Factions.RewardBonus(who.name)) * (urgent ? 1.3 : 1);
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
                if (b.RemoveAll(o => o.expires < j || (o.kind == "taglia" && Nemesis.Find(o.target)?.alive != true)) > 0) changed = true;
                foreach (var o in b) if (o.kind == "taglia") { var f = Nemesis.Find(o.target); if (f != null && f.sector != o.sector) { o.sector = f.sector; o.desc = BountyDesc(f); changed = true; } }
                if (j != lastJumps || b.Count == 0)
                {
                    lastJumps = j; float p = Director.Power(Stations.MyShip, out _, out _);
                    while (b.Count(o => !o.urgent) < Size - 1 && b.Count < Size)
                    {
                        var o = (!b.Any(x => x.kind == "taglia") && rng.NextDouble() < 0.3 ? Bounty(p) : null) ?? Make(p, false); if (o == null) break; b.Add(o); changed = true;
                    }
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
            if (Missions.AnyActive() || Saga.State.contract != null) { Stations.Announce("BACHECA", "C'è già un incarico in corso: portalo a termine o abbandonalo."); return; }
            if (o.kind == "taglia")
            {
                var f = Nemesis.Find(o.target); if (f == null || !f.alive) { b.Remove(o); Stations.Announce("BACHECA", "Il bersaglio non è più nella galassia."); return; }
                o.sector = f.sector; o.expires = Math.Max(o.expires, Jumps() + Nemesis.JumpsTo(f.sector) + 4);
                b.Remove(o); Saga.State.contract = o; Saga.MarkDirty(); lastSent = "";
                Director.Notify("TAGLIA ACCETTATA", $"Caccia a {f.Full} per conto di {o.issuer}: {o.reward} crediti. Ultima posizione nota: settore {o.sector}, a {Nemesis.JumpsTo(o.sector)} salti. Rotta tracciata.");
                Plot(o.sector); Service.Add("contratti.accettati", 1); return;
            }
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
            GalaxyWar.Support(c.issuer, success); Factions.OnContract(c, success); Crises.OnContract(c, success);
            News.Post("flotta", success ? $"{c.issuer} conferma: «{c.title}» è stato portato a termine." : $"{c.issuer} lamenta un contratto mancato: «{c.title}».");
            return success ? $"Contratto con {c.issuer} onorato: {c.reward} crediti, fiducia {Trust(c.issuer):+0;-0;0}." : $"Contratto con {c.issuer} mancato: la fiducia scende a {Trust(c.issuer):+0;-0;0}.";
        }

        // ---- taglie sui personaggi ricorrenti
        static string BountyDesc(Figure f) => $"{char.ToUpper(Nemesis.RoleLabel(f)[0]) + Nemesis.RoleLabel(f)[1..]}, grado {f.rank}. Ultima posizione nota: settore {f.sector}, a {Nemesis.JumpsTo(f.sector)} salti. {char.ToUpper(Nemesis.TraitText(f)[0]) + Nemesis.TraitText(f)[1..]}.";
        static ContractOffer Bounty(float power)
        {
            var taken = Saga.State.contract?.target;
            var pool = Nemesis.Alive.Where(f => f.Full != taken && f.sector >= 0 && Nemesis.JumpsTo(f.sector) < 12).ToList(); if (pool.Count == 0) return null;
            var f = pool[rng.Next(pool.Count)];
            var issuers = Issuers().Where(x => f.role != "ribelle" || x.name == GalaxyWar.Name(f.faction) || x.name == "Comando di Flotta").ToList(); if (issuers.Count == 0) issuers = Issuers();
            var who = issuers[rng.Next(issuers.Count)];
            int tier = Math.Clamp((int)(power / 4f), 0, 4);
            int reward = (int)Math.Round((2000 + 1800 * f.rank) * (1 + tier * 0.3) * (1 + Factions.RewardBonus(who.name)) / 50) * 50;
            return new ContractOffer
            {
                id = rng.Next(1, int.MaxValue), kind = "taglia", target = f.Full, sector = f.sector, tier = Math.Min(4, f.rank - 1), issuer = who.name, color = who.color,
                family = "Taglia", title = $"Taglia: {f.Full}", desc = BountyDesc(f), reward = reward, expires = Jumps() + Nemesis.JumpsTo(f.sector) + rng.Next(4, 7),
            };
        }

        public static void OnHunt(bool success, Figure f, string why = null)
        {
            var c = Saga.State.contract; if (c == null || c.kind != "taglia") return;
            Saga.State.contract = null; lastSent = "";
            var tr = Saga.State.trust; tr[c.issuer] = Math.Clamp(Trust(c.issuer) + (success ? 1 : -1), -10, 20);
            if (success) try { PLServer.Instance.CurrentCrewCredits += c.reward; } catch { }
            Factions.OnContract(c, success);
            Service.Add(success ? "contratti.riusciti" : "contratti.falliti", 1);
            string msg = success ? $"Taglia su {c.target} riscossa: {c.reward} crediti da {c.issuer}." : $"Taglia su {c.target} persa{(why != null ? ": " + why : "")}.";
            Director.Notify("BACHECA", msg); Saga.Chronicle(msg); Saga.MarkDirty();
            Unplot();
        }

        // ---- richieste delle crisi
        public static bool PostCrisis(float power, int family, string issuer, string crisis, int side)
        {
            if (!Saga.Loaded) return false;
            var o = Make(power, true, family, issuer); if (o == null) return false;
            o.crisis = crisis; o.side = side; o.expires = Jumps() + 4;
            var b = Saga.State.board; b.RemoveAll(x => x.urgent && x.crisis == ""); if (b.Count >= Size) b.RemoveAt(b.FindIndex(x => !x.urgent));
            b.Insert(0, o); Saga.MarkDirty(); lastSent = "";
            return true;
        }

        public static bool PostBounty(float power)
        {
            var b = Saga.State.board; if (!Saga.Loaded || b.Any(x => x.kind == "taglia")) return false;
            var o = Bounty(power); if (o == null) return false;
            if (b.Count >= Size) b.RemoveAt(b.FindLastIndex(x => !x.urgent)); b.Add(o); Touch(); return true;
        }

        public static string ForceBounty()
        {
            var o = Bounty(Director.Power(Stations.MyShip, out _, out _)); if (o == null) return "nessun personaggio disponibile";
            var b = Saga.State.board; if (b.Count >= Size) b.RemoveAt(b.Count - 1); b.Insert(0, o); Touch(); return $"{o.title}: {o.desc} ({o.reward} crediti)";
        }

        public static void Touch() { lastSent = ""; Saga.MarkDirty(); }

        // Rotta tracciata sulla mappa stellare verso un settore (taglie, crisi).
        public static void Plot(int sector) => Course.Plot(sector);
        static void Unplot() => Course.Clear();

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

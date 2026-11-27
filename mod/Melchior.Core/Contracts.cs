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
            foreach (var f in Diplomacy.F) // le fazioni della galassia (niente infetti); chi vi considera nemici non vi offre lavoro
            {
                if (Factions.Level(f) < -1) continue;
                string color = "#F2A65A"; try { color = "#" + UnityEngine.ColorUtility.ToHtmlStringRGB(PLGlobal.Instance.Galaxy.GetFactionColorForID(f)); } catch { }
                list.Add((GalaxyWar.Name(f), color));
            }
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
                reward = (int)Math.Round(pmd.Reward_Credits * mult * Rules.Reward * Talents.Mul("negoziatore", 0.05f) / 50) * 50, expires = Jumps() + (urgent ? 2 : rng.Next(3, 7)),
            };
        }

        public static void Tick()
        {
            long now = clock.ElapsedMilliseconds; if (now < nextTick) return; nextTick = now + 3000;
            try
            {
                if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
                var b = Saga.State.board; int j = Jumps(); bool changed = false;
                if (b.RemoveAll(o => o.expires < j || (o.kind == "taglia" && Nemesis.Find(o.target)?.alive != true) || (o.kind == "covo" && !Lairs.List.Any(l => l.name == o.target))) > 0) changed = true;
                foreach (var o in b) if (o.kind == "taglia") { var f = Nemesis.Find(o.target); if (f != null && f.sector != o.sector) { o.sector = f.sector; o.desc = BountyDesc(f); changed = true; } }
                if (j != lastJumps || b.Count == 0)
                {
                    lastJumps = j; float p = Director.Power(Stations.MyShip, out _, out _);
                    while (b.Count(o => !o.urgent) < Size - 1 && b.Count < Size)
                    {
                        var o = (!b.Any(x => x.kind == "taglia") && rng.NextDouble() < 0.3 * Calling.Bonus("caccia") * (Calling.Is("caccia") ? 1.6 : 1) ? Bounty(p) : null)
                             ?? (!b.Any(x => x.kind is "trasporto" or "contrabbando") && rng.NextDouble() < 0.35 ? Transport(p, rng.NextDouble() < 0.3 + Saga.State.infamia / 40.0) : null)
                             ?? (!b.Any(x => x.kind == "ambasciata") && rng.NextDouble() < 0.2 ? Embassy(p) : null)
                             ?? (!b.Any(x => x.kind == "covo") && Lairs.List.Any() && rng.NextDouble() < 0.3 ? LairOffer(p) : null)
                             ?? (!b.Any(x => x.crisis == "base") && Bases.NeedsSupply() != null ? SupplyOffer() : null)
                             ?? Make(p, false); if (o == null) break; b.Add(o); changed = true;
                    }
                }
                // carico a bordo: consegna all'arrivo nel settore di destinazione, fallimento alla scadenza
                var c = Saga.State.contract;
                if (c != null && c.kind is "trasporto" or "contrabbando" or "ambasciata")
                {
                    bool warp = false; try { warp = Stations.MyShip.InWarp; } catch { }
                    int here = -1; try { here = PLServer.GetCurrentSector().ID; } catch { }
                    if (here == c.sector && !warp) Deliver(true);
                    else if (j > c.expires) Deliver(false, "il tempo è scaduto");
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
            if (o.kind == "covo")
            {
                b.Remove(o); Saga.State.contract = o; Saga.MarkDirty(); lastSent = "";
                Director.Notify("ASSALTO AL COVO", $"Contratto accettato per {o.issuer}: espugnare {o.target}, nel settore {Places.Of(o.sector)}, a {Nemesis.JumpsTo(o.sector)} salti. Rotta tracciata.");
                Plot(o.sector); Service.Add("contratti.accettati", 1); return;
            }
            if (o.kind == "ambasciata")
            {
                b.Remove(o); Saga.State.contract = o; Saga.MarkDirty(); lastSent = "";
                Director.Notify("AMBASCIATA", $"A bordo {o.target}: va scortato nel settore {Places.Of(o.sector)}, a {Nemesis.JumpsTo(o.sector)} salti, entro {o.expires - Jumps()} salti. Rotta tracciata.");
                Plot(o.sector); Service.Add("contratti.accettati", 1); return;
            }
            if (o.kind is "trasporto" or "contrabbando")
            {
                b.Remove(o); Saga.State.contract = o; Saga.MarkDirty(); lastSent = "";
                Director.Notify("CARICO A BORDO", $"{o.target} caricato nella stiva per {o.issuer}: consegna nel settore {Places.Of(o.sector)}, a {Nemesis.JumpsTo(o.sector)} salti, entro {o.expires - Jumps()} salti. Rotta tracciata." + (o.kind == "contrabbando" ? " Evitiamo le ispezioni." : ""));
                Plot(o.sector); Service.Add("contratti.accettati", 1); return;
            }
            if (o.kind == "taglia")
            {
                var f = Nemesis.Find(o.target); if (f == null || !f.alive) { b.Remove(o); Stations.Announce("BACHECA", "Il bersaglio non è più nella galassia."); return; }
                o.sector = f.sector; o.expires = Math.Max(o.expires, Jumps() + Nemesis.JumpsTo(f.sector) + 4);
                b.Remove(o); Saga.State.contract = o; Saga.MarkDirty(); lastSent = "";
                Director.Notify("TAGLIA ACCETTATA", $"Caccia a {f.Full} per conto di {o.issuer}: {o.reward} crediti. Ultima posizione nota: settore {Places.Of(o.sector)}, a {Nemesis.JumpsTo(o.sector)} salti. Rotta tracciata.");
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
            GalaxyWar.Support(c.issuer, success); Factions.OnContract(c, success); Crises.OnContract(c, success); Morale.Change(success ? 3 : -4, null); if (success) { Competitors.Renown(Math.Max(5, c.reward / 150)); Saga.State.contractsDone++; }
            News.Post("flotta", success ? $"{c.issuer} conferma: «{c.title}» è stato portato a termine." : $"{c.issuer} lamenta un contratto mancato: «{c.title}».");
            return success ? $"Contratto con {c.issuer} onorato: {c.reward} crediti, fiducia {Trust(c.issuer):+0;-0;0}." : $"Contratto con {c.issuer} mancato: la fiducia scende a {Trust(c.issuer):+0;-0;0}.";
        }

        // ---- taglie sui personaggi ricorrenti
        static string BountyDesc(Figure f) => $"{char.ToUpper(Nemesis.RoleLabel(f)[0]) + Nemesis.RoleLabel(f)[1..]}, grado {f.rank}. Ultima posizione nota: settore {Places.Of(f.sector)}, a {Nemesis.JumpsTo(f.sector)} salti. {char.ToUpper(Nemesis.TraitText(f)[0]) + Nemesis.TraitText(f)[1..]}.";
        static ContractOffer Bounty(float power)
        {
            var taken = Saga.State.contract?.target;
            var pool = Nemesis.Alive.Where(f => f.Full != taken && f.sector >= 0 && Nemesis.JumpsTo(f.sector) < 12).ToList(); if (pool.Count == 0) return null;
            var f = pool[rng.Next(pool.Count)];
            var issuers = Issuers().Where(x => f.role != "ribelle" || x.name == GalaxyWar.Name(f.faction) || x.name == "Comando di Flotta").ToList(); if (issuers.Count == 0) issuers = Issuers();
            var who = issuers[rng.Next(issuers.Count)];
            int tier = Math.Clamp((int)(power / 4f), 0, 4);
            int reward = (int)Math.Round((2000 + 1800 * f.rank) * (1 + tier * 0.3) * Calling.Bonus("caccia") * (Calling.Is("caccia") ? 1.5 : 1) * (1 + Factions.RewardBonus(who.name)) / 50) * 50;
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
            Factions.OnContract(c, success); Morale.Change(success ? 4 : -4, null); if (success) { Competitors.Renown(15 + (f?.rank ?? 1) * 6); Saga.State.contractsDone++; Calling.Add("caccia", 2); }
            Service.Add(success ? "contratti.riusciti" : "contratti.falliti", 1);
            string msg = success ? $"Taglia su {c.target} riscossa: {c.reward} crediti da {c.issuer}." : $"Taglia su {c.target} persa{(why != null ? ": " + why : "")}.";
            Director.Notify("BACHECA", msg); Saga.Chronicle(msg); Saga.MarkDirty();
            Unplot();
        }

        // ---- trasporti e contrabbando: contratti senza missione nativa, consegna all'arrivo nel settore di destinazione
        static readonly string[] Legal = { "medicinali", "parti di ricambio", "colture idroponiche", "minerali raffinati", "apparecchiature scientifiche", "filtri per l'aria", "celle di energia", "posta e dispacci" };
        static readonly string[] Illegal = { "armi senza matricola", "reperti trafugati", "dati militari rubati", "tecnologia proibita", "carburante di contrabbando", "componenti del mercato nero" };
        public static bool Carrying(string kind = null) { var c = Saga.State.contract; return c != null && (kind == null ? c.kind is "trasporto" or "contrabbando" or "ambasciata" : c.kind == kind); }
        // Settori raggiungibili in 2-6 salti (visita in ampiezza dei vicini), con la loro distanza.
        static List<(PLSectorInfo s, int d)> Near(PLSectorInfo from, int min = 2, int max = 6)
        {
            var seen = new HashSet<int> { from.ID }; var frontier = new List<PLSectorInfo> { from }; var res = new List<(PLSectorInfo, int)>();
            for (int d = 1; d <= max && frontier.Count > 0; d++)
            {
                var next = new List<PLSectorInfo>();
                foreach (var s in frontier) { var nb = s.Neighbors; if (nb == null) continue; for (int i = 0; i < nb.Count; i++) { var n = nb[i]; if (n == null || !seen.Add(n.ID)) continue; next.Add(n); if (d >= min && n.MySPI != null && n.MissionSpecificID == -1) res.Add((n, d)); } }
                frontier = next;
            }
            return res;
        }
        static int FactionOf(string issuer) { foreach (var f in GalaxyWar.Factions) if (GalaxyWar.Name(f) == issuer) return f; return -1; }

        // Ambasciata: scortare l'inviato di una fazione in un settore di un'altra con cui i rapporti sono tesi.
        static ContractOffer Embassy(float power)
        {
            var cur = PLServer.GetCurrentSector(); if (cur == null) return null;
            var pairs = new List<(int a, int b)>();
            foreach (var a in Diplomacy.F) foreach (var b in Diplomacy.F) if (a != b && Diplomacy.StatusOf(a, b) <= 0 && Factions.Level(a) >= -1) pairs.Add((a, b));
            if (pairs.Count == 0) return null;
            var (fa, fb) = pairs[rng.Next(pairs.Count)];
            var near = Near(cur);
            var opts = pairs.OrderBy(_ => rng.Next()).Select(p => (p, list: near.Where(x => x.s.MySPI.Faction == p.b).ToList())).FirstOrDefault(x => x.list.Count > 0);
            if (opts.list == null) return null;
            (fa, fb) = opts.p; var pick = opts.list[rng.Next(opts.list.Count)]; var dest = pick.s; int d = pick.d;
            var who = Issuers().FirstOrDefault(x => x.name == GalaxyWar.Name(fa)); if (who.name == null) return null;
            int tier = Math.Clamp((int)(power / 4f), 0, 4); bool war = Diplomacy.StatusOf(fa, fb) == -1;
            int reward = (int)Math.Round((1200 + 500 * d) * (1 + tier * 0.25) * (war ? 1.5 : 1) * (1 + Factions.RewardBonus(who.name)) / 50) * 50;
            return new ContractOffer
            {
                id = rng.Next(1, int.MaxValue), kind = "ambasciata", target = $"l'inviato della {who.name}", sector = dest.ID, side = fb, tier = Math.Min(4, d - 1 + (war ? 1 : 0)), issuer = who.name, color = who.color,
                family = "Ambasciata", title = $"Ambasciata presso la {GalaxyWar.Name(fb)}",
                desc = $"Scortare l'inviato della {who.name} nel settore {Places.Of(dest.ID)} della {GalaxyWar.Name(fb)}, a {d} salti. Rapporti: {Diplomacy.StatusName(Diplomacy.StatusOf(fa, fb))}." + (war ? " Sono in guerra: c'è chi preferirebbe che non arrivasse." : ""),
                reward = reward, expires = Jumps() + d + 3,
            };
        }
        // ---- assalto a un covo dei pirati (Lairs.cs)
        static ContractOffer LairOffer(float power)
        {
            var l = Lairs.List.OrderBy(_ => rng.Next()).FirstOrDefault(); if (l == null) return null;
            var issuers = Issuers(); var who = issuers.FirstOrDefault(x => x.name == "Comando di Flotta"); if (who.name == null) who = issuers[rng.Next(issuers.Count)];
            int d = Nemesis.JumpsTo(l.sector); if (d > 12) return null;
            int tier = Math.Clamp((int)(power / 4f), 0, 4);
            return new ContractOffer
            {
                id = rng.Next(1, int.MaxValue), kind = "covo", target = l.name, sector = l.sector, tier = Math.Min(4, l.strength - 1), issuer = who.name, color = who.color,
                family = "Assalto", title = $"Assalto: {Lairs.Cap(l.name)}", desc = $"Espugnare il covo di {l.owner} nel settore {Places.Of(l.sector)}, a {d} salti. Difese: {new string('|', l.strength)}. Il bottino del covo resta a voi.",
                reward = (int)Math.Round((1500 + 900 * l.strength) * (1 + tier * 0.25) / 50) * 50, expires = Jumps() + d + 5,
            };
        }
        public static void EndLair(bool success, string why)
        {
            var c = Saga.State.contract; if (c == null || c.kind != "covo") return;
            Saga.State.contract = null; lastSent = "";
            var tr = Saga.State.trust; tr[c.issuer] = Math.Clamp(Trust(c.issuer) + (success ? 1 : -1), -10, 20);
            if (success) { try { PLServer.Instance.CurrentCrewCredits += c.reward; } catch { } Competitors.Renown(40); Saga.State.contractsDone++; Calling.Add("caccia", 2); Morale.Change(4, null); }
            string msg = success ? $"Assalto riuscito: {c.reward} crediti da {c.issuer}." : $"Assalto al covo annullato{(why != null ? ": " + why : "")}.";
            Director.Notify("BACHECA", msg); Saga.Chronicle(msg); Unplot(); Saga.MarkDirty();
        }
        public static string ForceLair() { var o = LairOffer(Director.Power(Stations.MyShip, out _, out _)); if (o == null) return "nessun covo"; var b = Saga.State.board; if (b.Count >= Size) b.RemoveAt(b.Count - 1); b.Insert(0, o); Touch(); return o.title; }

        // Rifornimenti per una nostra base con le scorte basse: un trasporto speciale.
        static ContractOffer SupplyOffer()
        {
            var bs = Bases.NeedsSupply(); if (bs == null) return null; int d = Nemesis.JumpsTo(bs.sector); if (d > 10) return null;
            return new ContractOffer
            {
                id = rng.Next(1, int.MaxValue), kind = "trasporto", crisis = "base", target = "rifornimenti per la base", sector = bs.sector, tier = 0, issuer = "Gilda dei Mercanti", color = "#FFD75E",
                family = "Rifornimenti", title = $"Rifornimenti per {bs.name}", desc = $"Le scorte di {bs.name} sono al {bs.supply}%. La Gilda carica i rifornimenti: consegnarli nel settore {Places.Of(bs.sector)}, a {d} salti.",
                reward = 400 + d * 150, expires = Jumps() + d + 4,
            };
        }

        public static string ForceEmbassy() { var o = Embassy(Director.Power(Stations.MyShip, out _, out _)); if (o == null) { var cur = PLServer.GetCurrentSector(); var nr = cur == null ? new() : Near(cur); return $"nessuna ambasciata possibile (vicini {nr.Count}: " + string.Join(" ", nr.GroupBy(x => x.s.MySPI.Faction).Select(g => g.Key + "=" + g.Count())) + "; committenti " + string.Join("/", Issuers().Select(x => x.name)) + ")"; } var b = Saga.State.board; if (b.Count >= Size) b.RemoveAt(b.Count - 1); b.Insert(0, o); Touch(); return $"{o.title}: {o.desc} ({o.reward} crediti)"; }
        static string PortName(PLSectorInfo s) { string n = System.Text.RegularExpressions.Regex.Replace(s.Name ?? "", @"\s*\(\d+\)$", ""); return string.IsNullOrEmpty(n) || int.TryParse(n, out _) ? $"il porto di {Places.Name(s.ID)}" : n; }

        static ContractOffer Transport(float power, bool contraband)
        {
            var cur = PLServer.GetCurrentSector(); if (cur == null) return null;
            var near = Near(cur); if (near.Count == 0) return null;
            var ports = near.Where(x => x.s.VisualIndication != ESectorVisualIndication.NONE).ToList(); var pool = ports.Count > 0 && rng.NextDouble() < 0.75 ? ports : near;
            var pk = pool[rng.Next(pool.Count)]; var dest = pk.s; int d = pk.d;
            int tier = Math.Clamp((int)(power / 4f), 0, 4); int df = dest.MySPI.Faction;
            (string name, string color) who = contraband ? ("Un intermediario anonimo", "#8B90A0") : df >= 0 && df != 4 && Array.IndexOf(GalaxyWar.Factions, df) >= 0 && Factions.Level(df) >= -1 && rng.NextDouble() < 0.6 ? Issuers().FirstOrDefault(x => x.name == GalaxyWar.Name(df)) : ("Gilda dei Mercanti", "#FFD75E");
            if (who.name == null) who = ("Gilda dei Mercanti", "#FFD75E");
            string cargo = contraband ? Illegal[rng.Next(Illegal.Length)] : Legal[rng.Next(Legal.Length)];
            int reward = (int)Math.Round((700 + 450 * d) * (1 + tier * 0.25) * (contraband ? 1.8 * Calling.Bonus("corsari") : Calling.Bonus("commercio") * (Calling.Is("franco") ? 1.2 : 1)) * (1 + Factions.RewardBonus(who.name)) * (1 + Math.Clamp(Trust(who.name), -5, 10) * 0.04) / 50) * 50;
            string port = PortName(dest);
            return new ContractOffer
            {
                id = rng.Next(1, int.MaxValue), kind = contraband ? "contrabbando" : "trasporto", target = cargo, sector = dest.ID, tier = Math.Min(4, d - 2 + (contraband ? 1 : 0)),
                issuer = who.name, color = who.color, family = contraband ? "Contrabbando" : "Trasporto", title = $"{char.ToUpper(cargo[0]) + cargo[1..]} per {port}",
                desc = $"Consegnare {cargo} nel settore {Places.Of(dest.ID)}, a {d} salti." + (contraband ? " Merce illegale: le ispezioni la confischerebbero." : " Il carico attira i predoni."),
                reward = reward, expires = Jumps() + d + 3 + rng.Next(2),
            };
        }

        public static void Deliver(bool success, string why = null)
        {
            var c = Saga.State.contract; if (c == null || !(c.kind is "trasporto" or "contrabbando" or "ambasciata")) return;
            Saga.State.contract = null; lastSent = "";
            var tr = Saga.State.trust; tr[c.issuer] = Math.Clamp(Trust(c.issuer) + (success ? 1 : -2), -10, 20);
            if (success) try { PLServer.Instance.CurrentCrewCredits += c.reward; } catch { }
            if (c.kind == "ambasciata")
            {
                int fa = FactionOf(c.issuer); var cc = new ContractOffer { issuer = c.issuer, side = -1 }; Factions.OnContract(cc, success);
                if (success) { Calling.Add("diplomazia", 3); Diplomacy.Change(fa, c.side, (int)(25 * Calling.Bonus("diplomazia")), $"Grazie alla mediazione della «{(string)Stations.MyShip.ShipName}»"); Factions.Change(c.side, 5, null); Saga.State.onore += 1; }
                else Diplomacy.Change(fa, c.side, -10, "L'ambasciata non è mai arrivata");
            }
            else if (c.kind == "trasporto") { Factions.OnContract(c, success); if (success) Calling.Add("commercio"); } else if (success) { Saga.State.infamia += 1; Calling.Add("corsari", 2); }
            if (success && Home.Has && c.sector == Home.H.sector) Home.Prosper(8, "Una consegna per casa");
            if (success && Bases.At(c.sector) != null) Bases.Supply(c.sector);
            Morale.Change(success ? 3 : -3, null); if (success) { Competitors.Renown(Math.Max(5, c.reward / 150)); Saga.State.contractsDone++; if (c.kind == "ambasciata") Saga.State.embassies++; Leaders.OnDeed("pace", -1); }
            Service.Add(success ? "contratti.riusciti" : "contratti.falliti", 1); if (success) Service.Add(c.kind == "contrabbando" ? "commercio.contrabbando" : "commercio.consegne", 1);
            if (c.kind == "ambasciata") { string m2 = success ? $"Ambasciata riuscita: {c.target} è arrivato a destinazione. {c.reward} crediti dalla {c.issuer}." : $"Ambasciata fallita{(why != null ? ": " + why : "")}."; Director.Notify("AMBASCIATA", m2); Saga.Chronicle(m2); Unplot(); Saga.MarkDirty(); return; }
            string msg = success ? $"Consegna completata: {c.target} a destinazione. {c.reward} crediti da {c.issuer}." : $"Consegna mancata ({c.target}){(why != null ? ": " + why : "")}.";
            Director.Notify(success ? "CONSEGNA" : "BACHECA", msg); Saga.Chronicle(msg);
            if (success && c.kind == "trasporto" && rng.NextDouble() < 0.4) News.Post("commercio", $"Arrivato a destinazione un carico di {c.target}, portato dalla «{(string)Stations.MyShip.ShipName}».");
            Unplot(); Saga.MarkDirty();
        }
        // Ispezione o assalto: il carico viene perso.
        public static void LoseCargo(string why) => Deliver(false, why);
        public static string ForceTransport(bool contraband)
        {
            var o = Transport(Director.Power(Stations.MyShip, out _, out _), contraband); if (o == null) return "nessuna destinazione adatta";
            var b = Saga.State.board; if (b.Count >= Size) b.RemoveAt(b.Count - 1); b.Insert(0, o); Touch(); return $"{o.title}: {o.desc} ({o.reward} crediti)";
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

// Borsa delle merci: otto merci con prezzi diversi in ogni porto, che seguono la galassia.
// - Ogni fazione produce alcune merci (costano poco nel suo spazio) e ne chiede altre (si vendono bene).
// - Crisi, guerre, stagioni, minaccia e le nostre basi spostano i prezzi; comprare molto in un porto fa salire il
//   prezzo, vendere molto lo fa scendere, e il mercato torna a posto in qualche salto.
// - Armi e reliquie aliene sono contrabbando: rendono molto, ma le pattuglie e le dogane le cercano.
// - La stiva ha una capienza; un carico ricco attira i predoni.
// Si compra e si vende in porto (o al cantiere di una nostra base) dalla scheda Mercato del pilota.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class BankLoan { public int owed, taken, due, late; }
    public class MarketState { public Dictionary<string, int> cargo = new(), sat = new(), paid = new(); public Dictionary<string, string> best = new(); public int profit, trades, lastJump = -1, npcTrades; public List<string> npcLog = new(); }

    public static class Market
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static MarketState M => Saga.State.market ??= new MarketState();

        public sealed class Good { public string id = "", name = ""; public int price; public bool contraband; public int[] makes = Array.Empty<int>(), wants = Array.Empty<int>(); }
        // fazioni: 0 C.U., 1 A.O.G., 2 W.D., 5 Polytechnic
        public static readonly Good[] Goods =
        {
            new() { id = "deuterio", name = "Deuterio", price = 60, makes = new[] { 2 }, wants = new[] { 0, 1 } },
            new() { id = "minerali", name = "Minerali grezzi", price = 45, makes = new[] { 1 }, wants = new[] { 2, 5 } },
            new() { id = "grano", name = "Grano idroponico", price = 35, makes = new[] { 0 }, wants = new[] { 1, 2 } },
            new() { id = "medicinali", name = "Medicinali", price = 120, makes = new[] { 5 }, wants = new[] { 1 } },
            new() { id = "ricambi", name = "Ricambi", price = 90, makes = new[] { 2 }, wants = new[] { 0 } },
            new() { id = "elettronica", name = "Elettronica", price = 150, makes = new[] { 5, 2 }, wants = new[] { 0, 1 } },
            new() { id = "vino", name = "Vino di Rigel", price = 200, makes = new[] { 1 }, wants = new[] { 0, 5 } },
            new() { id = "armi", name = "Armi (contrabbando)", price = 260, contraband = true, makes = new[] { 1 }, wants = new[] { 2 } },
            new() { id = "reliquie", name = "Reliquie aliene (contrabbando)", price = 420, contraband = true, makes = Array.Empty<int>(), wants = new[] { 5, 1 } },
        };
        public static Good G(string id) => Goods.FirstOrDefault(g => g.id == id);
        // nome della merce dentro una frase: minuscola iniziale, nomi propri intatti («vino di Rigel»)
        public static string Low(string n) => string.IsNullOrEmpty(n) ? "" : char.ToLowerInvariant(n[0]) + n[1..];
        public static int Capacity => 20 + (int)Loot.Mine("stiva") + Shipyard.Level("stiva") * 6 + (Saga.State.bases.Any(b => b.state == 1 && b.kind == "emporio") ? 6 : 0);
        public static int Load => M.cargo.Values.Sum();
        public static int Value => M.cargo.Sum(kv => (G(kv.Key)?.price ?? 0) * kv.Value);
        public static bool Contraband() => M.cargo.Any(kv => kv.Value > 0 && (G(kv.Key)?.contraband ?? false));

        static int Seed() { try { return (int)PLServer.Instance.GalaxySeed; } catch { return 0; } }
        static PLSectorInfo Cur() { try { return PLServer.GetCurrentSector(); } catch { return null; } }
        static int Fac(PLSectorInfo s) { try { return s?.MySPI?.Faction ?? -1; } catch { return -1; } }

        // Prezzo di riferimento di una merce in un settore (prima dello scarto fra acquisto e vendita).
        public static int Price(string id, int sector)
        {
            var g = G(id); var s = PLServer.GetSectorWithID(sector); if (g == null || s == null) return 0;
            int h = (Seed() * 31 + sector * 17 + id.GetHashCode()) & 0x7fffffff;
            double p = g.price * (0.8 + (h % 1000) / 1000.0 * 0.45); // carattere del porto
            int f = Fac(s);
            if (g.makes.Contains(f)) p *= 0.72; if (g.wants.Contains(f)) p *= 1.35;
            var cr = Crises.Cur;
            if (cr != null)
            {
                if (cr.kind == "epidemia" && id == "medicinali") p *= 2.0;
                if (cr.kind == "guerra" && id is "armi" or "ricambi" or "deuterio") p *= 1.6;
                if (cr.kind == "blocco") p *= 1.3;
            }
            if (Calling.Is("franco")) p *= g.contraband ? 0.85 : 1.1;
            if (Calling.Is("tempeste") && id == "ricambi") p *= 1.3;
            if (Saga.State.threat != null && Saga.State.threat.stage >= 3 && id is "medicinali" or "armi") p *= 1.4;
            if (id == "minerali" && Saga.State.bases.Any(b => b.state == 1 && b.kind == "miniera" && Nemesis.Distance(s, PLServer.GetSectorWithID(b.sector)) <= 2)) p *= 0.7;
            if (id == "reliquie" && f == 4) p *= 0.5;
            p *= global::Melchior.Ports.PriceMul(sector, id);
            int sat = M.sat.TryGetValue(Key(sector, id), out var v) ? v : 0; p *= Math.Clamp(1 + sat * 0.03, 0.5, 1.6);
            return Math.Max(5, (int)p);
        }
        static string Key(int sector, string id) => sector + ":" + id;
        public static int BuyPrice(string id, int sector) => (int)(Price(id, sector) * 1.08);
        public static int SellPrice(string id, int sector) => (int)(Price(id, sector) * 0.92 * global::Melchior.Ports.TrustPrice(sector) * Talents.Mul("negoziatore", 0.03f) * (1f + Loot.Mine("commercio") + PawnLoot.Crew("commercio")) * Calling.Bonus("commercio") * (G(id)?.contraband == true ? Calling.Bonus("corsari") : 1f));

        public static bool CanTrade() { if (!Shipyard.InPort()) return false; var s = Cur(); return s == null || !global::Melchior.Ports.Closed(s.ID) || Bases.OwnShipyardHere() != null; }

        public static string Buy(string id, int n)
        {
            var g = G(id); var s = Cur(); if (g == null || s == null) return "merce sconosciuta";
            if (!CanTrade()) return "si commercia solo in porto";
            n = Math.Min(n, Capacity - Load); if (n <= 0) return "la stiva è piena";
            int cost = 0; for (int i = 0; i < n; i++) { cost += BuyPrice(id, s.ID); Saturate(s.ID, id, 1); }
            int have = 0; try { have = PLServer.Instance.CurrentCrewCredits; } catch { }
            if (cost > have) { for (int i = 0; i < n; i++) Saturate(s.ID, id, -1); return "crediti insufficienti"; }
            try { PLServer.Instance.CurrentCrewCredits -= cost; } catch { }
            M.cargo[id] = M.cargo.GetValueOrDefault(id) + n; M.paid[id] = M.paid.GetValueOrDefault(id) + cost;
            M.trades++; Saga.MarkDirty(); GalaxyView.Dirty();
            return $"Imbarcati {n} × {Market.Low(g.name)} per {cost} crediti.";
        }
        public static string Sell(string id, int n)
        {
            var g = G(id); var s = Cur(); if (g == null || s == null) return "merce sconosciuta";
            if (!CanTrade()) return "si commercia solo in porto";
            int have = M.cargo.GetValueOrDefault(id); n = Math.Min(n, have); if (n <= 0) return "non ne abbiamo in stiva";
            if (g.contraband && Fac(s) >= 0 && Fac(s) != 1 && Fac(s) != 4 && !Calling.Is("franco") && Factions.Level(Fac(s)) < 2 && rng.NextDouble() < 0.15)
            {
                Wanted.Heat(6); Factions.Change(Fac(s), -2, null);
                Director.Notify("MERCATO NERO", "Il compratore era un informatore della dogana. La vendita va a buon fine, ma il nostro nome finisce su un registro.");
            }
            int gain = 0; for (int i = 0; i < n; i++) { gain += SellPrice(id, s.ID); Saturate(s.ID, id, -1); }
            int paid = have > 0 ? M.paid.GetValueOrDefault(id) * n / have : 0;
            try { PLServer.Instance.CurrentCrewCredits += gain; } catch { }
            M.cargo[id] = have - n; M.paid[id] = M.paid.GetValueOrDefault(id) - paid; if (M.cargo[id] <= 0) { M.cargo.Remove(id); M.paid.Remove(id); }
            int profit = gain - paid; M.profit += profit; M.trades++; global::Melchior.Ports.OnSold(s.ID, id, n); Feats.Trade(profit); if (n >= 5) try { Leaders.OnDeed("commercio", s.MySPI?.Faction ?? -1); } catch { }
            if (profit > 0) { Calling.Add(g.contraband ? "corsari" : "commercio"); if (profit >= 1500) Competitors.Renown(profit / 500); }
            Saga.MarkDirty(); GalaxyView.Dirty();
            if (profit >= 3000) News.Post("commercio", $"La «{Ship()}» ha concluso un affare d'oro al settore {Places.Of(s.ID)}: {Market.Low(g.name)}, {profit} crediti di guadagno.");
            return $"Venduti {n} × {Market.Low(g.name)} per {gain} crediti ({(profit >= 0 ? "guadagno" : "perdita")} {Math.Abs(profit)})." + global::Melchior.Ports.OnOrderSale(s.ID, id, n);
        }
        static void Saturate(int sector, string id, int d) { string k = Key(sector, id); int v = M.sat.GetValueOrDefault(k) + d; if (v == 0) M.sat.Remove(k); else M.sat[k] = Math.Clamp(v, -12, 15); }
        static string Ship() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }

        // ---- commercio in volo: i mercanti del traffico hanno la loro stiva e trattano con noi
        public sealed class SkyDeal { public string ship = "", good = "", want = ""; public int qty, price, buyBack; }
        public static SkyDeal Deal;
        public static bool OfferSky(string ship)
        {
            var s = Cur(); if (s == null) return false;
            var goods = Goods.Where(g => !g.contraband || rng.NextDouble() < 0.15).ToList(); var g = goods[rng.Next(goods.Count)];
            var want = M.cargo.Keys.Where(k => M.cargo[k] > 0 && k != g.id).OrderBy(_ => rng.Next()).FirstOrDefault() ?? "";
            Deal = new SkyDeal { ship = ship, good = g.id, qty = 5 + rng.Next(8), price = (int)(Price(g.id, s.ID) * (0.82 + rng.NextDouble() * 0.15)), want = want, buyBack = want.Length > 0 ? (int)(Price(want, s.ID) * (1.0 + rng.NextDouble() * 0.12)) : 0 };
            return Encounters.Start("commercio_volo", true) || (Deal = null) != null;
        }
        public static string SkyBuy(int n)
        {
            var d = Deal; if (d == null) return "Il mercante se n'è andato.";
            n = Math.Min(Math.Min(n, d.qty), Capacity - Load); if (n <= 0) return "Non c'è posto in stiva.";
            int cost = n * d.price; int cash = 0; try { cash = PLServer.Instance.CurrentCrewCredits; } catch { } if (cash < cost) return "Non abbiamo abbastanza crediti.";
            try { PLServer.Instance.CurrentCrewCredits -= cost; } catch { }
            M.cargo[d.good] = M.cargo.GetValueOrDefault(d.good) + n; M.paid[d.good] = M.paid.GetValueOrDefault(d.good) + cost; M.trades++; Deal = null; Saga.MarkDirty(); GalaxyView.Dirty(); Feats.SkyTrade();
            return $"Trasbordo in volo: {n} casse di {Low(G(d.good).name)} dalla «{d.ship}» per {cost} crediti.";
        }
        public static string SkySell()
        {
            var d = Deal; if (d == null || d.want.Length == 0) return "Il mercante se n'è andato.";
            int have = M.cargo.GetValueOrDefault(d.want); int n = Math.Min(have, 10); if (n <= 0) return "Non ne abbiamo più.";
            int gain = n * d.buyBack; int paid = M.paid.GetValueOrDefault(d.want) * n / Math.Max(1, have);
            try { PLServer.Instance.CurrentCrewCredits += gain; } catch { }
            M.cargo[d.want] = have - n; M.paid[d.want] = M.paid.GetValueOrDefault(d.want) - paid; if (M.cargo[d.want] <= 0) { M.cargo.Remove(d.want); M.paid.Remove(d.want); }
            M.profit += gain - paid; M.trades++; Deal = null; Saga.MarkDirty(); GalaxyView.Dirty(); Feats.SkyTrade(); Feats.Trade(gain - paid);
            return $"Trasbordo in volo: {n} casse di {Low(G(d.want).name)} alla «{d.ship}» per {gain} crediti.";
        }

        // ---- banca della Gilda: prestiti con interessi; chi non paga riceve la visita degli esattori
        public static string Borrow(int amount)
        {
            var b = Saga.State.bank ??= new BankLoan(); if (b.owed > 0) return "Abbiamo già un debito con la Gilda.";
            b.owed = amount; b.taken = amount; b.due = Obscured.Jumps() + 15; b.late = 0; try { PLServer.Instance.CurrentCrewCredits += amount; } catch { }
            Saga.MarkDirty(); GalaxyView.Dirty();
            return $"La Gilda ci presta {amount} crediti al 2% a salto. Scadenza: salto {b.due}. Dopo, arrivano gli esattori.";
        }
        public static string Repay()
        {
            var b = Saga.State.bank; if (b == null || b.owed <= 0) return "Nessun debito con la Gilda.";
            if (!CanTrade()) return "Si rimborsa in porto, allo sportello della Gilda.";
            int cash = 0; try { cash = PLServer.Instance.CurrentCrewCredits; } catch { } int pay = Math.Min(cash, b.owed); if (pay <= 0) return "Non abbiamo crediti da versare.";
            try { PLServer.Instance.CurrentCrewCredits -= pay; } catch { } b.owed -= pay; Saga.MarkDirty(); GalaxyView.Dirty();
            if (b.owed <= 0) { b.owed = 0; Feats.DebtFree(); if (b.late > 0) Wanted.Heat(-10); return $"Debito saldato con la Gilda: {pay} crediti. Siamo di nuovo liberi."; }
            return $"Versati {pay} crediti alla Gilda; restano {b.owed}.";
        }
        public static void BankTick(int j)
        {
            var b = Saga.State.bank; if (b == null || b.owed <= 0) return;
            b.owed += Math.Max(1, (int)(b.owed * 0.02));
            if (j > b.due)
            {
                b.late++;
                if (b.late == 1) { Director.Notify("DEBITO SCADUTO", $"La Gilda reclama {b.owed} crediti. Gli esattori sono già in viaggio."); Wanted.Heat(8); }
                if (rng.NextDouble() < 0.35) { var n = Traffic.Collectors(); if (n) Director.Notify("ESATTORI", "Una nave degli esattori della Gilda esce dalla curvatura: vogliono i loro crediti, o la nostra nave."); }
            }
            Saga.MarkDirty();
        }
        public static string BankLine() { var b = Saga.State.bank; return b == null || b.owed <= 0 ? "" : $"debito con la Gilda {b.owed} cr (scadenza salto {b.due})"; }

        // Merci prese senza pagarle (bottino): fin dove c'è posto in stiva.
        public static int Have(string id) => M.cargo.GetValueOrDefault(id);
        public static int Add(string id, int n) { n = Math.Min(n, Capacity - Load); if (n <= 0 || G(id) == null) return 0; M.cargo[id] = M.cargo.GetValueOrDefault(id) + n; Saga.MarkDirty(); GalaxyView.Dirty(); return n; }
        // Merci passate in dispensa (grano, medicinali): escono dalla stiva senza essere vendute.
        public static void Take(string id, int n) { int have = M.cargo.GetValueOrDefault(id); n = Math.Min(n, have); if (n <= 0) return; M.paid[id] = M.paid.GetValueOrDefault(id) * (have - n) / Math.Max(1, have); M.cargo[id] = have - n; if (M.cargo[id] <= 0) { M.cargo.Remove(id); M.paid.Remove(id); } Saga.MarkDirty(); GalaxyView.Dirty(); }
        // Perdite: predoni, dogane, sganci d'emergenza.
        public static string LoseShare(double share, string why)
        {
            var lost = new List<string>();
            foreach (var k in M.cargo.Keys.ToList()) { int n = (int)Math.Ceiling(M.cargo[k] * share); if (n <= 0) continue; int had = M.cargo[k]; M.paid[k] = M.paid.GetValueOrDefault(k) * (had - n) / Math.Max(1, had); M.cargo[k] -= n; if (M.cargo[k] <= 0) { M.cargo.Remove(k); M.paid.Remove(k); } lost.Add($"{n} {Market.Low(G(k)?.name)}"); }
            Saga.MarkDirty(); GalaxyView.Dirty();
            return lost.Count == 0 ? "" : $"Perduti ({why}): {string.Join(", ", lost)}.";
        }
        public static string Confiscate()
        {
            var lost = new List<string>();
            foreach (var k in M.cargo.Keys.ToList()) if (G(k)?.contraband == true) { lost.Add($"{M.cargo[k]} {Market.Low(G(k).name)}"); M.cargo.Remove(k); M.paid.Remove(k); }
            Saga.MarkDirty(); GalaxyView.Dirty(); return string.Join(", ", lost);
        }

        // A ogni salto: i mercati tornano lentamente a posto; nei porti si annotano i prezzi migliori.
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                int j = Obscured.Jumps(); if (j == M.lastJump) return; M.lastJump = j;
                foreach (var k in M.sat.Keys.ToList()) { int v = M.sat[k]; v += v > 0 ? -1 : 1; if (v == 0) M.sat.Remove(k); else M.sat[k] = v; }
                NpcTrades(); BankTick(j);
                var s = Cur(); if (s != null && CanTrade()) Remember(s.ID);
                if (s != null && Research.Has("mercato"))
                    try { var ps = Ports().ToHashSet(); foreach (var id in Within(s, 2)) if (ps.Contains(id)) Remember(id); } catch { }
                Saga.MarkDirty(); GalaxyView.Dirty();
            }
            catch (Exception e) { Plugin.L.LogWarning("Mercato: " + e.Message); }
        }
        // ---- mercanti NPC: fanno gli stessi affari che possiamo fare noi, e muovono gli stessi prezzi
        static readonly string[] Traders = { "Gazza Ladra", "Mercurio Lento", "Botte Piena", "Rondine di Vega", "Vecchia Zia", "Sale e Pepe", "Bilancia d'Oro", "Carovaniera", "Stiva Allegra", "Pellicano", "Fortuna di Kepler", "Contrabbasso" };
        static List<int> ports; static int portsSeed = int.MinValue;
        public static bool IsPort(int sector) => Ports().Contains(sector);
        static List<int> Ports()
        {
            int seed = Seed(); if (ports != null && portsSeed == seed) return ports;
            ports = new List<int>(); portsSeed = seed;
            try { foreach (var kv in PLGlobal.Instance.Galaxy.AllSectorInfos) { var s = kv.Value; if (s?.MySPI != null && s.VisualIndication != ESectorVisualIndication.NONE && s.MissionSpecificID == -1) ports.Add(s.ID); } } catch { }
            return ports;
        }
        // settori entro n salti (visita in ampiezza dei vicini, leggera)
        public static HashSet<int> Within(PLSectorInfo from, int depth)
        {
            var seen = new HashSet<int>(); if (from == null) return seen; seen.Add(from.ID); var frontier = new List<PLSectorInfo> { from };
            for (int d = 0; d < depth && frontier.Count > 0; d++)
            {
                var next = new List<PLSectorInfo>();
                foreach (var s in frontier) { try { var nb = s.Neighbors; for (int i = 0; i < nb.Count; i++) { var x = nb[i]; if (x != null && seen.Add(x.ID)) next.Add(x); } } catch { } }
                frontier = next;
            }
            return seen;
        }
        static void NpcTrades()
        {
            var all = Ports(); var cur = Cur(); if (all.Count < 2 || cur == null) return;
            var portSet = all.ToHashSet();
            var near = Within(cur, 6).Where(portSet.Contains).ToList();
            int n = 4 + (Calling.Is("franco") ? 3 : 0) - (Crises.Cur?.kind == "blocco" ? 3 : 0) + Saga.State.bases.Count(b => b.state == 1 && b.kind == "emporio");
            for (int i = 0; i < n; i++)
            {
                int a = (near.Count > 0 && rng.NextDouble() < 0.7 ? near : all)[rng.Next((near.Count > 0 ? near : all).Count)];
                var sa = PLServer.GetSectorWithID(a); var dests = Within(sa, 5).Where(id => id != a && portSet.Contains(id)).ToList(); if (dests.Count == 0) continue;
                int b = dests[rng.Next(dests.Count)];
                var g = Goods.Where(x => !x.contraband || rng.NextDouble() < 0.2).OrderByDescending(x => SellPrice(x.id, b) - BuyPrice(x.id, a)).First();
                if (SellPrice(g.id, b) <= BuyPrice(g.id, a)) continue;
                int q = 2 + rng.Next(3); Saturate(a, g.id, q); Saturate(b, g.id, -q); M.npcTrades++; global::Melchior.Ports.OnDelivered(b, g.id, q);
                string who = Traders[rng.Next(Traders.Length)];
                M.npcLog.Add($"«{who}» ha portato {q * 5} casse di {Market.Low(g.name).Replace(" (contrabbando)", "")} dal settore {a} al {b}");
                if (M.npcLog.Count > 8) M.npcLog.RemoveAt(0);
            }
            if (M.npcLog.Count > 0 && rng.NextDouble() < 0.15) News.Post("commercio", M.npcLog[^1] + ": là i prezzi scendono.");
        }

        // Miglior prezzo di vendita noto per ogni merce (porti visitati o voci del traffico).
        public static void Remember(int sector)
        {
            foreach (var g in Goods)
            {
                int p = SellPrice(g.id, sector);
                if (!M.best.TryGetValue(g.id, out var old) || int.Parse(old.Split('|')[1]) < p || int.Parse(old.Split('|')[0]) == sector) M.best[g.id] = $"{sector}|{p}";
            }
        }
        // Una voce dal traffico: il porto vicino che paga meglio una merce.
        public static string Rumor()
        {
            var cur = Cur(); if (cur == null) return null;
            var ports = new List<PLSectorInfo>();
            try { var ps = Ports().ToHashSet(); foreach (var id in Within(cur, 4)) if (id != cur.ID && ps.Contains(id)) ports.Add(PLServer.GetSectorWithID(id)); } catch { }
            if (ports.Count == 0) return null;
            var g = Goods[rng.Next(Goods.Length)]; var best = ports.OrderByDescending(p => SellPrice(g.id, p.ID)).First();
            int price = SellPrice(g.id, best.ID); M.best[g.id] = $"{best.ID}|{price}"; Saga.MarkDirty(); GalaxyView.Dirty();
            return $"al settore {Places.Of(best.ID)} pagano {price} crediti per {Market.Low(g.name).Replace(" (contrabbando)", "")}";
        }

        // ---- richieste dal pannello
        public static void Request(string op, string arg)
        {
            if (Host) { Execute(op, arg); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|mercato|{op}|{arg}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var p = msg.Split('|'); if (p.Length >= 4 && Host) Execute(p[2], p[3]); }
        static void Execute(string op, string arg)
        {
            var a = arg.Split(','); string id = a[0]; int n = a.Length > 1 && int.TryParse(a[1], out var x) ? x : 1;
            string r = op switch { "compra" => Buy(id, n), "vendi" => Sell(id, n), "rotta" => Route(id), "scorte" => Logistics.Restock(), "rimborsa" => Repay(), _ => "" };
            if (r.Length > 0) Stations.Announce("MERCATO", r);
        }
        static string Route(string id) { if (!M.best.TryGetValue(id, out var b)) return "Nessun prezzo noto per questa merce."; int s = int.Parse(b.Split('|')[0]); Course.Plot(s); return $"Rotta tracciata verso il settore {Places.Of(s)}."; }

        // ---- vista per il pannello (anche sui client)
        public class Row { public string id = "", name = "", best = ""; public int have, buy, sell, paid; public bool contraband; }
        public class Panel { public List<Row> rows = new(); public int load, cap, profit, value; public bool port; public string where = "", supplies = "", npc = "", portLine = "", orders = "", bank = ""; }
        public static Panel Snapshot()
        {
            var p = new Panel { load = Load, cap = Capacity, profit = M.profit, value = Value, port = CanTrade(), supplies = Logistics.Line(), npc = M.npcLog.Count > 0 ? M.npcLog[^1] : "", orders = global::Melchior.Ports.OrdersLine(), bank = BankLine() };
            var s = Cur(); p.where = s == null ? "" : Places.Of(s.ID);
            if (s != null) p.portLine = global::Melchior.Ports.Line(s.ID);
            foreach (var g in Goods)
            {
                int have = M.cargo.GetValueOrDefault(g.id);
                string best = M.best.TryGetValue(g.id, out var b) ? $"{b.Split('|')[1]} cr al settore {b.Split('|')[0]}" : "";
                p.rows.Add(new Row { id = g.id, name = g.name.Replace(" (contrabbando)", ""), contraband = g.contraband, have = have, buy = s != null ? BuyPrice(g.id, s.ID) : 0, sell = s != null ? SellPrice(g.id, s.ID) : 0, paid = have > 0 ? M.paid.GetValueOrDefault(g.id) / have : 0, best = best });
            }
            return p;
        }
        public static string Describe() => $"stiva {Load}/{Capacity} (valore {Value}): {string.Join(", ", M.cargo.Select(kv => kv.Key + " " + kv.Value))}; guadagno totale {M.profit}, scambi {M.trades}, scambi NPC {M.npcTrades} (ultimo: {(M.npcLog.Count > 0 ? M.npcLog[^1] : "-")}); prezzi qui: {(Cur() == null ? "-" : string.Join(", ", Goods.Select(g => $"{g.id} {BuyPrice(g.id, Cur().ID)}/{SellPrice(g.id, Cur().ID)}")))}";
    }
}

// Porti vivi: ogni porto ha prosperità, sicurezza e una sua vita fatta di eventi, che cambiano col traffico, con i
// commerci (nostri e dei mercanti NPC), con la guerra e con quello che facciamo noi.
// Eventi: carenza di una merce (la si paga quasi il triplo finché qualcuno non la porta), sciopero dei portuali
// (mercato e rifornimenti chiusi), festa (vino caro, licenza a terra più allegra), blocco dei pirati (assalti nella
// zona finché qualcuno non li caccia), boom economico (merci di lusso care, più traffico), epidemia locale
// (medicinali cari, nessuno vuole imbarcarsi).
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class PortState { public int prosperity = 50, security = 50, until = -1, progress, trust; public string ev = "", good = ""; }
    // Ordine di un porto: consegnare una merce in un altro porto entro qualche salto.
    public class PortOrder { public int from, to, qty, done, reward, until; public string good = ""; }

    public static class Ports
    {
        static readonly Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static Dictionary<string, PortState> S => Saga.State.ports ??= new Dictionary<string, PortState>();
        static int lastJump = -1;

        public static PortState Of(int sector)
        {
            if (!Saga.Loaded || sector < 0) return null;
            if (S.TryGetValue(sector.ToString(), out var p)) return p;
            if (!Market.IsPort(sector)) return null;
            p = Default(sector);
            S[sector.ToString()] = p; return p;
        }
        static PortState Default(int sector) { int h = (sector * 2654435 + 977) & 0x7fffffff; return new PortState { prosperity = 30 + h % 50, security = 25 + (h / 7) % 60 }; }
        // i porti lontani e tranquilli, rimasti come all'inizio, non serve ricordarli: la cronaca resta leggera
        static void Prune(PLSectorInfo cur)
        {
            if (S.Count < 40) return; var near = Market.Within(cur, 8);
            foreach (var k in S.Keys.ToList())
            {
                if (!int.TryParse(k, out int id) || near.Contains(id)) continue;
                var p = S[k]; var d = Default(id);
                if (p.ev.Length == 0 && Math.Abs(p.prosperity - d.prosperity) < 10 && Math.Abs(p.security - d.security) < 10) S.Remove(k);
            }
        }
        static string Name(int sector) => Places.Of(sector);

        public static string EvName(string ev) => ev switch { "carenza" => "carenza", "sciopero" => "sciopero dei portuali", "festa" => "festa", "blocco" => "blocco dei pirati", "boom" => "boom economico", "epidemia" => "epidemia", _ => "" };

        // ---- effetti
        public static float PriceMul(int sector, string good)
        {
            var p = Host && Saga.Loaded && S.TryGetValue(sector.ToString(), out var x) ? x : null; if (p == null || p.ev.Length == 0) return 1f;
            return p.ev switch
            {
                "carenza" when good == p.good => 2.6f,
                "festa" when good == "vino" => 1.6f,
                "blocco" => 1.3f,
                "boom" when good is "elettronica" or "vino" => 1.4f,
                "epidemia" when good == "medicinali" => 2.5f,
                _ => 1f,
            } * (0.9f + (100 - p.prosperity) / 500f); // i porti poveri pagano un po' di più
        }
        public static bool Closed(int sector) => Host && Saga.Loaded && S.TryGetValue(sector.ToString(), out var p) && p.ev == "sciopero";
        public static bool Blockade(int sector) => Host && Saga.Loaded && S.TryGetValue(sector.ToString(), out var p) && p.ev == "blocco";
        public static float Traffic(int sector) { var p = Host ? Of(sector) : null; return p == null ? 1f : 0.7f + p.prosperity / 100f * 0.6f + (p.ev is "boom" or "festa" ? 0.3f : 0f) - (p.ev == "blocco" ? 0.3f : 0f); }
        public static bool NoRecruits(int sector) => Host && Saga.Loaded && S.TryGetValue(sector.ToString(), out var p) && p.ev == "epidemia";

        // ---- eventi
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                int j = Obscured.Jumps(); if (j == lastJump) return; lastJump = j;
                var cur = PLServer.GetCurrentSector(); if (cur == null) return;
                Prune(cur);
                var near = Market.Within(cur, 5).Where(Market.IsPort).ToList();
                foreach (var id in near)
                {
                    var p = Of(id); if (p == null) continue;
                    if (p.ev.Length > 0 && j >= p.until) End(id, p, false);
                    else if (p.ev.Length == 0 && rng.NextDouble() < 0.05) Start(id, p, null);
                    // la prosperità segue la guerra e la sicurezza
                    int f = -1; try { f = PLServer.GetSectorWithID(id).MySPI.Faction; } catch { }
                    if (f == 4) { p.prosperity = Math.Max(0, p.prosperity - 2); p.security = Math.Max(0, p.security - 2); }
                    else if (p.prosperity < 50 && rng.NextDouble() < 0.3) p.prosperity++;
                }
                ExpireOrders(j);
                if (Market.IsPort(cur.ID) && rng.NextDouble() < 0.45) OfferOrder(cur.ID);
                if (Festive(cur.ID)) { Morale.Change(3, null); Director.Notify("FESTA AL PORTO", "Il porto è in festa: l'equipaggio scende a terra e torna a bordo cantando."); }
                Saga.MarkDirty(); GalaxyView.Dirty();
            }
            catch (Exception e) { Plugin.L.LogWarning("Porti: " + e.Message); }
        }

        public static string Start(int id, PortState p, string ev)
        {
            p ??= Of(id); if (p == null) return "non è un porto";
            var kinds = new List<string> { "carenza", "carenza", "sciopero", "festa", "boom", "epidemia" };
            if (p.security < 45) { kinds.Add("blocco"); kinds.Add("blocco"); }
            if (p.prosperity > 60) kinds.Add("boom");
            ev ??= kinds[rng.Next(kinds.Count)];
            p.ev = ev; p.progress = 0; p.until = Obscured.Jumps() + (ev == "carenza" ? 8 : ev == "sciopero" ? 3 + rng.Next(3) : 5 + rng.Next(5));
            if (ev == "carenza") { var goods = Market.Goods.Where(g => !g.contraband).ToList(); p.good = goods[rng.Next(goods.Count)].id; }
            string text = ev switch
            {
                "carenza" => $"Al porto del settore {Name(id)} manca {Market.Low(Market.G(p.good).name)}: lo pagano quasi il triplo, e chi ne porta almeno dieci casse sarà ricordato.",
                "sciopero" => $"Sciopero dei portuali al settore {Name(id)}: mercato e rifornimenti chiusi per qualche salto.",
                "festa" => $"Al porto del settore {Name(id)} si fa festa: vino a fiumi, licenze a terra indimenticabili.",
                "blocco" => $"I pirati hanno messo il blocco al porto del settore {Name(id)}: le navi in arrivo vengono assaltate. Il porto cerca chi li cacci.",
                "boom" => $"Boom economico al porto del settore {Name(id)}: elettronica e vino si vendono a peso d'oro, e il traffico cresce.",
                _ => $"Epidemia al porto del settore {Name(id)}: i medicinali valgono una fortuna e nessuno vuole imbarcarsi.",
            };
            News.Post("commercio", text); return text;
        }
        static void End(int id, PortState p, bool solved)
        {
            string ev = p.ev; p.ev = ""; p.good = ""; p.until = -1;
            if (!solved)
            {
                if (ev is "carenza" or "blocco" or "epidemia") p.prosperity = Math.Max(0, p.prosperity - 8);
                if (ev == "blocco") p.security = Math.Max(0, p.security - 5);
                if (ev == "boom") p.prosperity = Math.Min(100, p.prosperity + 8);
                News.Post("commercio", $"Al porto del settore {Name(id)} si chiude il periodo di {EvName(ev)}.");
            }
        }

        // ---- noi e gli NPC cambiamo i porti
        public static void OnSold(int sector, string good, int n)
        {
            var p = Of(sector); if (p == null) return;
            p.prosperity = Math.Min(100, p.prosperity + Math.Max(1, n / 5)); if (n >= 5) p.trust = Math.Min(30, p.trust + 1);
            if (p.ev == "carenza" && p.good == good)
            {
                p.progress += n;
                if (p.progress >= 10)
                {
                    End(sector, p, true); p.prosperity = Math.Min(100, p.prosperity + 12); p.trust = Math.Min(30, p.trust + 5); Feats.Shortage();
                    int f = -1; try { f = PLServer.GetSectorWithID(sector).MySPI.Faction; } catch { }
                    if (f >= 0 && Array.IndexOf(Diplomacy.F, f) >= 0) Factions.Change(f, 3, null);
                    Saga.State.onore += 1; Competitors.Renown(15); Morale.Change(3, null);
                    Director.Notify("CARENZA RISOLTA", $"Il porto del settore {Name(sector)} ha di nuovo {Market.Low(Market.G(good).name)}, grazie a noi. Lo racconteranno a lungo.");
                    News.Post("commercio", $"La «{(string)Stations.MyShip.ShipName}» ha messo fine alla carenza al porto del settore {Name(sector)}.");
                }
            }
            Saga.MarkDirty();
        }
        public static void OnDelivered(int sector, string good, int q)
        {
            if (!S.TryGetValue(sector.ToString(), out var p)) return;
            p.prosperity = Math.Min(100, p.prosperity + 1);
            if (p.ev == "carenza" && p.good == good) { p.progress += q; if (p.progress >= 14) { End(sector, p, true); News.Post("commercio", $"I mercanti hanno rifornito il porto del settore {Name(sector)}: la carenza è finita."); } }
        }
        public static void PirateDown(int sector)
        {
            foreach (var id in Market.Within(PLServer.GetSectorWithID(sector), 1))
            {
                if (!S.TryGetValue(id.ToString(), out var p) || p.ev != "blocco") continue;
                p.progress++; if (p.progress < 2) continue;
                End(id, p, true); p.security = Math.Min(100, p.security + 20); p.trust = Math.Min(30, p.trust + 6); Feats.Blockade(); p.prosperity = Math.Min(100, p.prosperity + 8);
                int f = -1; try { f = PLServer.GetSectorWithID(id).MySPI.Faction; } catch { }
                if (f >= 0 && Array.IndexOf(Diplomacy.F, f) >= 0) Factions.Change(f, 4, null);
                int cr = (int)(1500 * Rules.Reward); try { PLServer.Instance.CurrentCrewCredits += cr; } catch { }
                Saga.State.onore += 2; Competitors.Renown(20);
                Director.Notify("BLOCCO SPEZZATO", $"I pirati che assediavano il porto del settore {Name(id)} sono in fuga. Il porto ci paga {cr} crediti e ci apre le porte.");
                News.Post("flotta", $"La «{(string)Stations.MyShip.ShipName}» ha spezzato il blocco dei pirati al porto del settore {Name(id)}.");
            }
        }
        // ---- fiducia: il porto ci conosce (vendite, carenze risolte, ordini consegnati) e ci tratta meglio
        public static int Trust(int sector) { var p = Host && Saga.Loaded && S.TryGetValue(sector.ToString(), out var x) ? x : null; return p?.trust ?? 0; }
        public static void AddTrust(int sector, int d) { var p = Of(sector); if (p == null) return; p.trust = Math.Clamp(p.trust + d, -20, 30); Saga.MarkDirty(); }
        public static float TrustPrice(int sector) => 1f + Math.Clamp(Trust(sector), -10, 10) * 0.01f;

        // ---- ordini dei porti
        static List<PortOrder> Orders => Saga.State.orders ??= new List<PortOrder>();
        public static PortOrder Offered;
        static void OfferOrder(int from)
        {
            if (Orders.Count >= 3) return;
            var dests = Market.Within(PLServer.GetSectorWithID(from), 5).Where(id => id != from && Market.IsPort(id) && !Market.Within(PLServer.GetSectorWithID(from), 1).Contains(id)).ToList(); if (dests.Count == 0) return;
            int to = dests[rng.Next(dests.Count)]; var dp = Of(to);
            // il porto di destinazione chiede quello che gli manca, o quello che paga meglio
            var g = dp != null && dp.ev == "carenza" ? Market.G(dp.good) : Market.Goods.Where(x => !x.contraband).OrderByDescending(x => Market.SellPrice(x.id, to) - Market.BuyPrice(x.id, from) + rng.Next(40)).First();
            int qty = 5 + rng.Next(11); int dist = 2 + rng.Next(3);
            int reward = (int)((300 + qty * g.price * 0.45 + dist * 150) * Rules.Reward * (1f + Math.Max(0, Trust(from)) * 0.02f));
            Offered = new PortOrder { from = from, to = to, good = g.id, qty = qty, reward = reward, until = Obscured.Jumps() + dist * 2 + 3 };
            if (!Encounters.Start("ordine_porto", true)) Offered = null;
        }
        public static string AcceptOrder()
        {
            var o = Offered; if (o == null) return "L'ordine non c'è più."; Offered = null; Orders.Add(o); Saga.MarkDirty(); GalaxyView.Dirty();
            Course.Plot(o.to);
            return $"Ordine accettato: {o.qty} casse di {Market.Low(Market.G(o.good).name)} al porto del settore {Name(o.to)} entro il salto {o.until}. Rotta tracciata.";
        }
        // una vendita al porto giusto conta per l'ordine
        public static string OnOrderSale(int sector, string good, int n)
        {
            var o = Orders.FirstOrDefault(x => x.to == sector && x.good == good); if (o == null) return "";
            o.done += n; if (o.done < o.qty) { Saga.MarkDirty(); return $" Ordine: {o.done}/{o.qty} casse consegnate."; }
            Orders.Remove(o); try { PLServer.Instance.CurrentCrewCredits += o.reward; } catch { }
            AddTrust(o.from, 3); AddTrust(o.to, 3); var tp = Of(o.to); if (tp != null) tp.prosperity = Math.Min(100, tp.prosperity + 4);
            Competitors.Renown(6); Calling.Add("commercio"); Feats.Order();
            News.Post("commercio", $"La «{(string)Stations.MyShip.ShipName}» ha consegnato {o.qty} casse di {Market.Low(Market.G(o.good).name)} al porto del settore {Name(o.to)}, come promesso.");
            Saga.MarkDirty(); GalaxyView.Dirty();
            return $" Ordine completato: {o.reward} crediti di compenso, e due porti che si fidano di noi un po' di più.";
        }
        static void ExpireOrders(int j)
        {
            foreach (var o in Orders.Where(x => j > x.until).ToList())
            {
                Orders.Remove(o); AddTrust(o.from, -2);
                var rivals = Competitors.List(); var r = rivals.Count > 0 && rng.NextDouble() < 0.5 ? rivals[rng.Next(rivals.Count)] : null;
                if (r != null) { r.score += 8; r.deeds++; News.Post("commercio", $"La «{r.ship}» ha consegnato al posto nostro le casse di {Market.Low(Market.G(o.good).name)} attese al porto del settore {Name(o.to)}."); }
                Director.Notify("ORDINE SCADUTO", $"Non abbiamo consegnato in tempo {Market.Low(Market.G(o.good).name)} al settore {Name(o.to)}." + (r != null ? $" Ci ha pensato la «{r.ship}»." : "") + " Il porto d'origine se lo ricorderà.");
            }
        }
        public static string OrdersLine() { var l = Orders; return l.Count == 0 ? "" : "Ordini: " + string.Join("; ", l.Select(o => $"{Market.Low(Market.G(o.good).name)} {o.done}/{o.qty} → settore {o.to} (salto {o.until})")); }
        public static string ForceOrder() { var cur = PLServer.GetCurrentSector(); OfferOrder(cur.ID); return Offered == null ? "nessun ordine possibile" : "ordine offerto"; }

        public static bool Festive(int sector) => Host && Saga.Loaded && S.TryGetValue(sector.ToString(), out var p) && p.ev == "festa";

        // ---- vista
        public static string Line(int sector)
        {
            var p = Host ? Of(sector) : null; if (p == null) return "";
            string pr = p.prosperity >= 70 ? "prospero" : p.prosperity >= 40 ? "operoso" : p.prosperity >= 20 ? "in difficoltà" : "in rovina";
            string se = p.security >= 65 ? "sicuro" : p.security >= 35 ? "tranquillo" : "insidiato dai pirati";
            string ev = p.ev.Length == 0 ? "" : " · " + (p.ev == "carenza" ? $"CARENZA di {Market.Low(Market.G(p.good)?.name)} ({p.progress}/10)" : EvName(p.ev).ToUpperInvariant());
            string tr = p.trust >= 15 ? ", ci tratta da amici" : p.trust >= 5 ? ", ci conosce" : p.trust <= -5 ? ", diffida di noi" : "";
            return $"Porto {pr}, {se}{tr}{ev}";
        }
        public static string Describe()
        {
            var cur = PLServer.GetCurrentSector(); if (cur == null) return "";
            return string.Join("\n      ", Market.Within(cur, 5).Where(Market.IsPort).Select(id => $"{id}: {Line(id)} (prosperità {Of(id)?.prosperity}, sicurezza {Of(id)?.security}, fino al salto {Of(id)?.until})").Prepend("porti vicini:"));
        }
    }
}

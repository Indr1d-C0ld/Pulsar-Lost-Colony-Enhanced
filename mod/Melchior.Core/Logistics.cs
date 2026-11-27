// Logistica di bordo: viveri, medicinali e paghe degli ufficiali. Dà un peso economico al viaggio.
// - Viveri: ogni salto l'equipaggio ne consuma uno a testa. Sotto i tre salti di autonomia si razionano (il morale
//   cala); finiti i viveri si patisce la fame (il morale crolla e prima o poi qualcuno si ribella).
// - Medicinali: curano gli ufficiali feriti in metà tempo; durante un'epidemia, senza medicinali, l'equipaggio si ammala.
// - Paghe: gli ufficiali alle stazioni dei bot vogliono la loro paga a ogni salto (di più per i gradi alti). Se la
//   cassa non basta, il debito cresce, il morale cala e al primo porto qualcuno se ne va.
// Ci si rifornisce in porto dalla scheda Mercato (i prezzi seguono quelli di grano e medicinali), alle nostre basi
// (gratis), con le merci in stiva (grano e medicinali si possono passare in dispensa) e da alcune navi del traffico.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class LogisticsState { public int food = 30, med = 4, debt, hungry, lastJump = -1, paid; }

    public static class Logistics
    {
        static readonly Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static LogisticsState L => Saga.State.logistics ??= new LogisticsState();
        public const int FoodCap = 40; public static int MedCap => Research.Has("medicina") ? 15 : 10;

        static bool At(int cls, out bool bot) { bot = false; try { var p = PLServer.Instance.GetCachedFriendlyPlayerOfClass(cls, Stations.MyShip); if (p == null) return false; bot = p.IsBot; return true; } catch { return false; } }
        public static int Crew() { int n = 0; for (int c = 0; c < 5; c++) if (At(c, out _)) n++; return Math.Max(1, n); }
        public static int Wages()
        {
            int w = 0;
            for (int c = 1; c < 5; c++) if (At(c, out bool bot) && bot) { var o = Saga.State.officers.FirstOrDefault(x => x.alive && x.cls == c); w += 20 + 8 * (o?.rank ?? 0); }
            return (int)(w * (Rules.Mode == "tranquilla" ? 0.6f : 1f) * (Research.Has("automazione") ? 0.75f : 1f) * (1f - 0.08f * Talents.Ours("economo")));
        }
        public static int Autonomy => L.food / Math.Max(1, Crew());

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                int j = Obscured.Jumps(); var l = L;
                if (l.lastJump < 0) { l.lastJump = j; Saga.MarkDirty(); return; }
                if (j == l.lastJump) return;
                int jumps = Math.Min(3, j - l.lastJump); l.lastJump = j;
                for (int i = 0; i < jumps; i++) Step(l);
                Saga.MarkDirty(); GalaxyView.Dirty();
            }
            catch (Exception e) { Plugin.L.LogWarning("Logistica: " + e.Message); }
        }

        static void Step(LogisticsState l)
        {
            var me = Stations.MyShip; int crew = Crew() + Passengers.FoodLoad(); // anche i passeggeri mangiano
            // viveri
            l.food = Math.Max(0, l.food - (int)Math.Ceiling((Research.Has("idroponica") ? crew * 0.75 : crew) * (1 - 0.1 * Talents.Ours("navigatore")) * (1 - Math.Min(0.6, Loot.Mine("viveri")))));
            if (l.food == 0)
            {
                l.hungry++; Morale.Change(-4, null);
                Director.Notify("DISPENSA VUOTA", l.hungry == 1 ? "I viveri sono finiti. L'equipaggio stringe la cinghia, ma non durerà a lungo: serve un porto, una nostra base o qualcuno che venda provviste." : $"Da {l.hungry} salti si fa la fame a bordo. Il morale è a pezzi.");
                if (l.hungry >= 3 && rng.NextDouble() < 0.4 && !Saga.State.pending.Any(p => p.kind == "incontro" && p.name == "ammutinamento")) Saga.Later("incontro", 0, 1, "ammutinamento");
            }
            else
            {
                l.hungry = 0;
                if (l.food < crew * 3) { Morale.Change(-1, null); Stations.LogEvent(me.ShipID, $"Dispensa: viveri razionati, autonomia {l.food / crew} salti"); }
            }
            // medicinali: curano i feriti più in fretta
            foreach (var o in Saga.State.officers.Where(o => o.alive && o.injuredUntil > Obscured.Jumps()))
                if (l.med > 0 && o.injuredUntil - Obscured.Jumps() > 1) { l.med--; o.injuredUntil = Obscured.Jumps() + Math.Max(1, (o.injuredUntil - Obscured.Jumps()) / (Research.Has("medicina") ? 3 : 2)); Stations.LogEvent(me.ShipID, $"Infermeria: medicinali per {o.first} {o.last}, tornerà prima in servizio"); }
            if (Crises.Cur?.kind == "epidemia")
            {
                if (l.med > 0) l.med--;
                else if (rng.NextDouble() < 0.35)
                {
                    var sick = Saga.State.officers.Where(o => o.alive && o.injuredUntil <= Obscured.Jumps()).OrderBy(_ => rng.Next()).FirstOrDefault();
                    if (sick != null) { Officers.Wound(sick, 2); Morale.Change(-2, null); Director.Notify("CONTAGIO A BORDO", $"Senza medicinali l'epidemia arriva anche da noi: {sick.first} {sick.last} è in infermeria."); }
                }
            }
            // paghe
            int w = Wages();
            if (w > 0)
            {
                int cash = 0; try { cash = PLServer.Instance.CurrentCrewCredits; } catch { }
                int due = w + l.debt;
                if (cash >= due) { try { PLServer.Instance.CurrentCrewCredits -= due; } catch { } if (l.debt > 0) { Morale.Change(3, null); Stations.LogEvent(me.ShipID, $"Paghe arretrate saldate: {l.debt} crediti"); } l.debt = 0; l.paid += due; }
                else
                {
                    try { PLServer.Instance.CurrentCrewCredits = 0; } catch { }
                    l.debt = due - cash; l.paid += cash; Morale.Change(-2, null);
                    if (l.debt > w * 4 && Shipyard.InPort() && rng.NextDouble() < 0.5) Desert(l);
                    else Stations.LogEvent(me.ShipID, $"Paghe: mancano {l.debt} crediti, gli ufficiali mugugnano");
                }
            }
        }

        static void Desert(LogisticsState l)
        {
            var o = Saga.State.officers.Where(x => x.alive && x.cls >= 1).OrderBy(x => x.rank).FirstOrDefault(); if (o == null) return;
            Officers.Replace(o); l.debt = Math.Max(0, l.debt - (20 + 8 * o.rank) * 4); Morale.Change(-3, null);
            Director.Notify("CONGEDO PER PROTESTA", $"{o.first} {o.last} sbarca al porto: «Senza paga non si lavora.» Al suo posto arriva un guardiamarina.");
            Saga.Chronicle($"{o.first} {o.last} lascia la nave per le paghe arretrate.");
        }

        // ---- rifornimenti
        public static int FoodPrice() { try { var s = PLServer.GetCurrentSector(); return Math.Max(4, Market.BuyPrice("grano", s.ID) / 3); } catch { return 12; } }
        public static int MedPrice() { try { var s = PLServer.GetCurrentSector(); return Math.Max(12, Market.BuyPrice("medicinali", s.ID) / 3); } catch { return 60; } }
        public static string Restock()
        {
            if (!Shipyard.InPort()) return "Ci si rifornisce solo in porto o alle nostre basi.";
            if (!Market.CanTrade()) return "I portuali sono in sciopero: oggi non si carica niente.";
            var l = L; int nf = FoodCap - l.food, nm = MedCap - l.med; if (nf <= 0 && nm <= 0) return "La dispensa è già piena.";
            // prima le merci in stiva
            var mk = Saga.State.market; string used = "";
            if (mk != null)
            {
                int g = mk.cargo.GetValueOrDefault("grano"); int take = Math.Min(g, (nf + 3) / 4); if (take > 0) { Market.Take("grano", take); l.food = Math.Min(FoodCap, l.food + take * 4); nf = FoodCap - l.food; used += $"{take} grano dalla stiva "; }
                int m = mk.cargo.GetValueOrDefault("medicinali"); int tm = Math.Min(m, (nm + 1) / 2); if (tm > 0) { Market.Take("medicinali", tm); l.med = Math.Min(MedCap, l.med + tm * 2); nm = MedCap - l.med; used += $"{tm} medicinali dalla stiva "; }
            }
            bool free = Bases.At(PLServer.GetCurrentSector()?.ID ?? -1)?.state == 1;
            int cost = free ? 0 : Math.Max(0, nf) * FoodPrice() + Math.Max(0, nm) * MedPrice();
            int cash = 0; try { cash = PLServer.Instance.CurrentCrewCredits; } catch { }
            if (cost > cash) { // compra quello che si può, prima i viveri
                int af = Math.Min(Math.Max(0, nf), cash / Math.Max(1, FoodPrice())); cost = af * FoodPrice(); l.food += af; nf -= af;
                int am = Math.Min(Math.Max(0, nm), (cash - cost) / Math.Max(1, MedPrice())); cost += am * MedPrice(); l.med += am;
            }
            else { l.food = FoodCap; l.med = MedCap; }
            try { PLServer.Instance.CurrentCrewCredits -= cost; } catch { }
            Saga.MarkDirty(); GalaxyView.Dirty();
            return $"Dispensa rifornita{(used.Length > 0 ? " (" + used.Trim() + ")" : "")}: viveri {l.food}/{FoodCap}, medicinali {l.med}/{MedCap}" + (free ? ", gratis alla nostra base." : $", spesa {cost} crediti.");
        }
        public static void Gift(int food, int med) { var l = L; l.food = Math.Min(FoodCap, l.food + food); l.med = Math.Min(MedCap, l.med + med); Saga.MarkDirty(); GalaxyView.Dirty(); }
        public static void Refill() { var l = L; l.food = FoodCap; l.med = Math.Max(l.med, MedCap / 2); Saga.MarkDirty(); GalaxyView.Dirty(); }

        public static string Line()
        {
            var l = L; int crew = Crew(); int auto = l.food / Math.Max(1, crew);
            string f = auto <= 0 ? $"[FF8A7A]viveri finiti[-]" : auto < 3 ? $"[F2A65A]viveri {l.food}/{FoodCap} ({auto} salti)[-]" : $"viveri {l.food}/{FoodCap} ({auto} salti)";
            return $"Dispensa: {f} · medicinali {l.med}/{MedCap} · paghe {Wages()} cr/salto" + (l.debt > 0 ? $" · [FF8A7A]arretrati {l.debt}[-]" : "");
        }
        public static string Describe() { var l = L; return $"viveri {l.food}/{FoodCap}, medicinali {l.med}/{MedCap}, equipaggio {Crew()}, paghe {Wages()}/salto, debito {l.debt}, fame da {l.hungry} salti, pagato in tutto {l.paid}"; }
        public static string Set(int food, int med) { var l = L; l.food = food; l.med = med; Saga.MarkDirty(); GalaxyView.Dirty(); return Describe(); }
    }
}

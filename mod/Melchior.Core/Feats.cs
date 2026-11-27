// Imprese della galassia viva: un solo punto da cui i nuovi sistemi alimentano statistiche di servizio e achievement.
using System;
using System.Linq;

namespace Melchior
{
    public static class Feats
    {
        static void Count(string stat, string[] ach, double add = 1)
        {
            try { Service.Add(stat, add); int v = (int)Service.Get(stat); foreach (var a in ach) { var d = Achievements.Find(a); if (d == null) continue; if (d.Max > 0) Achievements.SetProgress(a, v); else Achievements.Unlock(a); } } catch { }
        }
        public static void Trade(int profit) { try { Service.Add("commercio.scambi", 1); if (profit > 0) { Service.Add("commercio.guadagno", profit); int g = (int)Service.Get("commercio.guadagno"); Achievements.SetProgress("MEL_TRADER", g); Achievements.SetProgress("MEL_TYCOON", g); } } catch { } }
        public static void Capture() => Count("abbordaggi.catture", new[] { "MEL_BOARD", "MEL_BOARD_10" });
        public static void Repel() => Count("abbordaggi.respinti", new[] { "MEL_REPEL" });
        public static void Research(int done, int total) { try { Service.Add("ricerca.tecnologie", 1); Achievements.Unlock("MEL_RESEARCH"); if (done >= total) Achievements.Unlock("MEL_RESEARCH_ALL"); } catch { } }
        public static void BaseFounded(int count) { try { Service.Add("basi.fondate", 1); Achievements.Unlock("MEL_BASE"); Achievements.SetProgress("MEL_BASES_6", count); } catch { } }
        public static void BaseLevel(int level) { if (level >= 3) try { Achievements.Unlock("MEL_BASE_3"); } catch { } }
        public static void Rescue() => Count("traffico.salvataggi", new[] { "MEL_RESCUE_10" });
        public static void Escort() => Count("traffico.scorte", new[] { "MEL_ESCORT_5" });
        public static void Friends(int n) { try { Achievements.SetProgress("MEL_FRIENDS", n); } catch { } }
        public static void Shortage() => Count("porti.carenze", new[] { "MEL_SHORTAGE" });
        public static void Blockade() => Count("porti.blocchi", new[] { "MEL_BLOCKADE" });
        public static void VeteranDown(int level) { try { Service.Add("capitani.veterani", 1); if (level >= 7) Achievements.Unlock("MEL_VETERAN"); } catch { } }
        public static void Talent(int rank) { if (rank >= 3) try { Achievements.Unlock("MEL_TALENT"); } catch { } }
        public static void Recruit(bool volunteer) { try { Service.Add("reclute.assunte", 1); Achievements.Unlock("MEL_RECRUIT"); if (volunteer) Achievements.Unlock("MEL_VOLUNTEER"); } catch { } }
        public static void Rival(int relation) { if (relation >= 60) try { Achievements.Unlock("MEL_RIVAL_FRIEND"); } catch { } }
        public static void RivalDown() { try { Achievements.Unlock("MEL_RIVAL_DOWN"); } catch { } }
        public static void Surrender() => Count("rese.accettate", new[] { "MEL_SURRENDER_3" });
        public static void OldFoe() { try { Achievements.Unlock("MEL_OLD_FOE"); } catch { } }
        public static void Legend() { try { Achievements.Unlock("MEL_FRONTIER_LEGEND"); } catch { } }
        public static void Hunt(bool vendetta) { try { Service.Add("capitani.taglie", 1); Achievements.Unlock(vendetta ? "MEL_VENDETTA" : "MEL_BOUNTY_CAPTAIN"); } catch { } }
        public static void Loan(bool repaid) { try { if (repaid) Achievements.Unlock("MEL_LOAN"); } catch { } }
        public static void Order() => Count("porti.ordini", new[] { "MEL_ORDERS_10" });
        public static void Wish() => Count("equipaggio.desideri", new[] { "MEL_WISHES_10" });
        public static void Loyal(int crew, int loyal) { if (crew >= 3 && loyal >= crew) try { Achievements.Unlock("MEL_LOYAL_CREW"); } catch { } }
        public static void LeaderTask(int opinion) { try { Achievements.Unlock("MEL_MEDAL"); if (opinion >= 80) Achievements.Unlock("MEL_LEADER_FRIEND"); Service.Add("capi.incarichi", 1); } catch { } }
        public static void SkyTrade() => Count("commercio.volo", new[] { "MEL_SKY_TRADE" });
        public static void DebtFree() { try { Achievements.Unlock("MEL_DEBT_FREE"); } catch { } }
        public static void Derelict() => Count("relitti.esplorati", new[] { "MEL_DERELICT", "MEL_DERELICTS_10" });
        public static void Survivor() => Count("relitti.sopravvissuti", new[] { "MEL_SURVIVOR" });
        public static void CrewDeed(string area) { Count("equipaggio.interventi", new[] { "MEL_CREW_TEAM" }); if (area == "antincendio") Count("equipaggio.incendi", new[] { "MEL_CREW_FIRE" }); if (area == "difesa") Count("equipaggio.intrusi", new[] { "MEL_CREW_REPEL" }); }
        public static void CrewRevive() => Count("equipaggio.rianimazioni", new[] { "MEL_CREW_REVIVE" });
        public static void CrewMaster() { try { Achievements.Unlock("MEL_CREW_MASTER"); } catch { } }
        public static void Craft(string what)
        {
            try
            {
                Achievements.Unlock("MEL_CRAFT"); Achievements.SetProgress("MEL_CRAFT_50", (int)Service.Get("fabbricazione.lavorazioni"));
                if (what == "carburante") Achievements.SetProgress("MEL_CRAFT_FUEL", (int)Service.Get("fabbricazione.carburante"));
                if (what == "componente") Achievements.Unlock("MEL_CRAFT_COMPONENT");
            }
            catch { }
        }
        public static void Wear(string what)
        {
            try
            {
                switch (what)
                {
                    case "manutenzione": Achievements.Unlock("MEL_WEAR"); Achievements.SetProgress("MEL_WEAR_50", (int)Service.Get("manutenzione.interventi")); break;
                    case "revisione": Achievements.Unlock("MEL_WEAR_OVERHAUL"); break;
                    case "zoppicando": Achievements.Unlock("MEL_WEAR_OVERHAUL"); Achievements.Unlock("MEL_WEAR_LIMP"); break;
                }
            }
            catch { }
        }
        public static void CrewOrder(int given, int doctrines) { try { Achievements.SetProgress("MEL_CREW_ORDERS", given); Achievements.SetProgress("MEL_CREW_DOCTRINES", doctrines); } catch { } }
        public static void Pax(string what, PaxGroup g)
        {
            try
            {
                switch (what)
                {
                    case "imbarco": Achievements.Unlock("MEL_PAX"); break;
                    case "consegna": Achievements.SetProgress("MEL_PAX_100", (int)Service.Get("passeggeri.consegnati")); if (g.mood >= 85) Achievements.Unlock("MEL_PAX_HAPPY"); break;
                    case "fuggiasco": Achievements.Unlock("MEL_PAX_FUGITIVE"); break;
                    case "nascita": Achievements.Unlock("MEL_PAX_BIRTH"); break;
                }
                if (what == "consegna" && g.kind == "profughi") { Service.Add("passeggeri.profughi", g.count); Achievements.SetProgress("MEL_PAX_REFUGEES", (int)Service.Get("passeggeri.profughi")); }
            }
            catch { }
        }
        public static void Mystery(string what)
        {
            try
            {
                switch (what)
                {
                    case "decifrato": Achievements.Unlock("MEL_SIGNAL"); Achievements.SetProgress("MEL_SIGNALS_10", (int)Service.Get("indagini.decifrati")); break;
                    case "localizzato": Achievements.Unlock("MEL_TRIANGULATE"); break;
                    case "strato": Achievements.Unlock("MEL_DIG"); break;
                    case "reperto": Achievements.SetProgress("MEL_RELICS_10", (int)Service.Get("indagini.reperti")); break;
                    case "civilta": Achievements.Unlock("MEL_CIV"); break;
                    case "origine": Achievements.Unlock("MEL_CIV_HOME"); break;
                    case "futuro": Achievements.Unlock("MEL_FUTURE"); break;
                    case "donato": Achievements.SetProgress("MEL_MUSEUM", (int)Service.Get("indagini.donati")); break;
                    case "nero": Achievements.Unlock("MEL_FENCE"); break;
                }
            }
            catch { }
        }
        public static void Crowd(int n) { try { Achievements.SetProgress("MEL_CROWD", n); } catch { } }

        // controllo periodico della nave: case produttrici diverse, qualità eccezionale
        static float next;
        public static void Tick()
        {
            if (UnityEngine.Time.time < next) return; next = UnityEngine.Time.time + 20f;
            try
            {
                var me = Stations.MyShip; if (me?.MyStats == null) return;
                var comps = me.MyStats.m_AllComponents; if (comps == null) return;
                var makers = new System.Collections.Generic.HashSet<int>(); float best = 0;
                for (int i = 0; i < comps.Count; i++) { var t = Rarity.Get(comps[i].Pointer); if (t == null || t.Tier == 0) continue; if (t.Maker >= 0) makers.Add(t.Maker); best = Math.Max(best, t.Quality); }
                Achievements.SetProgress("MEL_MAKERS", makers.Count);
                if (best >= 100f) Achievements.Unlock("MEL_QUALITY");
                // equipaggiamento personale del giocatore locale
                var pawn = PLNetworkManager.Instance?.MyLocalPawn; var inv = pawn?.GetPlayer()?.MyInventory; int gear = 0;
                if (inv != null) foreach (var l in inv.GetAllItems(true)) if (l != null) foreach (var it in l) { var t = PawnLoot.Of(it); if (t != null) gear = Math.Max(gear, t.Tier); }
                if (gear >= 4) Achievements.Unlock("MEL_GEAR_RARE"); if (gear >= 7) Achievements.Unlock("MEL_GEAR_LEGEND");
            }
            catch { }
        }
    }
}

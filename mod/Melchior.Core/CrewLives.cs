// Vite dell'equipaggio: ogni ufficiale ha desideri, lealtà, amicizie e rivalità.
// - Desideri: vedere un porto, abbattere un pirata con una taglia, vedere la cassa piena, assistere a un evento raro,
//   tornare a casa o a una nostra base, una licenza in un porto in festa. Esauditi, la lealtà e il morale salgono;
//   ignorati a lungo, la lealtà cala.
// - Lealtà: un ufficiale fedele lavora meglio, uno scontento peggio, e se scende troppo al primo porto se ne va.
// - Amicizie e rivalità: nascono dal carattere e dalle esperienze comuni; gli amici si sostengono, i rivali litigano.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public static class CrewLives
    {
        static readonly Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static int lastJump = -1;
        static IEnumerable<Officer> Crew => Saga.State.officers.Where(o => o.alive);

        public static string WishShort(Officer o) => o.wish switch
        {
            "porto" => $"il porto del settore {o.wishArg}", "taglia" => "una taglia riscossa", "crediti" => $"{o.wishArg} crediti in cassa",
            "meraviglia" => "vedere un evento raro", "casa" => "tornare a casa", "festa" => "una festa in porto", _ => o.wish,
        };
        static string WishLong(Officer o) => o.wish switch
        {
            "porto" => $"vorrebbe fare scalo al porto del settore {Places.Of(o.wishArg)}: ci sono persone che non vede da anni",
            "taglia" => "vorrebbe dare la caccia a un pirata con una taglia sulla testa: ha un conto in sospeso con quella gente",
            "crediti" => $"si preoccupa delle finanze: vorrebbe vedere almeno {o.wishArg} crediti in cassa",
            "meraviglia" => "sogna di vedere con i propri occhi un evento raro della galassia",
            "casa" => Home.Has ? "ha nostalgia: vorrebbe tornare al nostro porto di casa" : "ha nostalgia: vorrebbe passare da una delle nostre basi",
            _ => "vorrebbe una licenza in un porto in festa",
        };

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                int j = Obscured.Jumps(); if (j == lastJump) return; bool first = lastJump < 0; lastJump = j; if (first) return;
                int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { }
                foreach (var o in Crew.ToList())
                {
                    if (o.wish.Length == 0) { if (rng.NextDouble() < 0.08) NewWish(o, sec); }
                    else if (Fulfilled(o, sec)) Grant(o);
                    else if (j - o.wishSince > 10) { o.loyalty = Math.Max(0, o.loyalty - 1); if (j - o.wishSince > 25) { o.wish = ""; Stations.LogEvent(Stations.MyShip.ShipID, $"{o.first} {o.last} rinuncia al suo desiderio, con delusione"); o.loyalty = Math.Max(0, o.loyalty - 5); } }
                    // il morale dell'equipaggio pesa sulla lealtà
                    if (Saga.State.morale < 30 && rng.NextDouble() < 0.3) o.loyalty = Math.Max(0, o.loyalty - 1);
                    if (Saga.State.morale > 75 && rng.NextDouble() < 0.2) o.loyalty = Math.Min(100, o.loyalty + 1);
                    // chi è scontento lascia la nave al primo porto
                    if (o.loyalty < 15 && Shipyard.InPort() && rng.NextDouble() < 0.4)
                    {
                        Officers.Replace(o); Morale.Change(-2, null);
                        Director.Notify("UN UFFICIALE SE NE VA", $"{o.first} {o.last} raccoglie le sue cose e sbarca: «Non è più la nave per me.» Al suo posto arriva un guardiamarina.");
                        continue;
                    }
                }
                hunted = false;
                Bonds();
                Feats.Loyal(Crew.Count(o => o.cls >= 1), Crew.Count(o => o.cls >= 1 && o.loyalty >= 80));
                Saga.MarkDirty(); GalaxyView.Dirty();
            }
            catch (Exception e) { Plugin.L.LogWarning("Vite dell'equipaggio: " + e.Message); }
        }

        static void NewWish(Officer o, int sec)
        {
            var kinds = new List<string> { "porto", "porto", "taglia", "crediti", "festa", "casa" };
            if (Wonders.Kind.Length > 0) kinds.Add("meraviglia");
            if (!Home.Has && !Saga.State.bases.Any(b => b.state == 1)) kinds.Remove("casa");
            o.wish = kinds[rng.Next(kinds.Count)]; o.wishSince = Obscured.Jumps(); o.wishArg = -1;
            if (o.wish == "porto") { var cur = PLServer.GetSectorWithID(sec); var ports = Market.Within(cur, 6).Where(id => Market.IsPort(id) && id != sec).ToList(); if (ports.Count == 0) { o.wish = ""; return; } o.wishArg = ports[rng.Next(ports.Count)]; }
            if (o.wish == "crediti") { int cash = 0; try { cash = PLServer.Instance.CurrentCrewCredits; } catch { } o.wishArg = (int)Math.Round((cash + 3000 + rng.Next(5000)) / 500.0) * 500; }
            Stations.LogEvent(Stations.MyShip.ShipID, $"{o.first} {o.last} {WishLong(o)}");
            if (rng.NextDouble() < 0.4) Director.Notify("UN DESIDERIO A BORDO", $"{Officers.Full(o)} {WishLong(o)}.");
            Saga.MarkDirty();
        }
        static bool Fulfilled(Officer o, int sec)
        {
            switch (o.wish)
            {
                case "porto": return sec == o.wishArg;
                case "taglia": return hunted;
                case "crediti": try { return PLServer.Instance.CurrentCrewCredits >= o.wishArg; } catch { return false; }
                case "meraviglia": return Wonders.Kind.Length > 0 && Wonders.Sector == sec || Wonders.Kind is "allineamento" or "spaziomorto";
                case "casa": return (Home.Has && Home.H.sector == sec) || Bases.At(sec)?.state == 1;
                case "festa": return Ports.Festive(sec);
            }
            return false;
        }
        static bool hunted; public static void OnHunt() { hunted = true; }
        static void Grant(Officer o)
        {
            string what = WishShort(o); o.wish = ""; o.wishes++; o.loyalty = Math.Min(100, o.loyalty + 12); o.xp += 3;
            Morale.Change(2, null); Feats.Wish();
            Director.Notify("UN DESIDERIO ESAUDITO", $"{Officers.Full(o)} ringrazia il capitano: {what}, finalmente. Da oggi lavora con un altro spirito.");
            Saga.Chronicle($"Esaudito il desiderio di {o.first} {o.last}: {what}.");
            // le esperienze comuni avvicinano: chi è in servizio con lui può diventare amico
            var mate = Crew.Where(x => x != o && x.cls >= 1 && x.friend.Length == 0 && x.rival != o.last).OrderBy(_ => rng.Next()).FirstOrDefault();
            if (mate != null && o.friend.Length == 0 && rng.NextDouble() < 0.35) { o.friend = mate.last; mate.friend = o.last; Stations.LogEvent(Stations.MyShip.ShipID, $"Fra {o.first} {o.last} e {mate.first} {mate.last} è nata un'amicizia"); }
        }

        // ---- amicizie e rivalità
        static void Bonds()
        {
            var crew = Crew.Where(o => o.cls >= 1).ToList(); if (crew.Count < 2) return;
            // rivalità: caratteri che si scontrano
            if (rng.NextDouble() < 0.04)
            {
                var a = crew[rng.Next(crew.Count)]; var b = crew.Where(x => x != a && x.rival.Length == 0 && x.friend != a.last).OrderBy(_ => rng.Next()).FirstOrDefault();
                if (b != null && a.rival.Length == 0 && (a.trait != b.trait && (a.trait is "impulsivo" or "ambizioso" || b.trait is "impulsivo" or "ambizioso")))
                { a.rival = b.last; b.rival = a.last; Director.Notify("ATTRITI A BORDO", $"{a.first} {a.last} e {b.first} {b.last} non si sopportano più: {Officers.Traits[a.trait]} contro {Officers.Traits[b.trait]}."); }
            }
            // effetti: gli amici si sostengono, i rivali litigano
            foreach (var o in crew)
            {
                if (o.friend.Length > 0 && crew.Any(x => x.last == o.friend) && rng.NextDouble() < 0.05) { o.loyalty = Math.Min(100, o.loyalty + 2); }
                if (o.rival.Length > 0 && crew.Any(x => x.last == o.rival) && rng.NextDouble() < 0.04)
                {
                    var r = crew.First(x => x.last == o.rival); Morale.Change(-1, null); o.loyalty = Math.Max(0, o.loyalty - 2);
                    Stations.LogEvent(Stations.MyShip.ShipID, $"Lite in mensa fra {o.first} {o.last} e {r.first} {r.last}");
                    if (rng.NextDouble() < 0.25) { o.rival = ""; r.rival = ""; Stations.LogEvent(Stations.MyShip.ShipID, $"{o.first} {o.last} e {r.first} {r.last} si stringono la mano: pace fatta"); }
                }
            }
        }

        public static string Describe() => string.Join("\n      ", Crew.Select(o => $"{Officers.StationOf(o.cls),-10} {o.first} {o.last}: lealtà {o.loyalty}, desiderio {(o.wish.Length > 0 ? WishShort(o) + " dal salto " + o.wishSince : "-")}, esauditi {o.wishes}, amico {(o.friend.Length > 0 ? o.friend : "-")}, rivale {(o.rival.Length > 0 ? o.rival : "-")}").Prepend("equipaggio:"));
        public static string Force() { int sec = PLServer.GetCurrentSector()?.ID ?? -1; foreach (var o in Crew.Where(o => o.wish.Length == 0)) NewWish(o, sec); return Describe(); }
    }
}

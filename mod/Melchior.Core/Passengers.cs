// Passeggeri. Nei porti (e a volte da altre navi) si imbarcano gruppi di persone con una destinazione e una scadenza:
// profughi, pellegrini, scienziati dell'Accademia, diplomatici, ospiti illustri, coloni, tecnici e medici in viaggio,
// e qualche fuggiasco che paga bene e non fa domande. Occupano le cabine (capienza dagli «Alloggi migliori» del
// cantiere), mangiano i nostri viveri e hanno un umore: le battaglie, gli incendi, la fame e i ritardi lo abbassano,
// gli alloggi buoni e un equipaggio di buon umore lo alzano. All'arrivo pagano secondo l'umore e lasciano un ricordo
// diverso per ognuno (onore, fiducia dell'Accademia, reputazione presso una fazione, fama, coloni per le nostre basi).
// A bordo succedono cose: un malato, una lite, un clandestino, una nascita, un ospite che chiede una deviazione,
// cacciatori di taglie che reclamano il fuggiasco, una spia scoperta. I tecnici aiutano con le riparazioni, i medici
// con i feriti. Si vedono camminare nei corridoi della nave (PassengersWalk).
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Melchior
{
    public class PaxGroup { public int id, count, dest = -1, deadline, pay, mood = 70, boarded, fac = -1; public string kind = "", name = "", leader = ""; public bool illegal; }
    public class PaxState { public List<PaxGroup> groups = new(); public int nextId = 1, delivered, carried, lost, births, happy, groupsDone; }

    public static class Passengers
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        public static PaxState P => Saga.State.pax ??= new PaxState();
        public static int Aboard => P.groups.Sum(g => g.count);
        public static int Capacity => 6 + 3 * Shipyard.Level("alloggi") + (Research.Has("idroponica") ? 1 : 0);
        public static int FoodLoad() => (Aboard + 1) / 2;
        public static bool Has(string kind) => P.groups.Any(g => g.kind == kind);

        sealed class Kind { public string id = "", label = "", plural = ""; public int min, max, perHead; public double w; public bool illegal; }
        static readonly Kind[] Kinds =
        {
            new() { id = "profughi", label = "profughi", plural = "una famiglia di profughi", min = 3, max = 8, perHead = 70, w = 3 },
            new() { id = "pellegrini", label = "pellegrini", plural = "un gruppo di pellegrini", min = 2, max = 6, perHead = 110, w = 2 },
            new() { id = "scienziati", label = "scienziati dell'Accademia", plural = "una missione dell'Accademia", min = 1, max = 3, perHead = 320, w = 1.6 },
            new() { id = "diplomatici", label = "diplomatici", plural = "una delegazione diplomatica", min = 1, max = 2, perHead = 520, w = 1.2 },
            new() { id = "ospite", label = "ospite illustre", plural = "un ospite illustre", min = 1, max = 1, perHead = 950, w = 0.9 },
            new() { id = "coloni", label = "coloni", plural = "un gruppo di coloni", min = 4, max = 9, perHead = 60, w = 1.6 },
            new() { id = "tecnici", label = "tecnici", plural = "una squadra di tecnici", min = 1, max = 2, perHead = 200, w = 1.2 },
            new() { id = "medici", label = "medici", plural = "un'équipe medica", min = 1, max = 2, perHead = 220, w = 1.0 },
            new() { id = "fuggiasco", label = "fuggiasco", plural = "un viaggiatore che paga in contanti", min = 1, max = 1, perHead = 1500, w = 0.8, illegal = true },
        };
        static Kind K(string id) => Kinds.FirstOrDefault(k => k.id == id) ?? Kinds[0];
        public static string Label(PaxGroup g) => g.count == 1 && g.kind != "profughi" && g.kind != "coloni" ? $"{g.leader} ({K(g.kind).label})" : $"{g.count} {K(g.kind).label} con {g.leader}";
        static readonly string[] Firsts = { "Ilaria", "Tomas", "Mira", "Otto", "Selin", "Dario", "Yara", "Bruno", "Eda", "Kiran", "Livia", "Nestor", "Rhea", "Sven", "Talia", "Ugo" };
        static readonly string[] Lasts = { "Varga", "Okonkwo", "Ferraris", "Lund", "Abbasi", "Corvo", "Mendes", "Halvorsen", "Rossi", "Tanaka", "Brigante", "Del Mar", "Quint", "Esposito" };
        static string Person() => $"{Firsts[rng.Next(Firsts.Length)]} {Lasts[rng.Next(Lasts.Length)]}";
        static string Ship() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }
        static PLSectorInfo Cur() { try { return PLServer.GetCurrentSector(); } catch { return null; } }

        // ---------------------------------------------------------------- offerte
        public static readonly List<PaxGroup> Offers = new(); public static string TransferKind = "";
        static List<int> PortsNear(int min, int max)
        {
            var cur = Cur(); if (cur == null) return new List<int>(); var near = Market.Within(cur, max); var close = Market.Within(cur, min - 1); var l = new List<int>();
            foreach (var id in near.Where(x => !close.Contains(x))) { try { var s = PLServer.GetSectorWithID(id); if (s != null && s.VisualIndication != ESectorVisualIndication.NONE && s.MissionSpecificID == -1 && !global::Melchior.Ports.Closed(id)) l.Add(id); } catch { } }
            return l;
        }
        static PaxGroup Make(string kind = null)
        {
            Kind k; if (kind != null) k = K(kind); else { double tot = Kinds.Sum(x => x.w * Weight(x)), r = rng.NextDouble() * tot; k = Kinds[^1]; foreach (var x in Kinds) { r -= x.w * Weight(x); if (r <= 0) { k = x; break; } } }
            var ports = PortsNear(2, 5); if (k.id == "coloni" && Saga.State.bases.Any(b => b.state == 1)) ports = Saga.State.bases.Where(b => b.state == 1).Select(b => b.sector).ToList();
            if (ports.Count == 0) return null;
            int dest = ports[rng.Next(ports.Count)]; int jumps = Math.Max(1, Nemesis.JumpsTo(dest)); if (jumps >= 99) jumps = 4;
            int n = rng.Next(k.min, k.max + 1); int fac = -1; try { fac = PLServer.GetSectorWithID(dest).MySPI.Faction; } catch { }
            int pay = (int)(k.perHead * n * (0.8 + 0.15 * jumps) * (0.85 + rng.NextDouble() * 0.3));
            return new PaxGroup { kind = k.id, count = n, dest = dest, deadline = Obscured.Jumps() + jumps + 2 + rng.Next(3), pay = pay, leader = Person(), illegal = k.illegal, fac = fac, name = k.plural };
        }
        // le circostanze cambiano chi cerca un passaggio: crisi, epidemie, feste, la grande minaccia
        static double Weight(Kind k)
        {
            double w = 1; int sec = Cur()?.ID ?? -1; var port = global::Melchior.Ports.Of(sec);
            if (k.id == "profughi" && (Saga.State.threat?.stage >= 2 || Crises.Cur != null)) w *= 2.5;
            if (k.id == "medici" && port?.ev == "epidemia") w *= 3;
            if (k.id == "pellegrini" && (port?.ev == "festa" || Calling.Is("tregua"))) w *= 2.5;
            if (k.id == "fuggiasco") w *= Saga.State.infamia > Saga.State.onore ? 2 : 0.7;
            if (k.id == "coloni" && Saga.State.bases.Any(b => b.state == 1)) w *= 2;
            if (k.id == "scienziati" && Calling.Is("fiera")) w *= 2.5;
            return w;
        }
        public static bool PrepareOffers()
        {
            Offers.Clear(); int free = Capacity - Aboard; if (free <= 0) return false;
            for (int i = 0; i < 6 && Offers.Count < 2; i++) { var g = Make(); if (g != null && g.count <= free && !Offers.Any(o => o.kind == g.kind)) Offers.Add(g); }
            return Offers.Count > 0;
        }
        public static string Describe(PaxGroup g) => $"{Cap(g.name)} ({g.count} {(g.count == 1 ? "persona" : "persone")}, guida {g.leader}) per il settore {Places.Of(g.dest)}, entro il salto {g.deadline}: {g.pay} crediti" + (g.illegal ? ", in contanti e senza domande" : "");
        public static string Board(int i)
        {
            if (i < 0 || i >= Offers.Count) return "Nessuno da imbarcare.";
            var g = Offers[i]; if (g.count > Capacity - Aboard) return "Non abbiamo abbastanza cabine libere.";
            Add(g); Offers.Remove(g);
            return $"A bordo: {Label(g)}. Destinazione settore {Places.Of(g.dest)}, entro il salto {g.deadline}." + (g.illegal ? " Meglio non farlo vedere alle pattuglie." : "");
        }
        static void Add(PaxGroup g)
        {
            g.id = P.nextId++; g.boarded = Obscured.Jumps(); P.groups.Add(g); P.carried += g.count; Saga.MarkDirty(); GalaxyView.Dirty();
            Service.Add("passeggeri.imbarcati", g.count); Feats.Pax("imbarco", g);
        }
        public static PaxGroup Transfer(string kind) { var g = Make(kind); if (g == null || g.count > Capacity - Aboard) return null; g.pay = (int)(g.pay * 0.6); Add(g); return g; }

        // ---------------------------------------------------------------- viaggio
        static int lastJump = -1, lastSector = -1; static float arriveAt = -1, nextCheck; static bool battle, fire, breach;
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                var me = Stations.MyShip; float now = Time.time;
                if (now >= nextCheck && P.groups.Count > 0)
                {
                    nextCheck = now + 2f;
                    try { var t = me.TargetShip; if (me.m_AlertLevel >= 2 || (t != null && !t.HasBeenDestroyed && t.HostileShips.Contains(me.ShipID))) battle = true; } catch { }
                    if (CrewMind.GravityNow >= 2) fire = true;
                    try { var st = me.MyStats; if (st.HullMax > 0 && st.HullCurrent / st.HullMax < 0.4f) breach = true; } catch { }
                    Helpers();
                }
                var cur = Cur(); if (cur == null) return; bool warp = false; try { warp = me.InWarp; } catch { }
                if (warp) return;
                if (cur.ID != lastSector) { lastSector = cur.ID; arriveAt = now + 6f; }
                if (arriveAt > 0 && now > arriveAt) { arriveAt = -1; Arrived(cur); }
            }
            catch (Exception e) { Plugin.L.LogWarning("Passeggeri: " + e.Message); }
        }
        static void Arrived(PLSectorInfo cur)
        {
            int j = Obscured.Jumps();
            if (j != lastJump)
            {
                lastJump = j;
                if (P.groups.Count > 0) Journey(j);
            }
            // sbarco di chi è arrivato
            foreach (var g in P.groups.Where(x => x.dest == cur.ID).ToList()) Deliver(g, false);
            // un evento di bordo, ogni tanto
            if (P.groups.Count > 0 && rng.NextDouble() < 0.22) Event();
        }
        // a ogni salto l'umore cambia secondo quello che i passeggeri hanno vissuto
        static void Journey(int j)
        {
            int alloggi = Shipyard.Level("alloggi"); bool hungry = (Saga.State.logistics?.food ?? 1) <= 0; int morale = Saga.State.morale;
            foreach (var g in P.groups.ToList())
            {
                int d = -2 + alloggi + (morale >= 70 ? 1 : morale < 35 ? -2 : 0);
                if (battle) d -= g.kind is "diplomatici" or "ospite" or "scienziati" ? 9 : 6;
                if (fire) d -= 3; if (breach) d -= 5; if (hungry) d -= 6;
                if (g.kind == "ospite" && alloggi == 0) d -= 3;                 // chi è abituato bene si lamenta
                if (g.kind is "profughi" or "pellegrini" && !battle) d += 2;     // per loro un viaggio tranquillo è già tanto
                if (j > g.deadline) d -= 8;
                g.mood = Math.Clamp(g.mood + d, 0, 100);
                if (j == g.deadline + 1) Stations.LogEvent(Stations.MyShip.ShipID, $"{Cap(Label(g))}: siamo in ritardo, e lo fanno notare.");
                if (j > g.deadline + 4 || g.mood <= 5) Abandon(g, j > g.deadline + 4 ? "il ritardo è diventato insopportabile" : "non si fidano più di noi");
            }
            battle = fire = breach = false; Saga.MarkDirty(); GalaxyView.Dirty();
        }
        static void Abandon(PaxGroup g, string why)
        {
            // sbarcano al primo porto o si fanno prendere da un'altra nave: niente paga, e la voce gira
            P.groups.Remove(g); P.lost += g.count; Saga.State.onore = Math.Max(0, Saga.State.onore - 1); Competitors.Renown(-5);
            Director.Notify("PASSEGGERI", $"{Cap(Label(g))} lasciano la nave alla prima occasione: {why}. Niente paga, e nei porti se ne parlerà.");
            News.Post("dicerie", $"Si dice che sulla «{Ship()}» i passeggeri non siano trattati granché bene.");
            Service.Add("passeggeri.persi", g.count); Saga.MarkDirty(); GalaxyView.Dirty();
        }
        public static void Deliver(PaxGroup g, bool early)
        {
            if (!P.groups.Contains(g)) return;
            int j = Obscured.Jumps(); bool late = j > g.deadline;
            double f = 0.55 + 0.6 * g.mood / 100.0; if (late) f *= 0.75; if (early) f *= 0.4;
            int pay = (int)(g.pay * f); int tip = !early && !late && g.mood >= 85 ? (int)(g.pay * 0.2) : 0;
            try { PLServer.Instance.CurrentCrewCredits += pay + tip; } catch { }
            P.groups.Remove(g); if (!early) { P.delivered += g.count; P.groupsDone++; } if (g.mood >= 85) P.happy++;
            var bits = new List<string> { $"{pay + tip} crediti" + (tip > 0 ? $" (mancia di {tip})" : "") };
            if (!early)
            {
                switch (g.kind)
                {
                    case "profughi": Saga.State.onore += 2; Morale.Change(3, "Abbiamo portato in salvo dei profughi"); bits.Add("onore +2"); break;
                    case "pellegrini": Saga.State.onore += 1; Morale.Change(2, null); bits.Add("una benedizione per la nave"); break;
                    case "scienziati": Research.Add(3 + g.count, "passeggeri dell'Accademia"); AcademyTrust(1); bits.Add("dati di ricerca e la gratitudine dell'Accademia"); break;
                    case "diplomatici": if (g.fac >= 0 && g.fac != 4) { Factions.Change(g.fac, 4, null); bits.Add($"reputazione {Factions.Abbr(g.fac)} +4"); } break;
                    case "ospite": Competitors.Renown(18); bits.Add("fama nei porti"); break;
                    case "coloni": { var b = Saga.State.bases.FirstOrDefault(x => x.sector == g.dest && x.state == 1); if (b != null) { Bases.Colonists(b, g.count); bits.Add($"{g.count} coloni per {b.name}"); } else Saga.State.onore += 1; break; }
                    case "fuggiasco": Saga.State.infamia += 1; bits.Add("infamia +1"); Feats.Pax("fuggiasco", g); break;
                }
                var port = global::Melchior.Ports.Of(g.dest); if (port != null) { port.trust = Math.Min(10, port.trust + 1); }
                Service.Add("passeggeri.consegnati", g.count); Feats.Pax("consegna", g);
                if (g.mood >= 85) Service.Add("passeggeri.felici", 1);
            }
            Director.Notify("PASSEGGERI", early ? $"{Cap(Label(g))} sbarcano prima del previsto: pagano solo una parte, {pay} crediti." : $"{Cap(Label(g))} sbarcano a destinazione" + (late ? " (in ritardo)" : "") + $", umore {Mood(g.mood)}: " + string.Join(", ", bits) + ".");
            if (!early && g.mood >= 85 && rng.NextDouble() < 0.5) News.Post("dicerie", $"{g.leader} racconta in giro del viaggio sulla «{Ship()}»: «Non ho mai viaggiato così bene».");
            Saga.MarkDirty(); GalaxyView.Dirty();
        }
        public static string Mood(int m) => m >= 85 ? "entusiasti" : m >= 65 ? "soddisfatti" : m >= 45 ? "tranquilli" : m >= 25 ? "scontenti" : "esasperati";
        static void AcademyTrust(int d) { var t = Saga.State.trust; const string k = "Accademia delle Scienze"; t[k] = Math.Clamp((t.TryGetValue(k, out var v) ? v : 0) + d, -10, 10); Saga.MarkDirty(); }

        // tecnici e medici a bordo danno una mano, senza che nessuno glielo chieda
        static float nextHelp;
        static void Helpers()
        {
            if (Time.time < nextHelp) return; nextHelp = Time.time + 30f; var me = Stations.MyShip;
            if (Has("tecnici"))
            {
                var worst = Stations.SysIds.OrderByDescending(x => Stations.Get(me.ShipID, "dmx_" + x)).First(); float d = Stations.Get(me.ShipID, "dmx_" + worst);
                if (d > 15f) { Stations.Set(me.ShipID, "dmx_" + worst, Math.Max(0.01f, d - 10f)); Stations.LogEvent(me.ShipID, $"I tecnici in viaggio con noi danno una mano: {Stations.SysNames[Array.IndexOf(Stations.SysIds, worst)].ToLowerInvariant()} un po' meno malridotti."); Service.Add("passeggeri.aiuti", 1); }
            }
            if (Has("medici") && rng.NextDouble() < 0.3)
            {
                var o = Saga.State.officers.FirstOrDefault(x => x.alive && x.injuredUntil > Obscured.Jumps()); if (o != null) { o.injuredUntil--; Saga.MarkDirty(); Stations.LogEvent(me.ShipID, $"I medici a bordo visitano {o.first} {o.last}: guarirà prima."); Service.Add("passeggeri.aiuti", 1); }
            }
        }

        // ---------------------------------------------------------------- eventi di bordo
        public static PaxGroup Focus;
        static void Event(bool force = false)
        {
            var g = P.groups[rng.Next(P.groups.Count)]; Focus = g; string id;
            var pool = new List<(string id, double w)> { ("pax_malato", 2), ("pax_lite", 1.5), ("pax_clandestino", 0.7) };
            if (g.kind is "profughi" or "coloni" or "pellegrini" && g.count >= 3) pool.Add(("pax_nascita", 0.6));
            if (g.kind is "ospite" or "diplomatici") pool.Add(("pax_deviazione", 1.5));
            if (g.kind == "fuggiasco") pool.Add(("pax_ricercato", 2.5));
            if (g.kind is "diplomatici" or "scienziati") pool.Add(("pax_spia", 0.8));
            double tot = pool.Sum(x => x.w), r = rng.NextDouble() * tot; id = pool[^1].id; foreach (var x in pool) { r -= x.w; if (r <= 0) { id = x.id; break; } }
            if (!Encounters.Start(id, force)) Focus = null;
        }
        public static string Treat(string how)
        {
            var g = Focus; Focus = null; if (g == null || !P.groups.Contains(g)) return "Il passeggero è già sbarcato.";
            var l = Saga.State.logistics;
            switch (how)
            {
                case "medicine": if (l == null || l.med <= 0) { g.mood -= 10; return "Non abbiamo medicinali: il malato peggiora, e gli altri se ne accorgono."; } l.med--; g.mood = Math.Min(100, g.mood + 10); Saga.MarkDirty(); return "Un medicinale dalla dispensa e il passeggero si riprende. Gli altri ringraziano.";
                case "scienza": g.mood = Math.Min(100, g.mood + 14); return "La Scienza improvvisa una cura con quello che ha: funziona, e l'infermeria guadagna un fan.";
                case "ignora": g.mood -= 18; if (rng.NextDouble() < 0.3 && g.count > 1) { g.count--; P.lost++; Morale.Change(-3, null); Saga.State.onore = Math.Max(0, Saga.State.onore - 1); return "Nessuno se ne occupa. Al salto successivo, un passeggero in meno. A bordo nessuno ne parla."; } return "Il malato si riprende da solo, a fatica. Nessuno dimenticherà come è stato trattato.";
            }
            return "";
        }
        public static string Quarrel(string how)
        {
            var g = Focus; Focus = null; if (g == null || !P.groups.Contains(g)) return "La lite si è spenta da sola.";
            switch (how)
            {
                case "media": g.mood = Math.Min(100, g.mood + 6); Morale.Change(1, null); return $"Il capitano ascolta tutti, {g.leader} compreso. Si stringono mani, e si offre da bere.";
                case "separa": g.mood -= 4; return "Cabine diverse per chi litiga: problema risolto, umore un po' giù.";
                default: g.mood -= 12; Saga.State.infamia += 1; return "Un giorno in cella per far capire chi comanda. Funziona, ma il racconto arriverà ai porti.";
            }
        }
        public static string Stowaway(string how)
        {
            Focus = null; string who = Person();
            switch (how)
            {
                case "arruola": { var o = Saga.State.officers.Where(x => x.alive && x.cls >= 1).OrderBy(x => x.rank).FirstOrDefault(); if (o != null && o.rank == 0 && rng.NextDouble() < 0.5) { Officers.Hire(o.cls, who.Split(' ')[0], who.Split(' ')[1], "impulsivo", 0); return $"{who}, clandestino, chiede una possibilità: la ottiene, e prende servizio {Officers.At(o.cls)}."; } Morale.Change(2, null); return $"{who} lavora per il passaggio: pulisce, cucina, ripara. L'equipaggio lo adotta."; }
                case "porto": Saga.State.onore += 1; return $"{who} verrà lasciato al prossimo porto, con un pasto caldo e un consiglio.";
                default: Saga.State.infamia += 2; Morale.Change(-4, null); return $"{who} viene lasciato su una capsula di salvataggio. Qualcuno a bordo non lo approva.";
            }
        }
        public static string Birth(string how)
        {
            var g = Focus; Focus = null; if (g == null) return "";
            g.count++; P.births++; Feats.Pax("nascita", g); Service.Add("passeggeri.nascite", 1); g.mood = Math.Min(100, g.mood + 20);
            Saga.Chronicle($"A bordo della «{Ship()}» è nata una bambina, figlia di {g.leader}.");
            if (how == "nome") { Saga.State.onore += 2; Morale.Change(6, "Una nascita a bordo"); return $"È una bambina, e i genitori la chiamano come la nave. L'equipaggio festeggia fino al turno successivo."; }
            Morale.Change(4, "Una nascita a bordo"); return "È una bambina. Il medico di bordo non è mai stato così emozionato.";
        }
        public static string Detour(bool accept)
        {
            var g = Focus; Focus = null; if (g == null || !P.groups.Contains(g)) return "";
            if (!accept) { g.mood -= 10; return $"{g.leader} non è abituato ai «no». Lo si capisce dal silenzio a cena."; }
            var ports = PortsNear(1, 3).Where(x => x != g.dest).ToList(); if (ports.Count == 0) return "Non c'è nessun porto adatto nei dintorni.";
            g.dest = ports[rng.Next(ports.Count)]; g.pay = (int)(g.pay * 1.35); g.deadline = Math.Max(g.deadline, Obscured.Jumps() + Math.Max(2, Nemesis.JumpsTo(g.dest) + 1)); g.mood = Math.Min(100, g.mood + 8);
            Course.Plot(g.dest); Saga.MarkDirty(); GalaxyView.Dirty();
            return $"Nuova destinazione: settore {Places.Of(g.dest)}. Il compenso sale a {g.pay} crediti. Rotta tracciata.";
        }
        public static string Hunted(string how)
        {
            var g = Focus; Focus = null; if (g == null || !P.groups.Contains(g)) return "Il fuggiasco non è più a bordo.";
            var me = Stations.MyShip; float pw = Director.Power(me, out _, out _);
            switch (how)
            {
                case "consegna": P.groups.Remove(g); int reward = 900 + rng.Next(600); try { PLServer.Instance.CurrentCrewCredits += reward; } catch { } Saga.State.onore += 1; Saga.MarkDirty(); GalaxyView.Dirty(); return $"Consegniamo {g.leader} ai cacciatori: {reward} crediti di taglia. Il contante che ci aveva dato resta a noi. Non tutti a bordo dormono tranquilli.";
                case "paga": { int have = 0; try { have = PLServer.Instance.CurrentCrewCredits; } catch { } if (have < 800) goto default; try { PLServer.Instance.CurrentCrewCredits = have - 800; } catch { } return "800 crediti e i cacciatori decidono di non averci mai visti."; }
                default: Director.Spawn(me, Director.PickShip(pw, -1), Director.Modifiers(pw), "Cacciatore di taglie"); Saga.State.onore += 1; return $"«Nessuno a bordo con quel nome.» I cacciatori non ci credono e aprono il fuoco. {g.leader} osserva dal corridoio, pallido.";
            }
        }
        public static string Spy(string how)
        {
            var g = Focus; Focus = null; if (g == null || !P.groups.Contains(g)) return "";
            switch (how)
            {
                case "arresta": P.groups.Remove(g); Saga.State.onore += 1; if (g.fac >= 0 && g.fac != 4) Factions.Change(g.fac, 2, null); Saga.MarkDirty(); GalaxyView.Dirty(); return $"{g.leader} copiava i nostri registri di navigazione. In cella fino al porto, e le autorità ringraziano.";
                case "doppio": Research.Add(6, "informazioni di una spia"); Saga.State.infamia += 1; return $"{g.leader} continuerà a mandare rapporti, ma quelli che scriviamo noi. In cambio ci racconta qualcosa di utile.";
                default: g.mood += 5; return "Facciamo finta di niente. Le copie che si porta via sono di registri falsi, preparati in fretta dalla Scienza.";
            }
        }

        // ---------------------------------------------------------------- pannello del capitano
        public class Row { public int id, count, dest, deadline, mood, pay; public string label = "", kind = ""; public bool illegal, here; }
        public class Panel { public List<Row> rows = new(); public int aboard, capacity, delivered, jumps; public bool port; public string stats = ""; }
        public static Panel Snapshot()
        {
            var p = new Panel { aboard = Aboard, capacity = Capacity, delivered = P.delivered, jumps = Obscured.Jumps() };
            try { p.port = Market.CanTrade(); } catch { }
            int here = Cur()?.ID ?? -1;
            foreach (var g in P.groups) p.rows.Add(new Row { id = g.id, count = g.count, dest = g.dest, deadline = g.deadline, mood = g.mood, pay = g.pay, label = Label(g), kind = g.kind, illegal = g.illegal, here = g.dest == here });
            p.stats = $"trasportati {P.carried} · consegnati {P.delivered} · persi {P.lost} · nati a bordo {P.births} · viaggi felici {P.happy}";
            return p;
        }
        public static void Request(string op, string arg)
        {
            if (Host) { var t = Do(op, arg); if (t.Length > 0) Stations.Announce("PASSEGGERI", t); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|passeggeri|{op}|{arg}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var p = msg.Split('|'); if (p.Length >= 4 && Host) Request(p[2], p[3]); }
        static string Do(string op, string arg)
        {
            int.TryParse(arg, out int id); var g = P.groups.FirstOrDefault(x => x.id == id); if (g == null) return "";
            switch (op)
            {
                case "rotta": Course.Plot(g.dest); return $"Rotta verso il settore {Places.Of(g.dest)}, destinazione di {Label(g)}.";
                case "sbarca": if (!Market.CanTrade()) return "Si sbarca solo in porto."; Deliver(g, g.dest != (Cur()?.ID ?? -1)); return "";
            }
            return "";
        }

        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
        public static string Status() => $"passeggeri {Aboard}/{Capacity}: " + (P.groups.Count == 0 ? "nessuno" : string.Join("; ", P.groups.Select(g => $"{Label(g)} → {g.dest} entro {g.deadline}, umore {g.mood}, {g.pay} cr"))) + $" | {Snapshot().stats}";
        public static string Force(string what)
        {
            switch (what)
            {
                case "offerta": return Encounters.StartPort("pax_offerta") ? Encounters.Describe() : "nessuna offerta possibile (" + (Capacity - Aboard) + " cabine libere)";
                case "evento": if (P.groups.Count == 0) return "nessun passeggero"; Event(true); return Encounters.Describe();
                case "qui": { var cur = Cur(); foreach (var g in P.groups) g.dest = cur?.ID ?? g.dest; lastSector = -1; return "destinazioni spostate qui"; }
                case "salto": Journey(Obscured.Jumps() + 1); return Status();
                default: { var g = Make(what.Length > 0 && what != "imbarca" ? what : null); if (g == null) return "nessuna destinazione"; if (g.count > Capacity - Aboard) return $"cabine insufficienti ({Capacity - Aboard} libere)"; Add(g); return Status(); }
            }
        }
    }
}

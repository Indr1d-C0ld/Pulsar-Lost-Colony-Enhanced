// Traffico locale: lo spazio dove ci fermiamo non è vuoto. A ogni sosta il settore si popola di navi con un mestiere
// (mercanti, minatori, corrieri, recuperatori, pellegrini, ricercatori, pattuglie delle fazioni, cacciatori di taglie,
// profughi) scelte da ciò che c'è intorno: porti vicini, asteroidi, relitti, spazio di una fazione, fronte di guerra,
// covi pirati, grande minaccia, stagioni, nostre basi. Le navi arrivano e ripartono nel tempo, parlano sul canale
// locale e vivono le loro storie anche senza di noi:
// - predoni che assaltano un mercante (lo si può difendere, o lasciarlo al suo destino);
// - pattuglie che accorrono contro i predoni;
// - scaramucce fra fazioni in guerra lungo il fronte, in cui si può scegliere da che parte stare;
// - sciacalli che arrivano a spolpare i relitti dopo una battaglia;
// - cacciatori di taglie che cercano personaggi ricorrenti (o noi, se siamo ricercati).
// Chi aiutiamo e chi colpiamo lascia il segno: crediti, reputazione, onore, infamia, notizie.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Melchior
{
    // Capitani del traffico che si ricordano di noi: chi abbiamo salvato torna con un regalo, chi abbiamo colpito no.
    public class Acquaintance { public string ship = "", job = "", captain = ""; public int favor, met, lastJump = -1; public bool alive = true; }
    // Scorta: una nave del traffico ci segue fino a un settore, e paga all'arrivo.
    public class EscortState { public string ship = "", job = "", captain = ""; public int dest = -1, reward, until = -1, from = -1; }

    public static class Traffic
    {
        public static bool Enabled = true;
        public static float Density = 1f, MinFps = 20f; // densità del traffico e soglia di fotogrammi sotto cui il settore si sfoltisce
        static readonly System.Random rng = new();
        public static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }

        public sealed class Npc { public int id; public string job = "", name = ""; public int faction = -1; public float born, leaveAt; public bool eventShip, escort; public Acquaintance friend; public Competitor rival; }
        public sealed class Incident { public string kind = ""; public int victim = -1, sideA = -1, sideB = -1; public List<int> raiders = new(); public float start, boardedAt = -1f; public bool done, boarded; public int fac = -1, facB = -1; }
        static readonly List<Npc> npcs = new();
        static readonly List<Incident> incidents = new();
        static readonly HashSet<int> hitByUs = new();
        static int sector = -1; static float nextArrival, nextChatter, nextIncident, lastCombatEnd = -1f; static bool hadCombat;
        public static int Spawned, Departed, Raids, Rescued, Lost, Skirmishes;

        // ---- nomi
        static readonly string[] TraderNames = { "Fortuna di Kepler", "Gazza Ladra", "Mercurio Lento", "Botte Piena", "Rondine di Vega", "Vecchia Zia", "Sale e Pepe", "Bilancia d'Oro", "Carovaniera", "Stiva Allegra", "Contrabbasso", "Pellicano" };
        static readonly string[] MinerNames = { "Talpa Grigia", "Piccone Stanco", "Vena Buona", "Scavafossi", "Ferro Vecchio", "Polvere di Stelle", "Martello di Ceres" };
        static readonly string[] CourierNames = { "Freccia Postale", "Messaggera", "Lampo", "Rondinella", "Staffetta Sette" };
        static readonly string[] SalvageNames = { "Avvoltoio", "Sciacallo Magro", "Gru di Rottami", "Spazzino", "Mano Lesta" };
        static readonly string[] PilgrimNames = { "Speranza", "Pellegrina", "Santa Barbara", "Lume Lontano", "Via Crucis" };
        static readonly string[] ScienceNames = { "Curiosa", "Astrolabio", "Cannocchiale", "Galileo II", "Occhio Lungo" };
        static readonly string[] RaiderNames = { "Dente Storto", "Coltello Nero", "Lupo Magro", "Ghigno", "Spina", "Zanna Rossa", "Malasorte" };
        static readonly string[] RefugeeNames = { "Ultima Corsa", "Arca", "Casa Nostra", "Fuggiasca" };
        static string Pick(string[] a) { var used = npcs.Select(n => n.name).ToHashSet(); var free = a.Where(x => !used.Contains(x)).ToList(); return free.Count > 0 ? free[rng.Next(free.Count)] : a[rng.Next(a.Length)] + " " + rng.Next(2, 9); }
        static string FacTag(int f) => f switch { 0 => "C.U.", 1 => "A.O.G.", 2 => "W.D.", 5 => "Polytech", _ => "" };

        // ---- contesto del settore
        static PLSectorInfo Cur() { try { return PLServer.GetCurrentSector(); } catch { return null; } }
        static bool Special(PLSectorInfo s) { var v = s.VisualIndication; return v != ESectorVisualIndication.NONE && v is not (ESectorVisualIndication.NEBULA or ESectorVisualIndication.GREEN_NEBULA or ESectorVisualIndication.ASTEROID_FIELD or ESectorVisualIndication.MOLTEN_ASTEROID or ESectorVisualIndication.GRAVEYARD or ESectorVisualIndication.SPACE_JELLIES or ESectorVisualIndication.PURPLE_STORM_01); }
        static bool Asteroids(PLSectorInfo s) { int seed = 0; try { seed = (int)PLServer.Instance.GalaxySeed; } catch { } return s.VisualIndication is ESectorVisualIndication.ASTEROID_FIELD or ESectorVisualIndication.MOLTEN_ASTEROID || Environments.EnvOf(seed, s.ID)?.kind == "asteroidi" || Exploration.PoiOf(seed, s.ID)?.kind == "giacimento"; }
        static int PortsNear(PLSectorInfo s) { int n = Special(s) ? 2 : 0; try { var nb = s.Neighbors; for (int i = 0; i < nb.Count; i++) if (nb[i] != null && Special(nb[i])) n++; } catch { } return n; }
        static int Faction(PLSectorInfo s) { try { return s.MySPI?.Faction ?? -1; } catch { return -1; } }
        static int EnemyOnFront(PLSectorInfo s, int f)
        {
            if (Array.IndexOf(Diplomacy.F, f) < 0) return -1;
            try { var nb = s.Neighbors; for (int i = 0; i < nb.Count; i++) { int g = Faction(nb[i]); if (g >= 0 && g != f && Array.IndexOf(Diplomacy.F, g) >= 0 && Diplomacy.StatusOf(f, g) == -1) return g; } } catch { }
            return -1;
        }
        static bool QuietSector(PLSectorInfo s)
        {
            if (s == null || s.MissionSpecificID != -1) return true;
            if (Saga.State.lairs.Any(l => l.sector == s.ID)) return true;
            if (Saga.State.threat != null && Saga.State.threat.heart == s.ID) return true;
            return false;
        }

        // Quante navi ci sono in media e quali mestieri, secondo ciò che c'è intorno.
        static (int target, List<(string job, double w)> jobs) Mix(PLSectorInfo s)
        {
            int ports = PortsNear(s), fac = Faction(s); bool rocks = Asteroids(s);
            var j = new List<(string, double)>
            {
                ("mercante", 1.5 + ports * 1.2 * (Calling.Is("franco") ? 1.6 : 1) * (Crises.Cur?.kind == "blocco" ? 0.3 : 1)),
                ("corriere", 0.4 + ports * 0.4),
                ("pellegrini", 0.3 * (Calling.Is("tregua") ? 3 : 1)),
                ("ricerca", 0.3 * (Calling.Is("fiera") ? 3 : 1) + (Wonders.Kind.Length > 0 ? 0.6 : 0)),
                ("minatore", rocks ? 3 : 0.15),
                ("recuperatore", (s.VisualIndication == ESectorVisualIndication.GRAVEYARD || Wonders.Remnant(s.ID) ? 2.5 : 0.2)),
            };
            if (fac >= 0 && Array.IndexOf(Diplomacy.F, fac) >= 0) j.Add(("pattuglia", 1.0 + (EnemyOnFront(s, fac) >= 0 ? 1.5 : 0)));
            if (Calling.Is("caccia") || Wanted.Level >= 2) j.Add(("cacciatore", 0.6 + Wanted.Level * 0.2));
            if (Saga.State.threat != null && Saga.State.threat.stage >= 2) j.Add(("profughi", 0.4 * Saga.State.threat.stage));
            if (Crises.Cur?.kind == "epidemia") j.Add(("medici", 1.2));
            // le nostre basi vicine attirano traffico
            int bases = Saga.State.bases.Count(b => b.state == 1 && Nemesis.Distance(s, PLServer.GetSectorWithID(b.sector)) <= 1);
            if (bases > 0) j.Add(("rifornitore", 1.0 * bases));
            int target = (int)Math.Round((5 + Math.Min(4, ports) * 2 + (rocks ? 2 : 0) + (bases > 0 ? 2 : 0) + (fac >= 0 ? 1 : 0)) * global::Melchior.Ports.Traffic(s.ID));
            if (Rules.Mode == "tranquilla") target++;
            return (Math.Clamp(target, 4, 14), j);
        }

        static string Weighted(List<(string job, double w)> l) { double t = l.Sum(x => x.w), r = rng.NextDouble() * t; foreach (var x in l) { r -= x.w; if (r <= 0) return x.job; } return l[^1].job; }

        // ---- creazione delle navi
        static (EShipType type, int fac, string name) Shape(string job, PLSectorInfo s)
        {
            int f = Faction(s); int pf = Array.IndexOf(Diplomacy.F, f) >= 0 ? f : Diplomacy.F[rng.Next(3)];
            return job switch
            {
                "mercante" => (rng.Next(3) == 0 ? EShipType.E_FLUFFY_DELIVERY : EShipType.E_CIVILIAN_FUEL, -1, Pick(TraderNames)),
                "corriere" => (EShipType.E_FLUFFY_DELIVERY, -1, Pick(CourierNames)),
                "minatore" => (EShipType.E_CIVILIAN_STARTING_SHIP, -1, Pick(MinerNames)),
                "recuperatore" => (EShipType.E_OUTRIDER, -1, Pick(SalvageNames)),
                "pellegrini" or "profughi" => (EShipType.E_CIVILIAN_STARTING_SHIP, -1, Pick(job == "profughi" ? RefugeeNames : PilgrimNames)),
                "ricerca" or "medici" => (EShipType.E_POLYTECH_SHIP, 5, job == "medici" ? "Nave ospedale " + Pick(ScienceNames) : Pick(ScienceNames)),
                "rifornitore" => (EShipType.E_CIVILIAN_FUEL, -1, Pick(TraderNames)),
                "pattuglia" => (pf == 2 ? EShipType.E_WDCRUISER : pf == 1 ? EShipType.E_ROLAND : pf == 5 ? EShipType.E_POLYTECH_SHIP : EShipType.E_DESTROYER, pf, $"Pattuglia {FacTag(pf)}-{rng.Next(10, 99)}"),
                "cacciatore" => (EShipType.E_BOUNTY_HUNTER_01, -1, "Cacciatore " + Pick(RaiderNames)),
                _ => (rng.Next(2) == 0 ? EShipType.E_STARGAZER : EShipType.E_OUTRIDER, -1, Pick(RaiderNames)),
            };
        }

        // Ogni nave è diversa: modificatori secondo il mestiere (e da loro dipendono stile e livello del capitano).
        static int Mods(string job)
        {
            EShipModifierType[] pool = job switch
            {
                "mercante" or "rifornitore" => new[] { EShipModifierType.NEGLECTED, EShipModifierType.SECURE, EShipModifierType.SILENT, EShipModifierType.REPUTABLE },
                "corriere" => new[] { EShipModifierType.SWIFT, EShipModifierType.SILENT, EShipModifierType.REPUTABLE },
                "minatore" or "recuperatore" => new[] { EShipModifierType.NEGLECTED, EShipModifierType.IRRADIATED, EShipModifierType.ARMORED },
                "relitto" => new[] { EShipModifierType.NEGLECTED, EShipModifierType.IRRADIATED, EShipModifierType.VULNERABLE },
                "pellegrini" or "profughi" => new[] { EShipModifierType.NEGLECTED, EShipModifierType.VULNERABLE },
                "ricerca" or "medici" => new[] { EShipModifierType.SUBSPACE_COMMS, EShipModifierType.SECURE },
                "pattuglia" or "incursore" or "alleato" or "sentinella" => new[] { EShipModifierType.ARMORED, EShipModifierType.SECURE, EShipModifierType.MISSILE_DEFENSE_SYSTEM, EShipModifierType.ELITE },
                "predoni" or "cacciatore" => new[] { EShipModifierType.BRUTAL, EShipModifierType.RECKLESS, EShipModifierType.SWIFT, EShipModifierType.SILENT },
                _ => Array.Empty<EShipModifierType>(),
            };
            int m = 0; if (pool.Length == 0) return 0;
            double r = rng.NextDouble();
            if (r < 0.6) m |= 1 << (int)pool[rng.Next(pool.Length)];
            if (r < 0.15) m |= 1 << (int)pool[rng.Next(pool.Length)];
            if ((m & (1 << (int)EShipModifierType.ELITE)) != 0 && rng.NextDouble() < 0.6) m &= ~(1 << (int)EShipModifierType.ELITE); // l'élite è rara
            return m;
        }
        // pulizia durante la curvatura, dove un piccolo scatto non si vede
        static readonly List<PLPersistantShipInfo> ours = new(); public static int Unloads;
        static bool unloaded;
        static void Unload()
        {
            ours.Clear(); if (unloaded) return; unloaded = true;
            Janitor.Run(); Unloads++;
        }
        public static string Registry()
        {
            int all = 0, here = 0, mine = ours.Count; var cur = Cur();
            try { foreach (var p in PLServer.Instance.AllPSIs) { all++; try { if (cur != null && p.MyCurrentSector != null && p.MyCurrentSector.ID == cur.ID) here++; } catch { } } } catch { }
            int ships = 0, live = 0, dead = 0, named = 0, active = 0; var kinds = new Dictionary<string, int>();
            try
            {
                foreach (var kv in PLEncounterManager.Instance.AllShips)
                {
                    ships++; var v = kv.Value; if (v == null) continue; live++;
                    try { if (v.HasBeenDestroyed) dead++; } catch { }
                    try { if (v.gameObject.activeInHierarchy) active++; } catch { }
                    if (Director.Names.ContainsKey(kv.Key)) named++;
                    string k = "?"; try { k = v.ShipTypeID.ToString(); } catch { } kinds[k] = kinds.TryGetValue(k, out var c) ? c + 1 : 1;
                }
            }
            catch { }
            return $"registri persistenti {all} (nel settore {here}, nostri {mine}, pulizie {Unloads}); navi caricate {ships}: oggetti vivi {live}, attivi {active}, distrutte {dead}, nostre {named}; tipi {string.Join(" ", kinds.OrderByDescending(x => x.Value).Take(6).Select(x => x.Key + "×" + x.Value))}";
        }
        static PLShipInfoBase Spawn(EShipType type, int fac, string name, int hostileTo = -1, string job = "")
        {
            var cpei = PLEncounterManager.Instance?.GetCPEI(); var cur = Cur(); if (cpei == null || cur == null) return null;
            var psi = new PLPersistantShipInfo(type, fac, cur, Mods(job), false, false, false, -1, hostileTo);
            // i civili viaggiano senza squadre di bot a bordo: il settore può essere affollato senza appesantire il gioco
            if (job is "mercante" or "corriere" or "minatore" or "recuperatore" or "pellegrini" or "profughi" or "ricerca" or "medici" or "rifornitore" or "relitto") try { psi.m_EnsureNoCrew = true; } catch { }
            psi.ShipName = name; ours.Add(psi);
            var pos = cpei.GetEmptyLocationForEnemy(type, psi, rng.Next(1000));
            var ship = cpei.SpawnEnemyShip(type, psi, pos, Quaternion.identity);
            if (ship != null) { Director.Names[ship.ShipID] = name; try { ship.ShipNameValue = name; } catch { } if (job != "relitto") try { Talents.CaptainOf(ship, job.Length > 0 ? job : null); } catch { } if (job is "mercante" or "corriere" or "minatore" or "recuperatore" or "pellegrini" or "profughi" or "ricerca" or "medici" or "rifornitore") try { ship.SlowFlightAI = true; } catch { } }
            return ship;
        }

        public static Npc Arrive(string job, bool quiet = false)
        {
            var s = Cur(); if (s == null) return null;
            var (type, fac, name) = Shape(job, s);
            // a volte è qualcuno che conosciamo
            Acquaintance known = null;
            if (job is not ("pattuglia" or "cacciatore" or "predoni") && rng.NextDouble() < 0.25)
            {
                int j = Obscured.Jumps();
                var pool = Friends.Where(f => f.alive && f.job == job && f.lastJump != j && !npcs.Any(n => n.name == f.ship)).ToList();
                if (pool.Count > 0) { known = pool[rng.Next(pool.Count)]; name = known.ship; known.lastJump = j; known.met++; Saga.MarkDirty(); }
            }
            int hostileTo = -1;
            if (job == "cacciatore" && Wanted.Level >= 2) try { hostileTo = Stations.MyShip.ShipID; } catch { }
            var ship = Spawn(type, fac, name, hostileTo, job); if (ship == null) return null;
            var n = new Npc { id = ship.ShipID, job = job, name = name, faction = fac, born = Time.time, leaveAt = Time.time + 120f + rng.Next(240), friend = known };
            npcs.Add(n); Spawned++;
            if (known != null) Reunion(n, known);
            else if (!quiet) Say(n, Greeting(n, s));
            if (job == "cacciatore" && hostileTo >= 0) Director.Notify("CACCIATORE DI TAGLIE", $"Il «{name}» esce dalla curvatura: ha la nostra faccia sul suo schermo.");
            return n;
        }

        static void Leave(Npc n, string why = null)
        {
            var s = Ship(n.id); npcs.Remove(n); if (s == null) return;
            try { s.Ship_WarpOutNow(); Departed++; } catch { }
            if (why != null) Say(n, why);
        }
        static PLShipInfoBase Ship(int id) { try { var s = PLEncounterManager.Instance.GetShipFromID(id); return s == null || s.HasBeenDestroyed ? null : s; } catch { return null; } }

        // ---- conoscenze
        static List<Acquaintance> Friends => Saga.State.friends ??= new List<Acquaintance>();
        static readonly string[] Captains = { "Ada Ferri", "Bruno Kessler", "Celeste Moreau", "Dario Vance", "Elsa Brandt", "Fosco Lindqvist", "Greta Okafor", "Ivo Marchetti", "Lina Duarte", "Milo Sato", "Nives Kowal", "Orso Albani", "Pia Navarro", "Remo Castelli", "Sara Ilves", "Tito Barros" };
        static Acquaintance Remember(Npc n, int delta)
        {
            if (n == null || n.job is "predoni" or "incursore" or "cacciatore") return null;
            var f = n.friend ?? Friends.FirstOrDefault(x => x.ship == n.name);
            if (f == null) { f = new Acquaintance { ship = n.name, job = n.job, captain = Captains[rng.Next(Captains.Length)], lastJump = Obscured.Jumps() }; Friends.Add(f); if (Friends.Count > 30) Friends.RemoveAt(0); }
            n.friend = f; f.favor = Math.Clamp(f.favor + delta, -10, 10); f.met = Math.Max(1, f.met); Saga.MarkDirty(); Feats.Friends(Friends.Count(x => x.alive && x.favor >= 2));
            return f;
        }
        static void Reunion(Npc n, Acquaintance f)
        {
            var me = Stations.MyShip; string ship = ""; try { ship = (string)me.ShipName; } catch { }
            if (f.favor >= 2)
            {
                string gift;
                switch (rng.Next(3))
                {
                    case 0: { int cr = 200 + rng.Next(400); try { PLServer.Instance.CurrentCrewCredits += cr; } catch { } gift = $"ci trasferisce {cr} crediti «per il disturbo di allora»"; break; }
                    case 1: foreach (var sy in Stations.SysIds) Stations.Set(me.ShipID, "dmx_" + sy, Math.Max(0.01f, Stations.Get(me.ShipID, "dmx_" + sy) - 10)); gift = "ci manda i suoi tecnici a dare una sistemata ai sistemi"; break;
                    default: News.Rumor(); gift = "ci racconta le ultime voci dei porti (nel notiziario)"; break;
                }
                Morale.Change(1, null);
                Director.Notify("UNA VECCHIA CONOSCENZA", $"«{f.ship}» esce dalla curvatura. {f.captain}: «La «{ship}»! Non ho dimenticato quello che avete fatto per noi.» E {gift}.");
            }
            else if (f.favor <= -2)
            {
                Say(n, $"{f.captain} riconosce la nostra nave: «Voi. Statemi lontani.»");
                News.Post("dicerie", $"{f.captain} del «{f.ship}» racconta in giro cosa ha subito dalla «{ship}». Non è una bella storia.");
            }
            else Say(n, $"{f.captain} saluta: «Ancora voi! Il mondo è piccolo, anche quaggiù.»");
        }
        public static bool IsKnown(string ship) => Saga.State.friends?.Any(f => f.ship == ship) ?? false;
        // Un capitano amico che chiede di imbarcarsi: le stesse persone del traffico possono diventare nostri ufficiali.
        public static Acquaintance Volunteer() => Friends.Where(f => f.alive && f.favor >= 5).OrderByDescending(f => f.favor).FirstOrDefault();
        public static void Retire(Acquaintance f) { f.alive = false; Saga.MarkDirty(); }
        public static string FriendsLine() => Friends.Count(f => f.alive && f.favor >= 2) is int k && k > 0 ? $"{k} capitani amici" : "";

        // ---- scorte
        static EscortState Esc { get => Saga.State.escort; set { Saga.State.escort = value; Saga.MarkDirty(); GalaxyView.Dirty(); } }
        static int escortId = -1; static float escortRaidAt = -1f, escortPayAt = -1f;
        public static bool EscortOffer(Npc n)
        {
            if (Esc != null || n == null || n.job is not ("mercante" or "corriere" or "pellegrini" or "profughi" or "rifornitore")) return false;
            return Encounters.Start("scorta", true, null, "", n.id.ToString());
        }
        public static (string ship, int dest, int reward, int dist) EscortPlan(string id)
        {
            var n = npcs.FirstOrDefault(x => x.id.ToString() == id); var cur = Cur(); if (n == null || cur == null) return ("", -1, 0, 0);
            if (n.friend == null) n.friend = Remember(n, 0);
            var cand = new List<(PLSectorInfo s, int d)>();
            try { var r1 = Market.Within(cur, 1); var r2 = Market.Within(cur, 2); foreach (var sid in Market.Within(cur, 3)) { if (r1.Contains(sid)) continue; var s = PLServer.GetSectorWithID(sid); if (s?.MySPI == null || s.MissionSpecificID != -1) continue; cand.Add((s, r2.Contains(sid) ? 2 : 3)); } } catch { }
            if (cand.Count == 0) return ("", -1, 0, 0);
            var pick = cand[(n.id * 31 + cur.ID) % cand.Count];
            int reward = (int)((500 + 350 * pick.d + rng.Next(200)) * Rules.Reward * Calling.Bonus("commercio"));
            return (n.name, pick.s.ID, reward, pick.d);
        }
        public static string AcceptEscort(string id, int dest, int reward, int dist)
        {
            var n = npcs.FirstOrDefault(x => x.id.ToString() == id); if (n == null || dest < 0) return "La nave è già ripartita.";
            var f = n.friend ?? Remember(n, 0);
            Esc = new EscortState { ship = n.name, job = n.job, captain = f?.captain ?? "", dest = dest, reward = reward, until = Obscured.Jumps() + dist * 2 + 3, from = Cur()?.ID ?? -1 };
            n.eventShip = true; n.escort = true; n.leaveAt = float.MaxValue; escortId = n.id;
            Course.Plot(dest);
            return $"«{n.name}» si mette in scia: destinazione il settore {Places.Of(dest)}, compenso {reward} crediti all'arrivo. Rotta tracciata.";
        }
        static void EscortArrival(PLSectorInfo cur)
        {
            var e = Esc; if (e == null) return;
            if (Obscured.Jumps() > e.until) { Fail(e, $"Il «{e.ship}» ha perso le nostre tracce e ha proseguito da solo. Niente compenso."); return; }
            var (type, fac, _) = Shape(e.job, cur);
            var ship = Spawn(type, fac, e.ship, -1, e.job); if (ship == null) return;
            escortId = ship.ShipID;
            npcs.Add(new Npc { id = ship.ShipID, job = e.job, name = e.ship, faction = fac, born = Time.time, leaveAt = float.MaxValue, eventShip = true, escort = true, friend = Friends.FirstOrDefault(x => x.ship == e.ship) });
            if (cur.ID == e.dest) { escortPayAt = Time.time + 8f; Stations.LogEvent(Stations.MyShip.ShipID, $"«{e.ship}»: eccoci arrivati, finalmente!"); }
            else
            {
                Stations.LogEvent(Stations.MyShip.ShipID, $"«{e.ship}»: siamo dietro di voi. Ancora {Nemesis.Distance(cur, PLServer.GetSectorWithID(e.dest))} salti.");
                double p = 0.3 * (Rules.Mode == "spietata" ? 1.4 : Rules.Mode == "tranquilla" ? 0.6 : 1) + Saga.State.lairs.Count(l => Nemesis.Distance(cur, PLServer.GetSectorWithID(l.sector)) <= 2) * 0.15;
                escortRaidAt = rng.NextDouble() < p ? Time.time + 20f + rng.Next(25) : -1f;
            }
        }
        static void EscortTick(float now)
        {
            var e = Esc; if (e == null) return;
            if (escortId >= 0 && Ship(escortId) == null && npcs.Any(n => n.id == escortId)) { var nn = npcs.First(n => n.id == escortId); npcs.Remove(nn); var f = Friends.FirstOrDefault(x => x.ship == e.ship); if (f != null) f.alive = false; Fail(e, $"Il «{e.ship}» è stato distrutto. {(e.captain.Length > 0 ? e.captain + " non ce l'ha fatta." : "")}"); Morale.Change(-2, null); return; }
            if (escortRaidAt > 0 && now >= escortRaidAt) { escortRaidAt = -1f; StartRaid(escortId); }
            if (escortPayAt > 0 && now >= escortPayAt)
            {
                escortPayAt = -1f; try { PLServer.Instance.CurrentCrewCredits += e.reward; } catch { }
                var f = Friends.FirstOrDefault(x => x.ship == e.ship); if (f != null) { f.favor = Math.Min(10, f.favor + 3); }
                Calling.Add("commercio"); Morale.Change(2, null); Competitors.Renown(8); Saga.State.onore += 1; Feats.Escort();
                Director.Notify("SCORTA COMPIUTA", $"{(e.captain.Length > 0 ? e.captain : "Il capitano")} del «{e.ship}»: «Da soli non ce l'avremmo mai fatta.» {e.reward} crediti, e qualcuno in più che ci deve un favore sulle rotte.");
                News.Post("commercio", $"La «{(string)Stations.MyShip.ShipName}» ha scortato il «{e.ship}» fino al settore {Places.Of(e.dest)}.");
                var n = npcs.FirstOrDefault(x => x.id == escortId); if (n != null) { n.escort = false; n.leaveAt = now + 25f; }
                Esc = null; escortId = -1;
            }
        }
        static void Fail(EscortState e, string text) { Director.Notify("SCORTA FALLITA", text); var f = Friends.FirstOrDefault(x => x.ship == e.ship); if (f != null) f.favor -= 1; Esc = null; escortId = -1; escortRaidAt = -1f; }

        // ---- alleati in battaglia: pattuglie amiche, capitani amici che raccolgono il nostro SOS, sentinelle delle basi
        static readonly HashSet<int> joined = new(); static float nextHelp; static int helpJump = -100; static bool guardsUp, forceHelp;
        public static int Helps;
        static List<PLShipInfoBase> Foes(PLShipInfo me)
        {
            var l = new List<PLShipInfoBase>();
            foreach (var kv in PLEncounterManager.Instance.AllShips)
            {
                var o = kv.Value; if (o == null || o.HasBeenDestroyed || o.ShipID == me.ShipID) continue;
                if (npcs.Any(n => n.id == o.ShipID && n.job is not ("predoni" or "cacciatore") && !o.HostileShips.Contains(me.ShipID))) continue;
                bool h = false; try { h = me.HostileShips.Contains(o.ShipID) || o.HostileShips.Contains(me.ShipID) || (o.TargetShip != null && o.TargetShip.ShipID == me.ShipID); } catch { }
                if (h) l.Add(o);
            }
            return l;
        }
        static void Engage(PLShipInfoBase ally, List<PLShipInfoBase> foes) { foreach (var f in foes) { try { ally.AddHostileShip(f); f.AddHostileShip(ally); } catch { } } }
        static Npc Ally(string job, EShipType type, int fac, string name, List<PLShipInfoBase> foes, float stay = 240f)
        {
            var s = Spawn(type, fac, name, foes[0].ShipID, job); if (s == null) return null;
            Engage(s, foes); joined.Add(s.ShipID); Helps++;
            var n = new Npc { id = s.ShipID, job = job, name = name, faction = fac, born = Time.time, leaveAt = Time.time + stay, eventShip = true }; npcs.Add(n); return n;
        }
        static void Allies(float now)
        {
            var me = Stations.MyShip; var cur = Cur(); if (me == null || cur == null) return;
            bool combat = InCombat();
            if (!combat) { foreach (var n in npcs.Where(x => x.job is "alleato" or "sentinella" && x.leaveAt > now + 30f)) { n.leaveAt = now + 20f; Say(n, n.job == "sentinella" ? "settore libero, rientriamo alla base." : "tutto a posto? Allora noi andiamo. Alla prossima!"); } return; }
            var foes = Foes(me); if (foes.Count == 0) return;
            // pattuglie già presenti nello spazio di una fazione che ci rispetta
            foreach (var n in npcs.Where(x => x.job == "pattuglia" && !joined.Contains(x.id)).ToList())
            {
                var s = Ship(n.id); if (s == null || n.faction < 0 || Factions.Level(n.faction) < 1 || foes.Any(f => { try { return f.FactionID == n.faction; } catch { return false; } })) continue;
                joined.Add(n.id); Engage(s, foes); Helps++; Say(n, "vi copriamo noi, capitano!");
            }
            // i capitani amici già nel settore si schierano con noi
            foreach (var n in npcs.Where(x => ((x.friend != null && x.friend.favor >= 4) || (x.rival != null && x.rival.relation >= 40)) && !joined.Contains(x.id) && x.job is not ("alleato" or "sentinella")).ToList())
            {
                var s = Ship(n.id); if (s == null) continue;
                joined.Add(n.id); Engage(s, foes); Helps++; n.leaveAt = Math.Max(n.leaveAt, now + 240f);
                Director.Notify("UN AMICO AL NOSTRO FIANCO", $"{(n.friend?.captain ?? n.rival?.captain)} del «{n.name}» vira verso i nostri assalitori: «Non vi lasciamo soli, capitano!»");
            }
            // sentinelle delle nostre basi fortificate
            var b = Bases.At(cur.ID);
            if (!guardsUp && b != null && b.state == 1 && (b.defense >= 1 || b.kind == "fortezza"))
            {
                guardsUp = true; int k = Math.Max(1, b.defense + (b.kind == "fortezza" ? 1 : 0));
                for (int i = 0; i < Math.Min(3, k); i++) Ally("sentinella", i == 0 && b.kind == "fortezza" ? EShipType.E_DESTROYER : EShipType.E_WDDRONE1, -1, $"Sentinella {i + 1} di {b.name}", foes, 600f);
                Director.Notify("SENTINELLE IN VOLO", $"Dalla {b.name} decollano le sentinelle: combattono al nostro fianco.");
            }
            if (now < nextHelp) return; nextHelp = now + 45f;
            float hull = 1f; try { hull = me.MyStats.HullCurrent / Math.Max(1f, me.MyStats.HullMax); } catch { }
            if (hull > 0.55f && !forceHelp) return;
            // un capitano amico raccoglie il nostro SOS
            int j = Obscured.Jumps();
            var friend = Friends.Where(f => f.alive && f.favor >= 4 && !npcs.Any(n => n.name == f.ship)).OrderByDescending(f => f.favor).FirstOrDefault();
            if (friend != null && j - helpJump >= 6 && (forceHelp || rng.NextDouble() < 0.45))
            {
                helpJump = j; var (type, fac, _) = Shape(friend.job, cur);
                var n = Ally("alleato", type, fac, friend.ship, foes); if (n != null) { n.friend = friend; friend.favor -= 1; friend.met++; Saga.MarkDirty(); Director.Notify("SOS RACCOLTO", $"Il «{friend.ship}» esce dalla curvatura a tutta velocità. {friend.captain}: «Avevamo un debito con voi, capitano. Lo saldiamo adesso!»"); }
                return;
            }
            // nello spazio di una fazione alleata, una pattuglia accorre
            int sf = Faction(cur);
            if (Array.IndexOf(Diplomacy.F, sf) >= 0 && Factions.Level(sf) >= 2 && j - helpJump >= 3 && rng.NextDouble() < 0.35)
            {
                helpJump = j; var (pt, pf, pn) = Shape("pattuglia", cur);
                if (Ally("alleato", pt, pf, pn, foes) != null) Director.Notify("RINFORZI ALLEATI", $"La {GalaxyWar.Name(sf)} risponde alla nostra richiesta d'aiuto: la «{pn}» entra in combattimento.");
            }
        }
        public static string ForceAllies() { nextHelp = 0; helpJump = -100; guardsUp = false; forceHelp = true; var f = Friends.FirstOrDefault(x => x.alive); if (f != null && f.favor < 4) { f.favor = 4; Saga.MarkDirty(); } Allies(Time.time); forceHelp = false; return Describe(); }

        // ---- equipaggi concorrenti in carne e ossa
        static void RivalArrives(PLSectorInfo cur)
        {
            var list = Competitors.List(); if (list.Count == 0) return;
            var c = list[rng.Next(list.Count)]; if (npcs.Any(n => n.rival == c)) return;
            int st = c.style switch { "onesti" => 3, "cacciatori" => 0, _ => 2 };
            Talents.Register(c.captain, c.ship, 2 + c.score / 120 + Obscured.Jumps() / 30, st);
            bool grudge = c.relation <= -40 && c.style == "spregiudicati";
            int hostileTo = -1; if (grudge) try { hostileTo = Stations.MyShip.ShipID; } catch { }
            var s = Spawn(c.style == "onesti" ? EShipType.E_ROLAND : EShipType.E_OUTRIDER, -1, c.ship, hostileTo, c.style == "spregiudicati" ? "predoni" : "pattuglia"); if (s == null) return;
            var n = new Npc { id = s.ShipID, job = "rivale", name = c.ship, born = Time.time, leaveAt = Time.time + 150f + rng.Next(150), rival = c }; npcs.Add(n);
            c.met = true; Saga.MarkDirty();
            if (grudge) Director.Notify("RIVALI IN AGGUATO", $"La «{c.ship}» di {c.captain} esce dalla curvatura con le armi già calde: «Ci siamo pestati i piedi una volta di troppo.»");
            else Say(n, c.relation >= 30 ? $"{c.captain} vi saluta: «Che piacere, vecchi amici! Bevuta al prossimo porto?»" : c.relation <= -30 ? $"{c.captain}: «Ancora voi. La galassia è piccola, purtroppo.»" : $"{c.captain}: «Liberi equipaggi in transito. Buona caccia a tutti.»");
        }
        static string RivalHail(Npc n)
        {
            var c = n.rival; if (c == null) return null;
            if (c.relation >= 20)
            {
                c.relation = Math.Min(100, c.relation + 2); Saga.MarkDirty(); Feats.Rival(c.relation);
                if (Market.Rumor() is string mr) return $"{c.captain} della «{c.ship}»: «Fra amici ci si aiuta: {mr}.»";
                return $"{c.captain}: «Niente di nuovo, ma vi tengo d'occhio le spalle.»";
            }
            if (c.relation <= -30) return $"{c.captain}: «Non abbiamo niente da dirci. Ancora.»";
            if (rng.NextDouble() < 0.4 && Encounters.Start("gara", true)) return $"{c.captain} ha una proposta: la trovate sul pannello del capitano.";
            c.relation += 3; Saga.MarkDirty();
            return $"{c.captain}: «La classifica dice che siamo rivali, il canale dice che possiamo parlare. Buona rotta.»";
        }
        // la nave di un capitano ricercato: ostile a noi, con una scorta se è un veterano
        public static PLShipInfoBase SpawnHunted(string ship, int level)
        {
            var cur = Cur(); if (cur == null) return null; int me = -1; try { me = Stations.MyShip.ShipID; } catch { }
            var s = Spawn(level >= 6 ? EShipType.E_ROLAND : EShipType.E_OUTRIDER, -1, ship, me, "predoni"); if (s == null) return null;
            npcs.Add(new Npc { id = s.ShipID, job = "predoni", name = ship, born = Time.time, leaveAt = float.MaxValue, eventShip = true });
            if (level >= 6) { var (t, _, n) = Shape("predoni", cur); var e = Spawn(t, -1, n, me, "predoni"); if (e != null) npcs.Add(new Npc { id = e.ShipID, job = "predoni", name = n, born = Time.time, leaveAt = float.MaxValue, eventShip = true }); }
            return s;
        }
        // esattori della Gilda: una nave di cacciatori ostile a noi
        public static bool Collectors()
        {
            var cur = Cur(); if (cur == null || QuietSector(cur)) return false; int me = -1; try { me = Stations.MyShip.ShipID; } catch { }
            var s = Spawn(EShipType.E_BOUNTY_HUNTER_01, -1, "Esattore della Gilda", me, "cacciatore"); if (s == null) return false;
            npcs.Add(new Npc { id = s.ShipID, job = "cacciatore", name = "Esattore della Gilda", born = Time.time, leaveAt = Time.time + 400f, eventShip = true }); return true;
        }
        // un relitto: nave neutrale senza equipaggio, ferma
        public static PLShipInfoBase SpawnNeutral(EShipType t, string name)
        {
            var s = Spawn(t, -1, name, -1, "relitto"); if (s == null) return null;
            try { s.m_FlightAIEnabled = false; } catch { }
            try { s.MyStats.ShieldsCurrent = 0f; } catch { }
            npcs.Add(new Npc { id = s.ShipID, job = "relitto", name = name, born = Time.time, leaveAt = float.MaxValue, eventShip = true }); return s;
        }
        public static bool HasSalvagers() => npcs.Any(n => n.job == "recuperatore" && Ship(n.id) != null);
        public static bool IsRival(PLShipInfoBase s) => s != null && npcs.Any(x => x.id == s.ShipID && x.rival != null);
        public static void RivalDown(PLShipInfoBase s)
        {
            var n = npcs.FirstOrDefault(x => x.id == s.ShipID && x.rival != null); if (n == null) return;
            var c = n.rival; Feats.RivalDown(); c.relation = Math.Max(-100, c.relation - 40); c.score = Math.Max(0, c.score - 30); c.deeds = Math.Max(0, c.deeds - 1); Saga.MarkDirty();
            News.Post("flotta", $"La «{c.ship}» di {c.captain} è stata messa fuori combattimento dalla «{(string)Stations.MyShip.ShipName}». Si salvano con le capsule, e non dimenticheranno.");
        }

        // ---- il canale locale
        static void Say(Npc n, string text) { try { Stations.LogEvent(Stations.MyShip.ShipID, $"«{n.name}»: {text}"); } catch { } }
        static string Sec(PLSectorInfo s) => Places.Of(s.ID);
        static string Greeting(Npc n, PLSectorInfo s) => n.job switch
        {
            "mercante" => Choose($"in ingresso nel settore {Sec(s)}, carico di {Goods()} per il prossimo porto.", "chiediamo strada libera, siamo pesanti come una balena.", $"qualcuno compra {Goods()}? Prezzo da amici."),
            "corriere" => Choose("posta urgente, non fermateci.", "corriere in transito: tre salti e poi finalmente un letto."),
            "minatore" => Choose("buona vena su quel grosso asteroide, ci fermiamo qualche ora.", "attenzione alle schegge, stiamo facendo brillare una roccia."),
            "recuperatore" => Choose("qui c'è ancora roba buona fra i rottami, se nessuno la reclama.", "recupero autorizzato, più o meno. Non guardateci così."),
            "pellegrini" => Choose("in rotta verso il santuario, che le stelle vi siano propizie.", "siamo pellegrini, non abbiamo niente da rubare."),
            "profughi" => Choose("scappiamo da casa, laggiù non c'è più niente. Se avete viveri...", "profughi in transito, vi preghiamo di non sparare."),
            "ricerca" => Choose("misure di radiazione in corso, scusate le interferenze sui sensori.", "l'Accademia ringrazia per la pazienza: ancora un'ora di rilevamenti."),
            "medici" => "nave ospedale in servizio: chi ha feriti a bordo può chiamarci.",
            "rifornitore" => Choose("carico per le basi della zona, guardate un po' chi si rivede.", "rifornimenti per la vostra base, capitano: ci pensiamo noi."),
            "pattuglia" => Choose("pattuglia di settore a tutte le navi: transponder accesi, nessuna sorpresa.", "settore sotto sorveglianza. Buon viaggio a chi ha la coscienza pulita."),
            "cacciatore" => Choose("cerchiamo qualcuno, e non siete voi. Per ora.", "caccia aperta. Se vedete un ricercato, il premio si divide."),
            _ => "…",
        };
        static string Chatter(Npc n) => n.job switch
        {
            "mercante" => Choose($"i prezzi di {Goods()} sono saliti ancora, nei porti della Frontiera.", "qualcuno ha notizie dei predoni? Non vorrei incontrarli col carico pieno.", "ripartiamo fra poco, il motore di sinistra tossisce."),
            "minatore" => Choose("altra tonnellata in stiva. Ancora due e torniamo a casa.", "questa roccia è più dura del capo cantiere."),
            "recuperatore" => Choose("un generatore quasi intatto! Oggi si mangia.", "chi ha lasciato tutta questa roba qui? Non che ci lamentiamo."),
            "pattuglia" => Choose("controllo di routine completato, nessuna anomalia.", "segnalate navi sospette a questa frequenza."),
            "ricerca" => Choose("dati interessanti: la radiazione di fondo oscilla.", "qualcuno ha visto passare una cometa da queste parti?"),
            "corriere" => "consegna in ritardo, il cliente ci ucciderà.",
            _ => Choose("tutto tranquillo, per ora.", "bella giornata per viaggiare, o quello che è una giornata quaggiù."),
        };
        static string Goods() => Choose("deuterio", "grano idroponico", "ricambi", "medicinali", "minerale grezzo", "vino di Rigel", "componenti elettronici", "acqua purificata");
        static string Choose(params string[] a) => a[rng.Next(a.Length)];

        // ---- incidenti
        static bool StartRaid(int victimId = -1)
        {
            var victims = npcs.Where(n => (victimId < 0 ? n.job is "mercante" or "corriere" or "pellegrini" or "profughi" or "minatore" or "rifornitore" : n.id == victimId) && Ship(n.id) != null).ToList(); if (victims.Count == 0) return false;
            var v = victims[rng.Next(victims.Count)]; var cur = Cur(); if (cur == null) return false;
            int n = 1 + (rng.NextDouble() < 0.4 ? 1 : 0);
            var inc = new Incident { kind = "assalto", victim = v.id, start = Time.time };
            var again = rng.NextDouble() < 0.35 ? Talents.KnownPirate() : null;
            if (again != null) Feats.OldFoe();
            if (again != null) Director.Notify("UNA VECCHIA CONOSCENZA", $"Fra i predoni c'è la «{again.ship}» di {again.name}, livello {again.level}. L'abbiamo già incontrato{(again.life.Count > 0 ? "; da allora: " + again.life[^1].Split(": ").Last() : "")}.");
            for (int i = 0; i < n; i++) { var (type, _, name) = Shape("predoni", cur); if (i == 0 && again != null) name = again.ship; var r = Spawn(type, -1, name, v.id, "predoni"); if (r != null) { inc.raiders.Add(r.ShipID); npcs.Add(new Npc { id = r.ShipID, job = "predoni", name = name, born = Time.time, leaveAt = Time.time + 600f, eventShip = true }); } }
            if (inc.raiders.Count == 0) return false;
            incidents.Add(inc); Raids++;
            Director.Notify("SOS SUL CANALE LOCALE", $"«{v.name}»: «Predoni! Ci stanno attaccando, chiunque ci senta ci aiuti!» {inc.raiders.Count} nav{(inc.raiders.Count == 1 ? "e" : "i")} pirata contro un {Job(v.job)} disarmato. Possiamo intervenire, o restare a guardare.");
            // la pattuglia di settore a volte accorre
            int fac = Faction(cur);
            if (Array.IndexOf(Diplomacy.F, fac) >= 0 && rng.NextDouble() < 0.35) { var (pt, pf, pn) = Shape("pattuglia", cur); var p = Spawn(pt, pf, pn, inc.raiders[0], "pattuglia"); if (p != null) { npcs.Add(new Npc { id = p.ShipID, job = "pattuglia", name = pn, faction = pf, born = Time.time, leaveAt = Time.time + 300f }); Stations.LogEvent(Stations.MyShip.ShipID, $"«{pn}»: arriviamo, resistete!"); } }
            return true;
        }
        static string Job(string j) => j switch { "mercante" => "mercantile", "corriere" => "corriere", "pellegrini" => "trasporto di pellegrini", "profughi" => "carico di profughi", "minatore" => "minatore", "rifornitore" => "rifornitore", _ => j };

        static bool StartSkirmish()
        {
            var cur = Cur(); int f = Faction(cur); int g = EnemyOnFront(cur, f); if (g < 0) return false;
            var (ta, fa, na) = Shape("pattuglia", cur); var a = Spawn(ta, f, na, -1, "pattuglia");
            if (a == null) return false;
            int pg = g; var tb = pg == 2 ? EShipType.E_WDCRUISER : pg == 1 ? EShipType.E_ROLAND : pg == 5 ? EShipType.E_POLYTECH_SHIP : EShipType.E_DESTROYER; string nb = $"Incursore {FacTag(pg)}-{rng.Next(10, 99)}";
            var b = Spawn(tb, pg, nb, a.ShipID, "incursore"); if (b == null) return false;
            try { a.AddHostileShip(b); } catch { }
            npcs.Add(new Npc { id = a.ShipID, job = "pattuglia", name = na, faction = f, born = Time.time, leaveAt = Time.time + 600f, eventShip = true });
            npcs.Add(new Npc { id = b.ShipID, job = "incursore", name = nb, faction = pg, born = Time.time, leaveAt = Time.time + 600f, eventShip = true });
            incidents.Add(new Incident { kind = "scaramuccia", sideA = a.ShipID, sideB = b.ShipID, fac = f, facB = pg, start = Time.time }); Skirmishes++;
            Director.Notify("SCARAMUCCIA SUL FRONTE", $"Una nave della {GalaxyWar.Name(pg)} sconfina e attacca la pattuglia della {GalaxyWar.Name(f)}. Possiamo schierarci, o lasciarle sbrigare fra loro.");
            return true;
        }

        static string lastFoe = "";
        public static void NoteFoe(string name) { lastFoe = name ?? ""; }
        static void Jackals()
        {
            var cur = Cur(); if (cur == null) return;
            int n = 1 + rng.Next(2);
            for (int i = 0; i < n; i++) Arrive("recuperatore", i > 0);
            Stations.LogEvent(Stations.MyShip.ShipID, "Gli sciacalli arrivano: navi di recupero puntano sui relitti della battaglia.");
        }

        static void Resolve()
        {
            foreach (var inc in incidents.Where(x => !x.done).ToList())
            {
                if (inc.kind == "assalto")
                {
                    bool victimAlive = Ship(inc.victim) != null; bool raidersAlive = inc.raiders.Any(id => Ship(id) != null);
                    var v = npcs.FirstOrDefault(x => x.id == inc.victim); string vn = v?.name ?? "il mercantile";
                    bool weHelped = inc.raiders.Any(hitByUs.Contains), weHurt = hitByUs.Contains(inc.victim);
                    // abbordaggio: con gli scudi del mercante a terra, i predoni mandano una squadra
                    var vs = Ship(inc.victim);
                    if (vs != null && raidersAlive && !inc.boarded)
                    {
                        foreach (var rid in inc.raiders) { var rs = Ship(rid); if (rs != null && Boarding.Pirates(rs, vs)) { inc.boarded = true; inc.boardedAt = Time.time; Stations.LogEvent(Stations.MyShip.ShipID, $"«{vn}»: ci stanno abbordando! Si combatte nei corridoi!"); break; } }
                    }
                    if (vs != null && inc.boarded && Time.time - inc.boardedAt > 20f && Boarding.Overrun(vs))
                    {
                        inc.done = true; Lost++;
                        News.Post("commercio", $"Il «{vn}» è stato preso all'arrembaggio dai predoni nel settore {Places.Of(Cur()?.ID ?? -1)}" + (weHelped ? ", nonostante il nostro intervento." : "."));
                        var dead = v?.friend ?? Friends.FirstOrDefault(x => x.ship == vn); if (dead != null) dead.alive = false;
                        EnemyAI.Scuttle(vs); continue;
                    }
                    if (!victimAlive)
                    {
                        inc.done = true; Lost++;
                        foreach (var rid in inc.raiders) Talents.NpcXp(Ship(rid), 25, "ha predato un mercante");
                        News.Post("commercio", $"Il «{vn}» è stato distrutto dai predoni nel settore {Places.Of(Cur()?.ID ?? -1)}" + (weHurt ? ", e qualcuno giura di aver visto la nostra nave sparargli." : "."));
                        if (weHurt) { Saga.State.infamia += 2; Wanted.Heat(10); }
                        var dead = v?.friend ?? Friends.FirstOrDefault(x => x.ship == vn); if (dead != null) { dead.alive = false; if (dead.favor >= 2) { Morale.Change(-2, null); Director.Notify("UN AMICO PERDUTO", $"Il «{vn}» di {dead.captain} è stato distrutto. Ci aveva aiutati, una volta."); } }
                        else if (!weHelped) Morale.Change(-1, null);
                        foreach (var id in inc.raiders) { var r = npcs.FirstOrDefault(x => x.id == id); if (r != null) { r.leaveAt = Time.time + 15f; } }
                        Saga.MarkDirty();
                    }
                    else if (!raidersAlive)
                    {
                        inc.done = true;
                        if (weHelped)
                        {
                            Rescued++; Feats.Rescue(); global::Melchior.Ports.PirateDown(Cur()?.ID ?? -1); var fr = Remember(v, 3); Talents.NpcXp(Ship(inc.victim), 10, "è scampato ai predoni"); int cr = (int)((400 + rng.Next(700)) * Rules.Reward); try { PLServer.Instance.CurrentCrewCredits += cr; } catch { }
                            Saga.State.onore += 1; Morale.Change(3, null); Competitors.Renown(10); Calling.Add("guardiani");
                            int fac = Faction(Cur()); if (Array.IndexOf(Diplomacy.F, fac) >= 0) Factions.Change(fac, 2, null);
                            Director.Notify("SALVATAGGIO", $"«{vn}»: «Grazie, capitano! Non so come avremmo fatto.» Ci trasferiscono {cr} crediti, e la voce girerà nei porti.");
                            News.Post("commercio", $"La «{(string)Stations.MyShip.ShipName}» ha difeso il «{vn}» dai predoni nel settore {Places.Of(Cur()?.ID ?? -1)}.");
                            if (v != null) v.leaveAt = Time.time + 20f;
                            Saga.MarkDirty();
                        }
                        else Stations.LogEvent(Stations.MyShip.ShipID, $"«{vn}»: la pattuglia li ha messi in fuga. Grazie per… niente.");
                    }
                    else if (Time.time - inc.start > 240f) { inc.done = true; foreach (var id in inc.raiders) { var r = npcs.FirstOrDefault(x => x.id == id); if (r != null) Leave(r, "prendiamo quello che c'è e ce ne andiamo."); } }
                }
                else if (inc.kind == "scaramuccia")
                {
                    bool a = Ship(inc.sideA) != null, b = Ship(inc.sideB) != null;
                    if (a && b && Time.time - inc.start < 300f) continue;
                    inc.done = true;
                    bool helpedA = hitByUs.Contains(inc.sideB), helpedB = hitByUs.Contains(inc.sideA);
                    if (helpedA && !helpedB) { Factions.Change(inc.fac, 3, null); Factions.Change(inc.facB, -3, null); GalaxyWar.Shift(inc.fac, 2); Director.Notify("FRONTE", $"Ci siamo schierati con la {GalaxyWar.Name(inc.fac)}: lo ricorderanno, e lo ricorderà anche la {GalaxyWar.Name(inc.facB)}."); }
                    else if (helpedB && !helpedA) { Factions.Change(inc.facB, 3, null); Factions.Change(inc.fac, -3, null); GalaxyWar.Shift(inc.facB, 2); Director.Notify("FRONTE", $"Ci siamo schierati con la {GalaxyWar.Name(inc.facB)}: lo ricorderanno, e lo ricorderà anche la {GalaxyWar.Name(inc.fac)}."); }
                    else News.Post("fronte", $"Scaramuccia fra {GalaxyWar.Name(inc.fac)} e {GalaxyWar.Name(inc.facB)} nel settore {Places.Of(Cur()?.ID ?? -1)}: {(a && !b ? "l'incursore è stato abbattuto" : !a && b ? "la pattuglia ha avuto la peggio" : "nessun vincitore")}.");
                    foreach (var n in npcs.Where(x => x.id == inc.sideA || x.id == inc.sideB)) n.leaveAt = Time.time + 20f;
                }
            }
            incidents.RemoveAll(x => x.done && Time.time - x.start > 600f);
        }

        // Colpi della nostra nave sulle navi del traffico: chi aiutiamo e chi colpiamo.
        internal static void NoteHit(PLShipInfoBase victim, PLShipInfoBase atk)
        {
            try { var me = Stations.MyShip; if (me == null || atk == null || victim == null || atk.ShipID != me.ShipID) return; var n = npcs.FirstOrDefault(x => x.id == victim.ShipID); if (n != null && hitByUs.Add(victim.ShipID) && n.job is not ("predoni" or "incursore" or "cacciatore")) Remember(n, -3); } catch { }
        }

        static bool InCombat() { try { return (int)Stations.Get(Stations.MyShip.ShipID, "drx_phase") == 2; } catch { return false; } }

        public static void Tick()
        {
            if (!Enabled || !Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                bool warp = false; try { warp = Stations.MyShip.InWarp; } catch { }
                var cur = Cur(); if (cur == null) return;
                if (warp) { if (npcs.Count > 0) { npcs.Clear(); incidents.Clear(); hitByUs.Clear(); } Unload(); sector = -1; return; }
                float now = Time.time;
                if (cur.ID != sector)
                {
                    sector = cur.ID; unloaded = false; npcs.Clear(); incidents.Clear(); hitByUs.Clear(); warned.Clear(); joined.Clear(); guardsUp = false; hadCombat = false;
                    nextArrival = now + 8f; nextChatter = now + 40f + rng.Next(40); nextIncident = now + 60f + rng.Next(90);
                    EscortArrival(cur);
                    if (!QuietSector(cur) && rng.NextDouble() < 0.12) RivalArrives(cur);
                    if (QuietSector(cur)) { nextArrival = float.MaxValue; return; }
                    // all'arrivo qualcuno c'è già
                    var (target, jobs) = Mix(cur); int first = Math.Max(2, target * 2 / 3);
                    for (int i = 0; i < first; i++) Arrive(Weighted(jobs), i > 0);
                    return;
                }
                if (nextArrival == float.MaxValue) return;
                Feats.Crowd(npcs.Count(x => !x.eventShip));
                npcs.RemoveAll(n => Ship(n.id) == null && !incidents.Any(x => !x.done && (x.victim == n.id || x.raiders.Contains(n.id) || x.sideA == n.id || x.sideB == n.id)));
                // arrivi e partenze
                if (now >= nextArrival)
                {
                    nextArrival = now + 18f + rng.Next(25);
                    var (target, jobs) = Mix(cur); int ambient = npcs.Count(n => !n.eventShip);
                    // densità adattiva: con pochi fotogrammi al secondo il settore smette di riempirsi
                    float fps = 1f / Math.Max(0.001f, Time.smoothDeltaTime);
                    if (fps < MinFps && ambient > 3) { var extra = npcs.Where(n => !n.eventShip).OrderBy(n => n.leaveAt).FirstOrDefault(); if (extra != null) Leave(extra, "ripartiamo, qui c'è troppa ressa."); }
                    target = (int)Math.Round(target * Density);
                    if (ambient < target && fps >= MinFps + 4 && rng.NextDouble() < 0.8)
                    {
                        // a volte arriva una carovana: due o tre mercanti con la loro scorta
                        if (rng.NextDouble() < 0.2 && target - ambient >= 3) { int k = 2 + rng.Next(2); for (int i = 0; i < k; i++) Arrive("mercante", i > 0); Arrive("pattuglia", true); Say(npcs[^1], "carovana in transito, teniamo la formazione."); }
                        else Arrive(Weighted(jobs));
                        GalaxyView.Dirty();
                    }
                    var go = npcs.Where(n => !n.eventShip && now >= n.leaveAt).OrderBy(n => n.leaveAt).FirstOrDefault();
                    if (go != null && !incidents.Any(x => !x.done && x.victim == go.id)) Leave(go, Choose("ripartiamo, buon viaggio a tutti.", "carico fatto, si torna in curvatura.", "qui abbiamo finito. Alla prossima."));
                }
                foreach (var n in npcs.Where(n => n.eventShip && now >= n.leaveAt).ToList()) Leave(n);
                // voci sul canale
                if (now >= nextChatter) { nextChatter = now + 50f + rng.Next(70); var talk = npcs.Where(n => !n.eventShip && Ship(n.id) != null).ToList(); if (talk.Count > 0) { var n = talk[rng.Next(talk.Count)]; Say(n, Chatter(n)); } }
                // incidenti fra navi del traffico
                bool combat = InCombat();
                if (combat) hadCombat = true; else if (hadCombat) { hadCombat = false; lastCombatEnd = now; if (rng.NextDouble() < 0.5) Derelicts.Spawn(lastFoe); if (rng.NextDouble() < 0.5) Jackals(); }
                if (now >= nextIncident && !combat && !incidents.Any(x => !x.done))
                {
                    nextIncident = now + 120f + rng.Next(150);
                    double pirates = (Market.Within(cur, 1).Any(global::Melchior.Ports.Blockade) ? 0.6 : 0) + 0.3 + Saga.State.lairs.Count(l => Nemesis.Distance(cur, PLServer.GetSectorWithID(l.sector)) <= 2) * 0.2 + (Rules.Mode == "spietata" ? 0.15 : 0);
                    if (EnemyOnFront(cur, Faction(cur)) >= 0 && rng.NextDouble() < 0.45) StartSkirmish();
                    else if (rng.NextDouble() < pirates) StartRaid();
                }
                Patrols(now); EscortTick(now); Allies(now);
                Resolve();
            }
            catch (Exception e) { Plugin.L.LogWarning("Traffico: " + e.Message); }
        }

        // Le navi del traffico sono nemiche solo se ce l'hanno con noi.
        // alleati del momento: navi accorse in nostro aiuto, sentinelle delle basi, la nave che scortiamo
        public static string JobOf(int shipId) => npcs.FirstOrDefault(x => x.id == shipId)?.job ?? "";
        public static bool IsAlly(PLShipInfoBase s) => s != null && npcs.Any(x => x.id == s.ShipID && (x.job is "alleato" or "sentinella" || x.escort || joined.Contains(x.id)));
        public static bool Peaceful(PLShipInfoBase s, PLShipInfoBase me)
        {
            if (!npcs.Any(n => n.id == s.ShipID)) return false;
            try { return !(s.HostileShips.Contains(me.ShipID) || (s.TargetShip != null && s.TargetShip.ShipID == me.ShipID)); } catch { return true; }
        }
        // ---- chiamate alle navi del traffico: ognuna risponde secondo il suo mestiere
        public static string Hail(PLShipInfo me, PLShipInfoBase t)
        {
            var n = npcs.FirstOrDefault(x => x.id == t.ShipID); if (n == null) return null;
            if (n.job == "relitto") return Derelicts.Hail(t);
            if (n.rival != null) return RivalHail(n);
            int cr = 0; try { cr = PLServer.Instance.CurrentCrewCredits; } catch { }
            void Pay(int x) { try { PLServer.Instance.CurrentCrewCredits = Math.Max(0, PLServer.Instance.CurrentCrewCredits - x); } catch { } }
            if (n.job is "mercante" or "rifornitore" && rng.NextDouble() < 0.4 && Market.OfferSky(n.name))
                return $"«{n.name}» propone un affare in volo: lo trovate sul pannello del capitano.";
            if (n.job is "mercante" or "rifornitore" or "corriere" or "pellegrini" or "profughi" && Esc == null && rng.NextDouble() < 0.45 && EscortOffer(n))
                return $"«{n.name}» ha una richiesta da farci: la trovate sul pannello del capitano.";
            switch (n.job)
            {
                case "medici":
                    Logistics.Gift(0, 3);
                    foreach (var sy in Stations.SysIds) Stations.Set(me.ShipID, "dmx_" + sy, Math.Max(0.01f, Stations.Get(me.ShipID, "dmx_" + sy) - 20));
                    Morale.Change(2, null);
                    return $"«{n.name}»: «Mandiamo subito una squadra.» I tecnici della nave ospedale rimettono in sesto i sistemi e visitano l'equipaggio.";
                case "recuperatore":
                    if (cr < 500) return $"«{n.name}»: «Abbiamo un pezzo che fa per voi, ma costa 500 crediti. Tornate quando li avete.»";
                    { var got = Loot.Grant(me, 1, 3, "recupero"); if (got == null) return $"«{n.name}»: «Oggi niente di buono, solo rottami.»"; Pay(500); return $"«{n.name}»: «Quasi nuovo, ve lo giuro.» Compriamo {got.Name} per 500 crediti."; }
                case "minatore":
                {
                    int seed = (int)PLServer.Instance.GalaxySeed; var nb = Cur()?.Neighbors; string hint = null;
                    if (nb != null) for (int i = 0; i < nb.Count && hint == null; i++) { var s = nb[i]; var p = s == null ? null : Exploration.PoiOf(seed, s.ID); if (p != null && !Saga.State.explored.Contains(s.ID)) { hint = $"nel settore {Places.Of(s.ID)} ci sia qualcosa da vedere: {p.Value.name}"; Contracts.Plot(s.ID); } }
                    return hint != null ? $"«{n.name}»: «Noi scaviamo e basta, ma si dice che {hint}.» Rotta tracciata." : $"«{n.name}»: «Qui c'è solo roccia, capitano. Roccia e mal di schiena.»";
                }
                case "ricerca":
                    if (rng.NextDouble() < 0.4 && Mysteries.M.signals.Count(x => !x.reached) < 3 && Encounters.Start("accademia_segnale", true)) return $"«{n.name}»: «Proprio voi! Abbiamo bisogno di una nave veloce per un'indagine.» Il capitano ha una proposta sul pannello.";
                    if (rng.NextDouble() < 0.35) return $"«{n.name}»: «Abbiamo un vecchio frammento di mappa che non sappiamo leggere: prendetelo voi.» ({Exploration.Fragment("ricerca")})";
                    return $"«{n.name}»: «Stiamo misurando la radiazione di fondo. Affascinante, vero? No? Peccato.»";
                case "pellegrini": case "profughi":
                    if (rng.NextDouble() < 0.4 && Passengers.Capacity - Passengers.Aboard >= 2) { Passengers.TransferKind = n.job; if (Encounters.Start("pax_trasbordo", true)) return $"«{n.name}»: «Avete posto a bordo? Abbiamo gente che deve arrivare.» Il capitano ha una proposta sul pannello."; Passengers.TransferKind = ""; }
                    if (cr < 300) return $"«{n.name}»: «Non abbiamo bisogno di niente, grazie. Solo di arrivare.»";
                    Pay(300); Saga.State.onore += 1; Morale.Change(2, null); Calling.Add("guardiani"); Remember(n, 1); Saga.MarkDirty();
                    return $"«{n.name}»: «Viveri e medicine? Che le stelle vi benedicano.» Doniamo 300 crediti di provviste.";
                case "rifornitore" when Logistics.Autonomy < 6:
                    { int price = Logistics.FoodPrice() * 12; if (cr < price) return $"«{n.name}»: «Viveri? Ne abbiamo, ma costano {price} crediti per una dozzina di razioni.»"; Pay(price); Logistics.Gift(12, 0); return $"«{n.name}»: «Dodici razioni, fresche di idroponica.» {price} crediti, e la dispensa respira."; }
                case "mercante": case "rifornitore": case "corriere":
                    if (rng.NextDouble() < 0.45 && Market.Rumor() is string mr) return $"«{n.name}»: «Un consiglio da commerciante: {mr}.» Annotato nella scheda Mercato.";
                    if (rng.NextDouble() < 0.5 && Contracts.PostBounty(Director.Power(me, out _, out _))) return $"«{n.name}»: «Predoni su questa rotta: c'è una taglia, se vi interessa.» Nuova offerta sulla bacheca.";
                    News.Rumor(); return $"«{n.name}»: «I prezzi salgono, i predoni pure. Le ultime voci dei porti le trovate nel notiziario.»";
                case "pattuglia":
                    if (Wanted.Level >= 1) { Wanted.Heat(3); return $"«{n.name}»: «Il vostro transponder risulta nella lista dei ricercati. Restate dove siete.» Meglio non restare."; }
                    { var lair = Saga.State.lairs.OrderBy(l => Nemesis.Distance(Cur(), PLServer.GetSectorWithID(l.sector))).FirstOrDefault(); if (lair != null) { Contracts.Plot(lair.sector); return $"«{n.name}»: «C'è un covo pirata nel settore {Places.Of(lair.sector)}. Se volete renderci un favore…» Rotta tracciata."; } }
                    return $"«{n.name}»: «Settore tranquillo. Buon viaggio.»";
                case "cacciatore":
                    return Contracts.PostBounty(Director.Power(me, out _, out _)) ? $"«{n.name}»: «Dividiamo la preda? Vi passo il nome.» Nuova taglia sulla bacheca." : $"«{n.name}»: «Le mie prede sono affari miei.»";
            }
            return null;
        }

        // Le pattuglie fermano i ricercati: prima l'avvertimento, poi il fuoco.
        static readonly Dictionary<int, float> warned = new();
        static void Patrols(float now)
        {
            if (Wanted.Level < 2) return; var me = Stations.MyShip;
            foreach (var n in npcs.Where(x => x.job == "pattuglia" && !x.eventShip))
            {
                var s = Ship(n.id); if (s == null) continue;
                if (!warned.TryGetValue(n.id, out var at)) { warned[n.id] = now; Director.Notify("PATTUGLIA", $"«{n.name}»: «Nave {(string)me.ShipName}, risultate fra i ricercati. Spegnete i motori e preparatevi all'abbordaggio.» Abbiamo pochi secondi per andarcene."); continue; }
                if (now - at > 30f && at > 0) { warned[n.id] = -1f; try { s.AddHostileShip(me); } catch { } Stations.LogEvent(me.ShipID, $"«{n.name}»: apriamo il fuoco."); }
            }
        }

        public static string Short()
        {
            if (!Host) return GalaxyView.Current?.traffic ?? "";
            int n = npcs.Count(x => !x.eventShip); int sos = incidents.Count(x => !x.done);
            var e = Esc; string esc = e != null ? $"scorta del «{e.ship}» verso {e.dest}" : "";
            return string.Join(", ", new[] { n > 0 ? $"traffico {n} navi" : "", sos > 0 ? "SOS in corso" : "", esc, Lives.Line(), Leaders.Line() }.Where(x => x.Length > 0));
        }
        public static string RoleOf(int id) { var n = npcs.FirstOrDefault(x => x.id == id); return n == null ? "" : "traffico: " + n.job; }
        public static string Line() { var c = npcs.Where(n => !n.eventShip).GroupBy(n => n.job).Select(g => $"{g.Count()} {g.Key}").ToList(); return c.Count == 0 ? "" : "Traffico: " + string.Join(", ", c); }
        public static string Describe() => $"traffico {(Enabled ? "attivo" : "spento")} (fps {1f / Math.Max(0.001f, Time.smoothDeltaTime):0}), settore {sector}: {npcs.Count} navi ({string.Join(", ", npcs.Select(n => $"{n.id} {n.job} «{n.name}»"))}); incidenti in corso {incidents.Count(x => !x.done)}; scorta {(Esc == null ? "-" : Esc.ship + " → " + Esc.dest + " entro il salto " + Esc.until)}; conoscenze {string.Join(", ", Friends.Select(f => $"{f.ship} ({f.captain}, favore {f.favor}{(f.alive ? "" : ", perduto")})"))}; totali: aiuti in battaglia {Helps}, arrivi {Spawned}, partenze {Departed}, assalti {Raids}, salvataggi {Rescued}, perdite {Lost}, scaramucce {Skirmishes}";
        public static string Force(string what)
        {
            switch (what)
            {
                case "assalto": return StartRaid() ? Describe() : "nessuna vittima adatta";
                case "scaramuccia": return StartSkirmish() ? Describe() : "non siamo sul fronte";
                case "sciacalli": Jackals(); return Describe();
                case "alleati": return ForceAllies();
                case "densita": return Describe();
                case "rivale": { var cur = Cur(); RivalArrives(cur); return Describe(); }
                case "scorta": { var c = npcs.FirstOrDefault(x => x.job is "mercante" or "corriere"); return c != null && EscortOffer(c) ? Encounters.Describe() : "nessun mercante o scorta già in corso"; }
                case "amico": { var c = npcs.FirstOrDefault(x => x.job is "mercante" or "corriere"); if (c == null) return "nessun mercante"; Remember(c, 3); return Describe(); }
                default: return Arrive(what) != null ? Describe() : "impossibile";
            }
        }
    }
}

// Reputazione dell'equipaggio presso le fazioni (da -100 a +100), con conseguenze concrete:
// - si guadagna con i contratti e le crisi, si perde abbattendo le loro navi o lavorando per i loro nemici;
// - nello spazio di una fazione ostile arrivano pattuglie a intercettarvi; chi vi considera nemici non vi offre lavoro;
// - nello spazio di una fazione amica il direttore concede più respiro e arrivano rifornimenti; i compensi salgono.
// Lo stato vive nella cronaca della galassia. Il riepilogo (GalaxyView) viene inviato a tutto l'equipaggio.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Melchior
{
    public static class Factions
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static Dictionary<string, int> S => Saga.State.standing ??= new Dictionary<string, int>();
        public static int Value(int f) => S.TryGetValue(f.ToString(), out var v) ? v : f == 4 ? -60 : 0;

        // livelli: -3 guerra aperta, -2 ostilità, -1 diffidenza, 0 neutralità, 1 cordialità, 2 alleanza, 3 leggenda
        public static int Level(int f) { int v = Value(f); return v <= -60 ? -3 : v <= -25 ? -2 : v <= -10 ? -1 : v < 10 ? 0 : v < 35 ? 1 : v < 70 ? 2 : 3; }
        public static string LevelName(int l) => l switch { -3 => "Guerra aperta", -2 => "Ostilità", -1 => "Diffidenza", 1 => "Cordialità", 2 => "Alleanza", 3 => "Leggenda", _ => "Neutralità" };
        static int ByName(string issuer) { foreach (var f in GalaxyWar.Factions) if (GalaxyWar.Name(f) == issuer) return f; return -1; }

        public static void Change(int f, int d, string why, bool share = true)
        {
            if (!Host || f < 0 || Array.IndexOf(GalaxyWar.Factions, f) < 0 || d == 0) return;
            if (d > 0 && share) d = (int)Math.Round(d * Calling.Bonus("diplomazia"));
            Leaders.OnRep(f, d);
            if (share && f != 4 && Math.Abs(d) >= 3) foreach (var a in Diplomacy.Allies(f)) Change(a, d / 3, null, false); // gli alleati condividono un terzo
            int before = Level(f); S[f.ToString()] = Math.Clamp(Value(f) + d, Campaigns.Honored(f) ? 35 : -100, 100); int after = Level(f); // con l'onorificenza l'alleanza non si perde
            Saga.MarkDirty(); GalaxyView.Dirty();
            if (after <= -2 && before > -2 && f != 4) Wanted.Heat(15, $"La {GalaxyWar.Name(f)} ha messo una taglia su di noi");
            if (after != before && f != 4)
            {
                string text = $"{GalaxyWar.Name(f)}: {LevelName(after).ToLowerInvariant()}." + (why != null ? " " + why + "." : "") + after switch
                {
                    -3 => " Le loro pattuglie spareranno a vista e i loro negozi ci spennano.", -2 => " Nel loro spazio verremo intercettati e pagheremo di più.", -1 => " Le loro offerte si fanno più rare e meno generose.",
                    1 => " I loro compensi salgono e i loro negozi ci fanno lo sconto.", 2 => " Nel loro spazio troveremo rifugio e rifornimenti.", 3 => " Il nostro nome è leggenda fra i loro equipaggi.", _ => "",
                };
                Director.Notify("REPUTAZIONE", text);
                News.Post("flotta", $"La {GalaxyWar.Name(f)} cambia atteggiamento verso la «{OurShip()}»: {LevelName(after).ToLowerInvariant()}.");
                Saga.Chronicle(text);
            }
        }
        static string OurShip() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }

        // Nemici di una fazione: chi le contende più settori sul fronte.
        static int Enemy(int f)
        {
            var c = GalaxyWar.FrontCounts(f); return c.Count == 0 ? -1 : c.OrderByDescending(x => x.Value).First().Key;
        }

        public static void OnContract(ContractOffer c, bool success)
        {
            int f = c.side >= 0 ? c.side : ByName(c.issuer); if (f < 0) return;
            Change(f, success ? (c.urgent ? 10 : 7) : -5, success ? $"Contratto onorato per la {GalaxyWar.Name(f)}" : "Contratto mancato");
            int e = Enemy(f); if (success && e >= 0 && e != 4) Change(e, -2, null);
        }

        public static void OnFigureDown(Figure fig)
        {
            if (fig.role == "ribelle") Change(fig.faction, 10, $"Avete fermato {fig.Full}");
            else foreach (var f in GalaxyWar.Factions) if (f != 4) Change(f, 2, null);
        }

        // ---- navi abbattute dall'equipaggio
        static readonly Dictionary<int, int> seen = new(); // nave -> fazione (solo navi ostili o prese di mira nel settore)
        static readonly Dictionary<int, float> hullOf = new(); // ultima frazione di scafo vista
        static readonly HashSet<int> counted = new(); static int seenSector = -1;
        static long nextKill; static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static void TrackKills()
        {
            var me = Stations.MyShip; if (me == null) return;
            int sector = -1; try { sector = PLServer.GetCurrentSector().ID; } catch { }
            bool warp = false; try { warp = me.InWarp; } catch { }
            if (sector != seenSector || warp) { seen.Clear(); counted.Clear(); hullOf.Clear(); seenSector = sector; return; }
            int infected = 0; var present = new HashSet<int>(); var down = new List<(int id, int fac, bool noLoss)>();
            foreach (var kv in PLEncounterManager.Instance.AllShips)
            {
                var o = kv.Value; if (o == null || o.ShipID == me.ShipID || counted.Contains(o.ShipID)) continue;
                present.Add(o.ShipID);
                int fac = -1; try { fac = o.FactionID; } catch { }
                if (fac < 0) try { fac = o.PersistantShipInfo?.FactionID ?? -1; } catch { }
                bool dead = o.HasBeenDestroyed; try { if (!dead && o.MyStats != null && o.MyStats.HullCurrent <= 0f) dead = true; } catch { }
                if (!dead)
                {
                    bool engaged = false; try { engaged = me.HostileShips.Contains(o.ShipID) || o.HostileShips.Contains(me.ShipID) || (me.TargetShip != null && me.TargetShip.ShipID == o.ShipID); } catch { }
                    if (engaged && fac >= 0) { seen[o.ShipID] = fac; try { hullOf[o.ShipID] = o.MyStats.HullCurrent / Math.Max(1f, o.MyStats.HullMax); } catch { } }
                    continue;
                }
                if (!seen.ContainsKey(o.ShipID)) continue;
                bool noLoss = false; try { noLoss = o.NoRepLossOnKilled; } catch { }
                down.Add((o.ShipID, seen[o.ShipID], noLoss));
            }
            // navi sparite dall'elenco senza che l'equipaggio abbia cambiato settore: abbattute anche loro
            foreach (var id in seen.Keys.Where(x => !present.Contains(x)).ToList()) { if (hullOf.TryGetValue(id, out var h) && h <= 0.2f) down.Add((id, seen[id], false)); else seen.Remove(id); }
            foreach (var (id, f, noLoss) in down)
            {
                counted.Add(id); seen.Remove(id);
                Plugin.L.LogInfo($"[fazioni] abbattuta nave {id} della fazione {f}");
                Crises.OnKill(f);
                if (f == 4) infected++;
                else if (Array.IndexOf(GalaxyWar.Factions, f) >= 0 && !noLoss) { int e = Enemy(f); if (e >= 0 && e != 4) Change(e, 1, null); } // la perdita verso la fazione colpita arriva dalla reputazione nativa
            }
            if (infected > 0) { foreach (var f in GalaxyWar.Factions) if (f != 4) Change(f, 1, null); Threat.Add(-0.4f * infected); }
        }

        // ---- conseguenze nello spazio delle fazioni
        static int OwnerHere() { try { return PLServer.GetCurrentSector()?.MySPI?.Faction ?? -1; } catch { return -1; } }
        public static float CalmFactor()
        {
            int f = OwnerHere(); if (f < 0 || Array.IndexOf(GalaxyWar.Factions, f) < 0 || f == 4) return 1f;
            return Level(f) switch { >= 2 => 1.35f, 1 => 1.15f, -2 => 0.8f, -3 => 0.65f, _ => 1f };
        }
        public static float InterceptWeight() { int f = OwnerHere(); return f >= 0 && f != 4 && Array.IndexOf(GalaxyWar.Factions, f) >= 0 && Level(f) <= -2 ? (Level(f) == -3 ? 2.5f : 1.2f) : 0f; }
        public static float RewardBonus(string issuer) { int f = ByName(issuer); return f < 0 ? 0f : Math.Clamp(Value(f) / 200f, -0.2f, 0.35f) + Leaders.RewardBonus(f); }

        static readonly Dictionary<int, string[]> PatrolTypes = new()
        {
            [0] = new[] { "E_INTREPID_SC", "E_DESTROYER" }, [1] = new[] { "E_OUTRIDER", "E_CARRIER" }, [2] = new[] { "E_WDDRONE3", "E_WDCRUISER" }, [5] = new[] { "E_PTDRONE", "E_POLYTECH_SHIP" },
        };
        public static string Abbr(int f) => f switch { 0 => "C.U.", 1 => "A.O.G.", 2 => "W.D.", 5 => "Polytechnic", _ => "" };

        // Pattuglia di una fazione ostile che intercetta l'equipaggio nel proprio spazio.
        public static bool Intercept(PLShipInfo ship, float power)
        {
            int f = OwnerHere(); if (!PatrolTypes.TryGetValue(f, out var types) || Level(f) > -2) return false;
            int n = 1 + (power >= 8 ? 1 : 0) + (Level(f) == -3 && power >= 4 ? 1 : 0); int made = 0;
            for (int i = 0; i < n; i++)
            {
                // un equipaggio alle prime armi incontra droni di pattuglia, uno esperto le navi della fazione
                var t = power < 6 ? Director.PickShip(power, 0) : Enum.TryParse<EShipType>(types[Math.Min(types.Length - 1, i == 0 && power >= 12 ? 1 : 0)], out var x) ? x : Director.PickShip(power, 0);
                if (Director.Spawn(ship, t, Director.Modifiers(power), $"Pattuglia {Abbr(f)}-{rng.Next(10, 99)}") != null) made++;
            }
            if (made == 0) return false;
            Director.Notify("COMUNICAZIONE IN ARRIVO", Level(f) == -3 ? $"Pattuglia della {GalaxyWar.Name(f)}: «{OurShip()}, siete nemici della {GalaxyWar.Name(f)}. Nessun avvertimento.»"
                : $"Pattuglia della {GalaxyWar.Name(f)}: «{OurShip()}, siete ricercati in questo spazio. Spegnete i motori e preparatevi all'abbordaggio.»");
            Service.Add("fazioni.intercettazioni", 1);
            return true;
        }

        // Rifugio: all'arrivo nello spazio di una fazione alleata, ogni tanto riparazioni e rifornimenti.
        static int lastJumps = -1;
        static void Haven()
        {
            int j = 0; try { j = Obscured.Jumps(); } catch { }
            if (lastJumps < 0 || j == lastJumps) { lastJumps = j; return; }
            lastJumps = j;
            int f = OwnerHere(); if (f < 0 || f == 4 || Array.IndexOf(GalaxyWar.Factions, f) < 0 || Level(f) < 2 || rng.NextDouble() > 0.3) return;
            var st = Stations.MyShip?.MyStats; if (st == null) return;
            float heal = st.HullMax * (Level(f) == 3 ? 0.35f : 0.2f); st.HullCurrent = Math.Min(st.HullMax, st.HullCurrent + heal);
            int credits = Level(f) == 3 ? 1000 : 400; try { PLServer.Instance.CurrentCrewCredits += credits; } catch { }
            Morale.Change(4, null);
            Director.Notify("RIFUGIO", $"Una stazione della {GalaxyWar.Name(f)} ci riconosce: squadre di riparazione a bordo (+{heal:0} scafo) e {credits} crediti di rifornimenti.");
        }

        // ---- reputazione nativa del gioco (PLServer.RepLevels, da -20 a +10): decide i prezzi dei negozi nel settore
        // (1 - rep * 5%), l'ostilità delle navi quando l'equipaggio ha rivelato la propria reputazione e le torrette di guardia.
        // Il gioco la sincronizza a tutti i client ma non la salva: qui la si ricava dalla nostra reputazione (che invece
        // si salva) e si assorbono le variazioni decise dal gioco (navi colpite o abbattute, ricompense, dialoghi).
        static readonly int[] lastNative = { int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        static int seedNative = int.MinValue;
        public static int ToNative(int v) => v >= 0 ? Math.Min(8, v / 8) : v > -25 ? 0 : -(1 + (-v - 25) / 6);
        // Gli elementi sono ObscuredInt dell'Anti-Cheat Toolkit (0x14 byte: chiave, valore nascosto, inited, valore esca, esca attiva):
        // si leggono e scrivono direttamente in memoria, perché la copia della struttura fatta dall'interop non coincide.
        const int Elem = 0x14, Header = 0x20;
        static IntPtr Slot(int f) => PLServer.Instance.RepLevels.Pointer + Header + f * Elem;
        static int GetNative(int f) { var e = Slot(f); return System.Runtime.InteropServices.Marshal.ReadInt32(e, 4) ^ System.Runtime.InteropServices.Marshal.ReadInt32(e, 0); }
        static void SetNative(int f, int v)
        {
            var e = Slot(f); int key = System.Runtime.InteropServices.Marshal.ReadInt32(e, 0);
            System.Runtime.InteropServices.Marshal.WriteInt32(e, 4, v ^ key); System.Runtime.InteropServices.Marshal.WriteByte(e, 8, 1);
            System.Runtime.InteropServices.Marshal.WriteInt32(e, 0xC, v); // l'esca resta coerente col valore vero
        }
        static void SyncNative()
        {
            int seed = 0; try { seed = (int)PLServer.Instance.GalaxySeed; } catch { }
            if (seed != seedNative) { seedNative = seed; for (int i = 0; i < lastNative.Length; i++) lastNative[i] = int.MinValue; }
            var arr = PLServer.Instance.RepLevels; if (arr == null || arr.Length < 6) return;
            // galassia nuova: si parte dalla reputazione con cui il gioco fa nascere l'equipaggio
            if (S.Count == 0) { foreach (var f in GalaxyWar.Factions) if (f != 4) S[f.ToString()] = Math.Clamp(GetNative(f) * 5, -100, 100); Saga.MarkDirty(); }
            foreach (var f in GalaxyWar.Factions)
            {
                if (f == 4) continue;
                int cur = GetNative(f);
                if (lastNative[f] != int.MinValue && cur != lastNative[f]) { int d = cur - lastNative[f]; Plugin.L.LogInfo($"[fazioni] reputazione nativa {GalaxyWar.Name(f)} {d:+0;-0}"); Change(f, d * 4, d < 0 ? $"Abbiamo aperto il fuoco su navi della {GalaxyWar.Name(f)}" : null); }
                int want = ToNative(Value(f)); if (cur != want) SetNative(f, want);
                lastNative[f] = want;
            }
        }
        public static string NativeText() { try { return string.Join(", ", GalaxyWar.Factions.Select(f => $"{f}:{GetNative(f)}")) + $" rivelata {(bool)PLServer.Instance.IsCrewRepRevealed}"; } catch (Exception e) { return e.Message; } }

        public static void Tick()
        {
            long now = clock.ElapsedMilliseconds; if (now < nextKill) return; nextKill = now + 1000;
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try { SyncNative(); TrackKills(); Haven(); } catch (Exception e) { Plugin.L.LogWarning("Fazioni: " + e.Message); nextKill = now + 10000; }
        }

        public static string Describe() => string.Join(", ", GalaxyWar.Factions.Select(f => $"{GalaxyWar.Name(f)} {Value(f):+0;-0;0} ({LevelName(Level(f))})")) + " | nativa " + NativeText();
        public static string Set(int f, int v) { S[f.ToString()] = Math.Clamp(v, -100, 100); Saga.MarkDirty(); GalaxyView.Dirty(); return Describe(); }
    }

    // Riepilogo della galassia per il pannello del capitano: costruito dall'host, inviato a tutti.
    public class GalaxyView
    {
        public class FactionRow { public int id, standing, sectors, momentum; public string name = "", color = "#FFFFFF", level = ""; public bool offensive; }
        public class CrisisRow { public string kind = "", title = "", color = "#FFFFFF", progress = "", hint = ""; public int stage; public float frac; }
        public class WantedRow { public string name = "", role = "", memory = ""; public int rank, sector, jumps; public bool hunted; }
        public List<FactionRow> factions = new(); public CrisisRow crisis; public List<WantedRow> wanted = new(); public string atlas = "", fame = "", diplo = ""; public List<string> crew = new(); public string env = "", rank = ""; public List<string> goals = new(); public string stardate = "", threat = "", calling = "", season = ""; public int trophies; public string campaign = ""; public Dictionary<string, int> upgrades = new(); public List<CodexEntry> codex = new(); public List<string> accords = new(); public string challenge = ""; public int wantedLevel, probeFrom = -1; public List<ProbeRow> probe = new(); public string wonder = "", rules = "frontiera"; public Bases.Panel bases = new(); public int morale = 60; public string traffic = ""; public Market.Panel market = new(); public Research.Panel research = new(); public Mysteries.Panel mystery = new(); public Passengers.Panel pax = new(); public CrewMind.Panel crewCmd = new(); public Wear.Panel wear = new(); public Fabrication.Panel fab = new(); public string boarding = "", boardStatus = "";

        static readonly JsonSerializerOptions json = new() { IncludeFields = true };
        static GalaxyView remote = new(), cached; static float builtAt = -10f, nextSend; static string lastSent = "";
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        public static void Dirty() { builtAt = -10f; }

        public static GalaxyView Current
        {
            get
            {
                if (!Host) return remote;
                if (cached == null || UnityEngine.Time.time - builtAt > 2f) { try { cached = Build(); } catch (Exception e) { Plugin.L.LogWarning("Riepilogo galassia: " + e.Message); cached ??= new GalaxyView(); } builtAt = UnityEngine.Time.time; }
                return cached;
            }
        }

        static GalaxyView Build()
        {
            var v = new GalaxyView(); var w = Saga.State.war;
            foreach (var f in GalaxyWar.Factions)
            {
                string color = "#FFFFFF"; try { color = "#" + UnityEngine.ColorUtility.ToHtmlStringRGB(PLGlobal.Instance.Galaxy.GetFactionColorForID(f)); } catch { }
                v.factions.Add(new FactionRow { id = f, name = f == 5 ? "Polytechnic" : f == 1 ? "Alliance of Gentl." : GalaxyWar.Name(f), color = color, standing = Factions.Value(f), level = Factions.LevelName(Factions.Level(f)), sectors = w?.counts != null && w.counts.TryGetValue(GalaxyWar.Name(f), out var n) ? n : 0, momentum = (int)GalaxyWar.Mom(f), offensive = w != null && w.offensive == f });
            }
            v.atlas = (Home.Has ? Home.Summary() + " · " : "") + Exploration.Summary(); v.diplo = Diplomacy.Summary(); v.crew = Officers.Rows(); v.env = Environments.Here; v.rank = Competitors.Standings(); v.goals = Logbook.GoalRows(); v.stardate = Logbook.Stardate(); v.threat = Threat.Summary(); v.calling = Calling.Label(); v.season = Calling.Season; v.trophies = Saga.State.trophies.Count; v.campaign = Campaigns.Summary(); v.upgrades = new Dictionary<string, int>(Saga.State.upgrades ?? new()); v.codex = Codex.Entries; v.challenge = Challenge.Line(); v.wantedLevel = Wanted.Level; v.probe = Probes.Rows; v.probeFrom = Probes.From; v.wonder = Wonders.Kind; v.rules = Rules.Mode; v.bases = Bases.Snapshot(); v.traffic = Traffic.Short(); v.market = Market.Snapshot(); v.research = Research.Snapshot(); v.mystery = Mysteries.Snapshot(); v.pax = Passengers.Snapshot(); v.crewCmd = CrewMind.Snapshot(); v.wear = Wear.Snapshot(); v.fab = Fabrication.Snapshot(); v.boarding = Boarding.Short(); v.boardStatus = Boarding.Status(); v.accords = Species.Defs.Where(d => Species.Accord(d.id)).Select(d => d.id).ToList(); v.morale = Saga.State.morale; v.fame = Morale.Title();
            var c = Crises.Cur;
            if (c != null) v.crisis = new CrisisRow { kind = c.kind, title = c.title, color = Crises.Kinds[c.kind].color, stage = c.stage, progress = Crises.Progress(c), hint = Crises.Hint(c), frac = (c.kind == "guerra" ? Math.Max(c.pa, c.pb) : c.progress) / 100f };
            var hunted = Saga.State.contract?.kind == "taglia" ? Saga.State.contract.target : null;
            foreach (var f in Nemesis.Alive.OrderByDescending(x => x.Full == hunted).ThenByDescending(x => x.grudge + x.rank * 2).Take(4))
            {
                string mem = f.memory switch
                {
                    "vinto" => $"Ci ha messi in fuga nel settore {Places.Of(f.memSector)}.",
                    "sfregiato" => $"Abbattuto nel settore {Places.Of(f.memSector)}, salvo in una capsula: cerca vendetta.",
                    "erede" => $"Erede di {f.heir}: ha giurato vendetta.",
                    "debito" => "Ci deve la vita: l'abbiamo soccorso tra i rottami.",
                    _ => f.met > 0 ? $"Incontri con noi: {f.met}." : f.deeds == 1 ? "Una impresa nota nei porti." : f.deeds > 1 ? $"{f.deeds} imprese note nei porti." : "Un nome nuovo nei porti.",
                };
                v.wanted.Add(new WantedRow { name = f.Full, role = f.role == "ribelle" ? "ribelle " + Factions.Abbr(f.faction) : Nemesis.RoleLabel(f), rank = f.rank, sector = f.sector, jumps = Nemesis.JumpsTo(f.sector), hunted = f.Full == hunted, memory = mem + " Tratti: " + Nemesis.TraitText(f) + "." });
            }
            return v;
        }

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            if (UnityEngine.Time.time < nextSend) return;
            nextSend = UnityEngine.Time.time + 5f;
            try
            {
                string payload = JsonSerializer.Serialize(Current, json);
                if (payload == lastSent && UnityEngine.Time.time % 30f > 5f) return;
                lastSent = payload;
                PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)("gal|" + payload), true, new RaiseEventOptions { Receivers = ReceiverGroup.Others });
            }
            catch (Exception e) { Plugin.L.LogWarning("Riepilogo galassia: " + e.Message); nextSend = UnityEngine.Time.time + 30f; }
        }

        public static void Receive(string msg) { try { remote = JsonSerializer.Deserialize<GalaxyView>(msg[4..], json) ?? new(); } catch { } }
    }
}

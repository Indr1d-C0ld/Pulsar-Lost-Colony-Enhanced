// Cronaca della galassia: archi narrativi in più parti e conseguenze che durano.
// - Archi: catene di missioni con gli stessi personaggi, in cui l'esito di una parte decide la successiva
//   (es. primo contatto riuscito -> scorta dell'ambasciata; fallito -> rappresaglia).
// - Fama: onore e infamia dell'equipaggio, che cambiano gli eventi scelti dal direttore.
// - Conseguenze ritardate: qualche salto dopo una missione arrivano gratitudine, vendette, scoperte.
// Lo stato è per galassia (seme) e vive su Balthasar, così sopravvive alle sessioni; senza server resta in memoria.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using L = Il2CppSystem.Collections.Generic;

namespace Melchior
{
    public class SagaArcRun { public string arc = ""; public int tier, variant, node; }
    public class SagaPending { public string kind = ""; public int due; public string name = "", text = ""; public int a, b; }
    public class SagaEntry { public long t; public string text = ""; }
    public class SagaState
    {
        public int onore, infamia, archiCompiuti, archiPerduti;
        public SagaArcRun arc; public List<SagaPending> pending = new(); public List<SagaEntry> log = new(); public List<string> recentArcs = new();
        public List<ContractOffer> board = new(); public Dictionary<string, int> trust = new(); public ContractOffer contract;
        public List<NewsItem> news = new();
        public WarState war = new(); public List<FleetInfo> fleets = new();
        public long lastPlayed; // ultima volta che la galassia è stata giocata (ms UTC)
    }

    public static class Saga
    {
        public const int ChainBase = 930000;
        // archi: famiglie delle parti (indici di Missions.Families) e titolo
        sealed class ArcDef { public string Id = "", Name = ""; public int[] Nodes = Array.Empty<int>(); }
        static readonly ArcDef[] Arcs =
        {
            new() { Id = "silenzio", Name = "Il silenzio", Nodes = new[] { 2, 4, 3, 8 } },   // ricognizione, contatto, scorta dell'ambasciata | rappresaglia
            new() { Id = "taglia", Name = "La taglia", Nodes = new[] { 0, 5, 0 } },          // luogotenente, relitto con i dati, ammiraglia del capo
            new() { Id = "colonia", Name = "La caduta", Nodes = new[] { 1, 7, 3 } },         // rifornimenti, evacuazione, scorta dei profughi
        };
        const int Tiers = 5, Variants = 6;
        static readonly Dictionary<int, (int arc, int tier, int variant, int node)> chainIds = new();
        static readonly Dictionary<string, string> arcTitles = new();

        static SagaState st = new(); static int seed = int.MinValue; static bool loaded, dirty, busy; static long nextSave, nextKeepAlive;
        static readonly System.Random rng = new();
        static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
        static readonly JsonSerializerOptions json = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        public static SagaState State => st;
        public static bool Loaded => loaded;
        // Ore passate dall'ultima partita su questa galassia: le consumano il notiziario e la guerra fra fazioni.
        public static double OfflineHours;
        public static void MarkDirty() => dirty = true;

        // ---- nomi e titoli dell'arco, deterministici dal numero di variante
        static (Dictionary<string, string>[] ov, string title) ArcCast(int arc, int variant)
        {
            var r = new System.Random(ChainBase + arc * 97 + variant * 13);
            string Pick(string[] a) => a[r.Next(a.Length)];
            switch (arc)
            {
                case 0:
                {
                    string sp = Pick(new[] { "i Vel'nari", "gli Ossidiani", "il Coro di Tessa", "i Navigatori Pallidi", "i Kethri", "gli Eredi di Lume" });
                    string tail = sp.Split(' ').Last(), of = sp.StartsWith("il ") ? "del " + sp[3..] : sp.StartsWith("gli ") ? "degli " + sp[4..] : "dei " + sp[2..];
                    return (new[]
                    {
                        new Dictionary<string, string> { ["what"] = $"un segnale ripetuto da secoli, firmato da {sp}" },
                        new Dictionary<string, string> { ["species"] = sp },
                        new Dictionary<string, string> { ["civ"] = "Ambasciata " + of, ["gang"] = "Separatisti" },
                        new Dictionary<string, string> { ["raiders"] = "Guerrieri " + of, ["lane"] = "il confine " + of },
                    }, "Il silenzio " + of);
                }
                case 1:
                {
                    string boss = Pick(new[] { "il pirata Vasquez", "la contrabbandiera Ilsa Marr", "il disertore Okonkwo", "il mercenario Draal", "la ladra di dati Yune", "il traditore Halloran" });
                    string sur = boss.Split(' ').Last(), lt = "Artiglio di " + sur;
                    return (new[]
                    {
                        new Dictionary<string, string> { ["target"] = lt, ["outlaw"] = boss },
                        new Dictionary<string, string> { ["wreck"] = lt, ["gang"] = "Uomini di " + sur },
                        new Dictionary<string, string> { ["target"] = "Trono di " + sur, ["outlaw"] = boss },
                    }, "La taglia di " + sur);
                }
                default:
                {
                    string col = Pick(new[] { "Nuova Ravenna", "Tycho Minore", "Porto Kepler", "Solaria", "Vesta IV", "Castello di Ghiaccio" });
                    return (new[]
                    {
                        new Dictionary<string, string> { ["civ"] = "Rifornimenti per " + col },
                        new Dictionary<string, string> { ["colony"] = col },
                        new Dictionary<string, string> { ["civ"] = "Profughi di " + col },
                    }, "La caduta di " + col);
                }
            }
        }

        static readonly string[][] Intros =
        {
            new[] { "Prima parte.", "Seconda parte: il segnale ha una sorgente.", "Terza parte: ci chiedono di scortare la loro ambasciata.", "Terza parte: dopo il nostro errore, rispondono con le armi." },
            new[] { "Prima parte: si comincia dal suo braccio destro.", "Seconda parte: il relitto del luogotenente custodisce la rotta per il covo.", "Ultima parte: l'ammiraglia del capo." },
            new[] { "Prima parte: la colonia chiede aiuto.", "Seconda parte: la colonia va evacuata.", "Ultima parte: i profughi devono raggiungere un porto sicuro." },
        };

        public static int ChainId(int arc, int tier, int variant, int node) => ChainBase + arc * 10000 + tier * 1000 + variant * 10 + node;

        // Catalogo delle parti degli archi, registrato come il resto delle missioni procedurali (su ogni macchina).
        internal static IEnumerable<(int id, PickupMissionData pmd, int family, int tier)> BuildChains()
        {
            for (int a = 0; a < Arcs.Length; a++)
                for (int t = 0; t < Tiers; t++)
                    for (int v = 0; v < Variants; v++)
                    {
                        var (ov, title) = ArcCast(a, v); arcTitles[Arcs[a].Id + v] = title;
                        for (int n = 0; n < Arcs[a].Nodes.Length; n++)
                        {
                            int id = ChainId(a, t, v, n); int fam = Arcs[a].Nodes[n];
                            int tier = Math.Min(Tiers - 1, t + (n == Arcs[a].Nodes.Length - 1 && a == 1 ? 1 : 0)); // il capo è più forte
                            string part = Missions.Label(fam);
                            string intro = Intros[a][n];
                            var pmd = Missions.Build(id, fam, tier, v, ov[n], $"{title} ({Math.Min(n, 2) + 1}/3): {part}", intro);
                            if (n == Arcs[a].Nodes.Length - 1 || (a == 0 && n == 2)) pmd.Reward_Credits = pmd.Reward_Credits * 3 / 2;
                            chainIds[id] = (a, t, v, n);
                            yield return (id, pmd, fam, tier);
                        }
                    }
        }

        public static bool IsChain(int id) => chainIds.ContainsKey(id);

        // ---- stato per galassia
        static int Seed() { try { return (int)PLServer.Instance.GalaxySeed; } catch { return int.MinValue; } }
        static int Jumps() { try { return (int)PLServer.Instance.NumJumps; } catch { return 0; } }

        public static void Tick()
        {
            try
            {
                if (!PhotonNetwork.inRoom || !PhotonNetwork.isMasterClient || PLServer.Instance == null) return;
                int s = Seed(); if (s == int.MinValue) return;
                if (s != seed) { seed = s; st = new SagaState(); loaded = false; Load(); }
                long now = clock.ElapsedMilliseconds;
                if (loaded && OfflineHours > 0) { double h = OfflineHours; OfflineHours = 0; News.Digest(h); GalaxyWar.Offline(h); }
                if (loaded && now >= nextKeepAlive) { nextKeepAlive = now + 300000; dirty = true; } // segna la partita come giocata anche senza eventi
                if (dirty && loaded && !busy && now >= nextSave) { nextSave = now + 3000; dirty = false; Save(); }
            }
            catch (Exception e) { Plugin.L.LogWarning("Cronaca: " + e.Message); }
        }

        static void Load()
        {
            int s = seed; busy = true;
            Task.Run(async () =>
            {
                try
                {
                    var txt = await http.GetStringAsync($"{Service.Base}/api/saga?seed={s}");
                    var got = JsonSerializer.Deserialize<SagaState>(txt, json);
                    if (got != null && s == seed && got.lastPlayed > 0) OfflineHours = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - got.lastPlayed) / 3600000.0;
                    if (got != null && s == seed) { got.pending ??= new(); got.log ??= new(); got.recentArcs ??= new(); got.board ??= new(); got.trust ??= new(); got.news ??= new(); got.war ??= new(); got.war.momentum ??= new(); got.war.counts ??= new(); got.fleets ??= new(); st = got; }
                    Plugin.L.LogInfo($"[cronaca] galassia {s}: onore {st.onore}, infamia {st.infamia}, arco {(st.arc == null ? "nessuno" : st.arc.arc + " parte " + (st.arc.node + 1))}, conseguenze in attesa {st.pending.Count}");
                }
                catch (Exception e) { Plugin.L.LogWarning("Cronaca non letta dal server (resta in memoria): " + e.Message); }
                finally { loaded = true; busy = false; }
            });
        }

        static void Save()
        {
            st.lastPlayed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int s = seed; string body = JsonSerializer.Serialize(st, json); busy = true;
            Task.Run(async () =>
            {
                try { await http.PostAsync($"{Service.Base}/api/saga?seed={s}", new StringContent(body, Encoding.UTF8, "application/json")); }
                catch (Exception e) { Plugin.L.LogWarning("Cronaca non salvata: " + e.Message); dirty = true; }
                finally { busy = false; }
            });
        }

        static void Chronicle(string text)
        {
            st.log.Add(new SagaEntry { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), text = text }); if (st.log.Count > 80) st.log.RemoveAt(0);
            dirty = true;
        }

        static void Later(string kind, int minJumps, int maxJumps, string name = "", string text = "", int a = 0, int b = 0)
        {
            st.pending.Add(new SagaPending { kind = kind, due = Jumps() + rng.Next(minJumps, maxJumps + 1), name = name, text = text, a = a, b = b });
            dirty = true;
        }

        // ---- esiti delle missioni procedurali (chiamato dall'host)
        public static void OnOutcome(int id, int family, int tier, bool success, string mainName)
        {
            string fam = Missions.Families[family];
            Plugin.L.LogInfo($"[cronaca] esito {id} ({fam}): {(success ? "riuscita" : "fallita")}");
            // fama
            int dOn = 0, dInf = 0;
            switch (fam)
            {
                case "soccorso": dOn = success ? 2 : -1; break;
                case "scorta": dOn = success ? 2 : -1; break;
                case "evacuazione": dOn = success ? 3 : -2; break;
                case "contatto": if (success) dOn = 3; else dInf = 2; break;
                case "caccia": case "interdizione": if (success) dOn = 1; break;
            }
            st.onore = Math.Max(0, st.onore + dOn); st.infamia = Math.Max(0, st.infamia + dInf);
            // conseguenze ritardate
            if (success && (fam == "soccorso" || fam == "scorta" || fam == "evacuazione") && rng.NextDouble() < 0.6)
                Later("gratitudine", 2, 4, mainName, "", 1500 + tier * 1500);
            if (success && fam == "caccia" && rng.NextDouble() < 0.4) Later("vendetta", 2, 5, mainName, "", tier);
            if (success && fam == "recupero" && !IsChain(id) && st.arc == null && rng.NextDouble() < 0.5) Later("scoperta", 1, 3);
            if (success && fam == "eco" && rng.NextDouble() < 0.5) Later("paradosso", 1, 3);
            if (!success && fam == "contatto" && !IsChain(id)) Later("rappresaglia", 1, 3, mainName, "", tier);
            string deal = Contracts.OnOutcome(id, success); if (deal != null) { Director.Notify("BACHECA", deal); Chronicle(deal); }
            if (success) { var got = Loot.Grant(Stations.MyShip, 1 + tier / 2, Math.Min(6, 3 + tier), "missione"); if (got != null) Director.Notify("RICOMPENSA", $"Il committente aggiunge un pezzo alla stiva: {got.Name}."); }
            Chronicle($"{Missions.Label(family)} «{mainName}»: {(success ? "riuscita" : "fallita")}{(dOn != 0 ? $", onore {dOn:+0;-0}" : "")}{(dInf != 0 ? $", infamia +{dInf}" : "")}");
            // archi
            if (chainIds.TryGetValue(id, out var c) && st.arc != null && Arcs[c.arc].Id == st.arc.arc && st.arc.node == c.node) Advance(c, success);
            dirty = true;
        }

        static void Advance((int arc, int tier, int variant, int node) c, bool success)
        {
            var def = Arcs[c.arc]; string title = arcTitles.TryGetValue(def.Id + c.variant, out var t) ? t : def.Name;
            int next = -1;
            if (def.Id == "silenzio") next = c.node == 0 ? (success ? 1 : -1) : c.node == 1 ? (success ? 2 : 3) : -1;
            else if (c.node < def.Nodes.Length - 1 && success) next = c.node + 1;
            if (next >= 0)
            {
                st.arc.node = next; Later("arco", 1, 2);
                Chronicle($"{title}: la storia continua.");
                Director.Notify("CRONACA", $"{title}: la prossima parte arriverà presto.");
                return;
            }
            bool won = success && (def.Id != "silenzio" || c.node == 2);
            if (won) { st.archiCompiuti++; st.onore += 2; var got = Loot.Grant(Stations.MyShip, 4, 6, "arco"); if (got != null) Director.Notify("RICOMPENSA", $"Per la conclusione della storia: {got.Name}."); } else st.archiPerduti++;
            Chronicle($"{title}: {(won ? "conclusa con successo" : "conclusa male")}.");
            Director.Notify("CRONACA", won ? $"{title} si chiude. Se ne parlerà nei porti per molto tempo." : $"{title} si chiude, non come speravamo.");
            Service.Add(won ? "cronaca.archi.compiuti" : "cronaca.archi.perduti", 1);
            st.arc = null;
        }

        // ---- inizio di un arco (dal direttore o dalla console)
        public static bool StartArc(float power, string which = null)
        {
            if (st.arc != null || Missions.AnyActive()) return false;
            var pool = Enumerable.Range(0, Arcs.Length).Where(a => which != null ? Arcs[a].Id == which : !st.recentArcs.Contains(Arcs[a].Id)).ToList();
            if (pool.Count == 0) pool = Enumerable.Range(0, Arcs.Length).ToList();
            int arc = pool[rng.Next(pool.Count)], tier = Math.Clamp((int)(power / 4f), 0, Tiers - 1), variant = rng.Next(Variants);
            st.arc = new SagaArcRun { arc = Arcs[arc].Id, tier = tier, variant = variant, node = 0 };
            st.recentArcs.Add(Arcs[arc].Id); if (st.recentArcs.Count > 2) st.recentArcs.RemoveAt(0);
            string title = arcTitles.TryGetValue(Arcs[arc].Id + variant, out var t) ? t : Arcs[arc].Name;
            Chronicle($"Inizia un nuovo capitolo: {title}.");
            dirty = true;
            return StartNode();
        }

        static bool StartNode()
        {
            if (st.arc == null) return false;
            int a = Array.FindIndex(Arcs, x => x.Id == st.arc.arc); if (a < 0) { st.arc = null; return false; }
            return Missions.Start(ChainId(a, st.arc.tier, st.arc.variant, st.arc.node));
        }

        // ---- conseguenze arrivate a scadenza: le esegue il direttore al posto di un evento qualsiasi
        public static bool TryDue(PLShipInfo ship, float power, bool force = false)
        {
            if (!loaded || Missions.BusyHere()) return false;
            int j = Jumps(); var p = st.pending.Where(x => force || x.due <= j).OrderBy(x => x.due).FirstOrDefault(); if (p == null) return false;
            bool ok = Run(ship, power, p);
            if (ok || p.kind != "arco") { st.pending.Remove(p); dirty = true; }
            return ok;
        }

        static bool Run(PLShipInfo ship, float power, SagaPending p)
        {
            switch (p.kind)
            {
                case "arco": return StartNode();
                case "gratitudine":
                    try { PLServer.Instance.CurrentCrewCredits += p.a; } catch { return false; }
                    Director.Notify("COMUNICAZIONE IN ARRIVO", $"Da «{p.name}»: «Non vi abbiamo dimenticati.» Hanno fatto una colletta: {p.a} crediti accreditati.");
                    Chronicle($"Gratitudine da «{p.name}»: {p.a} crediti."); Service.Add("cronaca.gratitudine", 1);
                    return true;
                case "vendetta":
                {
                    int made = 0; string who = "Vendicatori di " + p.name.Split(' ').Last();
                    for (int i = 0; i < 1 + p.a / 2; i++) if (Director.Spawn(ship, Director.PickShip(power, 0), Director.Modifiers(power) | 1 << (int)EShipModifierType.RECKLESS, who) != null) made++;
                    if (made == 0) return false;
                    Director.Notify("ALLARME DI PROSSIMITÀ", $"Gli amici della «{p.name}» non hanno dimenticato: {made} navi ci tagliano la strada.");
                    Chronicle($"Vendetta per la «{p.name}»."); return true;
                }
                case "rappresaglia":
                {
                    int made = 0; string who = p.name.Replace("Vascello", "Guerriero");
                    for (int i = 0; i < 2 + p.a / 2; i++) if (Director.Spawn(ship, Director.PickShip(power + 4, 0), Director.Modifiers(power + 4), who) != null) made++;
                    if (made == 0) return false;
                    st.infamia++; Director.Notify("ALLARME DI PROSSIMITÀ", $"Navi simili a «{p.name}» escono dalla curvatura. Non sono venute a parlare.");
                    Chronicle("Rappresaglia per il primo contatto fallito."); return true;
                }
                case "scoperta":
                    Director.Notify("RAPPORTO DI PLANCIA", "La Scienza ha decifrato la scatola nera recuperata: contiene la rotta verso qualcosa di più grande.");
                    return StartArc(power);
                case "paradosso":
                    try { PLServer.Instance.CurrentCrewCredits += 1000; } catch { }
                    Director.Notify("ANOMALIA", "Nella stiva compare una cassa che nessuno ha caricato, firmata dal capitano. Con la data di domani. Dentro: 1000 crediti.");
                    Chronicle("Paradosso temporale: una cassa dal futuro."); return true;
            }
            return true;
        }

        // Influenza della fama sugli eventi del direttore.
        public static float Weight(string ev) => ev switch
        {
            "taglia" or "rivali" => 1f + st.infamia / 5f,
            "missione" => 1f + st.onore / 12f,
            _ => 1f,
        };

        public static string Describe()
        {
            string arc = st.arc == null ? "nessuno" : $"{st.arc.arc} parte {st.arc.node + 1} (livello {st.arc.tier + 1}, variante {st.arc.variant})";
            return $"galassia {seed} {(loaded ? "letta" : "in lettura")}: onore {st.onore}, infamia {st.infamia}, arco {arc}, archi compiuti {st.archiCompiuti}/perduti {st.archiPerduti}, salti {Jumps()}, in attesa: {string.Join(", ", st.pending.Select(p => $"{p.kind}@{p.due}").DefaultIfEmpty("niente"))}";
        }
    }
}

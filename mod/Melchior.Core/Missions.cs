// Missioni procedurali nel sistema nativo delle "pickup mission": settore dedicato, navi, obiettivi, ricompense,
// interfaccia e sincronizzazione sono quelli del gioco. Il catalogo è generato in modo deterministico dallo stesso
// codice su ogni macchina (host e client), con ID a partire da 910000: tutti hanno gli stessi dati senza scambiarli.
// Il direttore sceglie la missione adatta alla potenza dell'equipaggio e la avvia sull'host; la logica di ciascuna
// famiglia (arrivo, ondate, condizioni di stazione, fallimento) gira sull'host e completa gli obiettivi personalizzati.
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using L = Il2CppSystem.Collections.Generic;

namespace Melchior
{
    public static class Missions
    {
        public const int BaseId = 910000;
        public static readonly string[] Families = { "caccia", "soccorso", "ricognizione", "scorta", "contatto", "recupero", "eco", "evacuazione", "interdizione" };
        const int Tiers = 5, Variants = 6;
        static bool registered;
        static readonly Dictionary<int, (int family, int tier)> catalog = new();
        static readonly Dictionary<int, string[]> names = new(); // nomi propri usati dalla logica: [0] nave principale, [1] gruppo ostile
        static readonly System.Random rng = new();
        static readonly List<int> recent = new();
        static long nextCheck; static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static readonly HashSet<string> fired = new();

        // stato in corso di ogni missione, solo sull'host
        sealed class Run { public bool Arrived, Seen; public float InSector, Progress, Hazard, LastWave; public int Wave, Stage; public readonly List<int> Spawned = new(); public bool Failed; }
        static readonly Dictionary<int, Run> runs = new();

        // navi per fascia
        static readonly int[] HuntShip = { 2, 9, 3, 14, 6 };       // drone pesante, Stargazer, incrociatore, cacciatorpediniere, portaerei
        static readonly int[] RaiderShip = { 1, 2, 4, 9, 3 };
        static readonly int[] Derelicts = { 19, 18, 20 };          // relitti di Intrepid, cacciatorpediniere, portaerei
        static readonly int[] Strangers = { 26, 56, 9 };           // Alchimista, nave Polytech, Stargazer
        static readonly (int type, int maxSub)[] Salvage = { (1, 9), (3, 6), (2, 3), (6, 3) }; // scudi, reattore, curvatura, scafo

        static readonly string[] Targets = { "Lama di Kessar", "Vedova Nera", "Ombra di Teth", "Cuore di Brace", "Ultima Eclissi", "Sentenza", "Falco di Morrow", "Spina del Vuoto", "Marea Rossa", "Occhio di Nadir", "Silenzio Eterno", "Furia di Orione" };
        static readonly string[] Outlaws = { "il pirata Vasquez", "la contrabbandiera Ilsa Marr", "il disertore Okonkwo", "il mercenario Draal", "la ladra di dati Yune", "il traditore Halloran" };
        static readonly string[] Civilians = { "Speranza di Aldebaran", "Carovana Meridiana", "Stella del Mattino", "Pellegrina", "Seme di Arcadia", "Ultima Rotta", "Lanterna", "Madre Terra" };
        static readonly string[] Raiders = { "Predoni", "Sciacalli", "Razziatori", "Corvi" };
        static readonly string[] Anomalies = { "una distorsione gravitazionale", "un'eco di curvatura senza sorgente", "una nube di particelle tachioniche", "un relitto che emette un segnale antico", "una fenditura subspaziale", "una stella che pulsa in codice" };
        static readonly string[] Species = { "i Vel'nari", "gli Ossidiani", "il Coro di Tessa", "i Navigatori Pallidi", "i Kethri", "gli Eredi di Lume" };
        static readonly string[] Wrecks = { "Indomita", "Argo", "Coraggiosa", "Nautilus", "Aurora Boreale", "Sentinella", "Vagabonda" };
        static readonly string[] Colonies = { "Nuova Ravenna", "Tycho Minore", "Porto Kepler", "Solaria", "Vesta IV", "Castello di Ghiaccio" };
        static readonly string[] Lanes = { "il corridoio di Sirio", "la rotta del Sale", "il passo di Cerbero", "lo stretto di Hadley", "il varco di Orfeo" };

        // ---- catalogo
        public static void TryRegister()
        {
            if (registered) return;
            try
            {
                var io = PLCampaignIO.Instance; var cd = io?.m_CampaignData;
                if (cd == null || cd.PickupMissionTypes == null || cd.PickupMissionTypes.Count == 0 || cd.MissionTypes == null) return;
                int n = 0;
                for (int f = 0; f < Families.Length; f++)
                    for (int t = 0; t < Tiers; t++)
                        for (int v = 0; v < Variants; v++)
                        {
                            int id = BaseId + f * 1000 + t * 100 + v;
                            if (catalog.ContainsKey(id)) continue;
                            var pmd = Build(id, f, t, v);
                            cd.MissionTypes.Add(pmd.Cast<MissionData>()); cd.PickupMissionTypes.Add(pmd);
                            catalog[id] = (f, t); n++;
                        }
                foreach (var (cid, cpmd, cfam, ctier) in Saga.BuildChains())
                {
                    if (catalog.ContainsKey(cid)) continue;
                    cd.MissionTypes.Add(cpmd.Cast<MissionData>()); cd.PickupMissionTypes.Add(cpmd); catalog[cid] = (cfam, ctier); n++;
                }
                registered = true;
                Plugin.L.LogInfo($"Missioni procedurali registrate: {n} in {Families.Length} famiglie, archi compresi");
            }
            catch (Exception e) { registered = true; Plugin.L.LogError("Missioni procedurali non registrate: " + e); }
        }

        // "di" con l'articolo: di + il pirata = del pirata, di + la ladra = della ladra
        static string Di(string who) => who.StartsWith("il ") ? "del " + who[3..] : who.StartsWith("la ") ? "della " + who[3..] : who.StartsWith("lo ") ? "dello " + who[3..] : "di " + who;

        static ObjectiveData Obj(int type, string text, string script, params (string k, string v)[] data)
        {
            var o = new ObjectiveData { ObjType = type };
            if (o.Data == null) o.Data = new L.Dictionary<string, string>();
            o.UpdateDataMapEntry("CustomText", text); o.UpdateDataMapEntry("CustomTextOriginal", text);
            o.UpdateDataMapEntry("ScriptName", script); o.UpdateDataMapEntry("Comment", "");
            foreach (var (k, v) in data) o.UpdateDataMapEntry(k, v);
            return o;
        }

        static RewardData Reward(int type, int amount, int a = 0, int b = 0, float d = 0) => new() { RwdType = type, RewardAmount = amount, RewardDataA = a, RewardDataB = b, RewardDataC = "", RewardDataD = d };

        static PickupShipData Ship(string name, int type, int faction, bool hostile, float hull = 100)
        {
            var s = new PickupShipData { Name = name, ShipType = type, FactionID = faction, ForceHostileAgainstPlayerShip = hostile, ForceHostileAgainstAll = false, ForceHostileAgainstName = "",
                Flagged = false, CreditsToDrop = 0, DropDefaultCredits = true, DialogueActorID = "NONE", ImmediatelyStartDialogue = false, RandomizeName = false, StartingHullPercent = hull, StartingShldPercent = 100 };
            if (s.AllComponentOverrides == null) s.AllComponentOverrides = new L.List<ComponentOverrideData>();
            if (s.AllCMIDs == null) s.AllCMIDs = new Il2CppReferenceArray<CrewMemberInfoData>(0);
            return s;
        }

        static PickupSectorData Sector(float distance, int faction, params PickupShipData[] ships)
        {
            var s = new PickupSectorData { SectorType = 0, Distance = distance, FactionID = faction, Name = "", UniqueType = false, SpawnRegularShipsToo = false };
            if (s.Ships == null) s.Ships = new L.List<PickupShipData>();
            foreach (var x in ships) s.Ships.Add(x);
            return s;
        }

        // ov: nomi imposti dall'arco narrativo (bersaglio, specie, colonia...), title/intro: titolo e premessa della parte dell'arco
        internal static PickupMissionData Build(int id, int family, int tier, int variant, Dictionary<string, string> ov = null, string title = null, string intro = null)
        {
            var r = new System.Random(id);
            string P(string[] a) => a[r.Next(a.Length)];
            string O(string key, string[] a) => ov != null && ov.TryGetValue(key, out var v) ? v : P(a);
            var m = new PickupMissionData();
            m.MissionID = id; m.Rarity = 0f; // mai scelta dal gioco da sola: la avvia il direttore
            m.LongRangeDialogueActorID = ""; m.LongRangeDialogueDisplayName = ""; m.LongRangeDialogueDisplayNameOriginal = "";
            m.Hidden = false; m.BlocksOtherPickupMissionStarts = false; m.CanBeBlockedByOtherPickupMissions = true; m.IsRepeatable = true;
            m.CallOnStart = false; m.CallOnFailure = false; m.CallOnSuccess = false; m.CanBeAbandonedByPlayers = true;
            m.StartingRequirements_ALLMUSTMATCH = true; m.SuccessRequirements_ALLMUSTMATCH = true; m.FailureRequirements_ALLMUSTMATCH = true;
            m.RecMinLevel = tier * 3; m.RecMaxLevel = tier * 3 + 6; m.RecCrewLevel = -1; m.ChaosModifier = 0;
            m.FactionRepModifier = new Il2CppStructArray<int>(8); m.FactionStrModifier = new Il2CppStructArray<int>(8);
            m.Objectives = new L.List<ObjectiveData>();
            m.StartingRequirements = new L.List<RequirementData>(); m.SuccessRequirements = new L.List<RequirementData>(); m.FailureRequirements = new L.List<RequirementData>();
            m.StartingRewards = new L.List<RewardData>(); m.SuccessRewards = new L.List<RewardData>(); m.FailureRewards = new L.List<RewardData>();
            m.Sectors = new L.List<PickupSectorData>();
            // il fallimento automatico del gioco è disattivato (requisito che non passa mai): i fallimenti li decide la logica qui sotto
            var never = new RequirementData { ReqType = 5 }; if (never.Data == null) never.Data = new L.Dictionary<string, string>();
            never.UpdateDataMapEntry("MissionIDCompleted_ID", "1"); never.UpdateDataMapEntry("Comment", ""); m.FailureRequirements.Add(never);

            float dist = 2 + tier + r.Next(3); int credits = 1500 + tier * 2200 + r.Next(6) * 250, xp = 1 + tier / 2;
            string script = "mel" + id, where = $"a {dist:0} settori da qui";
            switch (family)
            {
                case 0: // caccia: un bersaglio con nome
                {
                    string target = O("target", Targets), who = O("outlaw", Outlaws);
                    names[id] = new[] { target };
                    m.Name = "Caccia: " + target; m.Desc = $"Una taglia pende sulla «{target}» {Di(who)}. Ultima posizione nota {where}.";
                    m.Sectors.Add(Sector(dist, 1, Ship(target, HuntShip[tier], 1, true)));
                    m.Objectives.Add(Obj(8, $"Distruggi la «{target}»", script + "a", ("KSN_Name", target), ("KSN_AmountNeeded", "1")));
                    if (tier >= 2) m.Objectives.Add(Obj(10, "", script + "t", ("CMIJC_Value", (6 + dist).ToString("0"))));
                    break;
                }
                case 1: // soccorso: predoni attorno a una nave civile
                {
                    string civ = O("civ", Civilians), gang = O("gang", Raiders); int n = 2 + Math.Min(2, tier / 2);
                    m.Name = "Soccorso: " + civ; m.Desc = $"Segnale di soccorso dalla nave civile «{civ}», attaccata da {gang.ToLowerInvariant()} {where}. Arriviamo prima che sia tardi.";
                    var ships = new List<PickupShipData> { Ship(civ, 11, 0, false, 45) };
                    for (int i = 0; i < n; i++) ships.Add(Ship(gang, RaiderShip[tier], 1, true));
                    m.Sectors.Add(Sector(dist, 0, ships.ToArray()));
                    m.Objectives.Add(Obj(8, $"Respingi i {gang.ToLowerInvariant()} ({n})", script + "a", ("KSN_Name", gang), ("KSN_AmountNeeded", n.ToString())));
                    m.SuccessRewards.Add(Reward(6, 1, 0)); credits = credits * 3 / 4;
                    break;
                }
                case 2: // ricognizione: raggiungere l'anomalia e analizzarla con l'impulso dei sensori
                {
                    string what = O("what", Anomalies);
                    m.Name = "Ricognizione: anomalia " + (char)('A' + variant) + "-" + (tier + 1) + (r.Next(9) + 1);
                    m.Desc = $"I sensori a lungo raggio hanno rilevato {what} {where}. Il Comando chiede dati di prima mano.";
                    m.Sectors.Add(Sector(dist, 1));
                    m.Objectives.Add(Obj(0, "Raggiungi il settore dell'anomalia", script + "a"));
                    m.Objectives.Add(Obj(0, "Analizza l'anomalia con l'impulso attivo dei sensori", script + "b"));
                    xp += 1;
                    break;
                }
                case 3: // scorta: proteggere un convoglio finché non salta
                {
                    string civ = O("civ", Civilians), gang = O("gang", Raiders); int secs = 90 + tier * 30;
                    names[id] = new[] { civ, gang };
                    m.Name = "Scorta: " + civ; m.Desc = $"Il convoglio «{civ}» deve caricare il motore a curvatura {where}, ma {gang.ToLowerInvariant()} lo braccano. Restate con loro finché non salta.";
                    m.Sectors.Add(Sector(dist, 0, Ship(civ, 11, 0, false, 80)));
                    m.Objectives.Add(Obj(0, $"Raggiungi il convoglio «{civ}»", script + "a"));
                    m.Objectives.Add(Obj(0, $"Proteggi il convoglio per {secs} secondi", script + "b"));
                    m.SuccessRewards.Add(Reward(6, 1, 0)); credits = credits * 11 / 10;
                    break;
                }
                case 4: // primo contatto: scansione della Scienza, poi silenzio del Capitano, senza sparare
                {
                    string who = O("species", Species), ship = "Vascello di " + who.Split(' ').Last();
                    names[id] = new[] { ship };
                    m.Name = "Primo contatto: " + who; m.Desc = $"Una nave sconosciuta {where} trasmette su una frequenza mai registrata. Sono {who}. Nessuno spari: osservare, capire, poi tendere la mano.";
                    m.Sectors.Add(Sector(dist, 3, Ship(ship, Strangers[r.Next(Strangers.Length)], 3, false)));
                    m.Objectives.Add(Obj(0, "Raggiungi le coordinate del contatto", script + "a"));
                    m.Objectives.Add(Obj(0, "Scienza: aggancia e completa la scansione della nave sconosciuta", script + "b"));
                    m.Objectives.Add(Obj(0, "Capitano: ordine SILENZIO per 30 secondi, armi a riposo", script + "c"));
                    xp += 2; credits = credits * 4 / 5;
                    break;
                }
                case 5: // recupero: scansionare un relitto, poi respingere gli sciacalli
                {
                    string wreck = O("wreck", Wrecks), gang = O("gang", Raiders); var (ct, maxSub) = Salvage[r.Next(Salvage.Length)];
                    names[id] = new[] { wreck, gang };
                    m.Name = "Recupero: relitto della " + wreck; m.Desc = $"Il relitto della «{wreck}» è alla deriva {where}. La sua scatola nera e il suo carico valgono il viaggio, se arriviamo prima degli sciacalli.";
                    m.Sectors.Add(Sector(dist, 1, Ship(wreck, Derelicts[r.Next(Derelicts.Length)], 0, false, 15)));
                    m.Objectives.Add(Obj(0, $"Raggiungi il relitto della «{wreck}»", script + "a"));
                    m.Objectives.Add(Obj(0, "Scienza: scansiona il relitto e recupera la scatola nera", script + "b"));
                    m.Objectives.Add(Obj(0, $"Respingi i {gang.ToLowerInvariant()}", script + "c"));
                    m.SuccessRewards.Add(Reward(3, Math.Min(5, tier + 1), ct, r.Next(maxSub + 1))); credits = credits * 3 / 5;
                    break;
                }
                case 6: // eco temporale: una copia ostile della nostra nave esce da una fenditura
                {
                    m.Name = "Anomalia temporale " + (tier + 1) + "." + (variant + 1);
                    m.Desc = $"Una fenditura temporale {where} emette il nostro stesso segnale di identificazione, con una data di domani. Andiamo a vedere chi lo trasmette.";
                    m.Sectors.Add(Sector(dist, 1));
                    m.Objectives.Add(Obj(0, "Raggiungi la fenditura temporale", script + "a"));
                    m.Objectives.Add(Obj(0, "Distruggi l'eco temporale della nave", script + "b"));
                    xp += 2;
                    break;
                }
                case 7: // evacuazione: l'ingegneria tiene in piedi il supporto vitale sotto radiazioni
                {
                    string col = O("colony", Colonies); int secs = 60 + tier * 15;
                    names[id] = new[] { "Evacuazione " + col };
                    m.Name = "Evacuazione: " + col; m.Desc = $"La colonia di {col} {where} è investita da una tempesta di radiazioni. Le navette arriveranno a bordo: servirà ogni litro d'aria che il supporto vitale può dare.";
                    m.Sectors.Add(Sector(dist, 0, Ship("Navetta di " + col, 11, 0, false, 60)));
                    m.Objectives.Add(Obj(0, $"Raggiungi l'orbita di {col}", script + "a"));
                    m.Objectives.Add(Obj(0, $"Ingegneria: rete EPS al supporto vitale (+2) e integrità oltre 70% per {secs} secondi", script + "b"));
                    m.SuccessRewards.Add(Reward(6, 2, 0)); xp += 1;
                    break;
                }
                default: // interdizione: tre ondate di incursori, entro un limite di salti
                {
                    string lane = O("lane", Lanes), gang = ov != null && ov.TryGetValue("raiders", out var rg) ? rg : "Incursori"; int per = 1 + Math.Min(2, tier / 2), total = per * 3;
                    names[id] = new[] { gang, gang };
                    m.Name = "Interdizione: " + lane; m.Desc = $"{gang} stanno chiudendo {lane}, {where}. Tre ondate, nessun rinforzo: teniamo aperto il passaggio.";
                    var ships = new List<PickupShipData>(); for (int i = 0; i < per; i++) ships.Add(Ship(gang, RaiderShip[tier], 1, true));
                    m.Sectors.Add(Sector(dist, 1, ships.ToArray()));
                    m.Objectives.Add(Obj(8, $"Respingi la prima ondata: {gang} ({per})", script + "a", ("KSN_Name", gang), ("KSN_AmountNeeded", per.ToString())));
                    m.Objectives.Add(Obj(0, "Respingi la seconda e la terza ondata", script + "b"));
                    m.Objectives.Add(Obj(10, "", script + "t", ("CMIJC_Value", (8 + dist).ToString("0"))));
                    credits = credits * 13 / 10; xp += 1;
                    break;
                }
            }
            if (title != null) m.Name = title;
            if (intro != null) m.Desc = intro + " " + m.Desc;
            m.OriginalName = m.Name; m.OriginalDesc = m.Desc; m.Reward_Credits = credits; m.Reward_XP = xp;
            m.SuccessRewards.Add(Reward(1, credits)); m.SuccessRewards.Add(Reward(4, xp));
            return m;
        }

        // ---- avvio, chiamato dal direttore sull'host
        static IEnumerable<PLMissionBase> Active()
        {
            var all = PLServer.Instance?.AllMissions; if (all == null) yield break;
            for (int i = 0; i < all.Count; i++) { var mb = all[i]; if (mb != null && !mb.Ended && !mb.Abandoned) yield return mb; }
        }
        public static bool AnyActive() => Active().Any(mb => catalog.ContainsKey(mb.MissionTypeID));

        public static bool Offer(float power, string family = null)
        {
            if (!registered || AnyActive()) return false;
            int tier = Math.Clamp((int)(power / 4f), 0, Tiers - 1);
            int[] fams;
            if (family != null) { int f = Array.IndexOf(Families, family); if (f < 0) return false; fams = new[] { f }; }
            else
            {
                // varietà: le famiglie usate di recente sono escluse, finché ce ne sono altre
                var used = recent.Where(catalog.ContainsKey).Select(x => catalog[x].family).TakeLast(4).ToHashSet();
                fams = Enumerable.Range(0, Families.Length).Where(f => !used.Contains(f)).ToArray();
                if (fams.Length == 0) fams = Enumerable.Range(0, Families.Length).ToArray();
                fams = new[] { fams[rng.Next(fams.Length)] };
            }
            var pool = catalog.Where(kv => kv.Key < Saga.ChainBase && fams.Contains(kv.Value.family) && kv.Value.tier == tier && !recent.Contains(kv.Key)).Select(kv => kv.Key).ToList();
            if (pool.Count == 0) return false;
            return Start(pool[rng.Next(pool.Count)]);
        }

        public static readonly string[] Labels = { "Caccia", "Soccorso", "Ricognizione", "Scorta", "Primo contatto", "Recupero", "Anomalia temporale", "Evacuazione", "Interdizione" };
        public static string Label(int family) => Labels[Math.Clamp(family, 0, Labels.Length - 1)];

        public static bool Start(int id)
        {
            if (!registered || AnyActive() || !catalog.ContainsKey(id)) return false;
            var pmd = PLCampaignIO.Instance.GetAllPickupMissionData().ToArray().FirstOrDefault(x => x.MissionID == id); if (pmd == null) return false;
            PLServer.Instance.StartPickupMission(id, false);
            recent.Add(id); if (recent.Count > 12) recent.RemoveAt(0);
            fired.RemoveWhere(s => s.StartsWith("mel" + id)); runs[id] = new Run();
            Director.Notify("COMUNICAZIONE IN ARRIVO", pmd.Name + ". " + pmd.Desc);
            Service.Add("missioni.procedurali.avviate", 1);
            Plugin.L.LogInfo($"[missioni] avviata {id} «{pmd.Name}»");
            return true;
        }

        // ---- logica delle famiglie, sull'host, una volta al secondo
        public static void Tick()
        {
            long now = clock.ElapsedMilliseconds; if (now < nextCheck) return; nextCheck = now + 1000;
            TryRegister();
            try
            {
                if (!registered || !PhotonNetwork.inRoom || !PhotonNetwork.isMasterClient || PLServer.Instance == null) return;
                var ship = Stations.MyShip; if (ship == null) return;
                var sector = PLServer.GetCurrentSector(); bool warp = false; try { warp = ship.InWarp; } catch { }
                CheckOutcomes();
                foreach (var mb in Active().ToList())
                {
                    int id = mb.MissionTypeID; if (!catalog.TryGetValue(id, out var c)) continue;
                    if (!runs.TryGetValue(id, out var run)) runs[id] = run = new Run();
                    bool here = !warp && sector != null && sector.MissionSpecificID == id;
                    if (!here) continue;
                    AdoptNames(sector);
                    if (!run.Arrived) { run.Arrived = true; OnArrive(mb, id, c.family, c.tier, ship, run); }
                    run.InSector += 1f;
                    Step(mb, id, c.family, c.tier, ship, run);
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Missioni procedurali: " + e.Message); nextCheck = now + 10000; }
        }

        // Il gioco crea alcuni tipi di nave (droni, civili) col nome generico invece di quello della missione:
        // le navi persistenti del settore riprendono il nome previsto, che il direttore poi mantiene.
        static void AdoptNames(PLSectorInfo sector)
        {
            try
            {
                var list = PLServer.Instance.AllPSIs;
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i]; if (p == null || p.MyCurrentSector == null || p.MyCurrentSector.ID != sector.ID) continue;
                    var inst = p.ShipInstance; string want = p.ShipName;
                    if (inst == null || inst.HasBeenDestroyed || string.IsNullOrEmpty(want) || Director.Names.ContainsKey(inst.ShipID)) continue;
                    Director.Names[inst.ShipID] = want;
                }
                Director.KeepNames();
            }
            catch (Exception e) { Plugin.L.LogWarning("Nomi delle navi di missione: " + e.Message); }
        }

        // Missioni nostre in corso al giro precedente: quando una non è più attiva, se ne legge l'esito.
        static readonly HashSet<int> watching = new();
        static void CheckOutcomes()
        {
            var now = Active().Select(mb => mb.MissionTypeID).Where(catalog.ContainsKey).ToHashSet();
            foreach (int id in watching.Where(x => !now.Contains(x)).ToList())
            {
                watching.Remove(id);
                PLMissionBase last = null; var all = PLServer.Instance.AllMissions;
                for (int i = 0; i < all.Count; i++) if (all[i] != null && all[i].MissionTypeID == id) last = all[i];
                bool success = last != null && last.m_ObjectivesAreCompleted && !last.Abandoned;
                string main = names.TryGetValue(id, out var nm) && nm.Length > 0 ? nm[0] : (last?.MyMissionData?.Name ?? id.ToString());
                runs.Remove(id);
                try { Saga.OnOutcome(id, catalog[id].family, catalog[id].tier, success, main); } catch (Exception e) { Plugin.L.LogWarning("Esito missione: " + e.Message); }
            }
            foreach (int id in now) watching.Add(id);
        }

        static float Pow(PLShipInfo ship) => Director.Power(ship, out _, out _);

        static PLShipInfoBase FindShip(string name)
        {
            try
            {
                foreach (var kv in PLEncounterManager.Instance.AllShips)
                {
                    var s = kv.Value; if (s == null || s.HasBeenDestroyed) continue;
                    if ((string)s.ShipName == name) return s;
                }
            }
            catch { }
            return null;
        }

        static bool Dead(PLShipInfoBase s) { if (s == null || s.HasBeenDestroyed) return true; try { return s.MyStats != null && s.MyStats.HullCurrent <= 0f; } catch { return false; } }

        static int SpawnWave(PLShipInfo ship, int tier, string name, int count, Run run, int modsExtra = 0)
        {
            int made = 0; float p = Pow(ship); run.LastWave = run.InSector;
            for (int i = 0; i < count; i++)
            {
                var type = (EShipType)RaiderShip[Math.Clamp(tier, 0, RaiderShip.Length - 1)];
                var s = Director.Spawn(ship, type, Director.Modifiers(p) | modsExtra, name);
                if (s != null) { run.Spawned.Add(s.ShipID); made++; }
            }
            return made;
        }

        static void Fail(PLMissionBase mb, Run run, string why)
        {
            if (run.Failed) return; run.Failed = true;
            Director.Notify("MISSIONE FALLITA", why);
            try { mb.FailMission(); } catch (Exception e) { Plugin.L.LogWarning("Fallimento missione: " + e.Message); }
        }

        static void OnArrive(PLMissionBase mb, int id, int family, int tier, PLShipInfo ship, Run run)
        {
            string[] nm = names.TryGetValue(id, out var x) ? x : Array.Empty<string>();
            switch (family)
            {
                case 2: Fire("mel" + id + "a", "Ricognizione: siamo sul posto. Scienza, impulso attivo dei sensori."); break;
                case 3: Fire("mel" + id + "a", $"Convoglio «{nm[0]}» in vista. Sta caricando la curvatura: restiamo in formazione."); break;
                case 4: Fire("mel" + id + "a", "Contatto visivo. Capitano, agganci la nave sconosciuta per la Scienza. Armi a riposo."); break;
                case 5: Fire("mel" + id + "a", $"Relitto della «{nm[0]}» in vista. Capitano, lo agganci per la scansione."); break;
                case 6:
                {
                    Fire("mel" + id + "a", "La fenditura si apre. Qualcosa ne sta uscendo.");
                    string me = (string)ship.ShipName; if (string.IsNullOrEmpty(me)) me = "nostra nave";
                    var echo = Director.Spawn(ship, ship.ShipTypeID, Director.Modifiers(Pow(ship) + 4) | 1 << (int)EShipModifierType.ELITE, "Eco della " + me);
                    if (echo != null) run.Spawned.Add(echo.ShipID);
                    Stations.Set(ship.ShipID, "scx_scan", 0.01f); Stations.Set(ship.ShipID, "dmx_sensors", Math.Min(90f, Stations.Get(ship.ShipID, "dmx_sensors") + 30f));
                    Director.Notify("ANOMALIA", "Dalla fenditura emerge una copia esatta della nostra nave, con i nostri stessi codici. È ostile.");
                    break;
                }
                case 7: Fire("mel" + id + "a", "In orbita. Le navette stanno attraccando: Ingegneria, tutta la potenza al supporto vitale."); break;
                case 8: run.Wave = 1; Director.Notify("RAPPORTO DI PLANCIA", $"Prima ondata in vista: {(nm.Length > 0 ? nm[0] : "Incursori")}."); break;
            }
        }

        static void Step(PLMissionBase mb, int id, int family, int tier, PLShipInfo ship, Run run)
        {
            string[] nm = names.TryGetValue(id, out var x) ? x : Array.Empty<string>();
            int sid = ship.ShipID; string s = "mel" + id;
            switch (family)
            {
                case 2: // ricognizione
                    if (fired.Contains(s + "a") && Stations.Get(sid, "scx_ping") > 0 && Fire(s + "b", "Anomalia analizzata: dati trasmessi al Comando."))
                    { Service.Add("scienza.anomalie", 1); Stations.Initiative(sid, Stations.Cfg.Captain.GainPerSuccess * 2); }
                    break;
                case 3: // scorta
                {
                    var civ = FindShip(nm[0]); int secs = 90 + tier * 30;
                    // la nave compare qualche secondo dopo l'arrivo: è persa solo se l'abbiamo vista e poi è caduta
                    if (civ != null && !Dead(civ)) run.Seen = true;
                    if (run.Seen && Dead(civ)) { Fail(mb, run, $"Il convoglio «{nm[0]}» è stato distrutto."); break; }
                    if (!run.Seen) { if (run.InSector > 45) Fail(mb, run, $"Del convoglio «{nm[0]}» restano solo i rottami: siamo arrivati tardi."); break; }
                    if (run.InSector == 15 || (run.Wave == 1 && run.InSector == 15 + secs / 2))
                    {
                        int n = SpawnWave(ship, tier, nm[1], 1 + tier / 2 + run.Wave, run); run.Wave++;
                        if (n > 0) Director.Notify("ALLARME DI PROSSIMITÀ", $"{n} navi di {nm[1].ToLowerInvariant()} puntano sul convoglio.");
                    }
                    if (run.InSector >= secs + 5 && Fire(s + "b", $"Il convoglio «{nm[0]}» è saltato in curvatura. Ci ringraziano."))
                        Stations.Initiative(sid, Stations.Cfg.Captain.GainPerSuccess);
                    break;
                }
                case 4: // primo contatto
                {
                    var alien = FindShip(nm[0]);
                    if (alien != null && !Dead(alien)) run.Seen = true;
                    if (!run.Seen) { if (run.InSector > 45) Fail(mb, run, "Delle coordinate restano solo tracce ioniche: il vascello se n'è andato."); break; }
                    if (alien == null || Dead(alien)) { Fail(mb, run, "La nave sconosciuta è stata distrutta. Il primo contatto è perduto per sempre."); break; }
                    try { if (alien.MyStats.HullCurrent < alien.MyStats.HullMax * 0.9f) { Fail(mb, run, "Abbiamo aperto il fuoco su di loro. Si ritirano e chiudono ogni canale."); break; } } catch { }
                    bool targeted = (int)Stations.Get(sid, "scx_tgt") == alien.ShipID + 1;
                    if (targeted && Stations.Get(sid, "scx_scan") >= 100f) Fire(s + "b", $"Scansione completa: struttura e biologia di {nm[0]} registrate.");
                    if (fired.Contains(s + "b"))
                    {
                        if ((int)Stations.Get(sid, "cpx_dir") == 4) run.Progress += 1f;
                        if (run.Progress >= 30f && Fire(s + "c", "Rispondono al nostro silenzio con un messaggio di pace. Primo contatto riuscito."))
                        { Service.Add("comando.primi.contatti", 1); Stations.Initiative(sid, Stations.Cfg.Captain.GainPerSuccess * 2); }
                    }
                    break;
                }
                case 5: // recupero
                {
                    var wreck = FindShip(nm[0]);
                    if (run.Stage == 0)
                    {
                        bool targeted = wreck != null && (int)Stations.Get(sid, "scx_tgt") == wreck.ShipID + 1;
                        bool done = targeted && Stations.Get(sid, "scx_scan") >= 100f;
                        if (wreck == null && run.InSector > 20) done = true; // relitto già distrutto: la scatola nera galleggia tra i rottami
                        if (done && Fire(s + "b", "Scatola nera recuperata. Contatti in avvicinamento: sciacalli."))
                        { run.Stage = 1; int n = SpawnWave(ship, tier, nm[1], 1 + (tier + 1) / 2, run); if (n == 0) run.Stage = 2; }
                    }
                    else if (run.Stage == 1 && run.InSector - run.LastWave > 12 && run.Spawned.All(i => Dead(Ship(i)))) run.Stage = 2;
                    if (run.Stage == 2) Fire(s + "c", "Sciacalli respinti. Il carico recuperato è nella stiva.");
                    break;
                }
                case 6: // eco temporale
                    if (run.Spawned.Count > 0 && run.InSector > 12 && run.Spawned.All(i => Dead(Ship(i))) && Fire(s + "b", "L'eco si dissolve. La fenditura si richiude: domani resta da scrivere."))
                        Service.Add("comando.paradossi", 1);
                    if (run.Spawned.Count == 0 && run.InSector > 5) Fire(s + "b", "La fenditura si richiude prima che qualcosa ne esca.");
                    break;
                case 7: // evacuazione
                {
                    int secs = 60 + tier * 15;
                    run.Hazard += 1f;
                    if (run.Hazard >= 15f) { run.Hazard = 0; string k = "dmx_life"; Stations.Set(sid, k, Math.Min(90f, Stations.Get(sid, k) + 6f + tier * 2f)); }
                    bool ok = Stations.Get(sid, "eps_life") >= 2f && Stations.Get(sid, "dmx_life") <= 30f;
                    if (ok) run.Progress += 1f;
                    if (run.Progress >= secs && Fire(s + "b", "Tutti i coloni sono a bordo e respirano. Evacuazione completata."))
                    { Service.Add("ingegneria.evacuazioni", 1); Stations.Initiative(sid, Stations.Cfg.Captain.GainPerSuccess * 2); }
                    break;
                }
                case 8: // interdizione: nuova ondata quando la precedente è caduta
                {
                    if (run.Wave < 1) break;
                    // le navi appena create compaiono nell'elenco con un po' di ritardo: si attende prima di dichiarare libero il settore
                    bool clear = run.InSector - run.LastWave > 12 && FindShip(nm[0]) == null && run.Spawned.All(i => Dead(Ship(i)));
                    if (run.Wave >= 3) { if (clear && Fire(s + "b", "Ultima ondata respinta: il passaggio è aperto.")) Service.Add("armi.interdizioni", 1); break; }
                    if (clear && run.InSector > 12)
                    {
                        run.Spawned.Clear(); int per = 1 + Math.Min(2, tier / 2);
                        int n = SpawnWave(ship, tier, nm[0], per, run); run.Wave++;
                        Director.Notify("ALLARME DI PROSSIMITÀ", run.Wave == 3 ? $"Ultima ondata: {n} navi." : $"Seconda ondata: {n} navi.");
                    }
                    break;
                }
            }
        }

        static PLShipInfoBase Ship(int shipId) { try { return PLEncounterManager.Instance.GetShipFromID(shipId); } catch { return null; } }

        public static bool Fire(string script, string say)
        {
            if (!fired.Add(script)) return false;
            PLMissionObjective_Custom.OnCustomObjEvent(script);
            var me = Stations.MyShip; if (me != null) Stations.LogEvent(me.ShipID, say);
            return true;
        }

        // Settore della missione attiva (per le prove e per il pilota)
        public static PLSectorInfo SectorOf(int id)
        {
            try { foreach (var kv in PLGlobal.Instance.Galaxy.AllSectorInfos) if (kv.Value != null && kv.Value.MissionSpecificID == id) return kv.Value; } catch { }
            return null;
        }

        // L'equipaggio è nel settore di una missione in corso: il direttore non aggiunge altri eventi.
        public static bool BusyHere() { try { return Active().Any(mb => runs.TryGetValue(mb.MissionTypeID, out var r) && r.Arrived && PLServer.GetCurrentSector()?.MissionSpecificID == mb.MissionTypeID); } catch { return false; } }

        public static int ActiveId() => Active().Select(mb => mb.MissionTypeID).FirstOrDefault(catalog.ContainsKey);

        public static string Describe()
        {
            var act = Active().Where(mb => catalog.ContainsKey(mb.MissionTypeID)).Select(mb =>
            {
                int id = mb.MissionTypeID; runs.TryGetValue(id, out var r); var sec = SectorOf(id);
                string objs = mb.Objectives == null ? "-" : string.Join(",", Enumerable.Range(0, mb.Objectives.Count).Select(i => { var o = mb.Objectives[i]; return (o.IsCompleted ? "+" : "") + o.GetAmt() + "/" + o.GetAmtNeeded(); }));
                return $"{id} {Families[catalog[id].family]} obiettivi [{objs}] settore {(sec == null ? "-" : sec.ID.ToString())} arrivati {(r?.Arrived ?? false)} nel settore {r?.InSector ?? 0:0} s ondata {r?.Wave ?? 0} progresso {r?.Progress ?? 0:0}";
            });
            return $"catalogo {catalog.Count} ({(registered ? "registrato" : "in attesa")}), attive: {string.Join("; ", act.DefaultIfEmpty("nessuna"))}";
        }
    }
}

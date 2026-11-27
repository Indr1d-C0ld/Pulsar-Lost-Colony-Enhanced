// La mente dell'equipaggio. I bot del gioco scelgono da soli il compito in cima alla loro lista di priorità (riparare,
// spegnere incendi, respingere intrusi, presidiare la stazione, curarsi, rianimare...), ma ognuno decide per conto suo:
// finiscono tutti sullo stesso incendio, il pilota lascia il timone in piena battaglia, un ferito grave va a caccia di
// intrusi. Qui, sull'host, l'equipaggio ragiona come una squadra:
//  - valuta la situazione a bordo (incendi, intrusi, compagni a terra o feriti, sistemi e scafo, battaglia) e ne ricava
//    una gravità, da «calma» a «disperata»;
//  - per ogni emergenza decide quanti servono e chi è più adatto (ruolo, distanza, salute, competenza, carattere), e
//    alza quel compito solo per chi è stato scelto, e lo abbassa per gli altri quando è già coperto;
//  - chi è ferito si ritira a curarsi, a meno che la situazione sia disperata; chi ha un posto di combattimento lo tiene;
//  - gli interventi riusciti fanno crescere le competenze personali degli ufficiali (antincendio, riparazioni, difesa,
//    soccorso) e i bot spendono i punti talento del gioco secondo ruolo e carattere, come farebbe una persona;
//  - nei momenti che contano parlano: chi va dove, cosa è stato risolto.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace Melchior
{
    // Ordini del capitano all'equipaggio (salvati nella cronaca della galassia).
    public class CrewOrders
    {
        public string doctrine = "equilibrio", boarding = "armi"; public int retreat = 40, given;
        public Dictionary<string, string> stance = new(), focus = new(); public List<string> used = new();
        public string Stance(int cls) => stance.TryGetValue(cls.ToString(), out var v) ? v : "auto";
        public string Focus(int cls) => focus.TryGetValue(cls.ToString(), out var v) ? v : "";
    }

    public static class CrewMind
    {
        public static CrewOrders Orders => Saga.State.crewOrders ??= new CrewOrders();
        public static readonly string[] Doctrines = { "equilibrio", "combattimento", "difesa", "danni", "scorta", "riposo" };
        public static string DoctrineName(string d) => d switch { "combattimento" => "posti di combattimento", "difesa" => "difesa interna", "danni" => "controllo danni", "scorta" => "scorta al capitano", "riposo" => "riposo", _ => "equilibrio" };
        public static readonly string[] Stances = { "auto", "posto", "scorta", "riserva", "libero" };
        public static string StanceName(string s) => s switch { "posto" => "al suo posto", "scorta" => "scorta", "riserva" => "riserva", "libero" => "libero", _ => "a giudizio" };
        public static readonly string[] Focuses = { "", "antincendio", "difesa", "riparazioni", "soccorso" };
        public static bool Enabled = true;
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }

        // indici delle priorità del gioco (EAIPriorityListDisplayed)
        const int REPAIR = 0, FIRE = 1, REPEL = 2, STATION = 3, TURRET = 4, ESCORT = 5, BOARD = 6, STARTUP = 10, HEAL_SELF = 11, REPAIR_MATES = 63, REVIVE = 64, AT_EASE = 65, HEAL_CREW = 66;
        static readonly Dictionary<int, string> TaskName = new()
        {
            [REPAIR] = "riparazioni", [FIRE] = "antincendio", [REPEL] = "difesa", [STATION] = "stazione", [TURRET] = "torretta", [ESCORT] = "scorta",
            [STARTUP] = "riavvio", [HEAL_SELF] = "cure", [AT_EASE] = "riposo", [REPAIR_MATES] = "soccorso", [REVIVE] = "soccorso", [HEAL_CREW] = "soccorso",
        };
        public static readonly string[] Gravity = { "calma", "attenzione", "pericolo", "crisi", "disperata" };

        // ---- la situazione a bordo
        sealed class Situation
        {
            public int fires, invaders, downed, wounded, gravity; public float hull = 1f, worstSys; public bool combat, power = true, captainAway;
            public readonly List<Vector3> firePos = new(), invaderPos = new(), downedPos = new(), woundedPos = new();
            public Vector3 captainPos;
        }
        static Situation sit = new();
        public static int GravityNow => sit.gravity; static int NativeFires;

        sealed class Member
        {
            public PLPlayer p; public int id, cls; public float hp = 1f; public Vector3 pos; public Officer off;
            public int task = -1; public float since; public string why = "", stance = "auto", focus = "";
        }
        static readonly Dictionary<int, Member> crew = new();   // id del giocatore -> membro
        static readonly Dictionary<int, int> lastTask = new();  // per l'isteresi e le comunicazioni
        static readonly Dictionary<int, bool> hurtNow = new();
        static readonly HashSet<int> covered = new(); // emergenze con abbastanza persone assegnate
        static float nextAssess, nextBark; static readonly Dictionary<int, float> barkAt = new();
        public static long PreSorts, Missing, Holds, Boosts, Assigns, Retreats, Thinks; static int lastFires, lastInvaders, lastDowned; static float lastWorst;

        static int MyTeam() { try { return Stations.MyShip?.TeamID ?? 0; } catch { return 0; } }
        static bool OurBot(PLPlayer p)
        {
            try { var me = Stations.MyShip; return p != null && p.IsBot && me != null && p.MyPawn != null && p.StartingShip != null && p.StartingShip.ShipID == me.ShipID; } catch { return false; }
        }

        // ---- valutazione, due volte al secondo
        public static void Tick()
        {
            if (!Enabled || !Host || !Saga.Loaded || Stations.MyShip == null) return;
            float now = Time.time; if (now < nextAssess) return; nextAssess = now + 0.5f;
            try { Assess(); Assign(); Learn(); }
            catch (Exception e) { Plugin.L.LogWarning("Mente dell'equipaggio: " + e.Message); nextAssess = now + 5f; }
        }

        static void Assess()
        {
            var me = Stations.MyShip; var s = new Situation(); int team = MyTeam();
            try { foreach (var kv in me.AllFires) { var f = kv.Value; if (f == null) continue; s.fires++; s.firePos.Add(f.transform.position); } } catch { }
            try { NativeFires = me.CountNonNullFires(); } catch { }
            int sub = Boarding.SubHubOf(me);
            crew.Clear();
            try
            {
                var all = PLServer.Instance.AllPlayers;
                for (int i = 0; i < all.Count; i++)
                {
                    var p = all[i]; if (p == null) continue; var pw = p.MyPawn; if (pw == null) continue;
                    int pteam = -1; try { pteam = pw.GetTeamID(); } catch { }
                    bool aboard = sub >= 0 && (p.SubHubID & 0x7FFFFFFF) == sub;
                    if (pteam != team)
                    {
                        if (aboard && !pw.IsDead) { s.invaders++; s.invaderPos.Add(pw.transform.position); }
                        continue;
                    }
                    if (pw.IsDead) { if (aboard) { s.downed++; s.downedPos.Add(pw.transform.position); } continue; }
                    float hp = pw.MaxHealth > 0 ? pw.Health / pw.MaxHealth : 1f;
                    if (hp < 0.5f && aboard) { s.wounded++; s.woundedPos.Add(pw.transform.position); }
                    if (!p.IsBot && p.GetClassID() == 0 && !aboard) { s.captainAway = true; s.captainPos = pw.transform.position; }
                    if (OurBot(p))
                    {
                        var m = new Member { p = p, id = p.GetPlayerID(), cls = p.GetClassID(), hp = hp, pos = pw.transform.position, off = Officers.Of(p.GetClassID()) };
                        crew[m.id] = m;
                        // le ferite si ricordano: chi viene colpito spesso cercherà corazza e resistenza
                        bool low = hp < 0.35f; hurtNow.TryGetValue(m.id, out var was);
                        if (low && !was && m.off != null) { m.off.skills ??= new(); m.off.skills["ferite"] = (m.off.skills.TryGetValue("ferite", out var f) ? f : 0) + 1; Saga.MarkDirty(); }
                        hurtNow[m.id] = low;
                    }
                }
            }
            catch { }
            try { var st = me.MyStats; s.hull = st.HullMax > 0 ? st.HullCurrent / st.HullMax : 1f; } catch { }
            try { s.worstSys = Stations.SysIds.Max(x => Stations.Get(me.ShipID, "dmx_" + x)); } catch { }
            try { var t = me.TargetShip; s.combat = me.m_AlertLevel >= 2 || (t != null && !t.HasBeenDestroyed && t.HostileShips.Contains(me.ShipID)); } catch { }
            try { s.power = !me.IsReactorInMeltdown(); } catch { }
            // gravità: quello che un equipaggio umano sentirebbe sulla pelle
            int g = 0;
            if (s.combat) g = 1;
            if (s.fires > 0 || s.worstSys > 40f || s.wounded > 0) g = Math.Max(g, 1);
            if (s.fires >= 3 || s.invaders > 0 || s.downed > 0 || s.hull < 0.5f || s.worstSys > 75f || !s.power) g = Math.Max(g, 2);
            if (s.fires >= 6 || s.invaders >= 3 || s.hull < 0.25f || (s.invaders > 0 && s.combat) || s.downed >= 2) g = Math.Max(g, 3);
            if (s.hull < 0.12f || s.invaders >= 5 || (s.fires >= 6 && s.invaders > 0) || (s.downed >= 2 && s.invaders > 0)) g = 4;
            s.gravity = g; sit = s;
        }

        // ---- chi fa cosa
        static float Affinity(int cls, int task) => task switch
        {
            FIRE => cls switch { 4 => 3f, 2 => 2f, 3 => 1.4f, 0 => 1.2f, _ => 0.6f },
            REPEL => cls switch { 3 => 3f, 0 => 2.2f, 2 => 1.2f, 4 => 1f, _ => 0.6f },
            REPAIR => cls switch { 4 => 3f, 2 => 1.4f, 3 => 1.1f, 0 => 1f, _ => 0.7f },
            REVIVE or HEAL_CREW => cls switch { 2 => 3f, 0 => 1.6f, 3 => 1.1f, 4 => 1f, _ => 0.7f },
            STARTUP => cls == 4 ? 3f : 1f,
            ESCORT => cls switch { 3 => 3f, 2 => 1.5f, _ => 1f },
            _ => 1f,
        };
        static string Area(int task) => task switch { FIRE => "antincendio", REPEL => "difesa", REPAIR or STARTUP => "riparazioni", REVIVE or HEAL_CREW or REPAIR_MATES => "soccorso", ESCORT => "difesa", _ => "" };
        static float TraitBias(Officer o, int task)
        {
            if (o == null) return 1f;
            return o.trait switch
            {
                "impulsivo" => task == REPEL ? 1.35f : 1f,
                "veterano" => task is REPEL or FIRE ? 1.2f : 1.05f,
                "meticoloso" => task is REPAIR or STARTUP ? 1.25f : 1f,
                "freddo" => sit.gravity >= 3 ? 1.2f : 1f,
                _ => 1f,
            };
        }
        static float Near(Vector3 from, List<Vector3> pts) { if (pts.Count == 0) return 1f; float d = pts.Min(p => (p - from).magnitude); return 1f / (1f + d / 18f); }

        static void Assign()
        {
            var s = sit; var O = Orders; string doc = O.doctrine; float retreat = O.retreat / 100f;
            foreach (var m in crew.Values) { m.task = -1; m.stance = O.Stance(m.cls); m.focus = O.Focus(m.cls); }
            // chi è «libero» decide da sé, con l'IA del gioco; chi è di scorta o al suo posto ha già il suo compito
            var free = crew.Values.Where(m => m.stance != "libero").ToList();
            foreach (var m in free)
            {
                if (m.hp < retreat && s.gravity < 4 && m.stance is "posto" or "scorta") { m.task = HEAL_SELF; m.why = "cure"; continue; }
                if (m.stance == "scorta" && !(s.invaders > 0 && s.gravity >= 3)) { m.task = ESCORT; m.why = "scorta"; }
                else if (m.stance == "posto") { m.task = STATION; m.why = "stazione"; }
            }
            int eligible = free.Count(m => m.task < 0 && m.cls != 1);
            var jobs = new List<(int task, int need, float urgency, List<Vector3> where)>();
            if (!s.power) jobs.Add((STARTUP, 1, 9f, null));
            if (s.invaders > 0) jobs.Add((REPEL, doc == "difesa" ? Math.Max(1, eligible) : Math.Min(free.Count, s.invaders + 1 + (s.gravity >= 3 ? 1 : 0)), 6f + s.invaders, s.invaderPos));
            if (s.downed > 0) jobs.Add((REVIVE, Math.Min(2, s.downed), 7f, s.downedPos));
            if (s.fires > 0) jobs.Add((FIRE, doc == "danni" ? Math.Max(1, eligible) : Math.Min(3, (s.fires + 1) / 2 + (s.fires >= 6 ? 1 : 0)), 4f + s.fires * 0.6f, s.firePos));
            if (doc == "danni" ? s.worstSys > 5f || s.hull < 0.95f : s.worstSys > 25f || s.hull < 0.6f) jobs.Add((REPAIR, doc == "danni" ? Math.Max(1, eligible) : s.worstSys > 70f || s.hull < 0.3f ? 2 : 1, 3f + s.worstSys / 25f, null));
            if (s.wounded > 0 && s.invaders == 0) jobs.Add((HEAL_CREW, 1, 2.5f, s.woundedPos));
            if (doc == "scorta" && s.gravity < 3) jobs.Add((ESCORT, 2, 5f, s.captainAway ? new List<Vector3> { s.captainPos } : null));
            else if (s.captainAway && !s.combat && s.gravity <= 1) jobs.Add((ESCORT, 1, 1.5f, new List<Vector3> { s.captainPos }));
            if (doc == "riposo") jobs.RemoveAll(j => j.task is REPAIR or HEAL_CREW && s.gravity < 2);
            covered.Clear();
            foreach (var job in jobs.OrderByDescending(j => j.urgency))
            {
                int got = 0;
                for (int k = 0; k < job.need; k++)
                {
                    Member best = null; float bestScore = 0f;
                    foreach (var m in free)
                    {
                        if (m.task >= 0) continue;
                        float sc = Affinity(m.cls, job.task) * TraitBias(m.off, job.task) * (1f + 0.08f * Level(m.off, Area(job.task))) * (m.focus.Length > 0 && m.focus == Area(job.task) ? 1.8f : 1f);
                        if (m.stance == "riserva" && s.gravity < 3) sc = 0f; // la riserva entra in gioco solo quando la situazione si fa seria
                        if (doc == "combattimento" && m.cls is not (2 or 4) && job.task is FIRE or REPAIR && s.gravity < 4) sc = 0f;
                        if (job.where != null) sc *= 0.5f + Near(m.pos, job.where);
                        // in battaglia il timone e le armi non si lasciano per un incendio o una riparazione
                        if (s.combat && m.cls == 1 && job.task != STARTUP && s.gravity < 4) sc = 0f;
                        if (s.combat && m.cls == 3 && job.task is FIRE or REPAIR && s.gravity < 3) sc *= 0.4f;
                        // un ferito non va a cercare intrusi
                        if (m.hp < 0.35f && job.task is REPEL) sc *= s.gravity >= 4 ? 0.6f : 0.15f;
                        if (lastTask.TryGetValue(m.id, out var lt) && lt == job.task) sc *= 1.4f; // non si cambia idea a ogni respiro
                        if (sc > bestScore) { bestScore = sc; best = m; }
                    }
                    if (best == null) break;
                    best.task = job.task; best.why = TaskName[job.task]; got++;
                }
                if (got >= job.need) covered.Add(job.task);
            }
            // chi resta: si cura se ferito, altrimenti in battaglia al suo posto
            foreach (var m in free.Where(m => m.task < 0))
            {
                if (m.hp < retreat && s.gravity < 4) { m.task = HEAL_SELF; m.why = "cure"; }
                else if ((s.combat || doc == "combattimento") && m.cls != 0) { m.task = STATION; m.why = "stazione"; }
                else if (doc == "riposo") { m.task = m.hp < 0.95f ? HEAL_SELF : AT_EASE; m.why = m.hp < 0.95f ? "cure" : "riposo"; }
            }
            // un ferito grave lascia anche un incarico, se non è disperata
            foreach (var m in free.Where(m => m.task is REPEL or FIRE && m.hp < retreat * 0.6f && s.gravity < 4)) { m.task = HEAL_SELF; m.why = "cure"; Retreats++; }
            float now = Time.time;
            foreach (var m in free)
            {
                lastTask.TryGetValue(m.id, out var before);
                if (m.task >= 0 && m.task != before) { Assigns++; Bark(m, m.task, before); }
                lastTask[m.id] = m.task;
            }
        }

        // ---- l'aggancio al calcolo del gioco
        // Il percorso usato davvero dall'equipaggio: la lista di priorità del giocatore-bot (valori interi piccoli,
        // di solito da 1 a 5), ricalcolata e poi ordinata. Subito prima dell'ordinamento si alza il compito assegnato
        // sopra gli altri, e si abbassano le emergenze già coperte da altri compagni.
        internal static void OnPreSort(PLPlayer p)
        {
            if (!Enabled || !Host) return;
            PreSorts++;
            try
            {
                if (!crew.TryGetValue(p.GetPlayerID(), out var m) || m.stance == "libero") return;
                var list = p.GetAIData()?.Priorities; if (list == null) return;
                // chi può andare all'abbordaggio: solo armi e comando, tutti (tranne il pilota), nessuno
                string br = Orders.boarding; bool mayBoard = br == "tutti" ? m.cls != 1 : br == "armi" ? m.cls is 3 or 0 : false;
                AIPriority mine = null; int top = 0;
                // «al proprio posto» vuol dire cose diverse: il pilota pilota, l'ufficiale alle armi sta alla torretta
                int task = m.task == STATION ? m.cls switch { 1 => 32, 3 => TURRET, _ => STATION } : m.task;
                for (int i = 0; i < list.Count; i++)
                {
                    var pr = list[i]; if (pr == null) continue;
                    bool main = pr.Type == AIPriorityType.E_MAIN || pr.Type == AIPriorityType.E_CLASS_MAIN;
                    // un'emergenza già coperta da chi è stato scelto non richiama tutti gli altri
                    if (main && pr.TypeData != task && covered.Contains(pr.TypeData) && pr.LastCalculated_PriorityValue > 1) { pr.LastCalculated_PriorityValue = 1; Holds++; }
                    if (main && pr.TypeData == BOARD && !mayBoard && pr.LastCalculated_PriorityValue > 0) { pr.LastCalculated_PriorityValue = 0; Holds++; }
                    if (pr.LastCalculated_PriorityValue > top) top = pr.LastCalculated_PriorityValue;
                    if (main && pr.TypeData == task) mine = pr;
                }
                if (m.task < 0) return;
                if (mine == null) { Missing++; return; }
                int want = top + 2 + sit.gravity;
                if (mine.LastCalculated_PriorityValue < want) { mine.LastCalculated_PriorityValue = want; Boosts++; }
            }
            catch { }
        }
        // in emergenza si pensa più in fretta, e chi ha esperienza ancora di più
        internal static void OnPriorities(PLPlayer p)
        {
            if (!Enabled || !Host || sit.gravity == 0) return;
            try
            {
                if (!crew.TryGetValue(p.GetPlayerID(), out var m)) return;
                float now = Time.time; if (p.LastTickAITime < now) return;
                float lv = m.off == null ? 0 : (Level(m.off, "antincendio") + Level(m.off, "difesa") + Level(m.off, "riparazioni") + Level(m.off, "soccorso")) / 4f;
                float think = Math.Max(0.3f, 1f - 0.15f * sit.gravity - 0.03f * lv);
                p.LastTickAITime = now - 1f + think; Thinks++;
            }
            catch { }
        }
        // ---- esperienza: quando le cose migliorano, chi ci stava lavorando impara
        static void Learn()
        {
            var s = sit;
            void Credit(int task, string area, int xp, string done)
            {
                var who = crew.Values.Where(m => m.task == task).ToList(); if (who.Count == 0) return;
                foreach (var m in who) Gain(m, area, xp);
                Feats.CrewDeed(area);
                var first = who[0]; if (done.Length > 0 && Time.time > nextBark - 4f) Say(first, done, true);
            }
            if (s.fires < lastFires) Credit(FIRE, "antincendio", 3 * (lastFires - s.fires), s.fires == 0 ? Pick("Incendio domato.", "Fuoco spento, si respira.", "Fiamme sotto controllo.") : "");
            if (s.invaders < lastInvaders) Credit(REPEL, "difesa", 5 * (lastInvaders - s.invaders), s.invaders == 0 ? Pick("Intrusi neutralizzati.", "Ponte sgombro.", "Non ne è rimasto in piedi nessuno.") : Pick("Uno in meno.", "Abbattuto!"));
            if (s.downed < lastDowned && lastDowned > 0) { Credit(REVIVE, "soccorso", 6, Pick("Respira di nuovo, rimettiti in piedi.", "Ce l'ho fatta, è tornato tra noi.", "Rianimato.")); Feats.CrewRevive(); }
            if (s.worstSys < lastWorst - 12f) Credit(REPAIR, "riparazioni", 2, s.worstSys < 10f ? Pick("Sistemi in ordine.", "Riparazione completata.") : "");
            lastFires = s.fires; lastInvaders = s.invaders; lastDowned = s.downed; lastWorst = s.worstSys;
        }
        static readonly int[] LevelAt = { 0, 10, 25, 45, 70, 100, 140, 190, 250, 320, 400 };
        public static int Level(Officer o, string area) { if (o?.skills == null || area.Length == 0 || !o.skills.TryGetValue(area, out var xp)) return 0; int l = 0; while (l < 10 && xp >= LevelAt[l + 1]) l++; return l; }
        static void Gain(Member m, string area, int xp)
        {
            var o = m.off; if (o == null || area.Length == 0) return;
            o.skills ??= new Dictionary<string, int>(); int before = Level(o, area);
            o.skills[area] = (o.skills.TryGetValue(area, out var v) ? v : 0) + xp; Saga.MarkDirty();
            Officers.Credit(m.cls, Math.Max(1, xp / 3));
            int after = Level(o, area);
            if (after > before)
            {
                Director.Notify("EQUIPAGGIO", $"{o.first} {o.last} cresce: {area} al livello {after}.");
                // ogni livello di esperienza vissuta vale un punto talento, che il bot spenderà da sé
                if (GrantPoint(m.p)) nextTalent = Math.Min(nextTalent, Time.time + 3f);
                Service.Add("equipaggio.competenze", 1);
                if (after >= 10) Feats.CrewMaster();
            }
        }

        // ---- talenti del gioco: i bot li spendono come farebbe una persona del loro ruolo e carattere
        static readonly Dictionary<int, ETalents[]> Plan = new()
        {
            [0] = new[] { ETalents.HEALTH_BOOST, ETalents.CAP_ARMOR_BOOST, ETalents.CAP_CREW_SPEED_BOOST, ETalents.ARMOR_BOOST, ETalents.INC_STAMINA, ETalents.HEALTH_BOOST_2, ETalents.CAP_SCREEN_DEFENSE },
            [1] = new[] { ETalents.PIL_REDUCE_HULL_DAMAGE, ETalents.HEALTH_BOOST, ETalents.PIL_SHIP_TURNING, ETalents.PIL_SHIP_SPEED, ETalents.PIL_REDUCE_SYS_DAMAGE, ETalents.ARMOR_BOOST, ETalents.PIL_KEEN_EYES },
            [2] = new[] { ETalents.SCI_HEAL_NEARBY, ETalents.HEALTH_BOOST, ETalents.INC_HEALING_RATE, ETalents.SCI_SENSOR_BOOST, ETalents.ARMOR_BOOST, ETalents.SCI_FREQ_AMPLIFIER, ETalents.HEALTH_BOOST_2 },
            [3] = new[] { ETalents.PISTOL_DMG_BOOST, ETalents.HEALTH_BOOST, ETalents.WPNS_REDUCE_PAWN_DMG, ETalents.WPNS_TURRET_BOOST, ETalents.ARMOR_BOOST, ETalents.WPNS_RELOAD_SPEED, ETalents.ARMOR_BOOST_2 },
            [4] = new[] { ETalents.ENG_FIRE_REDUCTION, ETalents.HEALTH_BOOST, ETalents.ENG_REPAIR_DRONES, ETalents.ENG_COREPOWERBOOST, ETalents.ARMOR_BOOST, ETalents.ENG_CORECOOLINGBOOST, ETalents.HEALTH_BOOST_2 },
        };
        static readonly Dictionary<ETalents, string> TalentIt = new()
        {
            [ETalents.HEALTH_BOOST] = "resistenza", [ETalents.HEALTH_BOOST_2] = "resistenza avanzata", [ETalents.ARMOR_BOOST] = "corazza", [ETalents.ARMOR_BOOST_2] = "corazza avanzata",
            [ETalents.CAP_ARMOR_BOOST] = "corazza del comandante", [ETalents.CAP_CREW_SPEED_BOOST] = "passo svelto dell'equipaggio", [ETalents.INC_STAMINA] = "fiato lungo", [ETalents.CAP_SCREEN_DEFENSE] = "difesa dei terminali",
            [ETalents.PIL_REDUCE_HULL_DAMAGE] = "manovre per proteggere lo scafo", [ETalents.PIL_SHIP_TURNING] = "virate strette", [ETalents.PIL_SHIP_SPEED] = "spinta extra", [ETalents.PIL_REDUCE_SYS_DAMAGE] = "manovre per proteggere i sistemi", [ETalents.PIL_KEEN_EYES] = "occhio di falco",
            [ETalents.SCI_HEAL_NEARBY] = "primo soccorso", [ETalents.INC_HEALING_RATE] = "guarigione rapida", [ETalents.SCI_SENSOR_BOOST] = "sensori potenziati", [ETalents.SCI_FREQ_AMPLIFIER] = "amplificatore di frequenza",
            [ETalents.PISTOL_DMG_BOOST] = "tiro con la pistola", [ETalents.WPNS_REDUCE_PAWN_DMG] = "combattimento ravvicinato", [ETalents.WPNS_TURRET_BOOST] = "torrette potenziate", [ETalents.WPNS_RELOAD_SPEED] = "ricarica rapida",
            [ETalents.ENG_FIRE_REDUCTION] = "prevenzione incendi", [ETalents.ENG_REPAIR_DRONES] = "droni di riparazione", [ETalents.ENG_COREPOWERBOOST] = "nucleo potenziato", [ETalents.ENG_CORECOOLINGBOOST] = "raffreddamento del nucleo",
        };
        // Ogni dote candidata riceve un punteggio: la tabella del ruolo dà la base, poi contano ciò che il bot ha vissuto,
        // ciò che manca alla squadra e il carattere. Ne esce una scelta motivata, diversa per ogni ufficiale.
        static IEnumerable<(ETalents t, string why)> Choices(Member m, ETalents[] plan)
        {
            var o = m.off; int X(string k) => o?.skills != null && o.skills.TryGetValue(k, out var v) ? v : 0;
            var pool = new Dictionary<ETalents, (float score, string why, float whyScore)>();
            // la ragione raccontata è la più forte fra quelle vissute; il mestiere resta la base
            void Add(ETalents t, float sc, string why)
            {
                if (sc <= 0f) return;
                if (!pool.TryGetValue(t, out var cur)) { pool[t] = (sc, why, why.Length > 0 ? sc : 0f); return; }
                bool better = why.Length > 0 && sc > cur.whyScore; pool[t] = (cur.score + sc, better ? why : cur.why, better ? sc : cur.whyScore);
            }
            for (int i = 0; i < plan.Length; i++) Add(plan[i], 10f - i * 1.2f, "");
            foreach (var k in pool.Keys.ToList()) if (pool[k].why.Length == 0) pool[k] = (pool[k].score, "come vuole il suo mestiere", 0.01f);
            // esperienza vissuta
            Add(ETalents.ENG_FIRE_REDUCTION, X("antincendio") * 0.25f, "dopo tanti incendi spenti a bordo");
            Add(ETalents.ENG_REPAIR_DRONES, X("riparazioni") * 0.2f, "stanco di saldare a mano i sistemi");
            Add(ETalents.PISTOL_DMG_BOOST, X("difesa") * 0.18f, "dopo gli scontri con gli intrusi");
            Add(ETalents.WPNS_REDUCE_PAWN_DMG, X("difesa") * 0.15f, "per reggere meglio gli abbordaggi");
            Add(ETalents.SCI_HEAL_NEARBY, X("soccorso") * 0.25f, "dopo aver soccorso tanti compagni");
            Add(ETalents.INC_HEALING_RATE, X("soccorso") * 0.15f, "per rimettere in piedi la squadra più in fretta");
            Add(ETalents.HEALTH_BOOST, X("ferite") * 2.5f, "dopo essere finito più volte a terra");
            Add(ETalents.ARMOR_BOOST, X("ferite") * 2f, "dopo troppe ferite");
            // ciò che manca alla squadra: se nessuno sa curare, la Scienza ci pensa per prima
            bool medic = crew.Values.Any(x => x.id != m.id && Rank(x.p, ETalents.SCI_HEAL_NEARBY) > 0);
            if (!medic && m.cls == 2) Add(ETalents.SCI_HEAL_NEARBY, 6f, "perché a bordo nessuno sa ancora curare");
            bool fireman = crew.Values.Any(x => x.id != m.id && Rank(x.p, ETalents.ENG_FIRE_REDUCTION) > 0);
            if (!fireman && m.cls == 4 && X("antincendio") > 0) Add(ETalents.ENG_FIRE_REDUCTION, 4f, "perché la nave prende fuoco troppo spesso");
            // la specializzazione chiesta dal capitano
            switch (Orders.Focus(m.cls))
            {
                case "antincendio": Add(ETalents.ENG_FIRE_REDUCTION, 7f, "come gli ha chiesto il capitano"); Add(ETalents.HEALTH_BOOST, 2f, "come gli ha chiesto il capitano"); break;
                case "difesa": Add(ETalents.PISTOL_DMG_BOOST, 7f, "come gli ha chiesto il capitano"); Add(ETalents.ARMOR_BOOST, 5f, "come gli ha chiesto il capitano"); break;
                case "riparazioni": Add(ETalents.ENG_REPAIR_DRONES, 7f, "come gli ha chiesto il capitano"); break;
                case "soccorso": Add(ETalents.SCI_HEAL_NEARBY, 7f, "come gli ha chiesto il capitano"); Add(ETalents.INC_HEALING_RATE, 5f, "come gli ha chiesto il capitano"); break;
            }
            // carattere
            switch (o?.trait)
            {
                case "impulsivo": Add(ETalents.PISTOL_DMG_BOOST, 5f, "da testa calda qual è"); break;
                case "veterano": Add(ETalents.ARMOR_BOOST, 4f, "da vecchio soldato"); break;
                case "meticoloso": if (plan.Length > 0) Add(plan[0], 3f, "da perfezionista del suo mestiere"); break;
                case "freddo": Add(ETalents.WPNS_REDUCE_PAWN_DMG, 3f, "per restare lucido sotto il fuoco"); break;
                case "ambizioso": Add(ETalents.HEALTH_BOOST_2, 2f, "puntando in alto"); break;
            }
            // un pizzico di gusto personale: due ufficiali dello stesso ruolo non scelgono identico
            int seed = (o?.first ?? "").GetHashCode() ^ (o?.last ?? "").GetHashCode();
            foreach (var k in pool.Keys.ToList()) if (pool[k].why.Length == 0) pool[k] = (pool[k].score, "per gusto personale", 0f);
            return pool.Select(kv => (kv.Key, kv.Value.score + (float)new System.Random(seed ^ (int)kv.Key).NextDouble() * 2.5f, kv.Value.why))
                       .OrderByDescending(x => x.Item2).Select(x => (x.Key, x.why));
        }
        static IntPtr FieldAt(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj, string name)
        {
            var cls = IL2CPP.il2cpp_object_get_class(obj.Pointer); var f = IL2CPP.il2cpp_class_get_field_from_name(cls, name);
            return f == IntPtr.Zero ? IntPtr.Zero : obj.Pointer + (int)IL2CPP.il2cpp_field_get_offset(f);
        }
        public static int Points(PLPlayer p) { var a = FieldAt(p, "TalentPointsAvailable"); return a == IntPtr.Zero ? 0 : Obscured.Int(a); }
        // ObscuredInt: chiave a +0, valore cifrato a +4, inizializzato a +8, esca a +0xC (vedi Factions.SetNative)
        public static bool GrantPoint(PLPlayer p, int n = 1)
        {
            var a = FieldAt(p, "TalentPointsAvailable"); if (a == IntPtr.Zero) return false;
            int v = Obscured.Int(a) + n, key = System.Runtime.InteropServices.Marshal.ReadInt32(a, 0);
            System.Runtime.InteropServices.Marshal.WriteInt32(a, 4, v ^ key); System.Runtime.InteropServices.Marshal.WriteByte(a, 8, 1);
            System.Runtime.InteropServices.Marshal.WriteInt32(a, 0xC, v); return true;
        }
        public static int Rank(PLPlayer p, ETalents t) { try { var arr = p.Talents; int i = (int)t; if (arr == null || i >= arr.Length) return 0; return Obscured.Int(arr.Pointer + 0x20 + i * 0x14); } catch { return 0; } }
        static float nextTalent; public static int TalentsSpent;
        public static void TalentTick()
        {
            if (!Enabled || !Host || Time.time < nextTalent) return; nextTalent = Time.time + 20f;
            foreach (var m in crew.Values)
            {
                try
                {
                    if (Points(m.p) <= 0 || !Plan.TryGetValue(m.cls, out var plan)) continue;
                    foreach (var (t, why) in Choices(m, plan))
                    {
                        int before = Rank(m.p, t); m.p.ServerRankTalent((int)t);
                        if (Rank(m.p, t) > before)
                        {
                            TalentsSpent++; Service.Add("equipaggio.talenti", 1);
                            string who = m.off != null ? $"{m.off.first} {m.off.last}" : (string)m.p.GetPlayerName();
                            Stations.LogEvent(Stations.MyShip.ShipID, $"{who} affina una dote: {(TalentIt.TryGetValue(t, out var n) ? n : t.ToString().ToLowerInvariant())} (grado {before + 1}){(why.Length > 0 ? ", " + why : "")}.");
                            Plugin.L.LogInfo($"[equipaggio] talento {t} a {who}: {why}");
                            break;
                        }
                    }
                }
                catch (Exception e) { Plugin.L.LogWarning("Talenti dell'equipaggio: " + e.Message); }
            }
        }

        // ---- la voce dell'equipaggio
        static string Pick(params string[] a) => a[rng.Next(a.Length)];
        static string Who(Member m) => m.off != null ? $"{Officers.Short[m.off.rank]} {m.off.last}" : (string)m.p.GetPlayerName();
        static readonly Dictionary<int, float> taskBarkAt = new(); static string lastLine = "";
        static void Say(Member m, string text, bool force = false)
        {
            float now = Time.time; if (!force && now < nextBark) return; if (text == lastLine) return; lastLine = text;
            if (barkAt.TryGetValue(m.id, out var t) && now < t && !force) return;
            nextBark = now + 6f; barkAt[m.id] = now + 20f;
            try { Stations.LogEvent(Stations.MyShip.ShipID, $"«{Who(m)}»: {text}"); } catch { }
            Plugin.L.LogInfo($"[equipaggio] {Who(m)}: {text}");
        }
        static void Bark(Member m, int task, int before)
        {
            if (sit.gravity < 2 && task is not REVIVE) return;
            string t = task switch
            {
                FIRE => Pick("Incendio! Ci penso io.", "Vado agli estintori.", "Fiamme a bordo, mi muovo.", "Prendo l'estintore, coprite le stazioni."),
                REPEL => sit.invaders > 2 ? Pick("Intrusi a bordo, con me!", "Sono in tanti: fuoco concentrato!", "Difendiamo il ponte!") : Pick("Intruso a bordo, lo prendo io.", "Ho visto un intruso, vado.", "Armi in pugno, c'è qualcuno a bordo."),
                REVIVE => Pick("Copritemi, lo rianimo!", "Uomo a terra, arrivo!", "Resisti, sto arrivando."),
                HEAL_CREW => Pick("Ti medico subito.", "Fermo lì, ti rimetto in sesto."),
                REPAIR => Pick("Vado sui sistemi danneggiati.", "Riparazioni, tocca a me.", "Prendo la saldatrice."),
                STARTUP => Pick("Il reattore è giù, lo riavvio!", "Niente energia: vado in sala macchine."),
                HEAL_SELF => Pick("Sono ferito, ripiego un attimo.", "Mi ritiro a curarmi, tenete la posizione.", "Colpito… mi serve un medikit."),
                STATION => before is FIRE or REPEL or REPAIR ? Pick("Torno al mio posto.", "Rientro in stazione.") : "",
                ESCORT => Pick("Capitano, vengo con lei.", "La copro io là fuori."),
                _ => "",
            };
            if (t.Length == 0) return;
            // quando in tre partono per lo stesso compito, parla uno solo
            if (taskBarkAt.TryGetValue(task, out var at) && Time.time < at) return; taskBarkAt[task] = Time.time + 12f;
            Say(m, t, sit.gravity >= 3 && task is REVIVE or REPEL);
        }

        // ---- ordini del capitano
        static Member Of(int cls) => crew.Values.FirstOrDefault(x => x.cls == cls);
        static void Given(string what) { var O = Orders; O.given++; if (!O.used.Contains(what)) O.used.Add(what); Saga.MarkDirty(); GalaxyView.Dirty(); Service.Add("equipaggio.ordini", 1); Feats.CrewOrder(O.given, O.used.Count(x => Doctrines.Contains(x))); }
        public static string Order(string op, string arg)
        {
            var O = Orders; int.TryParse(arg, out int cls);
            switch (op)
            {
                case "dottrina":
                    {
                        if (!Doctrines.Contains(arg)) return ""; O.doctrine = arg; Given(arg);
                        var who = crew.Values.OrderBy(x => x.cls == 3 ? 0 : x.cls == 2 ? 1 : 2).FirstOrDefault();
                        string ack = arg switch { "combattimento" => "Posti di combattimento! Tutti alle stazioni.", "difesa" => "Difesa interna: armi in pugno, nessuno sale a bordo.", "danni" => "Controllo danni: estintori e saldatrici, si ripara tutto.", "scorta" => "Due di noi con il capitano, sempre.", "riposo" => "Riposo: chi è ferito in infermeria, gli altri tirano il fiato.", _ => "Ricevuto: ognuno valuta da sé, come sempre." };
                        if (who != null) Say(who, ack, true);
                        return $"Dottrina dell'equipaggio: {DoctrineName(arg)}.";
                    }
                case "postura":
                    {
                        string cur = O.Stance(cls); string nx = Stances[(Array.IndexOf(Stances, cur) + 1) % Stances.Length]; O.stance[cls.ToString()] = nx; Given("postura");
                        var m = Of(cls); if (m != null) Say(m, nx switch { "posto" => "Resto al mio posto, qualunque cosa succeda.", "scorta" => "La seguo come un'ombra, capitano.", "riserva" => "Mi tengo pronto, ma fuori dai piedi.", "libero" => "Faccio di testa mia, allora.", _ => "Valuto io, come sempre." }, true);
                        return $"{Officers.StationOf(cls)}: {StanceName(nx)}.";
                    }
                case "specialita":
                    {
                        string cur = O.Focus(cls); string nx = Focuses[(Array.IndexOf(Focuses, cur) + 1) % Focuses.Length]; O.focus[cls.ToString()] = nx; Given("specialita");
                        return $"{Officers.StationOf(cls)}: " + (nx.Length == 0 ? "nessuna specializzazione" : "specializzazione " + nx) + ".";
                    }
                case "ritirata": { int[] steps = { 25, 40, 55, 70 }; int i = Array.IndexOf(steps, O.retreat); O.retreat = steps[(i + 1) % steps.Length]; Given("ritirata"); return $"Ritirata in infermeria sotto il {O.retreat}% di salute."; }
                case "abbordaggio": { string[] r2 = { "armi", "tutti", "nessuno" }; int i = Array.IndexOf(r2, O.boarding); O.boarding = r2[(i + 1) % r2.Length]; Given("abbordaggio"); return "Squadre d'abbordaggio: " + (O.boarding == "armi" ? "solo armi e comando" : O.boarding == "tutti" ? "tutti tranne il pilota" : "nessuno lascia la nave") + "."; }
                case "rapporto": Report(); return "";
            }
            return "";
        }
        // ognuno dice come sta e cosa fa, con parole sue
        public static void Report()
        {
            foreach (var m in crew.Values.OrderBy(x => x.cls))
            {
                string act = Activity(m); string state = m.hp < 0.35f ? Pick("Sono messo male, capitano.", "Ho bisogno di un medico.") : m.hp < 0.7f ? Pick("Qualche graffio, niente di grave.", "Reggo.") : Pick("Pronto.", "Tutto in ordine.", "Ai suoi ordini.");
                string mood = m.off == null ? "" : m.off.loyalty >= 80 ? Pick(" Con lei fino in fondo.", " Bella squadra, questa.") : m.off.loyalty < 30 ? Pick(" Ma non sono contento di come vanno le cose.", " Speriamo cambi qualcosa.") : "";
                lastLine = ""; Say(m, $"{act}, salute {m.hp:P0}. {state}{mood}", true); nextBark = 0f;
            }
            Given("rapporto");
        }
        static readonly Dictionary<int, string> ActIt = new()
        {
            [REPAIR] = "riparazioni", [FIRE] = "antincendio", [REPEL] = "caccia agli intrusi", [STATION] = "alla stazione", [TURRET] = "alla torretta", [ESCORT] = "con il capitano",
            [BOARD] = "all'abbordaggio", [STARTUP] = "riavvio dei sistemi", [HEAL_SELF] = "in infermeria", [REVIVE] = "soccorso a un compagno", [HEAL_CREW] = "cura i compagni", [AT_EASE] = "a riposo", [32] = "al timone", [58] = "a prendere munizioni",
        };
        static string Activity(Member m)
        {
            try { var am = m.p.ActiveMainPriority; if (am != null && ActIt.TryGetValue(am.TypeData, out var t)) return t; } catch { }
            return m.task >= 0 && ActIt.TryGetValue(m.task, out var t2) ? t2 : "in giro per la nave";
        }
        public class CrewRow { public int cls; public string name = "", rank = "", trait = "", activity = "", stance = "auto", focus = "", best = ""; public float hp = -1; public int loyalty = -1; public bool bot, injured, present; }
        public class Panel { public List<CrewRow> rows = new(); public string doctrine = "equilibrio", boarding = "armi", gravity = "calma"; public int retreat = 40, given; }
        public static Panel Snapshot()
        {
            var O = Orders; var p = new Panel { doctrine = O.doctrine, boarding = O.boarding, retreat = O.retreat, given = O.given, gravity = Gravity[sit.gravity] };
            for (int c = 0; c < 5; c++)
            {
                var row = new CrewRow { cls = c, stance = O.Stance(c), focus = O.Focus(c) };
                try
                {
                    var pl = PLServer.Instance.GetCachedFriendlyPlayerOfClass(c, Stations.MyShip);
                    if (pl != null) { row.present = true; row.bot = pl.IsBot; if (!pl.IsBot) row.name = (string)pl.GetPlayerName(); var pw = pl.MyPawn; if (pw != null && pw.MaxHealth > 0) row.hp = pw.IsDead ? 0f : pw.Health / pw.MaxHealth; }
                    var m = Of(c); if (m != null) row.activity = Activity(m);
                    if (row.bot || pl == null) { var o = Saga.State.officers.FirstOrDefault(x => x.alive && x.cls == c); if (o != null) { row.name = $"{o.first} {o.last}"; row.rank = Officers.Short[o.rank]; row.trait = o.trait; row.loyalty = o.loyalty; row.injured = o.injuredUntil > Obscured.Jumps(); var b = o.skills?.Where(kv => kv.Key != "ferite").OrderByDescending(kv => kv.Value).FirstOrDefault(); if (b != null && b.Value.Key != null) row.best = $"{b.Value.Key} {Level(o, b.Value.Key)}"; } }
                }
                catch { }
                p.rows.Add(row);
            }
            return p;
        }
        public static void Request(string op, string arg)
        {
            if (Host) { var t = Order(op, arg); if (t.Length > 0) Stations.Announce("EQUIPAGGIO", t); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|equipaggio|{op}|{arg}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var q = msg.Split('|'); if (q.Length >= 4 && Host) Request(q[2], q[3]); }

        // ---- prove
        public static string Train(int cls, string area, int xp)
        {
            var m = crew.Values.FirstOrDefault(x => x.cls == cls); if (m == null) return "nessun bot di quella classe";
            Gain(m, area, xp); return Describe();
        }
        public static string Describe()
        {
            var s = sit; var sb = new System.Text.StringBuilder($"mente dell'equipaggio {(Enabled ? "attiva" : "spenta")}: gravità {Gravity[s.gravity]} (incendi {s.fires}/{NativeFires}, intrusi {s.invaders}, a terra {s.downed}, feriti {s.wounded}, scafo {s.hull:P0}, sistema peggiore {s.worstSys:0}%, battaglia {(s.combat ? "sì" : "no")}, reattore {(s.power ? "stabile" : "in fusione")}{(s.captainAway ? ", capitano fuori nave" : "")}); ordinamenti {PreSorts}, compito assente {Missing}, trattenuti {Holds}, spinte {Boosts}, incarichi {Assigns}, ritirate {Retreats}, pensieri rapidi {Thinks}, talenti spesi {TalentsSpent}");
            foreach (var m in crew.Values)
            {
                string top = "";
                try
                {
                    var am = m.p.ActiveMainPriority; if (am != null) top += $" attiva {(EAIPriorityListDisplayed)am.TypeData}({am.Type})={am.LastCalculated_PriorityValue}";
                    var pl = m.p.GetAIData()?.Priorities;
                    if (pl != null)
                    {
                        var items = new List<AIPriority>(); for (int i = 0; i < pl.Count; i++) if (pl[i] != null) items.Add(pl[i]);
                        top += " | lista " + string.Join(" ", items.OrderByDescending(x => x.LastCalculated_PriorityValue).Take(5).Select(x => $"{(x.Type == AIPriorityType.E_MAIN || x.Type == AIPriorityType.E_CLASS_MAIN ? ((EAIPriorityListDisplayed)x.TypeData).ToString() : x.Type + ":" + x.TypeData)}={x.LastCalculated_PriorityValue}"));
                    }
                }
                catch { }
                string skills = m.off?.skills == null ? "" : string.Join(", ", m.off.skills.Where(kv => kv.Key != "ferite").Select(kv => $"{kv.Key} {Level(m.off, kv.Key)} ({kv.Value})")) + (m.off?.skills != null && m.off.skills.TryGetValue("ferite", out var fe) ? $", ferite {fe}" : "");
                sb.Append($"\n      {Who(m)} (classe {m.cls}, salute {m.hp:P0}, punti talento {Points(m.p)}): incarico {(m.task >= 0 ? TaskName.GetValueOrDefault(m.task, m.task.ToString()) : "-")}; {top}{(skills.Length > 0 ? "; competenze " + skills : "")}");
            }
            return sb.ToString();
        }
    }

    [HarmonyPatch(typeof(PLPlayer), nameof(PLPlayer.UpdateAIPriorities))]
    static class CrewMindThink { static void Postfix(PLPlayer __instance) => CrewMind.OnPriorities(__instance); }
}

namespace Melchior
{
    [HarmonyPatch(typeof(PLPlayer), nameof(PLPlayer.SortAIPriorities))]
    static class CrewMindPreSort { static void Prefix(PLPlayer __instance) => CrewMind.OnPreSort(__instance); }
}

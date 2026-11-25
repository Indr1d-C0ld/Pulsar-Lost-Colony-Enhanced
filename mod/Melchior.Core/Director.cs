// Direttore del ritmo: misura la tensione a bordo e alterna calma, allerta, crisi e respiro.
// Quando la calma dura abbastanza sceglie un evento (imboscata, cacciatore di taglie, anomalia di bordo, ...)
// in proporzione alla potenza dell'equipaggio, evitando di ripetere sempre gli stessi.
// Gira solo sull'host; lo stato viaggia con le regolazioni di stazione (chiavi "drx_"), i messaggi con l'evento 177.
// Configurazione: config/melchior.director.json.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityEngine;

namespace Melchior
{
    public class DirectorEvent { public string Id = ""; public float Weight = 1; public float MinPower; public bool NeedsOpenSpace; }

    public class DirectorConfig
    {
        public bool Enabled = true;
        public float CalmMin = 150, CalmMax = 330;   // secondi di calma prima di un evento
        public float AlertSeconds = 90;              // attesa dopo un evento prima di tornare alla calma
        public float RespiteSeconds = 120;           // dopo una crisi, niente eventi per questo tempo
        public float CrisisAt = 55, CalmAt = 22;     // soglie di tensione
        public float Smoothing = 0.25f;              // quanto in fretta la tensione segue la situazione
        public float PowerPerCrewLevel = 1f, PowerPerCrewMember = 1.5f, PowerPerRarityTier = 1.2f;
        public float FasterWhenStrong = 0.04f;       // la calma si accorcia del 4% per punto di potenza oltre 5
        public bool Announce = true;
        public DirectorEvent[] Events =
        {
            new() { Id = "imboscata", Weight = 3, NeedsOpenSpace = true },
            new() { Id = "rivali", Weight = 2.5f, NeedsOpenSpace = true },
            new() { Id = "missione", Weight = 3f },
            new() { Id = "taglia", Weight = 1.2f, MinPower = 6, NeedsOpenSpace = true },
            new() { Id = "sciame", Weight = 1.2f, MinPower = 3, NeedsOpenSpace = true },
            new() { Id = "tempesta", Weight = 1.5f },
            new() { Id = "sovraccarico", Weight = 1.2f },
            new() { Id = "interferenza", Weight = 1f },
            new() { Id = "microfratture", Weight = 1f },
        };
        // navi per fascia di potenza: la fascia è potenza / 4
        public string[][] ShipTiers =
        {
            new[] { "E_WDDRONE1", "E_WDDRONE2" },
            new[] { "E_WDDRONE3", "E_CORRUPTED_DRONE", "E_INFECTED_FIGHTER" },
            new[] { "E_WDCRUISER", "E_INFECTED_FIGHTER_HEAVY", "E_OUTRIDER" },
            new[] { "E_DESTROYER", "E_WDCRUISER", "E_INFECTED_CARRIER" },
            new[] { "E_CARRIER", "E_DESTROYER", "E_ANNIHILATOR" },
        };
    }

    public static class Director
    {
        public static DirectorConfig Cfg = new();
        public static readonly string[] PhaseNames = { "CALMA", "ALLERTA", "CRISI", "RESPIRO" };
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static readonly System.Random rng = new();
        static readonly List<string> recent = new();
        static long next; static float phaseTime, lowTime;
        public static string LastEvent = "";

        public static void Load(string dir)
        {
            string path = Path.Combine(dir, "melchior.director.json");
            var opt = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            try
            {
                if (File.Exists(path)) Cfg = JsonSerializer.Deserialize<DirectorConfig>(File.ReadAllText(path), opt) ?? new DirectorConfig();
                else File.WriteAllText(path, JsonSerializer.Serialize(Cfg, opt));
            }
            catch (Exception e) { Plugin.L.LogError("melchior.director.json non valido, uso i valori predefiniti: " + e.Message); Cfg = new DirectorConfig(); }
            Plugin.L.LogInfo($"Direttore: {Cfg.Events.Length} eventi ({(Cfg.Enabled ? "attivo" : "spento")}).");
        }

        static float G(int id, string k) => Stations.Get(id, k);
        static void S(int id, string k, float v) => Stations.SetQuiet(id, k, v);

        // Potenza dell'equipaggio: livello, persone a bordo (giocatori e bot), rarità media dell'equipaggiamento montato.
        public static float Power(PLShipInfo ship, out int crew, out float tier)
        {
            crew = 0; tier = 0; int level = 1;
            try { level = Math.Max(1, PLServer.Instance.CurrentCrewLevel); } catch { }
            try { for (int c = 0; c < 5; c++) if (PLServer.Instance.GetCachedFriendlyPlayerOfClass(c, ship) != null) crew++; } catch { }
            try
            {
                var list = ship.MyStats.m_AllComponents; int n = 0; float sum = 0;
                for (int i = 0; i < list.Count; i++) { var c = list[i]; if (c == null || !c.IsEquipped) continue; sum += Rarity.TierOf(c.getHash()); n++; }
                tier = n > 0 ? sum / n : 0;
            }
            catch { }
            return level * Cfg.PowerPerCrewLevel + Math.Max(0, crew - 1) * Cfg.PowerPerCrewMember + tier * Cfg.PowerPerRarityTier;
        }

        // Tensione grezza: nemici vicini e la loro forza rispetto alla nostra, danni recenti, scafo e sistemi malridotti.
        static float RawTension(PLShipInfo ship, out int hostiles)
        {
            hostiles = 0; var st = ship.MyStats; if (st == null) return 0;
            float ours = Math.Max(1f, st.HullMax + st.ShieldsMax), threat = 0;
            try
            {
                foreach (var kv in PLEncounterManager.Instance.AllShips)
                {
                    var o = kv.Value; if (o == null || o.ShipID == ship.ShipID || o.HasBeenDestroyed || o.MyStats == null) continue;
                    bool hostile = ship.HostileShips.Contains(o.ShipID) || o.HostileShips.Contains(ship.ShipID) || (o.TargetShip != null && o.TargetShip.ShipID == ship.ShipID);
                    if (!hostile) continue;
                    if (ship.Exterior != null && o.Exterior != null && Vector3.Distance(ship.Exterior.transform.position, o.Exterior.transform.position) > 9000f) continue;
                    hostiles++; threat += (o.MyStats.HullCurrent + o.MyStats.ShieldsCurrent) / ours;
                }
            }
            catch { }
            float hurt = st.HullMax > 1 ? 1f - st.HullCurrent / st.HullMax : 0;
            bool hitRecently = false; try { hitRecently = Time.time - ship.LastTookDamageTime() < 12f; } catch { }
            float sys = Stations.SysIds.Average(x => G(ship.ShipID, "dmx_" + x)) / 100f;
            return Math.Clamp(38f * Math.Min(threat, 2f) + (hitRecently ? 22f : 0f) + 30f * hurt + 12f * sys + (hostiles > 0 ? 10f : 0f), 0f, 100f);
        }

        static bool OpenSpace()
        {
            try { var s = PLServer.GetCurrentSector(); return s != null && s.VisualIndication == ESectorVisualIndication.NONE; } catch { return false; }
        }

        public static void Tick()
        {
            if (!Cfg.Enabled) return;
            long now = clock.ElapsedMilliseconds; if (now < next) return; next = now + 2000;
            try
            {
                if (!PhotonNetwork.inRoom || !PhotonNetwork.isMasterClient || PLServer.Instance == null) return;
                var ship = Stations.MyShip; if (ship == null || ship.MyStats == null || ship.HasBeenDestroyed) return;
                int id = ship.ShipID; KeepNames();
                float power = Power(ship, out _, out _); S(id, "drx_pow", (float)Math.Round(power, 1));
                float t = G(id, "drx_t"), raw = RawTension(ship, out int hostiles);
                t += (raw - t) * Cfg.Smoothing; if (t < 0.5f) t = 0.01f;
                S(id, "drx_t", t);
                int phase = (int)G(id, "drx_phase"); phaseTime += 2f;
                bool warp = false; try { warp = ship.InWarp || ship.IsChargingWarp; } catch { }
                float wait = G(id, "drx_wait");
                if (wait <= 0 && phase == 0) wait = NewCalm(power);

                switch (phase)
                {
                    case 0: // calma: il conto alla rovescia scende solo fuori dalla curvatura
                        if (t >= Cfg.CrisisAt) { SetPhase(id, 2, "Crisi in corso: tutto l'equipaggio ai posti di combattimento"); break; }
                        if (!warp && !Missions.BusyHere()) wait -= 2f;
                        if (wait <= 0 && Fire(ship, power)) { SetPhase(id, 1, null); wait = 0; }
                        else if (wait <= 0) wait = 30f; // nessun evento possibile qui: riprova tra poco
                        break;
                    case 1: // allerta: l'evento è in corso, si aspetta di vedere se diventa una crisi
                        if (t >= Cfg.CrisisAt) SetPhase(id, 2, null);
                        else if (phaseTime >= Cfg.AlertSeconds && hostiles == 0) { SetPhase(id, 0, null); wait = NewCalm(power); }
                        break;
                    case 2: // crisi: finisce quando la tensione resta bassa per 16 secondi
                        lowTime = t < Cfg.CalmAt && hostiles == 0 ? lowTime + 2f : 0f;
                        if (lowTime >= 16f)
                        {
                            lowTime = 0; SetPhase(id, 3, "Situazione sotto controllo. Squadre danni al lavoro."); Service.Add("direttore.crisi.superate", 1); Stations.Initiative(id, Stations.Cfg.Captain.GainPerSuccess);
                            if (rng.NextDouble() < 0.4) { var got = Loot.Grant(ship, 1, 4, "crisi"); if (got != null) Notify("RITROVAMENTO", "Dai rottami del combattimento recuperiamo: " + got.Name + "."); }
                        }
                        break;
                    case 3: // respiro
                        if (t >= Cfg.CrisisAt) SetPhase(id, 2, null);
                        else if (phaseTime >= Cfg.RespiteSeconds) { SetPhase(id, 0, null); wait = NewCalm(power); }
                        break;
                }
                S(id, "drx_wait", Math.Max(0.01f, wait));
            }
            catch (Exception e) { Plugin.L.LogWarning("Direttore: " + e.Message); next = now + 10000; }
        }

        static float NewCalm(float power)
        {
            float f = 1f - Math.Clamp((power - 5f) * Cfg.FasterWhenStrong, 0f, 0.5f);
            return (float)(Cfg.CalmMin + rng.NextDouble() * (Cfg.CalmMax - Cfg.CalmMin)) * f;
        }

        static void SetPhase(int id, int phase, string say)
        {
            S(id, "drx_phase", phase == 0 ? 0.01f : phase); phaseTime = 0; lowTime = 0;
            Plugin.L.LogInfo($"[direttore] fase {PhaseNames[phase]}");
            if (say != null) Notify("RAPPORTO DI PLANCIA", say);
        }

        public static void Notify(string title, string text)
        {
            var me = Stations.MyShip; if (me != null) Stations.LogEvent(me.ShipID, text);
            if (Cfg.Announce) Stations.Announce(title, text);
        }

        // Sceglie ed esegue un evento adatto alla situazione. Restituisce false se non c'era niente di adatto.
        public static bool Fire(PLShipInfo ship, float power, string force = null)
        {
            // prima le conseguenze arrivate a scadenza (seguiti degli archi, gratitudine, vendette...)
            if (force == null && Saga.TryDue(ship, power)) { LastEvent = "cronaca"; Service.Add("direttore.eventi", 1); return true; }
            bool open = OpenSpace();
            var pool = Cfg.Events.Where(e => force != null ? e.Id == force : power >= e.MinPower && (!e.NeedsOpenSpace || open)).ToList();
            if (pool.Count == 0) return false;
            float W(DirectorEvent e) => e.Weight * Saga.Weight(e.Id) * (recent.Contains(e.Id) ? 0.25f : 1f);
            double r = rng.NextDouble() * pool.Sum(W); var pick = pool[^1];
            foreach (var e in pool) { r -= W(e); if (r <= 0) { pick = e; break; } }
            bool ok;
            try { ok = Run(ship, power, pick.Id); }
            catch (Exception e) { Plugin.L.LogWarning($"Direttore: evento {pick.Id} fallito: {e.Message}"); ok = false; }
            if (!ok) return false;
            LastEvent = pick.Id; recent.Add(pick.Id); if (recent.Count > 3) recent.RemoveAt(0);
            Service.Add("direttore.eventi", 1);
            Plugin.L.LogInfo($"[direttore] evento {pick.Id} (potenza {power:0.0})");
            return true;
        }

        static bool Run(PLShipInfo ship, float power, string ev)
        {
            int id = ship.ShipID;
            switch (ev)
            {
                case "imboscata":
                {
                    int count = 1 + (power >= 7 ? 1 : 0) + (power >= 14 ? 1 : 0);
                    var names = new List<string>();
                    for (int i = 0; i < count; i++) { var s = Spawn(ship, PickShip(power, i == 0 ? 0 : -1), Modifiers(power)); if (s != null) names.Add((string)s.ShipName); }
                    if (names.Count == 0) return false;
                    Notify("ALLARME DI PROSSIMITÀ", names.Count == 1 ? $"Contatto ostile in avvicinamento: {names[0]}." : $"{names.Count} contatti ostili in avvicinamento: {string.Join(", ", names)}.");
                    return true;
                }
                case "taglia":
                {
                    var s = Spawn(ship, EShipType.E_BOUNTY_HUNTER_01, Modifiers(power) | 1 << (int)EShipModifierType.ELITE); if (s == null) return false;
                    Notify("ALLARME DI PROSSIMITÀ", $"Un cacciatore di taglie ha seguito la nostra scia di curvatura: {(string)s.ShipName}.");
                    return true;
                }
                case "sciame":
                {
                    int count = 2 + (int)Math.Min(3, power / 6); int made = 0;
                    var type = rng.Next(2) == 0 ? EShipType.E_WDDRONE1 : EShipType.E_CORRUPTED_DRONE;
                    for (int i = 0; i < count; i++) if (Spawn(ship, type, Modifiers(power) | 1 << (int)EShipModifierType.SWIFT) != null) made++;
                    if (made == 0) return false;
                    Notify("ALLARME DI PROSSIMITÀ", $"Sciame di {made} droni in rapido avvicinamento.");
                    return true;
                }
                case "rivali": return RivalForce.Spawn(ship, power);
                case "missione": return (Saga.State.arc == null && rng.NextDouble() < 0.35 && Saga.StartArc(power)) || Contracts.PostUrgent(power);
                case "tempesta":
                    Hurt(id, "sensors", 35 + power); Hurt(id, "shields", 15 + power / 2);
                    Notify("ANOMALIA", "Tempesta ionica: sensori e scudi danneggiati. Ingegneria, squadre ai sistemi.");
                    return true;
                case "sovraccarico":
                    Stations.Set(id, "core_stress", Math.Min(95f, G(id, "core_stress") + 30 + power));
                    Notify("ANOMALIA", "Picco di plasma nella rete EPS: lo stress del nucleo sale. Spurgo consigliato.");
                    return true;
                case "interferenza":
                    if (G(id, "scx_scan") > 0) Stations.Set(id, "scx_scan", Math.Max(0.01f, G(id, "scx_scan") * 0.3f));
                    if (G(id, "scx_band") > 0) Stations.Set(id, "scx_eff", Math.Max(0.01f, G(id, "scx_eff") - 40));
                    Hurt(id, "sensors", 20);
                    Notify("ANOMALIA", "Interferenza subspaziale: scansioni degradate e armonica degli scudi compromessa.");
                    return true;
                case "microfratture":
                    Hurt(id, "engines", 30 + power); Hurt(id, "warp", 20 + power / 2);
                    Notify("ANOMALIA", "Microfratture nei condotti dei motori. Spinta e curvatura ridotte finché non si ripara.");
                    return true;
            }
            return false;
        }

        static void Hurt(int id, string sys, float pts) { string k = "dmx_" + sys; Stations.Set(id, k, Math.Min(90f, G(id, k) + pts)); }

        // Tipo di nave in base alla potenza: fascia = potenza / 4, con un po' di varietà verso il basso.
        internal static EShipType PickShip(float power, int shift)
        {
            int tierIdx = Math.Clamp((int)(power / 4f) + shift, 0, Cfg.ShipTiers.Length - 1);
            var list = Cfg.ShipTiers[tierIdx];
            return Enum.TryParse<EShipType>(list[rng.Next(list.Length)], out var t) ? t : EShipType.E_WDDRONE1;
        }

        // Modificatori della nave nemica: deboli per un equipaggio alle prime armi, temibili per un veterano.
        internal static int Modifiers(float power)
        {
            int m = 0; void Add(EShipModifierType x) => m |= 1 << (int)x;
            if (power < 3) { if (rng.Next(2) == 0) Add(EShipModifierType.NEGLECTED); else Add(EShipModifierType.VULNERABLE); }
            else if (power >= 8)
            {
                var good = new[] { EShipModifierType.ARMORED, EShipModifierType.BRUTAL, EShipModifierType.SWIFT, EShipModifierType.RECKLESS, EShipModifierType.MISSILE_DEFENSE_SYSTEM };
                Add(good[rng.Next(good.Length)]);
                if (power >= 14) Add(good[rng.Next(good.Length)]);
                if (power >= 20) Add(EShipModifierType.ELITE);
            }
            return m;
        }

        internal static PLShipInfoBase Spawn(PLShipInfo me, EShipType type, int modifiers, string name = null)
        {
            var cpei = PLEncounterManager.Instance?.GetCPEI(); if (cpei == null) return null;
            var psi = new PLPersistantShipInfo(type, -1, PLServer.GetCurrentSector(), modifiers, false, false, false, -1, me.ShipID);
            if (name != null) psi.ShipName = name;
            var pos = cpei.GetEmptyLocationForEnemy(type, psi, rng.Next(1000));
            var ship = cpei.SpawnEnemyShip(type, psi, pos, Quaternion.identity);
            if (ship != null && name != null) { Names[ship.ShipID] = name; Rename(ship, name); }
            return ship;
        }

        // Nomi propri delle navi generate: il gioco li ricalcola dai modificatori subito dopo la creazione, quindi li riapplichiamo.
        internal static readonly Dictionary<int, string> Names = new();
        static void Rename(PLShipInfoBase s, string name) { try { if (s.ShipNameValue != name) s.ShipNameValue = name; } catch (Exception e) { Plugin.L.LogWarning("Nome nave: " + e.Message); } }
        internal static void KeepNames()
        {
            foreach (var (id, name) in Names.ToArray())
            {
                PLShipInfoBase s = null; try { s = PLEncounterManager.Instance?.GetShipFromID(id); } catch { }
                if (s == null || s.HasBeenDestroyed) { Names.Remove(id); continue; }
                Rename(s, name);
            }
        }

        public static string Describe()
        {
            var me = Stations.MyShip; if (me == null) return "nessuna nave";
            int id = me.ShipID; float p = Power(me, out int crew, out float tier);
            return $"fase {PhaseNames[Math.Clamp((int)G(id, "drx_phase"), 0, 3)]} tensione {G(id, "drx_t"):0} prossimo evento tra {G(id, "drx_wait"):0} s potenza {p:0.0} (equipaggio {crew}, rarità media {tier:0.0}) ultimo evento {(LastEvent.Length > 0 ? LastEvent : "-")}";
        }
    }
}

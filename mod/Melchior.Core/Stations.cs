// Regolazioni di stazione: ogni postazione della nave ha comandi continui con un vantaggio e un costo.
// Tutto è definito in config/melchior.stations.json (comandi, effetti, assetti predefiniti).
// Gli effetti si applicano alle statistiche della nave dopo il calcolo del gioco, come gli affissi;
// lo stato viaggia tra i giocatori con un evento Photon nostro (codice 177) attraverso il relay.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using UnityEngine;

namespace Melchior
{
    public class StationEffect { public string Stat = ""; public float Percent; } // variazione % della statistica a comando = 100

    public class StationControl
    {
        public string Id = "", Label = "", Help = "";
        public int Station;                 // 0 Capitano, 1 Pilota, 2 Scienziato, 3 Armi, 4 Ingegnere
        public float Min = 0, Max = 100, Step = 10;
        public string Page = "Regolazioni";  // scheda della pagina del pannello in cui compare
        public string Summary = "";          // descrizione breve degli effetti, usata nelle righe compatte
        public string Group = "";            // gruppo a bilancio: la somma dei valori del gruppo non può superare il bilancio
        public float Scale = 100;            // valore del comando a cui gli effetti raggiungono la loro percentuale piena
        public StationEffect[] Effects = Array.Empty<StationEffect>();
    }

    // Nucleo del reattore: stress che cresce con il carico e procedure manuali per gestirlo.
    public class CoreConfig
    {
        public bool Enabled = true;
        public float Cooling = 0.45f;            // stress smaltito al secondo a riposo
        public float PerOverdrive = 1.3f;        // stress al secondo con la sovralimentazione a 100
        public float PerEpsNotch = 0.12f;        // stress al secondo per ogni tacca EPS positiva
        public float WarnAt = 60, CriticalAt = 85;
        public float TempPenaltyPerPoint = 0.6f; // % di temperatura massima persa per ogni punto oltre WarnAt
        public float CriticalOutputPenalty = 15; // % di potenza persa in stato critico
        public int ScramLockSeconds = 30;        // blocco dopo l'arresto automatico
        public float VentRelief = 40, VentFailPenalty = 15; public int VentSeconds = 8, VentCooldown = 20;
        public float AlignBonus = 10; public int AlignSeconds = 120, AlignCooldown = 15; public float AlignFailPenalty = 10, AlignWindow = 0.16f;
    }

    // Scienza: armonica degli scudi che il nemico impara col tempo, impulso attivo dei sensori, decrittazione.
    public class ScienceConfig
    {
        public bool Enabled = true;
        public float HarmonicSwing = 30;          // % di scudi tra armonica fresca (+metà) e compromessa (-metà)
        public float DecayCalm = 0.1f, DecayYellow = 0.3f, DecayRed = 0.7f; // efficacia persa al secondo per stato d'allarme
        public int RotateCooldown = 20, RotateDipSeconds = 5; public float RotateDipPercent = 50;
        public int PingSeconds = 20, PingCooldown = 60; public float PingDetection = 100, PingSignature = 80;
        public int DecryptAttempts = 6, DecryptCooldown = 30, SolutionSeconds = 180; public float SolutionDamage = 12, SolutionCharge = 8;
    }

    // Armi: dottrina di tiro che matura col tempo, puntamento su scudi o scafo, sovraccarico dei condensatori.
    public class WeaponsConfig
    {
        public bool Enabled = true;
        public float QualityPerSecond = 1.4f;                       // crescita della soluzione di tiro
        public float PrecisionDamage = 15, RapidCharge = 20, SustainCooling = 35, SustainDamagePenalty = 5;
        public float AimShieldsBonus = 25, AimShieldsHullPenalty = 15; // puntamento sugli scudi: danno agli scudi su, allo scafo giù
        public float AimHullBonus = 20, AimHullShieldPenalty = 15;     // puntamento sullo scafo
        public float ChargeSeconds = 4, OverloadAt = 100, ChargeMax = 115;
        public float BurstMaxDamage = 30; public int BurstSeconds = 12, BurstCooldown = 25;
        public float JamChargePenalty = 30; public int JamSeconds = 15;
        public float CoreStressPerBurst = 8;                        // un sovraccarico pieno scalda il nucleo dell'ingegnere
    }

    // Pilota: ugelli che si sbilanciano e vanno tenuti centrati, manovre a tempo, calcolo di rotta a tre assi.
    public class PilotConfig
    {
        public bool Enabled = true;
        public float TrimDrift = 4, TrimStep = 15, TrimBonus = 6, TrimPenalty = 18; // deriva al secondo, correzione per clic, % di spinta al centro e al limite
        public int EvadeSeconds = 10, EvadeCooldown = 40; public float EvadeManeuver = 60, EvadeInertia = 40, EvadeDamagePenalty = 15, EvadeIncoming = 20;
        public int BurnSeconds = 8, BurnCooldown = 45; public float BurnThrust = 50, BurnSignature = 60, BurnCoreStress = 10;
        public float DriftThrustPenalty = 60, DriftManeuverPenalty = 40, DriftSignatureCut = 40;
        public int RouteSeconds = 300, RouteCooldown = 20; public float RouteCharge = 30, RouteRange = 15;
    }

    // Capitano: iniziativa di comando alimentata dai successi dell'equipaggio, ordini di plancia e interventi.
    public class CaptainConfig
    {
        public bool Enabled = true;
        public float Start = 50, Regen = 0.4f, OrderDrain = 1.0f, GainPerSuccess = 6; public int TiredSeconds = 20;
        public float BattleDamage = 10, BattleShields = 8;         // ordine "battaglia"
        public float RepairArmor = 15, RepairRecharge = 20;        // ordine "danni"
        public float EfficiencyOutput = 6, EfficiencyCooling = 0.4f; // ordine "regime": potenza e smaltimento dello stress
        public float SilenceSignature = 25, SilenceDetection = 10; // ordine "silenzio"
        public float InterventionCost = 30, EmergencyRelief = 25;
    }

    // Danni ai sottosistemi: i colpi allo scafo guastano i sistemi, le squadre li riparano.
    public class DamageConfig
    {
        public bool Enabled = true;
        public float MaxPenalty = 40;             // % di prestazioni perse da un sistema fuori uso
        public float PointsPerHullPercent = 2.5f; // punti di danno al sistema per ogni 1% di scafo perso
        public float FocusFactor = 1.8f, FocusHullPenalty = 10; // tiro mirato a un sistema: più danno al sistema, meno allo scafo
        public float RepairPerTeam = 1.5f, PassiveRepair = 0.1f; // punti al secondo
        public int BypassSeconds = 30, BypassCooldown = 60; public float BypassStress = 12;
    }

    // Sensori: scansione del bersaglio che matura col tempo e sblocca informazioni e tiro mirato.
    public class SensorsConfig
    {
        public bool Enabled = true;
        public float ScanPerSecond = 2.5f, GainBoost = 50, EpsBoost = 15; // % in più a guadagno 100 e per tacca EPS ai sensori
        public float ReadoutAt = 33, SubsystemAt = 66, WeakPointDamage = 10;
    }

    // Distanza d'ingaggio: il pilota sceglie una fascia e la tiene; il vantaggio cresce finché la nave resta nella fascia.
    public class EngageConfig
    {
        public bool Enabled = true;
        public float ShortMax = 1500, LongMin = 4000, ScaleMax = 8000;
        public float PosGain = 4, PosLoss = 6;
        public float ShortDamage = 12, ShortRechargePenalty = 10;
        public float MidCharge = 10, MidManeuver = 10;
        public float LongRecharge = 15, LongSignatureCut = 15, LongDamagePenalty = 5;
    }

    // Protocolli permanenti del capitano: vantaggi continui che rallentano il recupero dell'iniziativa.
    public class ProtocolsConfig
    {
        public bool Enabled = true;
        public int MaxActive = 2; public float RegenCost = 0.1f;
        public float RepairBoost = 50, RepairChargePenalty = 8;
        public float FocusBoost = 40, FocusRechargePenalty = 8;
        public float ScanBoost = 60, ScanSignature = 15;
        public float ThermalCooling = 0.25f, ThermalThrustPenalty = 8;
    }

    public class StationPreset { public string Name = "", Help = ""; public int Alert = -1; public Dictionary<string, float> Values = new(); }

    public class StationsConfig
    {
        public bool Enabled = true;
        public bool RoleLocked = true;      // ogni ufficiale regola solo la propria stazione; il capitano tutte
        public string[] StationNames = { "Capitano", "Pilota", "Scienza", "Armi", "Ingegneria" };
        // Pannelli fisici della nave che danno accesso alle regolazioni: tipo di schermo -> stazione.
        public Dictionary<string, int> Panels = new()
        {
            // Solo schermi quadrati: quelli a mezza altezza (stato nave del capitano, refrigerante) taglierebbero la pagina.
            ["PLCommsScreen"] = 0, ["PLUIPilotingScreen"] = 1, ["PLScientistSensorScreen"] = 2, ["PLScientistComputerScreen"] = 2,
            ["PLWeaponsNukeScreen"] = 3, ["PLMissileLauncherScreen"] = 3, ["PLEngineerReactorScreen"] = 4,
        };
        public string PanelButtonText = "REGOLAZIONI";
        public float PanelMaxDistance = 6f;   // oltre questa distanza dal pannello la console si chiude
        public Dictionary<string, string> StatNames = new()
        {
            ["ReactorOutputMax"] = "Potenza reattore", ["ReactorTempMax"] = "Temp. max reattore", ["ShieldsMax"] = "Scudi massimi",
            ["ShieldsChargeRateMax"] = "Ricarica scudi", ["HullArmor"] = "Corazza", ["HullMax"] = "Integrità scafo",
            ["WarpChargeRate"] = "Carica curvatura", ["WarpRange"] = "Raggio curvatura", ["ThrustOutputMax"] = "Spinta principale",
            ["InertiaThrustOutputMax"] = "Spinta inerziale", ["ManeuverThrustOutputMax"] = "Spinta di manovra",
            ["TurretDamageFactor"] = "Danno torrette", ["TurretChargeFactor"] = "Carica torrette", ["TurretCoolingSpeedFactor"] = "Raffreddamento torrette",
            ["EMSignature"] = "Firma EM", ["EMDetection"] = "Rilevamento EM", ["CyberDefenseRating"] = "Difesa informatica", ["OxygenRefillRate"] = "Rigenerazione ossigeno",
        };
        public StationControl[] Controls =
        {
            new() { Id = "eng_overdrive", Station = 4, Label = "Sovralimentazione del nucleo", Help = "Spinge il reattore oltre il regime nominale.",
                Effects = new[] { E("ReactorOutputMax", 25), E("ReactorTempMax", -30), E("EMSignature", 40) } },
            new() { Id = "eng_integrity", Station = 4, Label = "Campo di integrità strutturale", Help = "Devia energia dagli emettitori allo scafo.",
                Effects = new[] { E("HullArmor", 20), E("ShieldsChargeRateMax", -25) } },
            new() { Id = "eng_lifesupport", Station = 4, Label = "Supporto vitale potenziato", Help = "Aumenta il riciclo dell'atmosfera a spese del reattore.",
                Effects = new[] { E("OxygenRefillRate", 60), E("ReactorOutputMax", -5) } },
            new() { Id = "eng_warpgeom", Station = 4, Min = -100, Label = "Geometria del campo di curvatura", Help = "A destra salti più lunghi, a sinistra carica più rapida.",
                Effects = new[] { E("WarpRange", 20), E("WarpChargeRate", -30) } },
            new() { Id = "sci_modulation", Station = 2, Min = -100, Label = "Modulazione degli scudi", Help = "A destra scudi più capienti, a sinistra ricarica più rapida.",
                Effects = new[] { E("ShieldsMax", 20), E("ShieldsChargeRateMax", -30) } },
            new() { Id = "sci_gain", Station = 2, Label = "Guadagno dei sensori", Help = "Vede più lontano, ma la nave si fa notare.",
                Effects = new[] { E("EMDetection", 60), E("EMSignature", 30) } },
            new() { Id = "sci_silent", Station = 2, Label = "Smorzamento delle emissioni", Help = "Corsa silenziosa: meno firma, meno prestazioni.",
                Effects = new[] { E("EMSignature", -40), E("ReactorOutputMax", -12), E("ThrustOutputMax", -10) } },
            new() { Id = "sci_firewall", Station = 2, Label = "Indurimento del firewall", Help = "Dedica potenza di calcolo alla difesa informatica.",
                Effects = new[] { E("CyberDefenseRating", 50), E("TurretChargeFactor", -10) } },
            new() { Id = "wep_tuning", Station = 3, Min = -100, Label = "Taratura delle torrette", Help = "A destra colpi più pesanti, a sinistra cadenza più alta.",
                Effects = new[] { E("TurretDamageFactor", 20), E("TurretChargeFactor", -25) } },
            new() { Id = "wep_cooling", Station = 3, Label = "Dissipazione forzata", Help = "Raffredda le armi scaricando calore sul reattore.",
                Effects = new[] { E("TurretCoolingSpeedFactor", 50), E("ReactorTempMax", -10) } },
            new() { Id = "plt_trim", Station = 1, Min = -100, Label = "Assetto dei propulsori", Help = "A destra velocità di punta, a sinistra agilità.",
                Effects = new[] { E("ThrustOutputMax", 20), E("ManeuverThrustOutputMax", -30) } },
            // Rete EPS: sei canali da -3 a +3; ogni tacca sposta potenza da un sistema all'altro.
            new() { Id = "eps_shields", Station = 4, Page = "Rete EPS", Group = "eps", Summary = "Capienza e ricarica", Min = -3, Max = 3, Step = 1, Scale = 3, Label = "Scudi",
                Effects = new[] { E("ShieldsMax", 15), E("ShieldsChargeRateMax", 24) } },
            new() { Id = "eps_weapons", Station = 4, Page = "Rete EPS", Group = "eps", Summary = "Danno e carica", Min = -3, Max = 3, Step = 1, Scale = 3, Label = "Armi",
                Effects = new[] { E("TurretDamageFactor", 12), E("TurretChargeFactor", 18) } },
            new() { Id = "eps_engines", Station = 4, Page = "Rete EPS", Group = "eps", Summary = "Propulsori", Min = -3, Max = 3, Step = 1, Scale = 3, Label = "Motori",
                Effects = new[] { E("ThrustOutputMax", 18), E("ManeuverThrustOutputMax", 18), E("InertiaThrustOutputMax", 18) } },
            new() { Id = "eps_sensors", Station = 4, Page = "Rete EPS", Group = "eps", Summary = "Rilevamento e difesa dati", Min = -3, Max = 3, Step = 1, Scale = 3, Label = "Sensori e computer",
                Effects = new[] { E("EMDetection", 30), E("CyberDefenseRating", 24) } },
            new() { Id = "eps_warp", Station = 4, Page = "Rete EPS", Group = "eps", Summary = "Carica del salto", Min = -3, Max = 3, Step = 1, Scale = 3, Label = "Curvatura",
                Effects = new[] { E("WarpChargeRate", 36) } },
            new() { Id = "eps_life", Station = 4, Page = "Rete EPS", Group = "eps", Summary = "Riciclo dell'ossigeno", Min = -3, Max = 3, Step = 1, Scale = 3, Label = "Supporto vitale",
                Effects = new[] { E("OxygenRefillRate", 45) } },
            new() { Id = "plt_dampers", Station = 1, Label = "Smorzatori inerziali", Help = "Frenate e inversioni più pronte, spinta leggermente ridotta.",
                Effects = new[] { E("InertiaThrustOutputMax", 40), E("ThrustOutputMax", -10) } },
        };
        // Bilancio dei gruppi: somma massima dei valori dei comandi del gruppo (0 = ogni potenziamento va compensato).
        public Dictionary<string, float> GroupBudgets = new() { ["eps"] = 0 };
        public CoreConfig Core = new();
        public ScienceConfig Science = new();
        public WeaponsConfig Weapons = new();
        public PilotConfig Pilot = new();
        public CaptainConfig Captain = new();
        public BotsConfig Bots = new();
        public DamageConfig Damage = new();
        public SensorsConfig Sensors = new();
        public EngageConfig Engage = new();
        public ProtocolsConfig Protocols = new();
        public StationPreset[] Presets =
        {
            new() { Name = "Crociera", Help = "Tutti i comandi a regime nominale.", Alert = 0 },
            new() { Name = "Allarme rosso", Help = "Scudi, scafo e armi al massimo.", Alert = 2,
                Values = new() { ["sci_modulation"] = 60, ["eng_integrity"] = 50, ["eng_overdrive"] = 40, ["wep_tuning"] = 50, ["wep_cooling"] = 50, ["plt_dampers"] = 50 } },
            new() { Name = "Corsa silenziosa", Help = "Emissioni al minimo, sensori passivi.", Alert = 1,
                Values = new() { ["sci_silent"] = 100, ["sci_gain"] = 0, ["eng_overdrive"] = 0 } },
            new() { Name = "Fuga", Help = "Tutto ai motori e alla curvatura.", Alert = 1,
                Values = new() { ["plt_trim"] = 100, ["eng_overdrive"] = 70, ["eng_warpgeom"] = -100, ["sci_modulation"] = -50 } },
            new() { Name = "Esplorazione", Help = "Sensori al massimo e salti lunghi.", Alert = 0,
                Values = new() { ["sci_gain"] = 100, ["eng_warpgeom"] = 80, ["eng_lifesupport"] = 50 } },
        };
        static StationEffect E(string stat, float pct) => new() { Stat = stat, Percent = pct };
    }

    public static class Stations
    {
        public const byte EventCode = 177;
        public static StationsConfig Cfg = new();
        // nave -> (comando -> valore)
        static readonly Dictionary<int, Dictionary<string, float>> state = new();
        static readonly Dictionary<string, (Func<PLShipStats, float> get, Action<PLShipStats, float> set)> stats = new();
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static long nextSync;
        // sottosistemi soggetti a danno, nello stesso ordine dei canali EPS
        public static readonly string[] SysIds = { "shields", "weapons", "engines", "sensors", "warp", "life" };
        public static readonly string[] SysNames = { "Scudi", "Armi", "Motori", "Sensori", "Curvatura", "Supporto vitale" };
        static readonly string[][] SysStats =
        {
            new[] { "ShieldsMax", "ShieldsChargeRateMax" }, new[] { "TurretDamageFactor", "TurretChargeFactor" },
            new[] { "ThrustOutputMax", "ManeuverThrustOutputMax", "InertiaThrustOutputMax" }, new[] { "EMDetection", "CyberDefenseRating" },
            new[] { "WarpChargeRate" }, new[] { "OxygenRefillRate" },
        };
        public static readonly string[] SysShort = { "Scudi", "Armi", "Motori", "Sensori", "Curvatura", "Vitale" };
        public static readonly string[] BandNames = { "CORTA", "MEDIA", "LUNGA" };
        public static readonly string[] ProtocolNames = { "SQUADRE DANNI", "FUOCO MIRATO", "SCANSIONE PRIORITARIA", "DISCIPLINA TERMICA" };
        public static readonly List<string> Journal = new();
        // ultimo messaggio del server, mostrato a schermo per qualche secondo
        public static string Notice = "", NoticeTitle = ""; public static float NoticeUntil;
        static void ShowNotice(string title, string text) { NoticeTitle = title; Notice = text; NoticeUntil = Time.realtimeSinceStartup + 10f; }

        // Messaggio a tutto l'equipaggio, a schermo: compare qui e viene inviato agli altri giocatori.
        public static void Announce(string title, string text)
        {
            ShowNotice(title, text);
            try { if (PhotonNetwork.inRoom) PhotonNetwork.RaiseEvent(EventCode, (Il2CppSystem.Object)("note|" + title.Replace("|", " ") + "|" + text), true, new RaiseEventOptions { Receivers = ReceiverGroup.Others }); }
            catch { }
        }
        static readonly JsonSerializerOptions json = new()
        {
            IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static void Load(string dir)
        {
            string path = Path.Combine(dir, "melchior.stations.json");
            try
            {
                if (File.Exists(path)) Cfg = JsonSerializer.Deserialize<StationsConfig>(File.ReadAllText(path), json) ?? new StationsConfig();
                else File.WriteAllText(path, JsonSerializer.Serialize(Cfg, json));
            }
            catch (Exception e) { Plugin.L.LogError("melchior.stations.json non valido, uso i valori predefiniti: " + e.Message); Cfg = new StationsConfig(); }
            stats.Clear();
            foreach (var st in Cfg.Controls.SelectMany(c => c.Effects).Select(e => e.Stat).Concat(SysStats.SelectMany(x => x)).Distinct())
            {
                var acc = Rarity.Accessor(st);
                if (acc.get == null) Plugin.L.LogWarning($"Stazioni: statistica '{st}' sconosciuta, effetto ignorato.");
                else stats[st] = acc;
            }
            Plugin.L.LogInfo($"Stazioni: {Cfg.Controls.Length} comandi, {Cfg.Presets.Length} assetti ({(Cfg.Enabled ? "attive" : "disattivate")}).");
        }

        public static StationControl Find(string id) => Cfg.Controls.FirstOrDefault(c => c.Id == id);
        public static float Get(int ship, string id) => state.TryGetValue(ship, out var d) && d.TryGetValue(id, out var v) ? v : 0f;
        public static bool Any => state.Count > 0;

        public static PLShipInfo MyShip => PLNetworkManager.Instance?.LocalPlayer?.StartingShip;
        public static int MyClass { get { try { return PLNetworkManager.Instance?.LocalPlayer?.GetClassID() ?? -1; } catch { return -1; } } }
        public static bool CanEdit(int station) { if (!Cfg.RoleLocked) return true; int c = MyClass; return c == 0 || c == station; }

        public static bool IsDynamic(string id) => id.StartsWith("core_") || id.StartsWith("scx_") || id.StartsWith("wpx_") || id.StartsWith("plx_") || id.StartsWith("cpx_") || id.StartsWith("dmx_") || id.StartsWith("drx_");

        static void SetLocal(int ship, string id, float v)
        {
            if (!IsDynamic(id)) { var c = Find(id); if (c == null) return; v = Math.Clamp(v, c.Min, c.Max); }
            if (!state.TryGetValue(ship, out var d)) state[ship] = d = new Dictionary<string, float>();
            if (Math.Abs(v) < 0.001f) d.Remove(id); else d[id] = v;
            if (d.Count == 0) state.Remove(ship);
        }

        // I successi dell'equipaggio alle stazioni alimentano l'iniziativa del capitano.
        public static void Initiative(int ship, float gain)
        {
            if (!Cfg.Captain.Enabled) return;
            float ini = Get(ship, "cpx_ini"); if (ini <= 0) ini = Cfg.Captain.Start;
            Set(ship, "cpx_ini", Math.Clamp(ini + gain, 0.01f, 100f));
        }

        // Giornale di bordo: gli eventi importanti della nave, condivisi con tutto l'equipaggio.
        public static void LogEvent(int ship, string text)
        {
            var me = MyShip; if (me == null || me.ShipID != ship) return;
            AddJournal(text);
            try { if (PhotonNetwork.inRoom) PhotonNetwork.RaiseEvent(EventCode, (Il2CppSystem.Object)("log|" + ship + "|" + text), true, new RaiseEventOptions { Receivers = ReceiverGroup.Others }); }
            catch { }
        }
        static void AddJournal(string text) { Journal.Add(DateTime.Now.ToString("HH:mm:ss") + "   " + text); if (Journal.Count > 60) Journal.RemoveAt(0); }

        static readonly Dictionary<(int, int), float> downLogged = new();
        public static int Bits(int v) { int n = 0; while (v != 0) { n += v & 1; v >>= 1; } return n; }
        public static bool Protocol(int ship, int index) => Cfg.Protocols.Enabled && (((int)Get(ship, "cpx_proto") >> index) & 1) != 0;

        // Distanza dal bersaglio agganciato (-1 se non c'è).
        public static float TargetDistance(PLShipInfoBase ship)
        {
            try
            {
                var t = ship?.TargetShip; if (t == null || t.HasBeenDestroyed || ship.Exterior == null || t.Exterior == null) return -1f;
                return Vector3.Distance(ship.Exterior.transform.position, t.Exterior.transform.position);
            }
            catch { return -1f; }
        }
        public static int BandOf(float dist) => dist < 0 ? 0 : dist <= Cfg.Engage.ShortMax ? 1 : dist >= Cfg.Engage.LongMin ? 3 : 2;

        // Un colpo allo scafo: tiro mirato, punto debole, manovra evasiva; sull'host guasta anche un sottosistema.
        public static void HullHit(PLShipStats vs, PLShipInfoBase atk, ref float dmg)
        {
            var dg = Cfg.Damage; if (dmg <= 0) return;
            var victim = vs.Ship; if (victim == null) return;
            int vid = victim.ShipID, sub = 0; float factor = 1f; bool ours = false;
            if (atk != null && state.Count > 0)
            {
                int aid = atk.ShipID;
                if ((int)Get(aid, "scx_tgt") == vid + 1)
                {
                    float scan = Get(aid, "scx_scan"); int want = (int)Get(aid, "wpx_sub");
                    if (scan >= 100f) dmg *= 1f + Cfg.Sensors.WeakPointDamage / 100f;
                    if (dg.Enabled && want > 0 && want <= SysIds.Length && scan >= Cfg.Sensors.SubsystemAt)
                    { sub = want; factor = dg.FocusFactor * (Protocol(aid, 1) ? 1f + Cfg.Protocols.FocusBoost / 100f : 1f); dmg *= 1f - dg.FocusHullPenalty / 100f; }
                }
                var me = MyShip; ours = me != null && me.ShipID == aid;
            }
            if (state.Count > 0 && Get(vid, "plx_evade") > 0) dmg *= 1f - Cfg.Pilot.EvadeIncoming / 100f;
            if (!dg.Enabled) return;
            bool host = false; try { host = PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { }
            float max = vs.HullMax; if (!host || max <= 1f) return;
            float pts = dmg / max * 100f * dg.PointsPerHullPercent * factor; if (pts < 0.05f) return;
            if (sub == 0) sub = 1 + simRng.Next(SysIds.Length);
            string key = "dmx_" + SysIds[sub - 1]; float before = Get(vid, key), after = Math.Min(100f, before + pts);
            SetLocal(vid, key, after);
            if (before < 50f && after >= 50f && after < 100f) LogEvent(vid, SysNames[sub - 1] + ": danni gravi");
            if (before < 100f && after >= 100f && Time.time - (downLogged.TryGetValue((vid, sub), out var lt) ? lt : -999f) > 60f)
            {
                downLogged[(vid, sub)] = Time.time;
                LogEvent(vid, SysNames[sub - 1] + ": fuori uso");
                if (ours) { int mid = MyShip.ShipID; LogEvent(mid, "Bersaglio: " + SysNames[sub - 1].ToLowerInvariant() + " fuori uso"); Service.Add("armi.sottosistemi", 1); Initiative(mid, Cfg.Captain.GainPerSuccess); }
            }
        }

        public static float GroupSum(int ship, string group) => Cfg.Controls.Where(c => c.Group == group).Sum(c => Get(ship, c.Id));
        public static float GroupBudget(string group) => Cfg.GroupBudgets.TryGetValue(group, out var b) ? b : float.MaxValue;

        // Imposta un comando rispettando blocchi e bilanci. Restituisce un messaggio se la richiesta è rifiutata.
        public static string TrySet(int ship, string id, float v)
        {
            var c = Find(id); if (c == null) return "comando sconosciuto";
            if (Get(ship, "core_lock") > 0 && v != 0 && (c.Group == "eps" || c.Id == "eng_overdrive")) return "Nucleo in arresto di sicurezza";
            v = Math.Clamp(v, c.Min, c.Max);
            if (c.Group.Length > 0 && v > Get(ship, id) && GroupSum(ship, c.Group) - Get(ship, id) + v > GroupBudget(c.Group) + 0.001f)
                return "Bilancio esaurito: togli potenza a un altro canale";
            Set(ship, id, v); return null;
        }

        public static void SetQuiet(int ship, string id, float v) => SetLocal(ship, id, v); // senza invio immediato: lo ripete il ciclo dell'host

        public static void Set(int ship, string id, float v) { SetLocal(ship, id, v); Broadcast(ship); if (!IsDynamic(id)) Service.Add("stazioni.regolazioni", 1); }

        public static void ApplyPreset(int ship, StationPreset p)
        {
            foreach (var c in Cfg.Controls) SetLocal(ship, c.Id, p.Values.TryGetValue(c.Id, out var v) ? v : 0f);
            Broadcast(ship); Service.Add("stazioni.assetti", 1);
            try
            {
                if (p.Alert >= 0 && PhotonNetwork.isMasterClient && PLServer.Instance != null) PLServer.Instance.MasterClientShipSetAlertLevel(ship, p.Alert);
            }
            catch (Exception e) { Plugin.L.LogWarning("Stato d'allarme non impostato: " + e.Message); }
        }

        // Effetto complessivo sulle statistiche di una nave: statistica -> moltiplicatore.
        public static Dictionary<string, float> Multipliers(int ship)
        {
            var m = new Dictionary<string, float>();
            if (!state.TryGetValue(ship, out var d)) return m;
            void Mul(string stat, float f) { f = Math.Max(0.05f, f); m[stat] = m.TryGetValue(stat, out var cur) ? cur * f : f; }
            foreach (var (id, v) in d)
            {
                var c = Find(id); if (c == null) continue;
                foreach (var e in c.Effects) Mul(e.Stat, 1f + e.Percent / 100f * (v / (c.Scale == 0 ? 100f : c.Scale)));
            }
            float D(string key) => d.TryGetValue(key, out var x) ? x : 0f;
            if (Cfg.Damage.Enabled)
            {
                int byp = D("dmx_byp") > 0 ? (int)D("dmx_bypsys") : 0;
                for (int i = 0; i < SysIds.Length; i++)
                {
                    float dm = D("dmx_" + SysIds[i]); if (dm <= 0 || byp == i + 1) continue;
                    foreach (var stat in SysStats[i]) Mul(stat, 1f - dm / 100f * Cfg.Damage.MaxPenalty / 100f);
                }
            }
            if (Cfg.Protocols.Enabled && D("cpx_proto") > 0)
            {
                int pr = (int)D("cpx_proto"); var pc = Cfg.Protocols;
                if ((pr & 1) != 0) Mul("TurretChargeFactor", 1f - pc.RepairChargePenalty / 100f);
                if ((pr & 2) != 0) Mul("ShieldsChargeRateMax", 1f - pc.FocusRechargePenalty / 100f);
                if ((pr & 4) != 0) Mul("EMSignature", 1f + pc.ScanSignature / 100f);
                if ((pr & 8) != 0) Mul("ThrustOutputMax", 1f - pc.ThermalThrustPenalty / 100f);
            }
            if (Cfg.Engage.Enabled && D("plx_band") > 0 && D("plx_pos") > 0)
            {
                var en = Cfg.Engage; int band = (int)D("plx_band"); float pos = D("plx_pos") / 100f;
                if (band == 1) { Mul("TurretDamageFactor", 1f + en.ShortDamage / 100f * pos); Mul("ShieldsChargeRateMax", 1f - en.ShortRechargePenalty / 100f * pos); }
                else if (band == 2) { Mul("TurretChargeFactor", 1f + en.MidCharge / 100f * pos); Mul("ManeuverThrustOutputMax", 1f + en.MidManeuver / 100f * pos); }
                else if (band == 3) { Mul("ShieldsChargeRateMax", 1f + en.LongRecharge / 100f * pos); Mul("EMSignature", 1f - en.LongSignatureCut / 100f * pos); Mul("TurretDamageFactor", 1f - en.LongDamagePenalty / 100f * pos); }
            }
            var k = Cfg.Core;
            if (k.Enabled)
            {
                float stress = d.TryGetValue("core_stress", out var st) ? st : 0f;
                if (stress > k.WarnAt) Mul("ReactorTempMax", 1f - (stress - k.WarnAt) * k.TempPenaltyPerPoint / 100f);
                if (stress >= k.CriticalAt) Mul("ReactorOutputMax", 1f - k.CriticalOutputPenalty / 100f);
                if (d.TryGetValue("core_align", out var al) && al > 0) Mul("ReactorOutputMax", 1f + k.AlignBonus / 100f);
            }
            var sc = Cfg.Science;
            if (sc.Enabled)
            {
                float V(string key) => d.TryGetValue(key, out var x) ? x : 0f;
                if (V("scx_band") > 0) Mul("ShieldsMax", 1f + (V("scx_eff") - 50f) / 100f * sc.HarmonicSwing / 100f);
                if (V("scx_dip") > 0) Mul("ShieldsChargeRateMax", 1f - sc.RotateDipPercent / 100f);
                if (V("scx_sol") > 0) { Mul("TurretDamageFactor", 1f + sc.SolutionDamage / 100f); Mul("TurretChargeFactor", 1f + sc.SolutionCharge / 100f); }
                if (V("scx_ping") > 0) { Mul("EMDetection", 1f + sc.PingDetection / 100f); Mul("EMSignature", 1f + sc.PingSignature / 100f); }
            }
            var w = Cfg.Weapons;
            if (w.Enabled)
            {
                float V(string key) => d.TryGetValue(key, out var x) ? x : 0f;
                int doc = (int)V("wpx_doc"); float q = V("wpx_q") / 100f;
                if (doc == 1) Mul("TurretDamageFactor", 1f + w.PrecisionDamage / 100f * q);
                else if (doc == 2) Mul("TurretChargeFactor", 1f + w.RapidCharge / 100f * q);
                else if (doc == 3) { Mul("TurretCoolingSpeedFactor", 1f + w.SustainCooling / 100f * q); Mul("TurretDamageFactor", 1f - w.SustainDamagePenalty / 100f); }
                if (V("wpx_burst") > 0) Mul("TurretDamageFactor", 1f + V("wpx_burstp") / 100f);
                if (V("wpx_jam") > 0) Mul("TurretChargeFactor", 1f - w.JamChargePenalty / 100f);
            }
            var cp = Cfg.Captain;
            if (cp.Enabled && d.TryGetValue("cpx_dir", out var dir) && dir > 0)
            {
                if (dir == 1) { Mul("TurretDamageFactor", 1f + cp.BattleDamage / 100f); Mul("ShieldsMax", 1f + cp.BattleShields / 100f); }
                else if (dir == 2) { Mul("HullArmor", 1f + cp.RepairArmor / 100f); Mul("ShieldsChargeRateMax", 1f + cp.RepairRecharge / 100f); }
                else if (dir == 3) Mul("ReactorOutputMax", 1f + cp.EfficiencyOutput / 100f);
                else if (dir == 4) { Mul("EMSignature", 1f - cp.SilenceSignature / 100f); Mul("EMDetection", 1f + cp.SilenceDetection / 100f); }
            }
            var pl = Cfg.Pilot;
            if (pl.Enabled && d.Keys.Any(x => x.StartsWith("plx_")))
            {
                float V(string key) => d.TryGetValue(key, out var x) ? x : 0f;
                float off = Math.Abs(V("plx_trim")) / 100f;
                Mul("ThrustOutputMax", 1f + (pl.TrimBonus - (pl.TrimBonus + pl.TrimPenalty) * off) / 100f);
                if (V("plx_evade") > 0) { Mul("ManeuverThrustOutputMax", 1f + pl.EvadeManeuver / 100f); Mul("InertiaThrustOutputMax", 1f + pl.EvadeInertia / 100f); Mul("TurretDamageFactor", 1f - pl.EvadeDamagePenalty / 100f); }
                if (V("plx_burn") > 0) { Mul("ThrustOutputMax", 1f + pl.BurnThrust / 100f); Mul("EMSignature", 1f + pl.BurnSignature / 100f); }
                if (V("plx_drift") > 0) { Mul("ThrustOutputMax", 1f - pl.DriftThrustPenalty / 100f); Mul("ManeuverThrustOutputMax", 1f - pl.DriftManeuverPenalty / 100f); Mul("EMSignature", 1f - pl.DriftSignatureCut / 100f); }
                if (V("plx_route") > 0) { float q = V("plx_routeq") / 100f; Mul("WarpChargeRate", 1f + pl.RouteCharge / 100f * q); Mul("WarpRange", 1f + pl.RouteRange / 100f * q); }
            }
            return m;
        }

        public static void Apply(PLShipStats s)
        {
            if (!Cfg.Enabled || state.Count == 0) return;
            var ship = s.Ship; if (ship == null) return;
            foreach (var (stat, f) in Multipliers(ship.ShipID))
                if (stats.TryGetValue(stat, out var acc)) acc.set(s, acc.get(s) * f);
        }

        // ---- rete
        static string Encode(int ship)
        {
            var sb = new StringBuilder().Append(ship).Append('|');
            if (state.TryGetValue(ship, out var d))
                foreach (var (id, v) in d) sb.Append(id).Append('=').Append(v.ToString("0.##", CultureInfo.InvariantCulture)).Append(';');
            return sb.ToString();
        }

        static void Broadcast(int ship)
        {
            try
            {
                if (!PhotonNetwork.inRoom) return;
                PhotonNetwork.RaiseEvent(EventCode, (Il2CppSystem.Object)Encode(ship), true, new RaiseEventOptions { Receivers = ReceiverGroup.Others });
            }
            catch (Exception e) { Plugin.L.LogWarning("Regolazioni non inviate: " + e.Message); }
        }

        public static void Receive(string msg)
        {
            if (msg.StartsWith("msg|"))
            {
                string text = msg[4..].Trim(); if (text.Length == 0) return;
                AddJournal("Server: " + text); ShowNotice("MESSAGGIO DAL SERVER", text);
                Plugin.L.LogInfo("[server] " + text); Panels.RefreshAll(); return;
            }
            if (msg.StartsWith("note|"))
            {
                var np = msg.Split('|', 3); if (np.Length == 3) ShowNotice(np[1], np[2]); return;
            }
            if (msg.StartsWith("log|"))
            {
                var lp = msg.Split('|', 3); var me = MyShip;
                if (lp.Length == 3 && me != null && int.TryParse(lp[1], out int ls) && ls == me.ShipID) { AddJournal(lp[2]); Panels.RefreshAll(); }
                return;
            }
            int bar = msg.IndexOf('|'); if (bar <= 0 || !int.TryParse(msg.AsSpan(0, bar), out int ship)) return;
            state.Remove(ship);
            foreach (var part in msg[(bar + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && float.TryParse(part.AsSpan(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) SetLocal(ship, part[..eq], v);
            }
            Panels.RefreshAll();
        }

        static long nextSim; static readonly System.Random simRng = new();

        // L'host simula il nucleo una volta al secondo e ripete lo stato: chi entra dopo si allinea da solo.
        public static void Tick()
        {
            long now = clock.ElapsedMilliseconds;
            bool host = false; try { host = PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { }
            if (host && Cfg.Core.Enabled && now >= nextSim) { nextSim = now + 1000; try { SimulateCores(); } catch (Exception e) { Plugin.L.LogWarning("Nucleo: " + e.Message); } }
            if (state.Count == 0 || now < nextSync) return;
            nextSync = now + (state.Values.Any(d => d.Keys.Any(IsDynamic)) ? 2000 : 6000);
            if (host) foreach (var ship in state.Keys.ToArray()) BroadcastRaw(ship);
        }

        static void SimulateCores()
        {
            var k = Cfg.Core;
            var own = MyShip; int ownId = own != null ? own.ShipID : -1;
            foreach (var ship in state.Keys.ToArray())
            {
                // le navi distrutte o uscite dal settore non hanno più uno stato da simulare
                if (ship != ownId)
                {
                    PLShipInfoBase info = null; try { info = PLEncounterManager.Instance?.GetShipFromID(ship); } catch { }
                    if (info == null || info.HasBeenDestroyed) { state.Remove(ship); Broadcast(ship); continue; }
                }
                SimulateDamage(ship);
                if (ship == ownId) { SimulateScan(own, ship); SimulateEngage(own, ship); }
                float stress = Get(ship, "core_stress"), s0 = stress, lockLeft = Get(ship, "core_lock"), align = Get(ship, "core_align");
                float load = Get(ship, "eng_overdrive") / 100f * k.PerOverdrive + Cfg.Controls.Where(c => c.Group == "eps").Sum(c => Math.Max(0f, Get(ship, c.Id))) * k.PerEpsNotch;
                stress = Math.Clamp(stress + load - k.Cooling - ((int)Get(ship, "cpx_dir") == 3 ? Cfg.Captain.EfficiencyCooling : 0f) - (Protocol(ship, 3) ? Cfg.Protocols.ThermalCooling : 0f), 0f, 100f);
                if (lockLeft > 0) SetLocal(ship, "core_lock", Math.Max(0f, lockLeft - 1));
                if (align > 0) SetLocal(ship, "core_align", Math.Max(0f, align - 1));
                if (stress >= 100f && lockLeft <= 0)
                {
                    // arresto automatico: tutto il carico viene staccato e il nucleo resta bloccato
                    SetLocal(ship, "eng_overdrive", 0);
                    foreach (var c in Cfg.Controls.Where(c => c.Group == "eps")) SetLocal(ship, c.Id, 0);
                    SetLocal(ship, "core_lock", k.ScramLockSeconds); stress = 70f; LogEvent(ship, "Nucleo: arresto di sicurezza");
                    if (MyShip != null && MyShip.ShipID == ship) Service.Add("ingegneria.arresti", 1);
                    Plugin.L.LogInfo($"[nucleo] arresto di sicurezza sulla nave {ship}");
                }
                if (Math.Abs(stress - s0) > 0.0001f) SetLocal(ship, "core_stress", stress);
                // capitano: l'iniziativa si rigenera, gli ordini la consumano; a zero l'equipaggio è affaticato
                if (Cfg.Captain.Enabled && Get(ship, "cpx_ini") > 0)
                {
                    var c = Cfg.Captain; float ini = Get(ship, "cpx_ini"), tired = Get(ship, "cpx_tired"); int dirNow = (int)Get(ship, "cpx_dir");
                    if (tired > 0) SetLocal(ship, "cpx_tired", Math.Max(0f, tired - 1));
                    ini += c.Regen - (dirNow > 0 ? c.OrderDrain : 0f) - (Cfg.Protocols.Enabled ? Bits((int)Get(ship, "cpx_proto")) * Cfg.Protocols.RegenCost : 0f);
                    if (ini <= 0.01f && dirNow > 0) { SetLocal(ship, "cpx_dir", 0); SetLocal(ship, "cpx_tired", c.TiredSeconds); }
                    SetLocal(ship, "cpx_ini", Math.Clamp(ini, 0.01f, 100f));
                }
                // pilota: timer delle manovre e deriva degli ugelli (più nervosa durante le manovre)
                foreach (var key in new[] { "plx_evade", "plx_burn", "plx_route" }) { float left = Get(ship, key); if (left > 0) SetLocal(ship, key, Math.Max(0f, left - 1)); }
                if (Cfg.Pilot.Enabled && state.TryGetValue(ship, out var pd) && pd.Keys.Any(x => x.StartsWith("plx_")))
                {
                    float busy = Get(ship, "plx_evade") > 0 || Get(ship, "plx_burn") > 0 ? 2.5f : 1f;
                    float tr = Get(ship, "plx_trim") + (float)(simRng.NextDouble() * 2 - 1) * Cfg.Pilot.TrimDrift * busy;
                    SetLocal(ship, "plx_trim", Math.Clamp(Math.Abs(tr) < 0.01f ? 0.01f : tr, -100f, 100f));
                }
                // armi: la soluzione di tiro matura, raffica e inceppamento scadono
                foreach (var key in new[] { "wpx_burst", "wpx_jam" }) { float left = Get(ship, key); if (left > 0) SetLocal(ship, key, Math.Max(0f, left - 1)); }
                if (Get(ship, "wpx_burst") <= 0 && Get(ship, "wpx_burstp") > 0) SetLocal(ship, "wpx_burstp", 0);
                if (Get(ship, "wpx_doc") > 0) { float q = Get(ship, "wpx_q"); if (q < 100f) SetLocal(ship, "wpx_q", Math.Min(100f, q + Cfg.Weapons.QualityPerSecond)); }
                // scienza: timer e adattamento del nemico all'armonica degli scudi
                foreach (var key in new[] { "scx_dip", "scx_sol", "scx_ping" }) { float left = Get(ship, key); if (left > 0) SetLocal(ship, key, Math.Max(0f, left - 1)); }
                if (Get(ship, "scx_band") > 0)
                {
                    int alert = 1; try { var mine = MyShip; if (mine != null && mine.ShipID == ship) alert = mine.m_AlertLevel; } catch { }
                    float rate = alert >= 2 ? Cfg.Science.DecayRed : alert == 1 ? Cfg.Science.DecayYellow : Cfg.Science.DecayCalm;
                    float eff = Get(ship, "scx_eff"); if (eff > 0) SetLocal(ship, "scx_eff", Math.Max(0f, eff - rate));
                }
            }
        }

        static void SimulateDamage(int ship)
        {
            var dg = Cfg.Damage; if (!dg.Enabled) return;
            int t1 = (int)Get(ship, "dmx_t1"), t2 = (int)Get(ship, "dmx_t2");
            float boost = Protocol(ship, 0) ? 1f + Cfg.Protocols.RepairBoost / 100f : 1f;
            for (int i = 0; i < SysIds.Length; i++)
            {
                float dm = Get(ship, "dmx_" + SysIds[i]); if (dm <= 0) continue;
                int teams = (t1 == i + 1 ? 1 : 0) + (t2 == i + 1 ? 1 : 0);
                float nd = Math.Max(0f, dm - dg.PassiveRepair - teams * dg.RepairPerTeam * boost);
                SetLocal(ship, "dmx_" + SysIds[i], nd);
                if (nd <= 0 && teams > 0)
                {
                    if (t1 == i + 1) SetLocal(ship, "dmx_t1", 0); if (t2 == i + 1) SetLocal(ship, "dmx_t2", 0);
                    LogEvent(ship, "Ingegneria: " + SysNames[i].ToLowerInvariant() + " riparato");
                    var me = MyShip; if (me != null && me.ShipID == ship) { Service.Add("ingegneria.riparazioni", 1); Initiative(ship, Cfg.Captain.GainPerSuccess); }
                }
            }
            float byp = Get(ship, "dmx_byp");
            if (byp > 0) { SetLocal(ship, "dmx_byp", Math.Max(0f, byp - 1)); if (byp <= 1) SetLocal(ship, "dmx_bypsys", 0); }
        }

        static void SimulateScan(PLShipInfo mine, int ship)
        {
            var sn = Cfg.Sensors; if (!sn.Enabled) return;
            PLShipInfoBase tgt = null; try { tgt = mine.TargetShip; } catch { }
            int tid = tgt != null && !tgt.HasBeenDestroyed ? tgt.ShipID + 1 : 0;
            float scan = Get(ship, "scx_scan");
            if ((int)Get(ship, "scx_tgt") != tid)
            {
                SetLocal(ship, "scx_tgt", tid); SetLocal(ship, "scx_scan", 0); scan = 0;
                if (Get(ship, "wpx_sub") > 0) SetLocal(ship, "wpx_sub", 0);
            }
            if (tid == 0 || Get(ship, "scx_scanon") <= 0 || scan >= 100f) return;
            float rate = sn.ScanPerSecond * (1f + Get(ship, "sci_gain") / 100f * sn.GainBoost / 100f + Math.Max(-2f, Get(ship, "eps_sensors")) * sn.EpsBoost / 100f)
                * (Protocol(ship, 2) ? 1f + Cfg.Protocols.ScanBoost / 100f : 1f) * (1f - Get(ship, "dmx_sensors") / 100f * 0.7f);
            float ns = Math.Min(100f, scan + Math.Max(0.2f, rate));
            SetLocal(ship, "scx_scan", ns);
            if (scan < sn.SubsystemAt && ns >= sn.SubsystemAt && ns < 100f) LogEvent(ship, "Scienza: sistemi del bersaglio identificati");
            if (ns >= 100f) { LogEvent(ship, "Scienza: punto debole del bersaglio individuato"); Service.Add("scienza.scansioni", 1); Initiative(ship, Cfg.Captain.GainPerSuccess); }
        }

        static void SimulateEngage(PLShipInfo mine, int ship)
        {
            var en = Cfg.Engage; int band = (int)Get(ship, "plx_band"); if (!en.Enabled || band <= 0) return;
            bool inb = BandOf(TargetDistance(mine)) == band; float pos = Get(ship, "plx_pos");
            float np = Math.Clamp(pos + (inb ? en.PosGain : -en.PosLoss), 0f, 100f);
            if (Math.Abs(np - pos) > 0.001f) SetLocal(ship, "plx_pos", np);
            if (pos < 100f && np >= 100f) { LogEvent(ship, "Pilota: posizione d'ingaggio consolidata"); Service.Add("pilota.posizioni", 1); Initiative(ship, Cfg.Captain.GainPerSuccess); }
        }

        static void BroadcastRaw(int ship) => Broadcast(ship);

        public static void Clear() { state.Clear(); Journal.Clear(); }
    }

    [HarmonyPatch(typeof(NetworkingPeer), nameof(NetworkingPeer.OnEvent))]
    static class StationsNet
    {
        static void Prefix(ExitGames.Client.Photon.EventData photonEvent)
        {
            if (photonEvent.Code != Stations.EventCode) return;
            try
            {
                var data = photonEvent[245]; // 245 = contenuto dell'evento
                if (data != null) Stations.Receive(Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(data.Pointer));
            }
            catch (Exception e) { Plugin.L.LogWarning("Regolazioni ricevute non valide: " + e.Message); }
        }
    }

    // Console operativa: F8 (o comando di console "con"). Si usa col mouse.
    public static class StationConsole
    {
        public static bool Show; public static int Tab = 4;
        static GUIStyle btn, btnOn; static Texture2D texBtn, texOn, texBar, texBarNeg, texTrack;

        static Texture2D Flat(Color c) { var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave }; t.SetPixel(0, 0, c); t.Apply(); return t; }

        static void Init()
        {
            texBtn = Flat(new Color(0.16f, 0.19f, 0.27f, 1f)); texOn = Flat(new Color(0.95f, 0.65f, 0.35f, 1f));
            texBar = Flat(new Color(0.62f, 0.71f, 1f, 1f)); texBarNeg = Flat(new Color(0.77f, 0.54f, 0.98f, 1f)); texTrack = Flat(new Color(1f, 1f, 1f, 0.08f));
            btn = new GUIStyle(MelchiorHud.Small) { alignment = TextAnchor.MiddleCenter, fontSize = 13 };
            btn.normal.background = btn.hover.background = btn.active.background = texBtn; btn.normal.textColor = btn.hover.textColor = btn.active.textColor = Color.white;
            btnOn = new GUIStyle(btn); btnOn.normal.background = btnOn.hover.background = btnOn.active.background = texOn;
            btnOn.normal.textColor = btnOn.hover.textColor = btnOn.active.textColor = new Color(0.05f, 0.06f, 0.08f);
        }

        static PLUIScreen panel;

        // Aperta dal pulsante di un pannello di stazione: resta legata a quel pannello.
        public static void OpenAt(int station, PLUIScreen screen) { Tab = station; panel = screen; Show = true; }
        public static void Close() { Show = false; panel = null; }

        public static void Tick()
        {
            if (!Show) return;
            try
            {
                if (panel == null) { if (!Plugin.DevConsoleOn.Value) Close(); return; } // senza pannello solo in collaudo
                var pawn = PLNetworkManager.Instance?.MyLocalPawn;
                if (pawn == null || Vector3.Distance(pawn.transform.position, panel.transform.position) > Stations.Cfg.PanelMaxDistance) Close();
            }
            catch { Close(); }
        }

        static string Pct(float f) => (f >= 1 ? "+" : "") + ((f - 1) * 100).ToString("0") + "%";

        public static void Draw()
        {
            if (!Show) return;
            if (btn == null) Init();
            try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
            var ship = Stations.MyShip;
            float w = 1060, h = 660, x = (Screen.width - w) / 2, y = (Screen.height - h) / 2;
            GUI.Box(new Rect(x, y, w, h), "", MelchiorHud.Box);
            GUI.Label(new Rect(x + 24, y + 12, 700, 30), $"<color=#F2A65A>CONSOLE OPERATIVA</color>   {(ship == null ? "nessuna nave" : (string)ship.ShipName)}", MelchiorHud.Title);
            if (GUI.Button(new Rect(x + w - 110, y + 14, 86, 26), "Chiudi", btn)) { Close(); return; }
            if (ship == null) { GUI.Label(new Rect(x + 24, y + 70, 600, 30), "Sali a bordo di una nave per usare la console.", MelchiorHud.Body); return; }
            int id = ship.ShipID;

            // La stazione è quella del pannello da cui si è aperta la console: nessun cambio di scheda.
            string[] tabs = Stations.Cfg.StationNames;
            GUI.Button(new Rect(x + 24, y + 54, 220, 30), "Stazione: " + tabs[Math.Clamp(Tab, 0, tabs.Length - 1)], btnOn);

            bool can = Stations.CanEdit(Tab);
            float cy = y + 100;
            if (Tab == 0)
            {
                GUI.Label(new Rect(x + 24, cy, 640, 22), "<color=#8b90a0>Assetti di bordo: regolano tutte le stazioni in un colpo e impostano lo stato d'allarme.</color>", MelchiorHud.Small);
                cy += 30;
                foreach (var p in Stations.Cfg.Presets)
                {
                    if (GUI.Button(new Rect(x + 24, cy, 190, 34), p.Name, btn) && can) Stations.ApplyPreset(id, p);
                    GUI.Label(new Rect(x + 226, cy + 8, 440, 22), "<color=#8b90a0>" + p.Help + "</color>", MelchiorHud.Small);
                    cy += 42;
                }
            }
            else
            {
                foreach (var c in Stations.Cfg.Controls.Where(c => c.Station == Tab))
                {
                    float v = Stations.Get(id, c.Id);
                    GUI.Label(new Rect(x + 24, cy, 420, 24), c.Label, MelchiorHud.Body);
                    GUI.Label(new Rect(x + 24, cy + 22, 640, 20), "<color=#8b90a0>" + c.Help + "</color>", MelchiorHud.Small);
                    // barra
                    float bx = x + 70, bw = 430, by = cy + 48; GUI.DrawTexture(new Rect(bx, by, bw, 14), texTrack);
                    float zero = bx + bw * (0 - c.Min) / (c.Max - c.Min), pos = bx + bw * (v - c.Min) / (c.Max - c.Min);
                    if (v > 0) GUI.DrawTexture(new Rect(zero, by, pos - zero, 14), texBar); else if (v < 0) GUI.DrawTexture(new Rect(pos, by, zero - pos, 14), texBarNeg);
                    if (GUI.Button(new Rect(x + 24, by - 6, 38, 26), "-", btn) && can) Stations.Set(id, c.Id, v - c.Step);
                    if (GUI.Button(new Rect(bx + bw + 8, by - 6, 38, 26), "+", btn) && can) Stations.Set(id, c.Id, v + c.Step);
                    if (GUI.Button(new Rect(bx + bw + 52, by - 6, 52, 26), "zero", btn) && can) Stations.Set(id, c.Id, 0);
                    GUI.Label(new Rect(bx + bw + 112, by - 4, 60, 22), v.ToString("+0;-0;0"), MelchiorHud.Body);
                    // effetto corrente del solo comando
                    var parts = c.Effects.Select(e => { float f = 1f + e.Percent / 100f * (v / 100f); string n = Stations.Cfg.StatNames.TryGetValue(e.Stat, out var nn) ? nn : e.Stat;
                        return Math.Abs(v) < 0.001f ? $"<color=#8b90a0>{n} {(e.Percent > 0 ? "(+)" : "(-)")}</color>" : $"<color={(f >= 1 ? "#7CD992" : "#FF8A7A")}>{n} {Pct(f)}</color>"; });
                    GUI.Label(new Rect(x + 24, by + 22, 660, 20), string.Join("   ", parts), MelchiorHud.Small);
                    cy += 104;
                }
            }
            if (!can) GUI.Label(new Rect(x + 24, y + h - 34, 640, 22), "<color=#FF8A7A>Questa stazione non è la tua: solo il suo ufficiale o il capitano possono regolarla.</color>", MelchiorHud.Small);

            // telemetria
            float tx = x + 720; var st = ship.MyStats;
            GUI.Label(new Rect(tx, y + 96, 320, 24), "<color=#9db4ff>TELEMETRIA</color>", MelchiorHud.Body);
            if (st != null)
            {
                var mult = Stations.Multipliers(id); float ty = y + 126;
                foreach (var (stat, name) in Stations.Cfg.StatNames)
                {
                    var acc = Rarity.Accessor(stat); if (acc.get == null) continue;
                    string d = mult.TryGetValue(stat, out var f) && Math.Abs(f - 1) > 0.001f ? $"  <color={(f >= 1 ? "#7CD992" : "#FF8A7A")}>{Pct(f)}</color>" : "";
                    GUI.Label(new Rect(tx, ty, 200, 20), "<color=#8b90a0>" + name + "</color>", MelchiorHud.Small);
                    GUI.Label(new Rect(tx + 196, ty, 130, 20), acc.get(st).ToString("0.##") + d, MelchiorHud.Small);
                    ty += 22;
                }
                GUI.Label(new Rect(tx, ty + 8, 320, 20), $"<color=#8b90a0>Stato d'allarme</color>  {ship.m_AlertLevel}    <color=#8b90a0>Classe</color>  {Stations.MyClass}", MelchiorHud.Small);
            }
        }
    }

    // Con una nostra finestra aperta il gioco deve comportarsi come con un menu aperto: niente visuale col mouse.
    [HarmonyPatch]
    static class OverlayBlocksInput
    {
        public static bool Open => StationConsole.Show || MelchiorHud.ShowPanel || ServicePanel.Show;
        static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            typeof(PLNetworkManager).GetMethods().Where(m => m.Name is "IsActiveMenuOpen" or "IsActiveMenuOpenNow");
        static void Postfix(ref bool __result) { if (Open) __result = true; }
    }

    [HarmonyPatch(typeof(PLMouseLook), "Update")]
    static class OverlayBlocksLook
    {
        static bool Prefix() => !OverlayBlocksInput.Open;
    }

    // Pagina "REGOLAZIONI" disegnata dentro gli schermi fisici delle stazioni, con l'interfaccia nativa del gioco
    // (pannelli, etichette e pulsanti di PLUIScreen). Si apre dal pulsante REG nella barra del titolo dello schermo.
    // Pagina delle stazioni disegnata dentro gli schermi fisici, con un'impaginazione a griglia in stile LCARS:
    // riquadri piatti, colonne a larghezza fissa (il testo si restringe per starci), barre grafiche vere.
    sealed class PanelPage
    {
        public PLUIScreen Screen; public int Station; public UISprite Page; public bool Open;
        public UILabel Note; public GameObject OpenButton; public readonly List<GameObject> Hidden = new();
        public readonly List<(string name, GameObject go)> Tabs = new(); public int Tab;
        public readonly List<(UITexture box, UILabel label)> TabButtons = new();
        public readonly List<Action<int, long>> Updaters = new(); // aggiornano i contenuti: (nave, adesso)
        public string Message = ""; public long MessageUntil;
        // stato delle procedure manuali
        public string VentOrder = ""; public int VentPos; public long VentDeadline, VentReady, AlignReady;
        public long RotateReady, PingReady, DecReady; public string DecSecret = "", DecInput = ""; public readonly List<string> DecTries = new();
        public long ChargeStart = -1, BurstReady;
        public long EvadeReady, BurnReady, RouteReady, RouteAxisStart; public int RouteAxis = -1; public bool RouteShown; public readonly float[] RouteErr = new float[3];
        public long BypassReady;
    }

    static class Panels
    {
        public const string Prefix = "Mel|";
        static readonly Dictionary<IntPtr, PanelPage> pages = new();
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static readonly System.Random rng = new();
        static long nextRefresh;
        const string Grey = "[8b90a0]", Good = "[7CD992]", Bad = "[FF8A7A]", Amber = "[F2A65A]", Blue = "[9db4ff]", End = "[-]";
        const UIWidget.Pivot TL = UIWidget.Pivot.TopLeft;
        static readonly Color cBg = new Color(0.03f, 0.04f, 0.06f, 1f), cPanel = new Color(0.11f, 0.13f, 0.20f, 1f), cTrack = new Color(0.17f, 0.20f, 0.29f, 1f),
            cInk = new Color(0.04f, 0.05f, 0.07f, 1f), cGrey = new Color(0.55f, 0.57f, 0.63f, 1f), cWhite = new Color(0.93f, 0.92f, 0.88f, 1f),
            cGood = new Color(0.49f, 0.85f, 0.57f, 1f), cWarn = new Color(0.95f, 0.65f, 0.35f, 1f), cBad = new Color(1f, 0.45f, 0.40f, 1f), cBlue = new Color(0.62f, 0.71f, 1f, 1f);
        // colore d'accento per stazione: Capitano, Pilota, Scienza, Armi, Ingegneria
        static readonly Color[] Accent = { new Color(0.95f, 0.65f, 0.35f, 1f), new Color(0.62f, 0.71f, 1f, 1f), new Color(0.37f, 0.88f, 0.84f, 1f), new Color(1f, 0.50f, 0.45f, 1f), new Color(1f, 0.84f, 0.37f, 1f) };
        static readonly string[] Greek = { "ALFA", "BETA", "GAMMA", "DELTA" };
        static readonly string[] Doctrines = { "PRECISA", "RAPIDA", "CONTINUA" };
        static readonly string[] Aims = { "MISTO", "SCUDI", "SCAFO" };
        static readonly string[] Orders = { "BATTAGLIA", "DANNI", "REGIME", "SILENZIO" };

        // I figli diretti dello schermo hanno l'origine al centro di un quadrato 512x512.
        static Vector3 TopLeft(float x, float y) => new Vector3(x - 256f, y + 256f, 0f);
        static Vector3 V(float x, float y) => new Vector3(x, y, 0f);

        // ---- costruttore di elementi: ogni scheda si disegna con questi mattoni, su una griglia comune
        sealed class Kit
        {
            public PLUIScreen sc; public PanelPage pg; public Transform t; public Color accent;
            public const float L = 16f, W = 472f; // margine sinistro e larghezza utile
            public float ox, oy;                  // origine (serve per gli elementi figli diretti dello schermo)

            public UITexture Box(float x, float y, float w, float h, Color c) => sc.CreateBox(V(x + ox, y + oy), new Vector2(w, h), c, t, TL);

            // Testo in una colonna di larghezza fissa: se è più lungo si restringe, non esce mai dalla colonna.
            public UILabel Text(float x, float y, float w, string s, int size, Color c, NGUIText.Alignment al = NGUIText.Alignment.Left)
            {
                var l = sc.CreateLabel(s, V(x + ox, y + oy), size, c, t, TL);
                // le etichette del gioco hanno una scala propria: la colonna va espressa nelle loro unità
                float sx = Math.Max(0.01f, l.transform.localScale.x), sy = Math.Max(0.01f, l.transform.localScale.y);
                l.overflowMethod = UILabel.Overflow.ShrinkContent; l.multiLine = false; l.alignment = al;
                l.width = (int)(w / sx); l.height = (int)((size + 8) / sy);
                return l;
            }

            // Pulsante piatto: riquadro pieno con la scritta centrata.
            public (UITexture box, UILabel label) Button(string name, float x, float y, float w, float h, string text, Color fill, Color ink, int size = 12)
            {
                var b = Box(x, y, w, h, fill); b.gameObject.name = Prefix + name; sc.AllButtons.Add(b);
                var l = Text(x + 4f, y - (h - size - 6f) / 2f, w - 8f, text, size, ink, NGUIText.Alignment.Center);
                return (b, l);
            }

            public void Rule(float y) => Box(L, y, W, 1f, cTrack);

            // Titolo di sezione con tacca colorata e riga d'aiuto.
            public void Section(float y, string title, string help)
            {
                Box(L, y - 2f, 4f, 16f, accent);
                Text(L + 10f, y, W - 10f, title.ToUpperInvariant(), 13, cWhite);
                if (help.Length > 0) Text(L + 10f, y - 19f, W - 10f, help, 10, cGrey);
            }

            // Barra di livello 0..1.
            public Action<float, Color> Meter(float x, float y, float w, float h)
            {
                Box(x, y, w, h, cTrack); var fill = Box(x, y, 2f, h, accent);
                return (frac, col) => { frac = Math.Clamp(frac, 0f, 1f); bool on = frac > 0.004f; if (fill.gameObject.activeSelf != on) fill.gameObject.SetActive(on); if (on) { fill.width = Math.Max(2, (int)(w * frac)); fill.color = col; } };
            }

            // Barra a due versi attorno allo zero (per comandi da min a max).
            public Action<float, float, float, Color> Bipolar(float x, float y, float w, float h)
            {
                Box(x, y, w, h, cTrack); var fill = Box(x, y, 2f, h, accent); var zero = Box(x, y - 2f, 2f, h + 4f, cGrey);
                return (v, min, max, col) =>
                {
                    float z = (0f - min) / (max - min), p = Math.Clamp((v - min) / (max - min), 0f, 1f);
                    zero.transform.localPosition = V(x + w * z - 1f, y + 2f);
                    bool on = Math.Abs(p - z) > 0.004f; if (fill.gameObject.activeSelf != on) fill.gameObject.SetActive(on);
                    if (on) { fill.transform.localPosition = V(x + w * Math.Min(z, p), y); fill.width = Math.Max(2, (int)(w * Math.Abs(p - z))); fill.color = col; }
                };
            }

            // Barra con zona utile e indice mobile (posizione 0..1; negativa = indice nascosto).
            public Action<float, Color> Marker(float x, float y, float w, float h, float lo, float hi)
            {
                Box(x, y, w, h, cTrack); Box(x + w * lo, y, w * (hi - lo), h, Color.Lerp(cTrack, accent, 0.45f));
                var mark = Box(x, y + 4f, 4f, h + 8f, cWhite);
                return (pos, col) => { bool on = pos >= 0f; if (mark.gameObject.activeSelf != on) mark.gameObject.SetActive(on); if (on) { mark.transform.localPosition = V(x + w * Math.Clamp(pos, 0f, 1f) - 2f, y + 4f); mark.color = col; } };
            }
        }

        public static PanelPage Of(PLUIScreen s) => pages.TryGetValue(s.Pointer, out var p) ? p : null;

        public static void Say(PanelPage pg, string msg) { pg.Message = msg; pg.MessageUntil = clock.ElapsedMilliseconds + 4000; }

        public static void Build(PLUIScreen sc, int station)
        {
            var root = sc.MyRootPanel; if (root == null) return;
            var pg = new PanelPage { Screen = sc, Station = station };
            var accent = Accent[Math.Clamp(station, 0, Accent.Length - 1)];
            // pulsante di accesso nella barra del titolo dello schermo, piatto come il resto
            var rk = new Kit { sc = sc, pg = pg, t = root.transform, accent = accent, ox = -256f, oy = 256f };
            pg.OpenButton = sc.CreateBlankPanel("MelchiorAccesso", TopLeft(440f, -1f), new Vector2(68f, 17f), root.transform, TL).gameObject;
            var ok = new Kit { sc = sc, pg = pg, t = pg.OpenButton.transform, accent = accent };
            ok.Button("open", 0f, 0f, 68f, 17f, "REG", accent, cInk, 11);
            pg.Page = sc.CreatePanel(" ", TopLeft(4f, -20f), new Vector2(504f, 488f), cBg, root.transform, TL);
            var k = new Kit { sc = sc, pg = pg, t = pg.Page.transform, accent = accent };
            string name = Stations.Cfg.StationNames[Math.Clamp(station, 0, Stations.Cfg.StationNames.Length - 1)];

            // intestazione: blocco colorato col nome della stazione, filetto, pulsante di chiusura
            k.Box(0f, 0f, 6f, 488f, accent);
            k.Box(6f, 0f, 150f, 24f, accent); k.Text(14f, -4f, 136f, name.ToUpperInvariant(), 14, cInk);
            k.Box(160f, -10f, 228f, 4f, accent);
            k.Button("close", 394f, 0f, 104f, 24f, "CHIUDI", cPanel, cWhite, 12);

            // schede
            var names = new List<string>();
            if (station == 0) { names.Add("Assetti"); if (Stations.Cfg.Captain.Enabled) { names.Add("Ordini"); names.Add("Quadro"); if (Stations.Cfg.Protocols.Enabled) names.Add("Protocolli"); names.Add("Giornale"); } }
            names.AddRange(Stations.Cfg.Controls.Where(c => c.Station == station).Select(c => c.Page).Distinct());
            if (station == 4 && Stations.Cfg.Core.Enabled) names.Add("Nucleo");
            if (station == 4 && Stations.Cfg.Damage.Enabled) names.Add("Danni");
            if (station == 2 && Stations.Cfg.Science.Enabled) { names.Add("Scudi"); names.Add("Analisi"); }
            if (station == 2 && Stations.Cfg.Sensors.Enabled) names.Add("Sensori");
            if (station == 3 && Stations.Cfg.Weapons.Enabled) { names.Add("Tiro"); names.Add("Raffica"); }
            if (station == 3 && Stations.Cfg.Damage.Enabled && Stations.Cfg.Sensors.Enabled) names.Add("Bersaglio");
            if (station == 1 && Stations.Cfg.Pilot.Enabled) { names.Add("Manovre"); names.Add("Rotta"); }
            if (station == 1 && Stations.Cfg.Engage.Enabled) names.Add("Ingaggio");
            int nt = Math.Min(names.Count, 5); float tw = (Kit.W - (nt - 1) * 6f) / Math.Max(1, nt);
            for (int i = 0; i < nt; i++)
            {
                pg.TabButtons.Add(k.Button("tab|" + i, Kit.L + i * (tw + 6f), -32f, tw, 22f, names[i].ToUpperInvariant(), cPanel, cWhite, nt > 3 ? 11 : 12));
                var sub = sc.CreateBlankPanel("MelchiorScheda" + i, V(0f, -64f), new Vector2(504f, 388f), pg.Page.transform, TL);
                var sk = new Kit { sc = sc, pg = pg, t = sub.transform, accent = accent };
                switch (names[i])
                {
                    case "Assetti": BuildPresets(sk); break;
                    case "Ordini": BuildOrders(sk); break;
                    case "Quadro": BuildBoard(sk); break;
                    case "Nucleo": BuildCore(sk); break;
                    case "Scudi": BuildShields(sk); break;
                    case "Analisi": BuildAnalysis(sk); break;
                    case "Tiro": BuildFire(sk); break;
                    case "Raffica": BuildCapacitors(sk); break;
                    case "Manovre": BuildManeuvers(sk); break;
                    case "Rotta": BuildRoute(sk); break;
                    case "Danni": BuildDamage(sk); break;
                    case "Sensori": BuildSensors(sk); break;
                    case "Bersaglio": BuildTarget(sk); break;
                    case "Ingaggio": BuildEngage(sk); break;
                    case "Protocolli": BuildProtocols(sk); break;
                    case "Giornale": BuildJournal(sk); break;
                    default: BuildControls(sk, Stations.Cfg.Controls.Where(c => c.Station == station && c.Page == names[i]).ToList()); break;
                }
                sub.gameObject.SetActive(i == 0);
                pg.Tabs.Add((names[i], sub.gameObject));
            }
            // piè di pagina: filetto e riga di stato
            k.Box(Kit.L, -458f, Kit.W, 1f, cTrack);
            pg.Note = k.Text(Kit.L, -464f, Kit.W, "", 11, cWhite);
            pg.Page.gameObject.SetActive(false);
            pages[sc.Pointer] = pg;
        }

        static void BuildPresets(Kit k)
        {
            k.Section(-4f, "Assetti di bordo", "Regolano tutte le stazioni e lo stato d'allarme.");
            float y = -48f;
            for (int i = 0; i < Stations.Cfg.Presets.Length && i < 5; i++)
            {
                var p = Stations.Cfg.Presets[i];
                k.Button("p|" + i, Kit.L, y, 190f, 26f, p.Name.ToUpperInvariant(), k.accent, cInk, 13);
                k.Text(Kit.L + 204f, y - 6f, Kit.W - 204f, p.Help, 11, cGrey);
                k.Rule(y - 40f);
                y -= 62f;
            }
        }

        // Comandi continui: righe larghe (fino a 4) o righe compatte per i gruppi a bilancio (fino a 6).
        static void BuildControls(Kit k, List<StationControl> list)
        {
            bool compact = list.Count > 4 || list.Any(c => c.Group.Length > 0);
            float y = -4f;
            foreach (var c in list.Take(compact ? 6 : 4))
            {
                var cc = c; float scale = cc.Scale == 0 ? 100f : cc.Scale;
                if (compact)
                {
                    k.Text(Kit.L, y, 196f, cc.Label, 13, cWhite);
                    k.Button("c|" + cc.Id + "|-", 222f, y - 1f, 30f, 20f, "-", cPanel, cWhite, 13);
                    var bar = k.Bipolar(260f, y - 5f, 148f, 12f);
                    k.Button("c|" + cc.Id + "|+", 416f, y - 1f, 30f, 20f, "+", cPanel, cWhite, 13);
                    var val = k.Text(450f, y, 38f, "", 13, k.accent, NGUIText.Alignment.Right);
                    var fx = k.Text(Kit.L, y - 24f, Kit.W, "", 10, cGrey);
                    k.Rule(y - 46f);
                    string sum = cc.Summary.Length > 0 ? cc.Summary : string.Join(", ", cc.Effects.Select(e => Stations.Cfg.StatNames.TryGetValue(e.Stat, out var nn) ? nn : e.Stat));
                    k.pg.Updaters.Add((id, now) =>
                    {
                        float v = Stations.Get(id, cc.Id);
                        bar(v, cc.Min, cc.Max, v >= 0 ? k.accent : cBlue); val.text = v.ToString("+0;-0;0");
                        fx.text = Math.Abs(v) < 0.001f ? Grey + sum + End
                            : (v > 0 ? Good : Bad) + sum + ": " + string.Join(" / ", cc.Effects.Select(e => (e.Percent * v > 0 ? "+" : "") + (e.Percent * v / scale).ToString("0") + "%")) + End;
                    });
                    y -= 62f;
                }
                else
                {
                    k.Text(Kit.L, y, 370f, cc.Label, 14, cWhite);
                    var val = k.Text(Kit.L + 380f, y, 92f, "", 14, k.accent, NGUIText.Alignment.Right);
                    k.Text(Kit.L, y - 20f, Kit.W, cc.Help, 10, cGrey);
                    k.Button("c|" + cc.Id + "|-", Kit.L, y - 40f, 34f, 22f, "-", cPanel, cWhite, 14);
                    var bar = k.Bipolar(Kit.L + 42f, y - 45f, 344f, 12f);
                    k.Button("c|" + cc.Id + "|+", Kit.L + 394f, y - 40f, 34f, 22f, "+", cPanel, cWhite, 14);
                    k.Button("c|" + cc.Id + "|0", Kit.L + 436f, y - 40f, 36f, 22f, "0", cPanel, cGrey, 13);
                    var fx = k.Text(Kit.L, y - 68f, Kit.W, "", 10, cGrey);
                    k.Rule(y - 88f);
                    k.pg.Updaters.Add((id, now) =>
                    {
                        float v = Stations.Get(id, cc.Id);
                        bar(v, cc.Min, cc.Max, v >= 0 ? k.accent : cBlue); val.text = v.ToString("+0;-0;0");
                        fx.text = string.Join("   ", cc.Effects.Select(e => {
                            float f = 1f + e.Percent / 100f * (v / scale); string n = Stations.Cfg.StatNames.TryGetValue(e.Stat, out var nn) ? nn : e.Stat;
                            return Math.Abs(v) < 0.001f ? Grey + n + (e.Percent > 0 ? " (+)" : " (-)") + End : (f >= 1 ? Good : Bad) + n + " " + (f >= 1 ? "+" : "") + ((f - 1) * 100).ToString("0") + "%" + End; }));
                    });
                    y -= 96f;
                }
            }
        }

        static void BuildCore(Kit k)
        {
            var c = Stations.Cfg.Core;
            k.Section(-4f, "Stress del nucleo", "");
            k.Button("core|scram", Kit.L + 352f, -4f, 120f, 22f, "ARRESTO", cBad, cInk, 12);
            var stress = k.Meter(Kit.L, -28f, 400f, 14f); var pct = k.Text(Kit.L + 408f, -25f, 64f, "", 14, cWhite, NGUIText.Alignment.Right);
            var state = k.Text(Kit.L, -48f, Kit.W, "", 11, cWhite);
            k.Rule(-70f);

            k.Section(-80f, "Spurgo del plasma", "Avvia, poi apri le valvole nell'ordine indicato.");
            k.Button("core|vent", Kit.L, -122f, 100f, 24f, "AVVIA", k.accent, cInk, 12);
            for (int i = 0; i < 4; i++) k.Button("core|v|" + "ABCD"[i], Kit.L + 112f + i * 91f, -122f, 85f, 24f, Greek[i], cPanel, cWhite, 12);
            var seq = k.Text(Kit.L, -154f, 330f, "", 12, cWhite); var vstate = k.Text(Kit.L + 336f, -154f, 136f, "", 11, cWhite, NGUIText.Alignment.Right);
            k.Rule(-180f);

            k.Section(-190f, "Riallineamento della matrice", "Premi ALLINEA quando l'indice è nella zona evidenziata.");
            var mark = k.Marker(Kit.L, -238f, Kit.W, 12f, 0.5f - c.AlignWindow / 2, 0.5f + c.AlignWindow / 2);
            k.Button("core|align", Kit.L, -266f, 120f, 24f, "ALLINEA", k.accent, cInk, 12);
            var astate = k.Text(Kit.L + 132f, -270f, 340f, "", 11, cWhite);

            k.pg.Updaters.Add((id, now) =>
            {
                var pg = k.pg; float s = Stations.Get(id, "core_stress"), lockLeft = Stations.Get(id, "core_lock"), align = Stations.Get(id, "core_align");
                var col = s >= c.CriticalAt ? cBad : s >= c.WarnAt ? cWarn : cGood;
                stress(s / 100f, col); pct.text = s.ToString("0") + "%";
                state.text = lockLeft > 0 ? Bad + $"ARRESTO DI SICUREZZA: nucleo bloccato per {lockLeft:0} s" + End
                    : s >= c.CriticalAt ? Bad + $"CRITICO: potenza -{c.CriticalOutputPenalty:0}%. Spurga o stacca il carico." + End
                    : s >= c.WarnAt ? Amber + "SOLLECITATO: il margine termico si sta riducendo." + End : Good + "NOMINALE" + End;
                if (pg.VentOrder.Length > 0)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < pg.VentOrder.Length; i++) { string w = Greek[pg.VentOrder[i] - 'A']; sb.Append(i < pg.VentPos ? Good + w + End : i == pg.VentPos ? Amber + w + End : Grey + w + End).Append(i < pg.VentOrder.Length - 1 ? "  >  " : ""); }
                    seq.text = sb.ToString(); vstate.text = Amber + $"{(pg.VentDeadline - now) / 1000.0:0.0} s" + End;
                }
                else { seq.text = Grey + "Sequenza: in attesa" + End; vstate.text = now < pg.VentReady ? Grey + $"pronto tra {(pg.VentReady - now) / 1000:0} s" + End : Good + "pronto" + End; }
                mark(AlignPos(s), cWhite);
                astate.text = align > 0 ? Good + $"Matrice allineata: potenza +{c.AlignBonus:0}% per {align:0} s" + End
                    : now < pg.AlignReady ? Grey + $"Iniettori in assestamento: {(pg.AlignReady - now) / 1000:0} s" + End : Grey + "Matrice non allineata" + End;
            });
        }

        static void BuildShields(Kit k)
        {
            var c = Stations.Cfg.Science;
            k.Section(-4f, "Armonica degli scudi", "Il nemico impara la frequenza. Ruotala in tempo.");
            var eff = k.Meter(Kit.L, -48f, 400f, 14f); var pct = k.Text(Kit.L + 408f, -45f, 64f, "", 14, cWhite, NGUIText.Alignment.Right);
            var state = k.Text(Kit.L, -68f, Kit.W, "", 11, cWhite);
            var bands = new List<(UITexture box, UILabel label)>();
            for (int i = 0; i < 3; i++) bands.Add(k.Button("sci|band|" + (i + 1), Kit.L + i * 160f, -94f, 152f, 24f, "BANDA " + Greek[i], cPanel, cWhite, 12));
            var bstate = k.Text(Kit.L, -124f, Kit.W, "", 11, cWhite);
            k.Rule(-150f);

            k.Section(-160f, "Impulso attivo dei sensori", "Illumina il settore, ma rivela anche la tua nave.");
            k.Button("sci|ping", Kit.L, -204f, 120f, 24f, "IMPULSO", k.accent, cInk, 12);
            var pstate = k.Text(Kit.L + 132f, -208f, 340f, "", 11, cWhite);

            k.pg.Updaters.Add((id, now) =>
            {
                var pg = k.pg; int band = (int)Stations.Get(id, "scx_band"); float e = Stations.Get(id, "scx_eff"), dip = Stations.Get(id, "scx_dip"), ping = Stations.Get(id, "scx_ping");
                for (int i = 0; i < bands.Count; i++) { bool on = band == i + 1; bands[i].box.color = on ? k.accent : cPanel; bands[i].label.color = on ? cInk : cWhite; }
                if (band <= 0) { eff(0f, cGrey); pct.text = "-"; state.text = Grey + "Scudi non modulati: scegli una banda." + End; }
                else
                {
                    eff(e / 100f, e >= 60 ? cGood : e >= 30 ? cWarn : cBad); pct.text = e.ToString("0") + "%";
                    state.text = (e >= 60 ? Good + "Armonica solida" : e >= 30 ? Amber + "Il nemico si sta adattando" : Bad + "Armonica compromessa") + $"   scudi {(e - 50f) / 100f * c.HarmonicSwing:+0;-0;0}%" + End;
                }
                bstate.text = dip > 0 ? Bad + $"Rifasamento in corso: ricarica -{c.RotateDipPercent:0}% per {dip:0} s" + End
                    : now < pg.RotateReady ? Grey + $"Emettitori pronti tra {(pg.RotateReady - now) / 1000:0} s" + End : Good + "Emettitori pronti" + End;
                pstate.text = ping > 0 ? Amber + $"Attivo: {ping:0} s   rilevamento +{c.PingDetection:0}%, firma +{c.PingSignature:0}%" + End
                    : now < pg.PingReady ? Grey + $"Condensatori in carica: {(pg.PingReady - now) / 1000:0} s" + End : Good + "Pronto" + End;
            });
        }

        static void BuildAnalysis(Kit k)
        {
            var c = Stations.Cfg.Science;
            k.Section(-4f, "Decrittazione della firma nemica", "Trova i tre simboli. Ogni tentativo dà un indizio.");
            for (int i = 0; i < 4; i++) k.Button("sci|sym|" + "ABCD"[i], Kit.L + i * 91f, -48f, 85f, 24f, Greek[i], cPanel, cWhite, 12);
            k.Button("sci|del", Kit.L + 372f, -48f, 100f, 24f, "CANCELLA", cBad, cInk, 11);
            var entry = k.Text(Kit.L, -80f, Kit.W, "", 12, cWhite);
            k.Rule(-102f);
            var guess = new UILabel[6]; var hint = new UILabel[6];
            for (int i = 0; i < 6; i++)
            {
                k.Text(Kit.L, -110f - i * 22f, 20f, (i + 1).ToString(), 11, cGrey);
                guess[i] = k.Text(Kit.L + 26f, -110f - i * 22f, 220f, "", 11, cWhite);
                hint[i] = k.Text(Kit.L + 252f, -110f - i * 22f, 220f, "", 11, cWhite, NGUIText.Alignment.Right);
            }
            k.Rule(-246f);
            k.Button("sci|new", Kit.L, -256f, 120f, 24f, "NUOVA ANALISI", k.accent, cInk, 11);
            var state = k.Text(Kit.L + 132f, -260f, 340f, "", 11, cWhite);

            k.pg.Updaters.Add((id, now) =>
            {
                var pg = k.pg; float sol = Stations.Get(id, "scx_sol");
                entry.text = pg.DecSecret.Length == 0 ? Grey + "Nessuna analisi in corso." + End
                    : $"Tentativo {pg.DecTries.Count + 1} di {c.DecryptAttempts}:   " + Amber + string.Join("  ", pg.DecInput.Select(Word)) + End;
                for (int i = 0; i < 6; i++)
                {
                    if (i < pg.DecTries.Count) { var parts = pg.DecTries[i].Split('|'); guess[i].text = parts[0]; hint[i].text = parts.Length > 1 ? parts[1] : ""; }
                    else { guess[i].text = ""; hint[i].text = ""; }
                }
                state.text = sol > 0 ? Good + $"Soluzione tattica attiva: {sol:0} s   danno +{c.SolutionDamage:0}%" + End
                    : now < pg.DecReady ? Grey + $"Analizzatore in riavvio: {(pg.DecReady - now) / 1000:0} s" + End : Grey + "Nessuna soluzione tattica attiva" + End;
            });
        }

        static void BuildFire(Kit k)
        {
            var w = Stations.Cfg.Weapons;
            k.Section(-4f, "Dottrina di tiro", "La soluzione matura col tempo. Cambiarla la azzera.");
            var docs = new List<(UITexture box, UILabel label)>();
            for (int i = 0; i < 3; i++) docs.Add(k.Button("wep|doc|" + (i + 1), Kit.L + i * 160f, -48f, 152f, 24f, Doctrines[i], cPanel, cWhite, 12));
            var q = k.Meter(Kit.L, -82f, 400f, 14f); var pct = k.Text(Kit.L + 408f, -79f, 64f, "", 14, cWhite, NGUIText.Alignment.Right);
            var dstate = k.Text(Kit.L, -102f, Kit.W, "", 11, cWhite);
            k.Rule(-126f);

            k.Section(-136f, "Puntamento", "Dove concentrare il fuoco delle torrette.");
            var aims = new List<(UITexture box, UILabel label)>();
            for (int i = 0; i < 3; i++) aims.Add(k.Button("wep|aim|" + i, Kit.L + i * 160f, -180f, 152f, 24f, Aims[i], cPanel, cWhite, 12));
            var astate = k.Text(Kit.L, -212f, Kit.W, "", 11, cWhite);

            k.pg.Updaters.Add((id, now) =>
            {
                int doc = (int)Stations.Get(id, "wpx_doc"), aim = (int)Stations.Get(id, "wpx_aim"); float v = Stations.Get(id, "wpx_q");
                for (int i = 0; i < 3; i++) { bool on = doc == i + 1; docs[i].box.color = on ? k.accent : cPanel; docs[i].label.color = on ? cInk : cWhite; }
                for (int i = 0; i < 3; i++) { bool on = aim == i; aims[i].box.color = on ? k.accent : cPanel; aims[i].label.color = on ? cInk : cWhite; }
                if (doc <= 0) { q(0f, cGrey); pct.text = "-"; dstate.text = Grey + "Nessuna dottrina: fuoco a discrezione." + End; }
                else
                {
                    q(v / 100f, v >= 99.5f ? cGood : k.accent); pct.text = v.ToString("0") + "%";
                    dstate.text = Good + (doc == 1 ? $"Danno +{w.PrecisionDamage * v / 100:0}%" : doc == 2 ? $"Carica +{w.RapidCharge * v / 100:0}%" : $"Raffreddamento +{w.SustainCooling * v / 100:0}%   danno -{w.SustainDamagePenalty:0}%") + End;
                }
                astate.text = aim == 1 ? Good + $"Scudi +{w.AimShieldsBonus:0}%" + End + "   " + Bad + $"scafo -{w.AimShieldsHullPenalty:0}%" + End
                    : aim == 2 ? Good + $"Scafo +{w.AimHullBonus:0}%" + End + "   " + Bad + $"scudi -{w.AimHullShieldPenalty:0}%" + End : Grey + "Nessuna preferenza" + End;
            });
        }

        static void BuildCapacitors(Kit k)
        {
            var w = Stations.Cfg.Weapons;
            k.Section(-4f, "Sovraccarico dei condensatori", "Carica e scarica vicino al limite. Oltre, salta tutto.");
            var lvl = k.Meter(Kit.L, -48f, 400f, 14f); k.Box(Kit.L + 400f * w.OverloadAt / w.ChargeMax, -44f, 3f, 22f, cBad);
            var pct = k.Text(Kit.L + 408f, -45f, 64f, "", 14, cWhite, NGUIText.Alignment.Right);
            k.Button("wep|charge", Kit.L, -78f, 120f, 24f, "CARICA", cPanel, cWhite, 12);
            k.Button("wep|fire", Kit.L + 128f, -78f, 120f, 24f, "SCARICA", k.accent, cInk, 12);
            var state = k.Text(Kit.L, -110f, Kit.W, "", 11, cWhite);
            k.Rule(-134f);
            k.Text(Kit.L, -142f, Kit.W, "Ogni scarica riscalda il nucleo: avvisa l'ingegnere.", 10, cGrey);

            k.pg.Updaters.Add((id, now) =>
            {
                var pg = k.pg; float l = ChargeLevel(pg, now), burst = Stations.Get(id, "wpx_burst"), jam = Stations.Get(id, "wpx_jam");
                if (l >= 0) { lvl(Math.Min(l, w.ChargeMax) / w.ChargeMax, l > w.OverloadAt ? cBad : cWarn); pct.text = l.ToString("0") + "%"; } else { lvl(0f, cGrey); pct.text = "-"; }
                state.text = jam > 0 ? Bad + $"CONDENSATORI SALTATI: carica -{w.JamChargePenalty:0}% per {jam:0} s" + End
                    : burst > 0 ? Good + $"Raffica: danno +{Stations.Get(id, "wpx_burstp"):0}% per {burst:0} s" + End
                    : l >= 0 ? Amber + "In carica: scarica prima del segno rosso." + End
                    : now < pg.BurstReady ? Grey + $"Condensatori in recupero: {(pg.BurstReady - now) / 1000:0} s" + End : Good + "Pronto" + End;
            });
        }

        static void BuildManeuvers(Kit k)
        {
            var c = Stations.Cfg.Pilot;
            k.Section(-4f, "Bilanciamento degli ugelli", "Tieni l'indice nella zona centrale: lo sbilancio ruba spinta.");
            var trim = k.Marker(Kit.L, -52f, Kit.W, 12f, 0.42f, 0.58f);
            k.Button("plt|trim|L", Kit.L, -80f, 80f, 24f, "SINISTRA", cPanel, cWhite, 11);
            k.Button("plt|trim|R", Kit.L + 88f, -80f, 80f, 24f, "DESTRA", cPanel, cWhite, 11);
            var tstate = k.Text(Kit.L + 180f, -84f, 292f, "", 11, cWhite);
            k.Rule(-118f);

            k.Section(-128f, "Manovre", "Programmi di volo a tempo, con ricarica.");
            k.Button("plt|evade", Kit.L, -172f, 152f, 24f, "EVASIVA", k.accent, cInk, 12);
            k.Button("plt|burn", Kit.L + 160f, -172f, 152f, 24f, "SPINTA", cWarn, cInk, 12);
            var drift = k.Button("plt|drift", Kit.L + 320f, -172f, 152f, 24f, "DERIVA", cPanel, cWhite, 12);
            var es = k.Text(Kit.L, -202f, 152f, "", 10, cGrey, NGUIText.Alignment.Center); var bs = k.Text(Kit.L + 160f, -202f, 152f, "", 10, cGrey, NGUIText.Alignment.Center);
            var ds = k.Text(Kit.L + 320f, -202f, 152f, "", 10, cGrey, NGUIText.Alignment.Center);
            var mstate = k.Text(Kit.L, -226f, Kit.W, "", 11, cWhite);

            k.pg.Updaters.Add((id, now) =>
            {
                var pg = k.pg; float tr = Stations.Get(id, "plx_trim"), off = Math.Abs(tr) / 100f, evade = Stations.Get(id, "plx_evade"), burn = Stations.Get(id, "plx_burn"); bool dr = Stations.Get(id, "plx_drift") > 0;
                float p = c.TrimBonus - (c.TrimBonus + c.TrimPenalty) * off;
                trim(0.5f + tr / 200f, off < 0.16f ? cGood : off < 0.5f ? cWarn : cBad);
                tstate.text = (p >= 0 ? Good : Bad) + $"Spinta {p:+0;-0;0}%" + End + Grey + (off < 0.16f ? "   ugelli in asse" : "   ugelli sbilanciati") + End;
                drift.box.color = dr ? k.accent : cPanel; drift.label.color = dr ? cInk : cWhite;
                es.text = evade > 0 ? Amber + $"attiva {evade:0} s" + End : now < pg.EvadeReady ? $"pronta tra {(pg.EvadeReady - now) / 1000:0} s" : Good + "pronta" + End;
                bs.text = burn > 0 ? Amber + $"attiva {burn:0} s" + End : now < pg.BurnReady ? $"pronta tra {(pg.BurnReady - now) / 1000:0} s" : Good + "pronta" + End;
                ds.text = dr ? Blue + "inserita" + End : "disinserita";
                mstate.text = evade > 0 ? Amber + $"Evasiva: manovra +{c.EvadeManeuver:0}%, danno -{c.EvadeDamagePenalty:0}%" + End
                    : burn > 0 ? Amber + $"Spinta d'emergenza: spinta +{c.BurnThrust:0}%, firma +{c.BurnSignature:0}%" + End
                    : dr ? Blue + $"Deriva: spinta -{c.DriftThrustPenalty:0}%, firma -{c.DriftSignatureCut:0}%" + End : "";
            });
        }

        static void BuildRoute(Kit k)
        {
            var c = Stations.Cfg.Pilot;
            k.Section(-4f, "Calcolo di rotta", "Ferma i tre assi nella zona evidenziata, uno alla volta.");
            string[] ax = { "X", "Y", "Z" }; var marks = new Action<float, Color>[3];
            for (int i = 0; i < 3; i++) { k.Text(Kit.L, -50f - i * 28f, 18f, ax[i], 13, k.accent); marks[i] = k.Marker(Kit.L + 26f, -52f - i * 28f, Kit.W - 26f, 12f, 0.42f, 0.58f); }
            k.Button("plt|route|start", Kit.L, -138f, 120f, 24f, "AVVIA", cPanel, cWhite, 12);
            k.Button("plt|route|stop", Kit.L + 128f, -138f, 120f, 24f, "FERMA", k.accent, cInk, 12);
            var state = k.Text(Kit.L + 260f, -142f, 212f, "", 11, cWhite, NGUIText.Alignment.Right);
            k.Rule(-172f);
            var result = k.Text(Kit.L, -182f, Kit.W, "", 11, cWhite);

            k.pg.Updaters.Add((id, now) =>
            {
                var pg = k.pg;
                for (int i = 0; i < 3; i++)
                {
                    if (pg.RouteAxis < 0) marks[i](pg.RouteShown ? 0.5f + pg.RouteErr[i] : -1f, Math.Abs(pg.RouteErr[i]) <= 0.08f ? cGood : cBad);
                    else if (i < pg.RouteAxis) marks[i](0.5f + pg.RouteErr[i], Math.Abs(pg.RouteErr[i]) <= 0.08f ? cGood : cBad);
                    else if (i == pg.RouteAxis) marks[i](AxisPos(pg, now), cWhite);
                    else marks[i](-1f, cWhite);
                }
                float route = Stations.Get(id, "plx_route"), q = Stations.Get(id, "plx_routeq");
                state.text = pg.RouteAxis >= 0 ? Amber + $"Asse {ax[pg.RouteAxis]} in movimento" + End
                    : now < pg.RouteReady ? Grey + $"Ricalcolo: {(pg.RouteReady - now) / 1000:0} s" + End : Good + "Pronto" + End;
                result.text = route > 0 ? Good + $"Rotta al {q:0}%: carica +{c.RouteCharge * q / 100:0}%, raggio +{c.RouteRange * q / 100:0}% per {route:0} s" + End : Grey + "Nessuna rotta calcolata" + End;
            });
        }

        static void BuildOrders(Kit k)
        {
            var c = Stations.Cfg.Captain;
            k.Section(-4f, "Iniziativa di comando", "Cresce con i successi dell'equipaggio. Gli ordini la consumano.");
            var ini = k.Meter(Kit.L, -48f, 400f, 14f); var val = k.Text(Kit.L + 408f, -45f, 64f, "", 14, cWhite, NGUIText.Alignment.Right);
            k.Rule(-74f);
            k.Section(-84f, "Ordini di plancia", "");
            var ords = new List<(UITexture box, UILabel label)>();
            for (int i = 0; i < 4; i++) ords.Add(k.Button("cap|order|" + (i + 1), Kit.L + i * 95f, -108f, 89f, 24f, Orders[i], cPanel, cWhite, 11));
            k.Button("cap|order|0", Kit.L + 380f, -108f, 92f, 24f, "REVOCA", cBad, cInk, 11);
            var ostate = k.Text(Kit.L, -140f, Kit.W, "", 11, cWhite);
            k.Rule(-164f);
            k.Section(-174f, "Interventi", $"Costano {c.InterventionCost:0} di iniziativa ciascuno.");
            string[] ids = { "energy", "fire", "shields" }, names = { "ENERGIA", "FUOCO", "SCUDI" }, what = { "raffredda il nucleo", "soluzione di tiro piena", "ripristina l'armonica" };
            for (int i = 0; i < 3; i++)
            {
                k.Button("cap|int|" + ids[i], Kit.L + i * 160f, -218f, 152f, 24f, names[i], k.accent, cInk, 12);
                k.Text(Kit.L + i * 160f, -248f, 152f, what[i], 10, cGrey, NGUIText.Alignment.Center);
            }

            k.pg.Updaters.Add((id, now) =>
            {
                float v = Stations.Get(id, "cpx_ini"); if (v <= 0) v = c.Start; int dir = (int)Stations.Get(id, "cpx_dir"); float tired = Stations.Get(id, "cpx_tired");
                ini(v / 100f, v >= c.InterventionCost ? cGood : cWarn); val.text = v.ToString("0");
                for (int i = 0; i < 4; i++) { bool on = dir == i + 1; ords[i].box.color = on ? k.accent : cPanel; ords[i].label.color = on ? cInk : cWhite; }
                ostate.text = tired > 0 ? Bad + $"Equipaggio affaticato: nessun ordine per {tired:0} s" + End
                    : dir == 1 ? Blue + $"Battaglia: danno +{c.BattleDamage:0}%, scudi +{c.BattleShields:0}%" + End
                    : dir == 2 ? Blue + $"Danni: corazza +{c.RepairArmor:0}%, ricarica scudi +{c.RepairRecharge:0}%" + End
                    : dir == 3 ? Blue + $"Regime: potenza +{c.EfficiencyOutput:0}%, il nucleo si raffredda prima" + End
                    : dir == 4 ? Blue + $"Silenzio: firma -{c.SilenceSignature:0}%, rilevamento +{c.SilenceDetection:0}%" + End : Grey + "Nessun ordine in vigore" + End;
            });
        }

        static void BuildBoard(Kit k)
        {
            k.Section(-4f, "Quadro di situazione", "");
            string[] rows = { "ALLARME", "COMANDO", "INGEGNERIA", "SCIENZA", "ARMI", "PILOTA" }; int[] acc = { 0, 0, 4, 2, 3, 1 };
            var main = new UILabel[rows.Length]; var detail = new UILabel[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                float y = -32f - i * 58f;
                k.Box(Kit.L, y, 4f, 44f, Accent[acc[i]]);
                k.Text(Kit.L + 12f, y, 110f, rows[i], 12, Accent[acc[i]]);
                main[i] = k.Text(Kit.L + 130f, y, 342f, "", 12, cWhite);
                detail[i] = k.Text(Kit.L + 130f, y - 22f, 342f, "", 10, cGrey);
                k.Rule(y - 50f);
            }
            k.pg.Updaters.Add((id, now) =>
            {
                var c = Stations.Cfg.Captain; var co = Stations.Cfg.Core; float G(string key) => Stations.Get(id, key);
                string Left(string key) => G(key) > 0 ? G(key).ToString("0") + " s" : "-";
                var mine = Stations.MyShip; int alert = mine != null ? mine.m_AlertLevel : 0;
                float ini = G("cpx_ini"); if (ini <= 0) ini = c.Start; int dir = (int)G("cpx_dir"); float stress = G("core_stress");
                int band = (int)G("scx_band"), doc = (int)G("wpx_doc"), aim = (int)G("wpx_aim");
                main[0].text = alert >= 2 ? Bad + "ROSSO" + End : alert == 1 ? Amber + "GIALLO" + End : Good + "VERDE" + End;
                int ph = Math.Clamp((int)G("drx_phase"), 0, 3); float tens = G("drx_t");
                detail[0].text = Director.Cfg.Enabled ? $"Situazione {Director.PhaseNames[ph].ToLowerInvariant()}   tensione {(tens >= 55 ? Bad : tens >= 22 ? Amber : Good)}{tens:0}{End}   potenza dell'equipaggio {G("drx_pow"):0.0}" : "";
                main[1].text = "Iniziativa " + ini.ToString("0"); detail[1].text = "Ordine: " + (dir > 0 ? Orders[dir - 1] : "nessuno") + (G("cpx_tired") > 0 ? "   equipaggio affaticato" : "");
                main[2].text = "Nucleo " + (stress >= co.CriticalAt ? Bad : stress >= co.WarnAt ? Amber : Good) + stress.ToString("0") + "%" + End + (G("core_lock") > 0 ? Bad + "   ARRESTO " + Left("core_lock") + End : "");
                detail[2].text = $"Sovralimentazione {G("eng_overdrive"):0}   rete EPS {Stations.Cfg.Controls.Where(x => x.Group == "eps").Sum(x => Math.Max(0f, G(x.Id))):0} tacche   allineamento {Left("core_align")}   sistemi guasti {Stations.SysIds.Count(x => G("dmx_" + x) >= 25f)}";
                main[3].text = "Armonica " + (band > 0 ? Greek[Math.Clamp(band - 1, 0, 2)] + " " + G("scx_eff").ToString("0") + "%" : Grey + "spenta" + End);
                detail[3].text = "Soluzione tattica " + Left("scx_sol") + "   impulso " + Left("scx_ping") + "   scansione " + G("scx_scan").ToString("0") + "%";
                main[4].text = "Dottrina " + (doc > 0 ? Doctrines[doc - 1] + " " + G("wpx_q").ToString("0") + "%" : Grey + "nessuna" + End);
                detail[4].text = "Mira " + Aims[Math.Clamp(aim, 0, 2)].ToLowerInvariant() + "   raffica " + Left("wpx_burst") + (G("wpx_sub") > 0 ? "   bersaglio " + Stations.SysNames[Math.Clamp((int)G("wpx_sub") - 1, 0, 5)].ToLowerInvariant() : "") + (G("wpx_jam") > 0 ? "   condensatori saltati" : "");
                main[5].text = $"Ugelli {G("plx_trim"):+0;-0;0}"; detail[5].text = "Evasiva " + Left("plx_evade") + "   spinta " + Left("plx_burn") + "   rotta " + (G("plx_route") > 0 ? G("plx_routeq").ToString("0") + "%" : "-") + (G("plx_band") > 0 ? "   fascia " + Stations.BandNames[Math.Clamp((int)G("plx_band") - 1, 0, 2)].ToLowerInvariant() + " " + G("plx_pos").ToString("0") + "%" : "");
            });
        }

        static Color Health(float integrity) => integrity >= 75f ? cGood : integrity >= 40f ? cWarn : cBad;
        static string HealthTag(float integrity) => integrity >= 75f ? Good : integrity >= 40f ? Amber : Bad;
        const NGUIText.Alignment Right = NGUIText.Alignment.Right;

        static void BuildDamage(Kit k)
        {
            var c = Stations.Cfg.Damage; int n = Stations.SysIds.Length;
            k.Section(-4f, "Controllo danni", "Assegna le due squadre ai sistemi colpiti: un sistema guasto rende meno.");
            var bars = new Action<float, Color>[n]; var pct = new UILabel[n]; var ta = new (UITexture box, UILabel label)[n]; var tb = new (UITexture box, UILabel label)[n];
            for (int i = 0; i < n; i++)
            {
                float y = -48f - i * 38f;
                k.Text(Kit.L, y, 128f, Stations.SysNames[i], 12, cWhite);
                bars[i] = k.Meter(Kit.L + 134f, y - 3f, 150f, 12f); pct[i] = k.Text(Kit.L + 290f, y, 46f, "", 12, cWhite, Right);
                ta[i] = k.Button("dmg|team|1|" + (i + 1), Kit.L + 346f, y - 1f, 60f, 20f, "SQ. A", cPanel, cWhite, 11);
                tb[i] = k.Button("dmg|team|2|" + (i + 1), Kit.L + 412f, y - 1f, 60f, 20f, "SQ. B", cPanel, cWhite, 11);
            }
            k.Rule(-280f);
            k.Section(-290f, "Derivazione d'emergenza", $"Piena funzione per {c.BypassSeconds} s al sistema più colpito. Scalda il nucleo.");
            k.Button("dmg|bypass", Kit.L, -334f, 120f, 24f, "DERIVAZIONE", k.accent, cInk, 12);
            var bstate = k.Text(Kit.L + 132f, -338f, 340f, "", 11, cWhite);

            k.pg.Updaters.Add((id, now) =>
            {
                int t1 = (int)Stations.Get(id, "dmx_t1"), t2 = (int)Stations.Get(id, "dmx_t2"); float byp = Stations.Get(id, "dmx_byp"); int bs = (int)Stations.Get(id, "dmx_bypsys");
                for (int i = 0; i < n; i++)
                {
                    float integ = 100f - Stations.Get(id, "dmx_" + Stations.SysIds[i]);
                    bars[i](integ / 100f, byp > 0 && bs == i + 1 ? cBlue : Health(integ)); pct[i].text = integ.ToString("0") + "%";
                    bool a = t1 == i + 1, b = t2 == i + 1;
                    ta[i].box.color = a ? k.accent : cPanel; ta[i].label.color = a ? cInk : cWhite; tb[i].box.color = b ? k.accent : cPanel; tb[i].label.color = b ? cInk : cWhite;
                }
                bstate.text = byp > 0 && bs > 0 ? Blue + $"{Stations.SysNames[bs - 1]} in derivazione: {byp:0} s" + End
                    : now < k.pg.BypassReady ? Grey + $"Condotti in raffreddamento: {(k.pg.BypassReady - now) / 1000:0} s" + End : Good + "Pronta" + End;
            });
        }

        static void BuildSensors(Kit k)
        {
            var c = Stations.Cfg.Sensors;
            k.Section(-4f, "Scansione del bersaglio", "Tieni agganciato il bersaglio: l'analisi matura col tempo.");
            var scanBtn = k.Button("sci|scan", Kit.L, -48f, 120f, 24f, "SCANSIONE", cPanel, cWhite, 12);
            var sstate = k.Text(Kit.L + 132f, -52f, 340f, "", 11, cWhite);
            var prog = k.Meter(Kit.L, -82f, 400f, 14f); var pct = k.Text(Kit.L + 408f, -79f, 64f, "", 14, cWhite, Right);
            k.Rule(-108f);
            string[] rows = { "BERSAGLIO", "DISTANZA", "SCUDI", "SCAFO", "SISTEMI", "", "PUNTO DEBOLE" }; var val = new UILabel[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                float y = -118f - i * 26f;
                if (rows[i].Length > 0) k.Text(Kit.L, y, 110f, rows[i], 11, cGrey);
                val[i] = k.Text(Kit.L + 118f, y, 354f, "", 12, cWhite);
            }
            var cell = new UILabel[Stations.SysIds.Length];
            for (int i = 0; i < cell.Length; i++) cell[i] = k.Text(Kit.L + 118f + (i % 3) * 118f, -118f - (4 + i / 3) * 26f, 112f, "", 11, cWhite);
            k.pg.Updaters.Add((id, now) =>
            {
                bool on = Stations.Get(id, "scx_scanon") > 0; float scan = Stations.Get(id, "scx_scan");
                scanBtn.box.color = on ? k.accent : cPanel; scanBtn.label.color = on ? cInk : cWhite;
                PLShipInfoBase tgt = null; try { tgt = Stations.MyShip?.TargetShip; if (tgt != null && tgt.HasBeenDestroyed) tgt = null; } catch { }
                prog(scan / 100f, scan >= 100f ? cGood : k.accent); pct.text = tgt == null ? "-" : scan.ToString("0") + "%";
                sstate.text = tgt == null ? Grey + "Nessun bersaglio agganciato dal capitano" + End : !on ? Grey + "Sensori in ascolto passivo" + End
                    : scan >= 100f ? Good + "Analisi completa" + End : Amber + "Analisi in corso" + End;
                string none = Grey + "-" + End, wait = Grey + "dati insufficienti" + End;
                for (int i = 0; i < cell.Length; i++) cell[i].text = "";
                if (tgt == null) { for (int i = 0; i < val.Length; i++) val[i].text = i == 5 ? "" : none; return; }
                var ts = tgt.MyStats; int tid = tgt.ShipID; float dist = Stations.TargetDistance(Stations.MyShip);
                val[0].text = (string)tgt.ShipName; val[1].text = dist < 0 ? none : dist.ToString("0") + " m   fascia " + Stations.BandNames[Math.Clamp(Stations.BandOf(dist) - 1, 0, 2)].ToLowerInvariant();
                if (scan >= c.ReadoutAt && ts != null)
                {
                    val[2].text = ts.ShieldsMax > 1f ? $"{ts.ShieldsCurrent:0} / {ts.ShieldsMax:0}   ({ts.ShieldsCurrent / ts.ShieldsMax * 100f:0}%)" : "assenti";
                    val[3].text = ts.HullMax > 1f ? $"{ts.HullCurrent:0} / {ts.HullMax:0}   ({ts.HullCurrent / ts.HullMax * 100f:0}%)" : none;
                }
                else { val[2].text = wait; val[3].text = wait; }
                if (scan >= c.SubsystemAt)
                {
                    for (int i = 0; i < cell.Length; i++) { float integ = 100f - Stations.Get(tid, "dmx_" + Stations.SysIds[i]); cell[i].text = Stations.SysShort[i] + " " + HealthTag(integ) + integ.ToString("0") + "%" + End; }
                    val[4].text = ""; val[5].text = "";
                }
                else { val[4].text = Grey + $"servono {c.SubsystemAt:0}% di analisi" + End; val[5].text = ""; }
                val[6].text = scan >= 100f ? Good + $"individuato: danno allo scafo +{c.WeakPointDamage:0}%" + End : Grey + "non ancora individuato" + End;
            });
        }

        static void BuildTarget(Kit k)
        {
            var sn = Stations.Cfg.Sensors; var dg = Stations.Cfg.Damage; int n = Stations.SysIds.Length;
            k.Section(-4f, "Sottosistema bersaglio", $"Serve una scansione al {sn.SubsystemAt:0}%. Più danno al sistema, meno allo scafo.");
            var subs = new List<(UITexture box, UILabel label)>();
            subs.Add(k.Button("wep|sub|0", Kit.L, -112f, 152f, 24f, "TUTTA LA NAVE", cPanel, cWhite, 12));
            for (int i = 0; i < n; i++) subs.Add(k.Button("wep|sub|" + (i + 1), Kit.L + (i % 3) * 160f, -48f - (i / 3) * 32f, 152f, 24f, Stations.SysNames[i].ToUpperInvariant(), cPanel, cWhite, 12));
            var state = k.Text(Kit.L + 164f, -116f, 308f, "", 11, cWhite);
            k.Rule(-148f);
            k.Section(-158f, "Stato del bersaglio", "");
            var bars = new Action<float, Color>[n]; var pct = new UILabel[n];
            for (int i = 0; i < n; i++)
            {
                float x = Kit.L + (i % 2) * 240f, y = -186f - (i / 2) * 28f;
                k.Text(x, y, 96f, Stations.SysShort[i], 11, cGrey); bars[i] = k.Meter(x + 100f, y - 3f, 84f, 10f); pct[i] = k.Text(x + 188f, y, 44f, "", 11, cWhite, Right);
            }
            var info = k.Text(Kit.L, -276f, Kit.W, "", 11, cWhite);
            k.pg.Updaters.Add((id, now) =>
            {
                int sub = (int)Stations.Get(id, "wpx_sub"), tid = (int)Stations.Get(id, "scx_tgt") - 1; float scan = Stations.Get(id, "scx_scan"); bool known = tid >= 0 && scan >= sn.SubsystemAt;
                for (int i = 0; i < subs.Count; i++) { bool on = sub == i; subs[i].box.color = on ? k.accent : cPanel; subs[i].label.color = on ? cInk : cWhite; }
                state.text = sub > 0 ? Good + $"Sistema x{dg.FocusFactor:0.0}" + End + "   " + Bad + $"scafo -{dg.FocusHullPenalty:0}%" + End : Grey + "Fuoco sull'intera nave" + End;
                for (int i = 0; i < n; i++)
                {
                    if (!known) { bars[i](0f, cGrey); pct[i].text = "-"; continue; }
                    float integ = 100f - Stations.Get(tid, "dmx_" + Stations.SysIds[i]); bars[i](integ / 100f, Health(integ)); pct[i].text = integ.ToString("0") + "%";
                }
                info.text = tid < 0 ? Grey + "Nessun bersaglio agganciato." + End : !known ? Grey + $"Scansione al {scan:0}%: dati non ancora disponibili." + End
                    : scan >= 100f ? Good + $"Punto debole individuato: danno allo scafo +{sn.WeakPointDamage:0}%" + End : Amber + $"Scansione al {scan:0}%: sistemi identificati." + End;
            });
        }

        static void BuildEngage(Kit k)
        {
            var c = Stations.Cfg.Engage; float sMax = Math.Max(1f, c.ScaleMax);
            k.Section(-4f, "Distanza d'ingaggio", "Scegli una fascia e tienila: il vantaggio matura col tempo.");
            var bands = new List<(UITexture box, UILabel label)>();
            for (int i = 0; i < 3; i++) bands.Add(k.Button("plt|band|" + (i + 1), Kit.L + i * 160f, -48f, 152f, 24f, Stations.BandNames[i], cPanel, cWhite, 12));
            float a = Kit.W * Math.Clamp(c.ShortMax / sMax, 0.05f, 0.9f), b = Kit.W * Math.Clamp(c.LongMin / sMax, 0.1f, 0.95f);
            var zones = new[] { k.Box(Kit.L, -90f, a - 2f, 12f, cTrack), k.Box(Kit.L + a, -90f, b - a - 2f, 12f, cTrack), k.Box(Kit.L + b, -90f, Kit.W - b, 12f, cTrack) };
            var mark = k.Box(Kit.L, -86f, 4f, 20f, cWhite);
            var dtext = k.Text(Kit.L, -114f, 230f, "", 11, cWhite); var istate = k.Text(Kit.L + 236f, -114f, 236f, "", 11, cWhite, Right);
            k.Text(Kit.L, -140f, 90f, "POSIZIONE", 11, cGrey);
            var pos = k.Meter(Kit.L + 96f, -143f, 304f, 12f); var pct = k.Text(Kit.L + 408f, -140f, 64f, "", 13, cWhite, Right);
            var fx = k.Text(Kit.L, -164f, Kit.W, "", 11, cWhite);
            k.Rule(-190f);
            k.Text(Kit.L, -200f, Kit.W, $"CORTA (fino a {c.ShortMax:0} m): danno +{c.ShortDamage:0}%, ricarica scudi -{c.ShortRechargePenalty:0}%", 10, cGrey);
            k.Text(Kit.L, -220f, Kit.W, $"MEDIA: carica torrette +{c.MidCharge:0}%, manovra +{c.MidManeuver:0}%", 10, cGrey);
            k.Text(Kit.L, -240f, Kit.W, $"LUNGA (oltre {c.LongMin:0} m): ricarica scudi +{c.LongRecharge:0}%, firma -{c.LongSignatureCut:0}%, danno -{c.LongDamagePenalty:0}%", 10, cGrey);

            k.pg.Updaters.Add((id, now) =>
            {
                int band = (int)Stations.Get(id, "plx_band"); float p = Stations.Get(id, "plx_pos"); float dist = Stations.TargetDistance(Stations.MyShip); int cur = Stations.BandOf(dist);
                for (int i = 0; i < 3; i++)
                {
                    bool on = band == i + 1; bands[i].box.color = on ? k.accent : cPanel; bands[i].label.color = on ? cInk : cWhite;
                    zones[i].color = on ? Color.Lerp(cTrack, k.accent, 0.55f) : cTrack;
                }
                bool has = dist >= 0; if (mark.gameObject.activeSelf != has) mark.gameObject.SetActive(has);
                if (has) mark.transform.localPosition = V(Kit.L + Kit.W * Math.Clamp(dist / sMax, 0f, 1f) - 2f, -86f);
                dtext.text = has ? $"Distanza {dist:0} m   fascia {Stations.BandNames[cur - 1].ToLowerInvariant()}" : Grey + "Nessun bersaglio agganciato" + End;
                istate.text = band == 0 ? Grey + "Nessuna fascia scelta" + End : !has ? "" : cur == band ? Good + "In fascia" + End : Bad + (cur < band ? "Troppo vicino" : "Troppo lontano") + End;
                pos(p / 100f, p >= 99.5f ? cGood : k.accent); pct.text = band == 0 ? "-" : p.ToString("0") + "%";
                float f = p / 100f;
                fx.text = band == 1 ? Good + $"Danno +{c.ShortDamage * f:0}%" + End + "   " + Bad + $"ricarica scudi -{c.ShortRechargePenalty * f:0}%" + End
                    : band == 2 ? Good + $"Carica torrette +{c.MidCharge * f:0}%   manovra +{c.MidManeuver * f:0}%" + End
                    : band == 3 ? Good + $"Ricarica scudi +{c.LongRecharge * f:0}%   firma -{c.LongSignatureCut * f:0}%" + End + "   " + Bad + $"danno -{c.LongDamagePenalty * f:0}%" + End : "";
            });
        }

        static void BuildProtocols(Kit k)
        {
            var c = Stations.Cfg.Protocols;
            k.Section(-4f, "Protocolli permanenti", $"Fino a {c.MaxActive} insieme. Ognuno rallenta il recupero dell'iniziativa.");
            string[] gain = { $"Riparazioni +{c.RepairBoost:0}%", $"Danno ai sistemi nemici +{c.FocusBoost:0}%", $"Velocità di scansione +{c.ScanBoost:0}%", "Nucleo: stress smaltito più in fretta" };
            string[] cost = { $"Carica torrette -{c.RepairChargePenalty:0}%", $"Ricarica scudi -{c.FocusRechargePenalty:0}%", $"Firma EM +{c.ScanSignature:0}%", $"Spinta principale -{c.ThermalThrustPenalty:0}%" };
            var btn = new List<(UITexture box, UILabel label)>();
            for (int i = 0; i < 4; i++)
            {
                float y = -48f - i * 58f;
                btn.Add(k.Button("cap|proto|" + i, Kit.L, y, 190f, 26f, Stations.ProtocolNames[i], cPanel, cWhite, 11));
                k.Text(Kit.L + 204f, y + 1f, Kit.W - 204f, gain[i], 11, cGood); k.Text(Kit.L + 204f, y - 15f, Kit.W - 204f, cost[i], 10, cBad);
                k.Rule(y - 40f);
            }
            var state = k.Text(Kit.L, -286f, Kit.W, "", 11, cWhite);
            k.pg.Updaters.Add((id, now) =>
            {
                int pr = (int)Stations.Get(id, "cpx_proto"), on = Stations.Bits(pr);
                for (int i = 0; i < 4; i++) { bool a = ((pr >> i) & 1) != 0; btn[i].box.color = a ? k.accent : cPanel; btn[i].label.color = a ? cInk : cWhite; }
                state.text = on == 0 ? Grey + "Nessun protocollo in vigore" + End : Blue + $"{on} in vigore: recupero dell'iniziativa -{on * c.RegenCost:0.0} al secondo" + End;
            });
        }

        static void BuildJournal(Kit k)
        {
            k.Section(-4f, "Giornale di bordo", "");
            var times = new UILabel[15]; var lines = new UILabel[15];
            for (int i = 0; i < lines.Length; i++)
            {
                times[i] = k.Text(Kit.L, -30f - i * 23f, 64f, "", 11, k.accent);
                lines[i] = k.Text(Kit.L + 72f, -30f - i * 23f, Kit.W - 72f, "", 11, i == 0 ? cWhite : cGrey);
            }
            k.pg.Updaters.Add((id, now) =>
            {
                var j = Stations.Journal;
                for (int i = 0; i < lines.Length; i++)
                {
                    string e = i < j.Count ? j[j.Count - 1 - i] : ""; int cut = e.IndexOf("   ");
                    times[i].text = cut > 0 ? e[..cut] : ""; lines[i].text = cut > 0 ? e[(cut + 3)..] : i == 0 ? "Nessun evento registrato." : "";
                }
            });
        }

        static void DamageClick(PanelPage pg, string[] a, int id)
        {
            var c = Stations.Cfg.Damage; long now = clock.ElapsedMilliseconds;
            if (a[2] == "team")
            {
                string key = "dmx_t" + a[3]; int sys = int.Parse(a[4]);
                Stations.Set(id, key, (int)Stations.Get(id, key) == sys ? 0 : sys); return;
            }
            if (a[2] == "bypass")
            {
                if (now < pg.BypassReady) { Say(pg, "Condotti ancora caldi."); return; }
                if (Stations.Get(id, "core_lock") > 0) { Say(pg, "Nucleo in arresto di sicurezza."); return; }
                int worst = -1; float max = 0f;
                for (int i = 0; i < Stations.SysIds.Length; i++) { float d = Stations.Get(id, "dmx_" + Stations.SysIds[i]); if (d > max) { max = d; worst = i; } }
                if (worst < 0) { Say(pg, "Nessun sistema danneggiato."); return; }
                pg.BypassReady = now + c.BypassCooldown * 1000L;
                Stations.Set(id, "dmx_bypsys", worst + 1); Stations.Set(id, "dmx_byp", c.BypassSeconds);
                Stations.Set(id, "core_stress", Math.Min(100f, Stations.Get(id, "core_stress") + c.BypassStress));
                Service.Add("ingegneria.derivazioni", 1); Stations.LogEvent(id, "Ingegneria: derivazione d'emergenza su " + Stations.SysNames[worst].ToLowerInvariant());
                Say(pg, Stations.SysNames[worst] + " in derivazione.");
            }
        }

        static string Word(char c) => Greek[c - 'A'];

        // Posizione dell'indice di allineamento (0..1): oscilla, più veloce con lo stress.
        static float AlignPos(float stress) { double period = 2.6 - 1.6 * (stress / 100.0); double ph = (clock.ElapsedMilliseconds / 1000.0 / period) % 2.0; return (float)(ph < 1 ? ph : 2 - ph); }

        // Posizione dell'indice dell'asse in corso: ogni asse è più veloce del precedente.
        static float AxisPos(PanelPage pg, long now) { double period = 1.9 - 0.45 * pg.RouteAxis; double ph = ((now - pg.RouteAxisStart) / 1000.0 / period) % 2.0; return (float)(ph < 1 ? ph : 2 - ph); }

        // Livello di carica corrente (percento), -1 se non si sta caricando.
        static float ChargeLevel(PanelPage pg, long now) { var w = Stations.Cfg.Weapons; return pg.ChargeStart < 0 ? -1f : (now - pg.ChargeStart) / 1000f / w.ChargeSeconds * 100f; }

        static void Overload(PanelPage pg, int id, long now)
        {
            var w = Stations.Cfg.Weapons; pg.ChargeStart = -1; pg.BurstReady = now + w.BurstCooldown * 1000L;
            Stations.Set(id, "wpx_jam", w.JamSeconds); Stations.Set(id, "core_stress", Math.Min(100f, Stations.Get(id, "core_stress") + w.CoreStressPerBurst));
            Service.Add("armi.inceppamenti", 1); Stations.LogEvent(id, "Armi: condensatori saltati"); Say(pg, "Sovraccarico: i condensatori sono saltati.");
        }

        // Il gioco disegna alcuni separatori larghi più dello schermo a cui appartengono: sconfinano su quello accanto
        // e stanno in un pannello sempre in primo piano. Li accorciamo al bordo del nostro schermo.
        static void TrimIntruders(PanelPage pg)
        {
            try
            {
                var rt = pg.Screen.MyRootPanel.transform; var top = rt; while (top.parent != null) top = top.parent;
                var all = top.GetComponentsInChildren<UITexture>();
                for (int i = 0; i < all.Length; i++)
                {
                    var w = all[i]; var par = w.transform.parent;
                    if (par == null || par.name != "UIObject" || w.transform.IsChildOf(rt)) continue;
                    var wc = w.worldCorners; float x0 = 1e9f, x1 = -1e9f, y0 = 1e9f, y1 = -1e9f;
                    for (int q = 0; q < 4; q++) { var lp = rt.InverseTransformPoint(wc[q]); x0 = Math.Min(x0, lp.x); x1 = Math.Max(x1, lp.x); y0 = Math.Min(y0, lp.y); y1 = Math.Max(y1, lp.y); }
                    if (y1 - y0 > 6f || y1 < -256f || y0 > 256f || x1 - x0 < 8f) continue;
                    float ratio = w.width / (x1 - x0);
                    if (x0 < -257f && x1 > -255f) { w.pivot = UIWidget.Pivot.Left; w.width = Math.Max(2, (int)((-256f - x0) * ratio)); }
                    else if (x0 < 255f && x1 > 257f) { w.pivot = UIWidget.Pivot.Right; w.width = Math.Max(2, (int)((x1 - 256f) * ratio)); }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Separatori non accorciati: " + e.Message); }
        }

        // A pagina aperta il contenuto originale dello schermo viene spento, e riacceso alla chiusura.
        static void SetOpen(PanelPage pg, bool open)
        {
            pg.Open = open; if (open) TrimIntruders(pg);
            var root = pg.Screen.MyRootPanel.transform;
            if (open)
            {
                pg.Hidden.Clear();
                for (int i = 0; i < root.childCount; i++)
                {
                    var go = root.GetChild(i).gameObject;
                    if (go == pg.Page.gameObject || go == pg.OpenButton || !go.activeSelf) continue;
                    go.SetActive(false); pg.Hidden.Add(go);
                }
                // elementi ancora accesi che non appartengono alla pagina: cornici e linee dello schermo originale
                var all = pg.Screen.GetComponentsInChildren<UIWidget>();
                for (int i = 0; i < all.Length; i++)
                {
                    var w = all[i]; if (w == null) continue; var tr = w.transform; var go = w.gameObject;
                    if (go == root.gameObject || root.IsChildOf(tr) || tr.IsChildOf(pg.Page.transform) || tr.IsChildOf(pg.OpenButton.transform) || !go.activeSelf) continue;
                    go.SetActive(false); pg.Hidden.Add(go);
                }
            }
            else { foreach (var go in pg.Hidden) if (go != null) go.SetActive(true); pg.Hidden.Clear(); }
            pg.Page.gameObject.SetActive(open);
            if (open) Refresh(pg);
        }

        // Scadenze delle procedure che dipendono dal tempo, anche se nessuno preme nulla.
        static void Housekeeping(PanelPage pg, int id, long now)
        {
            var c = Stations.Cfg.Core; var w = Stations.Cfg.Weapons;
            if (pg.VentOrder.Length > 0 && now > pg.VentDeadline) { pg.VentOrder = ""; Stations.Set(id, "core_stress", Math.Min(100f, Stations.Get(id, "core_stress") + c.VentFailPenalty)); Say(pg, "Spurgo fallito: tempo scaduto."); }
            if (ChargeLevel(pg, now) >= w.ChargeMax) Overload(pg, id, now);
        }

        public static void Refresh(PanelPage pg)
        {
            try
            {
                var ship = Stations.MyShip; int id = ship != null ? ship.ShipID : -1; long now = clock.ElapsedMilliseconds;
                Housekeeping(pg, id, now);
                if (pg.Open)
                {
                    var root = pg.Screen.MyRootPanel.transform;
                    for (int i = 0; i < root.childCount; i++)
                    {
                        var go = root.GetChild(i).gameObject;
                        if (go.activeSelf && go != pg.Page.gameObject && go != pg.OpenButton) { go.SetActive(false); pg.Hidden.Add(go); }
                    }
                }
                for (int i = 0; i < pg.TabButtons.Count; i++) { bool on = i == pg.Tab; pg.TabButtons[i].box.color = on ? Accent[Math.Clamp(pg.Station, 0, 4)] : cPanel; pg.TabButtons[i].label.color = on ? cInk : cWhite; }
                foreach (var u in pg.Updaters) u(id, now);
                string note;
                if (now < pg.MessageUntil) note = Amber + pg.Message + End;
                else if (!Stations.CanEdit(pg.Station)) note = Bad + "Accesso negato: serve l'ufficiale di stazione o il capitano." + End;
                else
                {
                    string tab = pg.Tabs.Count > 0 ? pg.Tabs[pg.Tab].name : "";
                    var grp = Stations.Cfg.Controls.FirstOrDefault(c => c.Station == pg.Station && c.Page == tab && c.Group.Length > 0)?.Group;
                    note = grp != null ? Grey + $"Bilancio di rete: {Stations.GroupSum(id, grp):+0;-0;0} su {Stations.GroupBudget(grp):+0;-0;0}. Per dare potenza a un canale va tolta a un altro." + End : "";
                }
                pg.Note.text = note;
            }
            catch (Exception e) { Plugin.L.LogWarning("Pagina di stazione non aggiornata: " + e.Message); }
        }

        public static void RefreshAll() { foreach (var pg in pages.Values.ToArray()) { try { if (pg.Open && pg.Screen != null) Refresh(pg); } catch { } } }

        // Le pagine aperte si aggiornano da sole; il pulsante REG compare solo sugli schermi della propria nave.
        public static void Tick()
        {
            long now = clock.ElapsedMilliseconds; if (now < nextRefresh) return;
            bool anim = false; var mine = Stations.MyShip;
            foreach (var pg in pages.Values.ToArray())
            {
                try
                {
                    if (pg.Screen == null) continue;
                    if (pg.OpenButton != null && mine != null && !pg.Open)
                    {
                        var owner = pg.Screen.MyScreenHubBase?.OptionalShipInfo; bool ours = owner == null || owner.ShipID == mine.ShipID;
                        if (pg.OpenButton.activeSelf != ours) pg.OpenButton.SetActive(ours);
                    }
                    if (!pg.Open) continue;
                    string tab = pg.Tabs.Count > 0 ? pg.Tabs[pg.Tab].name : "";
                    if (tab == "Nucleo" || tab == "Manovre" || pg.ChargeStart >= 0 || pg.RouteAxis >= 0) anim = true;
                    Refresh(pg);
                }
                catch { pg.Open = false; }
            }
            nextRefresh = now + (anim ? 100 : 400);
        }

        static void CoreClick(PanelPage pg, string[] a, int id)
        {
            var k = Stations.Cfg.Core; long now = clock.ElapsedMilliseconds; float stress = Stations.Get(id, "core_stress");
            if (a[2] == "scram")
            {
                Stations.Set(id, "eng_overdrive", 0);
                foreach (var c in Stations.Cfg.Controls.Where(c => c.Group == "eps")) Stations.Set(id, c.Id, 0);
                Say(pg, "Carico staccato dal nucleo."); return;
            }
            if (a[2] == "vent")
            {
                if (pg.VentOrder.Length > 0) { Say(pg, "Spurgo già in corso."); return; }
                if (now < pg.VentReady) { Say(pg, "Condotti ancora caldi."); return; }
                var v = new List<char> { 'A', 'B', 'C', 'D' }; var sb = new StringBuilder();
                while (v.Count > 0) { int i = rng.Next(v.Count); sb.Append(v[i]); v.RemoveAt(i); }
                pg.VentOrder = sb.ToString(); pg.VentPos = 0; pg.VentDeadline = now + k.VentSeconds * 1000L; return;
            }
            if (a[2] == "v")
            {
                if (pg.VentOrder.Length == 0) { Say(pg, "Avvia prima lo spurgo."); return; }
                if (pg.VentOrder[pg.VentPos] == a[3][0])
                {
                    if (++pg.VentPos < pg.VentOrder.Length) return;
                    pg.VentOrder = ""; pg.VentReady = now + k.VentCooldown * 1000L;
                    Stations.Set(id, "core_stress", Math.Max(0f, stress - k.VentRelief)); Service.Add("ingegneria.spurghi", 1); Stations.Initiative(id, k.VentRelief > 0 ? Stations.Cfg.Captain.GainPerSuccess : 0); Stations.LogEvent(id, "Ingegneria: spurgo del plasma riuscito"); Say(pg, "Spurgo riuscito: stress ridotto.");
                }
                else { pg.VentOrder = ""; pg.VentReady = now + k.VentCooldown * 1000L; Stations.Set(id, "core_stress", Math.Min(100f, stress + k.VentFailPenalty)); Say(pg, "Valvola sbagliata: spurgo fallito."); }
                return;
            }
            if (a[2] == "align")
            {
                if (now < pg.AlignReady) { Say(pg, "Iniettori in assestamento."); return; }
                if (Stations.Get(id, "core_lock") > 0) { Say(pg, "Nucleo in arresto di sicurezza."); return; }
                pg.AlignReady = now + k.AlignCooldown * 1000L;
                if (Math.Abs(AlignPos(stress) - 0.5f) <= k.AlignWindow / 2)
                { Stations.Set(id, "core_align", k.AlignSeconds); Service.Add("ingegneria.allineamenti", 1); Stations.Initiative(id, Stations.Cfg.Captain.GainPerSuccess); Stations.LogEvent(id, "Ingegneria: matrice allineata"); Say(pg, "Matrice allineata."); }
                else { Stations.Set(id, "core_stress", Math.Min(100f, stress + k.AlignFailPenalty)); Say(pg, "Allineamento mancato: il nucleo ne risente."); }
            }
        }

        static void SciClick(PanelPage pg, string[] a, int id)
        {
            var k = Stations.Cfg.Science; long now = clock.ElapsedMilliseconds;
            if (a[2] == "band")
            {
                int band = int.Parse(a[3]);
                if (now < pg.RotateReady) { Say(pg, "Emettitori ancora in rifasamento."); return; }
                if ((int)Stations.Get(id, "scx_band") == band) { Say(pg, "È già la banda attiva: scegline un'altra."); return; }
                pg.RotateReady = now + k.RotateCooldown * 1000L;
                Stations.Set(id, "scx_band", band); Stations.Set(id, "scx_eff", 100); Stations.Set(id, "scx_dip", k.RotateDipSeconds);
                Service.Add("scienza.rotazioni", 1); Stations.LogEvent(id, "Scienza: armonica sulla banda " + Greek[band - 1].ToLowerInvariant()); Say(pg, "Frequenza ruotata su " + Greek[band - 1] + "."); return;
            }
            if (a[2] == "scan") { bool on = Stations.Get(id, "scx_scanon") > 0; Stations.Set(id, "scx_scanon", on ? 0 : 1); Say(pg, on ? "Scansione sospesa." : "Scansione avviata."); return; }
            if (a[2] == "ping")
            {
                if (now < pg.PingReady) { Say(pg, "Condensatori ancora in carica."); return; }
                pg.PingReady = now + k.PingCooldown * 1000L; Stations.Set(id, "scx_ping", k.PingSeconds); Service.Add("scienza.impulsi", 1); Say(pg, "Impulso emesso."); return;
            }
            if (a[2] == "new")
            {
                if (now < pg.DecReady) { Say(pg, "Analizzatore in riavvio."); return; }
                var sb = new StringBuilder(); for (int i = 0; i < 3; i++) sb.Append("ABCD"[rng.Next(4)]);
                pg.DecSecret = sb.ToString(); pg.DecInput = ""; pg.DecTries.Clear(); return;
            }
            if (pg.DecSecret.Length == 0) { Say(pg, "Avvia prima una nuova analisi."); return; }
            if (a[2] == "del") { if (pg.DecInput.Length > 0) pg.DecInput = pg.DecInput[..^1]; return; }
            if (a[2] == "sym")
            {
                pg.DecInput += a[3][0];
                if (pg.DecInput.Length < 3) return;
                // giusti al posto giusto, poi giusti fuori posto
                string g = pg.DecInput, sec = pg.DecSecret; int exact = 0, near = 0; var restS = new List<char>(); var restG = new List<char>();
                for (int i = 0; i < 3; i++) { if (g[i] == sec[i]) exact++; else { restS.Add(sec[i]); restG.Add(g[i]); } }
                foreach (var c in restG) if (restS.Remove(c)) near++;
                pg.DecTries.Add(string.Join("  ", g.Select(Word)) + "|" + (exact == 3 ? Good + "sequenza trovata" + End : Good + exact + " a posto" + End + "   " + Amber + near + " fuori posto" + End));
                pg.DecInput = "";
                if (exact == 3)
                { pg.DecSecret = ""; pg.DecReady = now + k.DecryptCooldown * 1000L; Stations.Set(id, "scx_sol", k.SolutionSeconds); Service.Add("scienza.decrittazioni", 1); Stations.Initiative(id, Stations.Cfg.Captain.GainPerSuccess * 2); Stations.LogEvent(id, "Scienza: firma nemica decrittata"); Say(pg, "Firma decrittata: soluzione tattica alle armi."); }
                else if (pg.DecTries.Count >= k.DecryptAttempts)
                { Say(pg, "Analisi fallita: la sequenza era " + string.Join(" ", sec.Select(Word)) + "."); pg.DecSecret = ""; pg.DecReady = now + k.DecryptCooldown * 1000L; }
            }
        }

        static void WepClick(PanelPage pg, string[] a, int id)
        {
            var w = Stations.Cfg.Weapons; long now = clock.ElapsedMilliseconds;
            if (a[2] == "doc")
            {
                int doc = int.Parse(a[3]);
                if ((int)Stations.Get(id, "wpx_doc") == doc) { Say(pg, "È già la dottrina in uso."); return; }
                Stations.Set(id, "wpx_doc", doc); Stations.Set(id, "wpx_q", 0); Say(pg, "Nuova dottrina: la soluzione riparte da zero."); return;
            }
            if (a[2] == "aim") { Stations.Set(id, "wpx_aim", int.Parse(a[3])); return; }
            if (a[2] == "sub")
            {
                int sub = int.Parse(a[3]);
                if (sub > 0 && (Stations.Get(id, "scx_tgt") <= 0 || Stations.Get(id, "scx_scan") < Stations.Cfg.Sensors.SubsystemAt)) { Say(pg, "Serve prima la scansione del bersaglio dalla Scienza."); return; }
                Stations.Set(id, "wpx_sub", sub); if (sub > 0) Stations.LogEvent(id, "Armi: fuoco mirato su " + Stations.SysNames[sub - 1].ToLowerInvariant()); return;
            }
            if (a[2] == "charge")
            {
                if (pg.ChargeStart >= 0) { Say(pg, "Carica già in corso."); return; }
                if (Stations.Get(id, "wpx_jam") > 0) { Say(pg, "Condensatori fuori uso."); return; }
                if (now < pg.BurstReady) { Say(pg, "Condensatori in recupero."); return; }
                pg.ChargeStart = now; return;
            }
            if (a[2] == "fire")
            {
                float lvl = ChargeLevel(pg, now);
                if (lvl < 0) { Say(pg, "Niente da scaricare: avvia la carica."); return; }
                if (lvl > w.OverloadAt) { Overload(pg, id, now); return; }
                pg.ChargeStart = -1; pg.BurstReady = now + w.BurstCooldown * 1000L;
                float pct = (float)Math.Round(w.BurstMaxDamage * lvl / 100f);
                Stations.Set(id, "wpx_burstp", pct); Stations.Set(id, "wpx_burst", w.BurstSeconds);
                Stations.Set(id, "core_stress", Math.Min(100f, Stations.Get(id, "core_stress") + w.CoreStressPerBurst * lvl / 100f));
                Service.Add("armi.raffiche", 1); if (lvl >= 80f) Stations.Initiative(id, Stations.Cfg.Captain.GainPerSuccess); Stations.LogEvent(id, $"Armi: raffica al {lvl:0}%"); Say(pg, $"Raffica al {lvl:0}%: danno +{pct:0}%.");
            }
        }

        static void PilotClick(PanelPage pg, string[] a, int id)
        {
            var k = Stations.Cfg.Pilot; long now = clock.ElapsedMilliseconds;
            if (a[2] == "trim") { float tr = Stations.Get(id, "plx_trim") + (a[3] == "L" ? -k.TrimStep : k.TrimStep); Stations.Set(id, "plx_trim", Math.Clamp(Math.Abs(tr) < 0.01f ? 0.01f : tr, -100f, 100f)); return; }
            if (a[2] == "evade")
            {
                if (now < pg.EvadeReady) { Say(pg, "Manovra evasiva in ricarica."); return; }
                pg.EvadeReady = now + k.EvadeCooldown * 1000L; Stations.Set(id, "plx_evade", k.EvadeSeconds); Service.Add("pilota.manovre", 1); Stations.LogEvent(id, "Pilota: manovra evasiva"); Say(pg, "Manovra evasiva!"); return;
            }
            if (a[2] == "burn")
            {
                if (now < pg.BurnReady) { Say(pg, "Spinta d'emergenza in ricarica."); return; }
                if (Stations.Get(id, "core_lock") > 0) { Say(pg, "Nucleo in arresto di sicurezza."); return; }
                pg.BurnReady = now + k.BurnCooldown * 1000L; Stations.Set(id, "plx_burn", k.BurnSeconds);
                Stations.Set(id, "core_stress", Math.Min(100f, Stations.Get(id, "core_stress") + k.BurnCoreStress));
                Service.Add("pilota.manovre", 1); Say(pg, "Spinta d'emergenza: il nucleo si scalda."); return;
            }
            if (a[2] == "band")
            {
                int band = int.Parse(a[3]); bool same = (int)Stations.Get(id, "plx_band") == band;
                Stations.Set(id, "plx_pos", 0); Stations.Set(id, "plx_band", same ? 0 : band);
                Say(pg, same ? "Fascia d'ingaggio annullata." : "Fascia d'ingaggio: " + Stations.BandNames[band - 1].ToLowerInvariant() + "."); return;
            }
            if (a[2] == "drift") { bool on = Stations.Get(id, "plx_drift") > 0; Stations.Set(id, "plx_drift", on ? 0 : 1); Say(pg, on ? "Motori a regime." : "Deriva silenziosa."); return; }
            if (a[2] == "route")
            {
                if (a[3] == "start")
                {
                    if (pg.RouteAxis >= 0) { Say(pg, "Calcolo già in corso."); return; }
                    if (now < pg.RouteReady) { Say(pg, "Navigatore in ricalcolo."); return; }
                    pg.RouteAxis = 0; pg.RouteAxisStart = now; pg.RouteShown = false; return;
                }
                if (pg.RouteAxis < 0) { Say(pg, "Avvia prima il calcolo."); return; }
                pg.RouteErr[pg.RouteAxis] = AxisPos(pg, now) - 0.5f;
                if (++pg.RouteAxis < 3) { pg.RouteAxisStart = now; return; }
                float avg = (Math.Abs(pg.RouteErr[0]) + Math.Abs(pg.RouteErr[1]) + Math.Abs(pg.RouteErr[2])) / 3f;
                float q = (float)Math.Round(Math.Clamp(100f * (1f - (avg - 0.03f) * 2.6f), 0f, 100f));
                pg.RouteAxis = -1; pg.RouteShown = true; pg.RouteReady = now + k.RouteCooldown * 1000L;
                if (q >= 1f) { Stations.Set(id, "plx_routeq", q); Stations.Set(id, "plx_route", k.RouteSeconds); Service.Add("pilota.rotte", 1); if (q >= 50f) Stations.Initiative(id, Stations.Cfg.Captain.GainPerSuccess); Stations.LogEvent(id, $"Pilota: rotta calcolata al {q:0}%"); Say(pg, $"Rotta calcolata al {q:0}%."); }
                else Say(pg, "Calcolo fallito: assi fuori tolleranza.");
            }
        }

        static void CaptainClick(PanelPage pg, string[] a, int id)
        {
            var c = Stations.Cfg.Captain; float ini = Stations.Get(id, "cpx_ini"); if (ini <= 0) { ini = c.Start; Stations.Set(id, "cpx_ini", ini); }
            if (a[2] == "order")
            {
                int dir = int.Parse(a[3]);
                if (dir == 0) { Stations.Set(id, "cpx_dir", 0); Say(pg, "Ordine revocato."); return; }
                if (Stations.Get(id, "cpx_tired") > 0) { Say(pg, "Equipaggio affaticato: lascialo respirare."); return; }
                if (ini < 5f) { Say(pg, "Iniziativa insufficiente."); return; }
                Stations.Set(id, "cpx_dir", dir); Service.Add("comando.ordini", 1); Stations.LogEvent(id, "Capitano: ordine " + Orders[dir - 1].ToLowerInvariant()); Say(pg, "Ordine impartito: " + Orders[dir - 1] + "."); return;
            }
            if (a[2] == "proto")
            {
                int bit = 1 << int.Parse(a[3]), pr = (int)Stations.Get(id, "cpx_proto");
                if ((pr & bit) == 0 && Stations.Bits(pr) >= Stations.Cfg.Protocols.MaxActive) { Say(pg, "Troppi protocolli in vigore: revocane uno."); return; }
                pr ^= bit; Stations.Set(id, "cpx_proto", pr);
                string pn = Stations.ProtocolNames[int.Parse(a[3])].ToLowerInvariant();
                Stations.LogEvent(id, "Capitano: protocollo " + pn + ((pr & bit) != 0 ? " in vigore" : " revocato")); Say(pg, (pr & bit) != 0 ? "Protocollo in vigore." : "Protocollo revocato."); return;
            }
            if (a[2] == "int")
            {
                if (ini < c.InterventionCost) { Say(pg, "Iniziativa insufficiente per un intervento."); return; }
                if (a[3] == "energy") { Stations.Set(id, "core_stress", Math.Max(0f, Stations.Get(id, "core_stress") - c.EmergencyRelief)); Say(pg, "Energia d'emergenza: il nucleo respira."); }
                else if (a[3] == "fire") { if (Stations.Get(id, "wpx_doc") <= 0) { Say(pg, "Le armi non hanno una dottrina attiva."); return; } Stations.Set(id, "wpx_q", 100); Say(pg, "Fuoco a volontà: soluzione di tiro completa."); }
                else if (a[3] == "shields") { if (Stations.Get(id, "scx_band") <= 0) { Say(pg, "La scienza non ha un'armonica attiva."); return; } Stations.Set(id, "scx_eff", 100); Say(pg, "Scudi al massimo: armonica ripristinata."); }
                Stations.Set(id, "cpx_ini", Math.Max(0.01f, ini - c.InterventionCost)); Service.Add("comando.interventi", 1);
            }
        }

        // Restituisce true se il clic era nostro (o va inghiottito perché la pagina copre lo schermo).
        public static bool Click(PLUIScreen sc, UIWidget b)
        {
            var pg = Of(sc); if (pg == null || b == null) return false;
            string n = b.name;
            if (n == null || !n.StartsWith(Prefix)) return pg.Open; // pagina aperta: i comandi sottostanti non rispondono
            var a = n.Split('|');
            var ship = sc.MyScreenHubBase?.OptionalShipInfo; var mine = Stations.MyShip;
            if (a[1] == "open") { if (ship != null && mine != null && ship.ShipID != mine.ShipID) return true; SetOpen(pg, !pg.Open); return true; }
            if (!pg.Open) return true;
            if (a[1] == "close") { SetOpen(pg, false); return true; }
            if (a[1] == "tab") { int i = int.Parse(a[2]); if (i < pg.Tabs.Count) { pg.Tab = i; for (int j = 0; j < pg.Tabs.Count; j++) pg.Tabs[j].go.SetActive(j == i); } Refresh(pg); return true; }
            if (mine == null || !Stations.CanEdit(pg.Station)) { Refresh(pg); return true; }
            if (a[1] == "p") Stations.ApplyPreset(mine.ShipID, Stations.Cfg.Presets[int.Parse(a[2])]);
            else if (a[1] == "core") CoreClick(pg, a, mine.ShipID);
            else if (a[1] == "sci") SciClick(pg, a, mine.ShipID);
            else if (a[1] == "wep") WepClick(pg, a, mine.ShipID);
            else if (a[1] == "plt") PilotClick(pg, a, mine.ShipID);
            else if (a[1] == "cap") CaptainClick(pg, a, mine.ShipID);
            else if (a[1] == "dmg") DamageClick(pg, a, mine.ShipID);
            else if (a[1] == "c")
            {
                var c = Stations.Find(a[2]); if (c == null) return true; float v = Stations.Get(mine.ShipID, c.Id);
                string err = Stations.TrySet(mine.ShipID, c.Id, a[3] == "+" ? v + c.Step : a[3] == "-" ? v - c.Step : 0f);
                if (err != null) Say(pg, err);
            }
            RefreshAll();
            return true;
        }

    }

    [HarmonyPatch]
    static class PanelButtons
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            Stations.Cfg.Panels.Keys.Select(n => typeof(PLUIScreen).Assembly.GetType(n)).Where(t => t != null)
                .Select(t => t.GetMethod("SetupUI", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)).Where(m => m != null);

        static void Postfix(PLUIScreen __instance)
        {
            try
            {
                if (!Stations.Cfg.Enabled) return;
                if (Stations.Cfg.Panels.TryGetValue(__instance.GetIl2CppType().Name, out int station)) Panels.Build(__instance, station);
            }
            catch (Exception e) { Plugin.L.LogWarning($"Pagina regolazioni non creata su {__instance.GetIl2CppType().Name}: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    static class PanelClicks
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            Stations.Cfg.Panels.Keys.Select(n => typeof(PLUIScreen).Assembly.GetType(n)).Where(t => t != null)
                .Select(t => t.GetMethod("OnButtonClick", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                          ?? typeof(PLUIScreen).GetMethod("OnButtonClick", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                .Where(m => m != null).Distinct();

        static bool Prefix(PLUIScreen __instance, UIWidget inButton)
        {
            try { return !Panels.Click(__instance, inButton); }
            catch (Exception e) { Plugin.L.LogWarning("Clic sul pannello non gestito: " + e.Message); return true; }
        }
    }

    // Puntamento delle armi: modifica il danno che le torrette di una nave infliggono a scudi e scafo del bersaglio.
    [HarmonyPatch(typeof(PLShipStats), nameof(PLShipStats.TakeHullDamage))]
    static class AimHull
    {
        public static int Calls, Mine, Modified; public static float LastIn, LastOut;
        static void Prefix(PLShipStats __instance, ref float inDmg, PLShipInfoBase attackingShip, PLTurret turret)
        {
            if (!Stations.Cfg.Enabled) return;
            try
            {
                Calls++;
                var atk = attackingShip ?? turret?.ShipStats?.Ship;
                if (atk != null && Stations.Any && Stations.Cfg.Weapons.Enabled)
                {
                    var me = Stations.MyShip; if (me != null && me.ShipID == atk.ShipID) Mine++;
                    int aim = (int)Stations.Get(atk.ShipID, "wpx_aim"); var w = Stations.Cfg.Weapons; LastIn = inDmg;
                    if (aim == 2) inDmg *= 1f + w.AimHullBonus / 100f; else if (aim == 1) inDmg *= 1f - w.AimShieldsHullPenalty / 100f;
                    if (aim != 0) Modified++; LastOut = inDmg;
                }
                Stations.HullHit(__instance, atk, ref inDmg);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PLShipStats), nameof(PLShipStats.TakeShieldDamage))]
    static class AimShields
    {
        public static int Calls, Mine, Modified, NoTurret; public static float LastIn, LastOut;
        static void Prefix(PLShipStats __instance, ref float inDmg, PLTurret turret)
        {
            if (!Stations.Any || !Stations.Cfg.Weapons.Enabled) return;
            try
            {
                Calls++;
                var vsh = __instance.Ship; if (vsh != null && Stations.Get(vsh.ShipID, "plx_evade") > 0) inDmg *= 1f - Stations.Cfg.Pilot.EvadeIncoming / 100f;
                var atk = turret?.ShipStats?.Ship; if (atk == null) { NoTurret++; return; }
                var me = Stations.MyShip; if (me != null && me.ShipID == atk.ShipID) Mine++;
                int aim = (int)Stations.Get(atk.ShipID, "wpx_aim"); var w = Stations.Cfg.Weapons; LastIn = inDmg;
                if (aim == 1) inDmg *= 1f + w.AimShieldsBonus / 100f; else if (aim == 2) inDmg *= 1f - w.AimHullShieldPenalty / 100f;
                if (aim != 0) Modified++; LastOut = inDmg;
            }
            catch { }
        }
    }
}

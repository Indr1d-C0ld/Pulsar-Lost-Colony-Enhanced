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
        public static string Notice = "", NoticeTitle = "", NoticeColor = "#F2A65A"; public static float NoticeUntil;
        // Gli annunci arrivati a breve distanza si mettono in coda: ognuno resta a schermo almeno 4 secondi.
        static readonly Queue<(string title, string text, string color)> notices = new();
        static void ShowNotice(string title, string text, string color = "#F2A65A")
        {
            float now = Time.realtimeSinceStartup;
            if (Notice.Length > 0 && now < NoticeUntil && NoticeUntil - now > 6f) { if (notices.Count < 8) notices.Enqueue((title, text, color)); return; }
            NoticeTitle = title; Notice = text; NoticeColor = color; NoticeUntil = now + 10f;
        }
        // chiamato dall'interfaccia: quando l'annuncio corrente ha avuto il suo tempo, passa al successivo
        public static void NextNotice()
        {
            float now = Time.realtimeSinceStartup;
            if (notices.Count > 0 && (now >= NoticeUntil || NoticeUntil - now <= 6f)) { var n = notices.Dequeue(); NoticeTitle = n.title; Notice = n.text; NoticeColor = n.color; NoticeUntil = now + 10f; }
        }
        // Ritrovamento di un pezzo raro: solo su questa macchina (ognuna vede i pezzi arrivare nella propria stiva).
        public static void ShowFound(string tier, string name, string color) => ShowNotice("RITROVAMENTO · " + tier, name, color);

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
            if (gain > 0) gain *= Morale.InitiativeFactor; // il morale dell'equipaggio accelera (o rallenta) l'iniziativa
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
            Traffic.NoteHit(victim, atk);
            dmg *= (1f + 0.04f * Talents.R(atk, "artigliere") + 0.02f * Talents.R(atk, "analista")) * (1f - 0.03f * Talents.R(victim, "evasione") - 0.03f * Talents.R(victim, "corazza")); // talenti, uguali per tutti
            int vid = victim.ShipID, sub = 0; float factor = 1f; bool ours = false;
            if (atk != null && state.Count > 0)
            {
                int aid = atk.ShipID;
                if ((int)Get(aid, "scx_tgt") == vid + 1)
                {
                    float scan = Get(aid, "scx_scan"); int want = (int)Get(aid, "wpx_sub");
                    if (scan >= 100f) dmg *= 1f + Cfg.Sensors.WeakPointDamage / 100f + Loot.Bonus(aid, "weakpoint");
                    if (dg.Enabled && want > 0 && want <= SysIds.Length && scan >= Cfg.Sensors.SubsystemAt)
                    { sub = want; factor = dg.FocusFactor * (Protocol(aid, 1) ? 1f + Cfg.Protocols.FocusBoost / 100f : 1f) * (1f + Loot.Bonus(aid, "subsystem")); dmg *= 1f - dg.FocusHullPenalty / 100f; }
                }
                var me = MyShip; ours = me != null && me.ShipID == aid;
            }
            if (state.Count > 0 && Get(vid, "plx_evade") > 0) dmg *= 1f - Cfg.Pilot.EvadeIncoming / 100f;
            dmg *= 1f - Math.Clamp(Loot.Bonus(vid, "incoming"), 0f, 0.5f);
            if (!dg.Enabled) return;
            bool host = false; try { host = PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { }
            float max = vs.HullMax; if (!host || max <= 1f) return;
            float pts = dmg / max * 100f * dg.PointsPerHullPercent * factor * (1f - 0.1f * Talents.R(victim, "meccanico")) * (1f - Math.Clamp(Loot.Bonus(vid, "sys_resist"), -1f, 0.7f)); if (pts < 0.05f) return;
            if (sub == 0) sub = EnemyAI.PickSub(atk, victim); // i nemici mirano ai sistemi secondo il loro ruolo
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
            if (msg.StartsWith("req|officina|")) { Loot.OnRequest(msg); return; }
            if (msg.StartsWith("req|bacheca|")) { Contracts.OnRequest(msg); return; }
            if (msg.StartsWith("board|")) { Contracts.Receive(msg); return; }
            if (msg.StartsWith("news|")) { News.Receive(msg); return; }
            if (msg.StartsWith("gal|")) { GalaxyView.Receive(msg); return; }
            if (msg.StartsWith("enc|")) { Encounters.Receive(msg); return; }
            if (msg.StartsWith("req|incontro|")) { Encounters.OnRequest(msg); return; }
            if (msg.StartsWith("req|cantiere|")) { Shipyard.OnRequest(msg); return; }
            if (msg.StartsWith("req|chiamata|")) { Hail.OnRequest(msg); return; }
            if (msg.StartsWith("req|sonda|")) { Probes.OnRequest(msg); return; }
            if (msg.StartsWith("req|basi|")) { Bases.OnRequest(msg); return; }
            if (msg.StartsWith("req|mercato|")) { Market.OnRequest(msg); return; }
            if (msg.StartsWith("req|ricerca|")) { Research.OnRequest(msg); return; }
            if (msg.StartsWith("req|indagini|")) { Mysteries.OnRequest(msg); return; }
            if (msg.StartsWith("req|passeggeri|")) { Passengers.OnRequest(msg); return; }
            if (msg.StartsWith("req|equipaggio|")) { CrewMind.OnRequest(msg); return; }
            if (msg.StartsWith("req|manutenzione|")) { Wear.OnRequest(msg); return; }
            if (msg.StartsWith("req|fabbrica|")) { Fabrication.OnRequest(msg); return; }
            if (msg.StartsWith("req|abbordaggio|")) { Boarding.OnRequest(msg); return; }
            if (msg.StartsWith("req|talenti|")) { Talents.OnRequest(msg); return; }
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
                load *= 1f - Math.Clamp(Loot.Bonus(ship, "core_load"), -1f, 0.6f);
                stress = Math.Clamp(stress + load - k.Cooling * (1f + Loot.Bonus(ship, "core_cooling")) - ((int)Get(ship, "cpx_dir") == 3 ? Cfg.Captain.EfficiencyCooling : 0f) - (Protocol(ship, 3) ? Cfg.Protocols.ThermalCooling : 0f), 0f, 100f);
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
                    ini += c.Regen * (1f + Loot.Bonus(ship, "initiative")) - (dirNow > 0 ? c.OrderDrain : 0f) - (Cfg.Protocols.Enabled ? Bits((int)Get(ship, "cpx_proto")) * Cfg.Protocols.RegenCost : 0f);
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
                    float rate = (alert >= 2 ? Cfg.Science.DecayRed : alert == 1 ? Cfg.Science.DecayYellow : Cfg.Science.DecayCalm) * (1f - Math.Clamp(Loot.Bonus(ship, "harmonic_decay"), 0f, 0.85f));
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
                float nd = Math.Max(0f, dm - dg.PassiveRepair - teams * dg.RepairPerTeam * boost * (1f + Loot.Bonus(ship, "repair_speed") + (Stations.MyShip != null && ship == Stations.MyShip.ShipID ? PawnLoot.Crew("repair_speed") : 0f)));
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
                * (Protocol(ship, 2) ? 1f + Cfg.Protocols.ScanBoost / 100f : 1f) * (1f - Get(ship, "dmx_sensors") / 100f * 0.7f) * (1f + Loot.Bonus(ship, "scan_speed"));
            float ns = Math.Min(100f, scan + Math.Max(0.2f, rate));
            SetLocal(ship, "scx_scan", ns);
            if (scan < sn.SubsystemAt && ns >= sn.SubsystemAt && ns < 100f) LogEvent(ship, "Scienza: sistemi del bersaglio identificati");
            if (ns >= 100f) { LogEvent(ship, "Scienza: punto debole del bersaglio individuato"); Service.Add("scienza.scansioni", 1); Initiative(ship, Cfg.Captain.GainPerSuccess); }
        }

        static void SimulateEngage(PLShipInfo mine, int ship)
        {
            var en = Cfg.Engage; int band = (int)Get(ship, "plx_band"); if (!en.Enabled || band <= 0) return;
            bool inb = BandOf(TargetDistance(mine)) == band; float pos = Get(ship, "plx_pos");
            float np = Math.Clamp(pos + (inb ? en.PosGain * (1f + Loot.Bonus(ship, "engage_gain")) : -en.PosLoss), 0f, 100f);
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
        public int CodexIdx, BaseKind, MystPage; public string CodexCat = "Fazioni";
        public int WorkSel = -1, WorkPage; public string WorkArmed = ""; public long WorkArmedUntil; public readonly List<int> WorkRows = new();
    }

    static class Panels
    {
        public const string Prefix = "Mel|";
        static readonly Dictionary<IntPtr, PanelPage> pages = new(); public static int Skipped, Released;
        public static int Count => pages.Count;
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
        // secondo colore LCARS per stazione: lilla, verde acqua, viola, arancio, rosso arancio
        static readonly Color[] Second = { new Color(0.78f, 0.62f, 0.95f, 1f), new Color(0.45f, 0.86f, 0.92f, 1f), new Color(0.68f, 0.60f, 1f, 1f), new Color(1f, 0.70f, 0.40f, 1f), new Color(1f, 0.58f, 0.32f, 1f) };
        static Color Dim(Color c, float k, float a = 1f) => new Color(c.r * k, c.g * k, c.b * k, a);
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
            public PLUIScreen sc; public PanelPage pg; public Transform t; public Color accent, second;
            public const float L = 16f, W = 472f; // margine sinistro e larghezza utile
            public float ox, oy;                  // origine (serve per gli elementi figli diretti dello schermo)

            public UITexture Box(float x, float y, float w, float h, Color c) => sc.CreateBox(V(x + ox, y + oy), new Vector2(w, h), c, t, TL);

            // Testo in una colonna di larghezza fissa: se è più lungo si restringe, non esce mai dalla colonna.
            public UILabel Text(float x, float y, float w, string s, int size, Color c, NGUIText.Alignment al = NGUIText.Alignment.Left)
            {
                var l = sc.CreateLabel(s, V(x + ox, y + oy), size, c, t, TL);
                // le etichette del gioco hanno una scala propria: la colonna va espressa nelle loro unità
                float sx = Math.Max(0.01f, l.transform.localScale.x), sy = Math.Max(0.01f, l.transform.localScale.y);
                Look.Apply(l);
                l.overflowMethod = UILabel.Overflow.ShrinkContent; l.multiLine = false; l.alignment = al;
                l.width = (int)(w / sx); l.height = (int)((size + 8) / sy);
                return l;
            }

            // Pulsante piatto: riquadro pieno con la scritta centrata.
            public (UITexture box, UILabel label) Button(string name, float x, float y, float w, float h, string text, Color fill, Color ink, int size = 12)
            {
                var b = Box(x, y, w, h, fill); b.gameObject.name = Prefix + name; sc.AllButtons.Add(b);
                if (h >= 20f && w >= 44f) Look.Sliced(b, Look.Pill, Math.Min(11f, h / 2f)); else Look.Sliced(b, Look.Round, Math.Min(7f, Math.Min(w, h) / 2f));
                // cornice luminosa, figlia del pulsante (si nasconde con lui)
                var fr = sc.CreateBox(V(0f, 0f), new Vector2(w, h), new Color(accent.r, accent.g, accent.b, 0.45f), b.transform, TL); Look.Sliced(fr, Look.Frame, Math.Min(7f, Math.Min(w, h) / 2f));
                var l = Text(x + 4f, y - (h - size - 6f) / 2f, w - 8f, text, size, ink, NGUIText.Alignment.Center);
                return (b, l);
            }

            public void Rule(float y) { var r = Box(L, y, W, 1f, Dim(accent, 0.55f, 0.7f)); Look.Simple(r, Look.Fade); }

            // Titolo di sezione con tacca colorata e riga d'aiuto.
            public void Section(float y, string title, string help)
            {
                var n = Box(L, y - 2f, 5f, 16f, accent); Look.Sliced(n, Look.Small, 2f);
                var n2 = Box(L + 7f, y - 2f, 2f, 16f, second); Look.Sliced(n2, Look.Small, 1f);
                Text(L + 14f, y, W - 14f, title.ToUpperInvariant(), 13, cWhite);
                if (help.Length > 0) Text(L + 14f, y - 19f, W - 14f, help, 10, cGrey);
            }

            // Barra di livello 0..1.
            public Action<float, Color> Meter(float x, float y, float w, float h)
            {
                var tr = Box(x, y, w, h, cTrack); Look.Sliced(tr, Look.Small, Math.Min(3f, h / 2f)); var fill = Box(x, y, 2f, h, accent); Look.Tiled(fill, Look.Seg);
                // una frazione negativa nasconde anche la traccia (righe vuote delle liste)
                return (frac, col) => { bool show = frac >= 0f; if (tr.gameObject.activeSelf != show) tr.gameObject.SetActive(show); frac = Math.Clamp(frac, 0f, 1f); bool on = frac > 0.004f; if (fill.gameObject.activeSelf != on) fill.gameObject.SetActive(on); if (on) { fill.width = Math.Max(2, (int)(w * frac)); fill.color = col; } };
            }

            // Barra a due versi attorno allo zero (per comandi da min a max).
            public Action<float, float, float, Color> Bipolar(float x, float y, float w, float h)
            {
                var tr = Box(x, y, w, h, cTrack); Look.Sliced(tr, Look.Small, Math.Min(3f, h / 2f)); var fill = Box(x, y, 2f, h, accent); Look.Tiled(fill, Look.Seg); var zero = Box(x, y - 2f, 2f, h + 4f, cGrey);
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
                var tr = Box(x, y, w, h, cTrack); Look.Sliced(tr, Look.Small, Math.Min(3f, h / 2f)); var zone = Box(x + w * lo, y, w * (hi - lo), h, Color.Lerp(cTrack, accent, 0.45f)); Look.Tiled(zone, Look.Seg);
                var mark = Box(x, y + 4f, 4f, h + 8f, cWhite); Look.Sliced(mark, Look.Small, 2f);
                return (pos, col) => { bool on = pos >= 0f; if (mark.gameObject.activeSelf != on) mark.gameObject.SetActive(on); if (on) { mark.transform.localPosition = V(x + w * Math.Clamp(pos, 0f, 1f) - 2f, y + 4f); mark.color = col; } };
            }
        }

        public static PanelPage Of(PLUIScreen s) => pages.TryGetValue(s.Pointer, out var p) ? p : null;

        public static void Say(PanelPage pg, string msg) { pg.Message = msg; pg.MessageUntil = clock.ElapsedMilliseconds + 4000; }

        public static void Build(PLUIScreen sc, int station)
        {
            var root = sc.MyRootPanel; if (root == null) return;
            // solo gli schermi della nostra nave: ogni nave del traffico ha i suoi schermi, e costruire un pannello completo
            // per ciascuno (poi mai rilasciato) faceva crescere la memoria di decine di MB a ogni salto
            try { var owner = sc.MyScreenHubBase?.OptionalShipInfo; var mine = Stations.MyShip; if (owner != null && mine != null && owner.ShipID != mine.ShipID) { Skipped++; return; } } catch { }
            var pg = new PanelPage { Screen = sc, Station = station };
            var accent = Accent[Math.Clamp(station, 0, Accent.Length - 1)];
            // pulsante di accesso nella barra del titolo dello schermo, piatto come il resto
            var second = Second[Math.Clamp(station, 0, Second.Length - 1)];
            var rk = new Kit { sc = sc, pg = pg, t = root.transform, accent = accent, second = second, ox = -256f, oy = 256f };
            pg.OpenButton = sc.CreateBlankPanel("MelchiorAccesso", TopLeft(440f, -1f), new Vector2(68f, 17f), root.transform, TL).gameObject;
            var ok = new Kit { sc = sc, pg = pg, t = pg.OpenButton.transform, accent = accent, second = second };
            ok.Button("open", 0f, 0f, 68f, 17f, "REG", accent, cInk, 11);
            pg.Page = sc.CreatePanel(" ", TopLeft(4f, -20f), new Vector2(504f, 488f), cBg, root.transform, TL);
            var k = new Kit { sc = sc, pg = pg, t = pg.Page.transform, accent = accent, second = second };
            string name = Stations.Cfg.StationNames[Math.Clamp(station, 0, Stations.Cfg.StationNames.Length - 1)];

            // intestazione: blocco colorato col nome della stazione, filetto, pulsante di chiusura
            // fondo: griglia tecnica, righe di scansione, vignettatura
            // colori pieni (alfa 0 o 1): lo schermo resta opaco anche nella texture di rendering
            var gd = k.Box(0f, 0f, 504f, 488f, Color.Lerp(cBg, accent, 0.07f)); Look.Tiled(gd, Look.Grid);
            var sl = k.Box(0f, 0f, 504f, 488f, Dim(cBg, 0.55f)); Look.Tiled(sl, Look.Scan);
            var sw = k.Box(0f, 0f, 504f, 3f, Color.Lerp(cBg, accent, 0.22f));
            // intestazione LCARS: gomito, barra laterale a segmenti, blocco col nome, barra sottile, spia, chiusura
            var el = k.Box(0f, 0f, Look.ElbowW, Look.ElbowH, accent); Look.Simple(el, Look.Elbow);
            var s1 = k.Box(0f, -Look.ElbowH + 6f, Look.Side, 150f, accent); Look.Sliced(s1, Look.Small, 3f);
            var s2 = k.Box(0f, -Look.ElbowH - 148f, Look.Side, 96f, second); Look.Sliced(s2, Look.Small, 3f);
            var s3 = k.Box(0f, -Look.ElbowH - 248f, Look.Side, 488f - Look.ElbowH - 248f, Dim(accent, 0.7f)); Look.Sliced(s3, Look.Small, 3f);
            k.Box(Look.ElbowW, 0f, 116f, Look.Bar, accent); k.Text(Look.ElbowW + 4f, -4f, 110f, name.ToUpperInvariant(), 14, cInk);
            var hb = k.Box(160f, -9f, 196f, 6f, second); Look.Sliced(hb, Look.Small, 3f);
            var led = k.Box(362f, -4f, 16f, 16f, second); Look.Simple(led, Look.Glow);
            var ledCore = k.Box(367f, -9f, 6f, 6f, cWhite); Look.Sliced(ledCore, Look.Small, 3f);
            k.Button("close", 394f, 0f, 104f, 24f, "CHIUDI", cPanel, cWhite, 12);
            pg.Updaters.Add((id, now) =>
            {
                float tsec = clock.ElapsedMilliseconds / 1000f;
                float p = (tsec % 6f) / 6f; sw.transform.localPosition = V(0f, -p * 470f + 10f);
                float pulse = 0.55f + 0.45f * (float)Math.Sin(tsec * 2.4f);
                led.color = new Color(second.r, second.g, second.b, 0.35f + 0.5f * pulse); ledCore.color = Color.Lerp(second, cWhite, pulse);
                hb.color = new Color(second.r, second.g, second.b, 0.75f + 0.25f * (float)Math.Sin(tsec * 0.9f));
            });

            // schede
            var names = new List<string>();
            if (station == 0) { names.Add("Assetti"); if (Stations.Cfg.Captain.Enabled) { names.Add("Ordini"); names.Add("Quadro"); if (Stations.Cfg.Protocols.Enabled) names.Add("Protocolli"); names.Add("Bacheca"); names.Add("Giornale"); names.Add("Galassia"); names.Add("Passeggeri"); names.Add("Equipaggio"); } }
            names.AddRange(Stations.Cfg.Controls.Where(c => c.Station == station).Select(c => c.Page).Distinct());
            if (station == 4 && Stations.Cfg.Core.Enabled) names.Add("Nucleo");
            if (station == 4 && Stations.Cfg.Damage.Enabled) names.Add("Danni");
            if (station == 4 && Rarity.Cfg.Enabled) names.Add("Officina");
            if (station == 4) { names.Add("Cantiere"); names.Add("Basi"); names.Add("Manutenzione"); names.Add("Fabbrica"); }
            if (station == 2 && Stations.Cfg.Science.Enabled) { names.Add("Scudi"); names.Add("Analisi"); }
            if (station == 2 && Stations.Cfg.Sensors.Enabled) names.Add("Sensori");
            if (station == 2) { names.Add("Sonde"); names.Add("Banca dati"); names.Add("Ricerca"); names.Add("Indagini"); }
            if (station == 3 && Stations.Cfg.Weapons.Enabled) { names.Add("Tiro"); names.Add("Raffica"); }
            if (station == 3 && Stations.Cfg.Damage.Enabled && Stations.Cfg.Sensors.Enabled) names.Add("Bersaglio");
            if (station == 1 && Stations.Cfg.Pilot.Enabled) { names.Add("Manovre"); names.Add("Rotta"); }
            if (station == 1 && Stations.Cfg.Engage.Enabled) names.Add("Ingaggio");
            if (station == 1) names.Add("Mercato");
            // fino a otto schede su una riga; oltre, due righe (e il contenuto scende di una riga)
            int nt = Math.Min(names.Count, 12); bool two = nt > 8; int perRow = two ? (nt + 1) / 2 : nt;
            float gapT = perRow > 7 ? 4f : 6f; float tw = (Kit.W - (perRow - 1) * gapT) / Math.Max(1, perRow);
            string Short(string n) => perRow > 7 ? n switch { "Regolazioni" => "Regolaz.", "Banca dati" => "B. dati", "Protocolli" => "Protoc.", "Manutenzione" => "Manutenz.", _ => n } : n;
            float subY = two ? -90f : -64f;
            for (int i = 0; i < nt; i++)
            {
                int row = i / perRow, col = i % perRow;
                pg.TabButtons.Add(k.Button("tab|" + i, Kit.L + col * (tw + gapT), -32f - row * 26f, tw, 22f, Short(names[i]).ToUpperInvariant(), cPanel, cWhite, perRow > 7 ? 8 : perRow > 6 ? 9 : perRow > 3 ? 11 : 12));
                var sub = sc.CreateBlankPanel("MelchiorScheda" + i, V(0f, subY), new Vector2(504f, 388f), pg.Page.transform, TL);
                var sk = new Kit { sc = sc, pg = pg, t = sub.transform, accent = accent, second = second };
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
                    case "Officina": BuildWorkshop(sk); break;
                    case "Cantiere": BuildShipyard(sk); break;
                    case "Basi": BuildBases(sk); break;
                    case "Banca dati": BuildCodex(sk); break;
                    case "Ricerca": BuildResearch(sk); break;
                    case "Indagini": BuildMysteries(sk); break;
                    case "Passeggeri": BuildPassengers(sk); break;
                    case "Equipaggio": BuildCrewCommand(sk); break;
                    case "Manutenzione": BuildWear(sk); break;
                    case "Fabbrica": BuildFabrication(sk); break;
                    case "Sonde": BuildProbes(sk); break;
                    case "Bacheca": BuildContracts(sk); break;
                    case "Sensori": BuildSensors(sk); break;
                    case "Bersaglio": BuildTarget(sk); break;
                    case "Ingaggio": BuildEngage(sk); break;
                    case "Mercato": BuildMarket(sk); break;
                    case "Protocolli": BuildProtocols(sk); break;
                    case "Giornale": BuildJournal(sk); break;
                    case "Galassia": BuildGalaxy(sk); break;
                    default: BuildControls(sk, Stations.Cfg.Controls.Where(c => c.Station == station && c.Page == names[i]).ToList()); break;
                }
                sub.gameObject.SetActive(i == 0);
                pg.Tabs.Add((names[i], sub.gameObject));
            }
            // piè di pagina: filetto e riga di stato
            var ft = k.Box(Kit.L, -458f, Kit.W, 1f, Dim(accent, 0.55f, 0.8f)); Look.Simple(ft, Look.Fade);
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
            k.Rule(-270f);
            k.Section(-280f, "Chiamare il bersaglio", "");
            var tgtName = k.Text(Kit.L + 200f, -280f, Kit.W - 200f, "", 11, cWhite, NGUIText.Alignment.Right);
            k.Button("cap|hail|notizie", Kit.L, -306f, 230f, 24f, "CHIEDERE NOTIZIE", k.accent, cInk, 11);
            k.Button("cap|hail|resa", Kit.L + 242f, -306f, 230f, 24f, "INTIMARE LA RESA", cBad, cInk, 11);
            var hailOut = k.Text(Kit.L, -338f, Kit.W, "", 10, cGrey); var hailOut2 = k.Text(Kit.L, -352f, Kit.W, "", 10, cGrey);
            k.Button("cap|board|abborda", Kit.L, -368f, 150f, 20f, "ABBORDAGGIO", cBad, cInk, 10); k.Button("cap|board|rientro", Kit.L + 156f, -368f, 104f, 20f, "RIENTRO", cPanel, cWhite, 10);
            var bstat = k.Text(Kit.L + 268f, -370f, Kit.W - 268f, "", 9, cGrey);

            k.pg.Updaters.Add((id, now) =>
            {
                PLShipInfoBase tg = null; try { tg = Stations.MyShip?.TargetShip; if (tg != null && tg.HasBeenDestroyed) tg = null; } catch { }
                tgtName.text = tg == null ? Grey + "nessun bersaglio" + End : (string)tg.ShipName + (Talents.Line(tg) is { Length: > 0 } cl ? Grey + " · " + cl + End : "");
                bstat.text = GalaxyView.Current?.boardStatus ?? "";
                var (h1, h2) = TwoLines(Hail.Last.Length > 0 ? Hail.Last : "Agganciate una nave e chiamatela: notizie dalle navi amiche, la resa da quelle nemiche malridotte.", 78); hailOut.text = h1; hailOut2.text = h2;
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
            var main = new UILabel[rows.Length]; var detail = new UILabel[rows.Length]; var who = new UILabel[rows.Length]; int[] clsOf = { -1, 0, 4, 2, 3, 1 };
            for (int i = 0; i < rows.Length; i++)
            {
                float y = -32f - i * 58f;
                k.Box(Kit.L, y, 4f, 44f, Accent[acc[i]]);
                k.Text(Kit.L + 12f, y, 110f, rows[i], 12, Accent[acc[i]]);
                if (i > 0) who[i] = k.Text(Kit.L + 12f, y - 22f, 116f, "", 9, cGrey);
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
                var crew = GalaxyView.Current?.crew; for (int r = 1; r < rows.Length; r++) { string s = crew != null && clsOf[r] < crew.Count ? crew[clsOf[r]] : ""; who[r].text = s.Contains("infermeria") ? Bad + s + End : s; }
                main[0].text = alert >= 2 ? Bad + "ROSSO" + End : alert == 1 ? Amber + "GIALLO" + End : Good + "VERDE" + End;
                int ph = Math.Clamp((int)G("drx_phase"), 0, 3); float tens = G("drx_t");
                string env = Environments.Here;
                detail[0].text = (Director.Cfg.Enabled ? $"Situazione {Director.PhaseNames[ph].ToLowerInvariant()}   tensione {(tens >= 55 ? Bad : tens >= 22 ? Amber : Good)}{tens:0}{End}   potenza {G("drx_pow"):0.0}" : "") + (env.Length > 0 ? "   " + Blue + env + End : "") + (Boarding.Short() is { Length: > 0 } bo ? "   " + bo : "") + (Traffic.Short() is { Length: > 0 } tr ? "   " + (tr.Contains("SOS") ? Amber : Grey) + tr + End : "") + "   " + Grey + Rules.Name(GalaxyView.Current?.rules ?? "frontiera") + End;
                main[1].text = "Iniziativa " + ini.ToString("0"); string voc = Calling.Label(); detail[1].text = "Ordine: " + (dir > 0 ? Orders[dir - 1] : "nessuno") + (G("cpx_tired") > 0 ? "   equipaggio affaticato" : "") + (voc.Length > 0 ? "   " + Amber + voc + End : "");
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
            k.Section(-4f, "Notiziario del subspazio", "");
            var ncat = new UILabel[4]; var ntext = new UILabel[4]; var ntext2 = new UILabel[4];
            for (int i = 0; i < ncat.Length; i++)
            {
                float y = -30f - i * 34f;
                ncat[i] = k.Text(Kit.L, y, 84f, "", 10, cGrey);
                ntext[i] = k.Text(Kit.L + 90f, y, Kit.W - 90f, "", 10, i == 0 ? cWhite : cGrey);
                ntext2[i] = k.Text(Kit.L + 90f, y - 14f, Kit.W - 90f, "", 10, i == 0 ? cWhite : cGrey);
            }
            k.Rule(-170f);
            k.Section(-180f, "Giornale di bordo", "");
            var sd = k.Text(Kit.L + 165f, -181f, Kit.W - 165f, "", 10, cBlue, NGUIText.Alignment.Right);
            var times = new UILabel[5]; var lines = new UILabel[5];
            for (int i = 0; i < lines.Length; i++)
            {
                times[i] = k.Text(Kit.L, -206f - i * 22f, 64f, "", 11, k.accent);
                lines[i] = k.Text(Kit.L + 72f, -206f - i * 22f, Kit.W - 72f, "", 11, i == 0 ? cWhite : cGrey);
            }
            k.Rule(-318f);
            var gl = new UILabel[3]; for (int i = 0; i < gl.Length; i++) gl[i] = k.Text(Kit.L, -324f - i * 14f, Kit.W, "", 10, i == 0 ? cWhite : cGrey);
            var weekly = k.Text(Kit.L, -366f, Kit.W, "", 10, cBlue);
            k.pg.Updaters.Add((id, now) =>
            {
                string sn = Wonders.Line().Length > 0 ? Wonders.Line() : Calling.SeasonName(); sd.text = (sn.Length > 0 ? Amber + sn + End + "   " : "") + "data stellare " + (GalaxyView.Current?.stardate ?? "");
                string wk = Challenge.Line(); weekly.text = wk.Length > 0 ? Amber + "SFIDA DELLA SETTIMANA " + End + wk : "";
                var goals = Logbook.GoalRows();
                for (int i = 0; i < gl.Length; i++) gl[i].text = i < goals.Count ? Amber + "AMBIZIONE " + End + goals[i] : i == 0 ? Grey + "Nessuna ambizione in corso." + End : "";
                var nw = News.Items;
                for (int i = 0; i < ncat.Length; i++)
                {
                    var n = i < nw.Count ? nw[nw.Count - 1 - i] : null;
                    ncat[i].text = n == null ? "" : (News.Cats.TryGetValue(n.cat, out var c) ? Hex(c.color) + c.label + End : n.cat);
                    if (n == null) { ntext[i].text = i == 0 ? Grey + "Nessuna notizia, per ora." + End : ""; ntext2[i].text = ""; }
                    else { var (l1, l2) = TwoLines(n.text, 54); ntext[i].text = l1; ntext2[i].text = l2; }
                }
                var j = Stations.Journal;
                for (int i = 0; i < lines.Length; i++)
                {
                    string e = i < j.Count ? j[j.Count - 1 - i] : ""; int cut = e.IndexOf("   ");
                    times[i].text = cut > 0 ? e[..cut] : ""; lines[i].text = cut > 0 ? TwoLines(e[(cut + 3)..], 50).Item1 + (e.Length - cut - 3 > 50 ? "…" : "") : i == 0 ? "Nessun evento registrato." : "";
                }
            });
        }

        static string Hex(string color) => "[" + color.TrimStart('#') + "]";

        // Sonde di ricognizione: lancio e rapporto sui settori vicini, con la rotta a portata di clic.
        static void BuildProbes(Kit k)
        {
            k.Section(-4f, "Sonde di ricognizione", $"Una sonda costa {Probes.Cost} crediti ed esamina i settori vicini.");
            k.Button("pr|lancia", Kit.L, -44f, 220f, 26f, "LANCIA UNA SONDA", k.accent, cInk, 12);
            var from = k.Text(Kit.L + 230f, -48f, Kit.W - 230f, "", 10, cGrey, NGUIText.Alignment.Right);
            const int N = 8; var sec = new UILabel[N]; var txt = new UILabel[N]; var txt2 = new UILabel[N]; var btn = new (UITexture box, UILabel label)[N];
            for (int i = 0; i < N; i++)
            {
                float y = -84f - i * 36f;
                sec[i] = k.Text(Kit.L, y, 96f, "", 11, cWhite);
                txt[i] = k.Text(Kit.L + 100f, y, 286f, "", 9, cGrey); txt2[i] = k.Text(Kit.L + 100f, y - 13f, 286f, "", 9, cGrey);
                btn[i] = k.Button("pr|rotta|" + i, Kit.L + 392f, y + 2f, 80f, 22f, "ROTTA", cPanel, cWhite, 10);
            }
            k.pg.Updaters.Add((id, now) =>
            {
                var rows = Probes.Rows; int f = Probes.From;
                from.text = rows.Count == 0 ? "nessun rapporto" : $"rapporto dal settore {f}";
                for (int i = 0; i < N; i++)
                {
                    var r = i < rows.Count ? rows[i] : null; bool on = r != null;
                    if (btn[i].box.gameObject.activeSelf != on) { btn[i].box.gameObject.SetActive(on); btn[i].label.gameObject.SetActive(on); }
                    if (!on) { sec[i].text = txt[i].text = txt2[i].text = ""; continue; }
                    sec[i].text = (r.interesting ? Amber : "") + $"settore {r.sector}" + (r.interesting ? End : "");
                    var (a1, a2) = TwoLines(r.text, 58); txt[i].text = a1; txt2[i].text = a2;
                    btn[i].box.color = r.interesting ? k.accent : cPanel; btn[i].label.color = r.interesting ? cInk : cWhite;
                }
            });
        }

        // Banca dati: le voci dell'enciclopedia di bordo, per categoria.
        static void BuildCodex(Kit k)
        {
            var cats = Codex.Cats; var cb = new (UITexture box, UILabel label)[cats.Length];
            for (int i = 0; i < cats.Length; i++) { int row = i / 4, col = i % 4; cb[i] = k.Button("cdx|cat|" + i, Kit.L + col * 119f, -4f - row * 26f, 113f, 22f, cats[i].ToUpperInvariant(), cPanel, cWhite, 10); }
            var title = k.Text(Kit.L, -62f, 330f, "", 13, k.accent); var idx = k.Text(Kit.L + 330f, -64f, 142f, "", 10, cGrey, NGUIText.Alignment.Right);
            const int NL = 12; var line = new UILabel[NL]; for (int i = 0; i < NL; i++) line[i] = k.Text(Kit.L, -86f - i * 15f, Kit.W, "", 10, cWhite);
            k.Button("cdx|prev", Kit.L, -278f, 100f, 22f, "< PRECEDENTE", cPanel, cWhite, 10); k.Button("cdx|next", Kit.L + 372f, -278f, 100f, 22f, "SEGUENTE >", cPanel, cWhite, 10);
            k.Rule(-308f); var foot = k.Text(Kit.L, -316f, Kit.W, "", 9, cGrey); var foot2 = k.Text(Kit.L, -330f, Kit.W, "", 9, cGrey);
            k.pg.Updaters.Add((id, now) =>
            {
                var pg = k.pg; var all = Codex.Entries; var list = all.Where(e => e.cat == pg.CodexCat).ToList();
                for (int i = 0; i < cats.Length; i++) { bool on = cats[i] == pg.CodexCat; cb[i].box.color = on ? k.accent : cPanel; cb[i].label.color = on ? cInk : cWhite; }
                if (list.Count == 0) { title.text = Grey + "Nessuna voce, per ora." + End; idx.text = ""; for (int i = 0; i < NL; i++) line[i].text = ""; }
                else
                {
                    pg.CodexIdx = ((pg.CodexIdx % list.Count) + list.Count) % list.Count; var e = list[pg.CodexIdx];
                    title.text = e.title; idx.text = $"voce {pg.CodexIdx + 1} di {list.Count}";
                    var w = Wrap(e.text, 66, NL); for (int i = 0; i < NL; i++) line[i].text = w[i];
                }
                var counts = cats.Select(c => $"{c.ToLowerInvariant()} {all.Count(x => x.cat == c)}").ToList();
                foot.text = "Voci: " + string.Join(" · ", counts.Take(4)); foot2.text = string.Join(" · ", counts.Skip(4));
            });
        }

        // Ricerca: dati raccolti, progetto in corso, tecnologie disponibili e completate.
        static void BuildResearch(Kit k)
        {
            k.Section(-4f, "Ricerca scientifica", "Dati da esplorazioni, sonde, specie aliene, eventi rari, laboratori e analisi di routine.");
            var head = k.Text(Kit.L, -42f, Kit.W, "", 11, cWhite);
            var cur = k.Text(Kit.L, -60f, 300f, "", 10, cGrey); var bar = k.Meter(Kit.L + 310f, -64f, Kit.W - 310f, 6f);
            k.Rule(-78f);
            int N = Research.Projects.Length; var nm = new UILabel[N]; var ef = new UILabel[N]; var co = new UILabel[N]; var bt = new (UITexture box, UILabel label)[N];
            for (int i = 0; i < N; i++)
            {
                float y = -86f - i * 31f; var p = Research.Projects[i];
                nm[i] = k.Text(Kit.L, y, 280f, "", 10, cWhite); ef[i] = k.Text(Kit.L, y - 13f, 370f, p.effect, 8, cGrey);
                co[i] = k.Text(Kit.L + 284f, y, 96f, "", 9, cGrey, NGUIText.Alignment.Right);
                bt[i] = k.Button("rc|avvia|" + i, Kit.L + 392f, y + 2f, 80f, 20f, "AVVIA", k.accent, cInk, 9);
            }
            k.pg.Updaters.Add((id, now) =>
            {
                var r = GalaxyView.Current?.research ?? new Research.Panel();
                head.text = $"Dati di ricerca: {Good}{r.data}{End}   raccolti in tutto {r.total}   tecnologie {r.done.Count}/{N}";
                var cp = Research.P(r.current);
                cur.text = cp == null ? Grey + "Laboratorio libero: scegli un progetto." + End : $"In corso: {cp.name} ({r.progress}/{cp.jumps} salti)";
                bar(cp == null ? 0f : (float)r.progress / Math.Max(1, cp.jumps), k.accent);
                for (int i = 0; i < N; i++)
                {
                    var p = Research.Projects[i]; bool done = r.done.Contains(p.id), running = r.current == p.id, can = !done && r.current.Length == 0 && r.data >= p.cost;
                    nm[i].text = done ? Good + "✓ " + p.name + End : running ? Amber + "» " + p.name + End : p.name;
                    co[i].text = done ? Grey + "completata" + End : $"{p.cost} dati · {p.jumps} salti";
                    bt[i].label.text = done ? "FATTO" : running ? "IN CORSO" : "AVVIA";
                    bt[i].box.color = can ? k.accent : cPanel; bt[i].label.color = can ? cInk : cGrey;
                }
            });
        }

        // Indagini: segnali da triangolare e decifrare, scavo in corso, reperti (a pagine di tre), civiltà perdute.
        static void BuildMysteries(Kit k)
        {
            k.Section(-4f, "Indagini e archeologia", "Ogni salto con la Scienza in servizio dà un rilevamento: tre, o una sonda, localizzano il segnale.");
            k.Text(Kit.L, -40f, 120f, "SEGNALI", 8, cGrey); var stats = k.Text(Kit.L + 120f, -40f, Kit.W - 120f, "", 8, cGrey, NGUIText.Alignment.Right);
            const int NS = 3; var sn = new UILabel[NS]; var s2 = new UILabel[NS]; var s3 = new UILabel[NS]; var bd = new (UITexture box, UILabel label)[NS]; var bp = new (UITexture box, UILabel label)[NS]; var br = new (UITexture box, UILabel label)[NS];
            for (int i = 0; i < NS; i++)
            {
                float y = -54f - i * 40f;
                sn[i] = k.Text(Kit.L, y, 290f, "", 10, cWhite); s2[i] = k.Text(Kit.L, y - 12f, 290f, "", 8, cGrey); s3[i] = k.Text(Kit.L, y - 23f, 290f, "", 8, cGrey);
                bd[i] = k.Button("ind|decifra|" + i, Kit.L + 296f, y + 2f, 62f, 18f, "DECIFRA", k.accent, cInk, 8);
                bp[i] = k.Button("ind|sonda|" + i, Kit.L + 362f, y + 2f, 52f, 18f, "SONDA", cPanel, cWhite, 8);
                br[i] = k.Button("ind|rotta|" + i, Kit.L + 418f, y + 2f, 54f, 18f, "ROTTA", cPanel, cWhite, 8);
            }
            k.Rule(-176f);
            var dg = k.Text(Kit.L, -182f, 300f, "", 9, cWhite); var dbar = k.Meter(Kit.L + 306f, -186f, 80f, 6f); var dl = k.Text(Kit.L, -195f, 380f, "", 8, cGrey);
            var bs = k.Button("ind|scavo|0", Kit.L + 392f, -184f, 80f, 18f, "→ SCAVO", cPanel, cWhite, 8);
            k.Rule(-210f);
            k.Text(Kit.L, -216f, 120f, "REPERTI", 8, cGrey); var pgl = k.Text(Kit.L + 120f, -216f, 280f, "", 8, cGrey, NGUIText.Alignment.Right);
            k.Button("ind|prev|0", Kit.L + 410f, -212f, 28f, 16f, "«", cPanel, cWhite, 9); k.Button("ind|next|0", Kit.L + 444f, -212f, 28f, 16f, "»", cPanel, cWhite, 9);
            const int NR = 3; var rn = new UILabel[NR]; var r2 = new UILabel[NR]; var r3 = new UILabel[NR]; var rb = new (UITexture box, UILabel label)[NR, 4];
            string[] acts = { "studia", "dona", "vendi", "getta" }; string[] labels = { "STUDIA", "DONA", "VENDI", "GETTA" }; float[] bx = { 276f, 332f, 380f, 428f }, bw = { 52f, 44f, 44f, 44f };
            for (int j = 0; j < NR; j++)
            {
                float y = -232f - j * 46f;
                rn[j] = k.Text(Kit.L, y, 270f, "", 10, cWhite); r2[j] = k.Text(Kit.L, y - 12f, 270f, "", 8, cGrey); r3[j] = k.Text(Kit.L, y - 23f, Kit.W, "", 8, cGrey);
                for (int b = 0; b < 4; b++) rb[j, b] = k.Button($"ind|{acts[b]}|{j}", Kit.L + bx[b], y + 2f, bw[b], 18f, labels[b], cPanel, cWhite, 8);
            }
            var civ = k.Text(Kit.L, -372f, Kit.W, "", 8, cGrey);
            string[] rc = { "[c8ccd8]", "[7CD992]", "[9db4ff]", "[c39bff]", "[F2A65A]" };
            void Show((UITexture box, UILabel label) b, bool on) { if (b.box.gameObject.activeSelf != on) { b.box.gameObject.SetActive(on); b.label.gameObject.SetActive(on); } }
            k.pg.Updaters.Add((id, now) =>
            {
                var p = GalaxyView.Current?.mystery ?? new Mysteries.Panel();
                stats.text = p.stats;
                for (int i = 0; i < NS; i++)
                {
                    var s = i < p.signals.Count ? p.signals[i] : null; Show(bd[i], s != null); Show(bp[i], s != null); Show(br[i], s != null);
                    if (s == null) { sn[i].text = i == 0 && p.signals.Count == 0 ? Grey + "Nessun segnale. La Scienza ascolta a ogni salto." + End : ""; s2[i].text = s3[i].text = ""; continue; }
                    sn[i].text = (s.fix ? Good : s.decode >= 30 ? Amber : "") + Mysteries.Cap(s.name) + (s.fix || s.decode >= 30 ? End : "");
                    s2[i].text = $"decifrato {s.decode}% · rilevamenti {s.bearings} · " + (s.fix ? $"origine: settore {s.source}" : s.bearings == 0 ? "direzione ignota" : $"zona: {s.zone} settori") + $" · si spegne fra {s.fade} salti";
                    s3[i].text = s.hint.Length > 0 ? (s.hint.Length > 64 ? s.hint[..62] + "…" : s.hint) : "";
                    bool canDec = s.decode < 100 && p.data >= 4; bd[i].box.color = canDec ? k.accent : cPanel; bd[i].label.color = canDec ? cInk : cGrey; bd[i].label.text = s.decode >= 100 ? "LETTO" : "DECIFRA";
                    bp[i].label.text = s.fix ? "TROVATO" : "SONDA"; bp[i].label.color = s.fix ? cGrey : cWhite;
                    br[i].box.color = s.fix ? k.accent : cPanel; br[i].label.color = s.fix ? cInk : cWhite;
                }
                dg.text = p.dig.Length > 0 ? Amber + p.dig + End : p.digs.Count > 0 ? "Scavi noti" : Grey + "Nessuno scavo aperto." + End;
                dbar(p.dig.Length > 0 ? p.digProgress : 0f, k.accent); dl.text = string.Join(" · ", p.digs); Show(bs, p.digs.Count > 0);
                int pages = Math.Max(1, (p.relics.Count + NR - 1) / NR); k.pg.MystPage = Math.Clamp(k.pg.MystPage, 0, pages - 1);
                pgl.text = p.relics.Count == 0 ? "stiva dei reperti vuota" : $"pagina {k.pg.MystPage + 1} di {pages} · {p.relics.Count} reperti" + (p.port ? (p.shady ? " · ricettatore in porto" : " · museo in porto") : "");
                for (int j = 0; j < NR; j++)
                {
                    int idx = k.pg.MystPage * NR + j; var r = idx < p.relics.Count ? p.relics[idx] : null;
                    for (int b = 0; b < 4; b++) Show(rb[j, b], r != null);
                    if (r == null) { rn[j].text = r2[j].text = r3[j].text = ""; continue; }
                    rn[j].text = rc[Math.Clamp(r.rarity, 0, 4)] + r.name + End;
                    r2[j].text = $"{new[] { "comune", "pregiato", "raro", "prezioso", "leggendario" }[Math.Clamp(r.rarity, 0, 4)]} · conservato {r.condition}% · {r.value} cr" + (r.curse.Length > 0 ? " · " + Bad + "inquieto" + End : "") + (r.studied ? " · studiato" : "");
                    r3[j].text = r.story;
                    rb[j, 0].label.color = r.studied ? cGrey : cWhite; rb[j, 1].label.color = rb[j, 2].label.color = p.port ? cWhite : cGrey;
                }
                civ.text = p.civs.Count == 0 ? "Civiltà perdute: nessuna traccia ancora." : "Civiltà perdute: " + string.Join(" · ", p.civs) + " capitoli";
            });
        }

        // Manutenzione: stato dei sette sistemi, efficienza, ricambi, manutenzione e revisione in porto.
        static void BuildWear(Kit k)
        {
            k.Section(-4f, "Usura e manutenzione", "I sistemi si consumano con l'uso: sotto l'80% rendono meno, sotto il 25% si guastano da soli.");
            var head = k.Text(Kit.L, -42f, Kit.W, "", 10, cWhite);
            k.Rule(-56f);
            string[] sys = Wear.Systems; int N = sys.Length; var nm = new UILabel[N]; var l2 = new UILabel[N]; var bar = new Action<float, Color>[N]; var bt = new (UITexture box, UILabel label)[N];
            for (int i = 0; i < N; i++)
            {
                float y = -64f - i * 38f;
                nm[i] = k.Text(Kit.L, y, 220f, Wear.Name(sys[i]), 10, cWhite); l2[i] = k.Text(Kit.L + 224f, y, 150f, "", 9, cGrey, NGUIText.Alignment.Right);
                bar[i] = k.Meter(Kit.L, y - 16f, 374f, 7f);
                bt[i] = k.Button("wr|ripara|" + sys[i], Kit.L + 384f, y + 2f, 88f, 20f, "", cPanel, cWhite, 8);
            }
            k.Rule(-334f);
            var foot = k.Text(Kit.L, -342f, 300f, "", 9, cGrey);
            var rev = k.Button("wr|revisione|0", Kit.L + 306f, -340f, 166f, 22f, "", cPanel, cWhite, 9);
            k.pg.Updaters.Add((id, now) =>
            {
                var p = GalaxyView.Current?.wear ?? new Wear.Panel();
                head.text = $"Ricambi in stiva: {(p.parts > 0 ? Good : Bad)}{p.parts}{End}   manutenzioni {p.maint} · guasti da usura {p.failures}";
                for (int i = 0; i < N; i++)
                {
                    float c = p.cond.TryGetValue(sys[i], out var v) ? v : 100f; float e = Wear.Eff(c);
                    l2[i].text = (c <= 0f ? Bad + "IN AVARIA" + End : c <= 25f ? Bad + $"{c:0}%" + End : c < 80f ? Amber + $"{c:0}%" + End : Good + $"{c:0}%" + End) + $" · resa {e:P0}";
                    bar[i](c / 100f, c <= 25f ? cBad : c < 80f ? cWarn : cGood);
                    int need = Math.Max(1, (int)Math.Ceiling((100f - c) / 20f)); bool can = c < 99f && p.parts > 0;
                    bt[i].label.text = c >= 99f ? "IN ORDINE" : $"RIPARA ({Math.Min(need, Math.Max(1, p.parts))})"; bt[i].box.color = can ? k.accent : cPanel; bt[i].label.color = can ? cInk : cGrey;
                }
                foot.text = p.port ? "In porto: il cantiere può revisionare tutta la nave." : "La revisione completa si fa in un cantiere di porto. I ricambi si comprano in borsa.";
                rev.label.text = p.cost <= 0 ? "NAVE IN ORDINE" : $"REVISIONE · {p.cost} CR"; bool cr = p.port && p.cost > 0; rev.box.color = cr ? k.accent : cPanel; rev.label.color = cr ? cInk : cGrey;
            });
        }

        // Fabbricazione: ricette con materie prime della stiva, coda di quattro lavorazioni, avanzamento della prima.
        static void BuildFabrication(Kit k)
        {
            k.Section(-4f, "Fabbricazione", "L'officina lavora le materie prime della stiva. Più veloce con un ingegnere alla stazione; ferma in battaglia.");
            var head = k.Text(Kit.L, -42f, Kit.W, "", 9, cGrey);
            k.Rule(-56f);
            var rs = Fabrication.Recipes; int N = rs.Length; var nm = new UILabel[N]; var l2 = new UILabel[N]; var tm = new UILabel[N]; var bt = new (UITexture box, UILabel label)[N];
            for (int i = 0; i < N; i++)
            {
                float y = -62f - i * 32f;
                nm[i] = k.Text(Kit.L, y, 250f, rs[i].name, 10, cWhite); tm[i] = k.Text(Kit.L + 250f, y, 128f, "", 9, cGrey, NGUIText.Alignment.Right);
                l2[i] = k.Text(Kit.L, y - 13f, 378f, "", 8, cGrey);
                bt[i] = k.Button("fb|fabbrica|" + rs[i].id, Kit.L + 384f, y + 1f, 88f, 20f, "", cPanel, cWhite, 8);
            }
            k.Rule(-322f);
            var q = k.Text(Kit.L, -330f, 374f, "", 9, cWhite);
            var bar = k.Meter(Kit.L, -348f, 374f, 6f);
            var cancel = k.Button("fb|annulla|0", Kit.L + 384f, -330f, 88f, 22f, "ANNULLA", cPanel, cWhite, 9);
            k.pg.Updaters.Add((id, now) =>
            {
                var p = GalaxyView.Current?.fab ?? new Fabrication.Panel();
                head.text = $"Stiva: {p.stock}   capsule {p.fuelCaps}" + (p.probes > 0 ? $"   sonde pronte {Good}{p.probes}{End}" : "") + $"   velocità {(p.speed >= 1f ? Good : Amber)}{p.speed:0.0}×{End}";
                for (int i = 0; i < N && i < p.recipes.Count; i++)
                {
                    var r = p.recipes[i];
                    l2[i].text = r.unlocked ? $"{r.inputs}  →  {r.output}" : $"{Amber}Richiede la ricerca «{r.needs}»{End}";
                    int secs = (int)Math.Ceiling(r.seconds / Math.Max(0.1f, p.speed)); tm[i].text = secs >= 60 ? $"{secs / 60}m {secs % 60:00}s" : $"{secs}s";
                    bool can = r.unlocked && r.afford && p.queue.Count < 4;
                    bt[i].label.text = !r.unlocked ? "BLOCCATA" : !r.afford ? "MANCA MATERIA" : p.queue.Count >= 4 ? "CODA PIENA" : "FABBRICA";
                    bt[i].box.color = can ? k.accent : cPanel; bt[i].label.color = can ? cInk : cGrey;
                }
                q.text = p.queue.Count == 0 ? Grey + "Officina ferma: nessuna lavorazione in coda." + End : $"In lavorazione: {Good}{p.queue[0]}{End} {p.progress:P0}" + (p.queue.Count > 1 ? $"   poi: {string.Join(", ", p.queue.Skip(1))}" : "");
                bar(p.queue.Count == 0 ? -1f : p.progress, cGood);
                bool c = p.queue.Count > 0; cancel.box.color = c ? cPanel : cPanel; cancel.label.color = c ? cWhite : cGrey;
            });
        }

        // Comando dell'equipaggio: dottrina di squadra, ordini individuali (postura, specializzazione), regole d'ingaggio, rapporto.
        static void BuildCrewCommand(Kit k)
        {
            k.Section(-4f, "Comando dell'equipaggio", "Dottrina di squadra, ordini a ogni ufficiale e regole d'ingaggio: la mente dell'equipaggio li rispetta.");
            string[] docs = CrewMind.Doctrines; string[] dl = { "EQUILIBRIO", "BATTAGLIA", "DIFESA", "DANNI", "SCORTA", "RIPOSO" };
            float dw = (Kit.W - 5 * 4f) / 6f; var db = new (UITexture box, UILabel label)[docs.Length];
            for (int i = 0; i < docs.Length; i++) db[i] = k.Button("cc|dottrina|" + docs[i], Kit.L + i * (dw + 4f), -40f, dw, 20f, dl[i], cPanel, cWhite, 8);
            var status = k.Text(Kit.L, -66f, Kit.W, "", 9, cGrey);
            k.Rule(-78f);
            const int N = 5; var nm = new UILabel[N]; var act = new UILabel[N]; var l2 = new UILabel[N]; var hp = new Action<float, Color>[N]; var bp = new (UITexture box, UILabel label)[N]; var bf = new (UITexture box, UILabel label)[N];
            string[] stn = { "COMANDO", "TIMONE", "SCIENZA", "ARMI", "INGEGNERIA" };
            for (int i = 0; i < N; i++)
            {
                float y = -86f - i * 50f;
                nm[i] = k.Text(Kit.L, y, 196f, "", 10, cWhite); act[i] = k.Text(Kit.L + 198f, y, 104f, "", 9, cGrey, NGUIText.Alignment.Right);
                l2[i] = k.Text(Kit.L, y - 13f, 302f, "", 8, cGrey); hp[i] = k.Meter(Kit.L, y - 29f, 302f, 5f);
                bp[i] = k.Button("cc|postura|" + i, Kit.L + 308f, y + 2f, 84f, 20f, "", cPanel, cWhite, 8);
                bf[i] = k.Button("cc|specialita|" + i, Kit.L + 396f, y + 2f, 76f, 20f, "", cPanel, cWhite, 8);
            }
            k.Rule(-336f);
            var br = k.Button("cc|ritirata|0", Kit.L, -344f, 150f, 22f, "", cPanel, cWhite, 9);
            var bb = k.Button("cc|abbordaggio|0", Kit.L + 156f, -344f, 196f, 22f, "", cPanel, cWhite, 9);
            k.Button("cc|rapporto|0", Kit.L + 358f, -344f, 114f, 22f, "RAPPORTO", k.accent, cInk, 10);
            k.pg.Updaters.Add((id, now) =>
            {
                var p = GalaxyView.Current?.crewCmd ?? new CrewMind.Panel();
                for (int i = 0; i < docs.Length; i++) { bool on = p.doctrine == docs[i]; db[i].box.color = on ? k.accent : cPanel; db[i].label.color = on ? cInk : cWhite; }
                status.text = $"Dottrina: {Amber}{CrewMind.DoctrineName(p.doctrine)}{End} · situazione a bordo: {p.gravity} · ordini impartiti {p.given}";
                for (int i = 0; i < N && i < p.rows.Count; i++)
                {
                    var r = p.rows[i];
                    nm[i].text = !r.present ? Grey + stn[i] + " · stazione vuota" + End : (r.bot ? $"{Amber}{stn[i]}{End} {r.rank} {r.name}" : $"{Good}{stn[i]}{End} {r.name} (giocatore)");
                    act[i].text = r.activity;
                    l2[i].text = !r.present ? "" : (r.hp >= 0 ? $"salute {(r.hp < 0.35f ? Bad : r.hp < 0.7f ? Amber : Good)}{r.hp:P0}{End}" : "") + (r.loyalty >= 0 ? $" · lealtà {r.loyalty}" : "") + (r.best.Length > 0 ? $" · {r.best}" : "") + (r.trait.Length > 0 ? $" · {r.trait}" : "") + (r.injured ? $" · {Bad}ferito{End}" : "");
                    hp[i](r.present && r.hp >= 0 ? r.hp : -1f, r.hp < 0.35f ? cBad : r.hp < 0.7f ? cWarn : cGood);
                    bool ctl = r.present && r.bot; 
                    bp[i].label.text = ctl ? CrewMind.StanceName(r.stance).ToUpperInvariant() : "—"; bp[i].box.color = ctl && r.stance != "auto" ? k.accent : cPanel; bp[i].label.color = ctl && r.stance != "auto" ? cInk : ctl ? cWhite : cGrey;
                    bf[i].label.text = ctl ? (r.focus.Length > 0 ? r.focus.ToUpperInvariant() : "SPECIALITÀ") : "—"; bf[i].box.color = ctl && r.focus.Length > 0 ? k.second : cPanel; bf[i].label.color = ctl && r.focus.Length > 0 ? cInk : ctl ? cWhite : cGrey;
                }
                br.label.text = $"RITIRATA SOTTO IL {p.retreat}%";
                bb.label.text = "ABBORDAGGI: " + (p.boarding == "armi" ? "ARMI E COMANDO" : p.boarding == "tutti" ? "TUTTI" : "NESSUNO");
            });
        }

        // Passeggeri: cabine, gruppi a bordo con destinazione, scadenza, compenso e umore; rotta e sbarco.
        static void BuildPassengers(Kit k)
        {
            k.Section(-4f, "Passeggeri", "Si imbarcano nei porti o da altre navi. Battaglie, incendi, fame e ritardi li scontentano; all'arrivo pagano secondo l'umore.");
            var head = k.Text(Kit.L, -42f, Kit.W, "", 11, cWhite); var stats = k.Text(Kit.L, -58f, Kit.W, "", 8, cGrey);
            k.Rule(-70f);
            const int N = 6; var nm = new UILabel[N]; var l2 = new UILabel[N]; var bars = new Action<float, Color>[N]; var br = new (UITexture box, UILabel label)[N]; var bs = new (UITexture box, UILabel label)[N];
            for (int i = 0; i < N; i++)
            {
                float y = -78f - i * 48f;
                nm[i] = k.Text(Kit.L, y, 300f, "", 10, cWhite); l2[i] = k.Text(Kit.L, y - 13f, 330f, "", 8, cGrey);
                bars[i] = k.Meter(Kit.L, y - 29f, 300f, 5f);
                br[i] = k.Button("pax|rotta|" + i, Kit.L + 336f, y + 2f, 66f, 20f, "ROTTA", cPanel, cWhite, 9);
                bs[i] = k.Button("pax|sbarca|" + i, Kit.L + 406f, y + 2f, 66f, 20f, "SBARCA", cPanel, cGrey, 9);
            }
            var empty = k.Text(Kit.L, -86f, Kit.W, "", 10, cGrey);
            void Show((UITexture box, UILabel label) b, bool on) { if (b.box.gameObject.activeSelf != on) { b.box.gameObject.SetActive(on); b.label.gameObject.SetActive(on); } }
            k.pg.Updaters.Add((id, now) =>
            {
                var p = GalaxyView.Current?.pax ?? new Passengers.Panel();
                head.text = $"Cabine occupate: {(p.aboard >= p.capacity ? Amber : Good)}{p.aboard}/{p.capacity}{End}" + (p.port ? "   " + Grey + "in porto: si può sbarcare" + End : "");
                stats.text = p.stats;
                empty.text = p.rows.Count == 0 ? "Nessun passeggero a bordo. Le offerte d'imbarco arrivano all'attracco." : "";
                for (int i = 0; i < N; i++)
                {
                    var r = i < p.rows.Count ? p.rows[i] : null; Show(br[i], r != null); Show(bs[i], r != null);
                    if (r == null) { nm[i].text = l2[i].text = ""; bars[i](-1f, cPanel); continue; }
                    int left = r.deadline - p.jumps;
                    nm[i].text = (r.illegal ? Bad : r.here ? Good : "") + Mysteries.Cap(r.label) + (r.illegal || r.here ? End : "");
                    l2[i].text = $"→ settore {r.dest}" + (r.here ? " (siamo arrivati)" : "") + $" · {(left >= 0 ? $"entro {left} salti" : Bad + $"in ritardo di {-left}" + End)} · {r.pay} cr · {Passengers.Mood(r.mood)}";
                    bars[i](r.mood / 100f, r.mood >= 65 ? cGood : r.mood >= 35 ? cWarn : cBad);
                    bs[i].label.color = p.port ? cWhite : cGrey;
                }
            });
        }

        // Mercato: borsa delle merci, una riga per merce con prezzi del porto, stiva e miglior vendita nota.
        static void BuildMarket(Kit k)
        {
            k.Section(-4f, "Borsa delle merci", "In porto: COMPRA imbarca 5 unità, VENDI scarica tutto. → traccia la rotta verso il miglior prezzo noto.");
            k.Text(Kit.L, -40f, 150f, "MERCE", 8, cGrey); k.Text(Kit.L + 152f, -40f, 40f, "STIVA", 8, cGrey, NGUIText.Alignment.Right);
            k.Text(Kit.L + 196f, -40f, 52f, "COMPRA", 8, cGrey, NGUIText.Alignment.Right); k.Text(Kit.L + 252f, -40f, 52f, "VENDE", 8, cGrey, NGUIText.Alignment.Right);
            int N = Market.Goods.Length; var nm = new UILabel[N]; var hv = new UILabel[N]; var by = new UILabel[N]; var sl = new UILabel[N]; var bt = new UILabel[N];
            var bb = new (UITexture box, UILabel label)[N]; var sb = new (UITexture box, UILabel label)[N];
            for (int i = 0; i < N; i++)
            {
                float y = -54f - i * 31f;
                nm[i] = k.Text(Kit.L, y, 150f, "", 10, cWhite); bt[i] = k.Text(Kit.L, y - 13f, 300f, "", 8, cGrey);
                hv[i] = k.Text(Kit.L + 152f, y, 40f, "", 10, cWhite, NGUIText.Alignment.Right);
                by[i] = k.Text(Kit.L + 196f, y, 52f, "", 10, cWhite, NGUIText.Alignment.Right); sl[i] = k.Text(Kit.L + 252f, y, 52f, "", 10, cWhite, NGUIText.Alignment.Right);
                bb[i] = k.Button("mk|compra|" + i, Kit.L + 314f, y + 2f, 66f, 20f, "COMPRA", k.accent, cInk, 9);
                sb[i] = k.Button("mk|vendi|" + i, Kit.L + 384f, y + 2f, 62f, 20f, "VENDI", cPanel, cWhite, 9);
                k.Button("mk|rotta|" + i, Kit.L + 450f, y + 2f, 22f, 20f, "→", cPanel, cWhite, 10);
            }
            k.Rule(-332f);
            var foot = k.Text(Kit.L, -338f, Kit.W, "", 10, cWhite); var foot2 = k.Text(Kit.L, -352f, Kit.W, "", 9, cGrey);
            var sup = k.Text(Kit.L, -368f, 258f, "", 9, cWhite); var rp = k.Button("mk|rimborsa|0", Kit.L + 264f, -364f, 100f, 20f, "RIMBORSA", cPanel, cGrey, 9); var rs = k.Button("mk|scorte|0", Kit.L + 370f, -364f, 102f, 20f, "RIFORNISCI", cPanel, cWhite, 9);
            k.pg.Updaters.Add((id, now) =>
            {
                var p = GalaxyView.Current?.market ?? new Market.Panel();
                for (int i = 0; i < N && i < p.rows.Count; i++)
                {
                    var r = p.rows[i];
                    nm[i].text = (r.contraband ? Bad : "") + r.name + (r.contraband ? End : "");
                    bt[i].text = (r.best.Length > 0 ? $"miglior vendita nota {r.best}" : "nessun prezzo noto altrove") + (r.have > 0 ? $" · pagato {r.paid}" : "");
                    hv[i].text = r.have > 0 ? r.have.ToString() : Grey + "-" + End;
                    by[i].text = p.port ? r.buy.ToString() : Grey + "-" + End;
                    sl[i].text = !p.port ? Grey + "-" + End : r.have > 0 ? (r.sell >= r.paid ? Good : Bad) + r.sell + End : r.sell.ToString();
                    bool canBuy = p.port && p.load < p.cap, canSell = p.port && r.have > 0;
                    bb[i].box.color = canBuy ? k.accent : cPanel; bb[i].label.color = canBuy ? cInk : cGrey;
                    sb[i].box.color = canSell ? k.second : cPanel; sb[i].label.color = canSell ? cInk : cGrey;
                }
                foot.text = $"Stiva {p.load}/{p.cap}   valore del carico {p.value:N0} cr   guadagno totale {(p.profit >= 0 ? Good : Bad)}{p.profit:N0}{End} cr".Replace(",", ".");
                foot2.text = (p.portLine.Length > 0 ? p.portLine + ". " : p.port ? $"In porto al settore {p.where}. " : "Fuori dai porti si guarda soltanto. ") + (p.orders.Length > 0 ? Amber + p.orders + End : p.npc.Length > 0 ? "Ultime: " + p.npc + "." : "");
                sup.text = p.bank.Length > 0 ? Bad + p.bank + End : p.supplies; bool debt = p.bank.Length > 0 && p.port; rp.box.color = debt ? cBad : cPanel; rp.label.color = debt ? cInk : cGrey; rs.box.color = p.port ? k.second : cPanel; rs.label.color = p.port ? cInk : cGrey;
            });
        }

        // Basi: atti di fondazione posseduti, fondazione nel settore corrente, elenco delle basi, lavori sul posto.
        static void BuildBases(Kit k)
        {
            k.Section(-4f, "Atti di fondazione", "Con un atto, in un settore libero, si fonda una base. Le basi crescono, rendono e vanno difese.");
            var deeds = k.Text(Kit.L, -42f, Kit.W, "", 10, cWhite);
            k.Button("bs|prev", Kit.L, -60f, 30f, 22f, "<", cPanel, cWhite, 12); var kindLbl = k.Text(Kit.L + 36f, -62f, 220f, "", 11, k.accent); k.Button("bs|next", Kit.L + 260f, -60f, 30f, 22f, ">", cPanel, cWhite, 12);
            var found = k.Button("bs|fonda", Kit.L + 300f, -60f, 172f, 22f, "FONDA QUI", k.accent, cInk, 11);
            var hint = k.Text(Kit.L, -88f, Kit.W, "", 9, cGrey);
            k.Rule(-106f); k.Section(-114f, "Le nostre basi", "");
            const int N = 6; var nm = new UILabel[N]; var ln = new UILabel[N]; var rb = new (UITexture box, UILabel label)[N];
            for (int i = 0; i < N; i++) { float y = -138f - i * 30f; nm[i] = k.Text(Kit.L, y, 300f, "", 10, cWhite); ln[i] = k.Text(Kit.L, y - 13f, 380f, "", 9, cGrey); rb[i] = k.Button("bs|rotta|" + i, Kit.L + 392f, y, 80f, 22f, "ROTTA", cPanel, cWhite, 10); }
            k.Rule(-322f); var here = k.Text(Kit.L, -330f, Kit.W, "", 10, cWhite);
            var up = k.Button("bs|migliora", Kit.L, -350f, 230f, 22f, "", k.accent, cInk, 10); var df = k.Button("bs|difese", Kit.L + 242f, -350f, 230f, 22f, "", k.accent, cInk, 10);
            k.pg.Updaters.Add((id, now) =>
            {
                var pg = k.pg; var p = GalaxyView.Current?.bases ?? new Bases.Panel(); var kind = Bases.Kinds[pg.BaseKind];
                deeds.text = p.deeds.Count == 0 ? Grey + "Nessun atto: si comprano all'ufficio del catasto dei porti o si ottengono come ricompensa." + End : "Atti: " + string.Join("  ·  ", p.deeds.Select(x => $"{Bases.K(x.Key)?.name.ToLowerInvariant()} ×{x.Value}"));
                int have = p.deeds.TryGetValue(kind.id, out var c) ? c : 0;
                kindLbl.text = kind.name + (have > 0 ? $" ×{have}" : Grey + " (nessun atto)" + End);
                string why = p.canHere.TryGetValue(kind.id, out var w) ? w : ""; bool ok = why.StartsWith("si può");
                found.box.color = ok ? k.accent : cPanel; found.label.color = ok ? cInk : cGrey;
                hint.text = (ok ? Good : Grey) + kind.desc + ". " + char.ToUpper(why.Length > 0 ? why[0] : ' ') + (why.Length > 1 ? why[1..] : "") + "." + End;
                for (int i = 0; i < N; i++)
                {
                    var b = i < p.bases.Count ? p.bases[i] : null; bool on = b != null;
                    if (rb[i].box.gameObject.activeSelf != on) { rb[i].box.gameObject.SetActive(on); rb[i].label.gameObject.SetActive(on); }
                    if (!on) { nm[i].text = i == 0 && p.bases.Count == 0 ? Grey + "Nessuna base, per ora." + End : ""; ln[i].text = ""; continue; }
                    nm[i].text = (b.raid ? Bad + "SOTTO ATTACCO " + End : "") + (b.here ? Amber : "") + b.name + (b.here ? " · qui" + End : "") + Grey + $"  settore {b.sector} ({b.jumps} salti)" + End;
                    ln[i].text = b.line;
                }
                here.text = p.here.Length > 0 ? p.here : Grey + "Lavori, migliorie e difese si fanno sul posto, alla base." + End;
                bool atBase = p.here.Length > 0;
                up.label.text = !atBase ? "MIGLIORA" : p.upCost < 0 ? "LIVELLO MASSIMO" : $"MIGLIORA · {p.upCost:N0} CR".Replace(",", ".");
                df.label.text = !atBase ? "DIFESE" : p.defCost < 0 ? "DIFESE AL MASSIMO" : $"DIFESE · {p.defCost:N0} CR".Replace(",", ".");
                bool canUp = atBase && p.upCost > 0 && p.credits >= p.upCost, canDf = atBase && p.defCost > 0 && p.credits >= p.defCost;
                up.box.color = canUp ? k.accent : cPanel; up.label.color = canUp ? cInk : cGrey;
                df.box.color = canDf ? k.accent : cPanel; df.label.color = canDf ? cInk : cGrey;
            });
        }

        // Cantiere navale: sette migliorie permanenti in tre livelli, acquistabili solo in porto.
        static void BuildShipyard(Kit k)
        {
            var defs = Shipyard.Defs; int n = defs.Length;
            k.Section(-4f, "Cantiere navale", "Migliorie permanenti della nave. Il cantiere lavora solo in porto.");
            var name = new UILabel[n]; var eff = new UILabel[n]; var lvl = new UILabel[n]; var btn = new (UITexture box, UILabel label)[n];
            for (int i = 0; i < n; i++)
            {
                float y = -44f - i * 42f;
                name[i] = k.Text(Kit.L, y, 220f, defs[i].name, 12, cWhite);
                lvl[i] = k.Text(Kit.L + 222f, y, 90f, "", 11, k.accent);
                eff[i] = k.Text(Kit.L, y - 17f, 320f, defs[i].effect, 9, cGrey);
                btn[i] = k.Button("yd|" + defs[i].id, Kit.L + 322f, y - 2f, 150f, 24f, "", k.accent, cInk, 10);
            }
            var foot = k.Text(Kit.L, -344f, Kit.W, "", 10, cWhite);
            k.pg.Updaters.Add((id, now) =>
            {
                bool port = Shipyard.InPort(); int credits = 0; try { credits = PLServer.Instance.CurrentCrewCredits; } catch { }
                for (int i = 0; i < n; i++)
                {
                    int l = Shipyard.Level(defs[i].id);
                    lvl[i].text = new string('|', l) + Grey + new string('.', 3 - l) + End + $"  liv. {l}";
                    if (l >= 3) { btn[i].label.text = "COMPLETO"; btn[i].box.color = cPanel; btn[i].label.color = cGrey; continue; }
                    int cost = Shipyard.CostOf(l); bool ok = port && credits >= cost;
                    btn[i].label.text = $"MIGLIORA · {cost:N0} CR".Replace(",", "."); btn[i].box.color = ok ? k.accent : cPanel; btn[i].label.color = ok ? cInk : cGrey;
                }
                foot.text = port ? Good + "In porto: il cantiere è aperto." + End + $"   Crediti disponibili: {credits:N0}".Replace(",", ".") : Grey + "In navigazione: il cantiere apre al prossimo attracco." + End;
            });
        }

        // Galassia: reputazione presso le fazioni, crisi in corso, personaggi ricercati.
        static void BuildGalaxy(Kit k)
        {
            const int NF = 5, NP = 3;
            k.Section(-4f, "Reputazione presso le fazioni", "");
            var fname = new UILabel[NF]; var fbar = new Action<float, float, float, Color>[NF]; var flvl = new UILabel[NF]; var fwar = new UILabel[NF];
            for (int i = 0; i < NF; i++)
            {
                float y = -26f - i * 19f;
                fname[i] = k.Text(Kit.L, y, 140f, "", 10, cWhite);
                fbar[i] = k.Bipolar(Kit.L + 142f, y - 5f, 100f, 6f);
                flvl[i] = k.Text(Kit.L + 250f, y, 110f, "", 10, cWhite);
                fwar[i] = k.Text(Kit.L + 360f, y, 112f, "", 10, cGrey, NGUIText.Alignment.Right);
            }
            k.Rule(-124f);
            k.Section(-132f, "Crisi", "");
            var ctitle = k.Text(Kit.L + 60f, -132f, Kit.W - 60f, "", 11, cWhite, NGUIText.Alignment.Right);
            var cstage = k.Text(Kit.L, -154f, 230f, "", 10, cGrey);
            var cmeter = k.Meter(Kit.L + 236f, -159f, Kit.W - 236f, 6f);
            var chint = k.Text(Kit.L, -170f, Kit.W, "", 10, cGrey); var chint2 = k.Text(Kit.L, -184f, Kit.W, "", 10, cGrey);
            k.Rule(-204f);
            k.Section(-212f, "Ricercati", "");
            var pname = new UILabel[NP]; var pinfo = new UILabel[NP]; var pmem = new UILabel[NP];
            for (int i = 0; i < NP; i++)
            {
                float y = -236f - i * 34f;
                pname[i] = k.Text(Kit.L, y, 300f, "", 11, cWhite);
                pinfo[i] = k.Text(Kit.L + 300f, y, 172f, "", 10, cGrey, NGUIText.Alignment.Right);
                pmem[i] = k.Text(Kit.L, y - 15f, Kit.W, "", 10, cGrey);
            }
            var crew = k.Text(Kit.L, -342f, Kit.W, "", 10, cWhite);
            var atlas = k.Text(Kit.L, -360f, Kit.W, "", 10, cBlue);
            k.pg.Updaters.Add((id, now) =>
            {
                var v = GalaxyView.Current;
                atlas.text = v.atlas;
                crew.text = $"Morale: {(v.morale >= 60 ? Good : v.morale < 40 ? Bad : "")}{v.morale} · {Morale.Level(v.morale).ToLowerInvariant()}{(v.morale >= 60 || v.morale < 40 ? End : "")}   ·   Nei porti: " + Amber + v.fame + End + (v.wantedLevel > 0 ? "   " + Bad + $"RICERCATI {v.wantedLevel}/5" + End : "");
                for (int i = 0; i < NF; i++)
                {
                    var f = i < v.factions.Count ? v.factions[i] : null;
                    if (f == null) { fname[i].text = flvl[i].text = fwar[i].text = ""; fbar[i](0, -100, 100, cTrack); continue; }
                    fname[i].text = Hex(f.color) + f.name + End;
                    var col = f.standing <= -25 ? cBad : f.standing >= 35 ? cGood : f.standing >= 10 ? k.accent : cGrey;
                    fbar[i](f.standing, -100, 100, col);
                    flvl[i].text = (f.standing <= -25 ? Bad : f.standing >= 35 ? Good : "") + f.level + (f.standing <= -25 || f.standing >= 35 ? End : "") + Grey + $" {f.standing:+0;-0;0}" + End;
                    fwar[i].text = f.offensive ? Bad + $"{f.sectors} settori, attacca" + End : $"{f.sectors} settori";
                }
                var c = v.crisis;
                if (c == null) { ctitle.text = string.IsNullOrEmpty(v.threat) ? Grey + "nessuna" + End : Hex("#9BE35B") + v.threat + End; cstage.text = Grey + "La galassia è tranquilla. Per ora." + End; var (d1, d2) = TwoLines(string.IsNullOrEmpty(v.diplo) ? "" : "Diplomazia: " + v.diplo, 78); chint.text = d1; chint2.text = d2; cmeter(0, cTrack); }
                else
                {
                    ctitle.text = Hex(c.color) + c.title + End;
                    cstage.text = $"Fase {c.stage + 1} di 3 · {c.progress}";
                    cmeter(c.frac, Crises.Kinds.TryGetValue(c.kind, out var kc) ? Color32(kc.color) : k.accent);
                    var (l1, l2) = TwoLines("Come aiutare: " + c.hint, 78); chint.text = l1; chint2.text = l2;
                }
                for (int i = 0; i < NP; i++)
                {
                    var p = i < v.wanted.Count ? v.wanted[i] : null;
                    if (p == null) { pname[i].text = i == 0 ? Grey + "Nessun nome sulla bocca di tutti." + End : ""; pinfo[i].text = pmem[i].text = ""; continue; }
                    string tier = p.rank >= 4 ? "#F2A65A" : p.rank >= 2 ? "#C58AF9" : "#9DB4FF";
                    pname[i].text = Hex(tier) + p.name + End + Grey + "  " + p.role + End + (p.hunted ? Bad + "  TAGLIA IN CORSO" + End : "");
                    pinfo[i].text = $"grado {p.rank}/5 · settore {p.sector} ({p.jumps} salti)";
                    pmem[i].text = p.memory;
                }
            });
        }
        static Color Color32(string hex) { ColorUtility.TryParseHtmlString(hex, out var c); return c; }

        // Officina: pezzi in stiva ordinati per grado; si sceglie una riga, poi si smonta o si potenzia (conferma col secondo clic).
        static void BuildWorkshop(Kit k)
        {
            const int Rows = 8;
            k.Section(-4f, "Officina di bordo", "Pezzi in stiva. Scegline uno, poi smontalo o tenta il potenziamento.");
            var rowBtn = new (UITexture box, UILabel label)[Rows]; var rowName = new UILabel[Rows]; var rowInfo = new UILabel[Rows];
            for (int i = 0; i < Rows; i++)
            {
                float y = -44f - i * 24f;
                rowBtn[i] = k.Button("wk|sel|" + i, Kit.L, y, Kit.W, 21f, "", cPanel, cWhite, 11);
                rowName[i] = k.Text(Kit.L + 8f, y - 3f, 330f, "", 11, cWhite);
                rowInfo[i] = k.Text(Kit.L + 340f, y - 3f, 124f, "", 10, cGrey, NGUIText.Alignment.Right);
            }
            k.Button("wk|prev", Kit.L, -238f, 60f, 20f, "<", cPanel, cWhite, 12); k.Button("wk|next", Kit.L + 66f, -238f, 60f, 20f, ">", cPanel, cWhite, 12);
            var page = k.Text(Kit.L + 134f, -240f, 200f, "", 10, cGrey);
            k.Rule(-264f);
            var det = new UILabel[5]; for (int i = 0; i < det.Length; i++) det[i] = k.Text(Kit.L, -272f - i * 17f, Kit.W, "", 10, cWhite);
            var bSalv = k.Button("wk|smonta", Kit.L, -360f, 150f, 22f, "SMONTA", cPanel, cWhite, 12);
            var bUp = k.Button("wk|potenzia", Kit.L + 158f, -360f, 150f, 22f, "POTENZIA", k.accent, cInk, 12);
            var sets = k.Text(Kit.L + 316f, -363f, 156f, "", 9, cGrey, NGUIText.Alignment.Right);

            k.pg.Updaters.Add((id, now) =>
            {
                var pg = k.pg; var ship = Stations.MyShip; pg.WorkRows.Clear();
                var cargo = new List<PLShipComponent>(); var equipped = new List<PLShipComponent>();
                try
                {
                    var all = ship?.MyStats?.m_AllComponents;
                    if (all != null) for (int i = 0; i < all.Count; i++) { var c = all[i]; if (c == null) continue; if (c.InCargoSlot()) cargo.Add(c); else if (c.IsEquipped) equipped.Add(c); }
                }
                catch { }
                int Tier(PLShipComponent c) => Rarity.Get(c.Pointer)?.Tier ?? 0;
                cargo = cargo.OrderByDescending(Tier).ThenBy(c => c.Name).ToList();
                int pages = Math.Max(1, (cargo.Count + Rows - 1) / Rows); pg.WorkPage = Math.Clamp(pg.WorkPage, 0, pages - 1);
                for (int i = 0; i < Rows; i++)
                {
                    int idx = pg.WorkPage * Rows + i; var c = idx < cargo.Count ? cargo[idx] : null;
                    bool on = rowBtn[i].box.gameObject.activeSelf; if (on != (c != null)) { rowBtn[i].box.gameObject.SetActive(c != null); }
                    if (c == null) { rowName[i].text = ""; rowInfo[i].text = ""; pg.WorkRows.Add(-1); continue; }
                    var tag = Rarity.Get(c.Pointer); var t = Rarity.Tier(tag?.Tier ?? 0); pg.WorkRows.Add(c.NetID);
                    rowBtn[i].box.color = c.NetID == pg.WorkSel ? Color.Lerp(cPanel, cWhite, 0.22f) : cPanel;
                    rowName[i].text = Hex(t.Color) + c.Name + End;
                    rowInfo[i].text = $"liv. {c.GetLevel()}   {t.Name.ToLowerInvariant()}";
                }
                page.text = cargo.Count == 0 ? Grey + "La stiva è vuota." + End : $"pagina {pg.WorkPage + 1} di {pages} · {cargo.Count} pezzi in stiva";
                var sel = cargo.FirstOrDefault(c => c.NetID == pg.WorkSel);
                for (int i = 0; i < det.Length; i++) det[i].text = "";
                if (sel == null) { pg.WorkSel = -1; det[0].text = Grey + "Nessun pezzo scelto." + End; }
                else
                {
                    var tag = Rarity.Get(sel.Pointer); int tier = tag?.Tier ?? 0; var t = Rarity.Tier(tier);
                    det[0].text = Hex(t.Color) + sel.Name + End + Grey + $"   {t.Name}" + (tag != null && tag.Bonus > 0 ? $", +{tag.Bonus} liv." : "") + End;
                    int line = 1;
                    if (tag != null) for (int i = 0; i < tag.Defs.Length && line < 4; i++, line++)
                    {
                        var a = tag.Defs[i]; string v = a.Mode == "add" ? $"{tag.Value[i]:+0;-0}" : $"{tag.Value[i] * 100:+0;-0}%";
                        string cond = a.Cond.Length > 0 && Loot.CondLabels.TryGetValue(a.Cond, out var cl) ? " (" + cl + ")" : "";
                        det[line].text = (a.Drawback ? Bad + "Difetto: " : Good) + (a.Label.Length > 0 ? a.Label : a.Key) + " " + v + cond + End;
                    }
                    int max = Rarity.Cfg.Tiers.Length - 2;
                    det[4].text = $"Smontaggio: +{Loot.SalvageValue(sel)} crediti   ·   " + (tier >= max ? Grey + "non potenziabile oltre questo grado" + End
                        : $"Potenziamento: {Loot.UpgradeCost(sel, tier)} crediti, riuscita {Loot.UpgradeChance(tier) * 100:0}%" + (tier >= 3 ? Bad + ", se fallisce il pezzo va perso" + End : ""));
                }
                bool armedS = pg.WorkArmed == "smonta" && now < pg.WorkArmedUntil, armedU = pg.WorkArmed == "potenzia" && now < pg.WorkArmedUntil;
                bSalv.label.text = armedS ? "CONFERMA" : "SMONTA"; bSalv.box.color = armedS ? cBad : cPanel; bSalv.label.color = armedS ? cInk : cWhite;
                bUp.label.text = armedU ? "CONFERMA" : "POTENZIA"; bUp.box.color = armedU ? cBad : k.accent;
                // set dei leggendari montati
                var setCount = equipped.Select(c => Rarity.Get(c.Pointer)).Where(x => x != null && x.Legend >= 0).Select(x => Rarity.Cfg.Legendaries[x.Legend].Set).Where(x => x.Length > 0).GroupBy(x => x).ToList();
                sets.text = setCount.Count == 0 ? "" : string.Join("  ", setCount.Select(g => $"{g.Key} {g.Count()}/4"));
            });
        }

        // Bacheca dei contratti: quattro offerte con committente, compenso, rischio e scadenza; accetta o rifiuta.
        static void BuildContracts(Kit k)
        {
            k.Section(-4f, "Bacheca dei contratti", "Scegli tu l'incarico. La fiducia dei committenti migliora le offerte.");
            var title = new UILabel[Contracts.Size]; var who = new UILabel[Contracts.Size]; var pay = new UILabel[Contracts.Size]; var desc = new UILabel[Contracts.Size]; var desc2 = new UILabel[Contracts.Size];
            var acc = new (UITexture box, UILabel label)[Contracts.Size]; var dec = new (UITexture box, UILabel label)[Contracts.Size]; var strip = new UITexture[Contracts.Size]; var rules = new UITexture[Contracts.Size];
            for (int i = 0; i < Contracts.Size; i++)
            {
                float y = -44f - i * 74f;
                strip[i] = k.Box(Kit.L, y, 4f, 64f, k.accent);
                title[i] = k.Text(Kit.L + 10f, y, 300f, "", 12, cWhite);
                pay[i] = k.Text(Kit.L + 312f, y, 70f, "", 12, cGood, NGUIText.Alignment.Right);
                who[i] = k.Text(Kit.L + 10f, y - 20f, 372f, "", 10, cGrey);
                desc[i] = k.Text(Kit.L + 10f, y - 34f, 372f, "", 10, cGrey); desc2[i] = k.Text(Kit.L + 10f, y - 48f, 372f, "", 10, cGrey);
                acc[i] = k.Button("bc|accetta|" + i, Kit.L + 390f, y, 82f, 26f, "ACCETTA", k.accent, cInk, 11);
                dec[i] = k.Button("bc|rifiuta|" + i, Kit.L + 390f, y - 32f, 82f, 22f, "RIFIUTA", cPanel, cWhite, 10);
                rules[i] = k.Box(Kit.L, y - 66f, Kit.W, 1f, cTrack);
            }
            var foot = k.Text(Kit.L, -344f, Kit.W, "", 10, cWhite);
            var trust = k.Text(Kit.L, -360f, Kit.W, "", 9, cGrey);
            var standings = k.Text(Kit.L, -374f, Kit.W, "", 9, cBlue);
            // incontro con scelta: sostituisce le offerte finché il capitano non decide
            const int EL = 6, EC = 4;
            var eTitle = k.Text(Kit.L + 10f, -44f, Kit.W - 10f, "", 13, k.accent); var eStrip = k.Box(Kit.L, -44f, 4f, 104f, k.accent);
            var eLine = new UILabel[EL]; for (int i = 0; i < EL; i++) eLine[i] = k.Text(Kit.L + 10f, -66f - i * 15f, Kit.W - 10f, "", 10, cWhite);
            var eBtn = new (UITexture box, UILabel label)[EC]; var eHint = new UILabel[EC];
            for (int i = 0; i < EC; i++) { float y = -164f - i * 42f; eBtn[i] = k.Button("enc|" + i, Kit.L, y, Kit.W, 24f, "", k.accent, cInk, 11); eHint[i] = k.Text(Kit.L + 8f, y - 26f, Kit.W - 8f, "", 9, cGrey); }
            void Show(GameObject g, bool on) { if (g.activeSelf != on) g.SetActive(on); }

            k.pg.Updaters.Add((id, now) =>
            {
                var enc = Encounters.Current; bool encOn = enc != null;
                Show(eTitle.gameObject, encOn); Show(eStrip.gameObject, encOn);
                for (int i = 0; i < EL; i++) Show(eLine[i].gameObject, encOn);
                for (int i = 0; i < EC; i++)
                {
                    var ch = encOn && i < enc.choices.Count ? enc.choices[i] : null; bool on = ch != null;
                    Show(eBtn[i].box.gameObject, on); Show(eBtn[i].label.gameObject, on); Show(eHint[i].gameObject, on);
                    if (!on) continue;
                    eBtn[i].label.text = ch.label.ToUpperInvariant(); eBtn[i].box.color = ch.ok ? k.accent : cPanel; eBtn[i].label.color = ch.ok ? cInk : cGrey;
                    eHint[i].text = ch.ok ? "" : "Non disponibile: " + ch.hint + ".";
                }
                for (int i = 0; i < Contracts.Size; i++) Show(rules[i].gameObject, !encOn);
                if (encOn)
                {
                    eTitle.text = "INCONTRO · " + enc.title.ToUpperInvariant();
                    var lines = Wrap(enc.text, 72, EL); for (int i = 0; i < EL; i++) eLine[i].text = lines[i];
                    for (int i = 0; i < Contracts.Size; i++) { title[i].text = pay[i].text = who[i].text = desc[i].text = desc2[i].text = ""; Show(acc[i].box.gameObject, false); Show(acc[i].label.gameObject, false); Show(dec[i].box.gameObject, false); Show(dec[i].label.gameObject, false); Show(strip[i].gameObject, false); }
                    foot.text = Amber + $"Decide il capitano: {enc.left} secondi, poi si sceglie l'opzione più prudente." + End; trust.text = ""; standings.text = "";
                    return;
                }
                var board = Contracts.Board;
                for (int i = 0; i < Contracts.Size; i++)
                {
                    var o = i < board.Count ? board[i] : null; bool on = o != null;
                    if (acc[i].box.gameObject.activeSelf != on) { acc[i].box.gameObject.SetActive(on); acc[i].label.gameObject.SetActive(on); dec[i].box.gameObject.SetActive(on); dec[i].label.gameObject.SetActive(on); strip[i].gameObject.SetActive(on); }
                    if (!on) { title[i].text = i == 0 && board.Count == 0 ? Grey + "Nessuna offerta: ne arriveranno al prossimo salto." + End : ""; pay[i].text = who[i].text = desc[i].text = desc2[i].text = ""; continue; }
                    strip[i].color = o.urgent ? cBad : k.accent;
                    title[i].text = (o.urgent ? Bad + "URGENTE " + End : "") + o.family.ToUpperInvariant() + Grey + " · " + End + o.title.Replace(o.family + ": ", "");
                    pay[i].text = $"{o.reward:N0} Cr".Replace(",", ".");
                    int left = Contracts.JumpsLeft(o);
                    who[i].text = Hex(o.color) + o.issuer + End + Grey + $"   rischio {new string('|', o.tier + 1)}{new string('.', 4 - o.tier)}   scade {(left <= 0 ? "a questo salto" : left == 1 ? "fra 1 salto" : $"fra {left} salti")}   fiducia {Contracts.Trust(o.issuer):+0;-0;0}" + End;
                    var (l1, l2) = TwoLines(o.desc, 64); desc[i].text = l1; desc2[i].text = l2;
                }
                var cur = Contracts.Current;
                string camp = GalaxyView.Current?.campaign ?? "";
                foot.text = cur != null ? Good + $"In corso: {cur.title} per {cur.issuer}, {cur.reward:N0} crediti alla consegna.".Replace(",", ".") + End : camp.Length > 0 ? Amber + camp + End : Grey + "Nessun contratto in corso." + End;
                standings.text = Competitors.Standings();
                var tr = Saga.State.trust.Where(x => x.Value != 0).OrderByDescending(x => x.Value).Take(4).Select(x => $"{x.Key} {x.Value:+0;-0}");
                trust.text = tr.Any() ? "Fiducia: " + string.Join("   ", tr) : "";
            });
        }

        // Divide un testo in n righe a fine parola; l'ultima finisce con i puntini se avanza.
        static string[] Wrap(string text, int max, int n)
        {
            var r = new string[n]; for (int i = 0; i < n; i++) r[i] = "";
            var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries); int line = 0;
            foreach (var w in words)
            {
                if (line >= n) { r[n - 1] = r[n - 1].TrimEnd('.') + "…"; break; }
                if (r[line].Length + w.Length + 1 > max && r[line].Length > 0) { line++; if (line >= n) { r[n - 1] += "…"; break; } }
                r[line] += (r[line].Length > 0 ? " " : "") + w;
            }
            return r;
        }

        // Divide un testo in due righe a fine parola; se avanza, la seconda finisce con i puntini.
        static (string, string) TwoLines(string text, int max)
        {
            text = (text ?? "").Trim(); if (text.Length <= max) return (text, "");
            int cut = text.LastIndexOf(' ', max); if (cut < max / 2) cut = max;
            string a = text[..cut].TrimEnd(), b = text[cut..].Trim();
            if (b.Length > max) { int c2 = b.LastIndexOf(' ', max - 1); b = b[..(c2 > max / 2 ? c2 : max - 1)].TrimEnd() + "…"; }
            return (a, b);
        }

        static void ContractsClick(PanelPage pg, string[] a)
        {
            int i = int.Parse(a[3]); var board = Contracts.Board; if (i >= board.Count) return;
            if (a[2] == "accetta" && !Stations.CanEdit(0)) { Say(pg, "Solo il capitano firma i contratti."); return; }
            Contracts.Request(a[2], board[i].id); Say(pg, a[2] == "accetta" ? "Contratto in firma..." : "Offerta rifiutata.");
        }

        static void WorkshopClick(PanelPage pg, string[] a)
        {
            long now = clock.ElapsedMilliseconds;
            switch (a[2])
            {
                case "sel": { int i = int.Parse(a[3]); if (i < pg.WorkRows.Count && pg.WorkRows[i] >= 0) { pg.WorkSel = pg.WorkRows[i]; pg.WorkArmed = ""; } return; }
                case "prev": pg.WorkPage--; return;
                case "next": pg.WorkPage++; return;
                case "smonta": case "potenzia":
                    if (pg.WorkSel < 0) { Say(pg, "Scegli prima un pezzo della stiva."); return; }
                    if (pg.WorkArmed != a[2] || now > pg.WorkArmedUntil) { pg.WorkArmed = a[2]; pg.WorkArmedUntil = now + 4000; Say(pg, "Premi di nuovo per confermare."); return; }
                    pg.WorkArmed = ""; Loot.Request(a[2], pg.WorkSel); pg.WorkSel = -1; return;
            }
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
            if (ChargeLevel(pg, now) >= w.ChargeMax + Loot.Bonus(id, "overload_margin")) Overload(pg, id, now);
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
            foreach (var kv in pages.ToArray())
            {
                var pg = kv.Value;
                try
                {
                    // schermo distrutto (nave sparita, settore lasciato): la pagina e tutto ciò che tiene si rilasciano
                    if (pg.Screen == null) { pages.Remove(kv.Key); Released++; continue; }
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
                if (lvl > w.OverloadAt + Loot.Bonus(id, "overload_margin")) { Overload(pg, id, now); return; }
                pg.ChargeStart = -1; pg.BurstReady = now + w.BurstCooldown * 1000L;
                float pct = (float)Math.Round(w.BurstMaxDamage * lvl / 100f * (1f + Loot.Bonus(id, "burst_damage")));
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
                pg.EvadeReady = now + (long)(k.EvadeCooldown * 1000L * (1f - Math.Clamp(Loot.Bonus(id, "evade_cooldown"), 0f, 0.7f))); Stations.Set(id, "plx_evade", k.EvadeSeconds); Service.Add("pilota.manovre", 1); Stations.LogEvent(id, "Pilota: manovra evasiva"); Say(pg, "Manovra evasiva!"); return;
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
            if (a[2] == "hail") { Hail.Request(a[3]); Say(pg, "Chiamata in corso..."); return; }
            if (a[2] == "board") { Boarding.Request(a[3]); Say(pg, a[3] == "abborda" ? "Squadra al teletrasporto..." : "Richiamo della squadra..."); return; }
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
            // la banca dati la sfoglia chiunque
            if (a[1] == "cdx") { if (a[2] == "cat") { pg.CodexCat = Codex.Cats[int.Parse(a[3])]; pg.CodexIdx = 0; } else pg.CodexIdx += a[2] == "next" ? 1 : -1; Refresh(pg); return true; }
            if (mine == null || !Stations.CanEdit(pg.Station)) { Refresh(pg); return true; }
            if (a[1] == "p") Stations.ApplyPreset(mine.ShipID, Stations.Cfg.Presets[int.Parse(a[2])]);
            else if (a[1] == "core") CoreClick(pg, a, mine.ShipID);
            else if (a[1] == "sci") SciClick(pg, a, mine.ShipID);
            else if (a[1] == "wep") WepClick(pg, a, mine.ShipID);
            else if (a[1] == "plt") PilotClick(pg, a, mine.ShipID);
            else if (a[1] == "cap") CaptainClick(pg, a, mine.ShipID);
            else if (a[1] == "dmg") DamageClick(pg, a, mine.ShipID);
            else if (a[1] == "wk") WorkshopClick(pg, a);
            else if (a[1] == "bc") ContractsClick(pg, a);
            else if (a[1] == "pr") { if (a[2] == "lancia") { Probes.Request("lancia"); Say(pg, "Sonda in lancio..."); } else { int i = int.Parse(a[3]); var rows = Probes.Rows; if (i < rows.Count) { Probes.Request("rotta", rows[i].sector); Say(pg, $"Rotta verso il settore {rows[i].sector}."); } } }
            else if (a[1] == "rc")
            {
                var pr = Research.Projects[Math.Clamp(int.Parse(a[3]), 0, Research.Projects.Length - 1)];
                Research.Request("avvia", pr.id); Say(pg, "Progetto richiesto...");
            }
            else if (a[1] == "wr") { Wear.Request(a[2] == "revisione" ? "revisione" : "ripara", a.Length > 3 ? a[3] : ""); Say(pg, "Ordine all'officina..."); }
            else if (a[1] == "fb") { Fabrication.Request(a[2], a.Length > 3 ? a[3] : ""); Say(pg, a[2] == "annulla" ? "Annullo l'ultima lavorazione..." : "Lavorazione richiesta..."); }
            else if (a[1] == "cc") { CrewMind.Request(a[2], a.Length > 3 ? a[3] : ""); Say(pg, "Ordine trasmesso..."); }
            else if (a[1] == "pax")
            {
                var p = GalaxyView.Current?.pax ?? new Passengers.Panel(); int i = a.Length > 3 && int.TryParse(a[3], out var x) ? x : 0;
                if (i < p.rows.Count) { Passengers.Request(a[2], p.rows[i].id.ToString()); Say(pg, "Richiesta inviata..."); }
            }
            else if (a[1] == "ind")
            {
                var p = GalaxyView.Current?.mystery ?? new Mysteries.Panel(); int i = a.Length > 3 && int.TryParse(a[3], out var x) ? x : 0;
                if (a[2] == "prev") pg.MystPage = Math.Max(0, pg.MystPage - 1);
                else if (a[2] == "next") pg.MystPage++;
                else if (a[2] is "decifra" or "sonda" or "rotta") { if (i < p.signals.Count) { Mysteries.Request(a[2], p.signals[i].id.ToString()); Say(pg, "Richiesta alla Scienza..."); } }
                else if (a[2] == "scavo") { if (p.digSectors.Count > 0) Mysteries.Request("scavo", p.digSectors[0].ToString()); }
                else { int idx = pg.MystPage * 3 + i; if (idx < p.relics.Count) { Mysteries.Request(a[2], p.relics[idx].id.ToString()); Say(pg, "Richiesta inviata..."); } }
            }
            else if (a[1] == "mk")
            {
                var g = Market.Goods[Math.Clamp(int.Parse(a[3]), 0, Market.Goods.Length - 1)];
                if (a[2] == "compra") Market.Request("compra", g.id + ",5");
                else if (a[2] == "vendi") Market.Request("vendi", g.id + ",999");
                else if (a[2] == "rotta") Market.Request("rotta", g.id);
                else if (a[2] == "scorte") Market.Request("scorte", g.id);
                else if (a[2] == "rimborsa") Market.Request("rimborsa", g.id);
            }
            else if (a[1] == "bs")
            {
                var kinds = Bases.Kinds; var rows = GalaxyView.Current?.bases?.bases ?? new List<Bases.View>();
                if (a[2] == "prev") pg.BaseKind = (pg.BaseKind + kinds.Length - 1) % kinds.Length;
                else if (a[2] == "next") pg.BaseKind = (pg.BaseKind + 1) % kinds.Length;
                else if (a[2] == "fonda") { Bases.Request("fonda", kinds[pg.BaseKind].id); Say(pg, "Richiesta di fondazione..."); }
                else if (a[2] == "migliora" || a[2] == "difese") { Bases.Request(a[2]); Say(pg, "Lavori richiesti..."); }
                else if (a[2] == "rotta") { int i = int.Parse(a[3]); if (i < rows.Count) Bases.Request("rotta", rows[i].sector.ToString()); }
            }
            else if (a[1] == "yd") { Shipyard.Request(a[2]); Say(pg, "Richiesta al cantiere..."); }
            else if (a[1] == "enc") { Encounters.Request(int.Parse(a[2]), Encounters.Serial); Say(pg, "Ordine trasmesso."); }
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
                Wear.OnShieldHit(vsh, inDmg);
                var atk = turret?.ShipStats?.Ship; if (atk == null) { NoTurret++; return; }
                Traffic.NoteHit(vsh, atk);
                inDmg *= (1f + 0.04f * Talents.R(atk, "sfondatore")) * (1f - 0.03f * Talents.R(vsh, "schermi"));
                var me = Stations.MyShip; if (me != null && me.ShipID == atk.ShipID) Mine++;
                int aim = (int)Stations.Get(atk.ShipID, "wpx_aim"); var w = Stations.Cfg.Weapons; LastIn = inDmg;
                if (aim == 1) inDmg *= 1f + w.AimShieldsBonus / 100f; else if (aim == 2) inDmg *= 1f - w.AimHullShieldPenalty / 100f;
                if (aim != 0) Modified++; LastOut = inDmg;
            }
            catch { }
        }
    }
}

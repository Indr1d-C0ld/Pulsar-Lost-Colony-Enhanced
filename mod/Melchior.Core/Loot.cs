// Bottino: catalogo degli affissi (statistici, di stazione, condizionali, difetti), leggendari e set,
// applicazione alla nave, bonus per i sistemi delle stazioni, annunci dei ritrovamenti, Officina dell'Ingegneria
// e doni di bottino dalle nuove fonti (missioni, capi rivali, crisi superate).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Melchior
{
    public static class Loot
    {
        // ---- etichette dei bonus di stazione e delle condizioni
        public static readonly Dictionary<string, string> KeyLabels = new()
        {
            ["core_cooling"] = "Smaltimento dello stress del nucleo", ["core_load"] = "Stress del nucleo da carico ridotto",
            ["scan_speed"] = "Velocità di scansione", ["harmonic_decay"] = "Tenuta dell'armonica degli scudi",
            ["repair_speed"] = "Velocità delle squadre di riparazione", ["overload_margin"] = "Margine dei condensatori",
            ["burst_damage"] = "Potenza della raffica", ["evade_cooldown"] = "Ricarica della manovra evasiva",
            ["initiative"] = "Recupero dell'iniziativa di comando", ["incoming"] = "Danni allo scafo ridotti",
            ["subsystem"] = "Danno ai sistemi nemici", ["weakpoint"] = "Danno sul punto debole",
            ["sys_resist"] = "Resistenza dei sistemi di bordo", ["engage_gain"] = "Assestamento nella fascia d'ingaggio",
            ["commercio"] = "Prezzi di vendita delle merci", ["stiva"] = "Posti in stiva", ["ricerca"] = "Dati di ricerca raccolti",
            ["arrembaggio"] = "Bottino e sabotaggi negli abbordaggi", ["sentinella"] = "Danni degli incursori a bordo ridotti",
            ["viveri"] = "Viveri consumati ridotti", ["dogane"] = "Probabilità di ispezioni doganali ridotta", ["furtivita"] = "Imboscate ridotte",
            ["fama"] = "Fama guadagnata", ["morale"] = "Morale guadagnato", ["recupero"] = "Componenti recuperati dai relitti", ["sonde"] = "Costo delle sonde ridotto",
            ["pirati"] = "Imboscate attirate", ["rumore"] = "Ispezioni doganali attirate",
        };
        public static readonly Dictionary<string, string> CondLabels = new()
        {
            ["rosso"] = "in allarme rosso", ["scafo"] = "con lo scafo sotto il 35%", ["scudi"] = "a scudi abbattuti",
            ["corta"] = "in fascia d'ingaggio corta", ["lunga"] = "in fascia d'ingaggio lunga", ["salto"] = "nei 30 secondi dopo un salto",
            ["crisi"] = "durante una crisi", ["silenzio"] = "con l'ordine SILENZIO", ["nucleo"] = "con il nucleo oltre il 60% di stress",
        };

        static AffixDef S(string name, string label, string stat, float min, float max, params int[] slots) => new() { Name = name, Label = label, Stat = stat, Min = min, Max = max, Slots = slots };
        static AffixDef K(string name, string key, float min, float max, params int[] slots) => new() { Name = name, Label = KeyLabels[key], Key = key, Min = min, Max = max, Slots = slots, Mode = key is "overload_margin" or "stiva" ? "add" : "mul" };
        static AffixDef C(string name, string label, string stat, string cond, float min, float max, params int[] slots) => new() { Name = name, Label = label, Stat = stat, Cond = cond, Min = min, Max = max, Slots = slots };
        static AffixDef D(string name, string label, string stat, float min, float max) => new() { Name = name, Label = label, Stat = stat, Min = min, Max = max, Drawback = true };
        static AffixDef DK(string name, string key, float min, float max) => new() { Name = name, Label = KeyLabels[key], Key = key, Min = min, Max = max, Drawback = true };
        static AffixDef E(string stat, float v, string cond = "") => new() { Name = "", Label = Loot.StatLabel(stat), Stat = stat, Min = v, Max = v, Cond = cond };
        static AffixDef EK(string key, float v) => new() { Name = "", Label = KeyLabels[key], Key = key, Min = v, Max = v, Mode = key is "overload_margin" or "stiva" ? "add" : "mul" };

        public static string StatLabel(string stat) => stat switch
        {
            "ReactorOutputMax" => "Potenza del reattore", "ReactorTempMax" => "Temperatura massima del reattore", "ReactorCoolantCapacity" => "Capacità del refrigerante",
            "ReactorHeatOutputFactor" => "Calore prodotto dal reattore", "StartupSpeed" => "Velocità di avvio", "ShieldsMax" => "Scudi massimi",
            "ShieldsChargeRateMax" => "Ricarica degli scudi", "ShieldsDeflection" => "Deflessione degli scudi", "ShieldsRecoveryRate" => "Recupero degli scudi",
            "HullMax" => "Integrità dello scafo", "HullArmor" => "Corazza dello scafo", "HullDefense" => "Difesa dello scafo", "HullSysProtection" => "Protezione dei sistemi",
            "Mass" => "Massa della nave", "WarpChargeRate" => "Carica della curvatura", "WarpRange" => "Raggio di curvatura", "ThrustOutputMax" => "Spinta principale",
            "InertiaThrustOutputMax" => "Spinta inerziale", "ManeuverThrustOutputMax" => "Spinta di manovra", "TurretDamageFactor" => "Danno delle torrette",
            "TurretChargeFactor" => "Carica delle torrette", "TurretCoolingSpeedFactor" => "Raffreddamento delle torrette", "EMDetection" => "Rilevamento EM",
            "QTDetection" => "Rilevamento QT", "LFDetection" => "Rilevamento LF", "RADetection" => "Rilevamento RA", "EMSignature" => "Firma EM",
            "LFSignature" => "Firma LF", "RASignature" => "Firma RA", "CyberDefenseRating" => "Difesa informatica", "CyberAttackRating" => "Attacco informatico",
            "OxygenRefillRate" => "Rigenerazione dell'ossigeno", "DiplomacyBonus" => "Diplomazia", "IntimidationBonus" => "Intimidazione",
            "SalvageStabilityPercent" => "Stabilità del recupero", _ => stat,
        };

        // ESlotType: 1 scudi, 2 curvatura, 3 reattore, 5 sensori, 6 scafo, 7 computer, 8 ossigeno, 9 propulsori, 10 torrette,
        // 11 torretta principale, 16 piastre, 24 torretta automatica, 25 inerziali, 26 manovra, 32 sensori, 33 occultamento.
        public static AffixDef[] DefaultAffixes() => new[]
        {
            // statistici
            S("a matrice di dilitio", "Potenza del reattore", "ReactorOutputMax", 0.06f, 0.15f, 3),
            S("a raffreddamento criogenico", "Temperatura massima del reattore", "ReactorTempMax", 0.05f, 0.12f, 3),
            S("a refrigerante pressurizzato", "Capacità del refrigerante", "ReactorCoolantCapacity", 0.10f, 0.25f, 3),
            S("a dispersione termica", "Calore prodotto dal reattore", "ReactorHeatOutputFactor", -0.15f, -0.06f, 3),
            S("ad avvio rapido", "Velocità di avvio", "StartupSpeed", 0.20f, 0.50f, 3, 2),
            S("a emettitori multifasici", "Scudi massimi", "ShieldsMax", 0.06f, 0.15f, 1),
            S("rigenerativo", "Ricarica degli scudi", "ShieldsChargeRateMax", 0.08f, 0.20f, 1),
            S("a deflessione angolata", "Deflessione degli scudi", "ShieldsDeflection", 0.05f, 0.12f, 1),
            S("a recupero rapido", "Recupero degli scudi", "ShieldsRecoveryRate", 0.10f, 0.25f, 1),
            S("in duranio rinforzato", "Integrità dello scafo", "HullMax", 0.06f, 0.15f, 6, 16),
            S("ad armatura ablativa", "Corazza dello scafo", "HullArmor", 0.05f, 0.12f, 6, 16),
            S("a strati compositi", "Difesa dello scafo", "HullDefense", 0.05f, 0.12f, 6, 16),
            S("a paratie stagne", "Protezione dei sistemi", "HullSysProtection", 0.08f, 0.20f, 6, 16),
            S("in lega leggera", "Massa della nave", "Mass", -0.10f, -0.04f, 6, 16, 9),
            S("a bobine ricalibrate", "Carica della curvatura", "WarpChargeRate", 0.10f, 0.25f, 2),
            S("a geometria variabile", "Raggio di curvatura", "WarpRange", 0.08f, 0.20f, 2),
            S("a impulso potenziato", "Spinta principale", "ThrustOutputMax", 0.06f, 0.15f, 9),
            S("con smorzatori inerziali", "Spinta inerziale", "InertiaThrustOutputMax", 0.08f, 0.18f, 25, 9),
            S("a manovra fine", "Spinta di manovra", "ManeuverThrustOutputMax", 0.08f, 0.18f, 26, 9),
            S("a banchi sincronizzati", "Danno delle torrette", "TurretDamageFactor", 0.04f, 0.10f, 10, 11, 24),
            S("a condensatori rapidi", "Carica delle torrette", "TurretChargeFactor", 0.05f, 0.12f, 10, 11, 24, 7),
            S("a dissipatori attivi", "Raffreddamento delle torrette", "TurretCoolingSpeedFactor", 0.08f, 0.20f, 10, 11, 24),
            S("con sensori laterali", "Rilevamento EM", "EMDetection", 0.10f, 0.25f, 5, 32),
            S("a interferometria quantistica", "Rilevamento QT", "QTDetection", 0.10f, 0.25f, 5, 32),
            S("ad antenne a bassa frequenza", "Rilevamento LF", "LFDetection", 0.10f, 0.25f, 5, 32),
            S("a spettrometro radiante", "Rilevamento RA", "RADetection", 0.10f, 0.25f, 5, 32),
            S("a firewall adattivo", "Difesa informatica", "CyberDefenseRating", 0.10f, 0.25f, 7),
            S("a intrusione silenziosa", "Attacco informatico", "CyberAttackRating", 0.10f, 0.25f, 7),
            S("a riciclo atmosferico", "Rigenerazione dell'ossigeno", "OxygenRefillRate", 0.15f, 0.35f, 8),
            S("a campo di occultamento", "Firma EM", "EMSignature", -0.30f, -0.12f, 33),
            S("a schermatura LF", "Firma LF", "LFSignature", -0.30f, -0.12f, 33),
            S("a schermatura RA", "Firma RA", "RASignature", -0.30f, -0.12f, 33),
            // universali, più deboli
            S("schermato", "Firma EM", "EMSignature", -0.12f, -0.04f),
            S("a paratie rinforzate", "Integrità dello scafo", "HullMax", 0.02f, 0.06f),
            S("con scudo ausiliario", "Scudi massimi", "ShieldsMax", 0.02f, 0.06f),
            S("a condotti EPS ottimizzati", "Potenza del reattore", "ReactorOutputMax", 0.02f, 0.05f),
            S("a subroutine difensive", "Difesa informatica", "CyberDefenseRating", 0.04f, 0.10f),
            S("a protocollo diplomatico", "Diplomazia", "DiplomacyBonus", 0.05f, 0.15f),
            S("dal profilo minaccioso", "Intimidazione", "IntimidationBonus", 0.05f, 0.15f),
            S("con stabilizzatori di recupero", "Stabilità del recupero", "SalvageStabilityPercent", 0.05f, 0.15f),
            // di stazione: agiscono sui sistemi dei pannelli
            K("a scambiatori di calore", "core_cooling", 0.10f, 0.30f, 3, 8),
            K("a distribuzione di carico", "core_load", 0.08f, 0.25f, 3),
            K("a correlatore di segnali", "scan_speed", 0.15f, 0.40f, 5, 32, 7),
            K("a modulazione caotica", "harmonic_decay", 0.15f, 0.35f, 1),
            K("con nanorobot di riparazione", "repair_speed", 0.15f, 0.40f, 6, 16, 8),
            K("a condensatori sovradimensionati", "overload_margin", 3f, 8f, 10, 11, 24),
            K("a scarica sincrona", "burst_damage", 0.10f, 0.30f, 10, 11),
            K("a ugelli vettoriali", "evade_cooldown", 0.10f, 0.30f, 25, 26, 9),
            K("con assistente tattico", "initiative", 0.15f, 0.40f, 7),
            K("a dissipazione cinetica", "incoming", 0.04f, 0.10f, 6, 16),
            K("a puntamento selettivo", "subsystem", 0.10f, 0.30f, 10, 11, 5),
            K("ad analisi strutturale", "weakpoint", 0.03f, 0.08f, 5, 32),
            K("a ridondanza dei circuiti", "sys_resist", 0.10f, 0.30f, 6, 16, 7),
            K("a stabilizzazione di rotta", "engage_gain", 0.15f, 0.40f, 9, 25, 26),
            K("con protocolli di comando", "initiative", 0.05f, 0.15f),
            K("a giunti di riserva", "sys_resist", 0.04f, 0.12f),
            // condizionali
            C("da battaglia", "Danno delle torrette", "TurretDamageFactor", "rosso", 0.08f, 0.18f, 10, 11, 24),
            C("dell'ultima difesa", "Ricarica degli scudi", "ShieldsChargeRateMax", "scafo", 0.15f, 0.35f, 1),
            C("a scafo reattivo", "Corazza dello scafo", "HullArmor", "scudi", 0.10f, 0.25f, 6, 16),
            C("da abbordaggio", "Danno delle torrette", "TurretDamageFactor", "corta", 0.10f, 0.20f, 10, 11),
            C("da tiro lontano", "Ricarica degli scudi", "ShieldsChargeRateMax", "lunga", 0.10f, 0.25f, 1, 5),
            C("a uscita di curvatura", "Spinta principale", "ThrustOutputMax", "salto", 0.15f, 0.30f, 9, 2),
            C("da emergenza", "Potenza del reattore", "ReactorOutputMax", "crisi", 0.08f, 0.18f, 3),
            C("dei corridori silenziosi", "Firma EM", "EMSignature", "silenzio", -0.40f, -0.20f, 33, 5, 7),
            C("a sovraspinta", "Carica delle torrette", "TurretChargeFactor", "nucleo", 0.10f, 0.22f, 3, 10),
            // difetti degli oggetti instabili
            D("firma instabile", "Firma EM", "EMSignature", 0.10f, 0.25f),
            D("tendenza al surriscaldamento", "Temperatura massima del reattore", "ReactorTempMax", -0.08f, -0.04f),
            D("interferenze sugli scudi", "Ricarica degli scudi", "ShieldsChargeRateMax", -0.10f, -0.05f),
            D("zavorra", "Massa della nave", "Mass", 0.05f, 0.10f),
            D("codice instabile", "Difesa informatica", "CyberDefenseRating", -0.20f, -0.10f),
            DK("nucleo sotto sforzo", "core_load", -0.20f, -0.10f),
            DK("circuiti fragili", "sys_resist", -0.20f, -0.10f),
            // legati ai sistemi della galassia viva (bonus di stazione, validi per tutta la nave)
            K("da mercante", "commercio", 0.03f, 0.08f, 7, 5), K("a stiva modulare", "stiva", 2f, 6f, 6, 16), K("a calcolo scientifico", "ricerca", 0.10f, 0.30f, 7, 5, 32),
            K("da arrembaggio", "arrembaggio", 0.15f, 0.40f, 6, 10), K("a paratie stagne", "sentinella", 0.15f, 0.35f, 6, 8), K("a riciclo biologico", "viveri", 0.08f, 0.20f, 8),
            K("a doppiofondo", "dogane", 0.15f, 0.35f, 6, 33), K("a profilo basso", "furtivita", 0.06f, 0.15f, 33, 5, 9), K("da parata", "fama", 0.10f, 0.25f, 6, 11),
            K("confortevole", "morale", 0.10f, 0.25f, 8, 7), K("da recupero", "recupero", 0.10f, 0.25f, 5, 32), K("a lancio economico", "sonde", 0.15f, 0.35f, 5, 32),
            DK("faro per i pirati", "pirati", 0.08f, 0.18f), DK("rumoroso alle dogane", "rumore", 0.10f, 0.25f),
        };
        // ---- case produttrici: ognuna ha uno stile (affissi preferiti) e un prestigio (prezzo)
        public sealed class Maker { public string Name = "", Code = "", Motto = ""; public float Prestige = 1f; public string[] Bias = Array.Empty<string>(); }
        public static readonly Maker[] Makers =
        {
            new() { Name = "Kessler Dynamics", Code = "KD", Motto = "potenza grezza, senza fronzoli", Prestige = 1.05f, Bias = new[] { "ReactorOutputMax", "ThrustOutputMax", "TurretDamageFactor" } },
            new() { Name = "Cantieri Okafor", Code = "OK", Motto = "scafi che non si arrendono", Prestige = 1.0f, Bias = new[] { "HullMax", "HullArmor", "sentinella", "sys_resist" } },
            new() { Name = "Vega Precision", Code = "VP", Motto = "la precisione è un'arte", Prestige = 1.2f, Bias = new[] { "weakpoint", "subsystem", "scan_speed", "TurretChargeFactor" } },
            new() { Name = "Officine Lindqvist", Code = "LQ", Motto = "costruito per durare", Prestige = 0.95f, Bias = new[] { "repair_speed", "core_cooling", "ReactorCoolantCapacity" } },
            new() { Name = "Moreau & Figli", Code = "MF", Motto = "eleganza fra le stelle", Prestige = 1.3f, Bias = new[] { "morale", "fama", "DiplomacyBonus", "commercio" } },
            new() { Name = "Sato Kinetics", Code = "SK", Motto = "più veloce di chi ti insegue", Prestige = 1.1f, Bias = new[] { "ManeuverThrustOutputMax", "WarpChargeRate", "evade_cooldown", "furtivita" } },
            new() { Name = "Ilves Scientific", Code = "IS", Motto = "conoscere è sopravvivere", Prestige = 1.15f, Bias = new[] { "ricerca", "sonde", "EMDetection", "scan_speed" } },
            new() { Name = "Barros Salvage", Code = "BS", Motto = "niente si butta", Prestige = 0.85f, Bias = new[] { "recupero", "stiva", "SalvageStabilityPercent" } },
            new() { Name = "Mercanti di Navarro", Code = "MN", Motto = "ogni cassa è un affare", Prestige = 1.0f, Bias = new[] { "commercio", "stiva", "dogane" } },
            new() { Name = "Arsenale della Frontiera", Code = "AF", Motto = "per chi non torna indietro", Prestige = 1.1f, Bias = new[] { "arrembaggio", "TurretDamageFactor", "burst_damage" } },
            new() { Name = "Fluffy Biscuit Co.", Code = "FB", Motto = "gusto e sicurezza", Prestige = 0.8f, Bias = new[] { "viveri", "morale", "OxygenRefillRate" } },
            new() { Name = "Polytechnic Labs", Code = "PL", Motto = "il futuro è già qui", Prestige = 1.25f, Bias = new[] { "ShieldsMax", "ShieldsRecoveryRate", "CyberDefenseRating" } },
        };
        public static readonly string[] Nicks =
        {
            "Fenice", "Aquila", "Vespa", "Orione", "Tempesta", "Cometa", "Falco", "Sirio", "Zefiro", "Nembo", "Lince", "Vega", "Altair", "Pegaso", "Drago",
            "Maestrale", "Scirocco", "Aurora", "Nova", "Quasar", "Pulsar", "Ermes", "Atlante", "Titano", "Medusa", "Chimera", "Grifone", "Corsaro", "Sentinella", "Ombra",
            "Saetta", "Folgore", "Ardito", "Vigile", "Tenace", "Audace", "Lampo", "Bora", "Libeccio", "Astore", "Nibbio", "Gheppio", "Sparviero", "Rondine", "Gabbiano",
        };

        public static LegendaryDef[] DefaultLegendaries() => new LegendaryDef[]
        {
            // leggendari della galassia viva
            new() { Name = "Bilancia di Navarro", Slots = new[] { 7 }, Lore = "Il computer di bordo del più grande mercante della Frontiera. Conta ancora in tre valute morte.", Effects = new[] { EK("commercio", 0.15f), EK("stiva", 6f), EK("dogane", 0.3f) } },
            new() { Name = "Cuore di Barros", Slots = new[] { 5 }, Lore = "Sensori di un recuperatore leggendario: vedono il metallo buono anche dentro un relitto in fiamme.", Effects = new[] { EK("recupero", 0.4f), EK("sonde", 0.4f) } },
            new() { Name = "Mantello del Fantasma", Slots = new[] { 33 }, Lore = "Nessuna dogana l'ha mai trovato. Nessun pirata l'ha mai visto arrivare.", Effects = new[] { EK("furtivita", 0.25f), EK("dogane", 0.45f), E("EMSignature", -0.25f) } },
            new() { Name = "Uncino di Morgan", Slots = new[] { 10, 11 }, Lore = "Il cannone dell'arrembaggio più famoso della storia. Le sue tacche non si contano più.", Effects = new[] { EK("arrembaggio", 0.6f), E("TurretDamageFactor", 0.12f, "corta") } },
            new() { Name = "Serra di Ceres", Slots = new[] { 8 }, Lore = "Un giardino idroponico che ha sfamato una colonia intera durante l'assedio di Ceres.", Effects = new[] { EK("viveri", 0.35f), EK("morale", 0.2f), E("OxygenRefillRate", 0.3f) } },
            new() { Name = "Occhio di Ilves", Slots = new[] { 32, 5 }, Lore = "Ha visto nascere tre stelle e morire una. L'Accademia lo rivorrebbe indietro.", Effects = new[] { EK("ricerca", 0.5f), EK("scan_speed", 0.2f) } },
            new() { Name = "Corona dei Moreau", Slots = new[] { 6 }, Lore = "Lo scafo di un'ammiraglia da parata. Nei porti, al suo arrivo, suonano ancora le campane.", Effects = new[] { EK("fama", 0.4f), EK("morale", 0.15f), E("HullMax", 0.1f) } },
            new() { Name = "Paratia di Okafor", Slots = new[] { 16 }, Lore = "Nessun incursore è mai passato oltre questa paratia. Qualcuno ci ha provato con un cannone.", Effects = new[] { EK("sentinella", 0.5f), E("HullArmor", 0.12f) } },
            new() { Name = "Cuore di Vega", Set = "Fucina di Vulcano", Slots = new[] { 3 }, Lore = "Il reattore della prima nave a superare il Margine. Non si è mai spento.", Effects = new[] { E("ReactorOutputMax", 0.25f), EK("core_cooling", 0.35f) } },
            new() { Name = "Fornace di Prometeo", Slots = new[] { 3 }, Lore = "Ruba fuoco a una stella e lo restituisce in battaglia. Scotta.", Effects = new[] { E("ReactorOutputMax", 0.35f), E("TurretDamageFactor", 0.12f, "crisi"), new AffixDef { Label = "Temperatura massima del reattore", Stat = "ReactorTempMax", Min = -0.10f, Max = -0.10f, Drawback = true } } },
            new() { Name = "Stella Nana", Set = "Corredo dell'Ammiraglia", Slots = new[] { 3 }, Lore = "Compatta, densa, instancabile: il cuore delle ammiraglie della Flotta.", Effects = new[] { E("ReactorOutputMax", 0.18f), EK("core_load", 0.30f), E("StartupSpeed", 0.5f) } },
            new() { Name = "Egida di Aldebaran", Set = "Corredo dell'Ammiraglia", Slots = new[] { 1 }, Lore = "Ha retto tre giorni di assedio. Porta ancora i segni di ogni colpo.", Effects = new[] { E("ShieldsMax", 0.30f), E("ShieldsDeflection", 0.15f) } },
            new() { Name = "Velo di Lume", Set = "Eredità di Lume", Slots = new[] { 1 }, Lore = "Una frequenza che nessun nemico ha mai imparato.", Effects = new[] { E("ShieldsChargeRateMax", 0.35f), EK("harmonic_decay", 0.50f) } },
            new() { Name = "Ultimo Baluardo", Slots = new[] { 1 }, Lore = "Dà il meglio quando tutto il resto ha ceduto.", Effects = new[] { E("ShieldsChargeRateMax", 0.60f, "scafo"), E("HullArmor", 0.20f, "scudi") } },
            new() { Name = "Chiglia dell'Indomita", Set = "Corredo dell'Ammiraglia", Slots = new[] { 6 }, Lore = "Recuperata dal relitto dell'Indomita, intatta.", Effects = new[] { E("HullMax", 0.30f), EK("incoming", 0.10f) } },
            new() { Name = "Pelle di Drago", Slots = new[] { 6, 16 }, Lore = "Pesante come una montagna, dura come la sua leggenda.", Effects = new[] { E("HullArmor", 0.30f), EK("sys_resist", 0.35f), new AffixDef { Label = "Massa della nave", Stat = "Mass", Min = 0.08f, Max = 0.08f, Drawback = true } } },
            new() { Name = "Ala di Hermes", Set = "Rotta dei Navigatori", Slots = new[] { 2 }, Lore = "Il messaggero arriva sempre per primo.", Effects = new[] { E("WarpChargeRate", 0.40f), E("WarpRange", 0.25f) } },
            new() { Name = "Passo dell'Abisso", Slots = new[] { 2 }, Lore = "Salti che nessun altro oserebbe. Lascia una scia che tutti vedono.", Effects = new[] { E("WarpRange", 0.45f), E("ThrustOutputMax", 0.30f, "salto"), new AffixDef { Label = "Firma EM", Stat = "EMSignature", Min = 0.15f, Max = 0.15f, Drawback = true } } },
            new() { Name = "Corsa di Orione", Set = "Rotta dei Navigatori", Slots = new[] { 9 }, Lore = "Il cacciatore non si ferma mai.", Effects = new[] { E("ThrustOutputMax", 0.30f), EK("evade_cooldown", 0.35f) } },
            new() { Name = "Danza delle Comete", Set = "Rotta dei Navigatori", Slots = new[] { 25, 26 }, Lore = "Ogni manovra è una figura di ballo.", Effects = new[] { E("ManeuverThrustOutputMax", 0.35f), E("InertiaThrustOutputMax", 0.25f), EK("engage_gain", 0.50f) } },
            new() { Name = "Lama di Kessar", Set = "Fucina di Vulcano", Slots = new[] { 10 }, Lore = "Taglia i sistemi di una nave come la seta.", Effects = new[] { E("TurretDamageFactor", 0.20f), EK("subsystem", 0.40f) } },
            new() { Name = "Furia Rossa", Slots = new[] { 10, 24 }, Lore = "Si sveglia solo quando suona l'allarme.", Effects = new[] { E("TurretDamageFactor", 0.30f, "rosso"), E("TurretCoolingSpeedFactor", 0.25f) } },
            new() { Name = "Sentenza", Set = "Fucina di Vulcano", Slots = new[] { 10, 11 }, Lore = "Quando parla, la discussione è chiusa.", Effects = new[] { EK("burst_damage", 0.50f), EK("overload_margin", 10f) } },
            new() { Name = "Martello di Tycho", Set = "Corredo dell'Ammiraglia", Slots = new[] { 11 }, Lore = "Il cannone principale della corazzata Tycho.", Effects = new[] { E("TurretDamageFactor", 0.25f), E("TurretChargeFactor", 0.15f) } },
            new() { Name = "Occhio del Ciclope", Slots = new[] { 11 }, Lore = "Vede il punto debole prima ancora di sparare.", Effects = new[] { EK("weakpoint", 0.12f), E("TurretDamageFactor", 0.15f, "lunga") } },
            new() { Name = "Sguardo di Argo", Set = "Eredità di Lume", Slots = new[] { 5, 32 }, Lore = "Cento occhi, nessuno chiuso.", Effects = new[] { E("EMDetection", 0.50f), EK("scan_speed", 0.60f) } },
            new() { Name = "Orecchio del Vuoto", Slots = new[] { 5, 32 }, Lore = "Ascolta ciò che il vuoto sussurra.", Effects = new[] { E("QTDetection", 0.40f), E("LFDetection", 0.40f), E("RADetection", 0.40f), EK("weakpoint", 0.06f) } },
            new() { Name = "Mente di Dedalo", Set = "Eredità di Lume", Slots = new[] { 7 }, Lore = "Il computer che progettava le proprie difese.", Effects = new[] { E("CyberDefenseRating", 0.50f), EK("initiative", 0.50f) } },
            new() { Name = "Spettro", Slots = new[] { 7 }, Lore = "Entra nei sistemi nemici e non lascia traccia.", Effects = new[] { E("CyberAttackRating", 0.50f), E("EMSignature", -0.30f, "silenzio") } },
            new() { Name = "Respiro di Gaia", Set = "Eredità di Lume", Slots = new[] { 8 }, Lore = "Ogni nave che lo porta profuma di pioggia.", Effects = new[] { E("OxygenRefillRate", 0.80f), EK("repair_speed", 0.40f) } },
            new() { Name = "Sentinella Eterna", Set = "Fucina di Vulcano", Slots = new[] { 24 }, Lore = "Non dorme, non si stanca, non manca.", Effects = new[] { E("TurretChargeFactor", 0.30f), E("TurretCoolingSpeedFactor", 0.30f) } },
            new() { Name = "Ombra di Teth", Slots = new[] { 33 }, Lore = "Sui sensori nemici, solo silenzio.", Effects = new[] { E("EMSignature", -0.45f), E("LFSignature", -0.30f), E("RASignature", -0.30f) } },
        };

        public static SetDef[] DefaultSets() => new SetDef[]
        {
            new() { Name = "Corredo dell'Ammiraglia", Bonus2 = new[] { E("ShieldsMax", 0.10f) }, Bonus3 = new[] { E("TurretDamageFactor", 0.10f) }, Bonus4 = new[] { EK("initiative", 0.60f), EK("incoming", 0.08f) } },
            new() { Name = "Rotta dei Navigatori", Bonus2 = new[] { E("WarpRange", 0.15f) }, Bonus3 = new[] { EK("evade_cooldown", 0.30f), E("ThrustOutputMax", 0.10f) } },
            new() { Name = "Eredità di Lume", Bonus2 = new[] { EK("scan_speed", 0.30f) }, Bonus3 = new[] { EK("harmonic_decay", 0.30f) }, Bonus4 = new[] { EK("weakpoint", 0.10f), E("CyberDefenseRating", 0.30f) } },
            new() { Name = "Fucina di Vulcano", Bonus2 = new[] { EK("core_cooling", 0.25f) }, Bonus3 = new[] { EK("overload_margin", 6f) }, Bonus4 = new[] { EK("burst_damage", 0.30f), E("TurretDamageFactor", 0.08f) } },
        };

        // ---- descrizione
        static string Value(AffixDef a, float v)
        {
            if (a.Mode == "add") return a.Key == "overload_margin" ? $"{v:+0;-0} punti" : $"{v:+0.#;-0.#}";
            // per i bonus di stazione il segno dice se aiuta; per le statistiche è la variazione
            return $"{v * 100:+0;-0}%";
        }
        static string Line(AffixDef a, float v)
        {
            string cond = a.Cond.Length > 0 && CondLabels.TryGetValue(a.Cond, out var c) ? $" ({c})" : "";
            string what = a.Label.Length > 0 ? a.Label : (a.Key.Length > 0 ? KeyLabels[a.Key] : StatLabel(a.Stat));
            return (a.Drawback ? "<color=#FF8A7A>Difetto: " : "") + $"{what} {Value(a, v)}{cond}" + (a.Name.Length > 0 ? $" — {a.Name}" : "") + (a.Drawback ? "</color>" : "");
        }

        public static string Describe(RarityTag tag)
        {
            var sb = new StringBuilder();
            sb.Append(Rarity.Colored(tag.Tier, Rarity.Tier(tag.Tier).Name));
            if (tag.Bonus > 0) sb.Append($" (+{tag.Bonus} liv.)");
            if (tag.Maker >= 0 && tag.Maker < Makers.Length) { var m = Makers[tag.Maker]; sb.Append($"\n{m.Name}, «{tag.Nick}» serie {m.Code}-{tag.Serial:0000} · qualità {tag.Quality:0}% · <i>{m.Motto}</i>"); }
            if (tag.Legend >= 0)
            {
                var l = Rarity.Cfg.Legendaries[tag.Legend];
                sb.Append($"\n<i>{l.Lore}</i>");
                for (int i = 0; i < tag.Defs.Length; i++) sb.Append("\n").Append(Line(tag.Defs[i], tag.Value[i]));
                if (l.Set.Length > 0)
                {
                    var set = Rarity.Cfg.Sets.FirstOrDefault(x => x.Name == l.Set);
                    sb.Append($"\n\n{Rarity.Colored(tag.Tier, "Set: " + l.Set)}");
                    if (set != null)
                        foreach (var (n, list) in new[] { (2, set.Bonus2), (3, set.Bonus3), (4, set.Bonus4) })
                            if (list.Length > 0) sb.Append($"\n{n} pezzi: ").Append(string.Join(", ", list.Select(x => Line(x, x.Min))));
                }
                return sb.ToString();
            }
            for (int i = 0; i < tag.Defs.Length; i++) sb.Append("\n").Append(Line(tag.Defs[i], tag.Value[i]));
            return sb.ToString();
        }

        // ---- applicazione alla nave: statistiche, condizioni, bonus di stazione, set
        static readonly Dictionary<int, Dictionary<string, float>> bonus = new();
        static readonly Dictionary<int, float> warpEnd = new();
        public static float Bonus(int ship, string key) => bonus.TryGetValue(ship, out var d) && d.TryGetValue(key, out var v) ? v : 0f;
        public static float Bonus(PLShipInfoBase ship, string key) => ship == null ? 0f : Bonus(ship.ShipID, key);
        public static float Mine(string key) { try { return Bonus(Stations.MyShip, key); } catch { return 0f; } }

        static bool CondOk(string cond, PLShipStats s, PLShipInfoBase ship)
        {
            if (cond.Length == 0) return true;
            int id = ship.ShipID; float G(string k) => Stations.Get(id, k);
            switch (cond)
            {
                case "rosso": try { return ship.m_AlertLevel >= 2; } catch { return false; }
                case "scafo": return s.HullMax > 1 && s.HullCurrent / s.HullMax < 0.35f;
                case "scudi": return s.ShieldsMax > 1 && s.ShieldsCurrent / s.ShieldsMax < 0.10f;
                case "corta": return (int)G("plx_band") == 1 && G("plx_pos") >= 50f;
                case "lunga": return (int)G("plx_band") == 3 && G("plx_pos") >= 50f;
                case "salto": return warpEnd.TryGetValue(id, out var t) && Time.time - t < 30f;
                case "crisi": return (int)G("drx_phase") == 2;
                case "silenzio": return (int)G("cpx_dir") == 4;
                case "nucleo": return G("core_stress") > 60f;
            }
            return false;
        }

        static void Use(PLShipStats s, PLShipInfoBase ship, Dictionary<string, float> d, AffixDef a, float v)
        {
            if (!CondOk(a.Cond, s, ship)) return;
            if (a.Key.Length > 0) d[a.Key] = (d.TryGetValue(a.Key, out var cur) ? cur : 0f) + v;
            else Rarity.ApplyStat(s, a, v);
        }

        public static void ApplyShip(PLShipStats s)
        {
            if (Rarity.TagCount == 0) return;
            var ship = s.Ship; if (ship == null) return;
            var list = s.m_AllComponents; if (list == null) return;
            var d = new Dictionary<string, float>(); var sets = new Dictionary<string, int>();
            for (int i = 0, n = list.Count; i < n; i++)
            {
                var c = list[i]; if (c == null || !c.IsEquipped) continue;
                var tag = Rarity.Get(c.Pointer); if (tag == null) continue;
                for (int k = 0; k < tag.Defs.Length; k++) Use(s, ship, d, tag.Defs[k], tag.Value[k]);
                if (tag.Legend >= 0) { string set = Rarity.Cfg.Legendaries[tag.Legend].Set; if (set.Length > 0) sets[set] = (sets.TryGetValue(set, out var m) ? m : 0) + 1; }
            }
            foreach (var (name, count) in sets)
            {
                var set = Rarity.Cfg.Sets.FirstOrDefault(x => x.Name == name); if (set == null) continue;
                foreach (var (need, l) in new[] { (2, set.Bonus2), (3, set.Bonus3), (4, set.Bonus4) })
                    if (count >= need) foreach (var a in l) Use(s, ship, d, a, a.Min);
            }
            bonus[ship.ShipID] = d;
        }

        // ---- ritrovamenti: ogni macchina annuncia i pezzi nuovi della propria nave
        static readonly HashSet<IntPtr> seen = new(); static bool primed; static int seenShip = -1; static float nextScan; static bool wasWarp;
        public static void Tick()
        {
            if (Time.time < nextScan) return; nextScan = Time.time + 1.5f;
            try
            {
                var ship = Stations.MyShip; if (ship == null || ship.MyStats == null) { primed = false; return; }
                bool warp = false; try { warp = ship.InWarp; } catch { }
                if (wasWarp && !warp) warpEnd[ship.ShipID] = Time.time; wasWarp = warp;
                if (seenShip != ship.ShipID) { seen.Clear(); primed = false; seenShip = ship.ShipID; }
                var list = ship.MyStats.m_AllComponents; if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i]; if (c == null || !seen.Add(c.Pointer) || !primed) continue;
                    var tag = Rarity.Get(c.Pointer); if (tag == null || tag.Tier < Rarity.Cfg.AnnounceFrom) continue;
                    var t = Rarity.Tier(tag.Tier);
                    Stations.ShowFound(t.Name.ToUpperInvariant(), c.Name, t.Color);
                    if (PhotonNetwork.isMasterClient) Stations.LogEvent(ship.ShipID, $"Ritrovamento {t.Name.ToLowerInvariant()}: {c.Name}");
                    Service.Add("bottino.grado." + tag.Tier, 1); if (tag.Legend >= 0) Service.Add("bottino.leggendari", 1);
                }
                primed = true; // al primo giro si prende nota di ciò che c'è già, senza annunciarlo
            }
            catch (Exception e) { Plugin.L.LogWarning("Ritrovamenti: " + e.Message); nextScan = Time.time + 10f; }
        }

        // ---- doni di bottino (solo host): un componente nuovo nella stiva, di grado compreso fra min e max
        static readonly (int type, int subs)[] Pool = { (1, 10), (2, 4), (3, 7), (6, 4), (9, 5), (10, 5), (26, 3), (8, 2) };
        static readonly System.Random rng = new();
        public static PLShipComponent Grant(PLShipInfo ship, int minTier, int maxTier, string why)
        {
            if (!PhotonNetwork.isMasterClient || ship?.MyStats == null) return null;
            int tier = Math.Clamp(minTier + (maxTier > minTier ? Rarity.Roll(rng, 1.6) % (maxTier - minTier + 1) : 0), 0, Rarity.Cfg.Tiers.Length - 1);
            var (type, subs) = Pool[rng.Next(Pool.Length)];
            int level = 0; try { level = Math.Clamp(PLServer.Instance.CurrentCrewLevel / 2, 0, 8); } catch { }
            uint h = (uint)(type & 0x3F) | ((uint)(rng.Next(subs) & 0x3F) << 6) | ((uint)(level & 0x1F) << 12) | ((uint)tier << 29);
            var comp = PLShipComponent.CreateShipComponentFromHash((int)h, null); if (comp == null) return null;
            ship.MyStats.AddShipComponent(comp, -1, ESlotType.E_COMP_CARGO);
            Plugin.L.LogInfo($"[bottino] dono ({why}): {comp.Name}");
            Service.Add("bottino.doni", 1);
            return comp;
        }

        // ---- Officina dell'Ingegneria: smontare o potenziare un pezzo della stiva (eseguito dall'host)
        // valori fissi per grado (più un poco per livello): il prezzo di mercato, gonfiato da grado e livelli, non fa testo
        static readonly int[] SalvageByTier = { 150, 300, 600, 1000, 1600, 2500, 4000, 6000 }, UpgradeByTier = { 400, 900, 1600, 2600, 4000, 6000 };
        static int BaseLevel(PLShipComponent c) { try { return Rarity.LevelOf(c.getHash()); } catch { return 0; } }
        public static int SalvageValue(PLShipComponent c) { int t = Rarity.Get(c.Pointer)?.Tier ?? 0; return SalvageByTier[Math.Clamp(t, 0, 7)] + BaseLevel(c) * 60; }
        public static int UpgradeCost(PLShipComponent c, int tier) => UpgradeByTier[Math.Clamp(tier, 0, UpgradeByTier.Length - 1)] + BaseLevel(c) * 120;
        public static float UpgradeChance(int tier) => Math.Clamp(0.92f - tier * 0.13f, 0.15f, 0.92f);

        // Richiesta dal pannello: l'host esegue subito, un client la inoltra all'host.
        public static void Request(string op, int netId)
        {
            var ship = Stations.MyShip; if (ship == null) return;
            if (PhotonNetwork.isMasterClient) { Execute(ship, op, netId); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|officina|{op}|{ship.ShipID}|{netId}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); }
            catch (Exception e) { Plugin.L.LogWarning("Officina: richiesta non inviata: " + e.Message); }
        }

        public static void OnRequest(string msg)
        {
            var p = msg.Split('|'); if (p.Length < 5 || !PhotonNetwork.isMasterClient) return;
            if (!int.TryParse(p[3], out int shipId) || !int.TryParse(p[4], out int netId)) return;
            var ship = PLEncounterManager.Instance?.GetShipFromID(shipId)?.TryCast<PLShipInfo>(); if (ship != null) Execute(ship, p[2], netId);
        }

        static void Execute(PLShipInfo ship, string op, int netId)
        {
            var st = ship.MyStats; var c = st?.GetComponentFromNetID(netId); if (c == null) { Stations.Announce("OFFICINA", "Il pezzo non è più in stiva."); return; }
            if (!c.InCargoSlot()) { Stations.Announce("OFFICINA", "Si lavora solo sui pezzi in stiva: smontalo prima dal suo alloggiamento."); return; }
            uint h = c.getHash(); int tier = Rarity.TierOf(h); string name = c.Name;
            if (op == "smonta")
            {
                int v = SalvageValue(c); st.RemoveShipComponent(c);
                try { PLServer.Instance.CurrentCrewCredits += v; } catch { }
                Stations.Announce("OFFICINA", $"{name} smontato: {v} crediti recuperati."); Stations.LogEvent(ship.ShipID, $"Officina: smontato {name} (+{v} crediti)");
                Service.Add("officina.smontaggi", 1); return;
            }
            if (op == "potenzia")
            {
                if (tier >= Rarity.Cfg.Tiers.Length - 2) { Stations.Announce("OFFICINA", "Oltre questo grado si arriva solo trovando pezzi leggendari."); return; }
                int cost = UpgradeCost(c, tier); int credits = 0; try { credits = PLServer.Instance.CurrentCrewCredits; } catch { }
                if (credits < cost) { Stations.Announce("OFFICINA", $"Servono {cost} crediti per tentare il potenziamento."); return; }
                try { PLServer.Instance.CurrentCrewCredits = credits - cost; } catch { }
                if (rng.NextDouble() < UpgradeChance(tier))
                {
                    var n = PLShipComponent.CreateShipComponentFromHash((int)((h & Rarity.BodyMask) | ((uint)(tier + 1) << 29)), null);
                    if (n == null) return;
                    st.RemoveShipComponent(c); st.AddShipComponent(n, -1, ESlotType.E_COMP_CARGO);
                    Stations.Announce("OFFICINA", $"Potenziamento riuscito: {n.Name}."); Stations.LogEvent(ship.ShipID, $"Officina: {n.Name}");
                    Service.Add("officina.potenziamenti", 1);
                }
                else if (tier >= 3)
                {
                    st.RemoveShipComponent(c);
                    Stations.Announce("OFFICINA", $"Potenziamento fallito: {name} è andato distrutto."); Stations.LogEvent(ship.ShipID, $"Officina: {name} distrutto nel potenziamento");
                    Service.Add("officina.fallimenti", 1);
                }
                else { Stations.Announce("OFFICINA", $"Potenziamento fallito: {name} è intatto, ma i materiali sono persi."); Service.Add("officina.fallimenti", 1); }
            }
        }
    }
}

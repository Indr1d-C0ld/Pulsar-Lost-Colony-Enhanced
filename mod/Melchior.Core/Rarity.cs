// Sistema di rarità dei componenti di bordo.
//
// L'hash di un componente (PLShipComponent.getHash) usa 29 bit: tipo 0-5, sottotipo 6-11, livello 12-16,
// dato extra 17-22, slot visivo 23-28. I bit 29-31 sono liberi: lì viaggia il grado di rarità (0-7), quindi
// rete e salvataggi lo trasportano senza alcuna modifica al protocollo. Senza la mod quei bit vengono ignorati.
//
// Un grado vale livelli nascosti in più (il gioco legge il livello ovunque: statistiche, danni, prezzo) e,
// dai gradi alti, uno o più affissi che ritoccano le statistiche della nave. Gli affissi dipendono solo
// dall'identità dell'oggetto, quindi ogni client li ricalcola identici.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using System.Text;
using System.Text.Json;
using CodeStage.AntiCheat.ObscuredTypes;
using HarmonyLib;

namespace Melchior
{
    public class TierDef
    {
        public string Name = "";
        public string Color = "#FFFFFF";
        public double Weight = 1;       // peso di estrazione
        public int LevelBonus;          // livelli nascosti aggiunti
        public double PriceMult = 1;    // moltiplicatore del prezzo, oltre all'effetto dei livelli
        public int Affixes;             // quanti affissi riceve
    }

    public class AffixDef
    {
        public string Name = "";
        public string Label = "";       // testo mostrato nella descrizione
        public string Stat = "";        // proprietà float di PLShipStats
        public string Mode = "mul";     // "mul": percentuale sul valore della nave; "add": valore fisso
        public float Min, Max;
        public int[] Slots = Array.Empty<int>(); // tipi di componente ammessi (ESlotType); vuoto = tutti
    }

    public class RarityConfig
    {
        public bool Enabled = true;
        public bool ColorNames = true;      // colora il grado nelle descrizioni (i titoli restano testo semplice)
        public string NameFormat = "{name} ({tier})";
        public double Luck = 1.0;          // fortuna di base: >1 sposta le estrazioni verso i gradi alti
        public double LuckPerLevel = 0.04; // fortuna aggiunta per ogni livello base dell'oggetto
        public double LuckPerChaos = 0.05; // fortuna aggiunta per ogni punto di Caos della galassia
        public int[] Slots = { 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 16, 19, 20, 24, 25, 26, 28, 32, 33, 34 };
        public TierDef[] Tiers =
        {
            new() { Name = "Standard",     Color = "#FFFFFF", Weight = 620, LevelBonus = 0, PriceMult = 1.0, Affixes = 0 },
            new() { Name = "Raffinato",    Color = "#7CD992", Weight = 200, LevelBonus = 0, PriceMult = 1.1, Affixes = 1 },
            new() { Name = "Avanzato",     Color = "#6FA8FF", Weight = 100, LevelBonus = 1, PriceMult = 1.15, Affixes = 1 },
            new() { Name = "Sperimentale", Color = "#C58AF9", Weight = 45,  LevelBonus = 1, PriceMult = 1.3, Affixes = 2 },
            new() { Name = "Prototipo",    Color = "#F2A65A", Weight = 20,  LevelBonus = 2, PriceMult = 1.4, Affixes = 2 },
            new() { Name = "Reliquia",     Color = "#FFD75E", Weight = 10,  LevelBonus = 2, PriceMult = 1.6, Affixes = 3 },
            new() { Name = "Anomalo",      Color = "#5EF0E0", Weight = 4,   LevelBonus = 3, PriceMult = 1.8, Affixes = 3 },
            new() { Name = "Singolare",    Color = "#FF5E7A", Weight = 1,   LevelBonus = 4, PriceMult = 2.2, Affixes = 4 },
        };
        // Slots indica i tipi di componente (ESlotType) che possono avere l'affisso; vuoto = riserva universale,
        // usata solo quando quelli specifici non bastano.
        public AffixDef[] Affixes =
        {
            new() { Name = "a matrice di dilitio",        Label = "Potenza del reattore",         Stat = "ReactorOutputMax",         Min = 0.06f, Max = 0.15f, Slots = new[] { 3 } },
            new() { Name = "a raffreddamento criogenico", Label = "Temperatura massima reattore", Stat = "ReactorTempMax",           Min = 0.05f, Max = 0.12f, Slots = new[] { 3 } },
            new() { Name = "a emettitori multifasici",    Label = "Scudi massimi",                Stat = "ShieldsMax",               Min = 0.06f, Max = 0.15f, Slots = new[] { 1 } },
            new() { Name = "rigenerativo",                Label = "Ricarica degli scudi",         Stat = "ShieldsChargeRateMax",     Min = 0.08f, Max = 0.20f, Slots = new[] { 1 } },
            new() { Name = "in duranio rinforzato",       Label = "Integrità dello scafo",        Stat = "HullMax",                  Min = 0.06f, Max = 0.15f, Slots = new[] { 6, 16 } },
            new() { Name = "ad armatura ablativa",        Label = "Corazza dello scafo",          Stat = "HullArmor",                Min = 0.05f, Max = 0.12f, Slots = new[] { 6, 16 } },
            new() { Name = "a bobine ricalibrate",        Label = "Carica della curvatura",       Stat = "WarpChargeRate",           Min = 0.10f, Max = 0.25f, Slots = new[] { 2 } },
            new() { Name = "a geometria variabile",       Label = "Raggio di curvatura",          Stat = "WarpRange",                Min = 0.08f, Max = 0.20f, Slots = new[] { 2 } },
            new() { Name = "a impulso potenziato",        Label = "Spinta principale",            Stat = "ThrustOutputMax",          Min = 0.06f, Max = 0.15f, Slots = new[] { 9 } },
            new() { Name = "con smorzatori inerziali",    Label = "Spinta inerziale",             Stat = "InertiaThrustOutputMax",   Min = 0.08f, Max = 0.18f, Slots = new[] { 25, 9 } },
            new() { Name = "a manovra fine",              Label = "Spinta di manovra",            Stat = "ManeuverThrustOutputMax",  Min = 0.08f, Max = 0.18f, Slots = new[] { 26, 9 } },
            new() { Name = "a banchi sincronizzati",      Label = "Danno delle torrette",         Stat = "TurretDamageFactor",       Min = 0.04f, Max = 0.10f, Slots = new[] { 10, 11, 24 } },
            new() { Name = "a condensatori rapidi",       Label = "Carica delle torrette",        Stat = "TurretChargeFactor",       Min = 0.05f, Max = 0.12f, Slots = new[] { 10, 11, 24, 7 } },
            new() { Name = "a dissipatori attivi",        Label = "Raffreddamento torrette",      Stat = "TurretCoolingSpeedFactor", Min = 0.08f, Max = 0.20f, Slots = new[] { 10, 11, 24 } },
            new() { Name = "con sensori laterali",        Label = "Rilevamento EM",               Stat = "EMDetection",              Min = 0.10f, Max = 0.25f, Slots = new[] { 5, 32 } },
            new() { Name = "a firewall adattivo",         Label = "Difesa informatica",           Stat = "CyberDefenseRating",       Min = 0.10f, Max = 0.25f, Slots = new[] { 7 } },
            new() { Name = "a riciclo atmosferico",       Label = "Rigenerazione ossigeno",       Stat = "OxygenRefillRate",         Min = 0.15f, Max = 0.35f, Slots = new[] { 8 } },
            new() { Name = "a campo di occultamento",     Label = "Firma EM",                     Stat = "EMSignature",              Min = -0.30f, Max = -0.12f, Slots = new[] { 33 } },
            new() { Name = "schermato",                   Label = "Firma EM",                     Stat = "EMSignature",              Min = -0.12f, Max = -0.04f },
            new() { Name = "a paratie rinforzate",        Label = "Integrità dello scafo",        Stat = "HullMax",                  Min = 0.02f, Max = 0.06f },
            new() { Name = "con scudo ausiliario",        Label = "Scudi massimi",                Stat = "ShieldsMax",               Min = 0.02f, Max = 0.06f },
            new() { Name = "a condotti EPS ottimizzati",  Label = "Potenza del reattore",         Stat = "ReactorOutputMax",         Min = 0.02f, Max = 0.05f },
            new() { Name = "a subroutine difensive",      Label = "Difesa informatica",           Stat = "CyberDefenseRating",       Min = 0.04f, Max = 0.10f },
        };
    }

    // Etichetta associata a un componente vivo.
    public sealed class RarityTag
    {
        public int Tier, Bonus, Slot;
        public int[] Affix = Array.Empty<int>();
        public float[] Value = Array.Empty<float>();
    }

    public static class Rarity
    {
        public static RarityConfig Cfg = new();
        public static long CalcCalls, CtorCalls;
        static readonly Dictionary<IntPtr, RarityTag> tags = new();
        static readonly Dictionary<string, (Func<PLShipStats, float> get, Action<PLShipStats, float> set)> stats = new();
        static readonly JsonSerializerOptions json = new()
        {
            IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public const uint BodyMask = 0x1FFFFFFF;
        public static int TierOf(uint hash) => (int)(hash >> 29);
        public static int LevelOf(uint hash) => (int)(hash >> 12) & 0x1F;
        public static uint WithLevel(uint hash, int level) => (hash & ~(0x1Fu << 12)) | ((uint)(level & 0x1F) << 12);
        public static int TagCount => tags.Count;

        public static void Load(string dir)
        {
            string path = Path.Combine(dir, "melchior.rarity.json");
            try
            {
                if (File.Exists(path)) Cfg = JsonSerializer.Deserialize<RarityConfig>(File.ReadAllText(path), json) ?? new RarityConfig();
                else File.WriteAllText(path, JsonSerializer.Serialize(Cfg, json));
            }
            catch (Exception e) { Plugin.L.LogError($"melchior.rarity.json non valido, uso i valori predefiniti: {e.Message}"); Cfg = new RarityConfig(); }
            if (Cfg.Tiers.Length == 0 || Cfg.Tiers.Length > 8) { Plugin.L.LogError("Servono da 1 a 8 gradi di rarità: uso i predefiniti."); Cfg = new RarityConfig(); }
            stats.Clear();
            foreach (var a in Cfg.Affixes)
            {
                if (stats.ContainsKey(a.Stat)) continue;
                var acc = Accessor(a.Stat);
                if (acc.get == null) Plugin.L.LogWarning($"Affisso '{a.Name}': statistica '{a.Stat}' sconosciuta, ignorato.");
                else stats[a.Stat] = acc;
            }
            Plugin.L.LogInfo($"Rarità: {Cfg.Tiers.Length} gradi, {stats.Count} statistiche per {Cfg.Affixes.Length} affissi ({(Cfg.Enabled ? "attiva" : "disattivata")}).");
        }

        static readonly Dictionary<string, (Func<PLShipStats, float> get, Action<PLShipStats, float> set)> accessors = new();

        // Lettura e scrittura di una statistica della nave per nome (proprietà float o ObscuredFloat di PLShipStats).
        public static (Func<PLShipStats, float> get, Action<PLShipStats, float> set) Accessor(string stat)
        {
            if (accessors.TryGetValue(stat, out var acc)) return acc;
            var p = typeof(PLShipStats).GetProperty(stat, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanRead && p.CanWrite && p.PropertyType == typeof(float))
                acc = ((Func<PLShipStats, float>)Delegate.CreateDelegate(typeof(Func<PLShipStats, float>), p.GetMethod),
                       (Action<PLShipStats, float>)Delegate.CreateDelegate(typeof(Action<PLShipStats, float>), p.SetMethod));
            else if (p != null && p.CanRead && p.CanWrite && p.PropertyType == typeof(ObscuredFloat))
            {
                var g = (Func<PLShipStats, ObscuredFloat>)Delegate.CreateDelegate(typeof(Func<PLShipStats, ObscuredFloat>), p.GetMethod);
                var st = (Action<PLShipStats, ObscuredFloat>)Delegate.CreateDelegate(typeof(Action<PLShipStats, ObscuredFloat>), p.SetMethod);
                acc = (x => g(x), (x, v) => st(x, v));
            }
            return accessors[stat] = acc;
        }

        public static RarityTag Get(IntPtr p) { lock (tags) return tags.TryGetValue(p, out var t) ? t : null; }
        public static void Forget(IntPtr p) { if (tags.Count != 0) lock (tags) tags.Remove(p); }
        public static TierDef Tier(int i) => Cfg.Tiers[Math.Clamp(i, 0, Cfg.Tiers.Length - 1)];
        public static bool SlotAllowed(int slot) => Array.IndexOf(Cfg.Slots, slot) >= 0;

        // Affissi: scelti in modo deterministico dall'identità dell'oggetto (hash senza livello nascosto).
        public static RarityTag MakeTag(uint identity, int tier, int bonus)
        {
            int slot = (int)(identity & 0x3F);
            var tag = new RarityTag { Tier = tier, Bonus = bonus, Slot = slot };
            int want = Tier(tier).Affixes;
            if (want <= 0) return tag;
            bool Usable(int i) => stats.ContainsKey(Cfg.Affixes[i].Stat);
            var own = Enumerable.Range(0, Cfg.Affixes.Length).Where(i => Usable(i) && Array.IndexOf(Cfg.Affixes[i].Slots, slot) >= 0).ToList();
            var any = Enumerable.Range(0, Cfg.Affixes.Length).Where(i => Usable(i) && Cfg.Affixes[i].Slots.Length == 0).ToList();
            uint s = Mix((identity & BodyMask) | ((uint)tier << 29));
            var ids = new List<int>(); var vals = new List<float>(); var used = new HashSet<string>();
            while (ids.Count < want && own.Count + any.Count > 0)
            {
                var pool = own.Count > 0 ? own : any;
                s = Mix(s); int k = (int)(s % (uint)pool.Count); int id = pool[k]; pool.RemoveAt(k);
                var a = Cfg.Affixes[id];
                if (!used.Add(a.Stat)) continue; // una sola modifica per statistica
                s = Mix(s); float t = (s & 0xFFFF) / 65535f;
                ids.Add(id); vals.Add(a.Min + (a.Max - a.Min) * t);
            }
            tag.Affix = ids.ToArray(); tag.Value = vals.ToArray();
            return tag;
        }

        static uint Mix(uint x) { x ^= x >> 16; x *= 0x7FEB352D; x ^= x >> 15; x *= 0x846CA68B; x ^= x >> 16; return x + 0x9E3779B9; }

        public static void Register(IntPtr p, RarityTag tag) { lock (tags) tags[p] = tag; }

        public static double CurrentLuck(int baseLevel)
        {
            double luck = Cfg.Luck + Cfg.LuckPerLevel * baseLevel;
            try { var srv = PLServer.Instance; if (srv != null) luck += Cfg.LuckPerChaos * (float)srv.ChaosLevel; } catch { }
            return Math.Max(0.05, luck);
        }

        // Estrazione pesata: la fortuna moltiplica il peso del grado t per luck^t.
        public static int Roll(Random rng, double luck)
        {
            double total = 0; var w = new double[Cfg.Tiers.Length];
            for (int i = 0; i < w.Length; i++) { w[i] = Math.Max(0, Cfg.Tiers[i].Weight) * Math.Pow(luck, i); total += w[i]; }
            if (total <= 0) return 0;
            double r = rng.NextDouble() * total;
            for (int i = 0; i < w.Length; i++) { r -= w[i]; if (r < 0) return i; }
            return 0;
        }

        // Sostituisce un componente appena generato con la sua versione di grado estratto.
        public static PLShipComponent Upgrade(PLShipComponent c, Random rng)
        {
            if (!Cfg.Enabled || c == null || Get(c.Pointer) != null) return c;
            uint h = c.getHash();
            if (TierOf(h) != 0 || !SlotAllowed((int)(h & 0x3F))) return c;
            int tier = Roll(rng, CurrentLuck(LevelOf(h)));
            if (tier == 0) return c;
            var n = PLShipComponent.CreateShipComponentFromHash((int)((h & BodyMask) | ((uint)tier << 29)), null);
            if (n != null && Plugin.RarityLog.Value) Plugin.L.LogInfo($"[rarità] generato: {n.Name} (liv. {n.GetLevel()})");
            return n ?? c;
        }

        public static string Colored(int tier, string text) =>
            Cfg.ColorNames ? $"<color={Tier(tier).Color}>{text}</color>" : text;

        public static string Describe(RarityTag tag)
        {
            var sb = new StringBuilder();
            sb.Append(Colored(tag.Tier, Tier(tag.Tier).Name));
            if (tag.Bonus > 0) sb.Append($" (+{tag.Bonus} liv.)");
            for (int i = 0; i < tag.Affix.Length; i++)
            {
                var a = Cfg.Affixes[tag.Affix[i]];
                string v = a.Mode == "add" ? $"{tag.Value[i]:+0.#;-0.#}" : $"{tag.Value[i] * 100:+0;-0}%";
                sb.Append($"\n{a.Label} {v} — {a.Name}");
            }
            return sb.ToString();
        }

        public static void Apply(PLShipStats s, RarityTag tag)
        {
            for (int i = 0; i < tag.Affix.Length; i++)
            {
                var a = Cfg.Affixes[tag.Affix[i]];
                if (!stats.TryGetValue(a.Stat, out var acc)) continue;
                float cur = acc.get(s);
                acc.set(s, a.Mode == "add" ? cur + tag.Value[i] : cur * (1f + tag.Value[i]));
            }
        }

        // Tutte le implementazioni di un metodo nella gerarchia dei componenti.
        public static IEnumerable<MethodBase> Impl(string name, bool isStatic = false) =>
            typeof(PLShipComponent).Assembly.GetTypes()
                .Where(t => typeof(PLShipComponent).IsAssignableFrom(t))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.DeclaredOnly | (isStatic ? BindingFlags.Static : BindingFlags.Instance)))
                .Where(m => m.Name == name && !m.IsAbstract && !m.IsGenericMethod);
    }

    // Un oggetto nuovo allo stesso indirizzo di uno liberato non deve ereditarne l'etichetta: i costruttori
    // base dei componenti la cancellano. Harmony non aggancia i costruttori interop, quindi serve un detour nativo.
    static class RarityCtorHook
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Ctor1(IntPtr self, int slot, IntPtr method);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Ctor3(IntPtr self, IntPtr name, IntPtr desc, int slot, IntPtr method);
        static Ctor1 orig1, hook1; static Ctor3 orig3, hook3; static INativeDetour d1, d3;

        static IntPtr Native(string field)
        {
            var f = typeof(PLShipComponent).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new MissingFieldException(field);
            return Marshal.ReadIntPtr((IntPtr)f.GetValue(null)); // primo campo di MethodInfo: puntatore al codice
        }

        public static void Install()
        {
            hook1 = (self, slot, m) => { Rarity.CtorCalls++; Rarity.Forget(self); orig1(self, slot, m); };
            hook3 = (self, n, d, slot, m) => { Rarity.CtorCalls++; Rarity.Forget(self); orig3(self, n, d, slot, m); };
            d1 = INativeDetour.CreateAndApply(Native("NativeMethodInfoPtr__ctor_Public_Void_ESlotType_0"), hook1, out orig1);
            d3 = INativeDetour.CreateAndApply(Native("NativeMethodInfoPtr__ctor_Public_Void_String_String_ESlotType_0"), hook3, out orig3);
        }
    }

    [HarmonyPatch(typeof(PLShipComponent), nameof(PLShipComponent.CreateShipComponentFromHash))]
    static class RarityFromHash
    {
        static void Prefix(ref int inHash, out int __state)
        {
            __state = 0;
            uint h = (uint)inHash; int tier = Rarity.TierOf(h);
            if (tier == 0 || !Rarity.Cfg.Enabled || tier >= Rarity.Cfg.Tiers.Length) return;
            int lvl = Rarity.LevelOf(h);
            int boosted = Math.Min(31, lvl + Rarity.Tier(tier).LevelBonus);
            inHash = (int)(Rarity.WithLevel(h, boosted) & Rarity.BodyMask);
            __state = tier | ((boosted - lvl) << 8) | (1 << 16);
        }

        static void Postfix(PLShipComponent __result, int __state, int inHash)
        {
            if (__state == 0 || __result == null) return;
            int tier = __state & 0xFF, bonus = (__state >> 8) & 0xFF;
            uint identity = Rarity.WithLevel((uint)inHash, Rarity.LevelOf((uint)inHash) - bonus);
            Rarity.Register(__result.Pointer, Rarity.MakeTag(identity, tier, bonus));
        }
    }

    [HarmonyPatch]
    static class RarityGetHash
    {
        static IEnumerable<MethodBase> TargetMethods() => Rarity.Impl("getHash");
        static void Postfix(PLShipComponent __instance, ref uint __result)
        {
            if (Rarity.TagCount == 0) return;
            var tag = Rarity.Get(__instance.Pointer);
            if (tag == null) return;
            if (tag.Slot != (int)(__result & 0x3F)) { Rarity.Forget(__instance.Pointer); return; }
            int baseLevel = Math.Max(0, Rarity.LevelOf(__result) - tag.Bonus);
            __result = (Rarity.WithLevel(__result, baseLevel) & Rarity.BodyMask) | ((uint)tag.Tier << 29);
        }
    }

    [HarmonyPatch]
    static class RarityLevelText
    {
        static IEnumerable<MethodBase> TargetMethods() => Rarity.Impl("GetLevel");
        static void Postfix(PLShipComponent __instance, ref string __result)
        {
            if (Rarity.TagCount == 0) return;
            var tag = Rarity.Get(__instance.Pointer);
            if (tag != null && tag.Bonus > 0) __result = (__instance.Level - tag.Bonus + 1).ToString();
        }
    }

    [HarmonyPatch(typeof(PLShipComponent), nameof(PLShipComponent.Name), MethodType.Getter)]
    static class RarityName
    {
        static void Postfix(PLShipComponent __instance, ref string __result)
        {
            if (Rarity.TagCount == 0 || __result == null) return;
            var tag = Rarity.Get(__instance.Pointer);
            if (tag == null) return;
            // I titoli del gioco non interpretano i tag colore: nel nome va solo testo.
            __result = Rarity.Cfg.NameFormat.Replace("{name}", __result).Replace("{tier}", Rarity.Tier(tag.Tier).Name);
        }
    }

    [HarmonyPatch(typeof(PLShipComponent), nameof(PLShipComponent.Desc), MethodType.Getter)]
    static class RarityDesc
    {
        static void Postfix(PLShipComponent __instance, ref string __result)
        {
            if (Rarity.TagCount == 0) return;
            var tag = Rarity.Get(__instance.Pointer);
            if (tag != null) __result = (__result ?? "") + "\n\n" + Rarity.Describe(tag);
        }
    }

    [HarmonyPatch]
    static class RarityPrice
    {
        static IEnumerable<MethodBase> TargetMethods() => Rarity.Impl("GetScaledMarketPrice");
        static void Postfix(PLShipComponent __instance, ref int __result)
        {
            if (Rarity.TagCount == 0) return;
            var tag = Rarity.Get(__instance.Pointer);
            if (tag != null) __result = (int)Math.Round(__result * Rarity.Tier(tag.Tier).PriceMult);
        }
    }

    // Gli affissi si applicano a statistiche complete, dopo che tutti i componenti hanno dato il loro contributo.
    [HarmonyPatch(typeof(PLShipStats), nameof(PLShipStats.CalculateStats))]
    static class RarityStats
    {
        static void Postfix(PLShipStats __instance)
        {
            Rarity.CalcCalls++;
            Stations.Apply(__instance);
            if (Rarity.TagCount == 0) return;
            var list = __instance.m_AllComponents;
            if (list == null) return;
            for (int i = 0, n = list.Count; i < n; i++)
            {
                var c = list[i];
                if (c == null) continue;
                var tag = Rarity.Get(c.Pointer);
                if (tag == null || tag.Affix.Length == 0 || !c.IsEquipped) continue;
                Rarity.Apply(__instance, tag);
            }
        }
    }

    // Generazione: pickup casuali (seme condiviso, quindi estrazione deterministica) e merci dei negozi (solo server).
    [HarmonyPatch]
    static class RarityRollRandom
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            Rarity.Impl("CreateRandom", isStatic: true).Where(m => { var p = m.GetParameters(); return p.Length == 2 && p[1].ParameterType == typeof(int); });
        static void Postfix(ref PLShipComponent __result, int inSeed)
        {
            if (__result != null) __result = Rarity.Upgrade(__result, new Random(inSeed ^ 0x4D454C43));
        }
    }

    [HarmonyPatch(typeof(TraderPersistantDataEntry), nameof(TraderPersistantDataEntry.ServerAddWare))]
    static class RarityRollShop
    {
        static readonly Random rng = new();
        static void Prefix(ref PLWare inShipComp)
        {
            var c = inShipComp?.TryCast<PLShipComponent>();
            if (c == null) return;
            var n = Rarity.Upgrade(c, rng);
            if (n.Pointer != c.Pointer) inShipComp = n;
        }
    }
}

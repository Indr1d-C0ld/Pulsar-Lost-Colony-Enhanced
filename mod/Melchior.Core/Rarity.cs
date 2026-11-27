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
        public double DrawbackChance;   // probabilità di un difetto (oggetti instabili)
    }

    // Un affisso ritocca una statistica della nave ("Stat") oppure un sistema delle stazioni ("Key", vedi Loot.KeyLabels).
    // Con "Cond" vale solo in una situazione (allarme rosso, scafo basso, fascia d'ingaggio...). I difetti hanno Drawback.
    public class AffixDef
    {
        public string Name = "";
        public string Label = "";       // testo mostrato nella descrizione
        public string Stat = "";        // proprietà float di PLShipStats
        public string Key = "";         // bonus di stazione (in alternativa a Stat)
        public string Mode = "mul";     // "mul": percentuale; "add": valore fisso
        public string Cond = "";        // condizione di validità (vuota = sempre)
        public bool Drawback;           // difetto degli oggetti instabili
        public float Min, Max;
        public int[] Slots = Array.Empty<int>(); // tipi di componente ammessi (ESlotType); vuoto = tutti
    }

    // Leggendari: pezzi unici con nome, storia ed effetti fissi; alcuni appartengono a un set.
    public class LegendaryDef
    {
        public string Name = "", Lore = "", Set = "";
        public int[] Slots = Array.Empty<int>();
        public AffixDef[] Effects = Array.Empty<AffixDef>();
    }

    public class SetDef
    {
        public string Name = "";
        public AffixDef[] Bonus2 = Array.Empty<AffixDef>(), Bonus3 = Array.Empty<AffixDef>(), Bonus4 = Array.Empty<AffixDef>();
    }

    public class RarityConfig
    {
        public bool Enabled = true;
        public bool ColorNames = true;      // colora il grado nelle descrizioni (i titoli restano testo semplice)
        public string NameFormat = "{name} ({tier})";
        public string LegendFormat = "«{legend}» ({tier})";
        public double Luck = 1.0;          // fortuna di base: >1 sposta le estrazioni verso i gradi alti
        public double LuckPerLevel = 0.04; // fortuna aggiunta per ogni livello base dell'oggetto
        public double LuckPerChaos = 0.05; // fortuna aggiunta per ogni punto di Caos della galassia
        public int AnnounceFrom = 3;       // dal grado Sperimentale in su i ritrovamenti vengono annunciati
        public int[] Slots = { 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 16, 19, 20, 24, 25, 26, 28, 32, 33, 34 };
        public TierDef[] Tiers =
        {
            new() { Name = "Standard",     Color = "#D8D8D8", Weight = 600, LevelBonus = 0, PriceMult = 1.0,  Affixes = 0 },
            new() { Name = "Raffinato",    Color = "#7CD992", Weight = 210, LevelBonus = 0, PriceMult = 1.1,  Affixes = 1 },
            new() { Name = "Avanzato",     Color = "#6FA8FF", Weight = 105, LevelBonus = 1, PriceMult = 1.2,  Affixes = 2 },
            new() { Name = "Sperimentale", Color = "#C58AF9", Weight = 48,  LevelBonus = 1, PriceMult = 1.35, Affixes = 2 },
            new() { Name = "Prototipo",    Color = "#F2A65A", Weight = 22,  LevelBonus = 2, PriceMult = 1.5,  Affixes = 3 },
            new() { Name = "Reliquia",     Color = "#FFD75E", Weight = 10,  LevelBonus = 2, PriceMult = 1.75, Affixes = 3 },
            new() { Name = "Anomalo",      Color = "#5EF0E0", Weight = 4,   LevelBonus = 3, PriceMult = 2.0,  Affixes = 4, DrawbackChance = 0.6 },
            new() { Name = "Leggendario",  Color = "#FF5E7A", Weight = 1,   LevelBonus = 4, PriceMult = 2.6,  Affixes = 4 },
        };
        public AffixDef[] Affixes = Loot.DefaultAffixes();
        public LegendaryDef[] Legendaries = Loot.DefaultLegendaries();
        public SetDef[] Sets = Loot.DefaultSets();
    }

    // Etichetta associata a un componente vivo.
    public sealed class RarityTag
    {
        public int Tier, Bonus, Slot, Legend = -1, Maker = -1, Serial; public string Nick = ""; public float Quality = 50f;
        public AffixDef[] Defs = Array.Empty<AffixDef>();   // affissi (o effetti del leggendario), difetti compresi
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
            // nuovi affissi e leggendari delle versioni successive: si aggiungono a una configurazione già esistente
            try
            {
                var have = Cfg.Affixes.Select(a => a.Name + "|" + a.Key + "|" + a.Stat).ToHashSet();
                var add = Loot.DefaultAffixes().Where(a => !have.Contains(a.Name + "|" + a.Key + "|" + a.Stat)).ToArray();
                var haveL = Cfg.Legendaries.Select(l => l.Name).ToHashSet(); var addL = Loot.DefaultLegendaries().Where(l => !haveL.Contains(l.Name)).ToArray();
                if (add.Length + addL.Length > 0) { Cfg.Affixes = Cfg.Affixes.Concat(add).ToArray(); Cfg.Legendaries = Cfg.Legendaries.Concat(addL).ToArray(); File.WriteAllText(path, JsonSerializer.Serialize(Cfg, json)); Plugin.L.LogInfo($"Rarità: aggiunti {add.Length} affissi e {addL.Length} leggendari alla configurazione."); }
            }
            catch (Exception e) { Plugin.L.LogWarning("Rarità, aggiornamento della configurazione: " + e.Message); }
            stats.Clear();
            var all = Cfg.Affixes.Concat(Cfg.Legendaries.SelectMany(l => l.Effects)).Concat(Cfg.Sets.SelectMany(x => x.Bonus2.Concat(x.Bonus3).Concat(x.Bonus4)));
            foreach (var a in all)
            {
                if (a.Stat.Length == 0 || stats.ContainsKey(a.Stat)) continue;
                var acc = Accessor(a.Stat);
                if (acc.get == null) Plugin.L.LogWarning($"Affisso '{a.Name}': statistica '{a.Stat}' sconosciuta, ignorato.");
                else stats[a.Stat] = acc;
            }
            Plugin.L.LogInfo($"Rarità: {Cfg.Tiers.Length} gradi, {stats.Count} statistiche, {Cfg.Affixes.Length} affissi, {Cfg.Legendaries.Length} leggendari, {Cfg.Sets.Length} set ({(Cfg.Enabled ? "attiva" : "disattivata")}).");
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

        public static bool Usable(AffixDef a) => a.Key.Length > 0 || stats.ContainsKey(a.Stat);
        static string Target(AffixDef a) => a.Key.Length > 0 ? "k:" + a.Key : "s:" + a.Stat + "|" + a.Cond;

        // Affissi: scelti in modo deterministico dall'identità dell'oggetto (hash senza livello nascosto),
        // quindi ogni client ricalcola gli stessi. Il grado più alto diventa un leggendario, se ce n'è uno per quel tipo.
        public static RarityTag MakeTag(uint identity, int tier, int bonus)
        {
            int slot = (int)(identity & 0x3F);
            var tag = new RarityTag { Tier = tier, Bonus = bonus, Slot = slot };
            uint s = Mix((identity & BodyMask) | ((uint)tier << 29));
            if (tier == Cfg.Tiers.Length - 1)
            {
                var leg = Enumerable.Range(0, Cfg.Legendaries.Length).Where(i => Array.IndexOf(Cfg.Legendaries[i].Slots, slot) >= 0).ToList();
                if (leg.Count > 0)
                {
                    tag.Legend = leg[(int)(Mix(s ^ 0x1E6E) % (uint)leg.Count)];
                    var eff = Cfg.Legendaries[tag.Legend].Effects.Where(Usable).ToArray();
                    tag.Defs = eff; tag.Value = eff.Select(e => e.Min).ToArray();
                    return tag;
                }
            }
            int want = Tier(tier).Affixes;
            // casa produttrice, modello, numero di serie: ogni esemplare ha la sua identità
            uint ms = Mix(s ^ 0x4D4B52);
            tag.Maker = (int)(ms % (uint)Loot.Makers.Length); ms = Mix(ms); tag.Nick = Loot.Nicks[ms % (uint)Loot.Nicks.Length]; ms = Mix(ms); tag.Serial = (int)(ms % 9000) + 1000;
            var bias = Loot.Makers[tag.Maker].Bias;
            var ids = new List<AffixDef>(); var vals = new List<float>(); var used = new HashSet<string>();
            var own = Cfg.Affixes.Where(a => !a.Drawback && Usable(a) && Array.IndexOf(a.Slots, slot) >= 0).ToList();
            // lo stile della casa: il primo affisso viene, se possibile, fra i suoi preferiti
            var fav = own.Where(a => Array.IndexOf(bias, a.Key.Length > 0 ? a.Key : a.Stat) >= 0).ToList();
            if (want > 0 && fav.Count > 0 && (Mix(s ^ 0xFA7) & 0xFF) < 180) { s = Mix(s ^ 0xFA8); var a = fav[(int)(s % (uint)fav.Count)]; own.Remove(a); used.Add(Target(a)); s = Mix(s); ids.Add(a); vals.Add(a.Min + (a.Max - a.Min) * ((s & 0xFFFF) / 65535f)); }
            var any = Cfg.Affixes.Where(a => !a.Drawback && Usable(a) && a.Slots.Length == 0).ToList();
            while (ids.Count < want && own.Count + any.Count > 0)
            {
                var pool = own.Count > 0 ? own : any;
                s = Mix(s); int k = (int)(s % (uint)pool.Count); var a = pool[k]; pool.RemoveAt(k);
                if (!used.Add(Target(a))) continue; // una sola modifica per bersaglio
                s = Mix(s); float t = (s & 0xFFFF) / 65535f;
                ids.Add(a); vals.Add(a.Min + (a.Max - a.Min) * t);
            }
            // oggetti instabili: un difetto, ma più potenza (i suoi affissi valgono di più)
            if (Tier(tier).DrawbackChance > 0 && (Mix(s ^ 0xD3F) & 0xFFFF) / 65535.0 < Tier(tier).DrawbackChance)
            {
                for (int i = 0; i < vals.Count; i++) vals[i] *= 1.25f;
                var bad = Cfg.Affixes.Where(a => a.Drawback && Usable(a) && !used.Contains(Target(a))).ToList();
                if (bad.Count > 0) { s = Mix(s ^ 0xBAD); var a = bad[(int)(s % (uint)bad.Count)]; s = Mix(s); ids.Add(a); vals.Add(a.Min + (a.Max - a.Min) * ((s & 0xFFFF) / 65535f)); }
            }
            tag.Defs = ids.ToArray(); tag.Value = vals.ToArray();
            // qualità dell'esemplare: quanto i suoi valori si avvicinano al massimo possibile
            var good = Enumerable.Range(0, ids.Count).Where(i => !ids[i].Drawback && ids[i].Max != ids[i].Min).Select(i => (vals[i] - ids[i].Min) / (ids[i].Max - ids[i].Min)).ToList();
            tag.Quality = good.Count == 0 ? 40f + (Mix(s ^ 0x9A1) % 40) : Math.Clamp(good.Average() * 100f, 1f, 125f);
            return tag;
        }

        static uint Mix(uint x) { x ^= x >> 16; x *= 0x7FEB352D; x ^= x >> 15; x *= 0x846CA68B; x ^= x >> 16; return x + 0x9E3779B9; }

        public static void Register(IntPtr p, RarityTag tag) { lock (tags) tags[p] = tag; }

        public static double CurrentLuck(int baseLevel)
        {
            double luck = Cfg.Luck + Cfg.LuckPerLevel * baseLevel + PawnLoot.Crew("fortuna");
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

        public static string Describe(RarityTag tag) => Loot.Describe(tag);

        // Applica una statistica (i bonus di stazione li raccoglie Loot).
        public static void ApplyStat(PLShipStats s, AffixDef a, float value)
        {
            if (!stats.TryGetValue(a.Stat, out var acc)) return;
            float cur = acc.get(s);
            acc.set(s, a.Mode == "add" ? cur + value : cur * Math.Max(0.05f, 1f + value));
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
            __result = tag.Legend >= 0
                ? Rarity.Cfg.LegendFormat.Replace("{legend}", Rarity.Cfg.Legendaries[tag.Legend].Name).Replace("{name}", __result).Replace("{tier}", Rarity.Tier(tag.Tier).Name)
                : Rarity.Cfg.NameFormat.Replace("{name}", __result + (tag.Tier > 0 && tag.Nick.Length > 0 ? $" «{tag.Nick}»" : "")).Replace("{tier}", Rarity.Tier(tag.Tier).Name);
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
            if (tag != null) __result = (int)Math.Round(__result * Rarity.Tier(tag.Tier).PriceMult * (tag.Maker >= 0 ? Loot.Makers[tag.Maker].Prestige : 1f) * (0.8f + Math.Min(1.25f, tag.Quality / 100f) * 0.4f));
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
            try { Loot.ApplyShip(__instance); } catch (Exception e) { Plugin.L.LogWarning("Bottino: " + e.Message); }
            Shipyard.Apply(__instance);
            Wear.Apply(__instance);
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

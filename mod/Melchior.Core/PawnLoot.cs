// Equipaggiamento personale con rarità: pistole, fucili, granate, attrezzi, armature e jetpack hanno lo stesso
// sistema dei componenti della nave (gradi, case produttrici, modelli, numero di serie, qualità, affissi, difetti).
// In rete gli oggetti viaggiano come tipo, sottotipo e livello, ma ognuno ha un identificativo di rete (NetID) uguale
// su tutti i computer: rarità e affissi si calcolano da quello, quindi ogni esemplare è unico e tutti lo vedono uguale.
// Vale per tutti: anche le armi degli equipaggi nemici hanno il loro grado.
// Effetti: danno delle armi e colpi critici (al tiro), corazza personale (sui danni subiti dalla pedina), e bonus
// d'equipaggio sommati su tutti a bordo (fortuna nel bottino, commercio, ricerca, morale, fama, riparazioni).
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace Melchior
{
    public sealed class PawnTag { public int Tier, Maker = -1, Serial, NetId; public string Nick = ""; public float Quality; public string[] Keys = Array.Empty<string>(); public float[] Vals = Array.Empty<float>(); public bool[] Bad = Array.Empty<bool>(); }

    public static class PawnLoot
    {
        public static bool Enabled = true;
        static readonly Dictionary<IntPtr, PawnTag> cache = new();

        sealed class FxDef { public string Key = "", Label = ""; public float Min, Max; public bool Bad; }
        static FxDef F(string k, string l, float a, float b, bool bad = false) => new() { Key = k, Label = l, Min = a, Max = b, Bad = bad };
        static readonly FxDef[] Weapon = { F("danno", "Danno", 0.06f, 0.22f), F("critico", "Colpi critici", 0.04f, 0.14f), F("corazza", "Corazza personale", 0.03f, 0.08f), F("fortuna", "Fortuna nel bottino", 0.05f, 0.15f), F("fama", "Fama guadagnata", 0.05f, 0.15f) };
        static readonly FxDef[] Tool = { F("repair_speed", "Riparazioni di bordo", 0.08f, 0.25f), F("ricerca", "Dati di ricerca", 0.08f, 0.25f), F("commercio", "Prezzi di vendita", 0.02f, 0.06f), F("carisma", "Morale guadagnato", 0.05f, 0.15f), F("fortuna", "Fortuna nel bottino", 0.05f, 0.15f) };
        static readonly FxDef[] Wear = { F("corazza", "Corazza personale", 0.06f, 0.18f), F("carisma", "Morale guadagnato", 0.05f, 0.15f), F("fama", "Fama guadagnata", 0.05f, 0.18f), F("fortuna", "Fortuna nel bottino", 0.05f, 0.12f), F("repair_speed", "Riparazioni di bordo", 0.05f, 0.15f) };
        static readonly FxDef[] Throw = { F("danno", "Potenza", 0.10f, 0.30f), F("fortuna", "Fortuna nel bottino", 0.04f, 0.10f) };
        static readonly FxDef[] Flaws = { F("corazza", "Corazza personale", -0.10f, -0.05f, true), F("fortuna", "Fortuna nel bottino", -0.10f, -0.05f, true), F("carisma", "Morale guadagnato", -0.10f, -0.05f, true) };

        static FxDef[] Pool(EPawnItemType t) => t switch
        {
            EPawnItemType.E_LASERPISTOL or EPawnItemType.E_PHASEPISTOL or EPawnItemType.E_PIERCING_BEAM_PISTOL or EPawnItemType.E_SMUGGLERS_PISTOL or EPawnItemType.E_BURST_PISTOL
                or EPawnItemType.E_RANGER or EPawnItemType.E_HEAVY_PISTOL or EPawnItemType.E_HAND_SHOTGUN or EPawnItemType.E_HELD_BEAM_PISTOL or EPawnItemType.E_HELD_BEAM_PISTOL_W_HEALING or EPawnItemType.E_WD_HEAVY or EPawnItemType.E_ICE_SPIKES => Weapon,
            EPawnItemType.E_REPAIRGUN or EPawnItemType.E_FIREGUN or EPawnItemType.E_SCANNER or EPawnItemType.E_SYRINGE => Tool,
            EPawnItemType.E_ARMOR or EPawnItemType.E_JETPACK => Wear,
            EPawnItemType.E_PULSE_GRENADE or EPawnItemType.E_HEAL_GRENADE or EPawnItemType.E_MINI_GRENADE or EPawnItemType.E_REPAIR_GRENADE or EPawnItemType.E_STUN_GRENADE or EPawnItemType.E_VORTEX_GRENADE or EPawnItemType.E_ANTIFIRE_GRENADE => Throw,
            _ => null,
        };

        static uint Mix(uint x) { x ^= x >> 16; x *= 0x7FEB352D; x ^= x >> 15; x *= 0x846CA68B; x ^= x >> 16; return x + 0x9E3779B9; }
        static int Seed() { try { return (int)PLServer.Instance.GalaxySeed; } catch { return 0; } }

        public static PawnTag Of(PLPawnItem it)
        {
            if (!Enabled || it == null) return null;
            int net; EPawnItemType type; int sub, lvl;
            try { net = it.NetID; type = it.PawnItemType; sub = it.SubType; lvl = it.Level; } catch { return null; }
            if (net <= 0) return null;
            if (cache.TryGetValue(it.Pointer, out var c) && c.NetId == net) return c;
            var pool = Pool(type); var tag = new PawnTag { NetId = net };
            if (pool != null)
            {
                uint s = Mix((uint)net * 2654435761u ^ (uint)Seed() ^ ((uint)type << 20) ^ ((uint)sub << 12) ^ ((uint)lvl << 4));
                tag.Tier = Rarity.Roll(new Random((int)(s & 0x7fffffff)), Math.Max(0.05, Rarity.Cfg.Luck + Rarity.Cfg.LuckPerLevel * lvl)); // fortuna di base: uguale su ogni computer
                if (tag.Tier > 0)
                {
                    uint m = Mix(s ^ 0x4D4B52); tag.Maker = (int)(m % (uint)Loot.Makers.Length); m = Mix(m); tag.Nick = Loot.Nicks[m % (uint)Loot.Nicks.Length]; m = Mix(m); tag.Serial = (int)(m % 9000) + 1000;
                    int want = Math.Min(pool.Length, Rarity.Tier(tag.Tier).Affixes); var keys = new List<string>(); var vals = new List<float>(); var bad = new List<bool>(); var used = new HashSet<string>();
                    var p = pool.ToList(); double qsum = 0; int qn = 0;
                    while (keys.Count < want && p.Count > 0) { s = Mix(s); var a = p[(int)(s % (uint)p.Count)]; p.Remove(a); if (!used.Add(a.Key)) continue; s = Mix(s); float t = (s & 0xFFFF) / 65535f; keys.Add(a.Key); vals.Add(a.Min + (a.Max - a.Min) * t); bad.Add(false); qsum += t; qn++; }
                    if (Rarity.Tier(tag.Tier).DrawbackChance > 0 && (Mix(s ^ 0xD3F) & 0xFFFF) / 65535.0 < Rarity.Tier(tag.Tier).DrawbackChance)
                    {
                        for (int i = 0; i < vals.Count; i++) vals[i] *= 1.25f;
                        var fl = Flaws.Where(f => !used.Contains(f.Key)).ToList(); if (fl.Count > 0) { s = Mix(s ^ 0xBAD); var f = fl[(int)(s % (uint)fl.Count)]; keys.Add(f.Key); vals.Add(f.Min + (f.Max - f.Min) * ((Mix(s) & 0xFFFF) / 65535f)); bad.Add(true); }
                    }
                    tag.Keys = keys.ToArray(); tag.Vals = vals.ToArray(); tag.Bad = bad.ToArray(); tag.Quality = qn == 0 ? 50f : (float)(qsum / qn * 100);
                }
            }
            cache[it.Pointer] = tag; return tag;
        }
        public static float Fx(PLPawnItem it, string key) { var t = Of(it); if (t == null) return 0f; float v = 0; for (int i = 0; i < t.Keys.Length; i++) if (t.Keys[i] == key) v += t.Vals[i]; return v; }

        // oggetti portati da una pedina (le armi e gli attrezzi in mano o nella cintura)
        static IEnumerable<PLPawnItem> Items(PLPawn p)
        {
            PLPawnInventory inv = null; try { inv = p?.GetPlayer()?.MyInventory; } catch { }
            if (inv == null) yield break;
            var all = inv.GetAllItems(true); if (all == null) yield break;
            foreach (var l in all) { if (l == null) continue; foreach (var it in l) if (it != null) yield return it; }
        }
        public static float Personal(PLPawn p, string key) { float v = 0; try { foreach (var it in Items(p)) v += Fx(it, key); } catch { } return v; }

        // bonus d'equipaggio: somma su tutte le pedine della nostra squadra a bordo
        static readonly Dictionary<string, float> crewCache = new(); static float crewAt;
        public static float Crew(string key)
        {
            if (!Enabled) return 0f;
            if (UnityEngine.Time.time - crewAt > 3f) { crewCache.Clear(); crewAt = UnityEngine.Time.time; }
            if (crewCache.TryGetValue(key, out var c)) return c;
            float v = 0;
            try
            {
                int team = Stations.MyShip?.TeamID ?? 0; var pawns = PLGameStatic.Instance.AllPawns;
                for (int i = 0; i < pawns.Count; i++) { var p = pawns[i]; if (p == null || p.IsDead || p.GetTeamID() != team) continue; v += Personal(p, key); }
            }
            catch { }
            v = Math.Clamp(v, -0.5f, key == "fortuna" ? 1.5f : 0.6f); crewCache[key] = v; return v;
        }

        public static string Describe(PawnTag t)
        {
            if (t == null || t.Tier == 0) return "";
            var sb = new System.Text.StringBuilder("\n\n" + Rarity.Colored(t.Tier, Rarity.Tier(t.Tier).Name));
            if (t.Maker >= 0) { var m = Loot.Makers[t.Maker]; sb.Append($"\n{m.Name}, «{t.Nick}» serie {m.Code}-{t.Serial:0000} · qualità {t.Quality:0}%"); }
            for (int i = 0; i < t.Keys.Length; i++)
            {
                string label = Weapon.Concat(Tool).Concat(Wear).Concat(Throw).Concat(Flaws).FirstOrDefault(f => f.Key == t.Keys[i])?.Label ?? t.Keys[i];
                sb.Append("\n").Append(t.Bad[i] ? "<color=#FF8A7A>Difetto: " : "").Append($"{label} {(t.Vals[i] >= 0 ? "+" : "")}{t.Vals[i] * 100:0}%").Append(t.Bad[i] ? "</color>" : "");
            }
            return sb.ToString();
        }
        public static string Report()
        {
            var p = PLNetworkManager.Instance?.MyLocalPawn; if (p == null) return "nessun personaggio";
            var sb = new System.Text.StringBuilder();
            foreach (var it in Items(p)) { var t = Of(it); sb.Append($"\n      {it.PawnItemType} liv.{it.Level} rete {it.NetID}: {(t == null || t.Tier == 0 ? "standard" : Rarity.Tier(t.Tier).Name + " «" + t.Nick + "» " + string.Join(", ", t.Keys.Select((k, i) => k + " " + t.Vals[i].ToString("0.00"))))}"); }
            sb.Append($"\n      equipaggio: fortuna {Crew("fortuna"):0.00}, commercio {Crew("commercio"):0.00}, ricerca {Crew("ricerca"):0.00}, morale {Crew("carisma"):0.00}");
            return sb.ToString();
        }
    }

    // ---- danno delle armi personali: tutte le classi che calcolano il danno
    [HarmonyPatch]
    static class PawnDamage
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            typeof(PLPawnItem).Assembly.GetTypes().Where(t => typeof(PLPawnItem).IsAssignableFrom(t))
                .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                .Where(m => m.Name == "CalcDamageDone" && m.ReturnType == typeof(float) && !m.IsAbstract);
        static readonly Random rng = new();
        static void Postfix(PLPawnItem __instance, ref float __result)
        {
            if (!PawnLoot.Enabled) return;
            try { __result *= 1f + PawnLoot.Fx(__instance, "danno"); float crit = PawnLoot.Fx(__instance, "critico"); if (crit > 0 && rng.NextDouble() < crit) __result *= 2f; } catch { }
        }
    }
    // ---- corazza personale: vale per chiunque, anche per i nemici
    [HarmonyPatch(typeof(PLPawn), nameof(PLPawn.TakeDamage))]
    static class PawnArmor
    {
        static void Prefix(PLPawn __instance, ref float inDmg)
        {
            if (!PawnLoot.Enabled || inDmg <= 0) return;
            try { float a = Math.Clamp(PawnLoot.Personal(__instance, "corazza"), -0.3f, 0.5f); inDmg *= 1f - a; } catch { }
        }
    }
    // ---- nome, descrizione e prezzo
    [HarmonyPatch(typeof(PLPawnItem), nameof(PLPawnItem.GetItemName))]
    static class PawnName
    {
        static void Postfix(PLPawnItem __instance, ref string __result)
        {
            try { var t = PawnLoot.Of(__instance); if (t != null && t.Tier > 0 && __result != null) __result = $"{__result} «{t.Nick}» ({Rarity.Tier(t.Tier).Name})"; } catch { }
        }
    }
    [HarmonyPatch(typeof(PLPawnItem), nameof(PLPawnItem.GetShortDesc))]
    static class PawnDesc
    {
        static void Postfix(PLPawnItem __instance, ref string __result)
        {
            try { var t = PawnLoot.Of(__instance); if (t != null && t.Tier > 0) __result = (__result ?? "") + PawnLoot.Describe(t); } catch { }
        }
    }
    [HarmonyPatch(typeof(PLPawnItem), nameof(PLPawnItem.GetScaledMarketPrice))]
    static class PawnPrice
    {
        static void Postfix(PLPawnItem __instance, ref int __result)
        {
            try { var t = PawnLoot.Of(__instance); if (t != null && t.Tier > 0) __result = (int)Math.Round(__result * Rarity.Tier(t.Tier).PriceMult * Loot.Makers[t.Maker].Prestige * (0.8f + Math.Min(1.25f, t.Quality / 100f) * 0.4f)); } catch { }
        }
    }
}

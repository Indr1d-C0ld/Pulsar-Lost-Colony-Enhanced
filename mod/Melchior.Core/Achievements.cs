// Sistema di obiettivi interno. Le definizioni stanno in plugins/Melchior/achievements/achievements.json
// (titoli, descrizioni, icone); i progressi in config/melchior.achievements.progress.json.
// Gli obiettivi "Classici" scattano quando il gioco chiede lo sblocco del suo obiettivo originale;
// quelli "Melchior" sono legati alle nostre aggiunte.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HarmonyLib;
using UnityEngine;

namespace Melchior
{
    public class AchDef
    {
        public string Id = "", Title = "", Desc = "", Group = "", Icon = "", IconOff = "";
        public string Stat = "";      // statistica del gioco che ne misura il progresso (facoltativa)
        public bool Hidden;
        public int Max;               // 0 = sblocco secco, >0 = progresso
    }

    public class AchState { public bool Done; public int Progress; public string When = ""; }

    public static class Achievements
    {
        public static List<AchDef> Defs = new();
        public static Dictionary<string, AchState> State = new();
        public static readonly Queue<AchDef> Toasts = new();
        public static string IconDir = "";
        static string savePath = "";
        static readonly HashSet<string> unknownStats = new();
        static readonly JsonSerializerOptions json = new()
        {
            IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static void Load(string pluginDir, string configDir)
        {
            IconDir = Path.Combine(pluginDir, "achievements", "icons");
            savePath = Path.Combine(configDir, Plugin.PerInstance("melchior.achievements.progress.json"));
            try
            {
                Defs = JsonSerializer.Deserialize<List<AchDef>>(File.ReadAllText(Path.Combine(pluginDir, "achievements", "achievements.json")), json) ?? new();
                if (File.Exists(savePath)) State = JsonSerializer.Deserialize<Dictionary<string, AchState>>(File.ReadAllText(savePath), json) ?? new();
            }
            catch (Exception e) { Plugin.L.LogError("Obiettivi non caricati: " + e.Message); }
            Plugin.L.LogInfo($"Obiettivi: {Defs.Count} definiti, {State.Values.Count(s => s.Done)} raggiunti.");
        }

        static void Save()
        {
            try { File.WriteAllText(savePath, JsonSerializer.Serialize(State, json)); }
            catch (Exception e) { Plugin.L.LogError("Progressi degli obiettivi non salvati: " + e.Message); }
        }

        public static AchDef Find(string id) => Defs.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        public static AchState Get(string id) { if (!State.TryGetValue(id, out var s)) State[id] = s = new AchState(); return s; }
        public static int DoneCount => Defs.Count(d => State.TryGetValue(d.Id, out var s) && s.Done);

        public static bool Unlock(string id)
        {
            var d = Find(id);
            if (d == null) return false;
            var s = Get(d.Id);
            if (s.Done) return false;
            s.Done = true; s.When = DateTime.Now.ToString("yyyy-MM-dd HH:mm"); if (d.Max > 0) s.Progress = d.Max;
            lock (Toasts) Toasts.Enqueue(d);
            Plugin.L.LogInfo($"[obiettivi] raggiunto: {d.Title} ({d.Id})");
            Save();
            return true;
        }

        // Progresso assoluto: sblocca al raggiungimento di Max.
        public static void SetProgress(string id, int value)
        {
            var d = Find(id);
            if (d == null || d.Max <= 0) return;
            var s = Get(d.Id);
            if (s.Done || value <= s.Progress) return;
            s.Progress = Math.Min(value, d.Max);
            if (s.Progress >= d.Max) Unlock(d.Id); else Save();
        }

        // Progresso a bandierine (bit): conta quante diverse ne sono state raccolte.
        public static void AddFlag(string id, int bit)
        {
            var s = Get(id + "#flags");
            int mask = s.Progress | (1 << bit);
            if (mask == s.Progress) return;
            s.Progress = mask;
            SetProgress(id, System.Numerics.BitOperations.PopCount((uint)mask));
            Save();
        }

        public static void Stat(string name, int value)
        {
            bool known = false;
            foreach (var d in Defs) if (d.Stat.Length > 0 && d.Stat == name) { known = true; SetProgress(d.Id, value); }
            if (!known && unknownStats.Add(name) && Plugin.NetTrace.Value) Plugin.L.LogInfo($"[obiettivi] statistica non mappata: {name} = {value}");
        }

        public static void Reset() { State.Clear(); Save(); }

        // Componente con grado salito a bordo della nave del giocatore.
        public static void OnRareComponent(RarityTag tag)
        {
            Service.Add("rarita.trovati", 1); Service.Max("rarita.max", tag.Tier);
            Unlock("MEL_RARITY_FIRST");
            if (tag.Tier >= 4) Unlock("MEL_RARITY_PROTO");
            if (tag.Tier >= 7) Unlock("MEL_RARITY_SINGULAR");
            if (tag.Defs.Count(d => !d.Drawback) >= 4) Unlock("MEL_AFFIX_FOUR");
            AddFlag("MEL_RARITY_SET", tag.Tier);
        }
    }

    // Tutte le vie con cui il gioco sblocca un obiettivo originale.
    [HarmonyPatch(typeof(PLNetworkManager), nameof(PLNetworkManager.UnlockAchievement))]
    static class AchFromGame
    {
        static void Prefix(PLNetworkManager.ESteamAchievements id) => Achievements.Unlock(id.ToString());
    }

    [HarmonyPatch(typeof(Steamworks.SteamUserStats), nameof(Steamworks.SteamUserStats.SetAchievement))]
    static class AchFromApi
    {
        static void Prefix(string pchName) => Achievements.Unlock(pchName);
    }

    [HarmonyPatch(typeof(Steamworks.SteamUserStats), nameof(Steamworks.SteamUserStats.SetStat), new[] { typeof(string), typeof(int) })]
    static class AchStat
    {
        static void Prefix(string pchName, int nData) => Achievements.Stat(pchName, nData);
    }

    [HarmonyPatch(typeof(PLShipStats), nameof(PLShipStats.AddShipComponent))]
    static class AchRarity
    {
        static void Postfix(PLShipStats __instance, PLShipComponent __result)
        {
            if (__result == null || Rarity.TagCount == 0) return;
            var tag = Rarity.Get(__result.Pointer);
            if (tag == null) return;
            var mine = PLNetworkManager.Instance?.LocalPlayer?.StartingShip?.MyStats;
            if (mine != null && mine.Pointer == __instance.Pointer) Achievements.OnRareComponent(tag);
        }
    }

    // Avvisi a schermo ed elenco degli obiettivi (F9 oppure comando di console "ach").
    // Nota: niente MonoBehaviour iniettati. L'iniezione di classi di Il2CppInterop corrompe la memoria di questo
    // gioco sotto Wine, quindi il disegno si aggancia all'OnGUI di PLNetworkManager.
    public static class MelchiorHud
    {
        public static bool ShowPanel; public static int Page;
        public static Rect DebugCoords = new Rect(0, 0, 1, 1);
        public static Texture DebugTexture; // anteprima di collaudo di uno schermo di stazione
        static readonly Dictionary<string, Texture2D> icons = new();
        static AchDef toast; static float toastUntil; static bool broken;
        static GUIStyle title, body, small, wrap, box; static Texture2D dark;
        public static GUIStyle Title => title; public static GUIStyle Body => body; public static GUIStyle Small => small; public static GUIStyle Box => box;

        static Texture2D Icon(string file)
        {
            if (icons.TryGetValue(file, out var t) && t != null) return t;
            t = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            try { ImageConversion.LoadImage(t, File.ReadAllBytes(Path.Combine(Achievements.IconDir, file))); } catch { }
            return icons[file] = t;
        }

        public static void Tick()
        {
            try { if (Input.GetKeyDown(KeyCode.F9)) ShowPanel = !ShowPanel; } catch { }
        }

        public static void OnGui()
        {
            if (broken) return;
            try { Draw(); }
            catch (Exception e) { broken = true; Plugin.L.LogError("Interfaccia obiettivi disattivata: " + e.Message); }
        }

        static void Init()
        {
            dark = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            dark.SetPixel(0, 0, new Color(0.04f, 0.05f, 0.07f, 0.93f)); dark.Apply();
            // Il font predefinito di IMGUI non è incluso nel gioco: ne prendo uno tra quelli che il gioco carica.
            Font font = null; var names = new List<string>();
            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            for (int i = 0; i < fonts.Length; i++)
            {
                var f = fonts[i]; if (f == null) continue; names.Add(f.name);
                if (font == null || f.name == "MainFont") font = f;
            }
            Plugin.L.LogInfo($"[obiettivi] font disponibili: {string.Join(", ", names.Distinct())} — uso {(font == null ? "nessuno" : font.name)}");
            title = new GUIStyle(GUI.skin.label) { fontSize = 20, richText = true, wordWrap = false, font = font };
            body = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = true, wordWrap = true, font = font };
            small = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true, wordWrap = false, font = font };
            wrap = new GUIStyle(small) { wordWrap = true, fontSize = 12 };
            title.normal.textColor = wrap.normal.textColor = body.normal.textColor = small.normal.textColor = Color.white;
            box = new GUIStyle(GUI.skin.box);
            box.normal.background = dark;
        }

        static void Draw()
        {
            if (title == null) Init();
            GUI.depth = -1000;
            // Il gioco può lasciare colori trasparenti dal proprio OnGUI (dissolvenza della chat).
            GUI.color = Color.white; GUI.contentColor = Color.white; GUI.backgroundColor = Color.white;
            if (toast == null || Time.realtimeSinceStartup > toastUntil)
            {
                toast = null;
                lock (Achievements.Toasts) if (Achievements.Toasts.Count > 0) { toast = Achievements.Toasts.Dequeue(); toastUntil = Time.realtimeSinceStartup + 7f; }
            }
            if (toast != null)
            {
                float w = 480, h = 100, x = Screen.width - w - 24, y = Screen.height - h - 120;
                GUI.Box(new Rect(x, y, w, h), "", box);
                GUI.DrawTexture(new Rect(x + 14, y + 18, 64, 64), Icon(toast.Icon));
                GUI.Label(new Rect(x + 92, y + 10, w - 100, 20), "<color=#F2A65A>OBIETTIVO RAGGIUNTO</color>", small);
                GUI.Label(new Rect(x + 92, y + 28, w - 100, 28), toast.Title, title);
                GUI.Label(new Rect(x + 92, y + 56, w - 100, 40), toast.Desc, wrap);
            }
            Stations.NextNotice();
            if (Stations.Notice.Length > 0 && Time.realtimeSinceStartup < Stations.NoticeUntil)
            {
                float w = Math.Min(760, Screen.width - 48), x = (Screen.width - w) / 2, y = 28;
                float th = Math.Max(22f, body.CalcHeight(new GUIContent(Stations.Notice), w - 34)), h = th + 40;
                GUI.Box(new Rect(x, y, w, h), "", box);
                Color bar = ColorUtility.TryParseHtmlString(Stations.NoticeColor, out var nc) ? nc : new Color(0.95f, 0.65f, 0.35f, 1f);
                GUI.DrawTexture(new Rect(x, y, 8, h), Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, bar, 0, 0);
                GUI.Label(new Rect(x + 22, y + 8, w - 34, 18), $"<color={Stations.NoticeColor}>" + Stations.NoticeTitle + "</color>", small);
                GUI.Label(new Rect(x + 22, y + 28, w - 34, th + 4), Stations.Notice, body);
            }
            if (DebugTexture != null && Plugin.DevConsoleOn.Value) GUI.DrawTextureWithTexCoords(new Rect(0, 0, 720, 720), DebugTexture, DebugCoords);
            StationConsole.Draw();
            ServicePanel.Draw();
            if (!ShowPanel) return;
            const int perPage = 12, cols = 2;
            var defs = Achievements.Defs; int pages = Math.Max(1, (defs.Count + perPage - 1) / perPage);
            Page = ((Page % pages) + pages) % pages;
            float pw = 980, ph = 640, px = (Screen.width - pw) / 2, py = (Screen.height - ph) / 2;
            GUI.Box(new Rect(px, py, pw, ph), "", box);
            GUI.Label(new Rect(px + 24, py + 14, 600, 30), $"<color=#F2A65A>OBIETTIVI</color>   {Achievements.DoneCount} / {defs.Count}", title);
            GUI.Label(new Rect(px + pw - 250, py + 20, 230, 20), $"pagina {Page + 1} di {pages}  ·  F9 chiude", small);
            for (int i = 0; i < perPage; i++)
            {
                int k = Page * perPage + i; if (k >= defs.Count) break;
                var d = defs[k]; var s = Achievements.Get(d.Id);
                float cx = px + 24 + (i % cols) * (pw - 48) / cols, cy = py + 64 + (i / cols) * 92;
                GUI.DrawTexture(new Rect(cx, cy, 64, 64), Icon(s.Done ? d.Icon : d.IconOff));
                bool secret = d.Hidden && !s.Done;
                string col = s.Done ? "#FFFFFF" : "#8b90a0";
                GUI.Label(new Rect(cx + 76, cy, 370, 26), $"<color={col}>{(secret ? "Obiettivo segreto" : d.Title)}</color>", body);
                GUI.Label(new Rect(cx + 76, cy + 22, 380, 34), $"<color=#8b90a0>{(secret ? "Continua a esplorare per scoprirlo." : d.Desc)}</color>", wrap);
                string foot = s.Done ? "<color=#7CD992>raggiunto il " + s.When + "</color>" : (d.Max > 0 ? $"{s.Progress} / {d.Max}" : "");
                GUI.Label(new Rect(cx + 76, cy + 54, 370, 20), foot, small);
            }
        }
    }

    [HarmonyPatch(typeof(PLNetworkManager), "OnGUI")]
    static class HudDraw
    {
        static void Postfix() => MelchiorHud.OnGui();
    }
}

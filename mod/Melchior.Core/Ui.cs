// Ritocchi all'interfaccia: lingua forzata all'avvio ed elementi da nascondere (per esempio i riferimenti a Steam).
// L'elenco degli elementi sta in config/melchior.ui.json: percorsi di oggetti dell'interfaccia e testi da sostituire.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using UnityEngine;
using UnityEngine.UI;

namespace Melchior
{
    public class UiConfig
    {
        public bool SkipIntro = true;                 // salta il filmato iniziale
        public string Language = "Italiano";          // cartella in translations/ da attivare all'avvio; vuoto = non toccare
        public string[] HideObjects = Array.Empty<string>();   // percorsi (o loro parte finale) di oggetti da disattivare
        public string[] HideTextContaining = { "Steam" };       // nasconde le etichette che contengono questi testi
        public string[] HideTextExact = { "Partita protetta", "Secured Game" }; // etichette da nascondere per testo esatto
        public string[] HideImagesNamed = { "Steam" };          // nasconde le immagini il cui oggetto contiene questi nomi
        public bool HideWholeRow = true;                        // nasconde anche caselle e icone sulla stessa riga dell'etichetta
        public Dictionary<string, string> ReplaceText = new();  // testo esatto -> sostituto
    }

    static class Ui
    {
        public static UiConfig Cfg = new();
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static long next; static bool langDone; static int langTries;
        static readonly JsonSerializerOptions json = new()
        {
            IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static void Load(string dir)
        {
            string path = Path.Combine(dir, "melchior.ui.json");
            try
            {
                if (File.Exists(path)) Cfg = JsonSerializer.Deserialize<UiConfig>(File.ReadAllText(path), json) ?? new UiConfig();
                else File.WriteAllText(path, JsonSerializer.Serialize(Cfg, json));
            }
            catch (Exception e) { Plugin.L.LogError("melchior.ui.json non valido: " + e.Message); Cfg = new UiConfig(); }
        }

        public static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 12; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse(); return string.Join("/", parts);
        }

        public static void Tick()
        {
            if (clock.ElapsedMilliseconds < next) return;
            next = clock.ElapsedMilliseconds + 1000;
            try { Intro(); Language(); Sweep(); } catch (Exception e) { Plugin.L.LogError("[ui] " + e.Message); next += 30000; }
        }

        // Il filmato iniziale non parte sotto Wine e lascia lo schermo nero in attesa di un tasto: lo chiudo io.
        public static string Intro()
        {
            if (!Cfg.SkipIntro || PLGlobal.Instance == null || clock.ElapsedMilliseconds > 120000) return "";
            int stopped = 0;
            var vp = UnityEngine.Object.FindObjectsOfType<UnityEngine.Video.VideoPlayer>();
            for (int i = 0; i < vp.Length; i++) { if (vp[i] == null) continue; try { vp[i].Stop(); vp[i].gameObject.SetActive(false); stopped++; } catch { } }
            bool was = PLGlobal.IntroFinished;
            if (!was) PLGlobal.IntroFinished = true;
            string menu = "";
            try
            {
                var mm = PLNetworkManager.Instance?.MainMenu;
                if (mm != null) { menu = $", menu attivo {mm.IsActive()} abilitato {mm.MainMenuEnabled} ({mm.GetActiveMenuCount()})"; if (!mm.IsActive() && clock.ElapsedMilliseconds > 15000 && !PhotonNetwork.inRoom) { mm.Open(); menu += " -> aperto"; } }
            }
            catch (Exception e) { menu = ", menu: " + e.Message; }
            return $"IntroFinished era {was}, filmati fermati {stopped}{menu}";
        }

        static void Language()
        {
            if (langDone || Cfg.Language.Length == 0) return;
            var loc = PLLocalize.Instance;
            if (loc == null || clock.ElapsedMilliseconds < 6000) return;
            loc.LocalLocalLanguagesIfNotLoaded();
            string id = ResolveId(loc);
            if (id == null)
            {
                if (++langTries > 20) { langDone = true; Plugin.L.LogWarning($"[ui] lingua '{Cfg.Language}' non trovata. Disponibili: {Languages()}"); }
                return;
            }
            langDone = true;
            if (loc.CurrentLanguage != null && loc.CurrentLanguage.ID == id) return;
            loc.ChangeCurrentLanguage(id, false);
            Plugin.L.LogInfo($"[ui] lingua attiva: {Cfg.Language} (id {loc.CurrentLanguage?.ID})");
        }

        // Il gioco numera le lingue locali senza conservarne il nome: la riconosco confrontando alcune stringhe
        // del file translations/<Lingua>/translation.tsv con quelle caricate.
        static string ResolveId(PLLocalize loc)
        {
            var list = loc.LoadedLanguages;
            for (int i = 0; i < list.Count; i++) if (list[i].ID == Cfg.Language) return list[i].ID;
            string file = Path.Combine(BepInEx.Paths.GameRootPath, "translations", Cfg.Language, "translation.tsv");
            if (!File.Exists(file)) return null;
            var probes = new List<(int id, string text)>();
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                var c = line.Split('\t');
                if (c.Length >= 3 && int.TryParse(c[0], out int k) && c[2] != c[1] && c[2].Length > 3) probes.Add((k, c[2]));
                if (probes.Count >= 25) break;
            }
            if (probes.Count == 0) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i].Strings; int hit = 0;
                foreach (var (k, text) in probes) if (d.ContainsKey(k) && d[k] == text) hit++;
                if (hit >= probes.Count - 2) return list[i].ID;
            }
            return null;
        }

        public static string Languages()
        {
            var loc = PLLocalize.Instance; if (loc == null) return "(non pronto)";
            var l = loc.LoadedLanguages; var sb = new StringBuilder();
            for (int i = 0; i < l.Count; i++) sb.Append(l[i].ID).Append(" (").Append(l[i].Strings.Count).Append(") ");
            return sb + "| attiva: " + (loc.CurrentLanguage?.ID ?? "nessuna");
        }

        // Passata periodica sulle etichette attive: nasconde o sostituisce i testi configurati.
        static void Sweep()
        {
            if (Cfg.HideTextContaining.Length == 0 && Cfg.ReplaceText.Count == 0 && Cfg.HideObjects.Length == 0) return;
            var all = UnityEngine.Object.FindObjectsOfType<Text>();
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i]; if (t == null) continue;
                string s = t.text; if (string.IsNullOrEmpty(s)) continue;
                if (Cfg.ReplaceText.TryGetValue(s, out var rep)) { t.text = rep; continue; }
                if (Array.IndexOf(Cfg.HideTextExact, s) >= 0) { Hide(t); continue; }
                foreach (var h in Cfg.HideTextContaining)
                    if (s.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0) { Hide(t); break; }
            }
            if (Cfg.HideImagesNamed.Length > 0 && ++sweeps % 3 == 0)
            {
                var imgs = UnityEngine.Object.FindObjectsOfType<Image>();
                for (int i = 0; i < imgs.Length; i++)
                {
                    var im = imgs[i]; if (im == null) continue;
                    string n = im.gameObject.name;
                    foreach (var h in Cfg.HideImagesNamed)
                        if (n.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (reported.Add("img:" + PathOf(im.transform))) Plugin.L.LogInfo($"[ui] nascosta immagine: {PathOf(im.transform)}");
                            im.gameObject.SetActive(false); break;
                        }
                }
            }
            foreach (var p in Cfg.HideObjects)
            {
                var go = GameObject.Find(p);
                if (go != null && go.activeSelf) go.SetActive(false);
            }
        }

        static readonly HashSet<string> reported = new(); static int sweeps;
        static void Hide(Text t)
        {
            string path = PathOf(t.transform);
            if (reported.Add(path)) Plugin.L.LogInfo($"[ui] nascosto: {path}  «{t.text.Replace("\n", " ")}»");
            var tr = t.transform; var parent = tr.parent;
            if (Cfg.HideWholeRow && parent != null)
            {
                float y = tr.localPosition.y;
                for (int i = 0; i < parent.childCount; i++)
                {
                    var c = parent.GetChild(i);
                    if (c == tr || !c.gameObject.activeSelf || Math.Abs(c.localPosition.y - y) > 14f) continue;
                    if (c.GetComponent<Text>() != null) continue; // le altre etichette si giudicano da sole
                    if (reported.Add("row:" + PathOf(c))) Plugin.L.LogInfo($"[ui] nascosto con la riga: {PathOf(c)}");
                    c.gameObject.SetActive(false);
                }
            }
            t.gameObject.SetActive(false);
        }

        public static string Tree(string path)
        {
            var go = GameObject.Find(path);
            if (go == null) return "oggetto non trovato";
            var sb = new StringBuilder(); var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i); var tx = c.GetComponent<Text>();
                sb.Append($"\n      {(c.gameObject.activeSelf ? "+" : "-")} {c.name} [{c.childCount}] {(tx != null ? "«" + tx.text.Replace("\n", " ") + "»" : "")}");
            }
            return sb.ToString();
        }

        // ---- strumenti di collaudo: premere pulsanti e leggere l'interfaccia senza mouse né tastiera
        static string LabelOf(Component c) { var t = c.GetComponentInChildren<Text>(); return t == null || t.text == null ? "" : t.text.Replace("\n", " "); }

        public static string Buttons()
        {
            var sb = new StringBuilder(); var all = UnityEngine.Object.FindObjectsOfType<Button>();
            for (int i = 0; i < all.Length && i < 80; i++)
                sb.Append($"\n      {PathOf(all[i].transform)}  «{LabelOf(all[i])}»{(all[i].interactable ? "" : " (spento)")}");
            var tg = UnityEngine.Object.FindObjectsOfType<Toggle>();
            for (int i = 0; i < tg.Length && i < 40; i++) sb.Append($"\n      [casella {(tg[i].isOn ? "x" : " ")}] {PathOf(tg[i].transform)}");
            return sb.Length == 0 ? "nessun pulsante attivo" : sb.ToString();
        }

        // Preme il primo pulsante attivo la cui etichetta o il cui percorso contiene il testo dato.
        public static string Press(string what)
        {
            var all = UnityEngine.Object.FindObjectsOfType<Button>(); Button hit = null;
            for (int i = 0; i < all.Length && hit == null; i++) if (string.Equals(LabelOf(all[i]).Trim(), what, StringComparison.OrdinalIgnoreCase)) hit = all[i];
            for (int i = 0; i < all.Length && hit == null; i++) if (PathOf(all[i].transform).EndsWith(what, StringComparison.OrdinalIgnoreCase)) hit = all[i];
            for (int i = 0; i < all.Length && hit == null; i++) if (PathOf(all[i].transform).IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0) hit = all[i];
            if (hit == null) return "pulsante non trovato";
            hit.onClick.Invoke();
            return "premuto " + PathOf(hit.transform);
        }

        public static string TypeInto(string field, string text)
        {
            var all = UnityEngine.Object.FindObjectsOfType<InputField>();
            for (int i = 0; i < all.Length; i++)
                if (PathOf(all[i].transform).IndexOf(field, StringComparison.OrdinalIgnoreCase) >= 0 || field == "*") { all[i].text = text; return "scritto in " + PathOf(all[i].transform); }
            return "campo non trovato";
        }

        // Ispezione dettagliata di un ramo dell'interfaccia uGUI: posizione, dimensioni, ancore, carattere, colori, sprite.
        public static string Info(string path, int depth = 2)
        {
            var go = GameObject.Find(path); if (go == null) return "oggetto non trovato";
            var sb = new StringBuilder(); void Walk(Transform t, int d, string ind)
            {
                var rt = t.TryCast<RectTransform>(); string geo = rt != null ? $"pos {rt.anchoredPosition} size {rt.sizeDelta} anc {rt.anchorMin}-{rt.anchorMax} piv {rt.pivot}" : $"pos {t.localPosition}";
                var tx = t.GetComponent<Text>(); var im = t.GetComponent<Image>(); var ri = t.GetComponent<RawImage>();
                sb.Append($"\n{ind}{(t.gameObject.activeSelf ? "+" : "-")} {t.name} [{geo} scala {t.localScale}]");
                if (tx != null) sb.Append($" TEXT font {tx.font?.name} {tx.fontSize} {tx.fontStyle} col {tx.color} allinea {tx.alignment} «{tx.text?.Replace("\n", "⏎")}»");
                if (im != null) sb.Append($" IMAGE sprite {im.sprite?.name} tipo {im.type} col {im.color}");
                if (ri != null) sb.Append($" RAW tex {ri.texture?.name} col {ri.color}");
                if (d < depth) for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), d + 1, ind + "  ");
            }
            Walk(go.transform, 0, "      "); return sb.ToString();
        }
        public static string FindText(string needle)
        {
            var sb = new StringBuilder(); int n = 0;
            var all = Resources.FindObjectsOfTypeAll<Text>();
            for (int i = 0; i < all.Length && n < 60; i++)
            {
                var t = all[i]; if (t == null || t.text == null) continue;
                if (t.text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                n++; sb.Append($"\n      {(t.gameObject.activeInHierarchy ? "visibile " : "nascosto ")} {PathOf(t.transform)}  «{t.text.Replace("\n", " ")}»");
            }
            return n == 0 ? "nessuna etichetta" : sb.ToString();
        }
    }
}

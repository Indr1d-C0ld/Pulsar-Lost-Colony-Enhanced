// Rivestimento dell'HUD di gioco nello stesso linguaggio del riquadro in alto a sinistra (salute, ossigeno, equipaggio):
// niente riquadri scuri, ma pillole piene dagli angoli arrotondati con il valore scritto dentro in bianco, tutte della
// stessa larghezza in una colonna ordinata, e accanto l'etichetta nel colore della pillola. Lo stesso carattere del
// riquadro di sinistra, con un contorno scuro che lo rende leggibile su qualsiasi sfondo (stelle, nebulose, paratie).
//  - elenco NAVI: nome nel colore del rapporto (nostra, ostile, civile, alleata), pillola dello scafo con il valore,
//    pillola sottile degli scudi sopra, indicatore d'allarme arrotondato; titolo in pillola;
//  - promemoria dei tasti: tasti in pillole uguali, descrizioni in colonna;
//  - oggetti in mano: pillole uguali con numero e nome, quella attiva in ambra;
//  - crediti: pillola dorata con il valore, totale sotto.
// Si applica dopo l'aggiornamento nativo dell'HUD (postfix su PLInGameUI.Update): la logica del gioco non cambia.
// Si disattiva da configurazione ([Interfaccia] HudRivestito) o in console (hud on|off).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Melchior
{
    public static class HudSkin
    {
        public static bool Enabled = true;
        static int errors; public static long Frames;

        // ---- tavolozza: i colori pieni del riquadro di sinistra
        static readonly Color Amber = new(0.80f, 0.50f, 0.05f, 1f), Gold = new(0.86f, 0.70f, 0.10f, 1f), Blue = new(0.26f, 0.42f, 0.66f, 1f);
        static readonly Color Green = new(0.24f, 0.62f, 0.22f, 1f), Red = new(0.66f, 0.10f, 0.08f, 1f), Steel = new(0.30f, 0.33f, 0.38f, 0.92f), Track = new(0.16f, 0.17f, 0.19f, 0.85f);
        static readonly Color Hostile = new(1f, 0.42f, 0.36f, 1f), Civil = new(0.62f, 0.84f, 1f, 1f), Ally = new(0.52f, 0.92f, 0.50f, 1f), Neutral = new(0.86f, 0.87f, 0.90f, 1f), Ours = new(1f, 0.74f, 0.30f, 1f);
        static readonly Color Shield = new(0.36f, 0.66f, 1f, 1f);
        // impaginazione: tutte le parti a destra seguono lo stesso margine; le pillole hanno larghezze fisse
        const float R = 14f, PillW = 132f, KeyW = 74f, RowH = 21f;

        // ---- sprite: rettangolo arrotondato come le barre di sinistra
        // lo stesso sprite delle barre di sinistra (TabMenuButton); di riserva il nostro rettangolo arrotondato
        static Sprite round;
        static Sprite Round
        {
            get
            {
                if (round != null) return round;
                try { var im = GameObject.Find("InGameUI/UIMarginScalar_VRLeft/HPTitle/HPFill")?.GetComponent<Image>(); if (im?.sprite != null) return round = im.sprite; } catch { }
                var t = Look.Round; round = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(7, 7, 7, 7)); round.hideFlags = HideFlags.HideAndDontSave; return round;
            }
        }
        // caratteri del riquadro di sinistra: MainFont 14 per nomi e testi, 13 dentro le barre, MainFontBold 14 per i titoli
        static Font font, bold; const int Size = 14, InBar = 13;
        static void F(PLInGameUI ui)
        {
            if (font == null) font = ui.HealthValueLabel?.font;
            if (bold == null) { try { bold = GameObject.Find("InGameUI/UIMarginScalar_VRLeft/HPTitle")?.GetComponent<Text>()?.font; } catch { } bold ??= font; }
            Left();
        }
        static void Std(Text t, int size = Size, bool title = false) { if (t == null) return; var f = title ? bold : font; if (f != null && t.font != f) t.font = f; if (t.fontSize != size) t.fontSize = size; if (t.fontStyle != FontStyle.Normal) t.fontStyle = FontStyle.Normal; Contour(t); }
        // anche i testi di sinistra prendono il contorno: tutto l'HUD si legge allo stesso modo su qualsiasi sfondo
        static float nextLeft;
        static void Left()
        {
            if (Time.time < nextLeft) return; nextLeft = Time.time + 3f;
            try { var root = GameObject.Find("InGameUI/UIMarginScalar_VRLeft"); if (root == null) return; foreach (var t in root.GetComponentsInChildren<Text>(true)) { if (t == null || t.name.StartsWith("Chat")) continue; Contour(t); } } catch { }
        }

        // ---- costruttori
        static Image Pill(Transform parent, string name, Color c, int sibling = -1)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false); go.layer = parent.gameObject.layer;
            go.AddComponent<RectTransform>(); var im = go.AddComponent<Image>(); im.sprite = Round; im.type = Image.Type.Sliced; im.color = c; im.raycastTarget = false;
            if (sibling >= 0) go.transform.SetSiblingIndex(sibling); return im;
        }
        static Text Label(Transform parent, string name, int size, Color c, TextAnchor a)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false); go.layer = parent.gameObject.layer;
            go.AddComponent<RectTransform>(); var t = go.AddComponent<Text>(); t.font = font; t.fontSize = size; t.color = c; t.alignment = a; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow; t.supportRichText = true; t.fontStyle = FontStyle.Normal;
            Contour(t); return t;
        }
        // contorno scuro: il testo si legge su qualsiasi sfondo
        static readonly HashSet<IntPtr> outlined = new();
        static void Contour(Text t)
        {
            if (t == null || !outlined.Add(t.Pointer)) return;
            var o = t.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0f, 0f, 0f, 0.75f); o.effectDistance = new Vector2(1f, -1f);
        }
        static void Place(Graphic g, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size) { var rt = g.rectTransform; rt.anchorMin = rt.anchorMax = anchor; rt.pivot = pivot; rt.anchoredPosition = pos; rt.sizeDelta = size; }
        static void Stretch(Graphic g) { var rt = g.rectTransform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f); rt.offsetMin = rt.offsetMax = Vector2.zero; }
        static void Show(Graphic g, bool on) { if (g != null && g.enabled != on) g.enabled = on; }

        // ---- punto d'ingresso
        internal static void After(PLInGameUI ui)
        {
            if (!Enabled || ui == null || errors > 20) return;
            try { F(ui); Ships(ui); Controls(ui); Items(ui); Credits(ui); Frames++; }
            catch (Exception e) { if (++errors <= 3 || errors == 20) Plugin.L.LogWarning("HUD: " + e.Message + (errors == 20 ? " (rivestimento sospeso)" : "")); }
        }

        // ================================================================ elenco navi
        sealed class ShipSkin { public Text value; }
        static readonly Dictionary<IntPtr, ShipSkin> ships = new(); static Image titlePill; static Text titleText; static float? upperX;
        static Color Relation(PLShipInfoBase s, out bool mine)
        {
            mine = false; var me = Stations.MyShip; if (s == null || me == null) return Neutral;
            if (s.ShipID == me.ShipID) { mine = true; return Ours; }
            try { if (s.HostileShips.Contains(me.ShipID) || (s.TargetShip != null && s.TargetShip.ShipID == me.ShipID)) return Hostile; } catch { }
            if (Traffic.IsAlly(s)) return Ally;
            if (Traffic.Peaceful(s, me)) return Civil;
            try { if (s.TeamID != me.TeamID) return Hostile; } catch { }
            return Neutral;
        }
        static void Ships(PLInGameUI ui)
        {
            var list = ui.AllDisplayRightShipRightInfos; if (list == null) return;
            var upper = ui.ShipInfoGrid?.parent?.TryCast<RectTransform>(); if (upper != null) { upperX ??= upper.anchoredPosition.x; upper.anchoredPosition = new Vector2(upperX.Value - R, upper.anchoredPosition.y); }
            var title = ui.ShipsTitle;
            if (title != null)
            {
                if (titlePill == null) { titlePill = Pill(title.transform.parent, "MelchiorTitolo", Steel); titleText = Label(titlePill.transform, "Testo", Size, Color.white, TextAnchor.MiddleCenter); Stretch(titleText); }
                Std(titleText, Size, true);
                title.canvasRenderer.SetAlpha(0f);
                var first = list.Count > 0 ? list[0]?.Root?.transform : null; float topY = first != null ? first.localPosition.y + 30f : 20f;
                Place(titlePill, new Vector2(0.5f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, topY), new Vector2(96f, 22f));
                int n = 0; for (int i = 0; i < list.Count; i++) if (list[i]?.Root != null && list[i].Root.activeInHierarchy) n++;
                titleText.text = $"NAVI  {n}";
            }
            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i]; var root = d?.Root; if (root == null || !root.activeInHierarchy) continue;
                if (!ships.TryGetValue(root.Pointer, out var sk) || sk.value == null) sk = ships[root.Pointer] = SkinShip(root.transform);
                if (sk == null) continue;
                var col = Relation(d.Ship, out bool mine);
                if (d.NameLabel != null) { float a = Math.Max(0.65f, d.NameLabel.color.a); d.NameLabel.color = new Color(col.r, col.g, col.b, a); Std(d.NameLabel); }
                float hull = 1f, hc0 = 0f, hm = 0f; try { var st = d.Ship?.MyStats; if (st != null && st.HullMax > 0) { hc0 = st.HullCurrent; hm = st.HullMax; hull = hc0 / hm; } } catch { }
                var fill = mine ? Gold : hull > 0.6f ? Green : hull > 0.3f ? Amber : Red;
                Bar(d.HullBar, fill); Bar(d.HullBarSlow, new Color(1f, 1f, 1f, 0.22f)); Bar(d.ShldBar, Shield); Bar(d.ShldBarSlow, new Color(0.8f, 0.92f, 1f, 0.25f));
                if (d.AlertTexture != null && d.AlertTexture.sprite != Round) { d.AlertTexture.sprite = Round; d.AlertTexture.type = Image.Type.Sliced; d.AlertTexture.rectTransform.sizeDelta = new Vector2(6f, 22f); }
                // il valore solo se il gioco mostra la barra (nave scansionata): niente informazioni regalate
                bool known = d.HullBar != null && d.HullBar.rectTransform.sizeDelta.x > 0.5f;
                sk.value.text = hm > 0 && known ? $"{hc0:0}/{hm:0}" : "";
                var bgi = root.transform.Find("BG")?.GetComponent<Image>(); if (bgi != null && bgi.color.a > 0f) bgi.color = new Color(0, 0, 0, 0);
            }
            if (ships.Count > 64) foreach (var k in ships.Where(x => x.Value?.value == null).Select(x => x.Key).ToList()) ships.Remove(k);
        }
        static void Bar(Image im, Color c) { if (im == null) return; if (im.sprite != Round) { im.sprite = Round; im.type = Image.Type.Sliced; } im.color = c; }
        static ShipSkin SkinShip(Transform root)
        {
            var sk = new ShipSkin();
            // niente riquadro: lo sfondo nativo diventa trasparente, come a sinistra
            var bg = root.Find("BG")?.GetComponent<Image>(); if (bg != null) bg.color = new Color(0, 0, 0, 0);
            foreach (var n in new[] { "ShldBarBG", "HullBarBG" }) { var im = root.Find(n)?.GetComponent<Image>(); if (im != null) { im.sprite = Round; im.type = Image.Type.Sliced; im.color = Track; } }
            // il valore dello scafo scritto dentro la sua pillola, come «100/100» a sinistra
            var hb = root.Find("HullBarBG")?.TryCast<RectTransform>(); if (hb == null) return null;
            sk.value = Label(root, "MelchiorValore", InBar, Color.white, TextAnchor.MiddleCenter);
            var vrt = sk.value.rectTransform; vrt.anchorMin = hb.anchorMin; vrt.anchorMax = hb.anchorMax; vrt.pivot = hb.pivot; vrt.anchoredPosition = hb.anchoredPosition; vrt.sizeDelta = hb.sizeDelta;
            sk.value.transform.SetAsLastSibling();
            return sk;
        }

        // ================================================================ promemoria dei tasti
        static readonly Dictionary<string, string> Words = new() { ["flashlight"] = "torcia", ["sprint"] = "scatto", ["usa lo jetpack"] = "jetpack", ["jump"] = "salto", ["crouch"] = "accovacciati", ["interact"] = "interagisci", ["reload"] = "ricarica" };
        static readonly Dictionary<string, string> Keys = new() { ["Space"] = "Spazio", ["LEFTSHIFT"] = "Maiusc", ["RIGHTSHIFT"] = "Maiusc dx", ["LEFTCONTROL"] = "Ctrl", ["LEFTALT"] = "Alt", ["MOUSE0"] = "Clic sx", ["MOUSE1"] = "Clic dx", ["MOUSE2"] = "Rotella", ["MOUSE3"] = "Mouse 3", ["MOUSE4"] = "Mouse 4", ["ESCAPE"] = "Esc", ["RETURN"] = "Invio", ["BACKSPACE"] = "Canc" };
        static readonly Dictionary<IntPtr, Image> keyPills = new();
        static void Controls(PLInGameUI ui)
        {
            var ct = ui.ControlsText; if (ct == null) return; var t = ct.transform; var crt0 = ct.rectTransform;
            // colonna: pillole dei tasti a sinistra, tutte uguali; descrizioni subito dopo
            float colRight = -crt0.anchoredPosition.x - R, pillLeft = colRight - PillW - KeyW - 8f;
            for (int i = 0; i < t.childCount; i++)
            {
                var line = t.GetChild(i); if (line.name != "ControlsLine") continue;
                var lt = line.GetComponent<Text>(); var key = line.Find("InputLabel")?.GetComponent<Text>(); var icon = line.Find("InputImage");
                bool on = lt != null && line.gameObject.activeSelf && !string.IsNullOrEmpty(lt.text);
                if (!on) { if (key != null && keyPills.TryGetValue(key.Pointer, out var hp) && hp != null) hp.enabled = false; continue; }
                if (Words.TryGetValue(lt.text, out var it)) lt.text = it;
                var lrt = lt.rectTransform; lt.color = Color.white; lt.alignment = TextAnchor.MiddleLeft; Std(lt);
                float textLeft = pillLeft + KeyW + 8f; lrt.anchoredPosition = new Vector2(textLeft + lrt.sizeDelta.x * lrt.pivot.x, lrt.anchoredPosition.y);
                float lineCx = lrt.anchoredPosition.x + lrt.sizeDelta.x * (0.5f - lrt.pivot.x), keyCx = pillLeft + KeyW * 0.5f;
                bool hasKey = key != null && key.gameObject.activeSelf && !string.IsNullOrEmpty(key.text);
                if (hasKey)
                {
                    if (Keys.TryGetValue(key.text, out var kn)) key.text = kn;
                    if (!keyPills.TryGetValue(key.Pointer, out var pill) || pill == null) pill = keyPills[key.Pointer] = Pill(lt.transform, "MelchiorTasto", Blue, key.transform.GetSiblingIndex());
                    pill.enabled = true; key.color = Color.white; Std(key, InBar); key.alignment = TextAnchor.MiddleCenter;
                    var krt = key.rectTransform; krt.anchoredPosition = new Vector2(keyCx - lineCx, 0f);
                    Place(pill, krt.anchorMin, new Vector2(0.5f, 0.5f), krt.anchoredPosition, new Vector2(KeyW, 20f));
                }
                if (icon != null && icon.gameObject.activeSelf) { var irt2 = icon.TryCast<RectTransform>(); if (irt2 != null) irt2.anchoredPosition = new Vector2(keyCx - lineCx - (hasKey ? KeyW * 0.5f + 12f : 0f), 0f); }
            }
        }

        // ================================================================ oggetti in mano
        sealed class Slot { public Image pill; public Text text; }
        static readonly List<Slot> slots = new(); static readonly Regex Line = new(@"(?:<color=#(?<c>[0-9A-Fa-f]{6,8})>)?(?:<size=(?<s>\d+)>)?\s*(?<n>.*?)\s+(?<k>\d+)\s*(?:</size>)?(?:</color>)?\s*$");
        static void Items(PLInGameUI ui)
        {
            var info = ui.ActiveItemLabel; if (info == null) return;
            var rows = (info.text ?? "").Split('\n').Where(x => x.Trim().Length > 0).ToList();
            info.canvasRenderer.SetAlpha(0f); // il testo nativo resta (lo aggiorna il gioco), invisibile
            var parent = info.transform.parent; var irt = info.rectTransform;
            while (slots.Count < rows.Count)
            {
                var s = new Slot { pill = Pill(parent, "MelchiorSlot", Steel, info.transform.GetSiblingIndex()) };
                s.text = Label(s.pill.transform, "Testo", Size, Color.white, TextAnchor.MiddleLeft);
                var rt = s.text.rectTransform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(9f, 0f); rt.offsetMax = new Vector2(-6f, 0f); slots.Add(s);
            }
            float top = irt.anchoredPosition.y + irt.sizeDelta.y * (1f - irt.pivot.y) + 14f;
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i]; bool vis = i < rows.Count; Show(s.pill, vis); Show(s.text, vis); if (!vis) continue;
                var m = Line.Match(rows[i]); string name = m.Success ? m.Groups["n"].Value : Regex.Replace(rows[i], "<.*?>", "").Trim(); string key = m.Success ? m.Groups["k"].Value : (i + 1).ToString();
                bool active = m.Success && m.Groups["s"].Success;
                Place(s.pill, irt.anchorMin, new Vector2(1f, 1f), new Vector2(-R, top - i * (RowH + 3f)), new Vector2(PillW + KeyW + 8f, RowH));
                s.pill.color = active ? Amber : Steel; s.text.text = $"{key}   {name}"; s.text.color = active ? Color.white : new Color(0.88f, 0.90f, 0.94f, 0.9f);
            }
            var ammo = ui.ActiveItemAmmo; if (ammo != null) { ammo.color = new Color(1f, 0.80f, 0.35f, 1f); Std(ammo, Size); }
        }

        // ================================================================ crediti
        static Image credPill; static Text credValue, credTotal; static readonly Regex Num = new(@"^\s*(?<v>[^\n<]+?)\s*(?:\n|<).*?(?<t>[\d\.,]+)\s*totale", RegexOptions.Singleline);
        static void Credits(PLInGameUI ui)
        {
            var cv = ui.CreditsValue; if (cv == null) return; var crt = cv.rectTransform;
            if (credPill == null)
            {
                credPill = Pill(cv.transform.parent, "MelchiorCrediti", Gold, cv.transform.GetSiblingIndex());
                credValue = Label(credPill.transform, "Valore", Size, Color.white, TextAnchor.MiddleCenter); Stretch(credValue);
                credTotal = Label(cv.transform.parent, "MelchiorTotale", InBar, new Color(1f, 0.88f, 0.55f, 0.95f), TextAnchor.UpperRight);
            }
            cv.canvasRenderer.SetAlpha(0f);
            string raw = cv.text ?? ""; var m = Num.Match(raw); string value = m.Success ? m.Groups["v"].Value : Regex.Replace(raw.Split('\n')[0], "<.*?>", ""); string total = m.Success ? m.Groups["t"].Value + " totale" : "";
            float y = crt.anchoredPosition.y;
            Place(credPill, crt.anchorMin, new Vector2(1f, 0.5f), new Vector2(-R, y + 8f), new Vector2(PillW + KeyW + 8f, RowH + 3f));
            Place(credTotal, crt.anchorMin, new Vector2(1f, 1f), new Vector2(-R - 4f, y - 6f), new Vector2(PillW + KeyW, 14f));
            credValue.text = value; credTotal.text = total;
            var ver = ui.CurrentVersionLabel; if (ver != null) { ver.color = new Color(0.62f, 0.66f, 0.74f, 0.8f); Std(ver, InBar); }
        }

        public static string Describe() => $"HUD rivestito {(Enabled ? "attivo" : "spento")}: fotogrammi {Frames}, errori {errors}, navi {ships.Count}, tasti {keyPills.Count}, slot {slots.Count}, carattere {font?.name}/{bold?.name} {Size}";
    }

    [HarmonyPatch(typeof(PLInGameUI), nameof(PLInGameUI.Update))]
    static class HudSkinPatch { static void Postfix(PLInGameUI __instance) => HudSkin.After(__instance); }
}

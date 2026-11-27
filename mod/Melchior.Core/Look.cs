// Aspetto delle schede di stazione: font da terminale (atlante bitmap cotto da JetBrains Mono) e texture generate
// al volo per un'interfaccia LCARS futuristica: angoli arrotondati con riflesso, gomiti LCARS, barre a segmenti,
// cornici luminose, griglia di fondo, righe di scansione, vignettatura e un'onda di scansione animata.
// Tutte le texture sono in scala di grigi con l'alfa della forma: il colore lo dà il widget che le usa.
using System;
using System.IO;
using System.Text.Json;
using BepInEx;
using UnityEngine;

namespace Melchior
{
    public static class Look
    {
        // ---- font da terminale
        static UIFont font; static bool tried;
        public static UIFont Terminal
        {
            get
            {
                if (font != null || tried) return font; tried = true;
                try
                {
                    string dir = Path.Combine(Paths.PluginPath, "Melchior", "fonts");
                    var png = File.ReadAllBytes(Path.Combine(dir, "terminale.png"));
                    using var js = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "terminale.json")));
                    var r = js.RootElement;
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { hideFlags = HideFlags.HideAndDontSave };
                    ImageConversion.LoadImage(tex, png, false);
                    tex.filterMode = FilterMode.Trilinear; tex.mipMapBias = -0.5f; tex.anisoLevel = 4; tex.wrapMode = TextureWrapMode.Clamp;
                    var go = new GameObject("MelchiorTerminale") { hideFlags = HideFlags.HideAndDontSave }; UnityEngine.Object.DontDestroyOnLoad(go);
                    var f = go.AddComponent<UIFont>();
                    var sh = Shader.Find("Unlit/Transparent Colored") ?? Shader.Find("Unlit/Transparent");
                    var mat = new Material(sh) { mainTexture = tex, hideFlags = HideFlags.HideAndDontSave };
                    var bm = f.bmFont;
                    bm.mSize = r.GetProperty("line").GetInt32(); // dimensione = altezza di riga: il testo resta nelle sue colonne bm.mBase = r.GetProperty("base").GetInt32();
                    bm.mWidth = r.GetProperty("w").GetInt32(); bm.mHeight = r.GetProperty("h").GetInt32();
                    foreach (var g in r.GetProperty("glyphs").EnumerateArray())
                    {
                        var gl = bm.GetGlyph(g.GetProperty("id").GetInt32(), true);
                        gl.x = g.GetProperty("x").GetInt32(); gl.y = g.GetProperty("y").GetInt32();
                        gl.width = g.GetProperty("w").GetInt32(); gl.height = g.GetProperty("h").GetInt32();
                        gl.offsetX = g.GetProperty("ox").GetInt32(); gl.offsetY = g.GetProperty("oy").GetInt32();
                        gl.advance = g.GetProperty("adv").GetInt32() - 1; // spaziatura un filo più stretta: si legge meglio in colonne strette
                        gl.channel = 0;
                    }
                    f.material = mat; f.MarkAsChanged();
                    font = f; Plugin.L.LogInfo($"[aspetto] font da terminale caricato: {bm.glyphCount} glifi");
                }
                catch (Exception e) { Plugin.L.LogWarning("Font da terminale non disponibile: " + e.Message); }
                return font;
            }
        }
        public static void Apply(UILabel l)
        {
            var f = Terminal; if (f == null || l == null) return;
            try { l.bitmapFont = f; } catch { }
        }

        // ---- texture
        static Texture2D Make(int w, int h, Func<float, float, float> alpha, Func<float, float, float> shade = null, int ss = 4)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float a = 0f;
                    for (int sy = 0; sy < ss; sy++) for (int sx = 0; sx < ss; sx++) a += alpha(x + (sx + 0.5f) / ss, h - (y + (sy + 0.5f) / ss)); // y dall'alto
                    a /= ss * ss; float s = shade?.Invoke(x + 0.5f, h - y - 0.5f) ?? 1f;
                    byte v = (byte)Math.Clamp(s * 255f, 0f, 255f);
                    px[y * w + x] = new Color32(v, v, v, (byte)Math.Clamp(a * 255f, 0f, 255f));
                }
            t.SetPixels32(px); t.Apply(false, false); return t;
        }
        // distanza con segno da un rettangolo arrotondato (in pixel, y dall'alto)
        static float RR(float x, float y, float w, float h, float r)
        {
            float qx = Math.Abs(x - w / 2f) - (w / 2f - r), qy = Math.Abs(y - h / 2f) - (h / 2f - r);
            return (float)Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0f) - r;
        }
        static float In(float d) => d <= 0 ? 1f : 0f;
        // riflesso verticale: più chiaro in alto, filo di luce sul bordo superiore
        static float Sheen(float y, float h) => y < 1.5f ? 1f : 0.97f - 0.16f * (y / h);

        static Texture2D round, small, pill, frame, seg, grid, vignette, scan, glow, elbow, fade, sweep;
        public static Texture2D Round => round ??= Make(24, 24, (x, y) => In(RR(x, y, 24, 24, 7)), (x, y) => Sheen(y, 24));
        public static Texture2D Small => small ??= Make(10, 10, (x, y) => In(RR(x, y, 10, 10, 3)));
        public static Texture2D Pill => pill ??= Make(44, 22, (x, y) => In(RR(x, y, 44, 22, 11)), (x, y) => Sheen(y, 22));
        public static Texture2D Frame => frame ??= Make(24, 24, (x, y) => { float d = RR(x, y, 24, 24, 7); return d <= 0 && d > -1.4f ? 1f : 0f; });
        public static Texture2D Seg => seg ??= Make(6, 16, (x, y) => x < 4.5f ? 1f : 0f, (x, y) => y < 2f ? 1f : 0.92f - 0.25f * (y / 16f), 1);
        public static Texture2D Grid => grid ??= Make(32, 32, (x, y) => (x < 1f || y < 1f) ? 1f : ((int)x % 8 == 0 && (int)y % 8 == 0) ? 1f : 0f, null, 1);
        public static Texture2D Vignette => vignette ??= Make(64, 64, (x, y) => { float dx = (x - 32f) / 32f, dy = (y - 32f) / 32f; float r = (float)Math.Sqrt(dx * dx + dy * dy); return Math.Clamp((r - 0.55f) / 0.9f, 0f, 1f); }, null, 1);
        public static Texture2D Scan => scan ??= Make(2, 3, (x, y) => y < 1f ? 1f : 0f, null, 1);
        public static Texture2D Glow => glow ??= Make(32, 32, (x, y) => { float dx = (x - 16f) / 16f, dy = (y - 16f) / 16f; float r = (float)Math.Sqrt(dx * dx + dy * dy); return (float)Math.Pow(Math.Clamp(1f - r, 0f, 1f), 1.6); }, null, 1);
        public static Texture2D Fade => fade ??= Make(64, 2, (x, y) => (float)Math.Sin(Math.PI * x / 64f), null, 1);
        public static Texture2D Sweep => sweep ??= Make(2, 32, (x, y) => (float)Math.Pow(Math.Sin(Math.PI * y / 32f), 3), null, 1);
        // gomito LCARS: braccio verticale largo 10, braccio orizzontale alto 24, angolo esterno tondo, raccordo interno concavo
        public const int ElbowW = 40, ElbowH = 52, Side = 10, Bar = 24;
        public static Texture2D Elbow => elbow ??= Make(ElbowW, ElbowH, (x, y) =>
        {
            bool inV = x < Side, inH = y < Bar; bool fill = inV || inH;
            const float ro = 18f; if (x < ro && y < ro && (x - ro) * (x - ro) + (y - ro) * (y - ro) > ro * ro) fill = false;
            const float ri = 8f; if (!fill && x >= Side && x < Side + ri && y >= Bar && y < Bar + ri && (x - Side - ri) * (x - Side - ri) + (y - Bar - ri) * (y - Bar - ri) > ri * ri) fill = true;
            return fill ? 1f : 0f;
        }, (x, y) => x < Side ? 0.96f : Sheen(y, Bar));

        // ---- applicazione ai widget
        public static void Sliced(UITexture t, Texture2D tex, float b) { if (t == null) return; t.mainTexture = tex; t.type = UIBasicSprite.Type.Sliced; t.border = new Vector4(b, b, b, b); }
        public static void Tiled(UITexture t, Texture2D tex) { if (t == null) return; t.mainTexture = tex; t.type = UIBasicSprite.Type.Tiled; }
        public static void Simple(UITexture t, Texture2D tex) { if (t == null) return; t.mainTexture = tex; t.type = UIBasicSprite.Type.Simple; }
    }
}

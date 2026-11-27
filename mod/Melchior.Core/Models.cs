// Importatore di modelli 3D: carica file GLB (glTF 2.0 binario, il formato di Thrixel e di quasi tutti i generatori)
// dalla cartella BepInEx/plugins/Melchior/models e li trasforma in oggetti del gioco, senza librerie esterne.
// - Geometria: POSITION, NORMAL, TEXCOORD_0, indici (8, 16, 32 bit); le primitive non indicizzate sono supportate.
// - Materiali: colore base, texture del colore base (PNG o JPG incorporate), metallicità e ruvidezza, emissione.
// - Gerarchia: nodi con traslazione, rotazione e scala (o matrice), figli, scena predefinita.
// - Assi: glTF è destrorso, Unity sinistrorso: si specchia la X e si inverte il verso dei triangoli; la V delle
//   coordinate di texture si capovolge.
// Agganci (models.json nella stessa cartella): ogni voce mette un modello su uno schermo di postazione o accanto alla
// nave, con posizione, rotazione, scala ed eventualmente nascondendo il modello originale.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using UnityEngine;

namespace Melchior
{
    public static class Models
    {
        public static string Dir => Path.Combine(Paths.PluginPath, "Melchior", "models");
        static readonly Dictionary<string, GameObject> protos = new();
        public static readonly List<GameObject> Shown = new();

        // ---- lettura del GLB
        sealed class Glb
        {
            public JsonElement J; public byte[] Bin = Array.Empty<byte>();
            public JsonElement Arr(string name) => J.TryGetProperty(name, out var a) ? a : default;
        }
        static Glb Parse(byte[] b)
        {
            if (b.Length < 20 || BitConverter.ToUInt32(b, 0) != 0x46546C67) throw new Exception("non è un file GLB");
            if (BitConverter.ToUInt32(b, 4) != 2) throw new Exception("serve glTF 2.0");
            int off = 12; var g = new Glb(); bool json = false;
            while (off + 8 <= b.Length)
            {
                int len = (int)BitConverter.ToUInt32(b, off); uint type = BitConverter.ToUInt32(b, off + 4); off += 8;
                if (type == 0x4E4F534A) { g.J = JsonDocument.Parse(new ReadOnlyMemory<byte>(b, off, len)).RootElement; json = true; }
                else if (type == 0x004E4942) { g.Bin = new byte[len]; Buffer.BlockCopy(b, off, g.Bin, 0, len); }
                off += len;
            }
            if (!json) throw new Exception("manca il blocco JSON");
            return g;
        }
        static int Int(JsonElement e, string n, int def = 0) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;
        static float[] Floats(JsonElement e, string n, float[] def) { if (!e.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Array) return def; return v.EnumerateArray().Select(x => x.GetSingle()).ToArray(); }

        // dati grezzi di un accessore: (byte, offset del primo elemento, passo, numero, componenti, tipo)
        static (byte[] buf, int start, int stride, int count, int comps, int ctype, bool norm) Acc(Glb g, int index)
        {
            var a = g.Arr("accessors")[index]; int count = Int(a, "count"); int ctype = Int(a, "componentType");
            int comps = a.GetProperty("type").GetString() switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => 1 };
            int size = ctype switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };
            bool norm = a.TryGetProperty("normalized", out var nn) && nn.GetBoolean();
            if (!a.TryGetProperty("bufferView", out var bvp)) return (new byte[count * comps * size], 0, comps * size, count, comps, ctype, norm);
            var bv = g.Arr("bufferViews")[bvp.GetInt32()];
            int start = Int(bv, "byteOffset") + Int(a, "byteOffset"); int stride = Int(bv, "byteStride", comps * size);
            return (g.Bin, start, stride, count, comps, ctype, norm);
        }
        static float Comp(byte[] b, int o, int ctype, bool norm) => ctype switch
        {
            5126 => BitConverter.ToSingle(b, o),
            5121 => norm ? b[o] / 255f : b[o], 5120 => norm ? Math.Max(-1f, (sbyte)b[o] / 127f) : (sbyte)b[o],
            5123 => norm ? BitConverter.ToUInt16(b, o) / 65535f : BitConverter.ToUInt16(b, o), 5122 => norm ? Math.Max(-1f, BitConverter.ToInt16(b, o) / 32767f) : BitConverter.ToInt16(b, o),
            _ => BitConverter.ToUInt32(b, o),
        };
        static float[] Read(Glb g, int index)
        {
            var (buf, start, stride, count, comps, ctype, norm) = Acc(g, index); int size = ctype switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };
            var r = new float[count * comps];
            for (int i = 0; i < count; i++) for (int c = 0; c < comps; c++) r[i * comps + c] = Comp(buf, start + i * stride + c * size, ctype, norm);
            return r;
        }
        static int[] Indices(Glb g, int index)
        {
            var (buf, start, stride, count, _, ctype, _) = Acc(g, index); var r = new int[count];
            for (int i = 0; i < count; i++) { int o = start + i * stride; r[i] = ctype switch { 5121 => buf[o], 5123 => BitConverter.ToUInt16(buf, o), _ => (int)BitConverter.ToUInt32(buf, o) }; }
            return r;
        }

        // ---- materiali e texture
        static Shader Shader() => UnityEngine.Shader.Find("Standard") ?? UnityEngine.Shader.Find("Legacy Shaders/Diffuse") ?? UnityEngine.Shader.Find("Unlit/Texture");
        static Material[] Materials(Glb g)
        {
            var texCache = new Dictionary<int, Texture2D>(); var list = new List<Material>();
            Texture2D Tex(int t)
            {
                if (texCache.TryGetValue(t, out var c)) return c;
                var te = g.Arr("textures")[t]; int src = Int(te, "source", -1); if (src < 0) return null;
                var im = g.Arr("images")[src]; if (!im.TryGetProperty("bufferView", out var bvp)) return null;
                var bv = g.Arr("bufferViews")[bvp.GetInt32()]; int o = Int(bv, "byteOffset"), l = Int(bv, "byteLength");
                var bytes = new byte[l]; Buffer.BlockCopy(g.Bin, o, bytes, 0, l);
                var tx = new Texture2D(2, 2, TextureFormat.RGBA32, true) { hideFlags = HideFlags.HideAndDontSave }; ImageConversion.LoadImage(tx, bytes, false);
                tx.wrapMode = TextureWrapMode.Repeat; tx.filterMode = FilterMode.Trilinear; tx.anisoLevel = 4;
                texCache[t] = tx; return tx;
            }
            if (g.J.TryGetProperty("materials", out var mats))
                foreach (var m in mats.EnumerateArray())
                {
                    var mat = new Material(Shader()) { hideFlags = HideFlags.HideAndDontSave };
                    if (m.TryGetProperty("pbrMetallicRoughness", out var pbr))
                    {
                        var col = Floats(pbr, "baseColorFactor", new[] { 1f, 1f, 1f, 1f }); mat.color = new Color(col[0], col[1], col[2], col[3]);
                        if (pbr.TryGetProperty("baseColorTexture", out var bt)) { var tx = Tex(Int(bt, "index")); if (tx != null) mat.mainTexture = tx; }
                        try { if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", pbr.TryGetProperty("metallicFactor", out var mf) ? mf.GetSingle() : 1f); if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 1f - (pbr.TryGetProperty("roughnessFactor", out var rf) ? rf.GetSingle() : 1f)); } catch { }
                    }
                    var em = Floats(m, "emissiveFactor", null);
                    if (em != null && em.Length >= 3 && (em[0] + em[1] + em[2]) > 0.01f) try { mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", new Color(em[0], em[1], em[2])); } catch { }
                    list.Add(mat);
                }
            return list.ToArray();
        }

        // ---- costruzione degli oggetti
        static Mesh BuildMesh(Glb g, JsonElement prim)
        {
            var at = prim.GetProperty("attributes"); var pos = Read(g, at.GetProperty("POSITION").GetInt32()); int n = pos.Length / 3;
            var v = new Vector3[n]; for (int i = 0; i < n; i++) v[i] = new Vector3(-pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            if (n > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = v;
            if (at.TryGetProperty("TEXCOORD_0", out var uvp)) { var uv = Read(g, uvp.GetInt32()); var u = new Vector2[n]; for (int i = 0; i < n; i++) u[i] = new Vector2(uv[i * 2], 1f - uv[i * 2 + 1]); mesh.uv = u; }
            int[] idx = prim.TryGetProperty("indices", out var ip) ? Indices(g, ip.GetInt32()) : Enumerable.Range(0, n).ToArray();
            for (int i = 0; i + 2 < idx.Length; i += 3) { (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]); } // verso dei triangoli
            mesh.triangles = idx;
            if (at.TryGetProperty("NORMAL", out var np)) { var nr = Read(g, np.GetInt32()); var nm = new Vector3[n]; for (int i = 0; i < n; i++) nm[i] = new Vector3(-nr[i * 3], nr[i * 3 + 1], nr[i * 3 + 2]); mesh.normals = nm; }
            else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
        static void Node(Glb g, int index, Transform parent, Material[] mats, Dictionary<int, List<(Mesh, int)>> meshCache)
        {
            var nd = g.Arr("nodes")[index];
            var go = new GameObject(nd.TryGetProperty("name", out var nm) ? nm.GetString() : "nodo" + index); go.transform.SetParent(parent, false);
            var m = Floats(nd, "matrix", null);
            if (m != null && m.Length == 16)
            {
                var t = new Vector3(m[12], m[13], m[14]); var sx = new Vector3(m[0], m[1], m[2]).magnitude; var sy = new Vector3(m[4], m[5], m[6]).magnitude; var sz = new Vector3(m[8], m[9], m[10]).magnitude;
                var fwd = new Vector3(m[8], m[9], m[10]) / Math.Max(1e-6f, sz); var up = new Vector3(m[4], m[5], m[6]) / Math.Max(1e-6f, sy);
                var q = Quaternion.LookRotation(fwd, up);
                go.transform.localPosition = new Vector3(-t.x, t.y, t.z); go.transform.localRotation = new Quaternion(q.x, -q.y, -q.z, q.w); go.transform.localScale = new Vector3(sx, sy, sz);
            }
            else
            {
                var t = Floats(nd, "translation", new[] { 0f, 0f, 0f }); var r = Floats(nd, "rotation", new[] { 0f, 0f, 0f, 1f }); var s = Floats(nd, "scale", new[] { 1f, 1f, 1f });
                go.transform.localPosition = new Vector3(-t[0], t[1], t[2]); go.transform.localRotation = new Quaternion(r[0], -r[1], -r[2], r[3]); go.transform.localScale = new Vector3(s[0], s[1], s[2]);
            }
            if (nd.TryGetProperty("mesh", out var mp))
            {
                int mi = mp.GetInt32();
                if (!meshCache.TryGetValue(mi, out var parts))
                {
                    parts = new List<(Mesh, int)>();
                    foreach (var prim in g.Arr("meshes")[mi].GetProperty("primitives").EnumerateArray()) { if (Int(prim, "mode", 4) != 4) continue; parts.Add((BuildMesh(g, prim), Int(prim, "material", -1))); }
                    meshCache[mi] = parts;
                }
                foreach (var (mesh, mat) in parts)
                {
                    var p = new GameObject("parte"); p.transform.SetParent(go.transform, false);
                    p.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = p.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat >= 0 && mat < mats.Length ? mats[mat] : new Material(Shader());
                }
            }
            if (nd.TryGetProperty("children", out var ch)) foreach (var c in ch.EnumerateArray()) Node(g, c.GetInt32(), go.transform, mats, meshCache);
        }

        // Modello pronto all'uso (prototipo nascosto, poi si clona).
        public static GameObject Load(string file)
        {
            string path = Path.IsPathRooted(file) ? file : Path.Combine(Dir, file);
            if (protos.TryGetValue(path, out var p) && p != null) return p;
            var g = Parse(File.ReadAllBytes(path)); var mats = Materials(g);
            var root = new GameObject("Modello " + Path.GetFileNameWithoutExtension(path)); UnityEngine.Object.DontDestroyOnLoad(root);
            var cache = new Dictionary<int, List<(Mesh, int)>>();
            int scene = Int(g.J, "scene", 0);
            if (g.J.TryGetProperty("scenes", out var sc) && sc.GetArrayLength() > scene && sc[scene].TryGetProperty("nodes", out var roots)) { foreach (var r in roots.EnumerateArray()) Node(g, r.GetInt32(), root.transform, mats, cache); }
            else if (g.J.TryGetProperty("nodes", out var ns)) for (int i = 0; i < ns.GetArrayLength(); i++) Node(g, i, root.transform, mats, cache);
            root.SetActive(false); protos[path] = root;
            int tris = cache.Values.SelectMany(x => x).Sum(x => (int)x.Item1.GetIndexCount(0) / 3);
            Plugin.L.LogInfo($"[modelli] caricato {Path.GetFileName(path)}: {cache.Count} mesh, {tris} triangoli, {mats.Length} materiali");
            return root;
        }
        public static GameObject Spawn(string file, Transform parent, Vector3 pos, Vector3 euler, float scale)
        {
            var proto = Load(file); var go = UnityEngine.Object.Instantiate(proto); go.SetActive(true);
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.localPosition = pos; go.transform.localRotation = Quaternion.Euler(euler); go.transform.localScale = Vector3.one * scale;
            Shown.Add(go); return go;
        }

        // ---- agganci da models.json: [{ "file": "...", "attach": "schermo:PLEngineerReactorScreen" | "nave", "pos": [x,y,z], "rot": [x,y,z], "scale": 1, "hideOriginal": false }]
        public sealed class Spec { public string file { get; set; } = ""; public string attach { get; set; } = ""; public float[] pos { get; set; } = { 0, 0, 0 }; public float[] rot { get; set; } = { 0, 0, 0 }; public float scale { get; set; } = 1f; public bool hideOriginal { get; set; } }
        static List<Spec> specs; static readonly HashSet<IntPtr> attached = new(); static float nextAttach;
        public static void Tick()
        {
            if (Time.time < nextAttach) return; nextAttach = Time.time + 5f;
            try
            {
                if (specs == null) { string f = Path.Combine(Dir, "models.json"); specs = File.Exists(f) ? JsonSerializer.Deserialize<List<Spec>>(File.ReadAllText(f)) ?? new List<Spec>() : new List<Spec>(); }
                if (specs.Count == 0) return;
                foreach (var sp in specs.Where(s => s.attach.StartsWith("schermo:")))
                {
                    string type = sp.attach.Substring(8);
                    foreach (var sc in UnityEngine.Object.FindObjectsOfType<PLUIScreen>())
                    {
                        if (sc == null || sc.GetIl2CppType().Name != type || attached.Contains(sc.Pointer)) continue;
                        attached.Add(sc.Pointer);
                        var m = Spawn(sp.file, sc.transform, V(sp.pos), V(sp.rot), sp.scale); SetLayer(m, sc.gameObject.layer);
                        if (sp.hideOriginal) foreach (var r in sc.transform.parent.GetComponentsInChildren<MeshRenderer>()) if (!r.transform.IsChildOf(sc.transform)) r.enabled = false;
                    }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Modelli: " + e.Message); specs = new List<Spec>(); }
        }
        static Vector3 V(float[] a) => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;

        // ---- console
        public static string Show(string file, float dist)
        {
            var pawn = PLNetworkManager.Instance?.MyLocalPawn; if (pawn == null) return "nessun personaggio";
            var pt = pawn.transform; var cam = Camera.main;
            Vector3 fwd = pt.forward; try { var hc = pawn.GetComponentInChildren<Camera>(); if (hc != null) { cam = hc; fwd = hc.transform.forward; } } catch { }
            var go = Spawn(file, pt.parent, Vector3.zero, Vector3.zero, 1f);
            go.transform.position = pt.position + Vector3.up * 1.4f + fwd * dist; go.transform.rotation = Quaternion.LookRotation(-fwd, Vector3.up);
            SetLayer(go, pawn.gameObject.layer);
            var r = go.GetComponentsInChildren<MeshRenderer>(); var b = r.Length > 0 ? r[0].bounds : new Bounds();
            foreach (var x in r) b.Encapsulate(x.bounds);
            return $"{file} davanti alla telecamera: {r.Length} parti, dimensioni {b.size.x:0.00} × {b.size.y:0.00} × {b.size.z:0.00}";
        }
        public static void SetLayer(GameObject g, int layer) { g.layer = layer; for (int i = 0; i < g.transform.childCount; i++) SetLayer(g.transform.GetChild(i).gameObject, layer); }
        public static string Clear() { int n = Shown.Count; foreach (var g in Shown) if (g != null) UnityEngine.Object.Destroy(g); Shown.Clear(); return $"{n} modelli rimossi"; }
        public static string List() { try { return Directory.Exists(Dir) ? string.Join(", ", Directory.GetFiles(Dir, "*.glb").Select(Path.GetFileName)) : "cartella vuota: " + Dir; } catch (Exception e) { return e.Message; } }
    }
}

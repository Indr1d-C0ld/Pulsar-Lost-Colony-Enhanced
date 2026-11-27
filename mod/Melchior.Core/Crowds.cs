// Folla negli interni: le stazioni abitate sono davvero affollate.
// I vagabondi originali (PLWanderer) camminano su percorsi validi della stazione; per ognuno nascono alcune comparse
// che ne seguono le orme a qualche decina di secondi di distanza, leggermente scostate di lato, con passo e pause
// proprie. Camminano con il controller del personaggio (le gambe si animano con la velocità), non attraversano muri
// e non pesano sulla rete: sono comparse locali, create su ogni computer della partita. Quante sono dipende dalla
// prosperità del porto, dagli eventi (in festa il doppio, con l'epidemia la metà) e dalla densità del traffico.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Melchior
{
    public static class Crowds
    {
        static readonly System.Random rng = new();
        public static bool Enabled = true;
        sealed class Trail { public PLWanderer src; public readonly List<Vector3> pts = new(); }
        sealed class Extra { public GameObject go; public CharacterController cc; public Trail trail; public int lag, cursor, dir = 1; public float side, speed, pauseUntil; }
        static readonly List<Trail> trails = new(); static readonly List<Extra> extras = new();
        public static IEnumerable<GameObject> Clones => extras.Select(e => e.go);
        static int sector = -1; static float nextSample, nextSpawn;

        static int Wanted(int originals)
        {
            float k = 2f; int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { }
            try { var p = Ports.Of(sec); if (p != null) { k = 1f + p.prosperity / 40f; if (p.ev == "festa") k *= 2f; if (p.ev == "epidemia") k *= 0.5f; } } catch { }
            return Math.Min(48, (int)Math.Round(originals * k * Traffic.Density));
        }

        public static void Tick()
        {
            if (!Enabled) return;
            try
            {
                int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { }
                if (sec != sector) { sector = sec; Clear(); nextSpawn = Time.time + 6f; }
                float now = Time.time;
                if (now >= nextSample)
                {
                    nextSample = now + 0.5f;
                    var ws = UnityEngine.Object.FindObjectsOfType<PLWanderer>();
                    foreach (var w in ws) { if (w == null || extras.Any(e => e.go == w.gameObject) || trails.Any(t => t.src != null && t.src.Pointer == w.Pointer)) continue; trails.Add(new Trail { src = w }); }
                    // le orme restano anche quando il vagabondo esce dalla stazione: sono percorsi validi
                    foreach (var t in trails) { if (t.src != null && (!t.src.gameObject.activeInHierarchy)) t.src = null; }
                    trails.RemoveAll(t => t.src == null && t.pts.Count < 20);
                    if (trails.Count > 40) trails.RemoveAt(0);
                    foreach (var t in trails.Where(t => t.src != null)) { var p = t.src.transform.position; if (t.pts.Count == 0 || (t.pts[^1] - p).sqrMagnitude > 0.04f) { t.pts.Add(p); if (t.pts.Count > 600) t.pts.RemoveAt(0); } }
                }
                if (now >= nextSpawn && trails.Count > 0)
                {
                    nextSpawn = now + 3f;
                    extras.RemoveAll(e => e.go == null);
                    int want = Wanted(trails.Count);
                    var ready = trails.Where(t => t.pts.Count >= 20).ToList(); if (!trails.Any(x => x.src != null) && !extras.Any(x => x.go != null)) ready.Clear();
                    for (int i = extras.Count; i < want && ready.Count > 0; i++) Spawn(ready[rng.Next(ready.Count)]);
                }
                float dt = Time.deltaTime;
                foreach (var e in extras)
                {
                    if (e.go == null) continue;
                    var pts = e.trail.pts; if (pts.Count < 2) continue;
                    // ogni comparsa percorre l'orma per conto suo, avanti e indietro
                    e.cursor = Math.Clamp(e.cursor, 0, pts.Count - 1);
                    var target = pts[e.cursor]; var pos = e.go.transform.position;
                    var to = target - pos; to.y = 0f;
                    int nx = Math.Clamp(e.cursor + e.dir, 0, pts.Count - 1);
                    { var dir = pts[nx] - pts[e.cursor]; dir.y = 0; if (dir.sqrMagnitude > 0.0001f) to += Vector3.Cross(Vector3.up, dir.normalized) * e.side * 0.5f; }
                    if (now < e.pauseUntil) { if (e.cc != null && e.cc.enabled) e.cc.Move(Vector3.down * 2f * dt); continue; }
                    if (to.magnitude < 0.45f)
                    {
                        e.cursor += e.dir;
                        if (e.cursor <= 0 || e.cursor >= pts.Count - 1) { e.dir = -e.dir; e.pauseUntil = now + 2f + (float)rng.NextDouble() * 6f; }
                        else if (rng.NextDouble() < 0.01) e.pauseUntil = now + 1.5f + (float)rng.NextDouble() * 4f;
                        continue;
                    }
                    var step = to.normalized * Math.Min(to.magnitude, e.speed * dt) + Vector3.down * 2f * dt;
                    if (e.cc != null && e.cc.enabled) e.cc.Move(step); else e.go.transform.position = pos + step;
                    var look = new Vector3(to.x, 0f, to.z); if (look.sqrMagnitude > 0.001f) e.go.transform.rotation = Quaternion.Slerp(e.go.transform.rotation, Quaternion.LookRotation(look), dt * 6f);
                }
            }
            catch (Exception ex) { Plugin.L.LogWarning("Folla: " + ex.Message); }
        }

        // ---- stampi: copie dei vagabondi senza rete, dialoghi né cervello.
        // Si clonano dentro un contenitore inattivo, così i componenti della copia non si inizializzano: se la copia di un
        // vagabondo attivo nascesse attiva, il suo PhotonView si registrerebbe con lo stesso identificativo dell'originale
        // e Photon scarterebbe quello vero («PhotonView ID duplicate»), con messaggi di rete orfani e il gioco che cade.
        static GameObject holder; static readonly Dictionary<IntPtr, GameObject> stamps = new();
        public static GameObject Stamp(GameObject src)
        {
            if (src == null) return null;
            if (stamps.TryGetValue(src.Pointer, out var st) && st != null) return st;
            if (holder == null) { holder = new GameObject("MelchiorStampi"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder); }
            var go = UnityEngine.Object.Instantiate(src, holder.transform, false);
            foreach (var c in go.GetComponents<Component>())
            {
                string n = c.GetIl2CppType().Name;
                if (n is "PhotonView" or "PLWanderer" or "Seeker" or "PLDialogueActorInstance") UnityEngine.Object.DestroyImmediate(c);
            }
            go.name = "Stampo"; stamps[src.Pointer] = go; return go;
        }
        // una comparsa dallo stampo: nasce già pulita, e solo allora si attiva
        public static GameObject FromStamp(GameObject stamp, Vector3 pos, Transform parent)
        {
            var go = UnityEngine.Object.Instantiate(stamp, holder.transform, false); go.name = "Comparsa";
            var cc = go.GetComponent<CharacterController>(); if (cc != null) cc.enabled = false;
            go.transform.SetParent(parent, false); go.transform.position = pos;
            if (cc != null) cc.enabled = true; return go;
        }
        static void Spawn(Trail t)
        {
            GameObject model = t.src != null ? t.src.gameObject : trails.FirstOrDefault(x => x.src != null)?.src?.gameObject; var stamp = model != null ? Stamp(model) : stamps.Values.FirstOrDefault(x => x != null); if (stamp == null) return;
            int cursor = rng.Next(t.pts.Count); var start = t.pts[cursor];
            var go = FromStamp(stamp, start, model != null ? model.transform.parent : null);
            var cc = go.GetComponent<CharacterController>();
            go.transform.rotation = Quaternion.Euler(0f, rng.Next(360), 0f);
            extras.Add(new Extra { go = go, cc = cc, trail = t, cursor = cursor, dir = rng.Next(2) == 0 ? 1 : -1, side = (float)(rng.NextDouble() * 1.6 - 0.8), speed = 1.0f + (float)rng.NextDouble() * 0.7f });
        }

        public static void Clear()
        {
            foreach (var e in extras) if (e.go != null) UnityEngine.Object.Destroy(e.go); extras.Clear(); trails.Clear();
            // gli stampi del settore lasciato non servono più (tranne quello dei passeggeri)
            foreach (var kv in stamps.ToList()) if (kv.Value != null && kv.Value != PassengersWalk.Model) { UnityEngine.Object.Destroy(kv.Value); stamps.Remove(kv.Key); } else if (kv.Value == null) stamps.Remove(kv.Key);
        }

        // ---- prove
        public static string Grow(int n) { var ready = trails.Where(t => t.pts.Count >= 5).ToList(); if (ready.Count == 0) return "aspetta qualche secondo: i vagabondi devono camminare"; for (int i = 0; i < n; i++) Spawn(ready[rng.Next(ready.Count)]); return Describe(); }
        public static string Look()
        {
            var c = extras.FirstOrDefault(x => x.go != null)?.go; var pawn = PLNetworkManager.Instance?.MyLocalPawn; if (c == null || pawn == null) return "niente da guardare";
            var p = c.transform.position; try { PLNetworkManager.Instance.LocalPlayer.NetworkTeleportToPos(p + new Vector3(3f, 0.3f, 3f)); } catch { pawn.transform.position = p + new Vector3(3f, 0.3f, 3f); }
            return $"davanti alla comparsa a {p}; " + Describe();
        }
        public static string Where() { var c = extras.FirstOrDefault(x => x.go != null)?.go; return c == null ? "-" : c.transform.position.ToString(); }
        public static string Debug()
        {
            var sb = new System.Text.StringBuilder(Describe());
            var all = Resources.FindObjectsOfTypeAll<PLWanderer>();
            sb.Append($"; vagabondi in memoria {all.Length}, attivi {all.Count(w => w != null && w.gameObject.activeInHierarchy)}");
            foreach (var t in trails.Take(5)) sb.Append($"\n      orma: punti {t.pts.Count}, sorgente {(t.src == null ? "persa" : t.src.gameObject.activeInHierarchy ? "attiva" : "spenta")} a {(t.src == null ? "-" : t.src.transform.position.ToString())}");
            foreach (var e in extras.Take(4)) { if (e.go == null) continue; int idx = Math.Clamp(e.cursor, 0, Math.Max(0, e.trail.pts.Count - 1)); sb.Append($"\n      comparsa a {e.go.transform.position} meta {(e.trail.pts.Count > 0 ? e.trail.pts[idx].ToString() : "-")} cc {(e.cc != null ? e.cc.enabled.ToString() : "nessuno")} attiva {e.go.activeInHierarchy}"); }
            return sb.ToString();
        }
        public static string Describe() => $"vagabondi {trails.Count}, comparse {extras.Count(e => e.go != null)}, previste {Wanted(trails.Count)}";
    }
}

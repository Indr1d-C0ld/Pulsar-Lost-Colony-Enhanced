// I passeggeri si vedono: camminano nei corridoi della nostra nave. Il loro aspetto viene dai vagabondi delle stazioni
// (la prima volta che se ne incontra uno se ne conserva una copia «modello», disattivata e senza rete, dialoghi né
// cervello). Camminano sulla rete di nodi che il gioco usa per muovere i bot dentro la nave (PLPathNode e i suoi
// vicini): da un nodo a un vicino scelto a caso, senza tornare subito indietro, con soste e passo propri, così nessuno
// attraversa le paratie. Fino a otto figure. Sono comparse locali: girano su ogni computer della partita.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Melchior
{
    public static class PassengersWalk
    {
        static readonly System.Random rng = new();
        static GameObject model; static List<PLPathNode> nodes = new(); static int netShip = -1; static float nextNet;
        sealed class Walker { public GameObject go; public CharacterController cc; public PLPathNode at, next, prev; public float side, speed, pauseUntil; }
        static readonly List<Walker> walkers = new(); static float nextSync;
        public static GameObject Model => model;
        public static IEnumerable<GameObject> Figures => walkers.Select(w => w.go);
        public static int Shown => walkers.Count(w => w.go != null);

        static int Pax() { try { return PhotonNetwork.isMasterClient ? Passengers.Aboard : (GalaxyView.Current?.pax?.aboard ?? 0); } catch { return 0; } }

        public static void Tick()
        {
            try
            {
                var me = Stations.MyShip; if (me == null) return; float now = Time.time;
                if (model == null) Capture();
                if (netShip != me.ShipID || now >= nextNet || nodes.Any(n => n == null)) { nextNet = now + (nodes.Count == 0 ? 5f : 60f); Network(me); }
                if (now >= nextSync) { nextSync = now + 2f; Sync(me); }
                Move(Time.deltaTime, now);
            }
            catch (Exception e) { Plugin.L.LogWarning("Passeggeri a bordo: " + e.Message); nextSync = Time.time + 10f; }
        }

        static void Capture()
        {
            var w = UnityEngine.Object.FindObjectsOfType<PLWanderer>().FirstOrDefault(x => x != null && x.gameObject.activeInHierarchy); if (w == null) return;
            model = Crowds.Stamp(w.gameObject); // stampo pulito, conservato nel contenitore inattivo (vedi Crowds.Stamp)
            Plugin.L.LogInfo("[passeggeri] modello di passeggero conservato");
        }
        // la rete di nodi della nostra nave: quella collegata al nodo più vicino a uno dei nostri a bordo
        static void Network(PLShipInfo me)
        {
            var nl = new List<PLPathNode>(); netShip = me.ShipID; int sub = Boarding.SubHubOf(me); if (sub < 0) { nodes = nl; return; }
            Vector3? anchor = null;
            try { var all = PLServer.Instance.AllPlayers; for (int i = 0; i < all.Count && anchor == null; i++) { var p = all[i]; var pw = p?.MyPawn; if (pw != null && !pw.IsDead && (p.SubHubID & 0x7FFFFFFF) == sub && p.StartingShip != null && p.StartingShip.ShipID == me.ShipID) anchor = pw.transform.position; } } catch { }
            if (anchor == null) { nodes = nl; return; }
            PLPathNode seed = null; float best = 400f;
            foreach (var n in UnityEngine.Object.FindObjectsOfType<PLPathNode>()) { if (n == null) continue; float d = (n.transform.position - anchor.Value).sqrMagnitude; if (d < best) { best = d; seed = n; } }
            if (seed == null) { nodes = nl; return; }
            var seen = new HashSet<IntPtr> { seed.Pointer }; var q = new Queue<PLPathNode>(); q.Enqueue(seed);
            while (q.Count > 0 && nl.Count < 600)
            {
                var n = q.Dequeue(); nl.Add(n); var nb = n.Neighbors; if (nb == null) continue;
                for (int i = 0; i < nb.Count; i++) { var x = nb[i]; if (x != null && seen.Add(x.Pointer)) q.Enqueue(x); }
            }
            nodes = nl;
        }
        static bool LocalAboard(PLShipInfo me)
        {
            try { var lp = PLNetworkManager.Instance.LocalPlayer; int sub = Boarding.SubHubOf(me); return lp != null && sub >= 0 && (lp.SubHubID & 0x7FFFFFFF) == sub; } catch { return false; }
        }
        static void Sync(PLShipInfo me)
        {
            walkers.RemoveAll(w => w.go == null);
            int want = LocalAboard(me) && model != null && nodes.Count >= 8 ? Math.Min(8, Pax()) : 0;
            while (walkers.Count > want) { var w = walkers[^1]; if (w.go != null) UnityEngine.Object.Destroy(w.go); walkers.RemoveAt(walkers.Count - 1); }
            while (walkers.Count < want) { var w = Spawn(); if (w == null) break; walkers.Add(w); }
        }
        static Walker Spawn()
        {
            if (model == null || nodes.Count == 0) return null;
            var at = nodes[rng.Next(nodes.Count)]; if (at == null) return null;
            var go = Crowds.FromStamp(model, at.transform.position, null); go.name = "Passeggero"; var cc = go.GetComponent<CharacterController>();
            // a bordo niente fisica: le collisioni degli interni della nave non reggerebbero il controller, e cadrebbero nel vuoto
            if (cc != null) cc.enabled = false;
            go.transform.rotation = Quaternion.Euler(0f, rng.Next(360), 0f);
            return new Walker { go = go, cc = cc, at = at, side = (float)(rng.NextDouble() * 0.6 - 0.3), speed = 0.8f + (float)rng.NextDouble() * 0.6f, pauseUntil = Time.time + (float)rng.NextDouble() * 5f };
        }
        static PLPathNode Pick(Walker w)
        {
            var nb = w.at?.Neighbors; if (nb == null || nb.Count == 0) return null;
            var opts = new List<PLPathNode>(); for (int i = 0; i < nb.Count; i++) { var x = nb[i]; if (x != null && (w.prev == null || x.Pointer != w.prev.Pointer)) opts.Add(x); }
            if (opts.Count == 0) opts.Add(w.prev); return opts[rng.Next(opts.Count)];
        }
        static void Move(float dt, float now)
        {
            foreach (var w in walkers)
            {
                if (w.go == null || w.at == null) continue;
                if (now < w.pauseUntil) continue;
                if (w.next == null) { w.next = Pick(w); if (w.next == null) { w.pauseUntil = now + 3f; continue; } }
                var pos = w.go.transform.position; var target = w.next.transform.position; var to = target - pos; float dy = to.y; to.y = 0f;
                var dir = target - w.at.transform.position; dir.y = 0f; if (dir.sqrMagnitude > 0.01f) to += Vector3.Cross(Vector3.up, dir.normalized) * w.side;
                if (to.magnitude < 0.5f)
                {
                    w.prev = w.at; w.at = w.next; w.next = null;
                    if (rng.NextDouble() < 0.12) w.pauseUntil = now + 2f + (float)rng.NextDouble() * 8f; // si ferma a guardare fuori, a parlare, a sedersi
                    continue;
                }
                // si cammina in piano e si segue l'altezza dei nodi (scale, rampe), senza gravità
                float stepLen = Math.Min(to.magnitude, w.speed * dt); var step = to.normalized * stepLen; step.y = to.magnitude > 0.01f ? dy * Math.Min(1f, stepLen / to.magnitude) : dy;
                w.go.transform.position = pos + step;
                var look = new Vector3(to.x, 0f, to.z); if (look.sqrMagnitude > 0.001f) w.go.transform.rotation = Quaternion.Slerp(w.go.transform.rotation, Quaternion.LookRotation(look), dt * 6f);
            }
        }
        public static void Clear() { foreach (var w in walkers) if (w.go != null) UnityEngine.Object.Destroy(w.go); walkers.Clear(); }
        public static string Debug()
        {
            var act = UnityEngine.Object.FindObjectsOfType<PLPathNode>(); var all = Resources.FindObjectsOfTypeAll<PLPathNode>();
            var me = Stations.MyShip; Vector3 a = Vector3.zero; try { a = PLNetworkManager.Instance.LocalPlayer.MyPawn.transform.position; } catch { }
            float near = act.Length == 0 ? -1 : act.Min(n => (n.transform.position - a).magnitude); float nearAll = all.Length == 0 ? -1 : all.Min(n => (n.transform.position - a).magnitude);
            return Describe() + $"; nodi attivi {act.Length} (il più vicino a {near:0.0} m), in memoria {all.Length} (il più vicino a {nearAll:0.0} m), sotto-hub {Boarding.SubHubOf(me)}";
        }
        public static string Describe() => $"passeggeri visibili {Shown}, modello {(model != null ? "pronto" : "da catturare (serve una stazione con vagabondi)")}, nodi della nave {nodes.Count}" + (walkers.Count > 0 && walkers[0].go != null ? $", il primo a {walkers[0].go.transform.position}" : "");
    }
}

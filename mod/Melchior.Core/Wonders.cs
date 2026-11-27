// Meraviglie e cataclismi: ogni 25-40 salti un evento raro e spettacolare.
// - supernova: una stella sta per esplodere fra tre salti. Chi arriva prima può evacuare la colonia (incontro);
//   poi il settore resta per sempre un campo di radiazioni, con i resti della stella da esplorare.
// - cometa: attraversa la galassia di settore in settore per otto salti; chi la raggiunge raccoglie materia esotica.
// - allineamento: per sei salti motori e curvatura si riparano da soli, il morale sale, il direttore concede più calma.
// - spazio morto: per quattro salti i sensori si accecano (meno con la Scienza) e le imboscate sono più frequenti.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class WonderState { public string kind = ""; public int sector = -1, until = -1, boom = -1, next = -1; public bool visited, offered; }

    public static class Wonders
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static WonderState W => Saga.State.wonder ??= new WonderState();
        public static string Kind => Host ? (Saga.Loaded ? W.kind : "") : GalaxyView.Current?.wonder ?? "";
        public static int Sector => Host ? W.sector : -1;
        static int lastJumps = -1; static float nextTick;
        static string Ship() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }

        // Settori trasformati per sempre (resti di supernova): ambiente e luogo da esplorare.
        public static bool Remnant(int sector) => Saga.State.remnants?.Contains(sector) ?? false;

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                var w = W; int j = Obscured.Jumps(); bool jumped = lastJumps >= 0 && j != lastJumps; lastJumps = j;
                if (w.next < 0) { w.next = j + rng.Next(20, 35); Saga.MarkDirty(); }
                if (w.kind.Length == 0) { if (j >= w.next) Start(null); return; }
                int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { }
                bool warp = false; try { warp = Stations.MyShip.InWarp; } catch { }
                switch (w.kind)
                {
                    case "supernova":
                        if (!w.offered && sec == w.sector && !warp && j < w.boom && Encounters.Start("supernova_colonia", true)) { w.offered = true; Saga.MarkDirty(); }
                        if (j >= w.boom) Explode(w);
                        break;
                    case "cometa":
                        if (jumped) { Move(w); }
                        if (!w.offered && sec == w.sector && !warp && Encounters.Start("cometa", true)) { w.offered = true; Saga.MarkDirty(); }
                        if (j >= w.until) End(w, "La cometa si allontana verso lo spazio profondo. Tornerà fra qualche secolo.");
                        break;
                    case "allineamento":
                        if (UnityEngine.Time.time >= nextTick) { nextTick = UnityEngine.Time.time + 30f; var id = Stations.MyShip.ShipID; foreach (var sy in new[] { "engines", "warp" }) Stations.Set(id, "dmx_" + sy, Math.Max(0.01f, Stations.Get(id, "dmx_" + sy) - 8)); }
                        if (jumped) Morale.Change(1, null);
                        if (j >= w.until) End(w, "L'allineamento subspaziale si scioglie: le stelle tornano al loro posto.");
                        break;
                    case "spaziomorto":
                        if (UnityEngine.Time.time >= nextTick) { nextTick = UnityEngine.Time.time + 30f; bool sci = false; try { sci = PLServer.Instance.GetCachedFriendlyPlayerOfClass(2, Stations.MyShip) != null; } catch { } var id = Stations.MyShip.ShipID; float v = Stations.Get(id, "dmx_sensors"); if (v < 60) Stations.Set(id, "dmx_sensors", Math.Min(60f, v + (sci ? 3 : 8))); }
                        if (j >= w.until) End(w, "L'ondata di spazio morto si dissolve. I sensori tornano a vedere.");
                        break;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Meraviglie: " + e.Message); }
        }

        static List<PLSectorInfo> Open() { var l = new List<PLSectorInfo>(); try { foreach (var kv in PLGlobal.Instance.Galaxy.AllSectorInfos) { var s = kv.Value; if (s?.MySPI != null && s.VisualIndication == ESectorVisualIndication.NONE && s.MissionSpecificID == -1) l.Add(s); } } catch { } return l; }

        public static string Start(string kind)
        {
            var w = W; if (w.kind.Length > 0) return "c'è già un evento in corso";
            var kinds = new[] { "supernova", "cometa", "allineamento", "spaziomorto" }; kind ??= kinds[rng.Next(kinds.Length)];
            int j = Obscured.Jumps(); var cur = PLServer.GetCurrentSector(); w.kind = kind; w.visited = false; w.offered = false;
            string text;
            switch (kind)
            {
                case "supernova":
                {
                    var cand = Open().Where(s => cur == null || (s.ID != cur.ID && Nemesis.Distance(cur, s) is >= 2 and <= 6) && (!Home.Has || s.ID != Home.H.sector) && Bases.At(s.ID) == null).ToList(); if (cand.Count == 0) { w.kind = ""; return "nessun settore adatto"; }
                    var t = cand[rng.Next(cand.Count)]; w.sector = t.ID; w.boom = j + 3; w.until = w.boom;
                    text = $"L'Accademia lancia l'allarme: la stella del settore {Places.Of(t.ID)} esploderà fra tre salti. Su un pianeta del sistema c'è una colonia mineraria che non ha navi per andarsene.";
                    Contracts.Plot(t.ID); break;
                }
                case "cometa":
                {
                    var cand = Open().Where(s => cur == null || Nemesis.Distance(cur, s) is >= 2 and <= 5).ToList(); if (cand.Count == 0) { w.kind = ""; return "nessun settore adatto"; }
                    var t = cand[rng.Next(cand.Count)]; w.sector = t.ID; w.until = j + 8;
                    text = $"Una cometa mai vista attraversa la galassia: ora è nel settore {Places.Of(t.ID)} e si sposta a ogni salto. La sua coda è piena di materia esotica. Le sonde possono seguirla.";
                    break;
                }
                case "allineamento":
                    w.until = j + 6; text = "Un allineamento subspaziale che capita una volta in una generazione: per qualche salto motori e curvatura si rigenerano da soli e l'equipaggio dorme sonni tranquilli."; break;
                default:
                    w.until = j + 4; text = "Un'ondata di spazio morto attraversa la galassia: per qualche salto i sensori vedono pochissimo, e i predoni ne approfittano."; break;
            }
            Saga.MarkDirty(); GalaxyView.Dirty();
            Director.Notify("MERAVIGLIA · " + Name(kind).ToUpperInvariant(), text);
            News.Post("scienza", text);
            Codex.Phenomenon(kind); Research.Add(6, "osservazione di un evento raro");
            Saga.Chronicle($"Evento raro: {Name(kind)}.");
            return text;
        }
        public static string Name(string k) => k switch { "supernova" => "supernova", "cometa" => "cometa errante", "allineamento" => "allineamento subspaziale", "spaziomorto" => "ondata di spazio morto", _ => k };

        static void Move(WonderState w)
        {
            try
            {
                var s = PLServer.GetSectorWithID(w.sector); var nb = s?.Neighbors; if (nb == null || nb.Count == 0) return;
                var n = nb[rng.Next(nb.Count)]; if (n == null) return; w.sector = n.ID; Saga.MarkDirty();
            }
            catch { }
        }

        static void Explode(WonderState w)
        {
            (Saga.State.remnants ??= new List<int>()).Add(w.sector);
            string text = w.visited ? $"La stella del settore {Places.Of(w.sector)} esplode. Della colonia, per fortuna, non c'era più nessuno: l'abbiamo portata via in tempo." : $"La stella del settore {Places.Of(w.sector)} esplode in una luce che si vede da mezza galassia. La colonia non ce l'ha fatta.";
            if (!w.visited) Morale.Change(-3, null);
            End(w, text + " Il settore resterà un campo di radiazioni, e i resti della stella attendono chi avrà il coraggio di esplorarli.");
        }

        static void End(WonderState w, string text)
        {
            Director.Notify("MERAVIGLIA", text); News.Post("scienza", text);
            if (w.kind is "supernova" or "cometa") Course.Clear();
            w.kind = ""; w.sector = -1; w.next = Obscured.Jumps() + rng.Next(25, 40); Saga.MarkDirty(); GalaxyView.Dirty();
        }

        public static void Evacuated() { W.visited = true; Saga.MarkDirty(); }
        public static string Line() { var k = Kind; return k.Length == 0 ? "" : Name(k); }
        public static string Describe() { var w = W; return w.kind.Length == 0 ? $"nessun evento; il prossimo al salto {w.next}" : $"{w.kind} settore {w.sector} fino al salto {w.until} (esplosione {w.boom}) visitato {w.visited}"; }
        public static string HereForTest() { var w = W; if (w.kind.Length == 0) return "nessun evento"; w.sector = PLServer.GetCurrentSector().ID; w.visited = false; w.offered = false; Saga.MarkDirty(); return Describe(); }
    }
}

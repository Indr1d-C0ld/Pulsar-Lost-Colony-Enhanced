// Ricerca scientifica: i dati raccolti viaggiando diventano tecnologie permanenti.
// Dati da: luoghi esplorati, luoghi leggendari, sonde, primi contatti e studi delle specie aliene, eventi rari,
// laboratori nelle nostre basi e analisi di routine quando la Scienza è presidiata (doppio durante la fiera dell'Accademia).
// Un progetto alla volta, dalla scheda Ricerca della Scienza: costa dati e richiede qualche salto di lavoro.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class ResearchState { public int data, total, progress, lastJump = -1; public string current = ""; public List<string> done = new(); }

    public static class Research
    {
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static ResearchState R => Saga.State.research ??= new ResearchState();

        public sealed class Project { public string id = "", name = "", effect = ""; public int cost, jumps; }
        public static readonly Project[] Projects =
        {
            new() { id = "mercato", name = "Analisi dei mercati", effect = "i prezzi di vendita dei porti entro due salti arrivano da soli nella scheda Mercato", cost = 15, jumps = 3 },
            new() { id = "idroponica", name = "Idroponica di bordo", effect = "l'equipaggio consuma un quarto di viveri in meno", cost = 20, jumps = 3 },
            new() { id = "cartografia", name = "Cartografia profonda", effect = "frammenti di mappa più frequenti nei luoghi esplorati", cost = 25, jumps = 4 },
            new() { id = "firma", name = "Schermatura della firma", effect = "imboscate e cacciatori di taglie ci trovano più di rado", cost = 30, jumps = 4 },
            new() { id = "medicina", name = "Medicina d'emergenza", effect = "dispensa con 15 medicinali e ferite che guariscono prima", cost = 25, jumps = 3 },
            new() { id = "automazione", name = "Automazione di bordo", effect = "paghe degli ufficiali ridotte di un quarto", cost = 35, jumps = 5 },
            new() { id = "disturbatori", name = "Disturbatori di puntamento", effect = "le torrette nemiche mirano peggio", cost = 40, jumps = 5 },
            new() { id = "resa", name = "Protocolli di resa", effect = "i nemici malridotti si arrendono più volentieri", cost = 30, jumps = 4 },
            new() { id = "minaccia", name = "Teoria della minaccia", effect = "la grande minaccia cresce un quarto più piano", cost = 50, jumps = 6 },
        };
        public static Project P(string id) => Projects.FirstOrDefault(p => p.id == id);
        public static bool Has(string id) => Host ? Saga.Loaded && R.done.Contains(id) : (GalaxyView.Current?.research?.done?.Contains(id) ?? false);

        public static void Add(int n, string why = null)
        {
            if (!Host || !Saga.Loaded || n <= 0) return;
            if (Calling.Is("fiera")) n *= 2;
            n = (int)Math.Round(n * Talents.Mul("ricercatore", 0.25f) * (1f + Loot.Mine("ricerca") + PawnLoot.Crew("ricerca")));
            R.data += n; R.total += n; Saga.MarkDirty(); GalaxyView.Dirty();
            if (why != null && n >= 5) try { Stations.LogEvent(Stations.MyShip.ShipID, $"Scienza: +{n} dati di ricerca ({why})"); } catch { }
        }

        static bool ScienceStaffed() { try { return PLServer.Instance.GetCachedFriendlyPlayerOfClass(2, Stations.MyShip) != null; } catch { return false; } }

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                var r = R; int j = Obscured.Jumps();
                if (r.lastJump < 0) { r.lastJump = j; Saga.MarkDirty(); return; }
                if (j == r.lastJump) return; r.lastJump = j;
                int labs = Saga.State.bases.Where(b => b.state == 1 && b.kind == "laboratorio").Sum(b => b.level * 2);
                Add((ScienceStaffed() ? 1 : 0) + labs);
                if (r.current.Length > 0)
                {
                    r.progress++; var p = P(r.current);
                    if (p != null && r.progress >= p.jumps) Complete(r, p);
                }
                Saga.MarkDirty(); GalaxyView.Dirty();
            }
            catch (Exception e) { Plugin.L.LogWarning("Ricerca: " + e.Message); }
        }

        static void Complete(ResearchState r, Project p)
        {
            r.done.Add(p.id); r.current = ""; r.progress = 0; Saga.MarkDirty(); GalaxyView.Dirty(); Feats.Research(r.done.Count, Projects.Length);
            Director.Notify("RICERCA COMPLETATA", $"{p.name}: {p.effect}.");
            Saga.Chronicle($"Ricerca completata: {p.name}.");
            News.Post("scienza", $"La «{(string)Stations.MyShip.ShipName}» ha messo a punto una nuova tecnologia: {p.name.ToLowerInvariant()}.");
            Competitors.Renown(10); Calling.Add("esplorazione");
            if (r.done.Count == Projects.Length) Trophies.Add("Tutte le tecnologie della Frontiera", "ricerca");
        }

        public static string Start(string id)
        {
            var p = P(id); var r = R; if (p == null) return "progetto sconosciuto";
            if (r.done.Contains(id)) return "già completato";
            if (r.current.Length > 0) return $"il laboratorio è occupato con {P(r.current)?.name.ToLowerInvariant()}";
            if (r.data < p.cost) return $"servono {p.cost} dati, ne abbiamo {r.data}";
            r.data -= p.cost; r.current = id; r.progress = 0; Saga.MarkDirty(); GalaxyView.Dirty();
            return $"Avviato: {p.name}. Pronto fra {p.jumps} salti.";
        }

        // ---- richieste dal pannello
        public static void Request(string op, string arg)
        {
            if (Host) { var t = op == "avvia" ? Start(arg) : ""; if (t.Length > 0) Stations.Announce("RICERCA", t); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|ricerca|{op}|{arg}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var p = msg.Split('|'); if (p.Length >= 4 && Host) Request(p[2], p[3]); }

        public class Panel { public int data, total, progress, jumps; public string current = ""; public List<string> done = new(); }
        public static Panel Snapshot() { var r = R; return new Panel { data = r.data, total = r.total, progress = r.progress, jumps = P(r.current)?.jumps ?? 0, current = r.current, done = r.done.ToList() }; }
        public static string Describe() { var r = R; return $"dati {r.data} (raccolti {r.total}); in corso: {(r.current.Length > 0 ? r.current + " " + r.progress + "/" + P(r.current)?.jumps : "nulla")}; completati: {string.Join(", ", r.done)}"; }
        public static string Finish() { var r = R; var p = P(r.current); if (p != null) Complete(r, p); return Describe(); }
    }
}

// Diario del capitano e ambizioni dell'equipaggio.
// - Data stellare: ogni galassia ha la sua, che avanza col tempo giocato e coi salti.
// - Diario del capitano: ogni tanto (e dopo i fatti importanti) una voce scritta dalla cronaca vera della galassia,
//   con lo stato dell'equipaggio. Al rientro in una galassia, «Nella puntata precedente…» riassume l'ultima sessione.
// - Ambizioni: tre obiettivi a lungo termine alla volta, presi da un catalogo; completati portano premi importanti e
//   vengono sostituiti da ambizioni nuove, via via più difficili.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class LogEntry { public long t; public string stardate = "", text = ""; }
    public class Goal { public string id = ""; public int target, level; public bool done; }

    public static class Logbook
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static SagaState St => Saga.State;

        // ---- data stellare
        public static string Stardate()
        {
            if (St.stardateBase <= 0) { St.stardateBase = 41000 + rng.Next(17000); Saga.MarkDirty(); }
            double sd = St.stardateBase + St.playedSeconds / 900.0 + St.jumps * 0.7;
            return sd.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---- diario
        static int lastChronicle = -1; static float nextEntry = 1500f, lastTick, previouslyAt = -1; static bool sessionDone; static int sessionSeed = int.MinValue;
        static readonly string[] Important = { "crisi", "Raggiunto", "Mappa completa", "Promozione", "In memoria", "Ambasciata", "Scontro con", "si chiude", "Inizia un nuovo capitolo", "Taglia su", "Consegna", "conquist", "Contratto con", "ambizione", "Ammutinamento", "Gratitudine", "Vendetta", "Sabotaggio" };

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                float now = UnityEngine.Time.time; float dt = lastTick > 0 ? Math.Min(5f, now - lastTick) : 0; lastTick = now;
                St.playedSeconds += dt;
                int seed = 0; try { seed = (int)PLServer.Instance.GalaxySeed; } catch { }
                if (seed != sessionSeed) { sessionSeed = seed; sessionDone = false; lastChronicle = St.log.Count; previouslyAt = now + 25f; nextEntry = now + 1500f; }
                if (!sessionDone && previouslyAt > 0 && now >= previouslyAt) { sessionDone = true; Previously(); }
                // una voce nuova ogni 25 minuti circa, o prima se sono successe parecchie cose importanti
                int fresh = St.log.Skip(Math.Max(0, lastChronicle)).Count(e => Important.Any(k => e.text.Contains(k)));
                if (now >= nextEntry || fresh >= 4) { Write(); nextEntry = now + 1500f; }
                Goals();
            }
            catch (Exception e) { Plugin.L.LogWarning("Diario: " + e.Message); }
        }

        static string Clean(string s) { s = s.Trim(); if (s.EndsWith(".")) s = s[..^1]; return s; }

        static string StatusLine()
        {
            var bits = new List<string> { $"Il morale è {Morale.Level(St.morale).ToLowerInvariant()}" };
            var allies = Diplomacy.F.Where(f => Factions.Level(f) >= 2).Select(GalaxyWar.Name).ToList(); if (allies.Count > 0) bits.Add("contiamo sull'amicizia della " + string.Join(" e della ", allies));
            var foes = Diplomacy.F.Where(f => Factions.Level(f) <= -2).Select(GalaxyWar.Name).ToList(); if (foes.Count > 0) bits.Add("la " + string.Join(" e la ", foes) + " ci danno la caccia");
            if (Crises.Cur != null) bits.Add("la galassia è alle prese con: " + Crises.Cur.title.ToLowerInvariant());
            if (Morale.Title() != "Sconosciuti") bits.Add($"nei porti ci chiamano «{Morale.Title()}»");
            return string.Join("; ", bits) + ".";
        }

        public static string Write(bool force = false)
        {
            var news = St.log.Skip(Math.Max(0, lastChronicle)).Select(e => Clean(e.text)).Where(x => x.Length > 0).ToList(); lastChronicle = St.log.Count;
            if (news.Count == 0 && !force) return null;
            var pick = news.Where(t => Important.Any(k => t.Contains(k))).TakeLast(3).ToList(); if (pick.Count == 0) pick = news.TakeLast(2).ToList();
            string open = Pick("Diario del capitano", "Diario del capitano, supplemento", "Diario personale del capitano");
            string body = pick.Count == 0 ? "Giornata tranquilla, per una volta." : string.Join(". ", pick) + ".";
            string text = $"{open}, data stellare {Stardate()}. {body} {StatusLine()}";
            St.captainLog.Add(new LogEntry { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stardate = Stardate(), text = text });
            if (St.captainLog.Count > 40) St.captainLog.RemoveAt(0);
            Saga.MarkDirty();
            Director.Notify("DIARIO DEL CAPITANO", text);
            return text;
        }
        static string Pick(params string[] a) => a[rng.Next(a.Length)];

        // Al rientro: le cose più importanti dell'ultima sessione giocata.
        static void Previously()
        {
            if (St.captainLog.Count == 0 && St.log.Count < 3) return;
            var last = St.captainLog.TakeLast(2).Select(e => e.text.Substring(e.text.IndexOf(". ", StringComparison.Ordinal) + 2)).ToList();
            var facts = St.log.TakeLast(12).Select(e => Clean(e.text)).Where(t => Important.Any(k => t.Contains(k))).TakeLast(3).ToList();
            string text = facts.Count > 0 ? string.Join(". ", facts) + "." : last.LastOrDefault() ?? "";
            if (text.Length == 0) return;
            Director.Notify("NELLA PUNTATA PRECEDENTE", text);
        }

        // ---- ambizioni
        sealed class GoalDef { public string id = "", text = "", one = ""; public int[] targets = Array.Empty<int>(); public Func<int> progress = () => 0; }
        static readonly GoalDef[] Defs =
        {
            new() { id = "scoperte", text = "Esplorare {0} luoghi e registrarli nell'Atlante", targets = new[] { 5, 15, 30 }, progress = () => St.discovered },
            new() { id = "leggende", text = "Raggiungere {0} luoghi leggendari", one = "Raggiungere un luogo leggendario", targets = new[] { 1, 3, 5 }, progress = () => St.legendsFound },
            new() { id = "taglie", text = "Abbattere {0} personaggi ricorrenti", targets = new[] { 2, 5, 10 }, progress = () => St.nemesisKills },
            new() { id = "contratti", text = "Portare a termine {0} contratti", targets = new[] { 5, 15, 30 }, progress = () => St.contractsDone },
            new() { id = "crisi", text = "Risolvere {0} crisi galattiche", one = "Risolvere una crisi galattica", targets = new[] { 1, 3, 6 }, progress = () => St.crisesWon },
            new() { id = "alleanze", text = "Essere alleati di {0} fazioni allo stesso tempo", one = "Diventare alleati di una fazione", targets = new[] { 1, 2, 4 }, progress = () => Diplomacy.F.Count(f => Factions.Level(f) >= 2) },
            new() { id = "fama", text = "Arrivare a {0} punti di fama fra i liberi equipaggi", targets = new[] { 150, 400, 900 }, progress = () => St.renown },
            new() { id = "primi", text = "Essere primi nella classifica dei liberi equipaggi", targets = new[] { 1, 1, 1 }, progress = () => Competitors.Rank() == 1 ? 1 : 0 },
            new() { id = "onore", text = "Raggiungere {0} punti d'onore", targets = new[] { 10, 25, 45 }, progress = () => St.onore },
            new() { id = "basi", text = "Fondare {0} basi", one = "Fondare una base", targets = new[] { 1, 3, 5 }, progress = () => St.basesFounded },
            new() { id = "base3", text = "Portare una base al livello {0}", targets = new[] { 2, 3, 3 }, progress = () => St.bases.Where(b => b.state == 1).Select(b => b.level).DefaultIfEmpty(0).Max() },
            new() { id = "commercio", text = "Guadagnare {0} crediti commerciando merci", targets = new[] { 5000, 20000, 60000 }, progress = () => Saga.State.market?.profit ?? 0 },
            new() { id = "ambasciate", text = "Riavvicinare due fazioni con {0} ambasciate riuscite", one = "Riavvicinare due fazioni con un'ambasciata riuscita", targets = new[] { 1, 3, 6 }, progress = () => St.embassies },
        };

        static void Goals()
        {
            var g = St.goals;
            while (g.Count(x => !x.done) < 3)
            {
                var have = g.Where(x => !x.done).Select(x => x.id).ToHashSet();
                var pool = Defs.Where(d => !have.Contains(d.id)).Select(d => (d, lvl: g.Count(x => x.done && x.id == d.id))).Where(x => x.lvl < x.d.targets.Length && x.d.progress() < x.d.targets[x.lvl]).ToList();
                if (pool.Count == 0) break;
                var p = pool[rng.Next(pool.Count)];
                g.Add(new Goal { id = p.d.id, level = p.lvl, target = p.d.targets[p.lvl] }); Saga.MarkDirty(); GalaxyView.Dirty();
            }
            foreach (var x in g.Where(x => !x.done).ToList())
            {
                var d = Defs.FirstOrDefault(y => y.id == x.id); if (d == null) { g.Remove(x); continue; }
                if (d.progress() < x.target) continue;
                x.done = true; Saga.MarkDirty(); GalaxyView.Dirty();
                var ship = Stations.MyShip; var got = Loot.Grant(ship, 4 + Math.Min(2, x.level), 5 + Math.Min(2, x.level), "ambizione");
                int cr = 2000 + x.level * 2000; try { PLServer.Instance.CurrentCrewCredits += cr; } catch { }
                Morale.Change(10, null); Competitors.Renown(40 + x.level * 30);
                string what = Text(d, x.target);
                Director.Notify("AMBIZIONE RAGGIUNTA", $"{what}: fatto. {cr} crediti dal Comando" + (got != null ? $" e {got.Name}" : "") + ". L'equipaggio festeggia.");
                Saga.Chronicle($"Ambizione raggiunta: {what.ToLowerInvariant()}.");
                News.Post("flotta", $"La «{(string)ship.ShipName}» ha raggiunto un traguardo: {what.ToLowerInvariant()}.");
                Service.Add("ambizioni.raggiunte", 1);
            }
            if (g.Count > 40) g.RemoveAll(x => x.done && g.IndexOf(x) < g.Count - 40);
        }

        static string Text(GoalDef d, int target) => target == 1 && d.one.Length > 0 ? d.one : string.Format(d.text, target);

        public static List<string> GoalRows()
        {
            if (!Host) return GalaxyView.Current?.goals ?? new List<string>();
            return St.goals.Where(x => !x.done).Select(x => { var d = Defs.FirstOrDefault(y => y.id == x.id); if (d == null) return ""; int p = Math.Min(x.target, d.progress()); return $"{Text(d, x.target)}   {p}/{x.target}"; }).ToList();
        }

        public static string Describe() => $"data stellare {Stardate()}, giocate {St.playedSeconds / 3600:0.0} ore, voci del diario {St.captainLog.Count}\n      " + string.Join("\n      ", GoalRows().Prepend("ambizioni:"));
    }
}

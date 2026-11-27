// Capi delle fazioni: ogni fazione ha un capo con nome, titolo e carattere (falco, colomba, mercante, zelota),
// un'opinione di noi e un sostegno interno.
// - Il carattere orienta il mondo: i falchi irrigidiscono la diplomazia e spingono la guerra, le colombe distendono,
//   i mercanti favoriscono chi commercia, gli zeloti odiano gli Infetti più di tutto.
// - L'opinione cresce con ciò che facciamo per la fazione e con ciò che quel carattere apprezza.
// - Il sostegno interno segue le sorti della guerra: se crolla, un colpo di stato o un'elezione porta un capo nuovo.
// - Udienze: con una buona reputazione il capo ci riceve nei suoi porti e ci affida un incarico secondo il suo
//   carattere; portarlo a termine vale crediti, reputazione e un'onorificenza.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class Leader { public int faction; public string name = "", temper = ""; public int opinion, approval = 60, since, tasks; }
    public class LeaderTask { public int faction, need, done, until, target = -1; public string kind = ""; }

    public static class Leaders
    {
        static readonly Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static Dictionary<string, Leader> L => Saga.State.leaders ??= new Dictionary<string, Leader>();
        static readonly string[] Tempers = { "falco", "colomba", "mercante", "zelota" };
        static readonly string[] Firsts = { "Aurelia", "Cassio", "Ilaria", "Marius", "Selma", "Tiberio", "Vesna", "Oren", "Livia", "Dragan", "Petra", "Ruben" };
        static readonly string[] Lasts = { "Valdés", "Okonkwo", "Hartmann", "Lindgren", "Moretti", "Sokolov", "Achterberg", "Nakagawa", "Duval", "Ferreira" };
        public static string Title(int f) => f switch { 0 => "Presidente del Consiglio coloniale", 1 => "Gran Maestro dell'Alleanza", 2 => "Amministratore delegato della W.D.", _ => "Rettore del Politecnico" };
        public static string TemperName(string t) => t switch { "falco" => "un falco", "colomba" => "una colomba", "mercante" => "un pragmatico dei commerci", _ => "uno zelota" };

        public static Leader Of(int f)
        {
            if (!Saga.Loaded || Array.IndexOf(Diplomacy.F, f) < 0) return null;
            if (L.TryGetValue(f.ToString(), out var l)) return l;
            int sd = 0; try { sd = (int)PLServer.Instance.GalaxySeed; } catch { } int h = (int)((sd * 31L + f * 977) & 0x7fffffff);
            l = new Leader { faction = f, name = $"{Firsts[h % Firsts.Length]} {Lasts[(h / 13) % Lasts.Length]}", temper = Tempers[(h / 7) % Tempers.Length], since = 0 };
            L[f.ToString()] = l; Saga.MarkDirty(); return l;
        }

        // ---- effetti sul mondo
        public static int DiploBias(int a, int b)
        {
            int d = 0;
            foreach (var f in new[] { a, b }) { var l = Of(f); if (l == null) continue; d += l.temper switch { "falco" => -1, "colomba" => 1, "mercante" => rng.NextDouble() < 0.5 ? 1 : 0, _ => 0 }; }
            return d;
        }
        public static void OnRep(int f, int d)
        {
            var l = Of(f); if (l == null || d == 0) return;
            l.opinion = Math.Clamp(l.opinion + d, -100, 100); Saga.MarkDirty();
        }
        // ciò che ogni carattere apprezza in particolare
        public static void OnDeed(string kind, int faction)
        {
            foreach (var l in Diplomacy.F.Select(Of).Where(x => x != null))
            {
                int d = 0;
                if (kind == "commercio" && l.temper == "mercante" && l.faction == faction) d = 2;
                if (kind == "nemico" && l.temper == "falco" && Diplomacy.StatusOf(l.faction, faction) == -1) d = 3;
                if (kind == "infetti" && l.temper == "zelota") d = 3;
                if (kind == "pace" && l.temper == "colomba") d = 3;
                if (d != 0) { l.opinion = Math.Clamp(l.opinion + d, -100, 100); Saga.MarkDirty(); }
                // incarichi in corso
                var t = Saga.State.leaderTask;
                if (t != null && t.faction == l.faction)
                {
                    bool counts = (t.kind == "colpire" && kind == "nemico" && Diplomacy.StatusOf(l.faction, faction) == -1) || (t.kind == "rifornire" && kind == "commercio" && faction == l.faction) || (t.kind == "purificare" && kind == "infetti");
                    if (counts) { t.done++; Saga.MarkDirty(); if (t.done >= t.need) Complete(t, l); }
                }
            }
        }

        static int lastJump = -1;
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                int j = Obscured.Jumps(); if (j == lastJump) return; bool first = lastJump < 0; lastJump = j; if (first) return;
                foreach (var f in Diplomacy.F)
                {
                    var l = Of(f); if (l == null) continue;
                    // il sostegno segue la guerra; il carattere spinge lo slancio
                    float mom = GalaxyWar.Mom(f); l.approval = Math.Clamp(l.approval + (mom > 60 ? 2 : mom < 35 ? -3 : 0) + rng.Next(-1, 2), 0, 100);
                    if (l.temper == "falco") GalaxyWar.Shift(f, 0.4f); else if (l.temper == "colomba") GalaxyWar.Shift(f, -0.2f);
                    if (l.approval < 18 || (j - l.since > 60 && rng.NextDouble() < 0.02)) Succession(l, j);
                }
                // incarico scaduto
                var t = Saga.State.leaderTask; if (t != null && j > t.until) { var l = Of(t.faction); if (l != null) l.opinion = Math.Max(-100, l.opinion - 8); Saga.State.leaderTask = null; Director.Notify("INCARICO FALLITO", $"Non abbiamo portato a termine l'incarico per {Title(t.faction).ToLowerInvariant()}. Non sarà dimenticato."); }
                // colomba: il messaggio di pace si consegna arrivando in un porto della fazione rivale
                if (t != null && t.kind == "pace") { var cur = PLServer.GetCurrentSector(); int cf = cur?.MySPI?.Faction ?? -1; if (cf == t.target && Market.IsPort(cur.ID)) { Diplomacy.Change(t.faction, t.target, 15, "un messaggio di pace portato da un libero equipaggio"); Complete(t, Of(t.faction)); } }
                // udienza: in un porto della fazione, se ci stimano
                var s = PLServer.GetCurrentSector(); int fac = s?.MySPI?.Faction ?? -1;
                if (Saga.State.leaderTask == null && Market.IsPort(s?.ID ?? -1) && Array.IndexOf(Diplomacy.F, fac) >= 0 && Factions.Level(fac) >= 2 && rng.NextDouble() < 0.2) Audience(fac);
                Saga.MarkDirty();
            }
            catch (Exception e) { Plugin.L.LogWarning("Capi delle fazioni: " + e.Message); }
        }

        static void Succession(Leader l, int j)
        {
            string old = l.name; bool coup = l.approval < 18; int h = rng.Next();
            l.name = $"{Firsts[h % Firsts.Length]} {Lasts[(h / 13) % Lasts.Length]}"; l.temper = Tempers.Where(x => x != l.temper).ElementAt(rng.Next(3));
            l.approval = 55 + rng.Next(20); l.since = j; l.opinion = Factions.Value(l.faction) / 2; l.tasks = 0;
            string text = coup ? $"Colpo di scena nella {GalaxyWar.Name(l.faction)}: {old} perde il potere. Al vertice sale {l.name}, {TemperName(l.temper)}." : $"Cambio al vertice della {GalaxyWar.Name(l.faction)}: {l.name}, {TemperName(l.temper)}, prende il posto di {old}.";
            News.Post("fronte", text); Director.Notify("POLITICA", text); Saga.Chronicle(text);
        }

        // ---- udienze e incarichi
        public static Leader Pending;
        static void Audience(int f)
        {
            var l = Of(f); if (l == null) return; Pending = l;
            if (!Encounters.Start("udienza", true)) Pending = null;
        }
        public static string AcceptTask()
        {
            var l = Pending; Pending = null; if (l == null) return "L'udienza è finita.";
            var t = new LeaderTask { faction = l.faction, until = Obscured.Jumps() + 10 };
            switch (l.temper)
            {
                case "falco": t.kind = "colpire"; t.need = 3; break;
                case "mercante": t.kind = "rifornire"; t.need = 3; break;
                case "zelota": t.kind = "purificare"; t.need = 2; break;
                default:
                    var enemies = Diplomacy.F.Where(x => x != l.faction && Diplomacy.StatusOf(l.faction, x) <= 0).ToList();
                    if (enemies.Count == 0) { t.kind = "rifornire"; t.need = 3; break; }
                    t.kind = "pace"; t.need = 1; t.target = enemies[rng.Next(enemies.Count)]; break;
            }
            Saga.State.leaderTask = t; Saga.MarkDirty(); GalaxyView.Dirty();
            return TaskText(t);
        }
        public static string TaskText(LeaderTask t) => t.kind switch
        {
            "colpire" => $"Incarico: abbattere {t.need} navi delle fazioni in guerra con la {GalaxyWar.Name(t.faction)} entro il salto {t.until}.",
            "rifornire" => $"Incarico: portare {t.need} carichi di merci (almeno cinque casse alla volta) nei porti della {GalaxyWar.Name(t.faction)} entro il salto {t.until}.",
            "purificare" => $"Incarico: abbattere {t.need} navi infette entro il salto {t.until}.",
            _ => $"Incarico: portare un messaggio di pace in un porto della {GalaxyWar.Name(t.target)} entro il salto {t.until}.",
        };
        static void Complete(LeaderTask t, Leader l)
        {
            if (l == null) return; Saga.State.leaderTask = null; l.tasks++; l.opinion = Math.Min(100, l.opinion + 20);
            int cr = (int)(2500 * Rules.Reward * (l.opinion >= 80 ? 1.2f : 1f)); try { PLServer.Instance.CurrentCrewCredits += cr; } catch { }
            Factions.Change(l.faction, 5, null); Competitors.Renown(15); Saga.State.onore += 1;
            string medal = l.temper switch { "falco" => "Croce al valore", "mercante" => "Sigillo della Gilda", "zelota" => "Stella della purificazione", _ => "Ramo d'ulivo" };
            Trophies.Add($"{medal} della {GalaxyWar.Name(l.faction)}", Title(l.faction));
            Feats.LeaderTask(l.opinion);
            Director.Notify("INCARICO COMPIUTO", $"{l.name}, {Title(l.faction).ToLowerInvariant()}, ci conferisce la {medal.ToLowerInvariant()}. {cr} crediti e la gratitudine della {GalaxyWar.Name(l.faction)}.");
            News.Post("fronte", $"La «{(string)Stations.MyShip.ShipName}» ha ricevuto la {medal.ToLowerInvariant()} da {l.name}.");
            Saga.MarkDirty(); GalaxyView.Dirty();
        }
        // da grandi amici, compensi migliori nei contratti della fazione
        public static float RewardBonus(int f) { var l = Host ? Of(f) : null; return l != null && l.opinion >= 80 ? 0.1f : 0f; }

        public static string Line()
        {
            var t = Saga.State.leaderTask; return t == null ? "" : $"incarico per la {GalaxyWar.Name(t.faction)}: {t.done}/{t.need}";
        }
        public static string Describe() => string.Join("\n      ", Diplomacy.F.Select(Of).Where(x => x != null).Select(l => $"{GalaxyWar.Name(l.faction)}: {l.name}, {l.temper}, opinione {l.opinion}, sostegno {l.approval}, incarichi {l.tasks}").Prepend("capi:")) + (Saga.State.leaderTask != null ? "\n      " + TaskText(Saga.State.leaderTask) + $" ({Saga.State.leaderTask.done}/{Saga.State.leaderTask.need})" : "");
        public static string ForceAudience() { var f = Diplomacy.F.OrderByDescending(Factions.Level).First(); Audience(f); return Pending != null ? "udienza con " + Of(f).name : "nessuna udienza"; }
        public static string ForceCoup(int f) { var l = Of(f); if (l == null) return "fazione sconosciuta"; l.approval = 0; lastJump = -2; Succession(l, Obscured.Jumps()); return Describe(); }
    }
}

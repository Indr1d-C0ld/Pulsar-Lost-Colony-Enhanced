// Identità dell'equipaggio e calendario della galassia.
// - Vocazione: nasce da ciò che l'equipaggio fa davvero (esplorare, commerciare, cacciare taglie, fare diplomazia,
//   contrabbandare, proteggere i deboli). La più praticata diventa il nome dell'equipaggio nei porti, sale di grado
//   (I, II, III) e dà vantaggi concreti in quel mestiere.
// - Stagioni: ogni 15-25 salti la galassia entra per qualche salto in un periodo speciale che cambia le regole
//   (regata della Gilda, fiera dell'Accademia, mercato franco, stagione delle tempeste, festa della tregua, grande caccia).
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class SeasonState { public string kind = ""; public int until, next = -1; }

    public static class Calling
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static Dictionary<string, int> C => Saga.State.calling ??= new Dictionary<string, int>();

        public static readonly Dictionary<string, (string name, string perk)> Kinds = new()
        {
            ["esplorazione"] = ("Esploratori", "dati dell'Atlante pagati di più, frammenti di mappa più frequenti"),
            ["commercio"] = ("Mercanti", "trasporti pagati di più, dogane meno pignole"),
            ["caccia"] = ("Cacciatori di taglie", "taglie più ricche e più frequenti"),
            ["diplomazia"] = ("Diplomatici", "ambasciate più efficaci, reputazione che sale più in fretta"),
            ["corsari"] = ("Corsari", "contrabbando pagato di più, mercato nero più generoso"),
            ["guardiani"] = ("Guardiani della Frontiera", "le scelte onorevoli rendono più morale e più fama"),
            ["coloni"] = ("Coloni della Frontiera", "le nostre basi rendono di più"),
        };
        static readonly string[] Roman = { "", "I", "II", "III" };

        public static void Add(string kind, int n = 1)
        {
            if (!Host || !Saga.Loaded || !Kinds.ContainsKey(kind)) return;
            var (k0, l0) = Current(); C[kind] = C.GetValueOrDefault(kind) + n; Saga.MarkDirty(); GalaxyView.Dirty();
            var (k1, l1) = Current();
            if (k1 != null && (k1 != k0 || l1 != l0))
            {
                string t = $"Nei porti ormai vi conoscono come {Kinds[k1].name} (grado {Roman[l1]}): {Kinds[k1].perk}.";
                Director.Notify("VOCAZIONE", t); Saga.Chronicle(t); News.Post("flotta", $"La «{Ship()}» si fa un nome: {Kinds[k1].name.ToLowerInvariant()}.");
            }
        }
        static string Ship() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }

        // Vocazione prevalente e grado (0 = nessuna).
        public static (string kind, int level) Current()
        {
            if (C.Count == 0) return (null, 0);
            var top = C.OrderByDescending(x => x.Value).First(); int lvl = top.Value >= 35 ? 3 : top.Value >= 15 ? 2 : top.Value >= 5 ? 1 : 0;
            return lvl == 0 ? (null, 0) : (top.Key, lvl);
        }
        public static int Level(string kind) { var (k, l) = Current(); return k == kind ? l : 0; }
        // Moltiplicatore per il mestiere indicato: +12% a grado.
        public static float Bonus(string kind) => 1f + Level(kind) * 0.12f;
        public static string Label() { if (!Host) return GalaxyView.Current?.calling ?? ""; var (k, l) = Current(); return k == null ? "" : $"{Kinds[k].name} {Roman[l]}"; }

        // ---- stagioni
        public static readonly Dictionary<string, (string name, string text)> Seasons = new()
        {
            ["regata"] = ("Regata della Gilda", "La Gilda dei Mercanti apre la regata della stagione: sfide di velocità ovunque, premi raddoppiati."),
            ["fiera"] = ("Fiera dell'Accademia", "Si apre la fiera dell'Accademia delle Scienze: i dati dell'Atlante valgono il doppio, ogni scoperta fa notizia."),
            ["franco"] = ("Mercato franco", "Le fazioni proclamano il mercato franco: niente controlli doganali e trasporti pagati meglio, finché dura."),
            ["tempeste"] = ("Stagione delle tempeste", "Comincia la stagione delle tempeste ioniche: anomalie di bordo più frequenti, navigate con prudenza."),
            ["tregua"] = ("Festa della tregua", "È la festa della tregua: le fazioni rallentano la guerra e i diplomatici lavorano senza sosta."),
            ["caccia"] = ("Grande caccia", "Il Comando di Flotta bandisce la grande caccia: taglie maggiorate e personaggi ricorrenti allo scoperto."),
        };
        static SeasonState S => Saga.State.season ??= new SeasonState();
        public static string Season => Host ? (S.kind.Length > 0 && Obscured.Jumps() < S.until ? S.kind : "") : GalaxyView.Current?.season ?? "";
        public static bool Is(string kind) => Season == kind;
        public static string SeasonName() { var k = Season; return k.Length > 0 && Seasons.TryGetValue(k, out var s) ? s.name : ""; }

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                var s = S; int j = Obscured.Jumps();
                if (s.next < 0) { s.next = j + rng.Next(8, 15); Saga.MarkDirty(); }
                if (s.kind.Length > 0 && j >= s.until)
                {
                    News.Post("flotta", $"Si chiude la stagione: {Seasons[s.kind].name}."); s.kind = ""; s.next = j + rng.Next(15, 26); Saga.MarkDirty(); GalaxyView.Dirty();
                }
                if (s.kind.Length == 0 && j >= s.next) Start(null);
            }
            catch (Exception e) { Plugin.L.LogWarning("Stagioni: " + e.Message); }
        }
        public static string Start(string kind)
        {
            var s = S; var keys = Seasons.Keys.ToList(); kind ??= keys[rng.Next(keys.Count)];
            if (!Seasons.ContainsKey(kind)) return "stagione sconosciuta";
            s.kind = kind; s.until = Obscured.Jumps() + rng.Next(6, 11); Saga.MarkDirty(); GalaxyView.Dirty();
            Director.Notify("STAGIONE · " + Seasons[kind].name.ToUpperInvariant(), Seasons[kind].text);
            News.Post("flotta", Seasons[kind].text);
            Saga.Chronicle($"Comincia la stagione: {Seasons[kind].name}.");
            return Seasons[kind].name;
        }

        public static string Describe() => $"vocazione: {(Label().Length > 0 ? Label() : "nessuna")} ({string.Join(", ", C.Select(x => x.Key + " " + x.Value))}); stagione: {(SeasonName().Length > 0 ? SeasonName() + " fino al salto " + S.until : "nessuna, la prossima al salto " + S.next)}";
    }
}

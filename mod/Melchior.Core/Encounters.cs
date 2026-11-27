// Incontri con scelta: situazioni che capitano arrivando in un settore o durante la calma, e che il capitano risolve
// scegliendo fra due-quattro opzioni dalla Bacheca. Alcune opzioni richiedono qualcuno alla stazione giusta (Scienza,
// Armi, Ingegneria, Pilota), crediti o una reputazione; ogni opzione ha esiti probabili che toccano crediti, bottino,
// danni, reputazione, fama, personaggi ricorrenti, crisi e conseguenze che arrivano qualche salto dopo.
// Gira sull'host; la scelta in corso viene inviata a tutti (evento 177, "enc|"), il capitano risponde dal pannello.
// Se il capitano è un bot (o non c'è) decide lui; se nessuno risponde, dopo due minuti vale l'opzione più prudente.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Melchior
{
    public class EncChoiceView { public string label = "", hint = ""; public bool ok; }
    public class EncView { public int serial; public string title = "", text = ""; public List<EncChoiceView> choices = new(); public int left; }

    public static class Encounters
    {
        // ---- modello
        sealed class Ctx
        {
            public PLShipInfo ship; public float power; public int tier, fac = -1; public string fname = "", sector = "";
            public Figure fig; public readonly Dictionary<string, string> v = new(); public readonly List<string> notes = new();
            public string poi, poiName = ""; // luogo da esplorare (Exploration.cs)
            public bool port;                 // attracco a un porto (incontri dei porti)
            public Officer off;               // ufficiale coinvolto (Officers.cs)
            public Competitor comp;           // equipaggio concorrente (Competitors.cs)
            public Officer off2;              // secondo ufficiale (vita di bordo)
        }
        sealed class Out { public double w = 1; public Func<Ctx, string> run; }
        sealed class Choice { public string label = "", req = "", hint = "", tone = ""; public int cost; public Out[] outs = Array.Empty<Out>(); }
        sealed class Def { public string id = "", title = ""; public double weight = 1; public bool portOnly, onboard; public Func<Ctx, bool> when = _ => true; public Action<Ctx> setup = _ => { }; public Func<Ctx, string> text; public Func<Ctx, Choice[]> choices; }

        static readonly System.Random rng = new();
        static readonly JsonSerializerOptions json = new() { IncludeFields = true };
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }

        static Def cur; static Ctx ctx; static Choice[] curChoices; static int serial, lastJumps = -1, lastEncJump = -99; static float due = -1, deadline, botAt, nextSend; static string lastSent = "";
        static EncView remote; static readonly List<string> recent = new();
        public static EncView Current => Host ? View() : remote;

        // ---- effetti (restituiscono una nota breve per il riepilogo)
        static string Cr(Ctx c, int d) { try { int have = PLServer.Instance.CurrentCrewCredits; d = Math.Max(-have, d); PLServer.Instance.CurrentCrewCredits = have + d; } catch { return ""; } c.notes.Add($"{d:+0;-0} crediti"); return ""; }
        static string Hull(Ctx c, float frac) { try { var st = c.ship.MyStats; float dmg = st.HullMax * frac; st.HullCurrent = Math.Max(1f, st.HullCurrent - dmg); c.notes.Add($"scafo -{dmg:0}"); } catch { } return ""; }
        static string Heal(Ctx c, float frac) { try { var st = c.ship.MyStats; float before = st.HullCurrent; st.HullCurrent = Math.Min(st.HullMax, st.HullCurrent + st.HullMax * frac); float h = st.HullCurrent - before; if (h >= 1) c.notes.Add($"scafo +{h:0}"); } catch { } return ""; }
        static string Sys(Ctx c, string sys, float pts) { string k = "dmx_" + sys; Stations.Set(c.ship.ShipID, k, Math.Clamp(Stations.Get(c.ship.ShipID, k) + pts, 0.01f, 90f)); c.notes.Add($"{Label(sys)} {(pts > 0 ? "danneggiati" : "riparati")}"); return ""; }
        static string Lt(int a, int b, string why) { var g = global::Melchior.Loot.Grant(Stations.MyShip, a, b, why); return g != null ? g.Name : ""; }
        static string Loot(Ctx c, int min, int max) { var g = global::Melchior.Loot.Grant(c.ship, min, max, "incontro"); if (g != null) c.notes.Add("ottenuto " + g.Name); return ""; }
        static string Rep(Ctx c, int f, int d) { if (f < 0 || f == 4 || Array.IndexOf(GalaxyWar.Factions, f) < 0) return ""; Factions.Change(f, d, null); c.notes.Add($"reputazione {Factions.Abbr(f)} {d:+0;-0}"); return ""; }
        static string Fame(Ctx c, int on, int inf) { var s = Saga.State; s.onore = Math.Max(0, s.onore + on); s.infamia = Math.Max(0, s.infamia + inf); Saga.MarkDirty(); if (on != 0) c.notes.Add($"onore {on:+0;-0}"); if (inf != 0) c.notes.Add($"infamia {inf:+0;-0}"); return ""; }
        static string Foes(Ctx c, int n, int shift, string name)
        {
            int made = 0; for (int i = 0; i < n; i++) if (Director.Spawn(c.ship, Director.PickShip(c.power, i == 0 ? shift : -1), Director.Modifiers(c.power), n > 1 ? $"{name} {i + 1}" : name) != null) made++;
            if (made > 0) c.notes.Add(made == 1 ? "1 nave ostile" : $"{made} navi ostili"); return "";
        }
        static string Label(string sys) => sys switch { "shields" => "scudi", "weapons" => "armi", "engines" => "motori", "sensors" => "sensori", "warp" => "curvatura", _ => "supporto vitale" };
        static int Enemy(int f) { var d = GalaxyWar.FrontCounts(f); return d.Count == 0 ? -1 : d.Where(x => x.Key != 4).OrderByDescending(x => x.Value).Select(x => x.Key).DefaultIfEmpty(-1).First(); }

        static Out O(double w, Func<Ctx, string> run) => new() { w = w, run = run };
        static Choice C(string label, string tone, params Out[] outs) => new() { label = label, tone = tone, outs = outs };
        static Choice R(string req, string label, string tone, params Out[] outs) => new() { req = req, label = label, tone = tone, outs = outs };
        static Choice P(int cost, string label, string tone, params Out[] outs) => new() { cost = cost, req = "crediti", label = label, tone = tone, outs = outs };

        // ---- requisiti
        static bool AtStation(PLShipInfo ship, int cls) { try { return PLServer.Instance.GetCachedFriendlyPlayerOfClass(cls, ship) != null; } catch { return false; } }
        static float Dmg(PLShipInfo ship, string sys) => Stations.Get(ship.ShipID, "dmx_" + sys);
        static (bool ok, string why) Check(Ctx c, Choice ch)
        {
            // manovre combinate: servono tutte le stazioni indicate (es. "scienza+pilota")
            if (ch.req.Contains('+'))
            {
                var whys = ch.req.Split('+').Select(r => Check(c, new Choice { req = r, cost = ch.cost })).Where(x => !x.ok).Select(x => x.why).ToList();
                return whys.Count == 0 ? (true, "") : (false, "manovra combinata: " + string.Join("; ", whys));
            }
            switch (ch.req)
            {
                case "scienza": return AtStation(c.ship, 2) && Dmg(c.ship, "sensors") < 60 ? (true, "") : (false, "serve qualcuno alla Scienza, con i sensori in ordine");
                case "armi": return AtStation(c.ship, 3) && Dmg(c.ship, "weapons") < 60 ? (true, "") : (false, "serve qualcuno alle Armi, con le armi in ordine");
                case "ingegneria": return AtStation(c.ship, 4) && Stations.Get(c.ship.ShipID, "core_lock") <= 0 ? (true, "") : (false, "serve qualcuno all'Ingegneria, con il nucleo attivo");
                case "pilota": return AtStation(c.ship, 1) && Dmg(c.ship, "engines") < 60 ? (true, "") : (false, "serve qualcuno al timone, con i motori in ordine");
                case "crediti": { int have = 0; try { have = PLServer.Instance.CurrentCrewCredits; } catch { } return have >= ch.cost ? (true, "") : (false, $"servono {ch.cost} crediti"); }
                case "alleati": return c.fac >= 0 && Factions.Level(c.fac) >= 2 ? (true, "") : (false, "serve l'alleanza con questa fazione");
                case "infamia": return Saga.State.infamia >= 3 ? (true, "") : (false, "serve una certa fama di spietatezza");
                case "onore": return Saga.State.onore >= 6 ? (true, "") : (false, "serve un nome rispettato nei porti");
                case "nulla": return (false, "non c'è niente di nuovo da vendere");
                case "amici": return (false, "serve un'amicizia di lunga data con loro");
                default: return (true, "");
            }
        }
        static string ReqTag(Choice ch) => ch.req.Contains('+') ? "[" + ch.req.ToUpperInvariant() + "] " : ch.req switch { "scienza" => "[SCIENZA] ", "armi" => "[ARMI] ", "ingegneria" => "[INGEGNERIA] ", "pilota" => "[PILOTA] ", "crediti" => $"[{ch.cost} CR] ", "alleati" => "[ALLEATI] ", "infamia" => "[INFAMIA] ", "onore" => "[ONORE] ", _ => "" };

        // ---- catalogo
        static readonly List<Def> Defs = new()
        {
            new Def
            {
                id = "avaria", title = "Nave in avaria", weight = 1.4,
                setup = c => c.v["nave"] = Pick("Stella di Kepler", "Gabbiano", "Laboriosa", "Madre Vesta", "Seconda Occasione", "Tartaruga"),
                text = c => $"La «{c.v["nave"]}», un mercantile {(c.fac >= 0 ? "della " + c.fname : "indipendente")}, è alla deriva con il nucleo spento. Il capitano chiede aiuto: hanno bambini a bordo e poca aria.",
                choices = c => new[]
                {
                    R("ingegneria", "Mandare una squadra a riavviare il loro nucleo", "onore", O(0.8, x => { Rep(x, x.fac, 7); Fame(x, 2, 0); Saga.Later("gratitudine", 2, 4, x.v["nave"], "", 1200 + x.tier * 800); return "Il nucleo riparte al terzo tentativo. Dalla loro plancia arrivano applausi e una promessa: non vi dimenticheranno."; }),
                                                                                          O(0.2, x => { Sys(x, "life", 25); Rep(x, x.fac, 7); Fame(x, 2, 0); return "Il nucleo riparte, ma il ritorno di fiamma investe la nostra squadra: supporto vitale danneggiato. Ne valeva la pena."; })),
                    P(400, "Cedere celle di energia e scorte d'aria", "onore", O(1, x => { Cr(x, -400); Rep(x, x.fac, 5); Fame(x, 1, 0); Saga.Later("gratitudine", 2, 5, x.v["nave"], "", 900 + x.tier * 600); return "Le scorte passano attraverso il condotto di attracco. Il capitano vi stringe la mano attraverso il vetro."; })),
                    C("Abbordarli e prendere il carico", "infamia", O(0.7, x => { Cr(x, 600 + x.tier * 400); Loot(x, 1, 4); Rep(x, x.fac, -12); Fame(x, 0, 3); return "Nessuna resistenza: non ne hanno la forza. La stiva è nostra; i loro sguardi resteranno con noi."; }),
                                                                O(0.3, x => { Cr(x, 400); Rep(x, x.fac, -12); Fame(x, 0, 3); Foes(x, 1 + x.tier / 2, 0, "Scorta del convoglio"); return "Prendiamo il carico, ma il segnale di soccorso è arrivato a una scorta armata. Eccola."; })),
                    C("Proseguire: non è affar nostro", "", O(1, x => { News.Post("commercio", $"La «{x.v["nave"]}» è stata trovata alla deriva nel settore {Places.Of(x.sector)}. Nessuno si è fermato ad aiutarla."); return "La «" + x.v["nave"] + "» resta indietro, un puntino che si spegne sui sensori."; })),
                },
            },
            new Def
            {
                id = "capsula", title = "Capsula di salvataggio", weight = 1.3,
                text = c => "I sensori agganciano una capsula di salvataggio alla deriva. Il segnale vitale è debole. Nessun transponder, nessuna nave madre nei dintorni.",
                choices = c => new[]
                {
                    R("scienza+armi", "Scansionare tenendo i cannoni puntati", "onore", O(1, x => { Fame(x, 1, 0); Loot(x, 3, 5); Saga.Later("informatore", 2, 4, "il naufrago"); return "La Scienza scansiona, le Armi coprono: se è un'esca, non funzionerà. Non lo è: un naufrago stremato, e accanto a lui un carico di valore."; })),
                    R("scienza", "Scansionare la capsula prima di aprirla", "", O(0.55, x => { Fame(x, 1, 0); Saga.Later("informatore", 2, 4, "il naufrago"); return "La scansione è pulita: dentro c'è un naufrago stremato, che giura di ripagarvi con un'informazione preziosa."; }),
                                                                                  O(0.45, x => { Loot(x, 2, 5); return "La scansione rivela una capsula vuota usata come contenitore: dentro, un pezzo di valore nascosto da qualcuno."; })),
                    C("Agganciarla e aprirla subito", "", O(0.5, x => { Fame(x, 1, 0); Saga.Later("informatore", 2, 4, "il naufrago"); return "Un naufrago, vivo per miracolo. Quando si riprende promette di ricambiare."; }),
                                                          O(0.3, x => { Foes(x, 1 + x.tier / 2, 0, "Predoni in agguato"); return "È un'esca: appena la capsula è a bordo, i predoni escono dall'ombra di un asteroide."; }),
                                                          O(0.2, x => { Sys(x, "life", 30); return "Dentro c'è solo un contagio: i filtri del supporto vitale si intasano prima che riusciamo a sigillare il compartimento."; })),
                    C("Lasciarla dov'è", "", O(1, x => "Il segnale vitale si allontana. Qualcun altro, forse, la troverà.")),
                },
            },
            new Def
            {
                id = "asilo", title = "Richiesta di asilo", weight = 1.1,
                when = c => c.fac >= 0 && c.fac != 4,
                setup = c => { c.v["nemico"] = Enemy(c.fac).ToString(); c.v["chi"] = Pick("un ufficiale di rotta", "un tecnico degli armamenti", "un cartografo", "un medico di bordo", "un pilota di caccia"); },
                text = c => $"Una navetta della {c.fname} chiede di attraccare: a bordo c'è {c.v["chi"]} che ha disertato e chiede asilo. Dice di avere dati riservati. La {c.fname} ne chiede la consegna immediata.",
                choices = c =>
                {
                    int en = int.Parse(c.v["nemico"]);
                    return new[]
                    {
                        C("Concedere asilo", "onore", O(1, x => { Rep(x, x.fac, -10); if (en >= 0) Rep(x, en, 6); Loot(x, 2, 5); Fame(x, 1, 0); Saga.Later("incontro", 2, 5, "seguito_asilo", x.v["chi"], x.fac); return $"Il disertore sale a bordo con i suoi dati. La {x.fname} non la prenderà bene."; })),
                        C("Consegnarlo alla sua fazione", "", O(0.8, x => { Rep(x, x.fac, 9); Cr(x, 500 + x.tier * 300); return "La consegna avviene senza una parola. La ricompensa arriva puntuale, gli sguardi della nostra plancia un po' meno."; }),
                                                          O(0.2, x => { Rep(x, x.fac, 9); Fame(x, 0, 1); News.Post("dicerie", $"Si dice che la «{Ship()}» abbia consegnato un disertore alla {x.fname}. Nei porti c'è chi non lo dimenticherà."); return "Lo consegniamo. Qualcuno, nei porti, l'ha visto."; })),
                        R("ingegneria", "Copiare i suoi dati, poi consegnarlo", "infamia", O(0.7, x => { Rep(x, x.fac, 6); if (en >= 0) Rep(x, en, 3); Cr(x, 800 + x.tier * 400); Fame(x, 0, 2); return "I dati finiscono nei nostri archivi e il disertore nelle loro celle. Doppio guadagno, coscienza a parte."; }),
                                                                                                     O(0.3, x => { Rep(x, x.fac, -8); Fame(x, 0, 2); return "La fazione si accorge della copia: non gradisce affatto il nostro doppio gioco."; })),
                    };
                },
            },
            new Def
            {
                id = "anomalia", title = "Anomalia spaziale", weight = 1.2,
                setup = c => c.v["cosa"] = Pick("una fenditura subspaziale che pulsa a intervalli regolari", "una nube di tachioni che riflette la luce di stelle inesistenti", "un vortice gravitazionale grande quanto una luna", "un'eco della nostra nave, sfasata di qualche secondo nel tempo"),
                text = c => $"Davanti a noi c'è {c.v["cosa"]}. La Scienza è in fibrillazione: niente del genere è negli archivi.",
                choices = c => new[]
                {
                    R("scienza+pilota", "Studiarla da vicino, in assetto stabile", "", O(1, x => { Loot(x, 4, 6); Fame(x, 2, 0); Heal(x, 0.15f); return "Il pilota tiene la nave sul bordo dell'anomalia mentre la Scienza raccoglie dati per ore: un lavoro da manuale, e un frammento di materia esotica come ricompensa."; })),
                    R("scienza", "Studiarla con i sensori a piena potenza", "", O(0.75, x => { Loot(x, 3, 6); Fame(x, 2, 0); News.Post("scienza", $"L'Accademia delle Scienze ringrazia la «{Ship()}» per i dati sull'anomalia del settore {Places.Of(x.sector)}."); return "I dati raccolti valgono una carriera accademica. E l'anomalia ci lascia un regalo: un frammento di materia esotica."; }),
                                                                                    O(0.25, x => { Sys(x, "sensors", 40); Loot(x, 2, 4); return "I sensori si saturano e si bruciano, ma qualcosa resta nei collettori."; })),
                    R("pilota", "Attraversarla sfiorandone il bordo", "", O(0.6, x => { Cr(x, 500 + x.tier * 300); Heal(x, 0.15f); return "Il pilota infila il bordo dell'anomalia: gli scudi si ricaricano di energia esotica e lo scafo sembra ringiovanito."; }),
                                                                            O(0.4, x => { Hull(x, 0.2f); Sys(x, "warp", 30); return "Troppo vicino: le sollecitazioni piegano le gondole di curvatura."; })),
                    C("Tenersi a distanza", "", O(1, x => "Registriamo le coordinate e proseguiamo. Forse qualcun altro avrà più coraggio.")),
                },
            },
            new Def
            {
                id = "contrabbando", title = "Contrabbandieri", weight = 1.2,
                setup = c => c.v["merce"] = Pick("componenti militari senza matricola", "armi di una fazione che non esiste più", "un pezzo che dicono provenga da un relitto leggendario", "pezzi di ricambio rubati a un cantiere"),
                text = c => $"Una nave senza transponder si affianca. Il loro capo offre {c.v["merce"]}: «Prezzo d'amico. Niente domande, niente ricevute.»",
                choices = c => new[]
                {
                    P(1200 + c.tier * 500, "Comprare la merce", "", O(0.7, x => { Cr(x, -(1200 + x.tier * 500)); Loot(x, 3, 6); return "La merce è autentica. Non chiediamo da dove venga."; }),
                                                                    O(0.3, x => { Cr(x, -(1200 + x.tier * 500)); Loot(x, 1, 3); Rep(x, x.fac, -4); Saga.Later("incontro", 2, 4, "seguito_contrabbando", "", x.fac); return "La merce vale meno di quanto pagato, e qualcuno della fazione locale ci ha visti trattare con loro."; })),
                    C("Denunciarli alla fazione locale", "onore", O(1, x => { Rep(x, x.fac, 6); Foes(x, 1 + x.tier / 2, 0, "Contrabbandieri"); return "I contrabbandieri capiscono subito: aprono il fuoco prima che arrivi la pattuglia."; })),
                    R("infamia", "Prendere la merce senza pagare", "infamia", O(0.6, x => { Loot(x, 3, 5); Fame(x, 0, 2); Saga.Later("incontro", 2, 4, "seguito_contrabbando", "", x.fac); return "Con la nostra fama, nessuno ha voglia di discutere. Se ne vanno a mani vuote."; }),
                                                                                    O(0.4, x => { Loot(x, 2, 4); Fame(x, 0, 2); Foes(x, 2, 0, "Contrabbandieri"); return "Prendiamo la merce, ma non la prendono bene."; })),
                    C("Rifiutare e proseguire", "", O(1, x => "«Peccato, capitano. Sarà per un'altra volta.» La nave sparisce in curvatura.")),
                },
            },
            new Def
            {
                id = "ispezione", title = "Ispezione di fazione", weight = 1.1,
                when = c => c.fac >= 0 && c.fac != 4 && Factions.Level(c.fac) <= 1,
                text = c => $"Una nave della {c.fname} ci intima l'alt: «Ispezione di routine. Abbassate gli scudi e preparatevi all'abbordaggio.» Nella stiva abbiamo pezzi di provenienza non sempre limpida.",
                choices = c => new[]
                {
                    C("Accettare l'ispezione", "", O(0.6, x => { if (Contracts.Carrying("contrabbando")) return Confisca(x); Rep(x, x.fac, 3); return "Gli ispettori controllano tutto, non trovano niente da ridire e se ne vanno quasi cordiali."; }),
                                                   O(0.4, x => { Cr(x, -(300 + x.tier * 200)); Rep(x, x.fac, 1); return "Trovano qualcosa che non piace: multa salata, ma niente di peggio."; })),
                    P(500 + c.tier * 200, "Offrire un «contributo volontario»", "infamia", O(0.75, x => { Cr(x, -(500 + x.tier * 200)); return "L'ufficiale conta i crediti e scopre all'improvviso che è tutto in regola."; }),
                                                                                    O(0.25, x => { Cr(x, -(500 + x.tier * 200)); Rep(x, x.fac, -8); return "L'ufficiale accetta i crediti... e li mette agli atti come tentata corruzione."; })),
                    R("pilota", "Rifiutare e scappare", "", O(0.7, x => { Rep(x, x.fac, -5); return "Il pilota ci porta fuori portata prima che possano agganciarci. Ci ricorderanno."; }),
                                                              O(0.3, x => { Rep(x, x.fac, -6); Foes(x, 1, 0, "Pattuglia " + Factions.Abbr(x.fac)); return "Non abbastanza veloci: la pattuglia apre il fuoco."; })),
                    C("Rifiutare e prepararsi al combattimento", "", O(1, x => { Rep(x, x.fac, -10); Foes(x, 1 + (x.power >= 8 ? 1 : 0), 0, "Pattuglia " + Factions.Abbr(x.fac)); return "«Come volete.» Le armi della pattuglia si accendono."; })),
                },
            },
            new Def
            {
                id = "segnale", title = "Segnale di soccorso", weight = 1.2,
                text = c => "Un segnale di soccorso automatico, vecchio di ore, da un punto oltre la nube di detriti. Le coordinate sono precise. Forse troppo.",
                choices = c => new[]
                {
                    R("scienza+pilota", "Avvicinarsi in silenzio e analizzare il segnale", "onore", O(1, x => { Fame(x, 2, 0); Loot(x, 3, 5); Rep(x, x.fac, 4); return "Il pilota ci porta dentro la nube a motori spenti mentre la Scienza legge il segnale: se c'erano predoni in agguato, non ci hanno visti arrivare. I minatori sono salvi, e grati."; })),
                    R("scienza", "Analizzare la firma del segnale", "", O(0.5, x => { Fame(x, 2, 0); Loot(x, 2, 4); Rep(x, x.fac, 4); return "La firma è autentica: un equipaggio minerario intrappolato. Li liberiamo, e insistono per darci qualcosa in cambio."; }),
                                                                             O(0.5, x => { Loot(x, 2, 4); return "La firma è falsa: una trappola dei predoni. La giriamo contro di loro e ci prendiamo l'esca."; })),
                    C("Rispondere al segnale", "onore", O(0.5, x => { Fame(x, 2, 0); Rep(x, x.fac, 4); return "Un equipaggio minerario intrappolato tra i detriti: li tiriamo fuori tutti."; }),
                                                        O(0.5, x => { Foes(x, 2 + x.tier / 2, 0, "Predoni"); return "Nessun minatore: solo predoni che aspettavano chi avrebbe risposto."; })),
                    C("Ignorarlo", "", O(1, x => "Il segnale continua a ripetersi mentre ci allontaniamo.")),
                },
            },
            new Def
            {
                id = "detriti", title = "Campo di battaglia", weight = 1.2,
                text = c => "Qui si è combattuto da poco: rottami ancora caldi, capsule vuote, una nave da guerra spezzata in due. Nessuno risponde alle chiamate.",
                choices = c => new[]
                {
                    R("ingegneria+pilota", "Entrare nel relitto più grande", "", O(1, x => { Loot(x, 4, 6); Cr(x, 600 + x.tier * 300); return "Il pilota infila la nave in una breccia dello scafo spezzato, l'Ingegneria taglia le paratie: la stiva della nave da guerra è ancora intatta."; })),
                    R("ingegneria", "Recupero accurato con le squadre tecniche", "", O(0.8, x => { Loot(x, 3, 5); Cr(x, 300 + x.tier * 200); return "Le squadre smontano con pazienza ciò che vale: un bottino notevole."; }),
                                                                                              O(0.2, x => { Loot(x, 2, 4); Sys(x, "shields", 25); return "Un reattore ancora instabile esplode durante il recupero: scudi danneggiati, ma il pezzo è nostro."; })),
                    C("Raccogliere in fretta ciò che si può", "", O(0.6, x => { Loot(x, 1, 4); return "Qualche pezzo recuperabile, raccolto alla svelta."; }),
                                                                  O(0.4, x => { Hull(x, 0.15f); Loot(x, 1, 3); return "Una mina rimasta attiva ci accoglie a modo suo."; })),
                    C("Recuperare i caduti e trasmettere i nomi", "onore", O(1, x => { Fame(x, 3, 0); foreach (var f in GalaxyWar.Factions) if (f != 4) Factions.Change(f, 1, null); News.Post("flotta", $"La «{Ship()}» ha recuperato i caduti di una battaglia nel settore {Places.Of(x.sector)} e trasmesso i loro nomi alle famiglie."); return "Il lavoro più triste della giornata. Nei porti se ne parlerà con rispetto."; })),
                },
            },
            new Def
            {
                id = "profughi", title = "Profughi", weight = 1.0,
                setup = c => c.v["da"] = Pick("Nuova Ravenna", "Tycho Minore", "Castello di Ghiaccio", "Solaria", "Porto Kepler"),
                text = c => $"Un vecchio trasporto stracarico di profughi da {c.v["da"]} chiede di essere scortato al porto più vicino. Hanno già perso due navi lungo la strada.",
                choices = c => new[]
                {
                    C("Scortarli", "onore", O(0.6, x => { Fame(x, 3, 0); Saga.Later("incontro", 3, 6, "seguito_profughi", x.v["da"]); return "Il viaggio è lungo ma tranquillo. Prima di separarci, i profughi fanno una colletta."; }),
                                            O(0.4, x => { Fame(x, 3, 0); Foes(x, 1 + x.tier / 2, 0, "Predoni"); return "I predoni che li inseguivano ci raggiungono: stavolta dovranno passare da noi."; })),
                    C("Accoglierne alcuni a bordo", "onore", O(0.75, x => { Fame(x, 2, 0); return "Una dozzina di persone in più a bordo: cucina affollata, ma nessuno si lamenta."; }),
                                                             O(0.25, x => { Fame(x, 2, 0); Saga.Later("sabotaggio", 1, 3, "i profughi"); return "Una dozzina di persone in più a bordo. Una di loro non è chi dice di essere."; })),
                    C("Indicare la rotta e proseguire", "", O(1, x => "Trasmettiamo la rotta più sicura che conosciamo. Il resto dipende da loro.")),
                },
            },
            new Def
            {
                id = "duello", title = "Sfida d'onore", weight = 0.8,
                when = c => c.power >= 3,
                setup = c => c.v["chi"] = Pick("Varek", "Ottavia Rhun", "Tor Massey", "Kael Vantor", "Imre Saldan"),
                text = c => $"Il capitano {c.v["chi"]}, mercenario di una certa fama, ci sfida: uno scontro leale, nave contro nave, niente rinforzi. «Il vincitore si prende la stima dei porti, e il carico dell'altro.»",
                choices = c => new[]
                {
                    C("Accettare la sfida", "onore", O(1, x => { Fame(x, 2, 0); if (Director.Spawn(x.ship, Director.PickShip(x.power, 1), Director.Modifiers(x.power) | 1 << (int)EShipModifierType.ELITE, "Capitano " + x.v["chi"]) != null) x.notes.Add("duello"); return "«Così si fa.» La nave dello sfidante si mette in posizione."; })),
                    R("armi", "Accettare, ma con un colpo a sorpresa", "infamia", O(1, x => { Fame(x, 0, 2); var s = Director.Spawn(x.ship, Director.PickShip(x.power, 1), Director.Modifiers(x.power), "Capitano " + x.v["chi"]); if (s?.MyStats != null) { s.MyStats.ShieldsCurrent = 0; x.notes.Add("scudi nemici a zero"); } return "Le Armi sparano prima del segnale: gli scudi dello sfidante sono già a terra. Non è leale, ma è efficace."; })),
                    C("Declinare", "", O(1, x => { News.Post("dicerie", $"Nei porti si ride della «{Ship()}», che ha declinato la sfida del capitano {x.v["chi"]}."); return "«Come immaginavo.» Lo sfidante se ne va ridendo."; })),
                },
            },
            new Def
            {
                id = "sonda", title = "Sonda sconosciuta", weight = 1.0,
                text = c => "Una sonda di fattura sconosciuta ci segue da due salti. Ora si avvicina e trasmette una sequenza di numeri primi.",
                choices = c => new[]
                {
                    R("scienza+ingegneria", "Collegarsi alla sonda e leggerne la memoria", "", O(1, x => { Fame(x, 3, 0); Loot(x, 5, 6); Trophies.Add("Memoria della sonda sconosciuta", "manovra combinata"); return "L'Ingegneria apre un varco nel suo guscio, la Scienza ne legge la memoria: mappe di stelle che nessuno ha mai visto, e un dono per chi è arrivato fin lì."; })),
                    R("scienza", "Rispondere alla sequenza", "", O(0.7, x => { Fame(x, 2, 0); Loot(x, 3, 6); return "La sonda risponde con una mappa stellare che non riconosciamo e una piccola capsula di dono."; }),
                                                                      O(0.3, x => { Sys(x, "sensors", 35); return "La risposta attiva un protocollo di difesa: un impulso acceca i sensori."; })),
                    R("armi", "Distruggerla", "", O(1, x => { Loot(x, 1, 4); return "Pochi colpi e la sonda si spegne. Tra i frammenti, componenti che nessuno sa costruire."; })),
                    C("Ignorarla", "", O(1, x => "La sonda ci segue ancora per un po', poi si allontana verso le stelle.")),
                },
            },
            new Def
            {
                id = "stazione", title = "Stazione abbandonata", weight = 1.0,
                text = c => "Una stazione di ricerca abbandonata, ancora pressurizzata. Le luci di emergenza lampeggiano in sequenza, come se qualcuno le controllasse.",
                choices = c => new[]
                {
                    R("ingegneria+armi", "Disattivare le difese e poi entrare", "", O(1, x => { Loot(x, 3, 6); Cr(x, 500); return "Le Armi abbattono le torrette una a una mentre l'Ingegneria spegne la rete di sicurezza: la stazione è nostra, senza sorprese."; })),
                    C("Esplorarla", "", O(0.55, x => { Loot(x, 2, 5); return "Laboratori deserti, ma i magazzini sono pieni. Ci prendiamo il meglio."; }),
                                        O(0.45, x => { Foes(x, 2 + x.tier / 2, -1, "Droni di sicurezza"); return "Il sistema di sicurezza della stazione è ancora attivo, e ci considera intrusi."; })),
                    R("ingegneria", "Collegarsi al computer centrale da remoto", "", O(0.8, x => { Loot(x, 2, 5); Cr(x, 400); return "Il computer si apre senza resistenza: inventario, codici e i dati di una ricerca interrotta."; }),
                                                                                       O(0.2, x => { Sys(x, "shields", 30); return "Il computer ci restituisce un virus. Gli scudi perdono la sincronia."; })),
                    C("Lasciarla al suo silenzio", "", O(1, x => "Le luci continuano a lampeggiare mentre ci allontaniamo.")),
                },
            },
            new Def
            {
                id = "ammutinamento", title = "Ammutinamento", weight = 0.8,
                when = c => c.fac >= 0 && c.fac != 4,
                setup = c => c.v["nave"] = Pick("Inflessibile", "Aurora", "Vigilanza", "Fiamma di Teth", "Costanza"),
                text = c => $"Sulla «{c.v["nave"]}», della {c.fname}, l'equipaggio si è ribellato al capitano. Entrambe le parti ci chiamano: il capitano chiede aiuto, gli ammutinati chiedono di non intervenire. «Ci tratta come schiavi.»",
                choices = c => new[]
                {
                    C("Aiutare il capitano", "", O(1, x => { Rep(x, x.fac, 8); Foes(x, 1, 0, "Ammutinati della " + x.v["nave"]); return "Gli ammutinati prendono le armi della nave e si rivoltano contro di noi."; })),
                    C("Stare con gli ammutinati", "", O(1, x => { Rep(x, x.fac, -8); var f = Nemesis.Recruit(x.fac); if (f != null) { x.notes.Add("nuovo personaggio: " + f.Full); return $"Gli ammutinati prendono la nave. Il loro capo, {f.Full}, vi saluta: «Non lo dimenticherò.» Per la {x.fname} ora è nella lista dei ribelli."; } return "Gli ammutinati prendono la nave e spariscono in curvatura."; })),
                    C("Non intervenire", "", O(1, x => "Ascoltiamo le due parti in silenzio, poi ci allontaniamo. Non sapremo mai come è finita.")),
                },
            },
            new Def
            {
                id = "pedaggio", title = "Un vecchio conoscente", weight = 1.0,
                when = c => c.fig != null,
                text = c => $"{c.fig.Full} ci chiama, senza armi spianate per una volta: «Capitano. Questo tratto è mio. Pagate il pedaggio e nessuno si fa male. Oppure no, e ci divertiamo.»",
                choices = c => new[]
                {
                    P(800 + c.fig.rank * 400, "Pagare il pedaggio", "", O(1, x => { Cr(x, -(800 + x.fig.rank * 400)); x.fig.grudge = Math.Max(0, x.fig.grudge - 2); Saga.MarkDirty(); return "«Un piacere fare affari con voi.» Per stavolta ci lascia passare."; })),
                    C("Rifiutare", "", O(1, x => { Nemesis.Appear(x.ship, x.power, x.fig); return "«Speravo lo diceste.»"; })),
                    R("onore", "Ricordargli chi siamo", "onore", O(0.6, x => { x.fig.grudge += 1; Saga.MarkDirty(); return "Un lungo silenzio. Poi chiude la comunicazione e se ne va. Non è finita, ma non è oggi."; }),
                                                                O(0.4, x => { Nemesis.Appear(x.ship, x.power, x.fig); return "«Il vostro nome non vale niente, qui.»"; })),
                },
            },
            new Def
            {
                id = "informatore", title = "Informatore", weight = 0.9,
                when = c => Nemesis.Alive.Any(),
                text = c => "Una voce anonima sul canale privato: «So dove si nasconde qualcuno che cercate. Mille crediti e vi do il settore esatto.»",
                choices = c => new[]
                {
                    P(1000, "Pagare l'informazione", "", O(0.8, x => { Cr(x, -1000); if (Contracts.PostBounty(x.power)) x.notes.Add("taglia sulla bacheca"); return "Le coordinate arrivano subito, con una taglia già pronta sulla bacheca."; }),
                                                         O(0.2, x => { Cr(x, -1000); return "I crediti spariscono, la voce anche. Truffati."; })),
                    R("scienza", "Rintracciare la fonte della trasmissione", "", O(0.6, x => { if (Contracts.PostBounty(x.power)) x.notes.Add("taglia sulla bacheca"); return "La Scienza rintraccia la fonte e i suoi archivi: l'informazione ce la prendiamo gratis."; }),
                                                                                  O(0.4, x => "La fonte si spegne prima che riusciamo a rintracciarla.")),
                    C("Chiudere il canale", "", O(1, x => "Il canale si chiude. Chi era, non lo sapremo.")),
                },
            },
            new Def
            {
                id = "quarantena", title = "Colonia in quarantena", weight = 1.5,
                when = c => Crises.Cur?.kind == "epidemia",
                text = c => "Una colonia in quarantena trasmette un appello: hanno medicine per pochi giorni e il blocco sanitario impedisce ai mercanti di avvicinarsi.",
                choices = c => new[]
                {
                    C("Forzare il blocco e portare le medicine", "onore", O(0.7, x => { Fame(x, 2, 0); Crises.Contribute(30, "medicine alla colonia"); return "Le medicine arrivano. La colonia resisterà."; }),
                                                                          O(0.3, x => { Fame(x, 2, 0); Crises.Contribute(30, "medicine alla colonia"); Sys(x, "life", 35); return "Le medicine arrivano, ma qualcosa sale a bordo con il carico di ritorno."; })),
                    R("scienza", "Sintetizzare un vaccino dai loro campioni", "onore", O(1, x => { Fame(x, 3, 0); Crises.Contribute(45, "vaccino"); return "Il vaccino funziona. Il Comando lo distribuirà a tutte le colonie."; })),
                    C("Rispettare la quarantena", "", O(1, x => "Mandiamo un messaggio di incoraggiamento. È tutto ciò che possiamo fare senza rischiare.")),
                },
            },
            new Def
            {
                id = "fronte", title = "Battaglia sul fronte", weight = 1.3,
                when = c => c.fac >= 0 && c.fac != 4 && Enemy(c.fac) >= 0,
                setup = c => c.v["nemico"] = Enemy(c.fac).ToString(),
                text = c => $"Siamo finiti in mezzo a uno scontro: navi della {c.fname} e della {GalaxyWar.Name(int.Parse(c.v["nemico"]))} si sparano a poca distanza. Entrambe ci chiedono di identificarci e di scegliere da che parte stare.",
                choices = c =>
                {
                    int en = int.Parse(c.v["nemico"]);
                    return new[]
                    {
                        C($"Schierarsi con la {c.fname}", "", O(1, x => { Rep(x, x.fac, 8); Rep(x, en, -8); GalaxyWar.Shift(x.fac, 4); Foes(x, 1 + x.tier / 2, 0, "Nave " + Factions.Abbr(en)); return $"Apriamo il fuoco sulle navi della {GalaxyWar.Name(en)}."; })),
                        C($"Schierarsi con la {GalaxyWar.Name(en)}", "", O(1, x => { Rep(x, en, 8); Rep(x, x.fac, -8); GalaxyWar.Shift(en, 4); Foes(x, 1 + x.tier / 2, 0, "Nave " + Factions.Abbr(x.fac)); return $"Apriamo il fuoco sulle navi della {x.fname}."; })),
                        R("pilota", "Sgusciare via tra i due fuochi", "", O(0.8, x => "Il pilota ci porta fuori dallo scontro senza un graffio."), O(0.2, x => { Hull(x, 0.15f); return "Ne usciamo, ma con qualche colpo vagante nello scafo."; })),
                        C("Dichiararsi neutrali", "", O(1, x => { Rep(x, x.fac, -2); Rep(x, en, -2); return "Nessuna delle due parti apprezza la neutralità, ma ci lasciano andare."; })),
                    };
                },
            },
            // ---- incontri tipici delle fazioni
            new Def
            {
                id = "ricevimento", title = "Un invito formale", weight = 1.0, when = c => c.fac == 1,
                text = c => "Un ufficiale dell'Alliance of Gentlemen, in alta uniforme, ci consegna un biglietto: «Il Commodoro sarà lieto di ricevere il capitano a bordo dell'ammiraglia. Abito da cerimonia gradito. Un dono, altrettanto.»",
                choices = c => new[]
                {
                    P(500 + c.tier * 300, "Partecipare, con un dono adeguato", "", O(1, x => { Cr(x, -(500 + x.tier * 300)); Rep(x, 1, 8); if (rng.NextDouble() < 0.5) Loot(x, 2, 5); return "Una serata di brindisi, discorsi e nodi diplomatici. Il Commodoro ci congeda con calore e un ricordo per la nostra stiva."; })),
                    R("onore", "Partecipare e proporre un brindisi alla Frontiera", "onore", O(1, x => { Rep(x, 1, 12); Fame(x, 1, 0); return "Il nostro nome precede il brindisi. Il Commodoro alza il bicchiere per primo."; })),
                    C("Declinare con cortesia", "", O(1, x => { Rep(x, 1, -2); return "«Che peccato.» Il biglietto torna indietro con un lieve gelo."; })),
                },
            },
            new Def
            {
                id = "prototipo", title = "Un prototipo da collaudare", weight = 1.0, when = c => c.fac == 2,
                text = c => "Un dirigente della W.D. Corporation propone un affare: «Montate questo prototipo per qualche salto e mandateci i dati. Lo tenete voi, ovviamente. Clausole di responsabilità standard.»",
                choices = c => new[]
                {
                    C("Montare il prototipo", "", O(0.6, x => { Loot(x, 4, 6); Rep(x, 2, 5); return "Il prototipo funziona. Meglio del previsto, a quanto pare."; }),
                                                  O(0.4, x => { Loot(x, 3, 5); Rep(x, 2, 5); Sys(x, Stations.SysIds[rng.Next(Stations.SysIds.Length)], 40); return "Il prototipo funziona. Il resto della nave un po' meno."; })),
                    R("ingegneria", "Smontarlo e vendere i progetti alla concorrenza", "infamia", O(1, x => { Cr(x, 1500 + x.tier * 600); Rep(x, 2, -10); Fame(x, 0, 1); return "I progetti valgono una fortuna. La W.D. non sarà contenta di trovarli in giro."; })),
                    C("Rifiutare", "", O(1, x => "«Peccato. Il prossimo equipaggio sarà meno prudente.»")),
                },
            },
            new Def
            {
                id = "esperimento", title = "Esperimento scientifico", weight = 1.0, when = c => c.fac == 5,
                text = c => "Una nave da ricerca della Polytechnic chiede il permesso di provare un nuovo modulatore sui nostri scudi. «Sicuro al novantadue per cento», assicura la ricercatrice capo.",
                choices = c => new[]
                {
                    C("Concedere il permesso", "", O(0.8, x => { Sys(x, "shields", -50); Loot(x, 2, 5); Rep(x, 5, 6); return "Il modulatore regge: gli scudi ne escono riparati e ricalibrati, e i ricercatori ci lasciano un prototipo."; }),
                                                    O(0.2, x => { Sys(x, "shields", 45); Rep(x, 5, 6); return "L'otto per cento. Gli scudi si spengono per un'ora buona."; })),
                    R("scienza", "Collaborare all'esperimento", "onore", O(1, x => { Loot(x, 3, 5); Rep(x, 5, 10); Fame(x, 1, 0); return "La nostra Scienza trova l'errore nei loro calcoli prima che diventi un problema. Ci firmano l'articolo."; })),
                    C("Declinare", "", O(1, x => { Rep(x, 5, -1); return "«Capiamo.» Non sembrano capire."; })),
                },
            },
            new Def
            {
                id = "leva", title = "Richiesta della Colonial Union", weight = 1.0, when = c => c.fac == 0,
                text = c => "Il comando di settore della Colonial Union chiede a tutte le navi civili di cedere scorte e personale tecnico alla flotta di frontiera. La richiesta è cortese. Per ora.",
                choices = c => new[]
                {
                    C("Cedere scorte e tecnici", "onore", O(1, x => { Rep(x, 0, 9); Sys(x, "engines", 25); Fame(x, 1, 0); return "I nostri tecnici migliori passano per qualche giorno alla flotta: i motori ne risentono, la Colonial Union ringrazia."; })),
                    P(800 + c.tier * 300, "Pagare un contributo al posto dei tecnici", "", O(1, x => { Cr(x, -(800 + x.tier * 300)); Rep(x, 0, 5); return "Il contributo viene accettato con una ricevuta in triplice copia."; })),
                    C("Rifiutare", "", O(1, x => { Rep(x, 0, -6); return "Il comando prende nota del nostro rifiuto. In tutti i sensi."; })),
                },
            },
            new Def
            {
                id = "spore", title = "Nube di spore", weight = 1.2, when = c => c.fac == 4 || Crises.Cur?.kind == "epidemia",
                text = c => "Entriamo in una nube di spore dell'infezione: si attaccano allo scafo e cercano le prese d'aria. Ogni secondo conta.",
                choices = c => new[]
                {
                    R("ingegneria", "Sigillare le prese e bruciare le spore con il plasma", "", O(1, x => { Stations.Set(x.ship.ShipID, "core_stress", Math.Min(95f, Stations.Get(x.ship.ShipID, "core_stress") + 25)); x.notes.Add("stress del nucleo +25"); return "Il plasma brucia le spore sullo scafo. Il nucleo protesta, ma l'aria resta pulita."; })),
                    R("pilota", "Uscire dalla nube alla massima spinta", "", O(0.7, x => "Fuori in pochi secondi, con le spore che scivolano via dallo scafo."), O(0.3, x => { Sys(x, "life", 30); return "Non abbastanza in fretta: qualcosa è entrato nei filtri."; })),
                    C("Attraversarla a scudi alzati", "", O(0.5, x => { Sys(x, "shields", 30); return "Gli scudi reggono, ma ne escono corrosi."; }), O(0.5, x => { Sys(x, "life", 35); Hull(x, 0.1f); return "Le spore trovano una via: supporto vitale contaminato."; })),
                },
            },
            new Def
            {
                id = "mercante", title = "Mercante errante", weight = 1.1,
                text = c => "Una nave-bazar dipinta di mille colori si affianca. Il mercante sorride con troppi denti: «Riparazioni, curiosità, mappe di posti che non esistono. Tutto ha un prezzo, capitano.»",
                choices = c => new[]
                {
                    P(2000, "Comprare un frammento di mappa antica", "", O(0.8, x => { Cr(x, -2000); x.notes.Add(Exploration.Fragment("mercante")); return "Il frammento è autentico: combacia con le nostre carte."; }), O(0.2, x => { Cr(x, -2000); return "Il frammento è un falso ben fatto. Il mercante è già lontano."; })),
                    P(600, "Pagare riparazioni rapide", "", O(1, x => { Cr(x, -600); Heal(x, 0.3f); foreach (var sy in Stations.SysIds) Stations.Set(x.ship.ShipID, "dmx_" + sy, Math.Max(0.01f, Stations.Get(x.ship.ShipID, "dmx_" + sy) - 30)); x.notes.Add("sistemi riparati"); return "I suoi tecnici lavorano in fretta e bene. Sorprendentemente bene."; })),
                    P(400, "Comprare voci di corridoio", "", O(1, x => { Cr(x, -400); if (Contracts.PostBounty(x.power)) x.notes.Add("taglia sulla bacheca"); else News.Rumor(); return "Il mercante sa sempre chi è stato visto dove."; })),
                    C("Nessun acquisto", "", O(1, x => "«Tornerò, capitano. Torno sempre.»")),
                },
            },
            new Def
            {
                id = "gara", title = "Una sfida di velocità", weight = 0.8,
                setup = c => c.v["chi"] = Pick("della «Saetta Rossa»", "della «Cometa di Halley»", "del «Fulmine di Sirio»", "della «Libellula»"),
                text = c => $"Il pilota {c.v["chi"]} ci sfida: una corsa fino al faro di navigazione più vicino. Posta: mille crediti. «Se avete il fegato.»",
                choices = c => new[]
                {
                    R("pilota", "Accettare la sfida", "", O(0.65, x => { Cr(x, Calling.Is("regata") ? 2000 : 1000); Fame(x, 1, 0); return "Arriviamo al faro con mezza lunghezza di vantaggio. La posta è nostra."; }), O(0.35, x => { Cr(x, -1000); return "Ci battono di un soffio. Il loro pilota saluta con la mano."; })),
                    C("Accettare e spingere i motori oltre il limite", "", O(0.5, x => { Cr(x, 1000); Sys(x, "engines", 30); return "Vinciamo, ma i motori fumano."; }), O(0.5, x => { Cr(x, -1000); Sys(x, "engines", 30); return "Perdiamo, e i motori fumano lo stesso."; })),
                    C("Declinare", "", O(1, x => "«Peccato. Sarà per la prossima.»")),
                },
            },
            new Def
            {
                id = "nemesi_ferita", title = "Un nemico in difficoltà", weight = 0.9, when = c => c.fig != null,
                text = c => $"Tra i rottami di uno scontro c'è l'ammiraglia di {c.fig.Full}, sventrata e senza energia. Il canale di soccorso è aperto. Nessuno ci ha visti, ancora.",
                choices = c => new[]
                {
                    C("Prestare soccorso", "onore", O(1, x => { x.fig.grudge = 0; x.fig.memory = "debito"; Fame(x, 2, 0); Saga.MarkDirty(); News.Post("taglie", $"Si dice che la «{Ship()}» abbia salvato {x.fig.Full}. Nei porti nessuno ci crede."); return $"Rimettiamo in moto i loro sistemi. {x.fig.first} {x.fig.last} ci guarda a lungo dallo schermo, poi chiude la comunicazione senza una parola."; })),
                    C("Finire l'opera", "infamia", O(1, x => { var f = x.fig; f.alive = false; f.memory = "abbattuto"; Fame(x, 0, 3); Loot(x, 3, 5); Saga.MarkDirty(); News.Post("taglie", $"Il corpo di {f.Full} è stato ritrovato in un'ammiraglia disarmata. Chi sia stato, non lo dice nessuno."); return "Un colpo solo. Nella stiva qualcosa di valore; nella plancia, un silenzio pesante."; })),
                    C("Lasciarlo alla deriva", "", O(1, x => { x.fig.grudge += 1; Saga.MarkDirty(); return "Lasciamo l'ammiraglia ai suoi rottami. Chi la comanda se ne caverà fuori, come sempre, e se lo ricorderà."; })),
                },
            },
            new Def
            {
                id = "rivolta", title = "Malcontento a bordo", weight = 0,
                text = c => "Una delegazione dell'equipaggio si presenta in plancia. Troppi salti, troppi rischi, troppe scelte che non condividono. «Capitano, così non si va avanti.»",
                choices = c => new[]
                {
                    P(1500, "Promettere licenza e una paga extra", "", O(1, x => { Cr(x, -1500); Morale.Change(25, "Paga extra e una promessa mantenuta"); return "La delegazione torna ai posti. Per ora la promessa basta."; })),
                    R("onore", "Parlare all'equipaggio", "onore", O(1, x => { Morale.Change(22, "Il discorso del capitano"); return "Il capitano parla a tutta la nave. Quando finisce, qualcuno applaude. Poi tutti."; })),
                    C("Reprimere la protesta", "infamia", O(0.6, x => { Morale.Change(6, null); Fame(x, 0, 1); return "La protesta finisce. Il silenzio che resta non è pace."; }),
                                                          O(0.4, x => { Morale.Change(3, null); Saga.Later("sabotaggio", 1, 2, "gli scontenti"); return "La protesta finisce. Qualcuno, però, non ha finito con noi."; })),
                    C("Ignorarli", "", O(1, x => { Morale.Change(-5, null); Saga.Later("sabotaggio", 1, 2, "gli scontenti"); return "La delegazione se ne va senza una parola. Brutto segno."; })),
                },
            },
            // ---- carico a bordo
            new Def
            {
                id = "ispezione_carico", title = "Controllo doganale", weight = 3, when = c => Contracts.Carrying("contrabbando") && !Calling.Is("franco") && rng.NextDouble() < Shipyard.Less("stiva") * (1 - 0.15 * Talents.Ours("contrabbandiere")) * Math.Max(0.1, 1 - global::Melchior.Loot.Mine("dogane") + global::Melchior.Loot.Mine("rumore")) / Calling.Bonus("commercio") && c.fac >= 0 && c.fac != 4 && Factions.Level(c.fac) <= 1,
                text = c => $"Un cutter della dogana della {c.fname} ci aggancia con il raggio traente: «Controllo del carico. Aprite la stiva.» Nella stiva c'è {Saga.State.contract?.target}.",
                choices = c => new[]
                {
                    C("Aprire la stiva", "", O(1, x => Confisca(x))),
                    P(900 + c.tier * 300, "Corrompere il doganiere", "infamia", O(0.7, x => { Cr(x, -(900 + x.tier * 300)); return "Il doganiere firma il modulo senza guardare. Il raggio traente si spegne."; }), O(0.3, x => { Cr(x, -(900 + x.tier * 300)); return Confisca(x); })),
                    R("ingegneria", "Schermare la stiva dai loro sensori", "", O(0.75, x => "Il cutter scansiona, non trova niente e ci lascia andare."), O(0.25, x => Confisca(x))),
                    R("pilota", "Spezzare il raggio traente e fuggire", "", O(0.7, x => { Rep(x, x.fac, -6); return "Il raggio si spezza con uno strattone. Il carico è salvo, la nostra fedina meno."; }), O(0.3, x => { Rep(x, x.fac, -6); Foes(x, 1, 0, "Cutter della dogana"); return "Il cutter non ci lascia andare tanto facilmente."; })),
                },
            },
            new Def
            {
                id = "assalto_carico", title = "Il carico fa gola", weight = 2.2, when = c => Contracts.Carrying(),
                setup = c => c.v["capo"] = c.fig != null && c.fig.role == "pirata" ? ", " + c.fig.Full + "," : "",
                text = c => $"Predoni all'orizzonte. Il loro capo{c.v["capo"]} va dritto al punto: «Sappiamo cosa trasportate. Sganciate il carico e nessuno si fa male.»",
                choices = c => new[]
                {
                    C("Sganciare il carico", "", O(1, x => { Contracts.LoseCargo("ceduto ai predoni"); Morale.Change(-3, null); return "Il carico si allontana nel vuoto, agganciato dai predoni. Brucia."; })),
                    R("pilota", "Seminarli", "", O(0.65, x => "Il pilota ci porta via prima che possano chiudere il cerchio."), O(0.35, x => { Foes(x, 1 + x.tier / 2, 0, "Predoni"); return "Ci raggiungono comunque."; })),
                    R("armi", "Colpo d'avvertimento", "", O(0.5, x => "Un colpo solo, preciso, a un metro dalla loro plancia. Ci ripensano."), O(0.5, x => { Foes(x, 2, 0, "Predoni"); return "Lo prendono come un invito."; })),
                    C("Difendere il carico", "", O(1, x => { Foes(x, 2 + x.tier / 2, 0, "Predoni"); return "«Come volete.»"; })),
                },
            },
            new Def
            {
                id = "inviato", title = "Un'offerta per l'inviato", weight = 2.5, when = c => Contracts.Carrying("ambasciata"),
                setup = c => c.v["chi"] = Pick("un gruppo di agenti senza insegne", "un intermediario dalla voce cortese", "il capitano di una nave che finge un'avaria"),
                text = c => $"Si fa avanti {c.v["chi"]}: «Sappiamo chi trasportate. Qualcuno pagherebbe molto perché quei negoziati non cominciassero mai. Tremila crediti per l'inviato, e nessuno saprà niente.»",
                choices = c => new[]
                {
                    C("Rifiutare", "onore", O(0.6, x => "«Come volete.» La comunicazione si chiude. Teniamo gli occhi aperti."), O(0.4, x => { Foes(x, 1 + x.tier / 2, 0, "Sicari"); return "Se non possono comprarlo, proveranno a prenderselo."; })),
                    C("Vendere l'inviato", "infamia", O(1, x => { Cr(x, 3000 + x.tier * 500); Contracts.LoseCargo("l'inviato è stato venduto"); Fame(x, 0, 4); return "L'inviato passa di mano senza capire cosa stia succedendo. I negoziati non cominceranno mai."; })),
                    R("scienza", "Rintracciare chi c'è dietro", "", O(0.7, x => { Fame(x, 1, 0); x.notes.Add("mandanti scoperti"); News.Post("fronte", "Sventato un complotto contro i negoziati di pace: i mandanti sono stati smascherati."); return "La traccia porta a un ufficiale che vuole la guerra a ogni costo. Inoltriamo le prove all'inviato."; }), O(0.3, x => "La traccia si perde in una rete di rimbalzi.")),
                },
            },
            // ---- ufficiali
            new Def
            {
                id = "ufficiale_richiesta", title = "Una richiesta personale", weight = 0.9, when = c => c.off != null,
                setup = c => c.v["cosa"] = Pick("una licenza per assistere alla nascita del primo figlio, su una colonia a due salti da qui", "il permesso di sposarsi al prossimo porto, con tutto l'equipaggio come testimone", "di portare a bordo un vecchio mentore malato, che non ha più nessuno", "di dedicare il prossimo turno di riposo a una commemorazione per i caduti della sua accademia"),
                text = c => $"{char.ToUpper(Officers.Full(c.off)[0]) + Officers.Full(c.off)[1..]}, {Officers.At(c.off.cls)}, chiede udienza al capitano. Chiede {c.v["cosa"]}.",
                choices = c => new[]
                {
                    C("Concedere", "onore", O(1, x => { Morale.Change(6, null); x.notes.Add("morale +6"); Officers.Credit(x.off.cls, 2); return "L'ufficiale ringrazia con la voce che trema. In plancia la notizia gira in un attimo."; })),
                    P(500, "Concedere e organizzare le cose in grande", "onore", O(1, x => { Cr(x, -500); Morale.Change(12, "Una festa a bordo"); x.notes.Add("morale +12"); Officers.Credit(x.off.cls, 3); return "Ne viene fuori una festa che l'equipaggio ricorderà a lungo."; })),
                    C("Negare: il servizio viene prima", "", O(1, x => { Morale.Change(-4, null); x.notes.Add("morale -4"); return "«Signorsì.» L'ufficiale torna al suo posto. Non sorride per giorni."; })),
                },
            },
            new Def
            {
                id = "ufficiale_offerta", title = "Un'offerta d'ingaggio", weight = 0.7, when = c => c.off != null && c.off.rank >= 1,
                text = c => $"Un mercantile di passaggio trasmette un'offerta privata a {Officers.Full(c.off)}, {Officers.At(c.off.cls)}: il doppio della paga e il comando di una nave tutta sua. L'ufficiale viene a parlarne con il capitano.",
                choices = c => new[]
                {
                    P(800 + c.off.rank * 400, "Pareggiare l'offerta", "", O(1, x => { Cr(x, -(800 + x.off.rank * 400)); Morale.Change(2, null); return "Paga pareggiata. L'ufficiale resta, e lo dice a tutti."; })),
                    R("onore", "Ricordare all'ufficiale perché è a bordo", "onore", O(0.7, x => { Morale.Change(5, null); Officers.Promote(x.off); return "L'ufficiale resta. E al capitano viene in mente che una promozione era dovuta da tempo."; }), O(0.3, x => { Officers.Replace(x.off); Morale.Change(-3, null); x.notes.Add("sostituzione con un guardiamarina"); return "Non basta: l'ufficiale accetta l'offerta. Ci lascia con una stretta di mano."; })),
                    C("Lasciarlo andare con la nostra benedizione", "onore", O(1, x => { Officers.Replace(x.off); Fame(x, 1, 0); Saga.Later("gratitudine", 3, 6, Officers.Full(x.off).Split(' ').Last() + ", ex ufficiale della nostra nave", "", 1500); x.notes.Add("sostituzione con un guardiamarina"); return "Salutiamo l'ufficiale con il picchetto d'onore. Un giorno, forse, ricambierà la cortesia."; })),
                },
            },
            // ---- la grande minaccia
            new Def
            {
                id = "campioni", title = "Campioni della minaccia", weight = 1.2, when = c => Threat.T.stage >= 1,
                text = c => $"Una nave di ricerca dell'Accademia ci chiama: «State attraversando le zone toccate da {Threat.T.name}. Ci servono campioni e dati, e chi li raccoglie viene pagato bene.»",
                choices = c => new[]
                {
                    R("scienza", "Raccogliere e analizzare i campioni", "onore", O(0.8, x => { Threat.Add(-6f, "la ricerca della Scienza"); Cr(x, 600 + x.tier * 300); Fame(x, 1, 0); x.notes.Add("minaccia -6"); return "Le analisi rivelano un punto debole. L'Accademia trasmette i risultati a tutte le flotte."; }), O(0.2, x => { Threat.Add(-6f, "la ricerca della Scienza"); Sys(x, "life", 30); x.notes.Add("minaccia -6"); return "I campioni non sono del tutto inerti: qualcosa contamina i filtri."; })),
                    C("Raccogliere i campioni e consegnarli così come sono", "", O(1, x => { Threat.Add(-3f); Cr(x, 400); x.notes.Add("minaccia -3"); return "Consegniamo tutto all'Accademia. Faranno loro."; })),
                    C("Non è affar nostro", "", O(1, x => { Threat.Add(1f); return "La nave di ricerca resta sola davanti al buio."; })),
                },
            },
            // ---- campagne delle fazioni
            new Def
            {
                id = "campagna", title = "Una convocazione", weight = 2, when = c => Campaigns.Candidate() >= 0,
                setup = c => c.v["f"] = Campaigns.Candidate().ToString(),
                text = c => Campaigns.Stories[int.Parse(c.v["f"])].brief + $" «{Campaigns.Stories[int.Parse(c.v["f"])].title}» è un incarico in tre atti, e chi lo porta a termine riceve la più alta onorificenza della {GalaxyWar.Name(int.Parse(c.v["f"]))}.",
                choices = c => new[]
                {
                    C("Accettare l'incarico", "onore", O(1, x => { Campaigns.Begin(int.Parse(x.v["f"])); return "Si comincia. Il comando trasmette le coordinate."; })),
                    C("Chiedere tempo", "", O(1, x => "«Vi aspetteremo. Non troppo a lungo.»")),
                    C("Declinare", "", O(1, x => { int f = int.Parse(x.v["f"]); Saga.State.campaignsFailed.Add(f); Rep(x, f, -3); Saga.MarkDirty(); return "«Ci dispiace. Troveremo qualcun altro.»"; })),
                },
            },
            new Def
            {
                id = "campagna_dilemma", title = "Il secondo atto", weight = 0,
                text = c => Campaigns.Stories.TryGetValue(int.Parse(c.v["argf"]), out var st) ? st.dilemma : "",
                choices = c =>
                {
                    int f = int.Parse(c.v["argf"]); string F = GalaxyWar.Name(f);
                    return f switch
                    {
                        0 => new[]
                        {
                            C("Consegnarlo al Comando", "", O(1, x => { Rep(x, f, 5); Campaigns.Act3(); return "Il Comando ringrazia. Troveremo il comandante da soli."; })),
                            C("Accettare lo scambio: libertà per l'informazione", "", O(1, x => { Fame(x, 1, 0); Campaigns.Act3(); return "L'ufficiale mantiene la parola: le coordinate sono giuste."; })),
                            C("Liberarlo senza condizioni", "onore", O(1, x => { Fame(x, 2, 0); Rep(x, f, -4); Campaigns.Act3(); return "«Perché?» chiede. «Perché la guerra finirà, un giorno.» Il Comando storce il naso, ma la campagna continua."; })),
                        },
                        1 => new[]
                        {
                            C("Rispettare la volontà del Commodoro", "", O(1, x => { Rep(x, f, 5); Campaigns.Act3(); return "Il figlio resta in cella. Il Commodoro non lo nomina più."; })),
                            C("Organizzare l'incontro fra padre e figlio", "onore", O(0.7, x => { Fame(x, 3, 0); Rep(x, f, 10); Campaigns.Act3(); return "Un'ora chiusi in una sala. Ne escono insieme, in silenzio, e il Commodoro vi stringe la mano più a lungo del necessario."; }), O(0.3, x => { Fame(x, 2, 0); Rep(x, f, -5); Campaigns.Act3(); return "L'incontro finisce male. Il Commodoro non vi perdona l'iniziativa, ma l'onore va servito."; })),
                        },
                        2 => new[]
                        {
                            C("Consegnare i dati alla W.D.", "infamia", O(1, x => { Rep(x, f, 6); Fame(x, 0, 1); Campaigns.Act3(); return "I dati spariscono negli archivi del Consiglio. Nessuno ne parlerà mai."; })),
                            C("Cancellarli", "", O(1, x => { Campaigns.Act3(); return "Cancellati. Non è giustizia, ma non è nemmeno complicità."; })),
                            C("Farli trapelare", "onore", O(1, x => { Fame(x, 4, 0); Rep(x, f, -20); Campaigns.Abort("i dati sono stati resi pubblici"); News.Post("fronte", "Scandalo alla W.D. Corporation: esperimenti su una colonia ignara. Le prove arrivano da un equipaggio indipendente."); return "La galassia saprà. La W.D. chiude il contratto e, per un po', anche le sue porte."; })),
                        },
                        _ => new[]
                        {
                            R("scienza", "Ricontrollare i calcoli", "onore", O(0.6, x => { Fame(x, 2, 0); Campaigns.Act3(); return "I calcoli reggono: Aurora è sicura. I sabotatori si sbagliavano, ma in buona fede."; }), O(0.4, x => { Fame(x, 3, 0); Rep(x, f, 5); Campaigns.Act3(); return "C'era un errore. Lo correggiamo insieme alla Polytechnic: Aurora sarà sicura davvero."; })),
                            C("Fidarsi della Polytechnic", "", O(1, x => { Campaigns.Act3(); return "La Polytechnic sa quello che fa. Si spera."; })),
                            C("Liberare i sabotatori", "", O(1, x => { Fame(x, 1, 0); Rep(x, f, -10); Campaigns.Abort("abbiamo liberato i sabotatori"); return "La Polytechnic considera chiusa la collaborazione."; })),
                        },
                    };
                },
            },
            new Def
            {
                id = "prigioniero", title = "Un prigioniero a bordo", weight = 0, when = c => Nemesis.Find(Saga.State.captive) != null,
                setup = c => c.v["chi"] = Saga.State.captive,
                text = c => $"{c.v["chi"]} è nella cella di sicurezza. Il Comando di Flotta pagherebbe per la consegna; la sua banda pagherebbe di più per il ritorno. E poi c'è la coscienza del capitano.",
                choices = c => new[]
                {
                    C("Consegnare alle autorità", "onore", O(1, x => { var f = Nemesis.Find(x.v["chi"]); Cr(x, 1500 + f.rank * 1000); Fame(x, 2, 0); Trophies.Add($"Cattura di {f.Full}", "personaggio catturato"); News.Post("taglie", $"La «{Ship()}» ha consegnato {f.Full} alla giustizia. Senza sparare un colpo di troppo."); Saga.State.captive = ""; return "Il Comando ringrazia, e la galassia ha un problema in meno."; })),
                    C("Chiedere un riscatto alla sua banda", "infamia", O(1, x => { var f = Nemesis.Find(x.v["chi"]); Cr(x, 3000 + f.rank * 1500); Fame(x, 0, 2); Nemesis.Release(f, "sfregiato", f.grudge + 4); Saga.State.captive = ""; return $"Il riscatto arriva puntuale. Prima di andarsene, {f.first} ci guarda come si guarda un debito da riscuotere."; })),
                    C("Lasciare andare", "onore", O(1, x => { var f = Nemesis.Find(x.v["chi"]); Fame(x, 3, 0); Nemesis.Release(f, "debito", 0); Saga.State.captive = ""; return "Una capsula, coordinate di un porto, nessuna condizione. Forse un giorno ce ne ricorderemo entrambi."; })),
                },
            },
            // ---- passeggeri
            new Def
            {
                id = "pax_offerta", title = "Cercano un passaggio", weight = 2.2, portOnly = true, when = c => c.port && Passengers.Capacity - Passengers.Aboard > 0,
                setup = c => Passengers.PrepareOffers(),
                text = c => Passengers.Offers.Count == 0 ? "Al molo nessuno cerca un passaggio, oggi." : $"Al molo c'è chi cerca un passaggio (cabine libere: {Passengers.Capacity - Passengers.Aboard}). " + string.Join(" ", Passengers.Offers.Select((o, i) => $"{(char)('A' + i)}) {Passengers.Describe(o)}.")),
                choices = c =>
                {
                    var l = new List<Choice>();
                    for (int i = 0; i < Passengers.Offers.Count && i < 2; i++) { int k = i; var o = Passengers.Offers[i]; l.Add(C($"Imbarcare {(o.count == 1 ? o.leader : o.name)} ({o.pay} cr)", o.illegal ? "infamia" : o.kind is "profughi" or "pellegrini" ? "onore" : "", O(1, x => Passengers.Board(k)))); }
                    if (Passengers.Offers.Count == 2 && Passengers.Offers.Sum(o => o.count) <= Passengers.Capacity - Passengers.Aboard) l.Add(C("Imbarcare tutti", "", O(1, x => { var a = Passengers.Board(0); var b = Passengers.Board(0); return a + " " + b; })));
                    l.Add(C("Nessun passeggero, grazie", "", O(1, x => { Passengers.Offers.Clear(); return "Le cabine restano vuote."; })));
                    return l.ToArray();
                },
            },
            new Def
            {
                id = "pax_trasbordo", title = "Un trasbordo in volo", weight = 0, when = c => Passengers.TransferKind.Length > 0,
                text = c => Passengers.TransferKind == "profughi" ? "La nave che abbiamo chiamato è stracolma di profughi: chiedono se possiamo prenderne qualcuno a bordo fino a un porto sicuro. Pagano quel che possono." : "I pellegrini chiedono un passaggio per alcuni dei loro: la loro nave è lenta e la festa non aspetta.",
                choices = c => new[]
                {
                    C("Prenderli a bordo", "onore", O(1, x => { var g = Passengers.Transfer(Passengers.TransferKind); Passengers.TransferKind = ""; return g == null ? "Non abbiamo cabine libere per loro." : $"Trasbordo completato: {Passengers.Label(g)}, per il settore {Places.Of(g.dest)}."; })),
                    C("Non possiamo", "", O(1, x => { Passengers.TransferKind = ""; return "Ci salutano lo stesso, e ripartono."; })),
                },
            },
            new Def
            {
                id = "pax_malato", title = "Un passeggero sta male", weight = 0, when = c => Passengers.Focus != null,
                text = c => $"Uno dei passeggeri ({Passengers.Label(Passengers.Focus)}) ha la febbre alta, e gli altri hanno paura che sia contagioso.",
                choices = c => new[]
                {
                    C("Un medicinale dalla dispensa", "onore", O(1, x => Passengers.Treat("medicine"))),
                    R("scienza", "La Scienza lo visita", "", O(1, x => Passengers.Treat("scienza"))),
                    C("Che si arrangi", "infamia", O(1, x => Passengers.Treat("ignora"))),
                },
            },
            new Def
            {
                id = "pax_lite", title = "Lite in corridoio", weight = 0, when = c => Passengers.Focus != null,
                text = c => $"{Passengers.Focus.leader} ha litigato con un membro dell'equipaggio per una cabina, un pasto, un'occhiata. Ora tutti aspettano di vedere cosa farà il capitano.",
                choices = c => new[]
                {
                    C("Ascoltare tutti e mediare", "onore", O(1, x => Passengers.Quarrel("media"))),
                    C("Separarli e chiudere la questione", "", O(1, x => Passengers.Quarrel("separa"))),
                    C("Un giorno in cella per il passeggero", "infamia", O(1, x => Passengers.Quarrel("punisci"))),
                },
            },
            new Def
            {
                id = "pax_clandestino", title = "Un clandestino", weight = 0, when = c => true,
                text = c => "Fra le casse della stiva salta fuori qualcuno che non ha mai comprato un biglietto: magro, affamato, e molto spaventato.",
                choices = c => new[]
                {
                    C("Dargli un lavoro a bordo", "onore", O(1, x => Passengers.Stowaway("arruola"))),
                    C("Lasciarlo al prossimo porto", "", O(1, x => Passengers.Stowaway("porto"))),
                    C("Una capsula di salvataggio, e via", "infamia", O(1, x => Passengers.Stowaway("capsula"))),
                },
            },
            new Def
            {
                id = "pax_nascita", title = "Una nascita a bordo", weight = 0, when = c => Passengers.Focus != null,
                text = c => $"Fra i passeggeri ({Passengers.Label(Passengers.Focus)}) c'è una donna incinta, e il bambino ha deciso di non aspettare il porto.",
                choices = c => new[]
                {
                    C("Tutti in infermeria: festa grande dopo", "onore", O(1, x => Passengers.Birth("nome"))),
                    C("Il medico di bordo se ne occupa", "", O(1, x => Passengers.Birth("medico"))),
                },
            },
            new Def
            {
                id = "pax_deviazione", title = "Una richiesta speciale", weight = 0, when = c => Passengers.Focus != null,
                text = c => $"{Passengers.Focus.leader} chiede di cambiare destinazione: un affare urgente in un porto vicino. Offre un compenso più alto.",
                choices = c => new[]
                {
                    C("Accettare la deviazione", "", O(1, x => Passengers.Detour(true))),
                    C("Si va dove si era detto", "", O(1, x => Passengers.Detour(false))),
                },
            },
            new Def
            {
                id = "pax_ricercato", title = "Cercano il nostro passeggero", weight = 0, when = c => Passengers.Focus != null,
                text = c => $"Una nave di cacciatori di taglie ci chiama: sanno che a bordo c'è {Passengers.Focus.leader}, e vogliono che glielo consegniamo.",
                choices = c => new[]
                {
                    C("Consegnarlo e incassare la taglia", "", O(1, x => Passengers.Hunted("consegna"))),
                    C("Pagare perché se ne vadano (800 crediti)", "", O(1, x => Passengers.Hunted("paga"))),
                    C("Negare tutto", "onore", O(1, x => Passengers.Hunted("nega"))),
                },
            },
            new Def
            {
                id = "pax_spia", title = "Una spia a bordo", weight = 0, when = c => Passengers.Focus != null,
                text = c => $"La Scienza nota accessi notturni ai registri di navigazione: è {Passengers.Focus.leader}, uno dei passeggeri.",
                choices = c => new[]
                {
                    C("Arrestarlo e consegnarlo alle autorità", "onore", O(1, x => Passengers.Spy("arresta"))),
                    C("Farne un doppiogiochista", "infamia", O(1, x => Passengers.Spy("doppio"))),
                    C("Lasciargli copiare registri falsi", "", O(1, x => Passengers.Spy("finge"))),
                },
            },
            // ---- indagini: siti dei segnali, scavi, proposte dell'Accademia
            new Def
            {
                id = "indagine_sos", title = "La nave dell'SOS", weight = 0, when = c => Mysteries.Pending != null,
                text = c => $"Eccola: la «{Mysteries.Pending.ship}», alla deriva da anni, luci di emergenza ancora accese. " + (Mysteries.Pending.decode >= 100 ? "Abbiamo il codice d'accesso dal messaggio decifrato." : Mysteries.Pending.decode >= 60 ? "Il messaggio parlava di «dormienti» a bordo." : "Non sappiamo cosa ci sia dentro."),
                choices = c => new[]
                {
                    R("ingegneria", "Squadra d'abbordaggio, con calma", "", O(1, x => Mysteries.Resolve("metodico", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                    R("scienza", "Scansione dei criosonni prima di entrare", "", O(1, x => Mysteries.Resolve("metodico", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                    C("Entrata rapida: prendere quel che si può", "", O(1, x => Mysteries.Resolve("rapido", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                    C("Lasciarla riposare", "", O(1, x => { Mysteries.Pending = null; return "Certe navi è meglio lasciarle al loro silenzio."; })),
                },
            },
            new Def
            {
                id = "indagine_faro", title = "Il faro antico", weight = 0, when = c => Mysteries.Pending != null,
                text = c => $"Il faro {Mysteries.Of(Mysteries.Pending)} pulsa davanti a noi, come ha fatto per millenni. Sotto, nella roccia, si intravedono strutture sepolte.",
                choices = c => new[]
                {
                    R("scienza", "Registrare e studiare il faro", "", O(1, x => Mysteries.Resolve("metodico", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                    C("Avvicinarsi e attraccare subito", "", O(1, x => Mysteries.Resolve("rapido", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                },
            },
            new Def
            {
                id = "indagine_numeri", title = "Il deposito dei numeri", weight = 0, when = c => Mysteries.Pending != null,
                text = c => "All'origine dei numeri c'è un piccolo contenitore ancorato a un asteroide, con un tastierino e nessun nome. " + (Mysteries.Pending.decode >= 100 ? "Abbiamo la combinazione." : "La combinazione non l'abbiamo decifrata per intero."),
                choices = c => new[]
                {
                    R("ingegneria", "Disinnescare con cura e aprire", "", O(1, x => Mysteries.Resolve("metodico", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                    C("Forzare il deposito", "", O(1, x => Mysteries.Resolve("rapido", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                    C("Lasciarlo dov'è", "", O(1, x => { Mysteries.Pending = null; return "Qualcuno tornerà a prenderlo. O forse no."; })),
                },
            },
            new Def
            {
                id = "indagine_eco", title = "L'eco gravitazionale", weight = 0, when = c => Mysteries.Pending != null,
                text = c => "Lo spazio si piega davanti alla prua: le stelle sullo sfondo ondeggiano come sotto l'acqua. " + (Mysteries.Pending.decode >= 60 ? "Conosciamo la forma dell'anomalia." : "Non sappiamo quanto sia profonda."),
                choices = c => new[]
                {
                    R("pilota", "Manovrare ai margini e misurare", "", O(1, x => Mysteries.Resolve("metodico", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                    R("scienza", "Sonde e misure da lontano", "", O(1, x => Mysteries.Resolve("metodico", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                    C("Entrarci dentro per misure migliori", "", O(1, x => Mysteries.Resolve("rapido", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                },
            },
            new Def
            {
                id = "indagine_canto", title = "Il canto", weight = 0, when = c => Mysteries.Pending != null,
                text = c => "Il canto è ovunque: negli altoparlanti, nello scafo, nei denti. Ci sta chiedendo qualcosa.",
                choices = c => new[]
                {
                    R("scienza", "Rispondere con la grammatica che abbiamo decifrato", "", O(1, x => Mysteries.Resolve("metodico", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                    C("Rispondere d'istinto", "", O(1, x => Mysteries.Resolve("rapido", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                    C("Ascoltare in silenzio e andarsene", "", O(1, x => { Mysteries.Pending = null; Morale.Change(2, null); return "Ascoltiamo finché il canto non si spegne. Nessuno dimenticherà."; })),
                },
            },
            new Def
            {
                id = "indagine_futuro", title = "La trasmissione impossibile", weight = 0, when = c => Mysteries.Pending != null,
                text = c => "All'origine non c'è niente: solo un piccolo trasmettitore con il nostro stemma, consumato da anni che non abbiamo ancora vissuto.",
                choices = c => new[]
                {
                    C("Ascoltare il messaggio fino in fondo", "", O(1, x => Mysteries.Resolve("metodico", (a, b) => Lt(a, b, "indagine"), cr => Cr(x, cr)))),
                },
            },
            new Def
            {
                id = "scavo", title = "Rovine da scavare", weight = 0, when = c => Mysteries.PendingDig != null,
                text = c => $"{Mysteries.Cap(Mysteries.PendingDig.name)}: strutture sepolte, {3 - Mysteries.PendingDig.strata} strati ancora intatti. Scavare richiede tempo nel settore, e il rumore attira visite.",
                choices = c => new[]
                {
                    R("scienza", "Scavo metodico (circa due minuti)", "", O(1, x => Mysteries.BeginDig("metodico"))),
                    C("Scavo rapido (meno di un minuto, si rompe qualcosa)", "", O(1, x => Mysteries.BeginDig("rapido"))),
                    C("Non ora", "", O(1, x => Mysteries.BeginDig("via"))),
                },
            },
            new Def
            {
                id = "scavo_origine", title = "Il mondo d'origine", weight = 0, when = c => Mysteries.PendingDig != null,
                text = c => $"{Mysteries.Cap(Mysteries.PendingDig.name)}. Dopo tutto quello che abbiamo letto, essere qui fa un certo effetto. I custodi non ci lasceranno lavorare in pace.",
                choices = c => new[]
                {
                    R("scienza", "Scavo metodico, sotto la nostra protezione", "", O(1, x => Mysteries.BeginDig("metodico"))),
                    C("Scavo rapido, prima che arrivino altri custodi", "", O(1, x => Mysteries.BeginDig("rapido"))),
                    C("Prima ripulire il settore", "", O(1, x => Mysteries.BeginDig("via"))),
                },
            },
            new Def
            {
                id = "spedizione_rivale", title = "Una spedizione rivale", weight = 0,
                text = c => $"Mentre scaviamo arriva {Mysteries.rivalName}. Sono gentili, armati di scanner e di pazienza, e hanno tutta l'aria di voler restare.",
                choices = c => new[]
                {
                    C("Condividere il sito", "onore", O(1, x => Mysteries.Rivals("condividi"))),
                    C("Pagarli perché se ne vadano (600 crediti)", "", O(1, x => Mysteries.Rivals("paga"))),
                    C("Intimar loro di andarsene", "infamia", O(1, x => Mysteries.Rivals("intima"))),
                    C("Gareggiare: scavare più in fretta", "", O(1, x => Mysteries.Rivals("corsa"))),
                },
            },
            new Def
            {
                id = "accademia_segnale", title = "Un'indagine per l'Accademia", weight = 0,
                text = c => "I ricercatori dell'Accademia delle Scienze hanno captato un segnale che non riescono a rintracciare: le loro navi sono lente. Ci affidano i loro rilevamenti, e pagano bene.",
                choices = c => new[]
                {
                    C("Accettare l'indagine", "onore", O(1, x => { var r = Mysteries.AcademyOffer(); return r.Length > 0 ? $"Accettato: {r}. Il segnale è nella scheda Indagini della Scienza." : "Il segnale si è già spento. Peccato."; })),
                    C("Non abbiamo tempo", "", O(1, x => "I ricercatori annuiscono, delusi.")),
                },
            },
            // ---- relitti alla deriva
            new Def
            {
                id = "relitto", title = "Un relitto alla deriva", weight = 0, when = c => Derelicts.Pending != null,
                text = c => $"{Derelicts.Pending.name} galleggia davanti a noi: luci d'emergenza, squarci nello scafo, nessun segno di vita. Il teletrasporto è pronto. Come procediamo?",
                choices = c => new[]
                {
                    R("ingegneria", "Squadra d'ingegneria, con calma", "", O(1, x => Derelicts.Explore("ingegneria", (a, b) => { var g = global::Melchior.Loot.Grant(Stations.MyShip, a, b, "relitto"); return g != null ? g.Name : ""; }, cr => Cr(x, cr)))),
                    R("scienza", "Prima una scansione completa", "", O(1, x => Derelicts.Explore("scienza", (a, b) => { var g = global::Melchior.Loot.Grant(Stations.MyShip, a, b, "relitto"); return g != null ? g.Name : ""; }, cr => Cr(x, cr)))),
                    C("Assalto rapido: prendere e scappare", "", O(1, x => Derelicts.Explore("assalto", (a, b) => { var g = global::Melchior.Loot.Grant(Stations.MyShip, a, b, "relitto"); return g != null ? g.Name : ""; }, cr => Cr(x, cr)))),
                    C("Lasciarlo stare", "", O(1, x => { Derelicts.Pending = null; return "Certi relitti è meglio non svegliarli."; })),
                },
            },
            // ---- commercio in volo e banca della Gilda
            new Def
            {
                id = "commercio_volo", title = "Un affare in volo", weight = 0, when = c => Market.Deal != null,
                text = c => { var d = Market.Deal; return $"La «{d.ship}» affianca la nostra nave: «Abbiamo {d.qty} casse di {Market.Low(Market.G(d.good).name)}, {d.price} crediti l'una, meno che in porto.»" + (d.want.Length > 0 ? $" E comprerebbero il nostro carico di {Market.Low(Market.G(d.want).name)} a {d.buyBack} crediti la cassa." : ""); },
                choices = c => new[]
                {
                    C("Comprare tutto", "", O(1, x => Market.SkyBuy(99))),
                    C("Comprarne cinque casse", "", O(1, x => Market.SkyBuy(5))),
                    Market.Deal?.want.Length > 0 ? C("Vendere loro il nostro carico", "", O(1, x => Market.SkySell())) : null,
                    C("Niente affari oggi", "", O(1, x => { Market.Deal = null; return "«Peccato. Buona rotta!»"; })),
                },
            },
            new Def
            {
                id = "banca", title = "Lo sportello della Gilda", weight = 0.8, portOnly = true, when = c => c.port && (Saga.State.bank == null || Saga.State.bank.owed <= 0),
                text = c => "Allo sportello della Gilda dei Mercanti un impiegato sorride troppo: «Un prestito, capitano? Al due per cento a salto, scadenza fra quindici salti. Gli esattori, poi, non sono gente paziente.»",
                choices = c => new[]
                {
                    C("Chiedere 5.000 crediti", "", O(1, x => Market.Borrow(5000))),
                    C("Chiedere 15.000 crediti", "", O(1, x => Market.Borrow(15000))),
                    C("No, grazie", "", O(1, x => "«Torni quando vuole. La Gilda è sempre qui.»")),
                },
            },
            // ---- udienza con il capo di una fazione
            new Def
            {
                id = "udienza", title = "Un'udienza", weight = 0, when = c => Leaders.Pending != null,
                text = c => { var l = Leaders.Pending; return $"{l.name}, {Leaders.Title(l.faction).ToLowerInvariant()}, ci riceve nel suo ufficio al porto. È {Leaders.TemperName(l.temper)}, e si vede: " + (l.temper switch { "falco" => "parla solo di fronti e di nemici.", "mercante" => "parla di rotte, di prezzi, di porti affamati.", "zelota" => "parla degli Infetti come di una piaga da estirpare.", _ => "parla di tregue, di vertici, di guerre da evitare." }) + " Ha un incarico per noi."; },
                choices = c => new[] { C("Accettare l'incarico", "onore", O(1, x => Leaders.AcceptTask())), C("Declinare con rispetto", "", O(1, x => { Leaders.Pending = null; return "«Un'altra volta, capitano. La mia porta resta aperta.»"; })) },
            },
            // ---- ordini dei porti
            new Def
            {
                id = "ordine_porto", title = "Un ordine del porto", weight = 0, when = c => Ports.Offered != null,
                text = c => { var o = Ports.Offered; return $"Il capo del porto ha un lavoro per noi: {o.qty} casse di {Market.Low(Market.G(o.good).name)} da consegnare al porto del settore {Places.Of(o.to)} entro il salto {o.until}. Compenso: {o.reward} crediti, oltre al prezzo della merce. Se non arriviamo in tempo, se ne ricorderanno."; },
                choices = c => new[] { C("Accettare l'ordine", "", O(1, x => Ports.AcceptOrder())), C("Non ora", "", O(1, x => { Ports.Offered = null; return "«Peccato. Troverò qualcun altro.»"; })) },
            },
            // ---- taglie, vendette e prestiti nati dalle vite dei capitani
            new Def
            {
                id = "taglia_capitano", title = "Una taglia sulla Frontiera", weight = 0, when = c => Saga.State.hunt != null && !Saga.State.hunt.accepted && !Saga.State.hunt.vendetta,
                text = c => { var h = Saga.State.hunt; return $"Il Comando di Flotta diffonde un avviso: la «{h.ship}» di {h.name}, pirata di livello {h.level}, è stata vista nei pressi del settore {Places.Of(h.sector)}. Taglia: {h.reward} crediti a chi la ferma."; },
                choices = c => new[] { C("Accettare la caccia", "onore", O(1, x => Lives.AcceptHunt())), C("Lasciarla ad altri", "", O(1, x => { Lives.DropHunt(); return "Qualcun altro riscuoterà quella taglia. O nessuno."; })) },
            },
            new Def
            {
                id = "vendetta", title = "Vendetta", weight = 0, when = c => Saga.State.hunt != null && !Saga.State.hunt.accepted && Saga.State.hunt.vendetta,
                text = c => { var h = Saga.State.hunt; return $"Sul canale dei mercanti si sa già chi è stato: la «{h.ship}» di {h.name}. {h.friend} ci aveva aiutati. Ora la «{h.ship}» si nasconde nei pressi del settore {Places.Of(h.sector)}."; },
                choices = c => new[] { C("Dare la caccia all'assassino", "onore", O(1, x => Lives.AcceptHunt())), C("Lasciar perdere", "", O(1, x => { Lives.DropHunt(); Morale.Change(-2, null); return "L'equipaggio non dice niente, ma ci pensa."; })) },
            },
            new Def
            {
                id = "prestito", title = "Un amico in difficoltà", weight = 0, when = c => Lives.Pending != null,
                text = c => { var p = Lives.Pending; return $"{p.name} della «{p.ship}» ci chiama: «I predoni mi hanno svuotato la stiva. Mi servono crediti per ricominciare: ve li restituisco con gli interessi, parola di mercante.»"; },
                choices = c => new[]
                {
                    P(1000, "Prestare 1.000 crediti", "onore", O(1, x => Lives.Lend(1000))),
                    P(3000, "Prestare 3.000 crediti", "onore", O(1, x => Lives.Lend(3000))),
                    C("Non possiamo", "", O(1, x => { Lives.Pending = null; return "«Capisco. Buona rotta, comunque.»"; })),
                },
            },
            // ---- reclutamento: candidati in porto, anche capitani amici del traffico
            new Def
            {
                id = "reclute", title = "Candidati all'imbarco", weight = 1.2, portOnly = true, when = c => c.port && !Ports.NoRecruits(PLServer.GetCurrentSector()?.ID ?? -1),
                setup = c =>
                {
                    var cls = new[] { 1, 2, 3, 4 }.OrderBy(_ => rng.Next()).Take(3).ToList();
                    var traits = Officers.Traits.Keys.ToList(); float pw = Director.Power(c.ship, out _, out _);
                    for (int i = 0; i < 3; i++)
                    {
                        int rank = Math.Clamp((int)(rng.NextDouble() * (1.5 + pw / 6)) + (Ports.Trust(PLServer.GetCurrentSector()?.ID ?? -1) >= 15 ? 1 : 0), 0, 3);
                        c.v["c" + i] = cls[i].ToString(); c.v["n" + i] = Officers.RandomFirst() + " " + Officers.RandomLast(); c.v["t" + i] = traits[rng.Next(traits.Count)]; c.v["r" + i] = rank.ToString(); c.v["f" + i] = (400 + 700 * rank).ToString();
                    }
                    var v = Traffic.Volunteer();
                    if (v != null && rng.NextDouble() < 0.6) { int ci = new[] { 1, 2, 3, 4 }[rng.Next(4)]; c.v["c2"] = ci.ToString(); c.v["n2"] = v.captain; c.v["t2"] = "veterano"; c.v["r2"] = "2"; c.v["f2"] = "0"; c.v["vol"] = v.ship; }
                },
                text = c => "Alla bacheca del porto ci aspettano tre candidati: " + string.Join("; ", Enumerable.Range(0, 3).Select(i => $"{c.v["n" + i]}, {Officers.StationOf(int.Parse(c.v["c" + i])).ToLowerInvariant()}, grado {int.Parse(c.v["r" + i]) + 1}, {Officers.Traits[c.v["t" + i]]}" + (c.v.ContainsKey("vol") && i == 2 ? $" (il capitano del «{c.v["vol"]}», che vuole imbarcarsi con noi)" : ""))) + ". Chi assumiamo prende il posto dell'ufficiale attuale.",
                choices = c => Enumerable.Range(0, 3).Select(i =>
                {
                    int fee = int.Parse(c.v["f" + i]); string label = $"Assumere {c.v["n" + i].Split(' ')[0]} ({Officers.StationOf(int.Parse(c.v["c" + i])).ToLowerInvariant()})";
                    Func<Ctx, string> run = x =>
                    {
                        if (fee > 0) Cr(x, -fee);
                        var parts = x.v["n" + i].Split(' ', 2); var o = Officers.Hire(int.Parse(x.v["c" + i]), parts[0], parts.Length > 1 ? parts[1] : "", x.v["t" + i], int.Parse(x.v["r" + i]));
                        if (x.v.ContainsKey("vol") && i == 2) { var f = Traffic.Volunteer(); if (f != null) Traffic.Retire(f); Morale.Change(3, null); } Feats.Recruit(x.v.ContainsKey("vol") && i == 2);
                        return $"{o.first} {o.last} firma il ruolo e prende posto {Officers.At(o.cls)}. Chi c'era prima sbarca con una stretta di mano.";
                    };
                    return fee > 0 ? P(fee, label, "", O(1, run)) : C(label, "onore", O(1, run));
                }).Append(C("Nessuno, per ora", "", O(1, x => "I candidati tornano a sedersi alla bacheca. Ci saranno altri porti."))).ToArray(),
            },
            // ---- abbordaggi
            new Def
            {
                id = "nave_catturata", title = "Una nave catturata", weight = 0, when = c => EnemyAI.Ship(c.v["arg"]) != null,
                setup = c => { var s = EnemyAI.Ship(c.v["arg"]); c.v["nave"] = s == null ? "la nave" : "la «" + (string)s.ShipName + "»"; c.v["hull"] = (s == null ? 1000 : (int)s.MyStats.HullMax).ToString(); },
                text = c => $"La squadra d'abbordaggio ha il controllo di {c.v["nave"]}: plancia occupata, equipaggio disarmato. Che ne facciamo?",
                choices = c => new[]
                {
                    C("Smontarla e vendere i pezzi", "", O(1, x => { var s = EnemyAI.Ship(x.v["arg"]); int cr = 800 + int.Parse(x.v["hull"]); Cr(x, cr); var got = global::Melchior.Loot.Grant(Stations.MyShip, 2, 4, "nave catturata"); if (s != null) { EnemyAI.Scuttle(s); Boarding.Release(s); } return "I tecnici smontano tutto quello che si può svitare." + (got != null ? $" Tra i pezzi c'è {got.Name}." : ""); })),
                    C("Svuotarne la stiva", "", O(1, x => { var s = EnemyAI.Ship(x.v["arg"]); var g = Market.Goods[rng.Next(Market.Goods.Length)]; int n = Market.Add(g.id, 5 + rng.Next(6)); Cr(x, 300 + rng.Next(500)); if (s != null) { EnemyAI.Scuttle(s); Boarding.Release(s); } return n > 0 ? $"Nella stiva c'erano {n} casse di {Market.Low(g.name)}: ora sono nostre." : "La stiva era piena, ma la nostra pure: prendiamo solo la cassa di bordo."; })),
                    C("Cercare prigionieri a bordo", "onore", O(0.45, x => { var s = EnemyAI.Ship(x.v["arg"]); Fame(x, 2, 0); Morale.Change(4, null); News.Post("flotta", $"La «{Ship()}» ha liberato dei prigionieri tenuti a bordo di {x.v["nave"]}."); if (s != null) { EnemyAI.Scuttle(s); Boarding.Release(s); } return "Nella cella di poppa ci sono coloni rapiti. Piangono, ringraziano, salgono a bordo: li porteremo al primo porto."; }), O(0.55, x => { var s = EnemyAI.Ship(x.v["arg"]); Cr(x, 300); if (s != null) { EnemyAI.Scuttle(s); Boarding.Release(s); } return "Nessun prigioniero: solo celle vuote e una cassa dimenticata."; })),
                    C("Consegnarla alla fazione del settore", "", O(1, x => { var s = EnemyAI.Ship(x.v["arg"]); if (x.fac >= 0 && x.fac != 4) Rep(x, x.fac, 3); Cr(x, 700); if (s != null) { EnemyAI.Scuttle(s); Boarding.Release(s); } return x.fac >= 0 && x.fac != 4 ? $"La {x.fname} ringrazia e manda un rimorchiatore. Ci pagano il disturbo." : "Nessuna fazione la reclama: la lasciamo alla deriva con un segnale d'avviso."; })),
                },
            },
            new Def
            {
                id = "prigioniero_incursore", title = "Un incursore catturato", weight = 0,
                text = c => "Uno degli incursori è ancora vivo: ferito, disarmato, chiuso in infermeria sotto scorta. Sa cose che potrebbero esserci utili.",
                choices = c => new[]
                {
                    C("Interrogarlo", "", O(0.6, x => { var lair = Saga.State.lairs.FirstOrDefault(); if (lair != null) { Course.Plot(lair.sector); return $"Parla: la sua banda risponde a {lair.owner}, e il covo è nel settore {Places.Of(lair.sector)}. Rotta tracciata."; } return Contracts.PostBounty(Director.Power(Stations.MyShip, out _, out _)) ? "Parla, e il nome che fa vale una taglia: nuova offerta sulla bacheca." : "Parla poco: sapeva solo di essere pagato bene."; }), O(0.4, x => "Non dice una parola. Prima del porto sarà lui a stancarsi, o noi.")),
                    C("Consegnarlo alle autorità", "onore", O(1, x => { Cr(x, 400); if (x.fac >= 0 && x.fac != 4) Rep(x, x.fac, 1); return "Al primo controllo lo prendono in consegna, con una piccola ricompensa."; })),
                    C("Liberarlo con un messaggio per i suoi", "", O(1, x => { Fame(x, 1, 1); return "«Dite ai vostri che la prossima volta non saremo così gentili.» Lo lasciamo in una capsula."; })),
                },
            },
            // ---- merci in stiva (borsa delle merci)
            new Def
            {
                id = "ispezione_merci", title = "Ispezione delle merci", weight = 2.5, when = c => Market.Contraband() && !Calling.Is("franco") && c.fac >= 0 && c.fac != 1 && c.fac != 4 && Factions.Level(c.fac) <= 1 && rng.NextDouble() < Shipyard.Less("stiva") * (1 - 0.15 * Talents.Ours("contrabbandiere")) * Math.Max(0.1, 1 - global::Melchior.Loot.Mine("dogane") + global::Melchior.Loot.Mine("rumore")),
                text = c => $"Un cutter della {c.fname} ci affianca: «Ispezione delle merci. Risultano a bordo carichi non dichiarati.» In stiva ci sono armi o reliquie che qui sono proibite.",
                choices = c => new[]
                {
                    C("Lasciarli ispezionare", "", O(1, x => { string l = Market.Confiscate(); Cr(x, -(400 + x.tier * 150)); Rep(x, x.fac, -2); return $"Sequestrano {l}, e c'è anche una multa."; })),
                    P(700 + c.tier * 250, "Pagare il doganiere", "infamia", O(0.75, x => { Cr(x, -(700 + x.tier * 250)); return "Il doganiere conta i crediti, sorride e se ne va."; }), O(0.25, x => { Cr(x, -(700 + x.tier * 250)); string l = Market.Confiscate(); return $"Prende i soldi e il carico: {l}."; })),
                    R("ingegneria", "Mascherare la stiva", "", O(0.7, x => "Le scansioni non trovano niente. Il cutter si allontana."), O(0.3, x => { string l = Market.Confiscate(); Rep(x, x.fac, -3); return $"Trovano il doppiofondo: sequestro di {l}."; })),
                    R("pilota", "Accelerare e sparire", "", O(0.65, x => { Rep(x, x.fac, -4); Wanted.Heat(5); return "Il cutter resta indietro. Il nostro nome, invece, va avanti."; }), O(0.35, x => { Rep(x, x.fac, -4); Foes(x, 1, 0, "Cutter della dogana"); return "Il cutter apre il fuoco."; })),
                },
            },
            new Def
            {
                id = "predoni_merci", title = "Una stiva che fa gola", weight = 1.8, when = c => Market.Value > 2500 && !Contracts.Carrying(),
                text = c => $"Predoni sul canale: «Avete la stiva piena, si vede da qui. Sganciatene metà e restiamo amici.»",
                choices = c => new[]
                {
                    C("Sganciare metà del carico", "", O(1, x => { string l = Market.LoseShare(0.5, "ceduti ai predoni"); Morale.Change(-2, null); return l.Length > 0 ? l : "Non c'era molto da prendere."; })),
                    R("pilota", "Seminarli", "", O(0.65, x => "Il pilota taglia per un campo di detriti: li perdiamo."), O(0.35, x => { Foes(x, 1 + x.tier / 2, 0, "Predoni"); return "Ci raggiungono."; })),
                    C("Combattere", "", O(1, x => { Foes(x, 2, 0, "Predoni"); return "Le torrette si girano verso di loro."; })),
                },
            },
            // ---- scorta richiesta da una nave del traffico
            new Def
            {
                id = "scorta", title = "Una richiesta di scorta", weight = 0, when = c => Traffic.EscortPlan(c.v["arg"]).dest >= 0,
                setup = c => { var p = Traffic.EscortPlan(c.v["arg"]); c.v["nave"] = p.ship; c.v["dest"] = p.dest.ToString(); c.v["paga"] = p.reward.ToString(); c.v["dist"] = p.dist.ToString(); },
                text = c => $"Dal «{c.v["nave"]}»: «Capitano, dobbiamo arrivare al settore {Places.Of(int.Parse(c.v["dest"]))}, {c.v["dist"]} salti da qui. Con i predoni che girano non ci arriviamo da soli. Ci fate da scorta? Paghiamo {c.v["paga"]} crediti all'arrivo.»",
                choices = c => new[]
                {
                    C("Accettare la scorta", "onore", O(1, x => Traffic.AcceptEscort(x.v["arg"], int.Parse(x.v["dest"]), int.Parse(x.v["paga"]), int.Parse(x.v["dist"])))),
                    C("Chiedere un compenso più alto", "", O(0.5, x => Traffic.AcceptEscort(x.v["arg"], int.Parse(x.v["dest"]), int.Parse(x.v["paga"]) * 3 / 2, int.Parse(x.v["dist"])) + " Hanno accettato di pagare la metà in più, a denti stretti."), O(0.5, x => "«Allora ci arrangiamo.» Il canale si chiude.")),
                    C("Rifiutare", "", O(1, x => "«Capiamo. Buon viaggio a voi.»")),
                },
            },
            // ---- resa spontanea di una nave nemica (IA nemica)
            new Def
            {
                id = "resa_offerta", title = "Il nemico chiede la resa", weight = 0, when = c => EnemyAI.Ship(c.v["arg"]) != null,
                setup = c => { var s = EnemyAI.Ship(c.v["arg"]); c.v["nave"] = s == null ? "dalla nave nemica" : "dalla «" + (string)s.ShipName + "»"; },
                text = c => $"Il canale si apre con un crepitio. {char.ToUpper(c.v["nave"][0]) + c.v["nave"][1..]}, rimasta sola e con lo scafo in pezzi, una voce stanca: «Basta così. Ci arrendiamo, capitano. Diteci cosa volete.»",
                choices = c => new[]
                {
                    C("Accettare la resa e prendere il carico", "onore", O(1, x => { var s = EnemyAI.Ship(x.v["arg"]); if (s == null) return "La nave è già esplosa: troppo tardi per le parole."; EnemyAI.Scuttle(s); var got = global::Melchior.Loot.Grant(Stations.MyShip, 1, 4, "resa"); Cr(x, 300 + rng.Next(500)); Fame(x, 1, 0); Morale.Change(2, null); Service.Add("comando.rese", 1); Feats.Surrender(); return "L'equipaggio abbandona la nave nelle capsule; prima che si autodistrugga, la stiva passa a noi" + (got != null ? $": c'era anche {got.Name}" : "") + "."; })),
                    C("Pretendere un riscatto e lasciarli andare", "", O(1, x => { var s = EnemyAI.Ship(x.v["arg"]); if (s == null) return "La nave è già esplosa: troppo tardi per le parole."; Cr(x, (int)((800 + rng.Next(900)) * Rules.Reward)); EnemyAI.LetGo(s); Calling.Add("corsari"); return "Il riscatto arriva in pochi secondi. La nave accende i motori e sparisce in curvatura, con un capitano che non dimenticherà il nostro nome."; })),
                    C("Rifiutare: fuoco a volontà", "infamia", O(1, x => { Fame(x, 0, 1); return "Il capitano chiude il canale senza rispondere. Le torrette tornano a cantare."; })),
                },
            },
            // ---- specie aliene
            new Def
            {
                id = "contatto", title = "Primo contatto", weight = 0.7, when = c => Species.Unmet() != null,
                setup = c => c.v["sp"] = Species.Unmet().id,
                text = c => Species.Get(c.v["sp"]).look + " Nessun archivio conosce questa specie: è un primo contatto.",
                choices = c => new[]
                {
                    R("scienza", "Applicare il protocollo di primo contatto", "onore", O(1, x => { var d = Species.Get(x.v["sp"]); Species.Change(d.id, 30, 20); Fame(x, 2, 0); x.notes.Add("nuova voce nella banca dati"); return $"La Scienza costruisce un ponte di numeri primi e immagini. {d.greet} Sono {d.name}."; })),
                    C("Salutare sulle frequenze standard", "", O(0.75, x => { var d = Species.Get(x.v["sp"]); Species.Change(d.id, 12, 8); return $"Rispondono. {d.greet} La Scienza li chiamerà {d.name}."; }), O(0.25, x => { var d = Species.Get(x.v["sp"]); Species.Change(d.id, 5, -15); return $"Qualcosa nel nostro saluto li offende: si ritirano di colpo. La Scienza li chiamerà {d.name}."; })),
                    C("Alzare gli scudi e mostrare le armi", "infamia", O(0.6, x => { var d = Species.Get(x.v["sp"]); Species.Change(d.id, 5, -30); return "Si allontanano, e non è un buon segno."; }), O(0.4, x => { var d = Species.Get(x.v["sp"]); Species.Change(d.id, 5, -30); Foes(x, 2, 0, "Sentinella aliena"); return "Rispondono come chiunque risponderebbe a delle armi puntate."; })),
                    C("Allontanarsi in silenzio", "", O(1, x => { Species.Change(x.v["sp"], 0, 0); return "Li lasciamo in pace. Forse un giorno torneranno loro da noi."; })),
                },
            },
            new Def
            {
                id = "specie_visita", title = "Una visita", weight = 0.9, when = c => Species.Met(s => !s.accord && s.attitude > -30) != null,
                setup = c => c.v["sp"] = Species.Met(s => !s.accord && s.attitude > -30).id,
                text = c => { var d = Species.Get(c.v["sp"]); var s = Species.Of(d.id); return $"{Species.Cap(d.name)} tornano a farci visita. Comprensione {s.understanding}%: " + (s.understanding < 40 ? "capiamo poco di quello che dicono." : s.understanding < 80 ? "cominciamo a capirci." : "ci capiamo quasi sempre.") + " " + d.greet; },
                choices = c =>
                {
                    var s = Species.Of(c.v["sp"]);
                    return new[]
                    {
                        R("scienza", "Studiare la loro lingua", "", O(1, x => { Species.Change(x.v["sp"], 25, 5); Species.Of(x.v["sp"]).visits++; return "Ore di lavoro su suoni, colori e silenzi. Ogni volta un po' meglio."; })),
                        P(500, "Scambiare doni", "onore", O(1, x => { var d = Species.Get(x.v["sp"]); Cr(x, -500); Species.Change(d.id, 8, 15); Loot(x, 2, 4); return $"In cambio ci lasciano {d.gift}."; })),
                        s.attitude >= 30 ? C("Chiedere la loro tecnologia", "", O(1, x => { Species.Change(x.v["sp"], 0, -8); Loot(x, 4, 6); return "Ce la concedono, con una certa freddezza: le cose preziose, per loro, si offrono, non si chiedono."; })) : null,
                        C("Salutare e proseguire", "", O(1, x => { Species.Change(x.v["sp"], 3, 0); return "Un saluto alla loro maniera, e ognuno per la sua strada."; })),
                    };
                },
            },
            new Def
            {
                id = "specie_crisi", title = "Un'incomprensione", weight = 1.5, when = c => Species.Met(s => !s.accord && s.attitude <= -30) != null,
                setup = c => c.v["sp"] = Species.Met(s => !s.accord && s.attitude <= -30).id,
                text = c => $"{Species.Cap(Species.Get(c.v["sp"]).name)} ci sbarrano la strada. Il loro messaggio è confuso, ma il senso è chiaro: ci considerano una minaccia, e vogliono una riparazione.",
                choices = c => new[]
                {
                    R("onore", "Chiedere scusa secondo il loro protocollo", "onore", O(1, x => { Species.Change(x.v["sp"], 10, 25); return "Le scuse arrivano, e vengono accettate. Il gelo si scioglie un poco."; })),
                    P(1500, "Offrire una riparazione", "", O(1, x => { Cr(x, -1500); Species.Change(x.v["sp"], 5, 20); return "La riparazione viene accettata in silenzio."; })),
                    C("Ignorare le loro richieste", "", O(1, x => { Species.Change(x.v["sp"], 0, -10); Foes(x, 2, 0, "Sentinella aliena"); return "Prendono la nostra indifferenza per una dichiarazione di guerra."; })),
                },
            },
            new Def
            {
                id = "relitto_leggendario", title = "Il relitto di una leggenda", weight = 0.6, when = c => Legacy.Any,
                setup = c => { var e = Legacy.Pick(); c.v["nave"] = e.ship; c.v["impresa"] = e.deed; },
                text = c => $"Alla deriva, coperto di brina, c'è il relitto della «{c.v["nave"]}»: la nave che {c.v["impresa"]}. Il suo nome si racconta ancora nei porti. Nessuno sa come sia finita qui.",
                choices = c => new[]
                {
                    C("Rendere gli onori", "onore", O(1, x => { Fame(x, 2, 0); Morale.Change(4, null); Trophies.Add($"Memoria della «{x.v["nave"]}»", "relitto leggendario"); return "Un minuto di silenzio in plancia, la bandiera a mezz'asta. Qualcuno, un giorno, lo farà anche per noi."; })),
                    R("scienza", "Recuperare il loro diario di bordo", "", O(1, x => { Loot(x, 4, 6); Competitors.Renown(15); return $"Il diario della «{x.v["nave"]}» racconta rotte, nemici e trucchi del mestiere. E nella cassaforte del capitano c'è ancora qualcosa."; })),
                    C("Smontare quello che resta", "infamia", O(1, x => { Loot(x, 3, 5); Morale.Change(-3, null); return "Il relitto rende bene. Nessuno, a bordo, ne va fiero."; })),
                },
            },
            // ---- regole della galassia (una volta, all'inizio)
            new Def
            {
                id = "regole", title = "Che tipo di viaggio?", weight = 0, onboard = true, when = c => Saga.State.rules.Length == 0,
                text = c => "Il primo salto è fatto. Prima di addentrarsi nella galassia, il capitano decide che tipo di viaggio sarà. (Si potrà cambiare idea solo da una nuova galassia.)",
                choices = c => new[]
                {
                    C("Rotta tranquilla: più storie, meno battaglie", "", O(1, x => { Rules.Set("tranquilla"); return "Si viaggia con calma."; })),
                    C("Frontiera: l'equilibrio di sempre", "", O(1, x => { Rules.Set("frontiera"); return "Si viaggia come si è sempre fatto."; })),
                    C("Frontiera spietata: più pericoli, più ricompense", "", O(1, x => { Rules.Set("spietata"); return "Si viaggia sul filo del rasoio."; })),
                },
            },
            // ---- basi dell'equipaggio
            new Def
            {
                id = "base_visita", title = "Alla nostra base", weight = 0, onboard = true, when = c => Bases.Here() != null && Bases.Here().state == 1,
                setup = c => c.v["base"] = Bases.Here().name,
                text = c => Pick($"Il responsabile di {c.v["base"]} ci viene incontro con il rapporto in mano: i coloni lavorano sodo, ma chiedono qualcosa.", $"A {c.v["base"]} c'è aria di festa: è arrivata la nave che l'ha fondata.", $"{c.v["base"]} cresce: nuove famiglie, nuovi moduli, qualche problema nuovo."),
                choices = c => new[]
                {
                    P(800, "Finanziare nuovi alloggi per i coloni", "onore", O(1, x => { Cr(x, -800); Morale.Change(3, null); Calling.Add("coloni", 2); return "I coloni ringraziano: la base ora può accogliere più famiglie, e la notizia gira."; })),
                    C("Una serata con i coloni", "onore", O(1, x => { Morale.Change(5, null); return "Una lunga serata di racconti e brindisi. L'equipaggio riparte con la sensazione di avere una casa in più."; })),
                    Home.Has ? null : C("Fare di questa base la nostra casa", "", O(1, x => { Home.Adopt(); return "Da oggi questa base è casa nostra."; })),
                    C("Solo un'ispezione di routine", "", O(1, x => "Tutto in ordine. Si riparte.")),
                },
            },
            // ---- il traditore a bordo
            new Def
            {
                id = "indagine", title = "L'indagine", weight = 0, when = c => Mole.Active,
                text = c => { var s = Mole.Suspects(); return s.Count <= 1 ? $"Gli indizi non lasciano dubbi: i sabotaggi portano tutti a {s.FirstOrDefault()?.first} {s.FirstOrDefault()?.last}." : $"I sabotaggi lasciano tracce: accessi ai terminali, turni scambiati, un messaggio cifrato. Gli indizi puntano a {string.Join(" e a ", s.Select(o => $"{o.first} {o.last}, {Officers.At(o.cls)}"))}. Uno di loro lavora per qualcun altro."; },
                choices = c =>
                {
                    var s = Mole.Suspects(); var l = new List<Choice>();
                    foreach (var o in s) { var who = o; l.Add(C($"Accusare {who.first} {who.last}", "", O(1, x => Mole.Accuse(who)))); }
                    if (s.Count > 1) l.Add(R("scienza", "Cercare altre prove prima di accusare", "", O(1, x => { Saga.State.mole.clues++; Saga.MarkDirty(); Saga.Later("incontro", 1, 2, "indagine"); return "La Scienza incrocia i registri degli accessi. Ci vorrà ancora un po', ma la rete si stringe."; })));
                    if (l.Count < 4) l.Add(C("Aspettare e osservare", "", O(1, x => "Per ora nessuno viene accusato. I sabotaggi, forse, continueranno.")));
                    return l.ToArray();
                },
            },
            // ---- meraviglie e cataclismi
            new Def
            {
                id = "supernova_colonia", title = "Prima della supernova", weight = 0,
                text = c => "La stella è gonfia e rossa, la superficie scossa da lampi. Sul pianeta della colonia mineraria trecento persone aspettano in fila davanti allo spazioporto: non hanno navi. Il tempo stringe.",
                choices = c => new[]
                {
                    C("Evacuare la colonia", "onore", O(1, x => { Wonders.Evacuated(); Fame(x, 4, 0); Morale.Change(6, null); Hull(x, 0.1f); Trophies.Add("Gli ultimi della supernova", "evacuazione"); return "Trecento persone stipate in ogni corridoio della nave. Ripartiamo con la stella che urla alle nostre spalle."; })),
                    R("pilota", "Evacuare e raccogliere i dati della stella", "onore", O(1, x => { Wonders.Evacuated(); Fame(x, 4, 0); Morale.Change(6, null); Loot(x, 4, 6); Trophies.Add("Gli ultimi della supernova", "evacuazione"); return "Il pilota tiene la nave ferma nel vento della stella abbastanza a lungo da caricare tutti e registrare tutto."; })),
                    C("Non c'è tempo per tutti", "", O(1, x => { Morale.Change(-5, null); return "Ce ne andiamo. Nessuno, in plancia, parla per ore."; })),
                },
            },
            new Def
            {
                id = "cometa", title = "La cometa errante", weight = 0,
                text = c => "Eccola: una cometa grande come una luna, con una coda che attraversa mezzo settore e brilla di colori che i sensori non sanno classificare.",
                choices = c => new[]
                {
                    R("ingegneria", "Raccogliere materia dalla coda", "", O(1, x => { Loot(x, 5, 7); Trophies.Add("Frammento della cometa errante", "meraviglia"); return "I collettori si riempiono di qualcosa che vale più di tutta la nave."; })),
                    R("pilota", "Volare dentro la coda", "", O(0.7, x => { Loot(x, 6, 7); Trophies.Add("Frammento della cometa errante", "meraviglia"); return "Dentro la coda è come volare in un'aurora. Ne usciamo con la stiva piena di meraviglie."; }), O(0.3, x => { Loot(x, 5, 6); Hull(x, 0.25f); return "La coda è più densa di quanto sembrasse: lo scafo ne esce graffiato, la stiva piena."; })),
                    C("Fermarsi ad ammirarla", "onore", O(1, x => { Morale.Change(8, null); return "Tutto l'equipaggio alle finestre, in silenzio. Certe cose si vedono una volta sola."; })),
                },
            },
            // ---- equipaggi concorrenti
            new Def
            {
                id = "concorrente", title = "Un equipaggio concorrente", weight = 1.1, when = c => c.comp != null,
                text = c => $"La «{c.comp.ship}» di {c.comp.captain}, {Competitors.Styles[c.comp.style]}, esce dalla curvatura a poca distanza. " + (c.comp.met ? (c.comp.relation >= 30 ? "«Vecchi amici! Che si dice da queste parti?»" : c.comp.relation <= -30 ? "«Voi. Che coincidenza sfortunata.»" : "«Ancora voi. Il settore è piccolo.»") : "«Liberi equipaggi come noi. Piacere di conoscervi... credo.»"),
                choices = c => new[]
                {
                    C("Scambiare informazioni", "onore", O(1, x => { x.comp.met = true; x.comp.relation += 10; Saga.MarkDirty(); if (Contracts.PostBounty(x.power)) x.notes.Add("taglia sulla bacheca"); else News.Rumor(); Competitors.Renown(2); return "Mezz'ora di chiacchiere sul canale e qualche dritta utile in entrambe le direzioni."; })),
                    c.comp.relation < 30 ? null : new Choice { label = "Chiedere un favore da vecchi amici", tone = "", outs = new[] { O(1, x => { x.comp.met = true; Heal(x, 0.25f); Loot(x, 2, 4); return "«Per voi, questo e altro.» Una squadra tecnica e un pezzo di ricambio che non sapevamo di cercare."; }) } },
                    R("pilota", "Sfidarli in velocità", "", O(0.6, x => { x.comp.met = true; x.comp.score = Math.Max(0, x.comp.score - 5); Competitors.Renown(10); Cr(x, 800); return "Li battiamo sul filo. Nei porti se ne parlerà."; }), O(0.4, x => { x.comp.met = true; x.comp.score += 5; Cr(x, -800); return "Ci battono di mezza lunghezza. Pagare brucia."; })),
                    c.comp.relation >= 30 ? null : C("Manomettere la loro nave durante l'aggancio", "infamia", O(1, x => { x.comp.met = true; x.comp.relation -= 40; x.comp.score = Math.Max(0, x.comp.score - 25); Fame(x, 0, 2); Saga.MarkDirty(); Saga.Later("incontro", 2, 5, "seguito_concorrente", x.comp.ship); return "Un modulo difettoso nel loro sistema di navigazione. Perderanno settimane. Se lo scoprono..."; })),
                    C("Salutare e proseguire", "", O(1, x => { x.comp.met = true; Saga.MarkDirty(); return "Un cenno sul canale e ognuno per la sua rotta."; })),
                },
            },
            new Def
            {
                id = "seguito_concorrente", title = "Conti da regolare", weight = 0,
                text = c => $"La «{c.v["arg"]}» ci aspetta all'uscita della curvatura, armi cariche: «Sappiamo chi ci ha manomesso la navigazione. Si paga.»",
                choices = c => new[]
                {
                    P(1500, "Pagare i danni", "", O(1, x => { Cr(x, -1500); return "I crediti chiudono la questione, ma la voce gira."; })),
                    R("onore", "Chiedere scusa, da capitano a capitano", "onore", O(0.6, x => "Un lungo silenzio, poi: «Non succeda più.» Se ne vanno."), O(0.4, x => { Foes(x, 1, 1, x.v["arg"]); return "«Le scuse non riparano una nave.»"; })),
                    C("Negare tutto", "", O(1, x => { Foes(x, 1 + x.tier / 2, 1, x.v["arg"]); return "«Bugiardi.» Aprono il fuoco."; })),
                },
            },
            // ---- storie a puntate (la prima parte compare da sola, le successive arrivano come seguiti)
            new Def
            {
                id = "fantasma_1", title = "La nave fantasma", weight = 0.5, when = c => !Saga.State.stories.Contains("fantasma"),
                text = c => $"Un relitto alla deriva trasmette un codice di identificazione che conosciamo bene: è il nostro. Stesso nome, stessa matricola. La data del registro di bordo è fra tre settimane.",
                choices = c => new[]
                {
                    C("Abbordarlo", "", O(1, x => { Story(x, "fantasma"); Saga.Later("incontro", 2, 4, "fantasma_2", "abbordaggio"); Loot(x, 2, 4); return "Corridoi identici ai nostri, coperti di brina. Nella plancia, un messaggio registrato con la voce del capitano: «Non andate nel settore che vi indicheranno.» Poi il relitto svanisce."; })),
                    R("scienza", "Scansionarlo da lontano", "", O(1, x => { Story(x, "fantasma"); Saga.Later("incontro", 2, 4, "fantasma_2", "scansione"); Fame(x, 1, 0); return "La firma temporale è reale: quel relitto viene davvero dal nostro futuro. Prima che la Scienza finisca, sparisce."; })),
                    R("armi", "Distruggerlo", "", O(1, x => { Saga.State.stories.Add("fantasma"); Saga.MarkDirty(); Loot(x, 1, 3); return "Pochi colpi e il relitto si dissolve in polvere luminosa. Nessuno a bordo dorme bene, quella notte."; })),
                },
            },
            new Def
            {
                id = "fantasma_2", title = "La nave fantasma (seconda parte)", weight = 0,
                text = c => "Il relitto con il nostro nome riappare, più vicino, più integro. Stavolta trasmette coordinate e una sola frase, con la voce del capitano: «Venite qui. È l'unico modo per chiudere il cerchio.»",
                choices = c => new[]
                {
                    C("Seguire le coordinate", "", O(1, x => { Saga.Later("incontro", 1, 2, "fantasma_3", "seguito"); Morale.Change(-2, null); return "Tracciamo la rotta. L'equipaggio fa finta di non avere paura."; })),
                    R("scienza", "Rispondere sul loro stesso canale", "onore", O(1, x => { Saga.Later("incontro", 1, 3, "fantasma_3", "risposta"); Fame(x, 1, 0); return "Il canale resta aperto per un istante. Dall'altra parte qualcuno sussurra: «Grazie.»"; })),
                    C("Ignorarlo: non siamo noi", "", O(1, x => { EndStory(x, "La nave fantasma", false); Morale.Change(-3, null); return "Il relitto si allontana. Per settimane, a bordo, nessuno nomina il nostro codice di identificazione."; })),
                },
            },
            new Def
            {
                id = "fantasma_3", title = "La nave fantasma (ultima parte)", weight = 0,
                text = c => "Eccolo: il relitto della nostra nave, spezzato in due, fermo da chissà quanto tempo. Il registro di bordo racconta un futuro che non abbiamo ancora vissuto, e una scelta sbagliata che possiamo ancora evitare.",
                choices = c => new[]
                {
                    C("Recuperare il registro di bordo", "", O(1, x => { Loot(x, 5, 6); EndStory(x, "La nave fantasma", true); return "Il registro è nostro. Lo leggiamo una volta sola, poi lo sigilliamo. Quel futuro non accadrà."; })),
                    C("Lasciare un faro per chi verrà", "onore", O(1, x => { Fame(x, 3, 0); Morale.Change(8, null); EndStory(x, "La nave fantasma", true); return "Programmiamo il faro con il nostro codice e una frase: «Venite qui.» Il cerchio si chiude, e capiamo chi ci ha chiamati."; })),
                    R("ingegneria", "Recuperare il nucleo, ancora intatto", "", O(1, x => { Cr(x, 3000 + x.tier * 800); Loot(x, 3, 5); EndStory(x, "La nave fantasma", true); return "Il nostro nucleo, quello del futuro, vale una fortuna. Nessuno chiede cosa significhi."; })),
                },
            },
            new Def
            {
                id = "debito_1", title = "Il finanziatore", weight = 0.5, when = c => !Saga.State.stories.Contains("debito"),
                text = c => "Un uomo elegante, con un sorriso che non arriva agli occhi, ci offre un prestito: «Cinquemila crediti subito. Ottomila fra sei salti. Una formalità, tra gentiluomini.»",
                choices = c => new[]
                {
                    C("Accettare il prestito", "", O(1, x => { Story(x, "debito"); Cr(x, 5000); Saga.Later("incontro", 6, 7, "debito_2", "8000"); return "I crediti arrivano subito. Anche il contratto, lungo e scritto molto piccolo."; })),
                    C("Rifiutare", "", O(1, x => { Saga.State.stories.Add("debito"); Saga.MarkDirty(); return "«Peccato. Sarà per un'altra volta.» Non sembra deluso."; })),
                },
            },
            new Def
            {
                id = "debito_2", title = "Il finanziatore (seconda parte)", weight = 0,
                text = c => $"Gli esattori del finanziatore escono dalla curvatura puntuali come un orologio: «Sono passati sei salti. Fanno {c.v["arg"]} crediti.»",
                choices = c => new[]
                {
                    P(int.Parse(c.v["arg"]), "Pagare il debito", "onore", O(1, x => { Cr(x, -int.Parse(x.v["arg"])); EndStory(x, "Il finanziatore", true); return "«Un piacere fare affari con voi.» Il finanziatore ci manda perfino un biglietto di ringraziamento."; })),
                    C("Chiedere una proroga", "", O(1, x => { int due = int.Parse(x.v["arg"]) + 3000; Saga.Later("incontro", 4, 5, "debito_2", due.ToString()); return $"«Una proroga? Certo.» La cifra diventa {due} crediti."; })),
                    C("Rifiutarsi di pagare", "infamia", O(1, x => { Foes(x, 1 + x.tier / 2, 0, "Esattori"); Saga.Later("incontro", 3, 5, "debito_3", x.v["arg"]); Fame(x, 0, 1); return "«Male. Molto male.» Gli esattori aprono il fuoco, e il finanziatore prende nota."; })),
                },
            },
            new Def
            {
                id = "debito_3", title = "Il finanziatore (ultima parte)", weight = 0,
                text = c => "Il finanziatore ha messo una taglia su di noi. I cacciatori che ci raggiungono sono i migliori che il denaro possa comprare: «Niente di personale. Paga lui.»",
                choices = c => new[]
                {
                    P(int.Parse(c.v["arg"]) * 2, "Pagare il doppio e chiudere la storia", "", O(1, x => { Cr(x, -int.Parse(x.v["arg"]) * 2); EndStory(x, "Il finanziatore", true); return "I cacciatori si ritirano. La lezione è costata cara."; })),
                    C("Combattere", "", O(1, x => { Foes(x, 2 + x.tier / 2, 1, "Cacciatore del finanziatore"); EndStory(x, "Il finanziatore", true); Loot(x, 3, 5); return "Se sopravviviamo, il finanziatore smetterà di considerarci un investimento."; })),
                },
            },
            new Def
            {
                id = "erede_1", title = "L'erede", weight = 0.5, when = c => c.fac >= 0 && c.fac != 4 && !Saga.State.stories.Contains("erede"),
                text = c => $"Una navetta senza contrassegni chiede asilo. A bordo c'è il giovane erede di un ammiraglio della {c.fname}, deposto da un colpo di stato. «Mi cercano. Se mi trovano, mi uccidono.»",
                choices = c => new[]
                {
                    C("Dargli rifugio", "onore", O(1, x => { Story(x, "erede"); Rep(x, x.fac, -6); Saga.Later("incontro", 2, 4, "erede_2", x.fname, x.fac); return "La navetta attracca. Il ragazzo ringrazia con una cortesia d'altri tempi."; })),
                    C("Consegnarlo ai nuovi padroni", "infamia", O(1, x => { Saga.State.stories.Add("erede"); Rep(x, x.fac, 10); Cr(x, 2500); Fame(x, 0, 3); Saga.MarkDirty(); return "La ricompensa arriva subito. Il suo sguardo, mentre lo portano via, resterà a bordo per molto."; })),
                    C("Rifiutare e lasciarlo andare", "", O(1, x => { Saga.State.stories.Add("erede"); Saga.MarkDirty(); return "La navetta riparte verso il buio. Speriamo che trovi qualcun altro."; })),
                },
            },
            new Def
            {
                id = "erede_2", title = "L'erede (seconda parte)", weight = 0,
                text = c => $"L'erede ha ricevuto un messaggio: gli ufficiali fedeli a suo padre lo aspettano in un settore di confine della {c.v["arg"]}. «Portatemi da loro, e la {c.v["arg"]} non dimenticherà.»",
                choices = c => new[]
                {
                    C("Portarlo dai fedelissimi", "onore", O(1, x => { Foes(x, 1 + x.tier / 2, 0, "Inseguitori"); Saga.Later("incontro", 1, 3, "erede_3", x.v["arg"], int.Parse(x.v["argf"])); return "Gli uomini del nuovo regime ci aspettavano. Dovremo aprirci la strada."; })),
                    R("pilota", "Portarlo per vie traverse", "", O(1, x => { Saga.Later("incontro", 2, 3, "erede_3", x.v["arg"], int.Parse(x.v["argf"])); return "Il pilota trova una rotta che nessuna pattuglia sorveglia."; })),
                    C("Lasciarlo al porto più vicino", "", O(1, x => { EndStory(x, "L'erede", false); return "Lo lasciamo al sicuro, ma da solo. Il suo destino non è più affar nostro."; })),
                },
            },
            new Def
            {
                id = "erede_3", title = "L'erede (ultima parte)", weight = 0,
                text = c => "Gli ufficiali fedeli salgono a bordo, si inginocchiano davanti al ragazzo e gli giurano obbedienza. Prima di andare, l'erede chiede di parlare con il capitano.",
                choices = c => new[]
                {
                    C("Ascoltarlo", "onore", O(0.75, x => { int f = int.Parse(x.v["argf"]); Rep(x, f, 25); Fame(x, 3, 0); Loot(x, 4, 6); EndStory(x, "L'erede", true); News.Post("fronte", $"Colpo di scena nella {x.v["arg"]}: l'erede dell'ammiraglio deposto riprende il comando della flotta."); return "«Quando sarò ammiraglio, la vostra nave avrà sempre un porto.» Mantiene la promessa."; }),
                                          O(0.25, x => { int f = int.Parse(x.v["argf"]); Rep(x, f, 8); Foes(x, 1 + x.tier / 2, 1, "Ex fedelissimi"); EndStory(x, "L'erede", true); return "Non tutti i fedelissimi erano fedeli. Il ragazzo si salva; la nostra nave deve combattere per lui un'ultima volta."; })),
                },
            },
            // ---- seguiti di scelte fatte in precedenza (compaiono solo come conseguenza)
            new Def
            {
                id = "seguito_asilo", title = "Il disertore", weight = 0,
                text = c => $"Cacciatori di taglie al soldo della {GalaxyWar.Name(int.Parse(c.v["argf"]))} ci intercettano: «Avete a bordo {c.v["arg"]}. Consegnatecelo e ve ne andrete in pace.»",
                choices = c => new[]
                {
                    C("Consegnare il disertore", "", O(1, x => { int f = int.Parse(x.v["argf"]); Rep(x, f, 8); Fame(x, -1, 1); return "Il disertore ci guarda mentre lo portano via. Non dice niente."; })),
                    R("pilota", "Seminarli", "", O(0.7, x => "Il pilota ci porta via prima che possano agganciarci."), O(0.3, x => { Foes(x, 2, 0, "Cacciatori di taglie"); return "Non abbastanza veloci."; })),
                    C("Proteggerlo con le armi", "onore", O(1, x => { Fame(x, 2, 0); Foes(x, 2, 0, "Cacciatori di taglie"); return "«Come volete.» Il disertore stringe i pugni accanto alla postazione delle armi."; })),
                },
            },
            new Def
            {
                id = "seguito_profughi", title = "La nuova colonia", weight = 0,
                text = c => $"Un messaggio dai profughi di {c.v["arg"]} che avevamo scortato: hanno fondato un insediamento su una luna di questo sistema e ci invitano a fermarci.",
                choices = c => new[]
                {
                    C("Fermarsi alla colonia", "onore", O(0.7, x => { Heal(x, 0.25f); Loot(x, 2, 5); Fame(x, 1, 0); return "Una festa improvvisata, riparazioni fatte con amore e un dono che hanno custodito per noi."; }),
                                                         O(0.3, x => { Fame(x, 2, 0); Foes(x, 1 + x.tier / 2, 0, "Predoni"); return "Arriviamo appena in tempo: i predoni hanno trovato la colonia prima di noi."; })),
                    C("Mandare un saluto e proseguire", "", O(1, x => { Cr(x, 600); return "Ci rispondono con una piccola colletta. Non hanno dimenticato."; })),
                },
            },
            new Def
            {
                id = "seguito_contrabbando", title = "I legittimi proprietari", weight = 0,
                text = c => "Una nave armata ci affianca: «La merce che avete comprato, o preso, era nostra. La rivogliamo. Con gli interessi.»",
                choices = c => new[]
                {
                    P(1000 + c.tier * 400, "Pagare per chiudere la faccenda", "", O(1, x => { Cr(x, -(1000 + x.tier * 400)); return "Pagati gli interessi, la faccenda è chiusa."; })),
                    R("armi", "Mostrare i cannoni", "", O(0.6, x => "Ci guardano, contano i nostri cannoni e decidono che non ne vale la pena."), O(0.4, x => { Foes(x, 1 + x.tier / 2, 0, "Recuperatori"); return "Decidono che ne vale la pena."; })),
                    C("Rifiutare", "", O(1, x => { Foes(x, 1 + x.tier / 2, 0, "Recuperatori"); return "«Allora ce la prendiamo.»"; })),
                },
            },
        };
        static string Pick(params string[] a) => a[rng.Next(a.Length)];
        static void Story(Ctx c, string id) { if (!Saga.State.stories.Contains(id)) Saga.State.stories.Add(id); Saga.MarkDirty(); }
        static void EndStory(Ctx c, string title, bool good)
        {
            Saga.Chronicle($"Storia conclusa: «{title}», {(good ? "con un lieto fine" : "lasciata a metà")}.");
            if (good) { Trophies.Add($"Ricordo di «{title}»", "storia"); Competitors.Renown(25); Morale.Change(3, null); }
        }
        static string Confisca(Ctx x)
        {
            var c = Saga.State.contract; string what = c?.target ?? "il carico";
            Contracts.LoseCargo("confiscato durante un'ispezione"); Cr(x, -(600 + x.tier * 300)); Rep(x, x.fac, -8); Fame(x, 0, 1);
            return $"Gli ispettori aprono la stiva e trovano {what}. Carico confiscato, multa e una nota sul nostro conto.";
        }

        // ---- incontri dei porti (all'attracco a una stazione o a un negozio)
        static string Port(Ctx c) { try { var n = System.Text.RegularExpressions.Regex.Replace(PLServer.GetCurrentSector()?.Name ?? "", @"\s*\(\d+\)$", ""); return string.IsNullOrEmpty(n) ? "il porto" : n; } catch { return "il porto"; } }
        static IEnumerable<Def> PortDefs()
        {
            yield return new Def
            {
                id = "taverna", title = "La taverna del porto", weight = 1.4, portOnly = true, when = c => c.port,
                text = c => $"La licenza a terra porta mezzo equipaggio nella taverna di {Port(c)}. Musica, fumo, voci. Un vecchio cartografo dell'Accademia beve da solo al bancone.",
                choices = c =>
                {
                    int unsold = Saga.State.discovered - Saga.State.atlasSold;
                    return new[]
                    {
                        new Choice { label = unsold > 0 ? $"Vendere i dati dell'Atlante ({unsold} scoperte)" : "Vendere i dati dell'Atlante (niente di nuovo)", req = unsold > 0 ? "" : "nulla", outs = new[] { O(1, x => { int n = Saga.State.discovered - Saga.State.atlasSold; Saga.State.atlasSold = Saga.State.discovered; Cr(x, (int)(n * (250 + x.tier * 80) * Calling.Bonus("esplorazione") * (Calling.Is("fiera") ? 2 : 1))); Saga.MarkDirty(); return "Il cartografo sfoglia i nostri rilievi con gli occhi lucidi, e paga senza trattare."; }) } },
                        P(300, "Offrire da bere e ascoltare le voci", "", O(0.6, x => { Cr(x, -300); if (Contracts.PostBounty(x.power)) x.notes.Add("taglia sulla bacheca"); else News.Rumor(); return "Tra un brindisi e l'altro salta fuori un nome, e un settore."; }),
                                                                    O(0.4, x => { Cr(x, -300); Morale.Change(5, null); x.notes.Add("morale +5"); return "Nessuna voce utile, ma una serata memorabile."; })),
                        P(1200, "Arruolare un veterano in cerca d'imbarco", "", O(1, x => { Cr(x, -1200); Morale.Change(12, "Un veterano si unisce a noi"); x.notes.Add("morale +12"); return "Un veterano di tre guerre firma il ruolino: la sua calma contagia tutta la nave."; })),
                        C("Una notte tranquilla", "", O(1, x => { Morale.Change(3, null); return "L'equipaggio rientra a bordo all'alba, riposato."; })),
                    };
                },
            };
            yield return new Def
            {
                id = "cantiere", title = "Il cantiere del porto", weight = 1.2, portOnly = true, when = c => c.port,
                text = c => $"Il capocantiere di {Port(c)} squadra la nostra nave dal molo: «Ne avete passate parecchie. Posso sistemarvela, a un prezzo onesto. Più o meno onesto.»",
                choices = c => new[]
                {
                    P(800 + c.tier * 300, "Revisione completa", "", O(1, x => { Cr(x, -(800 + x.tier * 300)); Heal(x, 1f); foreach (var sy in Stations.SysIds) Stations.Set(x.ship.ShipID, "dmx_" + sy, 0.01f); x.notes.Add("tutti i sistemi riparati"); return "Il cantiere lavora tutta la notte: la nave esce come nuova."; })),
                    R("ingegneria", "Lavorare fianco a fianco con i loro tecnici", "", O(1, x => { Cr(x, -(300 + x.tier * 100)); Heal(x, 0.5f); foreach (var sy in Stations.SysIds) Stations.Set(x.ship.ShipID, "dmx_" + sy, Math.Max(0.01f, Stations.Get(x.ship.ShipID, "dmx_" + sy) - 50)); Morale.Change(3, null); return "Le nostre squadre imparano due trucchi, i loro tecnici uno. Prezzo di favore."; })),
                    C("Ringraziare e ripartire", "", O(1, x => "«Come volete. Il molo resta aperto.»")),
                },
            };
            yield return new Def
            {
                id = "casa", title = "Un porto dove tornare", weight = 3, portOnly = true, when = c => c.port && !Home.Has,
                text = c => $"Al molo di {Port(c)} il capo del porto riconosce la nostra nave: «Passate spesso da queste parti. Perché non fate di questo posto la vostra casa? Qui c'è sempre un letto, un cantiere e qualcuno che vi aspetta.»",
                choices = c => new[]
                {
                    C("Fare di questo porto la nostra casa", "onore", O(1, x => { Home.Adopt(); Morale.Change(6, null); return $"{Port(x)} è casa nostra, da oggi. Le riparazioni, qui, saranno sempre gratuite."; })),
                    C("Non ancora: siamo gente di passaggio", "", O(1, x => "«Come volete. L'offerta resta valida.»")),
                },
            };
            yield return new Def
            {
                id = "catasto", title = "L'ufficio del catasto", weight = 1.5, portOnly = true, when = c => c.port,
                setup = c => { var k = Bases.Kinds.OrderBy(_ => rng.Next()).Take(3).ToList(); for (int i = 0; i < 3; i++) c.v["k" + i] = k[i].id; },
                text = c => $"All'ufficio del catasto della Gilda, un funzionario sfoglia un registro polveroso: «Atti di fondazione, capitano. Con uno di questi, in un settore libero, potete costruire qualcosa di vostro.» Offre: {string.Join(", ", Enumerable.Range(0, 3).Select(i => Bases.K(c.v["k" + i]).name.ToLowerInvariant() + " (" + Bases.K(c.v["k" + i]).brief + ")"))}.",
                choices = c => Enumerable.Range(0, 3).Select(i => { var k = Bases.K(c.v["k" + i]); return P(k.price, $"Atto: {k.name.ToLowerInvariant()}", "", O(1, x => { Cr(x, -k.price); Bases.GiveDeed(k.id, "acquistato al catasto"); return "Il funzionario timbra l'atto tre volte e ce lo consegna con una certa solennità."; })); }).Append(C("Non oggi", "", O(1, x => "«Il registro resta aperto.»"))).ToArray(),
            };
            yield return new Def
            {
                id = "saldare", title = "Conti da saldare", weight = 2.5, portOnly = true, when = c => c.port && Wanted.Level >= 1,
                text = c => $"Un funzionario del porto vi aspetta in banchina con un tablet in mano: «Sulla vostra nave pende una taglia di {Wanted.Bounty} crediti. Possiamo sistemare la faccenda qui, con discrezione. Oppure no.»",
                choices = c => new[]
                {
                    P(Wanted.Bounty, "Pagare la taglia per intero", "", O(1, x => { int b = Wanted.Bounty; Cr(x, -b); Wanted.Clear(999); return "Il funzionario timbra, firma e sorride. La nostra fedina torna pulita."; })),
                    P(Wanted.Bounty / 2, "Trattare uno sconto", "", O(0.7, x => { int b = Wanted.Bounty / 2; Cr(x, -b); Wanted.Clear(Saga.State.wanted.heat / 2 + 1); return "Metà della cifra per metà dei problemi. Un affare, a modo suo."; }), O(0.3, x => { Cr(x, -(Wanted.Bounty / 2)); return "Il funzionario intasca i crediti e si dimentica di cancellare la taglia."; })),
                    R("onore", "Far valere il nostro buon nome", "onore", O(1, x => { Wanted.Clear(30); return "Il funzionario riconosce il nome della nave e chiude un occhio. Forse due."; })),
                    C("Andarsene prima che chiami la sicurezza", "", O(1, x => "Ripartiamo in fretta. La taglia resta dov'è.")),
                },
            };
            yield return new Def
            {
                id = "contributo", title = "La colonia cresce", weight = 2, portOnly = true, when = c => c.port && Home.AtHome(),
                text = c => $"Il consiglio di {Home.H.name} vi invita a una riunione: la colonia vuole ampliare il porto, il cantiere e le difese. Prosperità attuale: {Home.H.prosperity}%.",
                choices = c => new[]
                {
                    P(1000, "Contribuire con mille crediti", "onore", O(1, x => { Cr(x, -1000); Home.Prosper(10, "Il nostro contributo"); return "Il consiglio vota all'unanimità di intitolare il nuovo molo alla nostra nave."; })),
                    R("ingegneria", "Prestare le squadre tecniche per i lavori", "onore", O(1, x => { Home.Prosper(8, "Le nostre squadre tecniche"); Morale.Change(-1, null); return "Una settimana di lavori in banchina: le squadre tornano stanche e orgogliose."; })),
                    C("Non questa volta", "", O(1, x => "«Sarà per la prossima.»")),
                },
            };
            yield return new Def
            {
                id = "mercato_nero", title = "Il mercato nero", weight = 1.0, portOnly = true, when = c => c.port && (Saga.State.infamia >= 2 || rng.NextDouble() < 0.3),
                text = c => "Nei vicoli del porto qualcuno ci riconosce e ci fa cenno di seguirlo. Dietro una porta blindata, merci che nessuna dogana ha mai visto.",
                choices = c => new[]
                {
                    P((int)((2500 + c.tier * 800) / Calling.Bonus("corsari") * (Species.Accord("kethri") ? 0.85 : 1)), "Comprare un pezzo raro, senza domande", "", O(0.75, x => { Cr(x, -(int)((2500 + x.tier * 800) / Calling.Bonus("corsari") * (Species.Accord("kethri") ? 0.85 : 1))); Loot(x, 4, 6); return "Il pezzo è autentico. Da dove venga, meglio non saperlo."; }), O(0.25, x => { Cr(x, -(2500 + x.tier * 800)); Loot(x, 2, 4); return "Il pezzo vale meno del prezzo. Lezione imparata."; })),
                    C("Prendere un carico di contrabbando da consegnare", "infamia", O(1, x => { x.notes.Add(Contracts.ForceTransport(true).Split(':')[0]); return "Stretta di mano, nessun nome. L'offerta è sulla bacheca."; })),
                    C("Andarsene", "", O(1, x => "La porta blindata si richiude alle nostre spalle.")),
                },
            };
        }
        // Vita di bordo: può capitare ovunque (anche in porto), mai in combattimento o in curvatura.
        public static bool StartOnboard(string id = null)
        {
            var ship = Stations.MyShip; if (!Host || ship == null || cur != null) return false;
            try { if (ship.InWarp || (int)Stations.Get(ship.ShipID, "drx_phase") == 2 || Nemesis.Engaged != null) return false; } catch { return false; }
            var c = NewCtx(ship);
            var pool = Defs.Where(d => d.onboard && (id == null ? !recent.Contains(d.id) : d.id == id) && Safe(() => d.when(c))).ToList(); if (pool.Count == 0) return false;
            var pick = pool[rng.Next(pool.Count)]; pick.setup(c);
            cur = pick; ctx = c; curChoices = pick.choices(c).Where(x => x != null).Take(4).ToArray(); serial++;
            deadline = UnityEngine.Time.time + 120f; botAt = UnityEngine.Time.time + 25f;
            recent.Add(pick.id); if (recent.Count > 6) recent.RemoveAt(0); lastSent = "";
            Director.Notify("VITA DI BORDO · " + pick.title.ToUpperInvariant(), "Il capitano deve decidere: le opzioni sono sulla Bacheca. " + pick.text(c));
            Plugin.L.LogInfo($"[incontri] bordo {pick.id}");
            return true;
        }
        static string Off(Officer o) => $"{o.first} {o.last}, {Officers.Ranks[o.rank].ToLowerInvariant()} {Officers.At(o.cls)},";
        static IEnumerable<Def> OnboardDefs()
        {
            Def B(string id, string title, Func<Ctx, bool> when, Func<Ctx, string> text, Func<Ctx, Choice[]> ch, Action<Ctx> setup = null) => new Def { id = id, title = title, weight = 1, onboard = true, when = when, text = text, choices = ch, setup = setup ?? (_ => { }) };
            yield return B("lite", "Una lite in plancia", c => c.off != null && c.off2 != null,
                c => $"{Off(c.off)} e {Off(c.off2)} litigano a voce alta su chi debba avere la precedenza sull'energia di bordo. L'equipaggio fa finta di non sentire.",
                c => new[]
                {
                    C($"Dare ragione a {c.off.last}", "", O(1, x => { Officers.Credit(x.off.cls, 2); Morale.Change(-1, null); return $"{x.off.last} ottiene la precedenza. {x.off2.last} incassa in silenzio."; })),
                    C($"Dare ragione a {c.off2.last}", "", O(1, x => { Officers.Credit(x.off2.cls, 2); Morale.Change(-1, null); return $"{x.off2.last} ottiene la precedenza. {x.off.last} incassa in silenzio."; })),
                    R("onore", "Mediare: turni condivisi", "onore", O(1, x => { Morale.Change(4, null); return "Un compromesso che scontenta tutti un po' e nessuno troppo: funziona."; })),
                    C("Richiamare entrambi all'ordine", "", O(1, x => { Morale.Change(-3, null); return "La lite finisce. Il malumore no."; })),
                });
            yield return B("compleanno", "Un compleanno a bordo", c => c.off != null,
                c => $"Oggi è il compleanno di {Off(c.off)[..^1]}. Qualcuno ha nascosto una torta nella cambusa, ma serve il permesso del capitano per festeggiare.",
                c => new[]
                {
                    P(200, "Una festa come si deve", "onore", O(1, x => { Cr(x, -200); Morale.Change(5, null); Officers.Credit(x.off.cls, 1); return "Musica, candeline e un brindisi del capitano. Per una sera la galassia può aspettare."; })),
                    C("Gli auguri del capitano, e basta", "", O(1, x => { Morale.Change(1, null); return "Gli auguri arrivano in plancia, la torta in cambusa. Tutti contenti."; })),
                    C("Niente feste in servizio", "", O(1, x => { Morale.Change(-2, null); return "La torta resta nascosta. Qualcuno la mangerà di nascosto, comunque."; })),
                });
            yield return B("clandestino", "Un clandestino in stiva", c => true,
                c => "Durante un controllo la sicurezza trova un ragazzino nascosto fra le casse. È salito al porto precedente: «Voglio vedere le stelle. Voglio fare il pilota.»",
                c => new[]
                {
                    C("Arruolarlo come mozzo", "onore", O(1, x => { Morale.Change(4, null); Fame(x, 1, 0); return "Una tuta troppo grande e un posto in mensa. Il timoniere ha già promesso di insegnargli i rudimenti."; })),
                    C("Riportarlo a casa al prossimo porto", "onore", O(1, x => { Fame(x, 1, 0); return "Lo riportiamo indietro. Si lascia promettere che, quando sarà grande, ci sarà un posto per lui."; })),
                    C("Consegnarlo alle autorità del porto", "infamia", O(1, x => { Cr(x, 300); Fame(x, 0, 1); Morale.Change(-2, null); return "Una piccola ricompensa e un grande silenzio in mensa."; })),
                });
            yield return B("replicatore", "Il replicatore guasto", c => true,
                c => "Il replicatore di cibo si è guastato: da tre giorni produce soltanto una pappa grigia che sa vagamente di cannella. L'equipaggio minaccia lo sciopero della fame.",
                c => new[]
                {
                    R("ingegneria", "Mandare l'Ingegneria a ripararlo", "", O(1, x => { Morale.Change(3, null); if (x.off != null && x.off.cls == 4) Officers.Credit(4, 1); return "Due ore di lavoro e un sottoprogramma corrotto dopo, il replicatore torna a fare la pasta. Applausi."; })),
                    P(400, "Comprare scorte vere al prossimo scalo", "", O(1, x => { Cr(x, -400); Morale.Change(2, null); return "Cibo vero, per qualche giorno. Nessuno rimpiange la cannella."; })),
                    C("Razionare e tenere duro", "", O(1, x => { Morale.Change(-4, null); return "Si tiene duro. Si tiene molto duro."; })),
                });
            yield return B("malattia", "Una febbre misteriosa", c => c.off != null,
                c => $"Mezzo equipaggio ha la febbre alta, e {Off(c.off)} non si regge in piedi. Il medico di bordo non ha mai visto niente del genere.",
                c => new[]
                {
                    R("scienza", "Far analizzare il contagio dalla Scienza", "onore", O(1, x => { Morale.Change(2, null); Fame(x, 1, 0); return "Un virus dei filtri dell'aria, innocuo se curato in fretta. Lo curiamo in fretta."; })),
                    C("Quarantena per i malati", "", O(0.75, x => { Morale.Change(-1, null); return "Qualche giorno di quarantena e la febbre passa."; }), O(0.25, x => { Officers.Wound(x.off, 2); x.notes.Add(x.off.last + " in infermeria"); return "La quarantena funziona, ma non per tutti: un ufficiale resta in infermeria."; })),
                    C("Ignorare: passerà", "", O(0.5, x => "Passa, per fortuna."), O(0.5, x => { Officers.Wound(x.off, 3); Morale.Change(-4, null); x.notes.Add(x.off.last + " in infermeria"); return "Non passa. Il capitano se ne ricorderà."; })),
                });
            yield return B("nostalgia", "Nostalgia di casa", c => Saga.State.sinceRest >= 5,
                c => "Troppi salti senza toccare un porto. In mensa nessuno parla più, e qualcuno ha appeso alle paratie le foto di casa.",
                c => new[]
                {
                    C("Promettere una licenza al porto più vicino", "", O(1, x => { int port = NearestPort(); if (port >= 0) { Contracts.Plot(port); x.notes.Add($"rotta verso il settore {Places.Of(port)}"); } Morale.Change(3, null); return "Il capitano traccia la rotta verso il porto più vicino. Il morale risale prima ancora di arrivare."; })),
                    C("Una serata di musica e racconti", "onore", O(1, x => { Morale.Change(4, null); return "Qualcuno tira fuori uno strumento, qualcun altro una storia. Per una sera la casa è un po' più vicina."; })),
                    C("Ricordare a tutti il dovere", "", O(1, x => { Morale.Change(-3, null); return "Il discorso è breve. L'effetto, ancora di più."; })),
                });
            yield return B("torneo", "Un torneo di bordo", c => c.off != null,
                c => $"L'equipaggio propone un torneo di scacchi tridimensionali, e tutti scommettono su {Off(c.off)[..^1]}. Serve solo il permesso, e un premio.",
                c => new[]
                {
                    P(300, "Mettere in palio un premio", "", O(1, x => { Cr(x, -300); Morale.Change(6, null); Officers.Credit(x.off.cls, 2); return $"Vince {x.off.last}, dopo una finale di quattro ore. Tutta la nave ne parlerà per settimane."; })),
                    C("Partecipare di persona", "onore", O(0.5, x => { Morale.Change(7, null); return "Il capitano arriva in semifinale e perde con onore. L'equipaggio è entusiasta."; }), O(0.5, x => { Morale.Change(5, null); Fame(x, 1, 0); return "Il capitano vince il torneo. Qualcuno sospetta, nessuno lo dice."; })),
                    C("Non è il momento", "", O(1, x => { Morale.Change(-1, null); return "Le scacchiere tornano negli armadietti."; })),
                });
            yield return B("rivalita", "Una promozione contesa", c => c.off != null && c.off2 != null && c.off.rank == c.off2.rank && c.off.rank < 4,
                c => $"C'è posto per una sola promozione straordinaria, e la meritano in due: {Off(c.off)[..^1]}, e {Off(c.off2)[..^1]}. Lo sanno entrambi.",
                c => new[]
                {
                    C($"Promuovere {c.off.last}", "", O(1, x => { Officers.Promote(x.off); Morale.Change(-1, null); return $"{x.off.last} sale di grado. {x.off2.last} si congratula, a denti stretti."; })),
                    C($"Promuovere {c.off2.last}", "", O(1, x => { Officers.Promote(x.off2); Morale.Change(-1, null); return $"{x.off2.last} sale di grado. {x.off.last} si congratula, a denti stretti."; })),
                    C("Rimandare: nessuna promozione per ora", "", O(1, x => { Morale.Change(-2, null); return "Nessuno è contento, ma almeno nessuno è scontento più dell'altro."; })),
                });
        }
        static int NearestPort()
        {
            try
            {
                var cur = PLServer.GetCurrentSector(); var seen = new HashSet<int> { cur.ID }; var frontier = new List<PLSectorInfo> { cur };
                for (int d = 1; d <= 8 && frontier.Count > 0; d++)
                {
                    var next = new List<PLSectorInfo>();
                    foreach (var s in frontier) { var nb = s.Neighbors; if (nb == null) continue; for (int i = 0; i < nb.Count; i++) { var n = nb[i]; if (n == null || !seen.Add(n.ID)) continue; if (n.VisualIndication != ESectorVisualIndication.NONE && n.MissionSpecificID == -1) return n.ID; next.Add(n); } }
                    frontier = next;
                }
            }
            catch { }
            return -1;
        }

        public static bool StartPort(string id = null)
        {
            var ship = Stations.MyShip; if (!Host || ship == null || cur != null) return false;
            var c = NewCtx(ship); c.port = true;
            var pool = Defs.Where(d => d.portOnly && (id == null ? !recent.Contains(d.id) : d.id == id) && Safe(() => d.when(c))).ToList(); if (pool.Count == 0) return false;
            double tot = pool.Sum(Wt), r = rng.NextDouble() * tot; var pick = pool[^1];
            foreach (var d in pool) { r -= Wt(d); if (r <= 0) { pick = d; break; } }
            pick.setup(c);
            cur = pick; ctx = c; curChoices = pick.choices(c).Where(x => x != null).Take(4).ToArray(); serial++;
            deadline = UnityEngine.Time.time + 120f; botAt = UnityEngine.Time.time + 25f;
            recent.Add(pick.id); if (recent.Count > 6) recent.RemoveAt(0); lastSent = "";
            Director.Notify("IN PORTO · " + pick.title.ToUpperInvariant(), "Il capitano deve decidere: le opzioni sono sulla Bacheca. " + pick.text(c));
            Plugin.L.LogInfo($"[incontri] porto {pick.id}");
            return true;
        }

        // ---- luoghi da esplorare: compaiono solo quando la Scienza ne rileva uno (Exploration.cs)
        static string Disc(Ctx c) { Exploration.Record(c.poi, c.poiName, c.sector); Mysteries.FromExploration(c.poi, c.poiName, c.sector); c.notes.Add("registrato nell'Atlante"); return ""; }
        static string Frag(Ctx c, double chance) { if (rng.NextDouble() < chance * Calling.Bonus("esplorazione") * (1 + 0.1 * Shipyard.Level("sensori")) * Bases.ResearchBonus() * (Research.Has("cartografia") ? 1.4 : 1)) c.notes.Add(Exploration.Fragment(c.poiName)); return ""; }
        static Encounters() { Defs.AddRange(PoiDefs()); Defs.AddRange(PortDefs()); Defs.AddRange(OnboardDefs()); }
        static IEnumerable<Def> PoiDefs()
        {
            Def P(string kind, string title, Func<Ctx, string> text, Func<Ctx, Choice[]> ch) => new Def { id = "luogo_" + kind, title = title, weight = 0, when = c => c.poi == kind, text = text, choices = ch };
            yield return P("relitto", "Relitto antico", c => $"I sensori a lungo raggio agganciano {c.poiName}: uno scafo di un'epoca che nessun archivio ricorda, ancora intatto per metà.", c => new[]
            {
                R("ingegneria", "Riattivare i sistemi del relitto", "", O(0.75, x => { Disc(x); Loot(x, 3, 6); Frag(x, 0.35); return "Le luci del relitto si riaccendono per l'ultima volta. Gli archivi di bordo si aprono, la stiva anche."; }),
                                                                       O(0.25, x => { Disc(x); Loot(x, 2, 4); Foes(x, 1 + x.tier / 2, -1, "Difese del relitto"); return "Si riaccendono anche le difese automatiche del relitto."; })),
                C("Recuperare la scatola nera", "", O(0.6, x => { Disc(x); Frag(x, 0.6); Cr(x, 400); return "La scatola nera parla di una rotta mai tornata indietro."; }),
                                                     O(0.4, x => { Disc(x); Saga.Later("scoperta", 1, 2); return "La scatola nera è cifrata: la Scienza ci lavorerà nei prossimi giorni."; })),
                C("Saccheggiare lo scafo in fretta", "", O(0.7, x => { Disc(x); Loot(x, 2, 4); return "Prendiamo ciò che si stacca senza troppa fatica."; }), O(0.3, x => { Disc(x); Loot(x, 2, 4); Hull(x, 0.15f); return "Una paratia cede mentre usciamo e ci colpisce di striscio."; })),
                C("Lasciarlo riposare", "onore", O(1, x => { Disc(x); Fame(x, 1, 0); return "Registriamo la posizione e lasciamo il relitto ai suoi fantasmi."; })),
            });
            yield return P("rovine", "Rovine", c => $"Sull'asteroide principale del settore la Scienza individua {c.poiName}: strutture regolari, troppo regolari per essere naturali.", c => new[]
            {
                R("scienza", "Decifrare le iscrizioni", "onore", O(1, x => { Disc(x); Fame(x, 2, 0); Frag(x, 0.55); Loot(x, 2, 4); return "Le iscrizioni sono una mappa, o una preghiera. Forse entrambe."; })),
                C("Mandare una squadra a terra", "", O(0.65, x => { Disc(x); Loot(x, 3, 5); Frag(x, 0.3); return "La squadra torna con le casse piene e gli occhi spalancati."; }),
                                                      O(0.35, x => { Disc(x); Loot(x, 2, 4); Foes(x, 2, -1, "Guardiani antichi"); return "Qualcosa, nelle rovine, si sveglia. E non gradisce le visite."; })),
                C("Registrare le coordinate per l'Accademia", "", O(1, x => { Disc(x); Cr(x, 300 + x.tier * 200); return "L'Accademia delle Scienze paga per le coordinate e manderà una spedizione."; })),
            });
            yield return P("giacimento", "Giacimento", c => $"Gli spettrometri impazziscono: nel settore c'è {c.poiName}, puro come raramente se ne trova.", c => new[]
            {
                R("ingegneria", "Estrazione controllata", "", O(1, x => { Disc(x); Cr(x, 1500 + x.tier * 700); return "Le squadre estraggono con calma e precisione: la stiva si riempie di valore."; })),
                C("Estrazione rapida con i raggi traenti", "", O(0.65, x => { Disc(x); Cr(x, 1000 + x.tier * 500); return "Veloce e redditizio."; }), O(0.35, x => { Disc(x); Cr(x, 800 + x.tier * 400); Hull(x, 0.15f); return "Un frammento schizza via dal raggio e centra lo scafo."; })),
                C("Vendere le coordinate alla Gilda", "", O(1, x => { Disc(x); Cr(x, 600 + x.tier * 300); Saga.State.trust["Gilda dei Mercanti"] = Contracts.Trust("Gilda dei Mercanti") + 1; x.notes.Add("fiducia della Gilda +1"); return "La Gilda dei Mercanti compra le coordinate senza discutere."; })),
            });
            yield return P("anomalia", "Anomalia stabile", c => $"Nel settore c'è {c.poiName}: un fenomeno stabile, che si può studiare con calma, se si ha il coraggio di avvicinarsi.", c => new[]
            {
                R("scienza", "Calibrare i sensori sull'anomalia", "", O(1, x => { Disc(x); Sys(x, "sensors", -60); Loot(x, 3, 5); Fame(x, 1, 0); return "I sensori escono dalla calibrazione più precisi di prima, e i collettori pieni di materia esotica."; })),
                C("Raccogliere materia esotica", "", O(0.7, x => { Disc(x); Loot(x, 2, 5); return "I collettori si riempiono di qualcosa che non ha ancora un nome."; }), O(0.3, x => { Disc(x); Loot(x, 2, 4); Sys(x, "shields", 30); return "L'anomalia restituisce il favore: gli scudi perdono la fase."; })),
                C("Registrare e proseguire", "", O(1, x => { Disc(x); return "Tutto finisce nell'Atlante. Per oggi basta così."; })),
            });
            yield return P("cimitero", "Cimitero di navi", c => $"Decine di scafi spenti, allineati come in parata: è {c.poiName}. Nessuno ha mai recuperato niente, qui.", c => new[]
            {
                R("pilota", "Infilarsi nel cuore del cimitero", "", O(0.75, x => { Disc(x); Loot(x, 4, 6); return "Il pilota ci porta dove nessuno era arrivato: l'ammiraglia della flotta perduta, con la stiva intatta."; }), O(0.25, x => { Disc(x); Loot(x, 3, 5); Hull(x, 0.2f); return "Ci arriviamo, ma lasciamo un pezzo di vernice e qualcosa di più."; })),
                C("Recuperare i pezzi migliori ai margini", "", O(0.6, x => { Disc(x); Loot(x, 3, 5); return "Lavoro lungo ma fruttuoso."; }), O(0.4, x => { Disc(x); Loot(x, 2, 4); Foes(x, 1 + x.tier / 2, 0, "Sciacalli"); return "Non siamo i soli sciacalli in zona."; })),
                C("Onorare i caduti", "onore", O(1, x => { Disc(x); Fame(x, 2, 0); Frag(x, 0.25); return "Un minuto di silenzio in plancia. Tra i messaggi registrati dei caduti, un frammento di rotta."; })),
            });
            yield return P("faro", "Faro alieno", c => $"La Scienza capta un segnale regolare, vecchio di millenni: proviene da {c.poiName}. La sua frequenza contiene una struttura che somiglia a una mappa.", c => new[]
            {
                R("scienza", "Interrogare il faro", "", O(1, x => { Disc(x); Frag(x, 1); Loot(x, 2, 4); return "Il faro risponde: un frammento di mappa stellare, e un piccolo dono per chi ha saputo ascoltare."; })),
                C("Smontare il faro", "", O(0.6, x => { Disc(x); Frag(x, 0.7); Loot(x, 3, 5); return "Il faro si spegne per sempre. I suoi componenti sono nostri."; }), O(0.4, x => { Disc(x); Frag(x, 0.5); Foes(x, 2, 0, "Sentinelle del faro"); return "Il faro aveva delle sentinelle. Si svegliano."; })),
                C("Ascoltarne il messaggio", "", O(1, x => { Disc(x); Frag(x, 0.45); return "Ascoltiamo a lungo. Forse abbiamo capito qualcosa."; })),
            });
            yield return P("deposito", "Deposito nascosto", c => $"Sotto la crosta di un asteroide la Scienza trova {c.poiName}, schermato e chiuso con codici militari.", c => new[]
            {
                R("armi", "Far saltare la serratura a distanza", "", O(1, x => { Disc(x); Loot(x, 3, 5); Cr(x, 500 + x.tier * 300); return "Un colpo preciso e il deposito si apre senza danni al contenuto."; })),
                C("Aprirlo di persona", "", O(0.65, x => { Disc(x); Loot(x, 2, 5); Cr(x, 400 + x.tier * 300); return "Dentro, il bottino di qualcuno che non tornerà a prenderlo."; }), O(0.35, x => { Disc(x); Loot(x, 2, 4); Foes(x, 1 + x.tier / 2, 0, "Proprietari del deposito"); return "I proprietari tornano a prenderlo proprio adesso."; })),
                C("Lasciarlo dov'è", "", O(1, x => { Disc(x); return "Non tutto ciò che è nascosto vale il rischio."; })),
            });
        }
        static string Ship() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }

        // ---- avvio
        static Ctx NewCtx(PLShipInfo ship)
        {
            var c = new Ctx { ship = ship, power = Director.Power(ship, out _, out _) }; c.tier = Math.Clamp((int)(c.power / 4f), 0, 4);
            try { var s = PLServer.GetCurrentSector(); c.sector = s?.ID.ToString() ?? "?"; c.fac = s?.MySPI?.Faction ?? -1; } catch { }
            if (c.fac == 3 || c.fac == 6) c.fac = -1;
            c.fname = c.fac >= 0 ? GalaxyWar.Name(c.fac) : "";
            c.off = Officers.Pick(); c.off2 = Officers.PickOther(c.off); c.comp = Competitors.Pick();
            var figs = Nemesis.Alive.Where(f => Nemesis.Engaged != f).ToList(); if (figs.Count > 0 && rng.NextDouble() < 0.5) c.fig = figs[rng.Next(figs.Count)];
            return c;
        }

        public static string Why() { try { var s = PLServer.GetCurrentSector(); return $"cur {(cur == null ? "libero" : cur.id)}, settore {s?.ID} vis {s?.VisualIndication} missione {s?.MissionSpecificID}, curvatura {Stations.MyShip.InWarp}, missione qui {Missions.BusyHere()}, nemesi {Nemesis.Engaged?.Full ?? "-"}, fase {(int)Stations.Get(Stations.MyShip.ShipID, "drx_phase")}"; } catch (Exception e) { return e.Message; } }
        static bool CanStart() { try { var s = PLServer.GetCurrentSector(); return cur == null && s != null && s.VisualIndication == ESectorVisualIndication.NONE && s.MissionSpecificID == -1 && !Stations.MyShip.InWarp && !Missions.BusyHere() && Nemesis.Engaged == null && (int)Stations.Get(Stations.MyShip.ShipID, "drx_phase") != 2; } catch { return false; } } // mai durante una crisi di combattimento

        public static bool Start(string id = null, bool force = false, string poi = null, string poiName = "", string arg = "", int argf = -1)
        {
            var ship = Stations.MyShip; if (!Host || ship == null || (!force && !CanStart()) || (force && cur != null)) return false;
            var c = NewCtx(ship); c.poi = poi; c.poiName = poiName; c.v["arg"] = arg ?? ""; c.v["argf"] = argf.ToString();
            var pool = Defs.Where(d => (id == null ? d.weight > 0 && !d.portOnly && !d.onboard && !recent.Contains(d.id) : d.id == id) && Safe(() => d.when(c))).ToList(); if (pool.Count == 0) return false;
            double tot = pool.Sum(Wt), r = rng.NextDouble() * tot; var pick = pool[^1];
            foreach (var d in pool) { r -= Wt(d); if (r <= 0) { pick = d; break; } }
            pick.setup(c);
            cur = pick; ctx = c; curChoices = pick.choices(c).Where(x => x != null).Take(4).ToArray(); serial++;
            deadline = UnityEngine.Time.time + 120f; botAt = UnityEngine.Time.time + 25f;
            recent.Add(pick.id); if (recent.Count > 6) recent.RemoveAt(0);
            lastEncJump = Obscured.Jumps(); lastSent = "";
            Director.Notify((poi != null ? "SCOPERTA · " : "INCONTRO · ") + pick.title.ToUpperInvariant(), "Il capitano deve decidere: le opzioni sono sulla Bacheca. " + pick.text(c));
            Plugin.L.LogInfo($"[incontri] {pick.id}: {string.Join(" | ", curChoices.Select(x => x.label))}");
            Service.Add("incontri.avviati", 1);
            return true;
        }
        // Le stagioni rendono più frequenti alcuni incontri.
        static double Wt(Def d) => d.weight * (d.id == "assalto_carico" ? Shipyard.Less("stiva") : 1) * (Calling.Is("regata") && d.id == "gara" ? 4 : 1) * (Calling.Is("caccia") && d.id is "pedaggio" or "nemesi_ferita" or "informatore" ? 2.5 : 1) * (Calling.Is("fiera") && d.id is "sonda" or "anomalia" or "campioni" ? 2 : 1) * (Calling.Is("tregua") && d.id is "fronte" ? 0.3 : 1);
        static bool Safe(Func<bool> f) { try { return f(); } catch { return false; } }

        // ---- scelta
        public static string Choose(int i, string who)
        {
            if (!Host || cur == null || i < 0 || i >= curChoices.Length) return "nessun incontro o scelta non valida";
            var ch = curChoices[i]; var (ok, why) = Check(ctx, ch); if (!ok) { Stations.Announce("INCONTRO", "Non possiamo: " + why + "."); return why; }
            double tot = ch.outs.Sum(o => o.w), r = rng.NextDouble() * tot; var o = ch.outs[^1];
            foreach (var x in ch.outs) { r -= x.w; if (r <= 0) { o = x; break; } }
            var c = ctx; var def = cur; cur = null; ctx = null; curChoices = null; lastSent = "";
            string text;
            try { text = o.run(c); } catch (Exception e) { Plugin.L.LogWarning("Incontro: " + e.Message); text = "Le cose non vanno come previsto."; }
            if (ch.tone == "onore") { Morale.Change(2 + Calling.Level("guardiani"), null); Competitors.Renown(2 + Calling.Level("guardiani") * 2); Calling.Add("guardiani"); } else if (ch.tone == "infamia") { Morale.Change(Saga.State.infamia > Saga.State.onore ? 1 : -3, null); Calling.Add("corsari"); }
            string tail = c.notes.Count > 0 ? " (" + string.Join(", ", c.notes) + ")" : "";
            Director.Notify(def.title.ToUpperInvariant(), $"{who}: {ch.label}. {text}{tail}");
            Saga.Chronicle($"{def.title}: {ch.label.ToLowerInvariant()}{(c.notes.Count > 0 ? " — " + string.Join(", ", c.notes) : "")}.");
            Service.Add("incontri.risolti", 1);
            Plugin.L.LogInfo($"[incontri] {def.id} scelta {i} ({ch.label}): {text}{tail}");
            return text + tail;
        }

        public static void Request(int i, int ser)
        {
            if (Host) { if (ser == serial) Choose(i, "Il capitano"); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|incontro|{i}|{ser}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var p = msg.Split('|'); if (p.Length >= 4 && Host && int.TryParse(p[2], out int i) && int.TryParse(p[3], out int s) && s == serial) Choose(i, "Il capitano"); }

        // Capitano automatico (o nessun capitano umano): sceglie fra le opzioni possibili, con un debole per l'onore.
        static int BotPick()
        {
            var idx = Enumerable.Range(0, curChoices.Length).Where(i => Check(ctx, curChoices[i]).ok).ToList(); if (idx.Count == 0) return curChoices.Length - 1;
            double W(int i) => (curChoices[i].req.Contains('+') ? 2.5 : curChoices[i].req is "scienza" or "armi" or "ingegneria" or "pilota" ? 2.0 : 1.0) * (curChoices[i].tone == "infamia" ? (Saga.State.infamia > Saga.State.onore ? 1.2 : 0.3) : curChoices[i].tone == "onore" ? 1.4 : 1.0);
            double tot = idx.Sum(W), r = rng.NextDouble() * tot; foreach (var i in idx) { r -= W(i); if (r <= 0) return i; }
            return idx[^1];
        }
        static bool HumanCaptain() { try { var p = PLServer.Instance.GetCachedFriendlyPlayerOfClass(0, Stations.MyShip); return p != null && !p.IsBot; } catch { return false; } }

        public static void Tick()
        {
            if (!Host) return;
            try
            {
                if (!Saga.Loaded || Stations.MyShip == null) return;
                float now = UnityEngine.Time.time;
                if (cur != null)
                {
                    if (!HumanCaptain() && now >= botAt) Choose(cur.id == "regole" ? 1 : BotPick(), "Il capitano automatico");
                    else if (now >= deadline) Choose(curChoices.Length - 1, "Nessuna risposta dalla plancia, si procede");
                    else if (Stations.MyShip.InWarp) { Plugin.L.LogInfo("[incontri] lasciato indietro col salto"); cur = null; lastSent = ""; }
                }
                // all'arrivo in un settore nuovo, ogni tanto succede qualcosa
                int j = Obscured.Jumps();
                if (lastJumps >= 0 && j != lastJumps && j - lastEncJump >= 1 && rng.NextDouble() < 0.35 * Rules.Encounters) due = now + 8f + rng.Next(10);
                lastJumps = j;
                if (Saga.State.rules.Length == 0 && j >= 1 && cur == null && !Stations.MyShip.InWarp) StartOnboard("regole"); // le regole si scelgono dopo il primo salto
                if (due > 0 && now >= due) { due = -1; Start(); }
                Send(now);
            }
            catch (Exception e) { Plugin.L.LogWarning("Incontri: " + e.Message); }
        }

        static EncView View()
        {
            if (cur == null) return null;
            var v = new EncView { serial = serial, title = cur.title, text = cur.text(ctx), left = (int)Math.Max(0, deadline - UnityEngine.Time.time) };
            foreach (var ch in curChoices) { var (ok, why) = Check(ctx, ch); v.choices.Add(new EncChoiceView { label = ReqTag(ch) + ch.label, ok = ok, hint = ok ? "" : why }); }
            return v;
        }
        static void Send(float now)
        {
            if (now < nextSend) return; nextSend = now + 2f;
            string payload = cur == null ? "" : JsonSerializer.Serialize(View(), json);
            if (payload == lastSent && now % 20f > 2f) return; lastSent = payload;
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)("enc|" + payload), true, new RaiseEventOptions { Receivers = ReceiverGroup.Others }); } catch { }
        }
        public static void Receive(string msg) { try { remote = msg.Length <= 4 ? null : JsonSerializer.Deserialize<EncView>(msg[4..], json); Panels.RefreshAll(); } catch { } }

        public static int Serial => Host ? serial : remote?.serial ?? 0;
        public static string Describe() => cur == null ? $"nessun incontro in corso; catalogo {Defs.Count}: {string.Join(", ", Defs.Select(d => d.id))}" : $"{cur.id}: {cur.text(ctx)}\n      " + string.Join("\n      ", curChoices.Select((x, i) => $"{i}) {ReqTag(x)}{x.label} {(Check(ctx, x).ok ? "" : "(" + Check(ctx, x).why + ")")}"));
    }
}

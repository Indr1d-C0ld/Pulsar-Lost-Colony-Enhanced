// Voci. Le persone parlano: le comparse e i vagabondi delle stazioni e i passeggeri della nave, quando il giocatore
// passa vicino, dicono qualcosa; a volte due persone vicine si scambiano qualche battuta. Le frasi vengono da ciò che
// succede davvero: l'evento del porto (festa, blocco, epidemia, sciopero, boom), la guerra fra le fazioni, la grande
// minaccia, i prezzi, il notiziario e soprattutto noi: se la nave è famosa (o famigerata) la gente la riconosce, e se
// siamo ricercati qualcuno si allontana. I passeggeri parlano del viaggio secondo il loro umore. Il resto è vita di tutti
// i giorni. Le frasi compaiono in un fumetto sopra la testa di chi parla, rivolto alla telecamera, e sfumano.
// Tutto locale, su ogni computer della partita (come la folla): non pesa sulla rete.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Melchior
{
    public static class Voices
    {
        public static bool Enabled = true;
        static readonly System.Random rng = new();
        sealed class Bubble { public GameObject root; public TextMesh text, shadow; public Transform who; public float born, life; }
        static readonly List<Bubble> bubbles = new(); static readonly Dictionary<IntPtr, float> spoke = new();
        static float nextLook, nextAny, nextPair; public static int Heard, UsCount;
        static Font font;

        static string P(params string[] a) => a[rng.Next(a.Length)];
        static string Ship() { try { return (string)Stations.MyShip.ShipName; } catch { return "quella nave"; } }
        static bool Host { get { try { return PhotonNetwork.isMasterClient; } catch { return false; } } }

        // ---------------------------------------------------------------- cosa si dice
        static string Daily() => P(
            "Il caffè sintetico di questa stazione sa di ruggine.", "Mio cugino lavora sulle miniere di elio-3. Non lo vedo da anni.", "Domani riparto, se la curvatura regge.",
            "Hanno di nuovo alzato l'affitto delle cuccette.", "Questo turno non finisce mai.", "Ho sognato la Terra, stanotte. Non l'ho mai vista.",
            "Al terzo ponte c'è un tizio che ripara qualsiasi cosa per due crediti.", "Mia figlia vuole fare la pilota. Le ho detto di pensarci bene.",
            "Le luci qui sfarfallano da una settimana. Nessuno se ne occupa.", "Il cibo della mensa? Meglio non chiedere.", "Ho perso una scommessa sulla regata. Di nuovo.",
            "Dicono che il nuovo capo del molo sia un tipo a posto.", "Qualcuno ha visto il mio gatto? È grigio, risponde a «Ammiraglio».", "Un giorno mi compro una nave tutta mia.",
            "Sento il ronzio dei generatori anche nel sonno.", "Il cielo di questo settore non mi piace. Troppo vuoto.", "Ieri un biscotto Fluffy mi ha guardato storto. Giuro.",
            "Dove si trova un buon meccanico da queste parti?", "Mi manca la gravità vera.", "Ho venduto tutto e ripartirei domani, se solo sapessi per dove.");
        static string AboutPort()
        {
            if (!Host) return null; var cur = PLServer.GetCurrentSector(); var p = cur == null ? null : Ports.Of(cur.ID); if (p == null) return null;
            return p.ev switch
            {
                "festa" => P("Stasera si festeggia: vino a fiumi, dicono.", "Hai visto le luci sul molo? È festa!", "Una festa così non la vedevo da anni."),
                "blocco" => P("Con il blocco dei pirati non arriva più niente.", "Ancora il blocco. Chi lo spezza diventa un eroe, qui.", "Mio fratello è rimasto fuori per il blocco. Spero stia bene."),
                "epidemia" => P("Copriti la bocca, c'è l'epidemia.", "L'infermeria è piena. Servono medici e medicinali.", "Non toccare niente, mi raccomando."),
                "sciopero" => P("Sciopero dei portuali: nessuno carica, nessuno scarica.", "Hanno ragione a scioperare, le paghe sono una vergogna.", "Con lo sciopero il molo è un deserto."),
                "boom" => P("Gli affari vanno a gonfie vele, di questi tempi.", "Tutti comprano, tutti vendono. Speriamo duri.", "Ho fatto più soldi questa settimana che in un anno."),
                _ => p.prosperity < 30 ? P("Questo porto sta morendo, piano piano.", "Una volta qui c'era vita.") : p.prosperity > 75 ? P("Bel porto, questo. Si sta bene.", "Qui non manca niente.") : p.good.Length > 0 ? $"Manca {Market.Low(Market.Goods.FirstOrDefault(g => g.id == p.good)?.name ?? p.good)}: lo pagano oro." : null,
            };
        }
        static string AboutUs()
        {
            string fame = Morale.Fame(); int wanted = Wanted.Level; string ship = Ship();
            if (wanted >= 2) return P($"Quelli della «{ship}»... non sono i ricercati?", "Non guardarli negli occhi. C'è una taglia su di loro.", $"Ho visto la «{ship}» sulla bacheca delle taglie.");
            var mine = News.Items?.Where(n => n.text != null && n.text.Contains(ship)).TakeLast(6).ToList();
            if (mine != null && mine.Count > 0 && rng.NextDouble() < 0.6)
            {
                var n = mine[rng.Next(mine.Count)]; string t = First(n.text);
                return P($"L'hai letto? {t}", $"Si parla di voi, sapete? «{t}»", $"Siete quelli del notiziario! {t}");
            }
            return fame switch
            {
                "Leggende della Frontiera" => P($"La «{ship}»! Mio figlio colleziona le vostre storie.", "Posso stringervi la mano? Una leggenda, qui da noi.", "Vi offrirei da bere, ma immagino ve lo offrano tutti."),
                "Eroi della Frontiera" or "Equipaggio rispettato" => P($"Bella nave, la «{ship}». Ho sentito parlare bene di voi.", "Se tutti gli equipaggi fossero come voi...", "Grazie per quello che fate, là fuori."),
                "Flagello del Margine" or "Predoni senza bandiera" or "Gente da evitare" => P("Meglio stare alla larga da quelli.", $"Gente della «{ship}». Tieni d'occhio il portafoglio.", "Quelli lì portano solo guai."),
                "Canaglie di buon cuore" => P("Mezzi pirati e mezzi santi, quelli della «" + ship + "».", "Non so se fidarmi, ma hanno aiutato mio cugino."),
                _ => null,
            };
        }
        static string AboutGalaxy()
        {
            var items = News.Items; if (items != null && items.Count > 0 && rng.NextDouble() < 0.5)
            {
                var pool = items.Where(n => n.cat is "fronte" or "commercio" or "taglie" or "dicerie" or "scienza").TakeLast(12).ToList();
                if (pool.Count > 0) { var n = pool[rng.Next(pool.Count)]; string t = First(n.text); return P($"Hai sentito? {t}", $"Al notiziario dicevano: {t}", $"{t} Che tempi."); }
            }
            string th = ""; try { th = Saga.State.threat?.name ?? ""; } catch { }
            int stage = 0; try { stage = Saga.State.threat?.stage ?? 0; } catch { }
            if (Host && th.Length > 0 && stage >= 1) return stage >= 3 ? P($"Se {th} arriva qui, io parto per primo.", $"Dicono che {th} abbia spento un altro settore.", $"Pregate che {th} non trovi questo porto.") : P($"Hai sentito parlare di {th}?", $"{Cap(th)}... la gente ne parla a bassa voce.");
            string r = Host ? Market.Rumor() : null; if (!string.IsNullOrEmpty(r)) return P($"Un consiglio da amico: {r}.", $"Dicono che {r}.");
            return null;
        }
        static string Passenger(int i)
        {
            if (!Host) return P("Bella nave.", "Quanto manca all'arrivo?", "Il viaggio è lungo, ma va bene così.");
            var gs = Passengers.P.groups; if (gs.Count == 0) return null; var g = gs[i % gs.Count];
            string dest = Places.Of(g.dest); int left = g.deadline - Obscured.Jumps();
            if (g.mood < 30) return P("Ho pagato per un viaggio tranquillo, non per questo.", "Ancora battaglie? Voglio scendere al primo porto.", "Il capitano sa quello che fa? Spero di sì.");
            if (left < 0) return P($"Dovevamo essere a {dest} da un pezzo.", "Siamo in ritardo. Mi aspettano, sa?");
            return g.kind switch
            {
                "profughi" => P("Grazie per averci presi a bordo.", "I bambini dormono, finalmente.", $"Speriamo che a {dest} ci sia posto per noi."),
                "pellegrini" => P("Che le stelle vi proteggano.", "Il viaggio è parte del pellegrinaggio, dicono.", "Pregheremo per la vostra nave."),
                "scienziati" => P("Posso dare un'occhiata ai vostri sensori? Solo un'occhiata.", "Affascinante, la radiazione di fondo di questo settore.", "All'Accademia non ci crederanno."),
                "diplomatici" => P("Spero che i colloqui vadano bene.", "Mi raccomando, discrezione su chi siamo.", $"A {dest} mi aspettano trattative difficili."),
                "ospite" => P("La cabina è... rustica.", "Di solito viaggio con più comodità.", "Avete un vino decente, a bordo?"),
                "coloni" => P("Una nuova vita, laggiù.", "Abbiamo venduto tutto per questo viaggio.", "Non vedo l'ora di vedere la nostra colonia."),
                "fuggiasco" => P("Non ho mai visto la vostra nave, d'accordo?", "Meno domande, più velocità.", "Se qualcuno chiede di me, non sono qui."),
                "tecnici" => P("Quel relè lì fa un rumore strano. Ci do un'occhiata.", "Bella nave, ma la manutenzione...", "Se serve una mano in sala macchine, chiamate."),
                "medici" => P("Se qualcuno si ferisce, chiamatemi.", "La vostra infermeria è ben tenuta.", "Bevete acqua, tutti. Me lo dicono tutti i pazienti, dopo."),
                _ => P($"Quanto manca a {dest}?", "Bella nave, capitano."),
            };
        }
        // botta e risposta fra due persone
        static (string a, string b) Pair()
        {
            string th = ""; try { th = Saga.State.threat?.name ?? ""; } catch { }
            var opts = new List<(string, string)>
            {
                ("Come va il lavoro?", "Come sempre: troppo e pagato poco."), ("Hai mai visto un Infetto da vicino?", "Una volta. Non voglio rivederlo."),
                ("Stasera al bar?", "Solo se offri tu."), ("Dove vai, dopo?", "Dove mi portano i crediti."), ("Hai sentito della regata?", "Ho scommesso sul favorito. Ho perso."),
                ("Ti trovo stanco.", "Turno doppio. Il terzo questa settimana."), ("Credi che la guerra arriverà qui?", "Spero di no. Ma ci credo."),
                ("Hai visto quella nave che è appena arrivata?", "Bella. Chissà chi la comanda."), ("Mi presti dieci crediti?", "Ancora? Mi devi già la cena di ieri."),
            };
            if (th.Length > 0) opts.Add(($"Hai sentito di {th}?", "Chi non ne ha sentito parlare... Speriamo restino lontani."));
            if (Wanted.Level >= 2) opts.Add(($"Quella è la «{Ship()}»?", "Sì. E c'è una taglia sopra. Andiamocene."));
            if (Morale.Fame() is "Leggende della Frontiera" or "Eroi della Frontiera") opts.Add(($"Guarda, la «{Ship()}»!", "Quelli di cui parlano tutti? Fammi vedere!"));
            return opts[rng.Next(opts.Count)];
        }
        static string First(string t) { if (string.IsNullOrEmpty(t)) return ""; int i = t.IndexOfAny(new[] { '.', '!', '?' }); string s = i > 10 && i < 140 ? t[..(i + 1)] : t.Length > 140 ? t[..137] + "…" : t; return s; }
        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
        static string Line(bool passenger, int index)
        {
            if (passenger) return Passenger(index) ?? Daily();
            double r = rng.NextDouble(); string l = null;
            if (r < 0.28) { l = AboutUs(); if (l != null) AboutUs_(); }
            else if (r < 0.50) l = AboutPort();
            else if (r < 0.72) l = AboutGalaxy();
            return l ?? Daily();
        }
        static void AboutUs_() { UsCount++; Service.Add("voci.noi", 1); try { Achievements.SetProgress("MEL_VOICES_FAME", (int)Service.Get("voci.noi")); } catch { } }

        // ---------------------------------------------------------------- chi parla, e il fumetto
        static readonly List<(Transform t, bool pax, int i)> speakers = new();
        public static void Tick()
        {
            if (!Enabled) return;
            try
            {
                float now = Time.time; Fade(now);
                Transform me = null; try { me = PLNetworkManager.Instance?.LocalPlayer?.MyPawn?.transform; } catch { }
                if (me == null) return;
                if (now >= nextLook) { nextLook = now + 2f; Gather(); }
                if (speakers.Count == 0) return;
                var pos = me.position;
                // una battuta da chi passa vicino
                if (now >= nextAny)
                {
                    nextAny = now + 3f + (float)rng.NextDouble() * 4f;
                    var near = speakers.Where(s => s.t != null && (s.t.position - pos).sqrMagnitude < 25f && (!spoke.TryGetValue(s.t.Pointer, out var at) || now - at > 30f)).ToList();
                    if (near.Count > 0 && rng.NextDouble() < 0.55) { var s = near[rng.Next(near.Count)]; Say(s.t, Line(s.pax, s.i), now); }
                }
                // due persone vicine fra loro, a portata d'orecchio
                if (now >= nextPair)
                {
                    nextPair = now + 15f + (float)rng.NextDouble() * 20f;
                    var cand = speakers.Where(s => s.t != null && !s.pax && (s.t.position - pos).sqrMagnitude < 144f).ToList();
                    for (int i = 0; i < cand.Count; i++) for (int j = i + 1; j < cand.Count; j++)
                    {
                        if ((cand[i].t.position - cand[j].t.position).sqrMagnitude > 9f) continue;
                        var (a, b) = Pair(); Say(cand[i].t, a, now); Say(cand[j].t, b, now + 2.5f); i = cand.Count; break;
                    }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Voci: " + e.Message); nextLook = Time.time + 10f; }
        }
        static void Gather()
        {
            speakers.Clear();
            foreach (var go in Crowds.Clones) if (go != null) speakers.Add((go.transform, false, 0));
            foreach (var w in UnityEngine.Object.FindObjectsOfType<PLWanderer>()) if (w != null) speakers.Add((w.transform, false, 0));
            int k = 0; foreach (var go in PassengersWalk.Figures) if (go != null) speakers.Add((go.transform, true, k++));
        }
        static void Say(Transform who, string text, float at)
        {
            if (who == null || string.IsNullOrEmpty(text)) return;
            spoke[who.Pointer] = at; Heard++; Service.Add("voci.ascoltate", 1);
            if (font == null) { try { font = PLInGameUI.Instance?.HealthValueLabel?.font; } catch { } if (font == null) return; }
            foreach (var old in bubbles.Where(b => b.who == who).ToList()) Kill(old);
            var root = new GameObject("Voce"); root.transform.position = who.position + Vector3.up * 1.9f; int layer = who.gameObject.layer; root.layer = layer;
            TextMesh Make(string name, Color c, Vector3 off)
            {
                var go = new GameObject(name); go.layer = layer; go.transform.SetParent(root.transform, false); go.transform.localPosition = off;
                var tm = go.AddComponent<TextMesh>(); tm.font = font; tm.text = Wrap(text, 34); tm.fontSize = 64; tm.characterSize = 0.022f; tm.anchor = TextAnchor.LowerCenter; tm.alignment = TextAlignment.Center; tm.color = c;
                var mr = go.GetComponent<MeshRenderer>(); if (mr != null) mr.material = font.material; return tm;
            }
            var b = new Bubble { root = root, who = who, born = at, life = 4.5f + text.Length * 0.05f };
            b.shadow = Make("Ombra", new Color(0f, 0f, 0f, 0.85f), new Vector3(0.006f, -0.006f, 0.004f)); b.text = Make("Testo", new Color(1f, 0.97f, 0.88f, 1f), Vector3.zero);
            root.SetActive(at <= Time.time); bubbles.Add(b);
        }
        static void Fade(float now)
        {
            var cam = Cam();
            foreach (var b in bubbles.ToList())
            {
                if (b.root == null) { bubbles.Remove(b); continue; }
                if (now < b.born) continue; if (!b.root.activeSelf) b.root.SetActive(true);
                float t = now - b.born; if (t > b.life || b.who == null) { Kill(b); continue; }
                b.root.transform.position = b.who.position + Vector3.up * 1.9f;
                if (cam != null) b.root.transform.rotation = cam.transform.rotation; // rivolto alla telecamera, leggibile
                float a = t < 0.3f ? t / 0.3f : t > b.life - 0.8f ? (b.life - t) / 0.8f : 1f;
                var c = b.text.color; b.text.color = new Color(c.r, c.g, c.b, a); var s = b.shadow.color; b.shadow.color = new Color(s.r, s.g, s.b, 0.85f * a);
            }
        }
        // la telecamera che sta disegnando la scena: quella principale se c'è, altrimenti la più «in alto» fra le attive
        static Camera cam; static float camAt;
        static Camera Cam()
        {
            if (cam != null && cam.isActiveAndEnabled && Time.time - camAt < 2f) return cam; camAt = Time.time;
            try { cam = Camera.main; } catch { cam = null; }
            if (cam == null || !cam.isActiveAndEnabled) { Camera best = null; foreach (var c in Camera.allCameras) if (c != null && c.isActiveAndEnabled && c.targetTexture == null && (best == null || c.depth > best.depth)) best = c; cam = best; }
            return cam;
        }
        static void Kill(Bubble b) { if (b.root != null) UnityEngine.Object.Destroy(b.root); bubbles.Remove(b); }
        static string Wrap(string s, int w)
        {
            var words = s.Split(' '); var sb = new System.Text.StringBuilder(); int col = 0;
            foreach (var word in words) { if (col > 0 && col + word.Length + 1 > w) { sb.Append('\n'); col = 0; } else if (col > 0) { sb.Append(' '); col++; } sb.Append(word); col += word.Length; }
            return sb.ToString();
        }
        public static string Describe() { var c = Cam(); return $"voci {(Enabled ? "attive" : "spente")}: persone che possono parlare {speakers.Count}, fumetti {bubbles.Count}, battute sentite {Heard}, su di noi {UsCount}; telecamera {c?.name} (maschera {c?.cullingMask}), livelli dei fumetti {string.Join(",", bubbles.Where(b => b.root != null).Select(b => b.root.layer))}"; }
        public static string ForceOn(int cls)
        {
            var p = PLServer.Instance.GetCachedFriendlyPlayerOfClass(cls, Stations.MyShip); var t = p?.MyPawn?.transform; if (t == null) return "nessuno a quella stazione";
            string l = Line(false, 0); Say(t, l, Time.time); return $"«{l}»";
        }
        public static string Force()
        {
            Transform me = PLNetworkManager.Instance?.LocalPlayer?.MyPawn?.transform; Gather(); if (me == null || speakers.Count == 0) return "nessuno vicino";
            var s = speakers.Where(x => x.t != null).OrderBy(x => (x.t.position - me.position).sqrMagnitude).First(); string l = Line(s.pax, s.i); Say(s.t, l, Time.time);
            return $"a {(s.t.position - me.position).magnitude:0.0} m: «{l}»";
        }
    }
}

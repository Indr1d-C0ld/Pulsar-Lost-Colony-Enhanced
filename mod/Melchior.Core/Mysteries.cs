// Indagini e archeologia dello spazio.
//
// CIVILTÀ PERDUTE. Ogni galassia ha quattro o cinque civiltà scomparse, generate dal seme: nome, epiteto, natura,
// materiale dei loro manufatti, epoca e modo in cui sono finite. La loro storia è divisa in sei capitoli che si
// ricostruiscono un pezzo alla volta (fari decifrati, strati scavati, reperti studiati, rovine dell'Atlante). Con
// tutti i capitoli la civiltà è ricostruita: la Banca dati ne racconta la storia e si scopre dove sorgeva il loro
// mondo d'origine, l'ultimo scavo, custodito.
//
// SEGNALI. Saltando, a volte la Scienza capta un segnale: un SOS degradato, il faro di una civiltà perduta, una stazione
// dei numeri, un'eco gravitazionale, un canto alieno, una richiesta d'aiuto troppo regolare per essere vera, e molto di
// rado una trasmissione impossibile. Per trovarne l'origine si TRIANGOLA: a ogni salto con la Scienza in servizio si
// misura la direzione del segnale da un punto diverso della mappa; due rilevamenti abbastanza distanti restringono la
// zona, tre (o una sonda) la individuano. Intanto lo si DECIFRA (a ogni salto, o spendendo dati di ricerca): al 30%
// se ne capisce la natura, al 60% il contenuto (e se è un'esca), al 100% il messaggio intero, che all'arrivo cambia
// le cose. I segnali si spengono dopo una decina di salti: chi tarda, perde la pista.
//
// SCAVI. Le rovine hanno tre strati. Scavare richiede tempo vero nel settore (metodico o rapido), mentre il sito può
// svegliarsi, arrivare una spedizione rivale o i predoni attratti dal rumore. Ogni strato dà reperti e un capitolo.
//
// REPERTI. Ogni reperto ha civiltà, rarità, stato di conservazione, valore e una piccola storia; alcuni portano con sé
// qualcosa di sgradevole. Si possono studiare (dati e conoscenza), donare all'Accademia, vendere a un museo di porto
// o al mercato nero, o gettare nel vuoto.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Melchior
{
    public class CivState { public string id = "", name = "", epithet = "", nature = "", fate = "", material = "", era = ""; public List<int> chapters = new(); public int home = -1; public bool homeKnown, homeDone; }
    public class SignalState
    {
        public int id, source = -1, heard, fade, decode, quality = 50; public string kind = "", civ = "", ship = ""; public bool fix, reached, academy;
        public List<float[]> bearings = new(); // [x, y, angolo, settore]
        public int reward;
    }
    public class DigState { public int sector; public string civ = "", name = ""; public int strata; public bool home, guarded; }
    public class RelicState { public int id; public string name = "", civ = "", story = "", curse = ""; public int rarity, condition, value; public bool studied; }
    public class MysteryState
    {
        public List<CivState> civs = new(); public List<SignalState> signals = new(); public List<DigState> digs = new(); public List<RelicState> relics = new();
        public int nextId = 1, heard, decoded, located, lost, dug, found, donated, sold, studied, rebuilt;
    }

    public static class Mysteries
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        public static MysteryState M => Saga.State.mystery ??= new MysteryState();
        static int Seed() { try { return (int)PLServer.Instance.GalaxySeed; } catch { return 0; } }

        // ---------------------------------------------------------------- civiltà
        static readonly string[] Syl1 = { "Vel", "Keth", "Or", "Tha", "Ylm", "Sar", "Ana", "Quor", "Del", "Ish", "Mor", "Zeph", "Lu", "Ny", "Ost", "Ver" };
        static readonly string[] Syl2 = { "ari", "rin", "athi", "oni", "uvel", "en", "ishi", "amar", "eth", "ori", "ydi", "anth", "eli", "urim" };
        static readonly (string nature, string epithet, string material, string item)[] Natures =
        {
            ("costruttori", "i Costruttori di Fari", "pietra stellare", "pilastro"), ("cantori", "i Cantori del Vuoto", "cristallo risonante", "diapason"),
            ("navigatori", "i Navigatori delle Correnti", "metallo vivo", "astrolabio"), ("custodi", "i Custodi della Memoria", "ossidiana incisa", "tavoletta"),
            ("guerrieri", "la Stirpe delle Mille Lame", "acciaio nero", "lama"), ("giardinieri", "i Giardinieri delle Stelle", "seme cristallino", "reliquiario"),
            ("tessitori", "i Tessitori di Luce", "luce solida", "telaio"), ("sognatori", "i Sognatori Immobili", "vetro d'ombra", "maschera"),
        };
        static readonly string[] Fates = { "si dissolsero in una guerra contro se stessi", "partirono tutti insieme verso un'altra galassia", "trascesero la materia, dicono le loro ultime iscrizioni", "furono spazzati via da una stella impazzita", "si spensero per un morbo che nessuna medicina fermò", "scelsero di dormire, e non si svegliarono più", "furono cancellati da un nemico di cui non resta il nome" };
        static readonly string[] Eras = { "dodicimila anni fa", "quarantamila anni fa", "prima che la Colonia avesse un nome", "novantamila anni fa", "all'alba delle prime rotte di curvatura", "mezzo milione di anni fa" };

        public static void EnsureCivs()
        {
            if (M.civs.Count > 0) return;
            var r = new System.Random(unchecked(Seed() * 131 + 7)); int n = 4 + r.Next(2); var used = new HashSet<string>(); var natures = Natures.OrderBy(_ => r.Next()).ToList();
            for (int i = 0; i < n; i++)
            {
                string name; do { name = Syl1[r.Next(Syl1.Length)] + Syl2[r.Next(Syl2.Length)]; } while (!used.Add(name));
                var nt = natures[i % natures.Count];
                M.civs.Add(new CivState { id = name.ToLowerInvariant(), name = name, epithet = nt.epithet, nature = nt.nature, material = nt.material, fate = Fates[r.Next(Fates.Length)], era = Eras[r.Next(Eras.Length)] });
            }
            Saga.MarkDirty();
        }
        public static CivState Civ(string id) { EnsureCivs(); return M.civs.FirstOrDefault(c => c.id == id) ?? M.civs[0]; }
        static CivState RandomCiv() { EnsureCivs(); var open = M.civs.Where(c => c.chapters.Count < 6).ToList(); return (open.Count > 0 ? open : M.civs)[rng.Next(open.Count > 0 ? open.Count : M.civs.Count)]; }
        static string Of(CivState c) => "dei " + c.name;

        // i sei capitoli della storia di una civiltà, scritti dal loro carattere
        public static string Chapter(CivState c, int i) => i switch
        {
            0 => $"Le origini: i {c.name} nacquero su un mondo dalle notti lunghissime, {c.era} o anche prima. Si chiamavano da sé {c.epithet}.",
            1 => c.nature switch
            {
                "costruttori" => "Il loro dono: costruivano fari che parlavano fra loro attraverso il vuoto, e tracciavano rotte che nessuno ha più percorso.",
                "cantori" => "Il loro dono: parlavano cantando, e le loro navi si guidavano con la voce. Il silenzio, per loro, era una forma di lutto.",
                "navigatori" => "Il loro dono: leggevano le correnti dello spazio come noi leggiamo il vento. Le loro navi erano vive e crescevano.",
                "custodi" => "Il loro dono: ricordavano tutto. Incidevano ogni nascita, ogni morte, ogni debito su tavolette che non si consumano.",
                "guerrieri" => "Il loro dono: la guerra come arte. Ogni lama aveva un nome, e ogni nome una genealogia.",
                "giardinieri" => "Il loro dono: seminavano la vita su mondi morti. Alcuni pianeti che oggi chiamiamo nostri potrebbero essere i loro giardini.",
                "tessitori" => "Il loro dono: intrecciavano la luce fino a renderla solida. Le loro città non avevano ombre.",
                _ => "Il loro dono: sognavano insieme. Le loro decisioni nascevano da un sogno condiviso da un intero popolo.",
            },
            2 => $"Il loro impero: si estesero su decine di sistemi. I loro manufatti sono di {c.material}, e portano tutti lo stesso segno, ripetuto come una firma.",
            3 => "La crepa: le iscrizioni più tarde parlano di una scelta che divise il popolo. Metà dei sigilli di quell'epoca sono stati cancellati a mano.",
            4 => $"La fine: {c.fate}. Le ultime iscrizioni sono calme, quasi serene.",
            _ => "L'eredità: le coordinate del loro mondo d'origine erano nascoste nelle iscrizioni, in un ordine che solo chi aveva letto tutto poteva ricomporre.",
        };
        static string AddChapter(CivState c, string from)
        {
            var missing = Enumerable.Range(0, 6).Where(i => !c.chapters.Contains(i)).ToList(); if (missing.Count == 0) return "";
            int ch = missing.Count == 1 ? missing[0] : missing.Where(i => i < 5).OrderBy(_ => rng.Next()).First(); // l'eredità arriva per ultima
            c.chapters.Add(ch); Saga.MarkDirty(); GalaxyView.Dirty();
            string note = $"capitolo {c.chapters.Count}/6 della storia {Of(c)}";
            if (c.chapters.Count >= 6) Rebuilt(c);
            Plugin.L.LogInfo($"[indagini] {c.name}: capitolo {ch} da {from}");
            return note;
        }
        static void Rebuilt(CivState c)
        {
            M.rebuilt++; Service.Add("indagini.civilta", 1); Feats.Mystery("civilta");
            var home = PickSector(5, 9); if (home != null) { c.home = home.ID; c.homeKnown = true; M.digs.Add(new DigState { sector = home.ID, civ = c.id, name = $"il mondo d'origine {Of(c)}", home = true, guarded = true }); Course.Plot(home.ID); }
            Director.Notify("CIVILTÀ RICOSTRUITA", $"Abbiamo letto tutta la storia {Of(c)}, {c.epithet}. " + (home != null ? $"Le ultime iscrizioni indicano il loro mondo d'origine: settore {Places.Of(home.ID)}, a {Nemesis.JumpsTo(home.ID)} salti. Rotta tracciata." : ""));
            News.Post("scienza", $"L'Accademia delle Scienze annuncia: la «{Ship()}» ha ricostruito la storia {Of(c)}, {c.epithet}, scomparsi {c.era}.");
            Saga.Chronicle($"Ricostruita la storia {Of(c)}."); Research.Add(15, "una civiltà ricostruita"); Competitors.Renown(25);
        }

        // ---------------------------------------------------------------- segnali
        static readonly (string kind, double w)[] Kinds = { ("sos", 3), ("faro", 3), ("numeri", 2), ("eco", 2), ("canto", 1.4), ("esca", 2), ("futuro", 0.35) };
        public static string KindName(string k) => k switch
        {
            "sos" => "SOS degradato", "faro" => "faro di una civiltà perduta", "numeri" => "stazione dei numeri", "eco" => "eco gravitazionale",
            "canto" => "canto alieno", "esca" => "richiesta d'aiuto (esca)", "futuro" => "trasmissione impossibile", _ => k,
        };
        // Quel che si sa del segnale dipende da quanto è stato decifrato: sotto il 30% non se ne conosce nemmeno la natura,
        // e un'esca sembra un SOS finché non la si decifra al 60%.
        public static string Known(SignalState s) => s.decode < 30 ? $"segnale non identificato n. {s.id}" : s.kind == "esca" && s.decode < 60 ? "SOS degradato" : KindName(s.kind) + (s.civ.Length > 0 && s.decode >= 60 ? " " + Of(Civ(s.civ)) : "");

        static PLSectorInfo Cur() { try { return PLServer.GetCurrentSector(); } catch { return null; } }
        static PLSectorInfo PickSector(int min, int max)
        {
            var cur = Cur(); if (cur == null) return null; var near = Market.Within(cur, max); var close = Market.Within(cur, min - 1);
            var pool = new List<PLSectorInfo>();
            foreach (var id in near.Where(x => !close.Contains(x)))
            {
                try { var s = PLServer.GetSectorWithID(id); if (s?.MySPI != null && s.VisualIndication == ESectorVisualIndication.NONE && s.MissionSpecificID == -1 && !M.digs.Any(d => d.sector == id) && !M.signals.Any(x => x.source == id)) pool.Add(s); } catch { }
            }
            return pool.Count == 0 ? null : pool[rng.Next(pool.Count)];
        }
        // quanto ascolta bene la nostra Scienza: qualcuno alla stazione, grado e tratto dell'ufficiale, ricerca e talenti
        static int Ears(out bool manned)
        {
            manned = false; float q = 0f;
            try { var p = PLServer.Instance.GetCachedFriendlyPlayerOfClass(2, Stations.MyShip); manned = p != null; if (p != null) { q = 45f + (p.IsBot ? 10f * Officers.Skill(2, false) : 25f); q += 6f * CrewMind.Rank(p, ETalents.SCI_SENSOR_BOOST); } } catch { }
            if (Research.Has("sensori")) q += 12f; if (Wonders.Kind == "spazio_morto") q -= 30f;
            return (int)Math.Clamp(q, 0f, 100f);
        }

        public static SignalState Hear(string kind = null, bool academy = false)
        {
            var cur = Cur(); if (cur == null) return null; EnsureCivs();
            if (kind == null) { double tot = Kinds.Sum(k => k.w), x = rng.NextDouble() * tot; kind = Kinds[^1].kind; foreach (var k in Kinds) { x -= k.w; if (x <= 0) { kind = k.kind; break; } } }
            var src = PickSector(kind == "futuro" ? 4 : 3, kind == "faro" ? 8 : 6); if (src == null) return null;
            var s = new SignalState { id = M.nextId++, kind = kind, source = src.ID, heard = Obscured.Jumps(), fade = Obscured.Jumps() + 10 + rng.Next(6), academy = academy };
            if (kind is "faro" or "canto") s.civ = RandomCiv().id;
            if (kind == "sos") s.ship = new[] { "Speranza di Vega", "Ammiraglio Halloran", "Lanterna", "Nadir", "Seconda Alba", "Ostinata", "Mira Ceti", "Ultimo Approdo" }[rng.Next(8)];
            M.signals.Add(s); M.heard++; Service.Add("indagini.segnali", 1); Saga.MarkDirty(); GalaxyView.Dirty();
            Listen(s, cur, true);
            Director.Notify("SEGNALE CAPTATO", academy ? $"L'Accademia ci affida un segnale da rintracciare: {Known(s)}. Primo rilevamento fatto." : $"La Scienza capta qualcosa nel rumore di fondo: {Known(s)}. Direzione rilevata; per trovarne l'origine servono altri rilevamenti da settori diversi.");
            Plugin.L.LogInfo($"[indagini] segnale {s.id} {kind} dall'origine {src.ID}");
            return s;
        }

        // Un rilevamento: la direzione del segnale vista dal settore corrente, con un errore che dipende da chi ascolta.
        static void Listen(SignalState s, PLSectorInfo from, bool first = false)
        {
            if (s.fix || s.bearings.Any(b => (int)b[3] == from.ID)) return;
            var src = PLServer.GetSectorWithID(s.source); if (src == null) return;
            int q = Ears(out bool manned); if (!manned && !first) return; s.quality = Math.Max(s.quality, q);
            var a = from.Position; var b = src.Position;
            float ang = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg; float err = Mathf.Lerp(14f, 3f, q / 100f);
            ang += ((float)rng.NextDouble() * 2f - 1f) * err;
            s.bearings.Add(new[] { a.x, a.y, ang, from.ID });
            var cands = Candidates(s);
            if (cands.Count == 1 || (s.bearings.Count >= 3 && cands.Contains(s.source))) Locate(s, "triangolazione");
            Saga.MarkDirty(); GalaxyView.Dirty();
        }
        // settori compatibili con tutti i rilevamenti (entro la tolleranza)
        public static List<int> Candidates(SignalState s)
        {
            var l = new List<int>(); if (s.bearings.Count == 0) return l;
            float tol = Mathf.Lerp(18f, 6f, s.quality / 100f);
            try
            {
                foreach (var kv in PLGlobal.Instance.Galaxy.AllSectorInfos)
                {
                    var x = kv.Value; if (x?.MySPI == null) continue; var p = x.Position; bool ok = true;
                    foreach (var b in s.bearings)
                    {
                        if ((int)b[3] == x.ID) { ok = false; break; }
                        float ang = Mathf.Atan2(p.y - b[1], p.x - b[0]) * Mathf.Rad2Deg; if (Mathf.Abs(Mathf.DeltaAngle(ang, b[2])) > tol) { ok = false; break; }
                    }
                    if (ok) l.Add(x.ID);
                }
            }
            catch { }
            if (!l.Contains(s.source) && s.bearings.Count > 0) l.Add(s.source); // l'origine vera resta sempre fra i candidati
            return l;
        }
        static void Locate(SignalState s, string how)
        {
            if (s.fix) return; s.fix = true; M.located++; Service.Add("indagini.localizzati", 1); Feats.Mystery("localizzato");
            Director.Notify("SEGNALE LOCALIZZATO", $"{Cap(Known(s))}: l'origine è nel settore {Places.Of(s.source)}, a {Nemesis.JumpsTo(s.source)} salti ({how}). Rotta disponibile dalla scheda Indagini.");
            Saga.MarkDirty(); GalaxyView.Dirty();
        }
        static void Decode(SignalState s, int add, string how)
        {
            int before = s.decode; s.decode = Math.Min(100, s.decode + add); if (s.decode == before) return;
            if (before < 30 && s.decode >= 30) Stations.LogEvent(Stations.MyShip.ShipID, $"Scienza: il segnale n. {s.id} è {KindName(s.kind == "esca" ? "sos" : s.kind)}.");
            if (before < 60 && s.decode >= 60)
            {
                if (s.kind == "esca") Director.Notify("SEGNALE DECIFRATO", "Il segnale si ripete con una regolarità che nessun naufrago avrebbe: è un'esca. Chi lo trasmette ci aspetta armato.");
                else Stations.LogEvent(Stations.MyShip.ShipID, $"Scienza: {Hint(s)}");
            }
            if (before < 100 && s.decode >= 100)
            {
                M.decoded++; Service.Add("indagini.decifrati", 1); Feats.Mystery("decifrato");
                string extra = s.kind == "faro" ? AddChapter(Civ(s.civ), "un faro decifrato") : "";
                Director.Notify("SEGNALE DECIFRATO", Message(s) + (extra.Length > 0 ? $" ({extra})" : ""));
            }
            Saga.MarkDirty(); GalaxyView.Dirty();
        }
        static string Hint(SignalState s) => s.kind switch
        {
            "sos" => $"è la «{s.ship}», persa da anni. Il messaggio parla di sistemi in avaria e di «dormienti» a bordo.",
            "faro" => $"è un faro {Of(Civ(s.civ))}: indica un luogo, forse un loro avamposto.",
            "numeri" => "sequenze di numeri lette da una voce sintetica: è un codice, e i codici custodiscono qualcosa.",
            "eco" => "la sorgente piega lo spazio attorno a sé: è un'anomalia gravitazionale stabile. Avvicinarsi senza conoscerla è un rischio.",
            "canto" => "è un canto, e cambia quando rispondiamo. Qualcosa, là fuori, ci sta ascoltando.",
            "futuro" => "porta il codice identificativo della nostra nave. Ma la data è sbagliata.",
            _ => "",
        };
        static string Message(SignalState s) => s.kind switch
        {
            "sos" => $"Il diario della «{s.ship}» per intero: i superstiti si sono messi in criosonno in attesa di soccorsi. Il codice di accesso dello scafo è nel messaggio.",
            "faro" => $"Il faro {Of(Civ(s.civ))} recita una sola frase, ripetuta da millenni: «Qui riposa ciò che abbiamo amato.»",
            "numeri" => "La chiave dei numeri: coordinate e combinazione di un deposito nascosto. Chi lo usava non tornerà a prenderlo.",
            "eco" => "Il modello completo dell'anomalia: sappiamo dove passare senza farci male, e cosa misurare.",
            "canto" => "Il canto ha una grammatica. Possiamo rispondere senza offendere.",
            "esca" => "I predoni che trasmettono l'esca usano frequenze di una banda nota: ora sappiamo chi sono e quanti.",
            "futuro" => "«Qui la vostra nave, da molti salti nel futuro. Non chiedete come. Prendete questa mappa e non fidatevi del silenzio.» Il messaggio contiene un frammento di mappa.",
            _ => "",
        };

        // ---------------------------------------------------------------- ciclo
        static int lastSector = -1, lastJump = -1; static float arriveAt = -1; static readonly HashSet<int> chanceTried = new();
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                var cur = Cur(); if (cur == null) return; bool warp = false; try { warp = Stations.MyShip.InWarp; } catch { }
                if (warp) { if (dig != null) Interrupt("il salto"); return; }
                if (cur.ID != lastSector) { lastSector = cur.ID; arriveAt = Time.time + 6f; }
                if (arriveAt > 0 && Time.time > arriveAt) { arriveAt = -1; Arrived(cur); }
                DigTick(cur);
                CurseTick();
            }
            catch (Exception e) { Plugin.L.LogWarning("Indagini: " + e.Message); }
        }
        static void Arrived(PLSectorInfo cur)
        {
            int j = Obscured.Jumps();
            if (j != lastJump)
            {
                lastJump = j;
                // ogni salto: si ascolta, si decifra, i segnali vecchi si spengono, a volte se ne capta uno nuovo
                int ears = Ears(out bool manned);
                foreach (var s in M.signals.Where(x => !x.reached).ToList())
                {
                    if (manned) { Listen(s, cur); Decode(s, 6 + ears / 9, "routine"); }
                    if (!s.fix && j >= s.fade) { M.signals.Remove(s); M.lost++; Stations.LogEvent(Stations.MyShip.ShipID, $"Scienza: {Known(s)} si è spento. Pista perduta."); }
                }
                double p = 0.07 + (manned ? 0.03 + ears / 2500.0 : 0) + (Wonders.Kind == "allineamento" ? 0.05 : 0) - (Wonders.Kind == "spazio_morto" ? 0.06 : 0);
                if (M.signals.Count(x => !x.reached) < 3 && rng.NextDouble() < p) Hear();
                M.signals.RemoveAll(x => x.reached && j - x.heard > 30);
            }
            // arrivo all'origine di un segnale
            var sig = M.signals.FirstOrDefault(x => x.source == cur.ID && !x.reached);
            if (sig != null) { if (!sig.fix) Locate(sig, "arrivo sul posto"); Site(sig); return; }
            // arrivo a uno scavo
            var d = M.digs.FirstOrDefault(x => x.sector == cur.ID && x.strata < 3);
            if (d != null) { StartDigEncounter(d); return; }
            // ritrovamento fortuito: rovine che affiorano dove nessuno le cercava (una volta per settore)
            if (!M.digs.Any(x => x.sector == cur.ID) && !chanceTried.Contains(cur.ID) && cur.MissionSpecificID == -1)
            {
                chanceTried.Add(cur.ID); Ears(out bool sci);
                bool wild = cur.VisualIndication is ESectorVisualIndication.ASTEROID_FIELD or ESectorVisualIndication.NEBULA or ESectorVisualIndication.GRAVEYARD or ESectorVisualIndication.MOLTEN_ASTEROID;
                bool open = cur.VisualIndication == ESectorVisualIndication.NONE || wild;
                double p = open ? 0.03 + (wild ? 0.03 : 0) + (sci ? 0.015 : 0) : 0;
                if (rng.NextDouble() < p)
                {
                    var c = RandomCiv(); string where = wild ? new[] { "su un asteroide", "nel cuore di un relitto fossile", "sotto il ghiaccio di una cometa spenta" }[rng.Next(3)] : new[] { "su una luna senza nome", "nella crosta di un planetoide", "dentro una caverna di un asteroide" }[rng.Next(3)];
                    var nd = new DigState { sector = cur.ID, civ = c.id, name = $"le rovine {Of(c)} {where}" }; M.digs.Add(nd); Saga.MarkDirty(); GalaxyView.Dirty();
                    Service.Add("indagini.fortuiti", 1);
                    Director.Notify("RITROVAMENTO", $"I sensori a lungo raggio notano geometrie troppo regolari per essere naturali: {nd.name}. Nessuno le aveva mai segnalate.");
                    StartDigEncounter(nd);
                }
            }
        }

        // ---------------------------------------------------------------- siti dei segnali
        public static SignalState Pending; public static DigState PendingDig;
        static void Site(SignalState s)
        {
            Pending = s; s.reached = true; Saga.MarkDirty(); GalaxyView.Dirty();
            var me = Stations.MyShip;
            switch (s.kind)
            {
                case "sos": Traffic.SpawnNeutral(EShipType.E_ABANDONED_SHIP_INTREPID, $"«{s.ship}»"); break;
                case "esca":
                    {
                        float pw = Director.Power(me, out _, out _); int n = s.decode >= 100 ? 1 : 2;
                        for (int i = 0; i < n; i++) Director.Spawn(me, Director.PickShip(pw, -1), Director.Modifiers(pw), i == 0 ? "Predone dell'esca" : "Complice");
                        Director.Notify("IMBOSCATA", s.decode >= 60 ? "Come previsto: l'esca era una trappola. Li aspettavamo noi, stavolta." : "Nessun naufrago: solo predoni che escono dall'ombra. Era un'esca!");
                        if (s.decode >= 60) Morale.Change(2, null); Pending = null; return;
                    }
                case "faro": M.digs.Add(new DigState { sector = s.source, civ = s.civ, name = $"l'avamposto {Of(Civ(s.civ))}" }); break;
            }
            if (!Encounters.Start("indagine_" + s.kind, true, null, "", s.id.ToString())) { Stations.LogEvent(me.ShipID, "Siamo all'origine del segnale, ma il capitano è occupato: ci torneremo."); s.reached = false; Pending = null; }
        }
        // esito delle scelte negli incontri dei siti (Encounters.cs)
        public static string Resolve(string how, Func<int, int, string> loot, Action<int> cr)
        {
            var s = Pending; Pending = null; if (s == null) return "Il segnale tace.";
            bool full = s.decode >= 100, half = s.decode >= 60; var bits = new List<string>(); var me = Stations.MyShip;
            Feats.Mystery("sito"); Service.Add("indagini.siti", 1);
            switch (s.kind)
            {
                case "sos":
                    if (how == "rapido" && !full && rng.NextDouble() < 0.35) return Trap("i sistemi di sicurezza della nave, ancora attivi, scambiano la squadra per intrusi");
                    if (full || how == "metodico") { if (rng.NextDouble() < 0.55) bits.Add(Sleeper()); else { Morale.Change(-2, null); bits.Add("i criosonni sono vuoti da anni: nessuno ce l'ha fatta"); } }
                    bits.Add(loot(2, full ? 5 : 4)); if (rng.NextDouble() < 0.5) { var g = Market.Goods[rng.Next(Market.Goods.Length)]; int n = Market.Add(g.id, 4 + rng.Next(6)); if (n > 0) bits.Add($"{n} casse di {Market.Low(g.name)}"); }
                    News.Post("flotta", $"Ritrovata la «{s.ship}», data per dispersa da anni: l'ha rintracciata la «{Ship()}» seguendo un SOS quasi spento.");
                    break;
                case "faro":
                    {
                        var c = Civ(s.civ); bits.Add(AddChapter(c, "il faro")); lastSector = -1; // fra poco si propone lo scavo dell'avamposto Research.Add(5, "faro alieno"); bits.Add("5 dati di ricerca");
                        bits.Add($"sotto il faro ci sono le rovine dell'avamposto: si possono scavare (tre strati)");
                        break;
                    }
                case "numeri":
                    if (how == "rapido" && !full && rng.NextDouble() < 0.3) return Trap("il deposito era minato: chi l'ha chiuso non voleva ospiti");
                    cr(800 + rng.Next(1200)); bits.Add(loot(3, full ? 6 : 5));
                    if (rng.NextDouble() < 0.5 && Lairs.List.OrderBy(_ => rng.Next()).FirstOrDefault() is Lair lr) bits.Add($"tra le carte, la posizione di {lr.name} di {lr.owner}, nel settore {Places.Of(lr.sector)}");
                    break;
                case "eco":
                    {
                        if (!half && how == "rapido" && rng.NextDouble() < 0.5) { try { var st = me.MyStats; st.HullCurrent = Math.Max(1f, st.HullCurrent - st.HullMax * 0.18f); } catch { } bits.Add("la curvatura dello spazio ci torce lo scafo (-18%)"); }
                        int d = (full ? 18 : half ? 12 : 8) + (how == "metodico" ? 4 : 0); Research.Add(d, "eco gravitazionale"); bits.Add($"{d} dati di ricerca");
                        if (rng.NextDouble() < 0.3) bits.Add(Exploration.Fragment("eco"));
                        break;
                    }
                case "canto":
                    {
                        var c = Civ(s.civ);
                        if (!half && rng.NextDouble() < 0.4) { Morale.Change(-3, null); bits.Add("la nostra risposta stona: il canto si interrompe, offeso, e l'equipaggio ne resta turbato"); }
                        else { Morale.Change(5, "Abbiamo cantato con le stelle"); Research.Add(8, "canto alieno"); bits.Add("rispondiamo al canto, e per un po' nessuno a bordo parla"); bits.Add(AddChapter(c, "il canto")); }
                        break;
                    }
                case "futuro":
                    bits.Add(Exploration.Fragment("trasmissione impossibile")); Threat.Add(-3f, "un avvertimento dal futuro"); Feats.Mystery("futuro");
                    Saga.Chronicle("Abbiamo ricevuto un messaggio da noi stessi, dal futuro.");
                    break;
            }
            if (s.academy) { int pay = s.reward > 0 ? s.reward : 1500; cr(pay); AcademyTrust(2); bits.Add("l'Accademia paga quanto promesso"); }
            return Cap(string.Join(", ", bits.Where(x => !string.IsNullOrEmpty(x)))) + ".";
        }
        static string Trap(string what)
        {
            var o = Saga.State.officers.Where(x => x.alive && x.cls >= 1).OrderBy(_ => rng.Next()).FirstOrDefault(); if (o != null) Officers.Wound(o, 2); Morale.Change(-2, null);
            return $"Andiamo di fretta e paghiamo: {what}." + (o != null ? $" {o.first} {o.last} finisce in infermeria." : "") + " Decifrare il segnale per intero ci avrebbe avvertito.";
        }
        static string Sleeper()
        {
            string first = Officers.RandomFirst(), last = Officers.RandomLast();
            var weak = Saga.State.officers.Where(x => x.alive && x.cls >= 1).OrderBy(x => x.rank).FirstOrDefault();
            if (weak != null && weak.rank == 0) { var n = Officers.Hire(weak.cls, first, last, "veterano", 2); return $"un superstite in criosonno, {first} {last}: ufficiale esperto, prende servizio {Officers.At(n.cls)}"; }
            Saga.State.onore += 2; Morale.Change(4, null); return $"tre superstiti in criosonno, svegliati e accolti a bordo fino al primo porto";
        }

        // ---------------------------------------------------------------- scavi
        sealed class Dig { public DigState site; public string method = ""; public float start, length, nextEvent; public float progress; public bool interrupted, rivals, shared; }
        public static string rivalName = "";
        static Dig dig;
        static void StartDigEncounter(DigState d)
        {
            if (dig != null || PendingDig != null) return;
            PendingDig = d;
            if (!Encounters.Start(d.home ? "scavo_origine" : "scavo", true, null, "", d.sector.ToString())) PendingDig = null;
            else if (d.home && d.guarded) Guardian(d);
        }
        static void Guardian(DigState d)
        {
            var me = Stations.MyShip; float p = Director.Power(me, out _, out _);
            Director.Spawn(me, p < 8 ? EShipType.E_WDDRONE2 : EShipType.E_WDDRONE3, Director.Modifiers(p + 4), "Custode " + Of(Civ(d.civ)));
            Director.Spawn(me, EShipType.E_WDDRONE2, Director.Modifiers(p), "Sentinella");
            d.guarded = false; Saga.MarkDirty();
            Director.Notify("IL SITO SI SVEGLIA", $"Qualcosa protegge ancora il mondo d'origine {Of(Civ(d.civ))}: i custodi si attivano.");
        }
        public static string BeginDig(string method)
        {
            var d = PendingDig; PendingDig = null; if (d == null) return "Non c'è niente da scavare qui.";
            if (method == "via") return "Lasciamo le rovine dove sono. Saranno qui anche domani.";
            dig = new Dig { site = d, method = method, start = Time.time, length = method == "metodico" ? 120f : 45f, nextEvent = Time.time + 25f + rng.Next(20) };
            Service.Add("indagini.scavi", 1); GalaxyView.Dirty();
            return method == "metodico" ? $"Squadra a terra con pennelli e scanner: scavo metodico dello strato {d.strata + 1} {Di(d.name)} (circa due minuti). Teniamo d'occhio i sensori." : $"Scavo rapido dello strato {d.strata + 1}: cariche leggere e trivelle (meno di un minuto). Qualcosa si romperà.";
        }
        static void DigTick(PLSectorInfo cur)
        {
            if (dig == null) return;
            if (cur.ID != dig.site.sector) { Interrupt("la partenza"); return; }
            dig.progress = Mathf.Clamp01((Time.time - dig.start) / dig.length);
            // durante lo scavo il settore non resta tranquillo: rivali, predoni attratti dal rumore, il sito che si difende
            if (Time.time > dig.nextEvent)
            {
                dig.nextEvent = Time.time + 40f + rng.Next(40);
                double r = rng.NextDouble(); var me = Stations.MyShip; float pw = Director.Power(me, out _, out _);
                if (r < (dig.method == "rapido" ? 0.22 : 0.12)) { Director.Spawn(me, Director.PickShip(pw, -1), Director.Modifiers(pw), "Predone attirato dal rumore"); Director.Notify("SCAVO", "Il rumore delle trivelle ha attirato visite: predoni in avvicinamento!"); }
                else if (r < 0.30 && !dig.rivals)
                {
                    dig.rivals = true; Traffic.Force("ricerca");
                    rivalName = new[] { "la spedizione della professoressa Vane", "gli archeologi della Gilda", "la squadra del dottor Okafor", "una spedizione dell'Università di Teth", "i cercatori di reliquie di Moreau" }[rng.Next(5)];
                    if (!Encounters.Start("spedizione_rivale", true)) { Director.Notify("SCAVO", $"Arriva {rivalName}: vogliono il sito. Il tempo stringe."); dig.length *= 0.85f; }
                }
                else if (r < 0.38) { Director.Spawn(me, EShipType.E_WDDRONE2, Director.Modifiers(pw), "Sentinella antica"); Director.Notify("SCAVO", "Il sito si difende: una sentinella antica si risveglia."); }
            }
            if (dig.progress >= 1f) Finish();
        }
        static void Interrupt(string why)
        {
            if (dig == null) return; Stations.LogEvent(Stations.MyShip.ShipID, $"Scavo interrotto per {why}: lo strato {dig.site.strata + 1} {Di(dig.site.name)} resta da finire."); dig = null; GalaxyView.Dirty();
        }
        // la spedizione rivale: cosa fare di chi arriva a contenderci il sito
        public static string Rivals(string how)
        {
            if (dig == null) return "Lo scavo è già finito: i rivali arrivano tardi e se ne vanno a mani vuote.";
            switch (how)
            {
                case "condividi": dig.shared = true; dig.length *= 0.7f; AcademyTrust(1); Saga.State.onore += 1; Saga.MarkDirty(); return $"Lavoriamo insieme a {rivalName}: lo strato si chiude prima, ma un reperto andrà a loro. L'Accademia apprezza.";
                case "paga":
                    {
                        int have = 0; try { have = PLServer.Instance.CurrentCrewCredits; } catch { }
                        if (have < 600) return "Non abbiamo 600 crediti da offrire: i rivali ridono e si mettono a scavare accanto a noi."; try { PLServer.Instance.CurrentCrewCredits = have - 600; } catch { }
                        return $"600 crediti e {rivalName} trova improvvisamente un altro sito più interessante.";
                    }
                case "intima":
                    {
                        Saga.State.infamia += 1; Saga.MarkDirty();
                        if (rng.NextDouble() < 0.55) return $"Bastano due torrette puntate: {rivalName} fa marcia indietro. Nei porti se ne parlerà, e non bene.";
                        var me = Stations.MyShip; float pw = Director.Power(me, out _, out _); Director.Spawn(me, Director.PickShip(pw, -1), Director.Modifiers(pw), "Scorta dei rivali");
                        return $"{Cap(rivalName)} non si lascia intimidire: chiama la sua scorta armata.";
                    }
                default: dig.length *= 0.85f; return "Corsa contro il tempo: trivelle al massimo. Si scava più in fretta, e si rompe di più.";
            }
        }
        static void Finish()
        {
            var d = dig.site; bool careful = dig.method == "metodico"; bool shared = dig.shared; dig = null; d.strata++; M.dug++; Saga.MarkDirty(); GalaxyView.Dirty();
            Feats.Mystery("strato"); var c = Civ(d.civ); var bits = new List<string>();
            int n = 1 + (rng.NextDouble() < (careful ? 0.5 : 0.35) ? 1 : 0) + (d.home ? 1 : 0) - (shared ? 1 : 0);
            if (n <= 0) bits.Add("l'unico reperto dello strato va alla spedizione con cui l'abbiamo condiviso");
            for (int i = 0; i < n; i++) { var rl = Relic(c, d.strata + (d.home ? 2 : 0), careful); bits.Add(rl.name + $" ({RarityName(rl.rarity)}, {rl.condition}%)"); }
            string ch = AddChapter(c, "uno scavo"); if (ch.Length > 0) bits.Add(ch);
            if (d.strata >= 3)
            {
                if (d.home && !c.homeDone)
                {
                    c.homeDone = true; var me = Stations.MyShip; var g = Loot.Grant(me, 7, 7, "mondo d'origine " + Of(c)); if (g != null) bits.Add(g.Name);
                    Trophies.Add($"Eredità {Of(c)}", "mondo d'origine"); Legacy.Record("civilta", $"ha raggiunto il mondo d'origine {Of(c)}"); Research.Add(25, "il mondo d'origine"); Feats.Mystery("origine");
                    Saga.Chronicle($"Scavato il mondo d'origine {Of(c)}."); News.Post("scienza", $"La «{Ship()}» ha raggiunto il mondo d'origine {Of(c)}, {c.epithet}. L'Accademia è in fermento.");
                }
                else if (rng.NextDouble() < 0.4) bits.Add(Exploration.Fragment("lo strato più profondo"));
                bits.Add("il sito è esaurito");
            }
            Director.Notify("SCAVO COMPLETATO", $"Strato {d.strata} {Di(d.name)}: " + string.Join(", ", bits) + ".");
        }

        // ---------------------------------------------------------------- reperti
        static readonly string[] RarityNames = { "comune", "pregiato", "raro", "prezioso", "leggendario" };
        static string RarityName(int r) => RarityNames[Math.Clamp(r, 0, 4)];
        static readonly string[] Objects = { "sigillo", "statuetta", "mappa stellare", "sfera", "corona", "chiave", "lente", "anello", "urna", "frammento di iscrizione", "strumento musicale", "busto", "calice", "medaglione", "seme", "disco inciso" };
        static readonly string[] Stories =
        {
            "Lo si trovò accanto a una figura rannicchiata, come se l'avesse stretto fino alla fine.", "Porta un'iscrizione che si ripete tre volte: forse una preghiera, forse un nome.",
            "Al buio emette un debole bagliore, sempre alla stessa ora di bordo.", "Qualcuno l'ha riparato, millenni fa, con cura evidente.",
            "È caldo al tatto, anche nel vuoto.", "Le sue incisioni descrivono una costellazione che oggi non esiste più.",
            "Sembra un oggetto di tutti i giorni. È questo che lo rende commovente.", "Era sepolto con un ordine preciso, come in un archivio.",
        };
        static readonly string[] Curses = { "sussurri", "guasti", "malinconia" };
        static RelicState Relic(CivState c, int depth, bool careful)
        {
            int rar = Math.Clamp((int)Math.Floor(rng.NextDouble() * 2.2 + depth * 0.7 + (rng.NextDouble() < 0.06 ? 2 : 0)), 0, 4);
            int cond = Math.Clamp((careful ? 65 : 35) + rng.Next(35), 10, 100);
            string obj = Objects[rng.Next(Objects.Length)]; string name = $"{char.ToUpper(obj[0]) + obj[1..]} di {c.material} {Of(c)}";
            int value = (int)((150 + rar * rar * 260 + rng.Next(120)) * (0.4 + cond / 100.0));
            var rl = new RelicState { id = M.nextId++, name = name, civ = c.id, rarity = rar, condition = cond, value = value, story = Stories[rng.Next(Stories.Length)] };
            if (rng.NextDouble() < 0.04 + rar * 0.02) rl.curse = Curses[rng.Next(Curses.Length)];
            M.relics.Add(rl); M.found++; Service.Add("indagini.reperti", 1); Feats.Mystery("reperto"); Saga.MarkDirty();
            return rl;
        }
        // i reperti «inquieti» si fanno sentire a ogni salto finché restano a bordo
        static int curseJump = -1;
        static void CurseTick()
        {
            int j = Obscured.Jumps(); if (j == curseJump) return; curseJump = j;
            var cursed = M.relics.Where(r => r.curse.Length > 0).ToList(); if (cursed.Count == 0 || rng.NextDouble() > 0.5) return;
            var r = cursed[rng.Next(cursed.Count)]; var me = Stations.MyShip;
            switch (r.curse)
            {
                case "sussurri": Morale.Change(-2, null); Stations.LogEvent(me.ShipID, $"Di notte, in stiva, qualcuno giura di sentire sussurrare il {Low(r.name)}."); break;
                case "guasti": { var sys = Stations.SysIds[rng.Next(Stations.SysIds.Length)]; Stations.Set(me.ShipID, "dmx_" + sys, Math.Min(90f, Stations.Get(me.ShipID, "dmx_" + sys) + 12f)); Stations.LogEvent(me.ShipID, $"Un guasto inspiegabile, proprio accanto al {Low(r.name)}."); break; }
                default: Morale.Change(-1, null); Stations.LogEvent(me.ShipID, $"Chi passa accanto al {Low(r.name)} diventa malinconico per ore."); break;
            }
        }
        static bool InPort() { try { return Market.CanTrade(); } catch { return false; } } // mai dalla vista della galassia: la si sta costruendo
        static bool Shady() { try { var s = Cur(); return s != null && (s.MySPI.Faction == 4 || Wanted.Level > 0 || Saga.State.infamia > Saga.State.onore); } catch { return false; } }
        public static string Act(string op, int id)
        {
            var r = M.relics.FirstOrDefault(x => x.id == id); if (r == null) return "reperto non trovato";
            var c = Civ(r.civ);
            switch (op)
            {
                case "studia":
                    {
                        if (r.studied) return "già studiato a fondo";
                        r.studied = true; M.studied++; int d = 3 + r.rarity * 2; Research.Add(d, "studio di un reperto"); string ch = rng.NextDouble() < 0.35 + r.rarity * 0.1 ? AddChapter(c, "lo studio di un reperto") : "";
                        string lift = ""; if (r.curse.Length > 0 && rng.NextDouble() < 0.5) { r.curse = ""; lift = " Lo studio ne spiega le stranezze: non dà più problemi."; }
                        r.condition = Math.Max(5, r.condition - 5); Saga.MarkDirty(); GalaxyView.Dirty();
                        return $"Studiamo il {Low(r.name)}: {d} dati di ricerca" + (ch.Length > 0 ? ", " + ch : "") + "." + lift;
                    }
                case "dona":
                    if (!InPort()) return "le donazioni si consegnano in porto, al delegato dell'Accademia";
                    M.relics.Remove(r); M.donated++; Saga.State.onore += 1 + r.rarity; AcademyTrust(1 + r.rarity / 2); Competitors.Renown(4 + r.rarity * 3); Research.Add(2 + r.rarity, "gratitudine dell'Accademia");
                    Service.Add("indagini.donati", 1); Feats.Mystery("donato"); Saga.MarkDirty(); GalaxyView.Dirty();
                    News.Post("scienza", $"Il {Low(r.name)} è esposto nel museo dell'Accademia, dono della «{Ship()}».");
                    return $"Donato all'Accademia: onore +{1 + r.rarity}, fiducia dell'Accademia in crescita, e il nome della nave sulla targhetta.";
                case "vendi":
                    {
                        if (!InPort()) return "per vendere serve un porto con un museo, o un ricettatore";
                        bool black = Shady(); int pay = (int)(r.value * (black ? 1.6 : 1.0));
                        try { PLServer.Instance.CurrentCrewCredits += pay; } catch { }
                        M.relics.Remove(r); M.sold++; Service.Add("indagini.venduti", 1); if (black) { Saga.State.infamia += 1 + r.rarity / 2; AcademyTrust(-1); Feats.Mystery("nero"); }
                        Saga.MarkDirty(); GalaxyView.Dirty();
                        return black ? $"Un ricettatore paga {pay} crediti senza fare domande. L'Accademia, se lo saprà, non gradirà." : $"Il museo del porto paga {pay} crediti.";
                    }
                case "getta":
                    M.relics.Remove(r); Saga.MarkDirty(); GalaxyView.Dirty(); if (r.curse.Length > 0) Morale.Change(2, null);
                    return r.curse.Length > 0 ? $"Il {Low(r.name)} se ne va nel vuoto. A bordo, qualcuno respira meglio." : $"Il {Low(r.name)} se ne va nel vuoto. Un pezzo di storia in meno.";
            }
            return "";
        }

        // ---------------------------------------------------------------- pannello (scheda Indagini della Scienza)
        public class SigRow { public int id, decode, bearings, zone, fade, source = -1; public string name = "", hint = ""; public bool fix; }
        public class RelicRow { public int id, rarity, condition, value; public string name = "", story = "", curse = ""; public bool studied; }
        public class Panel
        {
            public List<SigRow> signals = new(); public List<RelicRow> relics = new(); public List<string> civs = new(); public List<string> digs = new(); public List<int> digSectors = new();
            public string dig = ""; public float digProgress; public int data; public bool port, shady; public string stats = "";
        }
        public static Panel Snapshot()
        {
            var p = new Panel(); int j = Obscured.Jumps();
            foreach (var s in M.signals.Where(x => !x.reached).Take(4))
                p.signals.Add(new SigRow { id = s.id, name = Known(s), decode = s.decode, bearings = s.bearings.Count, zone = s.fix ? 1 : Candidates(s).Count, fix = s.fix, fade = Math.Max(0, s.fade - j), source = s.fix ? s.source : -1, hint = s.decode >= 60 ? Hint(s) : "" });
            foreach (var r in M.relics.OrderByDescending(x => x.rarity).ThenBy(x => x.id).Take(30)) p.relics.Add(new RelicRow { id = r.id, name = r.name, rarity = r.rarity, condition = r.condition, value = r.value, story = r.story, curse = r.curse, studied = r.studied });
            foreach (var c in M.civs.Where(x => x.chapters.Count > 0)) p.civs.Add($"{c.name} {c.chapters.Count}/6");
            foreach (var d in M.digs.Where(x => x.strata < 3).Take(3)) { p.digs.Add($"{Cap(d.name)}, settore {d.sector}, strato {d.strata + 1}/3"); p.digSectors.Add(d.sector); }
            if (dig != null) { p.dig = $"Scavo {dig.method} in corso: {dig.site.name}, strato {dig.site.strata + 1}"; p.digProgress = dig.progress; }
            try { p.data = Saga.State.research?.data ?? 0; } catch { }
            p.port = InPort(); p.shady = Shady();
            p.stats = $"segnali {M.heard} · decifrati {M.decoded} · localizzati {M.located} · strati {M.dug} · reperti {M.found} · civiltà ricostruite {M.rebuilt}";
            return p;
        }
        public static void Request(string op, string arg)
        {
            if (Host) { var t = Do(op, arg); if (t.Length > 0) Stations.Announce("INDAGINI", t); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|indagini|{op}|{arg}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var p = msg.Split('|'); if (p.Length >= 4 && Host) Request(p[2], p[3]); }
        static string Do(string op, string arg)
        {
            int.TryParse(arg, out int id); var s = M.signals.FirstOrDefault(x => x.id == id);
            switch (op)
            {
                case "decifra":
                    {
                        if (s == null) return ""; var r = Saga.State.research; if (r == null || r.data < 4) return "servono 4 dati di ricerca per un passaggio di decifrazione";
                        if (s.decode >= 100) return "già decifrato"; r.data -= 4; Decode(s, 20 + Ears(out _) / 10, "dati"); return $"Decifrazione: {s.decode}%.";
                    }
                case "sonda":
                    {
                        if (s == null || s.fix) return ""; int cost = Probes.Cost; int have = 0; try { have = PLServer.Instance.CurrentCrewCredits; } catch { }
                        if (have < cost) return $"una sonda costa {cost} crediti"; try { PLServer.Instance.CurrentCrewCredits = have - cost; } catch { }
                        if (s.bearings.Count == 0 || rng.NextDouble() < 0.25) { var cur = Cur(); if (cur != null) Listen(s, cur, true); return "La sonda torna con un rilevamento in più, ma non basta ancora."; }
                        Locate(s, "sonda"); return "";
                    }
                case "rotta":
                    {
                        if (s == null) return "";
                        { var here = Cur(); Vector3 hp = here != null ? here.Position : Vector3.zero; // la distanza vera costa (percorso del gioco): prima si sfoltisce a occhio
                          int to = s.fix ? s.source : Candidates(s).Where(x => here == null || x != here.ID).OrderBy(x => { var sc = PLServer.GetSectorWithID(x); return sc == null ? 1e9f : (sc.Position - hp).sqrMagnitude; }).Take(5).OrderBy(x => Nemesis.JumpsTo(x)).FirstOrDefault(-1); if (to >= 0) { Course.Plot(to); return s.fix ? $"Rotta verso l'origine del segnale, settore {Places.Of(to)}." : $"Rotta verso la zona più vicina compatibile con i rilevamenti, settore {Places.Of(to)}: lì misureremo di nuovo."; } return ""; }
                    }
                case "scavo": { var d = M.digs.FirstOrDefault(x => x.sector == id && x.strata < 3); if (d == null) return ""; Course.Plot(d.sector); return $"Rotta verso {d.name}, settore {Places.Of(d.sector)}."; }
                case "studia": case "dona": case "vendi": case "getta": return Act(op, id);
            }
            return "";
        }

        // ---------------------------------------------------------------- incontri con l'Accademia e banca dati
        public static string AcademyOffer() { var s = Hear(null, true); if (s == null) return ""; s.reward = 1200 + rng.Next(1300); Saga.MarkDirty(); return $"compenso {s.reward} crediti"; }
        public static IEnumerable<CodexEntry> Codex()
        {
            foreach (var c in M.civs.Where(x => x.chapters.Count > 0))
                yield return new CodexEntry { cat = "Luoghi", title = $"{c.name}, {c.epithet}", text = string.Join(" ", c.chapters.OrderBy(x => x).Select(i => Chapter(c, i))) + (c.chapters.Count < 6 ? $" (Capitoli ricostruiti: {c.chapters.Count}/6.)" : c.homeDone ? " Abbiamo raggiunto il loro mondo d'origine." : c.homeKnown ? $" Il loro mondo d'origine è nel settore {c.home}." : "") };
            foreach (var r in M.relics.Where(x => x.rarity >= 2).Take(10))
                yield return new CodexEntry { cat = "Reliquie", title = r.name, text = $"{Cap(RarityName(r.rarity))}, conservato al {r.condition}%. {r.story}" + (r.curse.Length > 0 ? " Si dice porti con sé qualcosa di inquieto." : "") };
        }
        public static void FromExploration(string kind, string name, string sector)
        {
            // le rovine e i fari dell'Atlante appartengono anch'essi a qualche civiltà perduta; le rovine si possono scavare
            if (kind is not ("rovine" or "faro")) return;
            var c = RandomCiv(); string ch = AddChapter(c, "l'Atlante");
            if (ch.Length > 0) Stations.LogEvent(Stations.MyShip.ShipID, $"Scienza: {name} porta il segno {Of(c)}: {ch}.");
            if (kind == "rovine" && int.TryParse(sector, out int sec) && !M.digs.Any(x => x.sector == sec))
            {
                M.digs.Add(new DigState { sector = sec, civ = c.id, name = name }); Saga.MarkDirty(); GalaxyView.Dirty(); lastSector = -1;
                Stations.LogEvent(Stations.MyShip.ShipID, $"Scienza: sotto {name} ci sono altri strati. Si può scavare.");
            }
        }

        static void AcademyTrust(int d) { var t = Saga.State.trust; const string k = "Accademia delle Scienze"; t[k] = Math.Clamp((t.TryGetValue(k, out var v) ? v : 0) + d, -10, 10); Saga.MarkDirty(); }
        static string Ship() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }
        // «di» + articolo: «delle rovine», «del faro», «dell'avamposto», «di un sito»
        public static string Di(string n) => n.StartsWith("le ") ? "delle " + n[3..] : n.StartsWith("la ") ? "della " + n[3..] : n.StartsWith("il ") ? "del " + n[3..] : n.StartsWith("i ") ? "dei " + n[2..] : n.StartsWith("gli ") ? "degli " + n[4..] : n.StartsWith("lo ") ? "dello " + n[3..] : n.StartsWith("l'") ? "dell'" + n[2..] : "di " + n;
        public static string Of(SignalState s) => s.civ.Length > 0 ? Of(Civ(s.civ)) : "senza nome";
        static string Low(string s) => string.IsNullOrEmpty(s) ? s : char.ToLower(s[0]) + s[1..]; // solo l'iniziale: i nomi propri restano maiuscoli
        public static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

        // ---------------------------------------------------------------- prove
        public static string Describe()
        {
            EnsureCivs(); var cur = Cur();
            var sb = new System.Text.StringBuilder($"indagini: {Snapshot().stats}");
            foreach (var c in M.civs) sb.Append($"\n      civiltà {c.name} ({c.epithet}, {c.nature}, {c.material}): capitoli {string.Join(",", c.chapters)}{(c.homeKnown ? ", origine " + c.home : "")}");
            foreach (var s in M.signals) sb.Append($"\n      segnale {s.id} {s.kind} → origine {s.source} ({Nemesis.JumpsTo(s.source)} salti): decifrato {s.decode}%, rilevamenti {s.bearings.Count}, candidati {Candidates(s).Count}, {(s.fix ? "localizzato" : "da localizzare")}, si spegne al salto {s.fade}{(s.reached ? ", raggiunto" : "")}");
            foreach (var d in M.digs) sb.Append($"\n      scavo {d.name} settore {d.sector}: strati {d.strata}/3{(d.home ? " (origine)" : "")}");
            foreach (var r in M.relics) sb.Append($"\n      reperto {r.id} {r.name}: {RarityName(r.rarity)}, {r.condition}%, {r.value} cr{(r.curse.Length > 0 ? ", " + r.curse : "")}");
            if (dig != null) sb.Append($"\n      scavo in corso: {dig.site.name} {dig.progress:P0}");
            return sb.ToString();
        }
        public static string Force(string what, string arg)
        {
            switch (what)
            {
                case "segnale": return Hear(arg.Length > 0 ? arg : null) is SignalState s ? $"segnale {s.id} {s.kind} → {s.source}" : "nessun settore adatto";
                case "decifra": foreach (var x in M.signals) Decode(x, 100, "prova"); return Describe();
                case "qui": { var q = M.signals.LastOrDefault(x => !x.reached); var cur = Cur(); if (q == null || cur == null) return "nessun segnale"; q.source = cur.ID; q.fix = true; lastSector = -1; return "origine spostata qui: arrivo simulato fra pochi secondi"; }
                case "scavo": { var cur = Cur(); if (cur == null) return ""; var c = RandomCiv(); M.digs.Add(new DigState { sector = cur.ID, civ = c.id, name = $"le rovine {Of(c)}" }); lastSector = -1; return "scavo aggiunto qui"; }
                case "fine": if (dig != null) dig.start -= 9999f; return "scavo concluso al prossimo controllo";
                case "reperto": { var r = Relic(RandomCiv(), 2, true); return r.name; }
                case "capitoli": { var c = RandomCiv(); while (c.chapters.Count < 6) AddChapter(c, "prova"); return Describe(); }
            }
            return Describe();
        }
    }
}

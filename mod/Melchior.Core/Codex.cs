// Banca dati di bordo: un'enciclopedia che si riempie giocando. Fazioni (con una loro storia), personaggi ricorrenti
// incontrati (con una biografia scritta dalle loro imprese e dagli incontri con noi), luoghi esplorati e leggendari,
// reliquie leggendarie ottenute (con la loro storia), fenomeni stellari attraversati, equipaggi concorrenti conosciuti,
// la grande minaccia. Si sfoglia dalla scheda Banca dati della Scienza. Le voci si costruiscono dallo stato della
// galassia sull'host e viaggiano con GalaxyView.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class CodexEntry { public string cat = "", title = "", text = ""; }

    public static class Codex
    {
        public static readonly string[] Cats = { "Fazioni", "Specie", "Personaggi", "Luoghi", "Reliquie", "Fenomeni", "Equipaggi", "Minacce" };
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static readonly Dictionary<int, string> FactionLore = new()
        {
            [0] = "Nata dal patto fra le prime colonie di frontiera, la Colonial Union è la potenza più antica e più burocratica della galassia: flotte di linea, registri in triplice copia e un giuramento che fa della difesa delle colonie un dovere sacro. Lenta a decidere, ostinata a non arretrare.",
            [1] = "L'Alliance of Gentlemen è un'aristocrazia di armatori, mercanti e corsari autorizzati, tenuta insieme da un codice d'onore scritto in nessun luogo e rispettato ovunque. Si ricevono gli ospiti in alta uniforme e si regolano i torti a duello; i contratti, però, si leggono fino all'ultima riga.",
            [2] = "La W.D. Corporation è un'impresa diventata stato: cantieri, droni da combattimento, laboratori senza finestre. Tutto ha un prezzo, compresa la lealtà, e il Consiglio non dimentica mai un investimento andato male. Le sue flotte automatiche non dormono.",
            [4] = "Nessuno sa da dove sia arrivata l'infezione. Si attacca agli scafi e agli equipaggi, li spegne e poi li rimette in moto, tutti con la stessa volontà. Gli Infetti non trattano, non si arrendono e non dimenticano un settore che hanno già toccato.",
            [5] = "La Polytechnic Federation è nata attorno alle accademie e ai laboratori della frontiera interna: scienziati, ingegneri, sognatori con le maniche rimboccate. Preferisce i trattati alle guerre e i dati alle opinioni, ma sa difendere i suoi esperimenti meglio di quanto si creda.",
        };
        static readonly Dictionary<string, string> PhenomenaLore = new()
        {
            ["nebulosa"] = "Nubi di gas ionizzato che accecano i sensori di chiunque: un nascondiglio per chi fugge, una trappola per chi caccia.",
            ["asteroidi"] = "Frammenti di pianeti mai nati, ricchi di minerali e di insidie. I piloti esperti li attraversano; gli altri li evitano.",
            ["radiazioni"] = "Stelle instabili che inondano i loro sistemi di radiazioni. Gli scudi proteggono, finché reggono.",
            ["plasma"] = "Tempeste di plasma che si muovono come maree: scaricano gli scudi e mettono alla prova i nuclei più robusti.",
            ["gravita"] = "Pozzi gravitazionali che piegano rotte e motori. Si attraversano con pazienza, o con motori migliori.",
            ["corrente"] = "Correnti subspaziali che avvolgono le navi come un fiume: chi le conosce ne esce più in forma di come c'è entrato.",
            ["quiete"] = "Settori in cui non succede niente, letteralmente. Gli equipaggi li cercano per dormire una notte intera.",
            ["supernova"] = "La morte di una stella: un lampo che si vede da mezza galassia e un settore che resta per sempre un campo di radiazioni, con i resti della stella da esplorare.",
            ["cometa"] = "Comete erranti che attraversano la galassia una volta ogni qualche secolo, con code piene di materia esotica che non si trova da nessun'altra parte.",
            ["allineamento"] = "Rari allineamenti subspaziali durante i quali i motori si rigenerano da soli e perfino il nucleo sembra respirare meglio.",
            ["spaziomorto"] = "Ondate di spazio morto che accecano i sensori di intere regioni. I predoni le aspettano con impazienza.",
        };

        static List<CodexEntry> cache = new(); static float builtAt = -100f;
        public static List<CodexEntry> Entries => Host ? Build() : GalaxyView.Current?.codex ?? new List<CodexEntry>();

        static List<CodexEntry> Build()
        {
            if (UnityEngine.Time.time - builtAt < 3f) return cache;
            builtAt = UnityEngine.Time.time; var l = new List<CodexEntry>(); var st = Saga.State;
            try
            {
                foreach (var f in GalaxyWar.Factions)
                    l.Add(new CodexEntry { cat = "Fazioni", title = GalaxyWar.Name(f), text = (Leaders.Of(f) is Leader ld ? $"Guida: {ld.name}, {Leaders.Title(f).ToLowerInvariant()}, {Leaders.TemperName(ld.temper)}; ci stima {(ld.opinion >= 40 ? "molto" : ld.opinion >= 10 ? "abbastanza" : ld.opinion <= -20 ? "poco" : "con riserbo")}. " : "") + (FactionLore.TryGetValue(f, out var t) ? t : "") + (f != 4 ? $" Rapporti con noi: {Factions.LevelName(Factions.Level(f)).ToLowerInvariant()}." : "") + (f != 4 && Campaigns.Honored(f) ? $" Ci ha conferito la {Campaigns.Stories[f].honor}." : "") });
                foreach (var d in Species.Defs) { var s = Species.Of(d.id); if (!s.contact) continue; l.Add(new CodexEntry { cat = "Specie", title = Species.Cap(d.name), text = d.lore + $" Comprensione: {s.understanding}%. Atteggiamento verso di noi: {(s.attitude >= 40 ? "amichevole" : s.attitude >= 0 ? "curioso" : s.attitude > -30 ? "diffidente" : "ostile")}." + (s.accord ? $" Abbiamo un accordo: {d.perk}." : "") }); }
                foreach (var g in st.figures.Where(x => x.met > 0 || !x.alive || x.memory == "catturato").TakeLast(20))
                {
                    string status = g.alive ? "in libertà" : g.memory == "catturato" ? "in prigionia" : "fuori dai giochi";
                    string mem = g.memory switch { "vinto" => $"L'ultima volta ci ha messi in fuga, nel settore {Places.Of(g.memSector)}.", "sfregiato" => $"Abbiamo abbattuto la sua ammiraglia nel settore {Places.Of(g.memSector)}, ma una capsula l'ha portata in salvo.", "erede" => $"Ha raccolto l'eredità di {g.heir}.", "debito" => "Ci deve la vita.", "abbattuto" => $"La sua carriera è finita nel settore {Places.Of(g.memSector)}.", _ => "" };
                    l.Add(new CodexEntry { cat = "Personaggi", title = g.Full, text = $"{char.ToUpper(Nemesis.RoleLabel(g)[0]) + Nemesis.RoleLabel(g)[1..]}, grado {g.rank} su 5. Tratti: {Nemesis.TraitText(g)}. Imprese note nei porti: {g.deeds}. Incontri con noi: {g.met}; cicatrici: {g.scars}. {mem} Stato: {status}." });
                }
                foreach (var c in st.captains.Where(c => c.met > 0 && (c.level >= 4 || c.met >= 2 || !c.alive)).OrderByDescending(c => c.level).Take(15))
                    l.Add(new CodexEntry { cat = "Personaggi", title = $"{(c.alive ? "" : c.retired ? "(ritirato) " : "† ")}cap. {c.name}", text = $"Capitano della «{c.ship}», {c.job}, stile {c.style}, livello {c.level}. Incontrato {c.met} volte." + (c.life.Count > 0 ? " Ultime notizie: " + string.Join("; ", c.life.TakeLast(3).Select(x => x.Split(": ").Last())) + "." : "") });
                foreach (var d in st.discoveries.Where(x => x.kind != "leggenda").TakeLast(20))
                    l.Add(new CodexEntry { cat = "Luoghi", title = char.ToUpper(d.name[0]) + d.name[1..], text = $"{char.ToUpper(Exploration.KindLabel(d.kind)[0]) + Exploration.KindLabel(d.kind)[1..]} nel settore {Places.Of(d.sector)}, esplorato dalla nostra nave e registrato nell'Atlante." });
                foreach (var d in st.discoveries.Where(x => x.kind == "leggenda"))
                    l.Add(new CodexEntry { cat = "Luoghi", title = char.ToUpper(d.name[0]) + d.name[1..], text = $"Luogo leggendario nel settore {Places.Of(d.sector)}. Lo abbiamo raggiunto seguendo tre frammenti di mappa e abbiamo abbattuto il suo guardiano." });
                foreach (int i in st.relics.Distinct())
                    if (i >= 0 && i < Rarity.Cfg.Legendaries.Length) { var lg = Rarity.Cfg.Legendaries[i]; l.Add(new CodexEntry { cat = "Reliquie", title = lg.Name, text = lg.Lore + (lg.Set.Length > 0 ? $" Fa parte del set «{lg.Set}»." : "") }); }
                foreach (var k in st.phenomena.Distinct())
                    if (PhenomenaLore.TryGetValue(k, out var t)) l.Add(new CodexEntry { cat = "Fenomeni", title = char.ToUpper(Kind(k)[0]) + Kind(k)[1..], text = t });
                foreach (var e in Legacy.Others.TakeLast(10)) l.Add(new CodexEntry { cat = "Equipaggi", title = $"«{e.ship}» (leggenda)", text = $"Un equipaggio di un'altra epoca, la cui fama è arrivata fin qui: la nave che {e.deed}." });
                foreach (var c in st.competitors.Where(x => x.met))
                    l.Add(new CodexEntry { cat = "Equipaggi", title = $"«{c.ship}»", text = $"Libero equipaggio guidato da {c.captain}: {Competitors.Styles[c.style]}. Fama {c.score}, imprese {c.deeds}. Rapporti con noi: {(c.relation >= 30 ? "amicizia" : c.relation <= -30 ? "rancore" : "cordiali")}." });
                foreach (var b in st.bases) { var k = Bases.K(b.kind); if (k != null) l.Add(new CodexEntry { cat = "Luoghi", title = b.name, text = $"{k.name}, la nostra base nel settore {Places.Of(b.sector)}: {k.desc.ToLowerInvariant()}. " + (b.state == 0 ? "Ancora in costruzione." : $"Livello {b.level}, difese {b.defense}. Ha reso finora {b.earned} crediti.") }); }
                foreach (var lr in st.lairs) l.Add(new CodexEntry { cat = "Luoghi", title = Lairs.Cap(lr.name), text = $"Covo di {lr.owner} nel settore {Places.Of(lr.sector)}. Difese: {lr.strength} su 5. I convogli della zona cambiano rotta." });
                var th = st.threat;
                if (th != null && th.name.Length > 0 && (th.stage >= 1 || th.defeated > 0 || th.catastrophes > 0))
                    l.Add(new CodexEntry { cat = "Minacce", title = char.ToUpper(th.name[0]) + th.name[1..], text = $"Una minaccia senza volto che cresce ai margini della galassia, spegnendo i settori uno dopo l'altro e spingendo gli Infetti oltre ogni fronte. Fase {th.stage} di 4, livello {th.level:0}%." + (th.heart >= 0 ? $" Il suo cuore è nel settore {Places.Of(th.heart)}." : "") + $" Minacce sconfitte finora: {th.defeated}; catastrofi: {th.catastrophes}." });
                l.AddRange(Mysteries.Codex());
            }
            catch (Exception e) { Plugin.L.LogWarning("Banca dati: " + e.Message); }
            cache = l; return l;
        }
        static string Kind(string k) => k switch { "supernova" => "supernova", "cometa" => "cometa errante", "allineamento" => "allineamento subspaziale", "spaziomorto" => "ondata di spazio morto", "nebulosa" => "nebulosa ionica", "asteroidi" => "campo di asteroidi", "radiazioni" => "radiazioni stellari", "plasma" => "tempesta di plasma", "gravita" => "pozzo gravitazionale", "corrente" => "corrente subspaziale", _ => "quiete assoluta" };

        public static void Phenomenon(string kind) { if (!Host || !Saga.Loaded || Saga.State.phenomena.Contains(kind)) return; Saga.State.phenomena.Add(kind); Saga.MarkDirty(); GalaxyView.Dirty(); Director.Notify("BANCA DATI", $"Nuova voce: {Kind(kind)}."); }

        // Reliquie: i pezzi leggendari passati per la nostra nave.
        static float nextScan;
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null || UnityEngine.Time.time < nextScan) return;
            nextScan = UnityEngine.Time.time + 10f;
            try
            {
                var all = Stations.MyShip.MyStats?.m_AllComponents; if (all == null) return;
                for (int i = 0; i < all.Count; i++)
                {
                    var c = all[i]; if (c == null) continue; var tag = Rarity.Get(c.Pointer);
                    if (tag != null && tag.Legend >= 0 && !Saga.State.relics.Contains(tag.Legend))
                    { Saga.State.relics.Add(tag.Legend); Saga.MarkDirty(); GalaxyView.Dirty(); Director.Notify("BANCA DATI", $"Nuova voce fra le reliquie: {Rarity.Cfg.Legendaries[tag.Legend].Name}."); }
                }
            }
            catch { }
        }

        public static string Describe() => string.Join(", ", Cats.Select(c => $"{c} {Entries.Count(e => e.cat == c)}"));
    }
}

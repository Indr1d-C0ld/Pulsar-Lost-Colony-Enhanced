// Specie aliene e primo contatto. Quattro specie sconosciute vivono ai margini della galassia. Le si incontra con un
// primo contatto (incontro), poi si impara a capirle (comprensione 0-100, soprattutto con la Scienza) e si costruisce
// un rapporto (atteggiamento -100..100) con visite, doni e incomprensioni. Con comprensione piena e un atteggiamento
// buono si firma un accordo: trofeo, fama e un vantaggio unico per ciascuna specie. Le specie compaiono nella Banca dati.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class SpeciesState { public bool contact, accord; public int understanding, attitude, visits; }

    public static class Species
    {
        public sealed class Def { public string id = "", name = "", look = "", lore = "", greet = "", gift = "", perk = ""; }
        public static readonly Def[] Defs =
        {
            new() { id = "velnari", name = "i Vel'nari", look = "Una nave che sembra fatta di luce liquida si ferma a distanza di cortesia. Sugli schermi non compare nessun volto: solo una luminosità che pulsa al ritmo del nostro respiro.", lore = "Esseri di luce coerente che comunicano per emozioni più che per parole. Percepiscono lo stato d'animo di un equipaggio prima ancora di parlarci, e non sopportano la menzogna.", greet = "Una sensazione di calma si diffonde in plancia, come se qualcuno avesse aperto una finestra.", gift = "una gemma che si scalda quando qualcuno a bordo è triste", perk = "i loro consiglieri aiutano il morale a reggere nei momenti duri" },
            new() { id = "kethri", name = "i Kethri", look = "Una nave a forma di alveare, coperta di bandiere commerciali di cento porti diversi. Il loro portavoce, un essere dagli occhi composti, tiene già in mano un elenco di prezzi.", lore = "Mercanti insettoidi per cui ogni scambio è un rito: un dono va sempre ricambiato, un debito sempre saldato, un inganno mai perdonato. Hanno rotte commerciali che nessuna fazione conosce.", greet = "Il portavoce fa schioccare le mandibole: è, ci assicurano, un sorriso.", gift = "un registro di rotte commerciali segrete", perk = "il cantiere navale e il mercato nero ci fanno prezzi da soci in affari" },
            new() { id = "ossidiani", name = "gli Ossidiani", look = "Un asteroide che non è un asteroide: una nave di cristallo nero, antica come le stelle vicine, che si muove con una lentezza sovrana.", lore = "Esseri di cristallo vivo che pensano in secoli. Una loro frase può durare un giorno intero, una loro decisione una generazione. Custodiscono una memoria della galassia più antica di qualunque fazione.", greet = "Dopo un'ora di silenzio arriva una sola parola, che la Scienza traduce con «benvenuti».", gift = "una lastra di cristallo che si salda da sola", perk = "le loro piastre di cristallo rinforzano lo scafo" },
            new() { id = "coro", name = "il Coro di Tessa", look = "Una costellazione di piccole navi che si muovono all'unisono, trasmettendo un canto a più voci su tutte le frequenze.", lore = "Una mente collettiva fatta di canto: ogni nave è una voce, ogni voce è il Coro. Sentono le minacce nello spazio come un'armonia stonata, molto prima che compaiano sui sensori.", greet = "Il canto cambia tonalità e per un momento la plancia vibra insieme a loro.", gift = "un frammento di partitura che i sensori sanno leggere", perk = "il loro canto ci avverte delle imboscate prima che scattino" },
        };
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static Dictionary<string, SpeciesState> S => Saga.State.species ??= new Dictionary<string, SpeciesState>();
        public static SpeciesState Of(string id) { if (!S.TryGetValue(id, out var s)) { s = new SpeciesState(); S[id] = s; } return s; }
        public static Def Get(string id) => Defs.FirstOrDefault(d => d.id == id);
        public static bool Accord(string id) => Host ? Saga.Loaded && S.TryGetValue(id, out var s) && s.accord : (GalaxyView.Current?.accords?.Contains(id) ?? false);
        public static string Cap(string s) => char.ToUpper(s[0]) + s[1..];

        public static Def Unmet() { var l = Defs.Where(d => !Of(d.id).contact).ToList(); return l.Count == 0 ? null : l[new System.Random().Next(l.Count)]; }
        public static Def Met(Func<SpeciesState, bool> f) { var l = Defs.Where(d => Of(d.id).contact && f(Of(d.id))).ToList(); return l.Count == 0 ? null : l[new System.Random().Next(l.Count)]; }

        public static void Change(string id, int understanding, int attitude)
        {
            if (understanding > 0) understanding = (int)Math.Round(understanding * Talents.Mul("xenologo", 0.2f));
            if (understanding > 0) Research.Add(Math.Max(1, understanding / 5), "studio di una specie aliena");
            var s = Of(id); s.contact = true; s.understanding = Math.Clamp(s.understanding + understanding, 0, 100); s.attitude = Math.Clamp(s.attitude + attitude, -100, 100);
            Saga.MarkDirty(); GalaxyView.Dirty();
            if (!s.accord && s.understanding >= 100 && s.attitude >= 40)
            {
                s.accord = true; var d = Get(id);
                Director.Notify("ACCORDO", $"Comprendiamo {d.name} e loro comprendono noi: firmiamo un accordo, il primo nella storia della galassia. Da oggi {d.perk}.");
                News.Post("scienza", $"Storico: la «{(string)Stations.MyShip.ShipName}» firma un accordo con {d.name}. L'Accademia parla di una nuova era.");
                Trophies.Add($"Accordo con {d.name}", "primo contatto"); Legacy.Record("accordo", $"ha firmato il primo accordo con {d.name}"); Competitors.Renown(80); Morale.Change(8, null); Saga.State.onore += 3;
                Saga.Chronicle($"Accordo con {d.name}.");
            }
        }

        public static string Describe() => string.Join("; ", Defs.Select(d => { var s = Of(d.id); return $"{d.id}: {(s.contact ? $"comprensione {s.understanding}, atteggiamento {s.attitude}{(s.accord ? ", accordo" : "")}" : "mai incontrati")}"; }));
    }
}

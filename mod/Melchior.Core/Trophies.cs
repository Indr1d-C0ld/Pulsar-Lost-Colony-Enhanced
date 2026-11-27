// Sala dei trofei: ogni impresa importante lascia un trofeo con nome, origine e data (insegne dei nemici abbattuti,
// reliquie dei luoghi leggendari, encomi delle crisi risolte, ricordi delle storie concluse, il frammento del cuore della
// grande minaccia). Restano nella cronaca della galassia; il conteggio compare nella scheda Galassia, l'elenco su Balthasar.
using System;
using System.Linq;

namespace Melchior
{
    public class Trophy { public long t; public string name = "", source = "", stardate = ""; }

    public static class Trophies
    {
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }

        public static void Add(string name, string source)
        {
            if (!Host || !Saga.Loaded) return;
            var l = Saga.State.trophies; if (l.Any(x => x.name == name)) return;
            l.Add(new Trophy { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), name = name, source = source, stardate = Logbook.Stardate() });
            Saga.MarkDirty(); GalaxyView.Dirty(); Morale.Change(1, null);
            Director.Notify("SALA DEI TROFEI", $"Nuovo trofeo: {name}." + (l.Count == 1 ? " È il primo della nostra sala." : $" Sono {l.Count} in tutto."));
            Service.Add("trofei", 1);
        }
        public static int Count => Host ? Saga.State.trophies.Count : GalaxyView.Current?.trophies ?? 0;
        public static string Describe() => Saga.State.trophies.Count == 0 ? "sala dei trofei vuota" : string.Join("\n      ", Saga.State.trophies.Select(x => $"{x.stardate}  {x.name} ({x.source})").Prepend($"trofei: {Saga.State.trophies.Count}"));
    }
}

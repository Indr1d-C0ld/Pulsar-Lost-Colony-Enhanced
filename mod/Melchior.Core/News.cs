// Notiziario del subspazio: notizie della galassia per categoria (fronte, commercio, rivali, taglie, scienza, dicerie),
// generate dall'host a partire da fatti veri della partita e da dicerie; alcune dicerie sono fondate e portano un'offerta
// sulla bacheca. Le notizie si salvano con la cronaca della galassia e arrivano a tutto l'equipaggio.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Melchior
{
    public class NewsItem { public long t; public string cat = "", text = ""; }

    public static class News
    {
        public static readonly Dictionary<string, (string label, string color)> Cats = new()
        {
            ["fronte"] = ("FRONTE", "#FF8073"), ["commercio"] = ("COMMERCIO", "#FFD75E"), ["rivali"] = ("RIVALI", "#C58AF9"),
            ["taglie"] = ("TAGLIE", "#F2A65A"), ["scienza"] = ("SCIENZA", "#5EE0D6"), ["dicerie"] = ("DICERIE", "#8B90A0"), ["flotta"] = ("FLOTTA", "#9DB4FF"),
        };
        static readonly System.Random rng = new();
        static readonly JsonSerializerOptions json = new() { IncludeFields = true };
        static List<NewsItem> remote = new();
        static float nextRumor = 90f, nextSend; static string lastSent = "";
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        public static List<NewsItem> Items => Host ? Saga.State.news : remote;

        public static void Post(string cat, string text, bool alsoBanner = false)
        {
            if (!Host || !Saga.Loaded) return;
            var n = Saga.State.news; n.Add(new NewsItem { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cat = cat, text = text });
            if (n.Count > 40) n.RemoveAt(0);
            Saga.MarkDirty(); lastSent = "";
            if (alsoBanner) Stations.Announce("NOTIZIARIO · " + (Cats.TryGetValue(cat, out var c) ? c.label : cat.ToUpperInvariant()), text);
            Plugin.L.LogInfo($"[notiziario] {cat}: {text}");
        }

        // Dicerie: fatti della galassia raccontati a mezza voce; una su tre è fondata e porta un'offerta sulla bacheca.
        static readonly string[] Places = { "Porto Kepler", "Tycho Minore", "Cornelia", "Sirio", "Nuova Ravenna", "Hadley", "Vesta IV", "Cerbero" };
        static readonly string[] Traders = { "un mercante di Ganimede", "una carovana di minatori", "un capitano in pensione", "un contrabbandiere", "un pilota della Gilda" };
        public static void Rumor()
        {
            string P(string[] a) => a[rng.Next(a.Length)];
            var legend = Rarity.Cfg.Legendaries.Length > 0 ? Rarity.Cfg.Legendaries[rng.Next(Rarity.Cfg.Legendaries.Length)].Name : "un pezzo leggendario";
            var rival = RivalForce.Current;
            var options = new List<Func<(string cat, string text, int family)>>
            {
                () => ("dicerie", $"Nei bar di {P(Places)} si dice che «{legend}» sia nella stiva di {P(Traders)}. Nessuno sa per quanto.", -1),
                () => ("commercio", $"Prezzi del carburante in salita verso {P(Places)}: le rotte commerciali evitano i settori di confine.", -1),
                () => ("scienza", $"L'Accademia registra un'anomalia gravitazionale vicino a {P(Places)} e cerca un equipaggio disposto a indagare.", 2),
                () => { var f = Nemesis.Alive.OrderBy(_ => rng.Next()).FirstOrDefault(); return f == null ? ("taglie", $"Il traditore Halloran è stato avvistato vicino a {P(Places)}. La taglia è ancora aperta.", 0) : ("taglie", $"Avvistamenti di {f.Full} nel settore {Melchior.Places.Of(f.sector)}. Nei porti si parla di una taglia.", -2); },
                () => ("flotta", $"Il Comando di Flotta segnala convogli civili in difficoltà nei settori esterni: chi può, risponda alle chiamate.", 3),
                () => ("dicerie", $"Un relitto senza nome alla deriva oltre {P(Places)}: chi l'ha visto giura che la scatola nera trasmette ancora.", 5),
                () => ("scienza", $"Segnali di una specie sconosciuta captati da {P(Traders)}: il Comando raccomanda prudenza e armi a riposo.", 4),
            };
            var leg = Legacy.Pick(); if (leg != null) options.Add(() => ("dicerie", $"Nei porti si racconta ancora della «{leg.ship}», la nave che {leg.deed}. C'è chi giura di averne visto il relitto.", -1));
            if (rival != null) options.Add(() => ("rivali", $"{rival.name} raduna navi: chi li ha incrociati parla di {rival.leader} e di una flotta più grande del solito.", -1));
            var (cat, text, family) = options[rng.Next(options.Count)]();
            Post(cat, text);
            if (family == -2 && rng.NextDouble() < 0.5 && Stations.MyShip != null && Contracts.PostBounty(Director.Power(Stations.MyShip, out _, out _)))
                Post(cat, "La diceria è fondata: c'è una taglia sulla bacheca.");
            if (family >= 0 && rng.NextDouble() < 0.4 && Stations.MyShip != null && Contracts.PostLead(Director.Power(Stations.MyShip, out _, out _), family))
                Post(cat, "La diceria sembra fondata: c'è un'offerta sulla bacheca.");
        }

        public static void Tick()
        {
            if (UnityEngine.Time.time < nextSend && UnityEngine.Time.time < nextRumor) return;
            try
            {
                if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
                if (UnityEngine.Time.time >= nextRumor) { nextRumor = UnityEngine.Time.time + 240f + rng.Next(240); Rumor(); }
                string payload = JsonSerializer.Serialize(Saga.State.news.TakeLast(12).ToList(), json);
                if (payload != lastSent || UnityEngine.Time.time >= nextSend)
                {
                    lastSent = payload; nextSend = UnityEngine.Time.time + 20f;
                    try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)("news|" + payload), true, new RaiseEventOptions { Receivers = ReceiverGroup.Others }); } catch { }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Notiziario: " + e.Message); nextSend = UnityEngine.Time.time + 30f; }
        }

        // Al rientro dopo un'assenza: un riassunto e alcune notizie maturate nel frattempo.
        public static void Digest(double hours)
        {
            if (hours < 0.5) return;
            string span = hours >= 48 ? $"{hours / 24:0} giorni" : hours >= 2 ? $"{hours:0} ore" : "poco meno di un'ora";
            int n = Math.Clamp((int)Math.Ceiling(Math.Log(1 + hours, 2)), 1, 6);
            for (int i = 0; i < n; i++) Rumor();
            Post("flotta", $"Mentre eravate via ({span}) la galassia è andata avanti: {n} notizie nel notiziario.", alsoBanner: true);
            Service.Add("galassia.rientri", 1);
        }

        public static void Receive(string msg) { try { remote = JsonSerializer.Deserialize<List<NewsItem>>(msg[5..], json) ?? new(); Panels.RefreshAll(); } catch { } }
    }
}

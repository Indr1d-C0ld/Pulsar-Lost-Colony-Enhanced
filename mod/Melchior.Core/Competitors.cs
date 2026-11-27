// Equipaggi concorrenti: tre liberi equipaggi con nave, capitano e stile che vivono nella stessa galassia.
// A ogni salto (e ogni pochi minuti) possono prendersi un'offerta della bacheca, riscuotere una taglia (anche quella che
// state inseguendo), arrivare prima di voi a un luogo leggendario. Tutti, voi compresi, accumulano fama in una
// classifica dei liberi equipaggi. Li si incontra di persona negli incontri: si collabora, si gareggia, si sabota.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class Competitor { public string ship = "", captain = "", style = ""; public int score, relation, deeds; public bool met; }

    public static class Competitors
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static List<Competitor> All => Saga.State.competitors ??= new List<Competitor>();
        public static List<Competitor> List() { if (Host && Saga.Loaded) Ensure(); return All; }
        static readonly string[] Ships = { "Saetta Rossa", "Cometa di Halley", "Fulmine di Sirio", "Libellula", "Vecchia Volpe", "Stella Errante", "Nottola", "Gabbiano d'Argento" };
        static readonly string[] Captains = { "Mirelle Okoro", "Tobias Rennick", "Hana Vasquez-Lee", "Gregor Antal", "Sunniva Brekke", "Dax Moreno", "Ilaria Fontana", "Kofi Mensah" };
        public static readonly Dictionary<string, string> Styles = new() { ["onesti"] = "equipaggio onesto", ["cacciatori"] = "cacciatori di taglie", ["spregiudicati"] = "spregiudicati" };
        static int lastJumps = -1; static float next = 180f;

        static void Ensure()
        {
            if (All.Count >= 3) return;
            var keys = Styles.Keys.ToList();
            while (All.Count < 3)
            {
                var used = All.Select(c => c.ship).ToHashSet(); var ships = Ships.Where(s => !used.Contains(s)).ToList();
                var usedC = All.Select(c => c.captain).ToHashSet(); var caps = Captains.Where(s => !usedC.Contains(s)).ToList();
                All.Add(new Competitor { ship = ships[rng.Next(ships.Count)], captain = caps[rng.Next(caps.Count)], style = keys[All.Count % keys.Count], score = rng.Next(20, 80) });
            }
            Saga.MarkDirty();
        }
        public static Competitor Pick() { if (!Host || !Saga.Loaded) return null; Ensure(); return All[rng.Next(All.Count)]; }
        static string Ship(Competitor c) => $"la «{c.ship}»";
        static string Cap(string s) => char.ToUpper(s[0]) + s[1..];

        // Fama dell'equipaggio nella classifica dei liberi equipaggi.
        public static void Renown(int pts, string why = null)
        {
            if (!Host || !Saga.Loaded || pts == 0) return;
            if (pts > 0) pts = (int)Math.Round(pts * (1f + Loot.Mine("fama") + PawnLoot.Crew("fama")));
            int before = Rank(); Saga.State.renown += pts; Saga.MarkDirty(); GalaxyView.Dirty();
            int after = Rank();
            if (after == 1 && before > 1) Trophies.Add("Coppa dei liberi equipaggi", "primo posto in classifica");
            if (after < before) { string t = $"La «{Our()}» sale al {after}° posto fra i liberi equipaggi" + (after == 1 ? ": nessuno ha una fama più grande, adesso." : "."); News.Post("flotta", t); Director.Notify("CLASSIFICA", t); }
        }
        static string Our() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }
        public static int Rank() { Ensure(); return 1 + All.Count(c => c.score > Saga.State.renown); }
        public static string Standings()
        {
            if (!Host) return GalaxyView.Current?.rank ?? "";
            Ensure();
            var rows = All.Select(c => (name: c.ship, score: c.score, us: false)).Append((name: Our(), score: Saga.State.renown, us: true)).OrderByDescending(x => x.score).ToList();
            return "Liberi equipaggi: " + string.Join(" · ", rows.Select((x, i) => $"{i + 1}° {(x.us ? "noi" : x.name)} {x.score}"));
        }

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                Ensure();
                int j = Obscured.Jumps(); bool jumped = lastJumps >= 0 && j != lastJumps; lastJumps = j;
                if (!jumped && UnityEngine.Time.time < next) return;
                next = UnityEngine.Time.time + 180f + rng.Next(120);
                foreach (var c in All) Act(c);
            }
            catch (Exception e) { Plugin.L.LogWarning("Concorrenti: " + e.Message); }
        }

        static void Act(Competitor c)
        {
            var b = Saga.State.board; var mine = Saga.State.contract;
            double r = rng.NextDouble();
            // una taglia: se è quella che state inseguendo, la perdete
            if (r < (c.style == "cacciatori" ? 0.05 : 0.015))
            {
                var figs = Nemesis.Alive.ToList(); if (figs.Count == 0) return;
                // la taglia che state inseguendo ve la soffiano solo se tardate (a due salti dalla scadenza)
                var hunted = mine?.kind == "taglia" && mine.expires - Obscured.Jumps() <= 2 ? Nemesis.Find(mine.target) : null;
                var f = hunted ?? figs[rng.Next(figs.Count)];
                if (f == Nemesis.Engaged || (mine?.kind == "taglia" && f.Full == mine.target && f != hunted)) return;
                if (f.rank >= 4 && rng.NextDouble() < 0.7) { News.Post("taglie", $"{Cap(Ship(c))} ha messo alle strette {f.Full}, ma l'ammiraglia nemica è sfuggita."); return; }
                f.alive = false; f.memory = "abbattuto"; c.score += 20 + f.rank * 8; c.deeds++; Saga.MarkDirty();
                News.Post("taglie", $"{Cap(Ship(c))} di {c.captain} ha abbattuto {f.Full} e riscosso la taglia.");
                if (f == hunted) Contracts.OnHunt(false, f, $"{Ship(c)} è arrivata prima di noi");
                b.RemoveAll(o => o.kind == "taglia" && o.target == f.Full); Contracts.Touch();
                return;
            }
            // un luogo leggendario lasciato aspettare troppo
            var L = Saga.State.legend;
            if (L != null && r < 0.2 && Saga.State.legendSince > 0 && Obscured.Jumps() - Saga.State.legendSince > 8)
            {
                Saga.State.legend = null; c.score += 60; c.deeds++; Course.Clear(); Saga.MarkDirty();
                News.Post("scienza", $"{Cap(Ship(c))} ha raggiunto {L.name} e ne ha svuotato i forzieri. Altri, si dice, avevano la mappa da tempo.");
                Director.Notify("CONCORRENTI", $"{Cap(Ship(c))} è arrivata prima di noi a {L.name}. La mappa non vale più niente.");
                return;
            }
            // un contratto della bacheca (mai quello in corso, mai le richieste urgenti)
            if (r < 0.45 && b.Count > 1)
            {
                var o = b.Where(x => !x.urgent && x.kind != "taglia").OrderBy(_ => rng.Next()).FirstOrDefault(); if (o == null) return;
                if (c.style == "spregiudicati" && o.kind == "trasporto") return;
                b.Remove(o); c.score += Math.Max(5, o.reward / 150); c.deeds++; Contracts.Touch(); Saga.MarkDirty();
                if (rng.NextDouble() < 0.5) News.Post("flotta", $"{Cap(Ship(c))} ha preso il contratto «{o.title}» per {o.issuer}.");
                return;
            }
            c.score += rng.Next(0, 6); // lavori minori che non fanno notizia
        }

        public static string Describe() { Ensure(); return string.Join("\n      ", All.Select(c => $"«{c.ship}» di {c.captain}, {Styles[c.style]}, fama {c.score}, rapporti {c.relation:+0;-0;0}, imprese {c.deeds}").Prepend(Standings())); }
        public static string Force() { Ensure(); foreach (var c in All) Act(c); return Describe(); }
    }
}

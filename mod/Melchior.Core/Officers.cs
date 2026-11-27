// Ufficiali dell'equipaggio: chi siede a una stazione affidata a un bot ha un nome, un grado della marina, un tratto e
// una carriera. Ogni procedura riuscita vale esperienza; con le promozioni l'ufficiale diventa più bravo (le procedure
// dei bot riescono più spesso). Nei momenti più duri un ufficiale può restare ferito (lavora un sostituto, peggio, per
// qualche salto) e, raramente, cadere: al suo posto arriva un guardiamarina. Gli ufficiali vivono nella cronaca della
// galassia; il riepilogo viaggia con GalaxyView e compare nella scheda Quadro del capitano.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class Officer
    {
        public int cls, rank, xp, injuredUntil = -1, deeds; public string first = "", last = "", trait = ""; public bool alive = true, left;
        public int loyalty = 60, wishArg = -1, wishSince = -1, wishes; public string wish = "", friend = "", rival = "";
        public Dictionary<string, int> skills = new(); // competenze personali: antincendio, riparazioni, difesa, soccorso (esperienza)
    }

    public static class Officers
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static List<Officer> All => Saga.State.officers ??= new List<Officer>();
        public static readonly string[] Ranks = { "Guardiamarina", "Sottotenente di vascello", "Tenente di vascello", "Capitano di corvetta", "Capitano di fregata" };
        public static readonly string[] Short = { "GM", "STV", "TV", "CC", "CF" };
        static readonly int[] Need = { 30, 80, 160, 300 };
        static readonly string[] Firsts = { "Ada", "Bruno", "Celeste", "Davide", "Elia", "Fosca", "Gaia", "Ivo", "Lea", "Marco", "Nora", "Oscar", "Rita", "Sami", "Teo", "Vera", "Yuri", "Zoe" };
        static readonly string[] Lasts = { "Hale", "Rinaldi", "Okoye", "Brandt", "Sorensen", "Valli", "Ishikawa", "Moreau", "Castelli", "Nakamura", "Ferrante", "Lindqvist", "Adeyemi", "Kowalski", "Serra", "Duarte" };
        public static readonly Dictionary<string, string> Traits = new()
        {
            ["freddo"] = "sangue freddo sotto il fuoco", ["meticoloso"] = "precisione maniacale", ["impulsivo"] = "temperamento impulsivo", ["fortunato"] = "fortuna sfacciata", ["ambizioso"] = "ambizione", ["veterano"] = "esperienza di guerra",
        };
        public static string At(int cls) => cls switch { 0 => "al comando", 1 => "al timone", 2 => "alla Scienza", 3 => "alle Armi", _ => "all'Ingegneria" };
        public static string StationOf(int cls) => cls switch { 0 => "Comando", 1 => "Timone", 2 => "Scienza", 3 => "Armi", _ => "Ingegneria" };

        static Officer Make(int cls, int rank = -1)
        {
            var used = All.Where(o => o.alive).Select(o => o.last).ToHashSet(); var lasts = Lasts.Where(x => !used.Contains(x)).ToList(); if (lasts.Count == 0) lasts = Lasts.ToList();
            var keys = Traits.Keys.ToList(); string t = keys[rng.Next(keys.Count)];
            var o = new Officer { cls = cls, first = Firsts[rng.Next(Firsts.Length)], last = lasts[rng.Next(lasts.Count)], trait = t, rank = rank >= 0 ? rank : t == "veterano" ? 2 : rng.Next(0, 2) };
            if (Home.Level >= 3 && o.rank == 0) o.rank = 1; // i veterani del porto di casa
            o.xp = o.rank > 0 ? Need[o.rank - 1] : 0;
            All.Add(o); Saga.MarkDirty(); return o;
        }
        public static Officer Of(int cls) { if (!Host || !Saga.Loaded) return null; return All.FirstOrDefault(o => o.alive && o.cls == cls) ?? Make(cls); }
        public static string Name(Officer o) => $"{Short[o.rank]} {o.last}";
        public static string Full(Officer o) => $"{Ranks[o.rank].ToLowerInvariant()} {o.first} {o.last}";
        static bool Injured(Officer o) => o.injuredUntil >= 0 && Obscured.Jumps() < o.injuredUntil;

        static bool IsBotAt(int cls) { try { var p = PLServer.Instance.GetCachedFriendlyPlayerOfClass(cls, Stations.MyShip); return p != null && p.IsBot; } catch { return false; } }

        // Moltiplicatore della riuscita delle procedure del bot alla stazione indicata.
        public static float Skill(int cls, bool fight)
        {
            var o = Of(cls); if (o == null) return 1f;
            if (Injured(o)) return 0.6f;
            float m = 1f + o.rank * 0.06f + (o.loyalty >= 80 ? 0.05f : o.loyalty < 25 ? -0.08f : 0f); // la lealtà conta
            switch (o.trait)
            {
                case "freddo": if (fight) m += 0.1f; break;
                case "meticoloso": m += 0.06f; break;
                case "impulsivo": m += fight ? 0.15f : -0.05f; break;
            }
            return m;
        }

        // Una procedura riuscita: esperienza e, al momento giusto, la promozione.
        public static void Credit(int cls, int xp = 1)
        {
            if (!Host || !Saga.Loaded) return;
            var o = Of(cls); if (o == null || Injured(o)) return;
            o.deeds++; o.xp += o.trait == "ambizioso" ? xp * 3 / 2 + (xp == 1 && rng.Next(2) == 0 ? 1 : 0) : xp; Saga.MarkDirty();
            if (o.rank < Ranks.Length - 1 && o.xp >= Need[o.rank])
            {
                o.rank++; GalaxyView.Dirty();
                Director.Notify("PROMOZIONE", $"{o.first} {o.last}, {At(o.cls)}, riceve la promozione a {Ranks[o.rank].ToLowerInvariant()}. Applausi in plancia.");
                Morale.Change(3, null); Saga.Chronicle($"Promozione: {Full(o)}, {At(o.cls)}.");
                Service.Add("ufficiali.promozioni", 1);
            }
        }
        // Dal testo dei messaggi dei bot ("Ingegneria", "Scienza", ...) alla classe.
        public static int ClassOf(string who) => who switch { "Ingegneria" => 4, "Scienza" => 2, "Armi" => 3, "Pilota" or "Timone" => 1, "Capitano" or "Comando" => 0, _ => -1 };
        public static string Sign(string who) { int c = ClassOf(who); if (c < 0 || !Host || !Saga.Loaded) return who; var o = Of(c); return o == null ? who : $"{who} ({Name(o)}{(Injured(o) ? ", sostituto" : "")})"; }

        // ---- ferite e caduti: quando lo scafo è a pezzi
        static bool hurtArmed = true;
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                var st = Stations.MyShip.MyStats; float f = st.HullMax > 0 ? st.HullCurrent / st.HullMax : 1f;
                if (f > 0.5f) hurtArmed = true;
                if (!hurtArmed || f > 0.25f) return;
                hurtArmed = false;
                var bots = new[] { 1, 2, 3, 4, 0 }.Where(IsBotAt).ToList(); if (bots.Count == 0) return;
                var o = Of(bots[rng.Next(bots.Count)]); if (o == null || Injured(o)) return;
                double p = (o.trait == "fortunato" ? 0.2 : 0.4) * Shipyard.Less("infermeria"); if (rng.NextDouble() > p) return;
                if (f < 0.12f && rng.NextDouble() < (o.trait == "fortunato" ? 0.06 : 0.15) * Shipyard.Less("infermeria", 0.3f))
                {
                    o.alive = false; var n = Make(o.cls, 0); GalaxyView.Dirty();
                    Director.Notify("IN MEMORIA", $"Abbiamo perso {Full(o)}. {char.ToUpper(At(o.cls)[0]) + At(o.cls)[1..]} arriva {n.first} {n.last}, guardiamarina.");
                    Morale.Change(-8, "Abbiamo perso " + o.first + " " + o.last); Saga.Chronicle($"In memoria di {Full(o)}, dopo {o.deeds} imprese a bordo.");
                    News.Post("flotta", $"La «{(string)Stations.MyShip.ShipName}» piange la perdita di {Full(o)}.");
                    Service.Add("ufficiali.caduti", 1);
                    return;
                }
                int jumps = Math.Max(1, 2 + rng.Next(3) - Shipyard.Level("infermeria")); o.injuredUntil = Obscured.Jumps() + jumps; Saga.MarkDirty(); GalaxyView.Dirty();
                Director.Notify("FERITO", $"{Full(o)} è in infermeria: {At(o.cls)} lavora un sostituto per {jumps} salti.");
                Morale.Change(-2, null);
            }
            catch (Exception e) { Plugin.L.LogWarning("Ufficiali: " + e.Message); }
        }

        // Un ufficiale in servizio a una stazione affidata a un bot (per gli incontri).
        public static Officer Pick() { if (!Host) return null; var c = new[] { 1, 2, 3, 4 }.Where(IsBotAt).ToList(); if (c.Count == 0) return null; var o = Of(c[rng.Next(c.Count)]); return o != null && !Injured(o) ? o : null; }
        // Un secondo ufficiale in servizio, diverso dal primo (per gli eventi di bordo).
        public static Officer PickOther(Officer first) { if (!Host || first == null) return null; var c = new[] { 1, 2, 3, 4 }.Where(x => x != first.cls && IsBotAt(x)).ToList(); if (c.Count == 0) return null; var o = Of(c[rng.Next(c.Count)]); return o != null && !Injured(o) ? o : null; }
        public static void Wound(Officer o, int jumps) { o.injuredUntil = Obscured.Jumps() + jumps; Saga.MarkDirty(); GalaxyView.Dirty(); }
        // Assunzione: chi c'era a quella stazione sbarca, arriva il nuovo ufficiale con il suo nome, tratto e grado.
        public static Officer Hire(int cls, string first, string last, string trait, int rank)
        {
            foreach (var old in All.Where(x => x.alive && x.cls == cls)) { old.alive = false; old.left = true; }
            var o = new Officer { cls = cls, first = first, last = last, trait = Traits.ContainsKey(trait) ? trait : "meticoloso", rank = Math.Clamp(rank, 0, Ranks.Length - 1) };
            o.xp = o.rank > 0 ? Need[o.rank - 1] : 0; All.Add(o); Saga.MarkDirty(); GalaxyView.Dirty(); return o;
        }
        public static string RandomFirst() => Firsts[rng.Next(Firsts.Length)];
        public static string RandomLast() => Lasts[rng.Next(Lasts.Length)];
        public static void Replace(Officer o) { o.alive = false; o.left = true; Make(o.cls, 0); Saga.MarkDirty(); GalaxyView.Dirty(); }
        public static void Promote(Officer o) { if (o.rank < Ranks.Length - 1) { o.xp = Math.Max(o.xp, Need[o.rank]); Credit(o.cls, 0); } }

        // Per il riepilogo del pannello: una riga per stazione.
        public static List<string> Rows()
        {
            var r = new List<string>();
            for (int c = 0; c < 5; c++)
            {
                string s = "";
                try
                {
                    var p = PLServer.Instance.GetCachedFriendlyPlayerOfClass(c, Stations.MyShip);
                    if (p != null && !p.IsBot) s = (string)p.GetPlayerName();
                    else if (p != null) { var o = Of(c); if (o != null) s = Name(o) + (Injured(o) ? " · in infermeria" : "") + (o.wish.Length > 0 ? " · desidera " + CrewLives.WishShort(o) : "") + (o.loyalty >= 80 ? " · fedele" : o.loyalty < 25 ? " · scontento" : ""); }
                }
                catch { }
                r.Add(s);
            }
            return r;
        }

        public static string Describe() => !All.Any() ? "nessun ufficiale" : string.Join("\n      ", All.OrderBy(o => o.cls).Select(o => $"{(o.alive ? "" : o.left ? "(congedo) " : "† ")}{StationOf(o.cls),-10} {Full(o)}, {Traits[o.trait]}, esperienza {o.xp}, imprese {o.deeds}{(Injured(o) ? $", ferito fino al salto {o.injuredUntil}" : "")}").Prepend("ufficiali:"));
        public static string Wound(int cls) { var o = Of(cls); if (o == null) return "nessuno"; o.injuredUntil = Obscured.Jumps() + 2; Saga.MarkDirty(); GalaxyView.Dirty(); return Full(o) + " ferito per 2 salti"; }
    }
}

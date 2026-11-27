// Taglia sulla nostra testa: l'infamia accumulata e le fazioni rese ostili alzano un livello di ricercati (0-5).
// A ogni livello il direttore manda cacciatori di taglie più numerosi, che annunciano la cifra. Il livello cala piano
// a ogni salto; in porto si può saldare il conto (incontro dedicato).
using System;
using System.Linq;

namespace Melchior
{
    public class WantedState { public int heat, lastInfamy = -1, caught; }

    public static class Wanted
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static WantedState W => Saga.State.wanted ??= new WantedState();
        public static int Level => Host ? Math.Clamp(W.heat / 20, 0, 5) : GalaxyView.Current?.wantedLevel ?? 0;
        public static int Bounty => W.heat * 60;
        static int lastJumps = -1;

        public static void Heat(int d, string why = null)
        {
            if (!Host || !Saga.Loaded || d == 0) return;
            if (d > 0) d = Math.Max(1, (int)Math.Round(d * Rules.Heat));
            int before = Level; W.heat = Math.Clamp(W.heat + d, 0, 120); int after = Level; Saga.MarkDirty(); GalaxyView.Dirty();
            if (after > before) { Director.Notify("RICERCATI", $"La taglia sulla nostra testa sale: livello {after} su 5, {Bounty} crediti." + (why != null ? " " + why + "." : "") + " Arriveranno cacciatori."); News.Post("taglie", $"Sale la taglia sulla «{(string)Stations.MyShip.ShipName}»: {Bounty} crediti a chi la porta in porto."); }
            else if (after < before && after == 0) Director.Notify("RICERCATI", "Nessuno ci cerca più. Per ora.");
        }

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            var w = W; int inf = Saga.State.infamia;
            if (w.lastInfamy < 0) w.lastInfamy = inf;
            if (inf > w.lastInfamy) Heat((inf - w.lastInfamy) * 6, "Le nostre azioni fanno parlare");
            w.lastInfamy = inf;
            int j = Obscured.Jumps(); if (lastJumps >= 0 && j != lastJumps && w.heat > 0) { w.heat = Math.Max(0, w.heat - 2 - Bases.List.Where(b => b.kind == "fortezza" && b.state == 1).Sum(b => b.level)); Saga.MarkDirty(); GalaxyView.Dirty(); }
            lastJumps = j;
        }

        // Evento del direttore: cacciatori di taglie, tanti quanti il livello lo giustifica.
        public static float Weight() => Level == 0 ? 0f : 0.6f + Level * 0.5f;
        public static bool Hunters(PLShipInfo ship, float power)
        {
            int lvl = Level; if (lvl == 0) return false;
            int n = Math.Min(4, 1 + lvl / 2), made = 0;
            for (int i = 0; i < n; i++) if (Director.Spawn(ship, i == 0 && lvl >= 3 ? Director.PickShip(power + 2, 1) : EShipType.E_BOUNTY_HUNTER_01, Director.Modifiers(power + lvl) | (lvl >= 4 ? 1 << (int)EShipModifierType.ELITE : 0), "Cacciatore di taglie") != null) made++;
            if (made == 0) return false;
            Director.Notify("COMUNICAZIONE IN ARRIVO", $"Cacciatori di taglie: «{(string)ship.ShipName}, sulla vostra testa ci sono {Bounty} crediti. Niente di personale.»");
            W.caught++; Saga.MarkDirty(); Service.Add("ricercati.cacciatori", 1);
            return true;
        }

        public static string Line() => Level == 0 ? "" : $"Ricercati {Level}/5";
        public static string Describe() => $"calore {W.heat}, livello {Level}, taglia {Bounty} crediti, cacciatori incontrati {W.caught}";
        public static string Set(int heat) { W.heat = heat; Saga.MarkDirty(); GalaxyView.Dirty(); return Describe(); }
        public static void Clear(int amount) { W.heat = Math.Max(0, W.heat - amount); Saga.MarkDirty(); GalaxyView.Dirty(); }
    }
}

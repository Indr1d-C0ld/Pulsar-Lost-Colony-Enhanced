// Chiamate al bersaglio: il capitano prende l'iniziativa dalla scheda Ordini.
// - Chiedere notizie a una nave non ostile: voci, piste per taglie, tracce di luoghi nei settori vicini.
// - Intimare la resa a una nave ostile: riesce più spesso se è malridotta e se l'equipaggio è forte o famigerato.
//   L'equipaggio nemico abbandona la nave (che si autodistrugge) e lascia il carico; un personaggio ricorrente
//   viene catturato, e il capitano decide se consegnarlo, chiedere un riscatto o lasciarlo andare.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public static class Hail
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static readonly Dictionary<int, float> cooldown = new();
        public static string Last = "";

        public static void Request(string op)
        {
            if (Host) { Execute(op); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|chiamata|{op}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var p = msg.Split('|'); if (p.Length >= 3 && Host) Execute(p[2]); }

        // ostile è chi ce l'ha con noi: agganciare una nave la mette nella nostra lista, ma non la rende nemica
        static bool Hostile(PLShipInfo me, PLShipInfoBase o) { try { return o.HostileShips.Contains(me.ShipID) || (o.TargetShip != null && o.TargetShip.ShipID == me.ShipID) || (o.FactionID >= 0 && Factions.Level(o.FactionID) <= -3); } catch { return false; } }
        static string Name(PLShipInfoBase o) { try { var c = Talents.CaptainOf(o); return (string)o.ShipName + (c != null ? $" (cap. {c.name}, liv. {c.level}, nave {Wear.NpcLabel(o)})" : $" (nave {Wear.NpcLabel(o)})"); } catch { return "la nave"; } }
        static void Say(string t) { Last = t; Director.Notify("COMUNICAZIONI", t); }

        public static string Execute(string op)
        {
            var me = Stations.MyShip; if (me == null || !Saga.Loaded) return "";
            PLShipInfoBase t = null; try { t = me.TargetShip; } catch { }
            if (t == null || t.HasBeenDestroyed) { Say("Nessun bersaglio agganciato: il capitano deve prima scegliere con chi parlare."); return Last; }
            float now = UnityEngine.Time.time;
            if (cooldown.TryGetValue(t.ShipID, out var until) && now < until) { Say($"{Name(t)} non risponde più alle nostre chiamate, per ora."); return Last; }
            cooldown[t.ShipID] = now + 45f;
            bool hostile = Hostile(me, t); int fac = -1; try { fac = t.FactionID; } catch { }
            return op == "resa" ? Surrender(me, t, hostile) : News(me, t, hostile, fac);
        }

        static string News(PLShipInfo me, PLShipInfoBase t, bool hostile, int fac)
        {
            if (hostile) { Say($"{Name(t)} risponde con un'unica parola, e non è ripetibile in plancia."); return Last; }
            var local = Traffic.Hail(me, t); if (local != null) { Say(local); return Last; }
            if (fac >= 0 && fac != 4 && Array.IndexOf(GalaxyWar.Factions, fac) >= 0 && Factions.Level(fac) <= -2) { Say($"{Name(t)}: «La {GalaxyWar.Name(fac)} non parla con voi.»"); return Last; }
            bool friend = fac >= 0 && Factions.Level(fac) >= 2;
            double r = rng.NextDouble() - (friend ? 0.2 : 0);
            if (r < 0.35 && Contracts.PostBounty(Director.Power(me, out _, out _))) Say($"{Name(t)}: «Cercate lavoro? Abbiamo incrociato un nome che vale una taglia.» C'è una nuova offerta sulla bacheca.");
            else if (r < 0.65)
            {
                int seed = (int)PLServer.Instance.GalaxySeed; var nb = PLServer.GetCurrentSector()?.Neighbors; string hint = null;
                if (nb != null) for (int i = 0; i < nb.Count && hint == null; i++) { var n = nb[i]; var p = n == null ? null : Exploration.PoiOf(seed, n.ID); if (p != null && !Saga.State.explored.Contains(n.ID)) hint = $"nel settore {Places.Of(n.ID)} c'è qualcosa da vedere: {p.Value.name}"; }
                if (hint != null) Say($"{Name(t)}: «Se vi interessano le stranezze, {hint}.»"); else { News_Rumor(); Say($"{Name(t)} ci racconta le ultime voci dei porti: le trovate nel notiziario."); }
            }
            else if (r < 0.85) { News_Rumor(); Say($"{Name(t)} ci racconta le ultime voci dei porti: le trovate nel notiziario."); }
            else Say($"{Name(t)}: «Non abbiamo niente da dirvi. Buon viaggio.»");
            if (friend && rng.NextDouble() < 0.3) { try { var st = me.MyStats; st.HullCurrent = Math.Min(st.HullMax, st.HullCurrent + st.HullMax * 0.1f); } catch { } Stations.LogEvent(me.ShipID, $"{Name(t)} ci manda una squadra di riparazione: scafo +10%"); }
            return Last;
        }
        static void News_Rumor() { try { global::Melchior.News.Rumor(); } catch { } }

        static string Surrender(PLShipInfo me, PLShipInfoBase t, bool hostile)
        {
            if (!hostile) { Say($"{Name(t)}: «Resa? Non siamo nemmeno in guerra, capitano.»"); return Last; }
            float frac = 1f; try { frac = (t.MyStats.HullCurrent + t.MyStats.ShieldsCurrent) / Math.Max(1f, t.MyStats.HullMax + t.MyStats.ShieldsMax); } catch { }
            var fig = Nemesis.Engaged != null && Nemesis.FlagshipId == t.ShipID ? Nemesis.Engaged : null;
            float power = Director.Power(me, out _, out _);
            double chance = 0.08 + (1 - frac) * 0.7 + Math.Min(0.2, power * 0.01) + Math.Min(0.15, Saga.State.infamia * 0.01) + (Morale.Title() != "Sconosciuti" ? 0.05 : 0) + (Research.Has("resa") ? 0.12 : 0) + 0.06 * Talents.Ours("ascendente");
            if (fig != null) chance *= Math.Max(0.25, 0.8 - fig.rank * 0.1) * (fig.traits.Contains("rancore") ? 0.6 : 1);
            chance = Math.Clamp(chance, 0.03, 0.9);
            Plugin.L.LogInfo($"[chiamate] resa a {Name(t)}: scafo {frac:P0}, probabilità {chance:P0}");
            if (rng.NextDouble() > chance) { Say(frac > 0.6 ? $"{Name(t)} ride sul canale aperto: «Arrendersi? A voi?»" : $"{Name(t)} esita, poi chiude la comunicazione. Non ancora."); return Last; }
            if (fig != null)
            {
                Nemesis.Capture(fig);
                Scuttle(t, me);
                Say($"{fig.Full} si arrende e sale a bordo in manette; l'ammiraglia viene abbandonata e si autodistrugge.");
                Saga.State.captive = fig.Full; Saga.MarkDirty();
                Encounters.Start("prigioniero", true);
                return Last;
            }
            Scuttle(t, me);
            var got = Loot.Grant(me, 1, 3 + Math.Min(3, (int)(power / 4)), "resa"); int cr = 200 + rng.Next(600); try { PLServer.Instance.CurrentCrewCredits += cr; } catch { }
            Saga.State.onore += 1; Saga.MarkDirty(); Morale.Change(2, null);
            Say($"{Name(t)} si arrende: l'equipaggio abbandona la nave nelle capsule e ci lascia il carico. {cr} crediti" + (got != null ? $" e {got.Name}" : "") + ". Nessuno è morto, oggi.");
            Service.Add("comando.rese", 1); Feats.Surrender();
            return Last;
        }

        static void Scuttle(PLShipInfoBase t, PLShipInfo me)
        {
            try { t.MyStats.HullCurrent = 0f; } catch { }
            try { t.DestroySelfIfDead(null); } catch { try { t.DestroySelfIfDead(me); } catch { } }
        }
    }
}

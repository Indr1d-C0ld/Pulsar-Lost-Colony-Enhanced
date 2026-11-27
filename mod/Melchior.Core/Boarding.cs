// Abbordaggi: squadre che si teletrasportano da una nave all'altra quando gli scudi del bersaglio cedono.
// (prima parte: strumenti per trovare interni ed equipaggi e spostare le pedine)
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public static partial class Boarding
    {
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }

        public static int SubHubOf(PLShipInfoBase ship)
        {
            if (ship == null) return -1;
            try { var all = PLTeleportationLocationInstance.AllTLIs; for (int i = 0; i < all.Count; i++) { var t = all[i]; if (t != null && t.MyShipInfo != null && t.MyShipInfo.ShipID == ship.ShipID) return t.SubHubID & 0x7FFFFFFF; } } catch { }
            return -1;
        }
        public static List<PLPlayer> CrewOf(PLShipInfoBase ship, bool botsOnly = true)
        {
            var l = new List<PLPlayer>(); if (ship == null) return l;
            try
            {
                var all = PLServer.Instance.AllPlayers;
                for (int i = 0; i < all.Count; i++)
                {
                    var p = all[i]; if (p == null || (botsOnly && !p.IsBot)) continue;
                    var pw = p.MyPawn; if (pw == null || pw.IsDead) continue;
                    if (p.StartingShip != null && p.StartingShip.ShipID == ship.ShipID) l.Add(p);
                }
            }
            catch { }
            return l;
        }
        public static List<PLPlayer> AboardOf(PLShipInfoBase ship)
        {
            var l = new List<PLPlayer>(); int sub = SubHubOf(ship); if (sub < 0) return l;
            try { var all = PLServer.Instance.AllPlayers; for (int i = 0; i < all.Count; i++) { var p = all[i]; if (p?.MyPawn != null && !p.MyPawn.IsDead && (p.SubHubID & 0x7FFFFFFF) == sub) l.Add(p); } } catch { }
            return l;
        }
        public static bool Send(PLPlayer p, PLShipInfoBase to)
        {
            int sub = SubHubOf(to); if (p == null || sub < 0) return false;
            try { p.NetworkTeleportToSubHub(sub, 0); return true; } catch (Exception e) { Plugin.L.LogWarning("Abbordaggio: " + e.Message); return false; }
        }

        public static string Info()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in PLEncounterManager.Instance.AllShips)
            {
                var s = kv.Value; if (s == null || s.HasBeenDestroyed) continue;
                sb.Append($"\n      {s.ShipID} «{(string)s.ShipName}» squadra {s.TeamID} interno {SubHubOf(s)} equipaggio {CrewOf(s).Count} a bordo {string.Join(",", AboardOf(s).Select(p => (string)p.GetPlayerName() + "/" + (p.MyPawn?.GetTeamID() ?? -1)))}");
            }
            return sb.ToString();
        }
        public static string TestIn(int enemyId, int n)
        {
            var e = PLEncounterManager.Instance.GetShipFromID(enemyId); var me = Stations.MyShip; if (e == null || me == null) return "nave non trovata";
            var crew = CrewOf(e).Take(n).ToList(); int ok = crew.Count(p => Send(p, me));
            return $"{ok}/{crew.Count} nemici mandati a bordo (interno {SubHubOf(me)})";
        }
        public static string TestOut(int enemyId)
        {
            var e = PLEncounterManager.Instance.GetShipFromID(enemyId); var me = Stations.MyShip; if (e == null || me == null) return "nave non trovata";
            var ours = CrewOf(me).Where(p => p.GetClassID() != 1).ToList(); int ok = ours.Count(p => Send(p, e));
            return $"{ok}/{ours.Count} dei nostri mandati sulla nave {enemyId} (interno {SubHubOf(e)})";
        }
        public static string TestHome()
        {
            var me = Stations.MyShip; int ok = 0;
            foreach (var p in CrewOf(me)) if ((p.SubHubID & 0x7FFFFFFF) != SubHubOf(me) && Send(p, me)) ok++;
            return $"{ok} rientrati";
        }
    }
}

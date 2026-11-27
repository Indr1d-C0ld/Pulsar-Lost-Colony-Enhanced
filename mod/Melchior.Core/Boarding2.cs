// Abbordaggi (seconda parte): regole, incursioni nemiche a bordo, nostri assalti e cattura delle navi.
// Regole del teletrasporto, uguali per tutti: la nave d'arrivo deve avere interni, scudi a terra (8% o meno),
// essere entro 4 km e nessuna delle due può essere in curvatura. I droni non hanno interni.
// - Incursioni nemiche: quando i nostri scudi cedono, le navi nemiche con equipaggio possono mandarci una squadra.
//   Gli incursori sabotano i sistemi finché restano a bordo; respinti, lasciano armi e a volte un prigioniero;
//   se resistono, saccheggiano cassa e stiva e ripartono.
// - Nostri assalti: dalla scheda Ordini del capitano (ABBORDAGGIO / RIENTRO), con l'ordine nativo di abbordaggio.
//   La squadra sabota la nave nemica e ne svuota le casse; abbattuto tutto l'equipaggio (che altrimenti rinasce),
//   la nave è nostra: smontarla, svuotarne la stiva, liberare i prigionieri o consegnarla alla fazione.
// - Le stesse regole valgono per le navi del traffico: i predoni abbordano i mercanti rimasti senza scudi.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Melchior
{
    public static partial class Boarding
    {
        static readonly System.Random rng = new();
        public static bool Enabled = true;

        // ---- regole
        public static (bool ok, string why) CanTeleport(PLShipInfoBase from, PLShipInfoBase to)
        {
            if (from == null || to == null || to.HasBeenDestroyed) return (false, "nessun bersaglio");
            bool drone = false; try { drone = to.IsDrone; } catch { }
            if (drone || SubHubOf(to) < 0) return (false, "il bersaglio non ha interni: è un drone o una sonda");
            try { if (from.InWarp || to.InWarp) return (false, "una delle due navi è in curvatura"); } catch { }
            float sh = 1f; try { var st = to.MyStats; sh = st.ShieldsMax > 0 ? st.ShieldsCurrent / st.ShieldsMax : 0f; } catch { }
            if (sh > 0.08f) return (false, $"scudi del bersaglio ancora al {sh * 100:0}%: il teletrasporto non passa");
            float d = 0f; try { d = (from.ExteriorTransformCached.position - to.ExteriorTransformCached.position).magnitude; } catch { }
            float range = 4000f + 1000f * Talents.R(from, "inseguitore");
            if (d > range) return (false, $"troppo lontani ({d / 1000f:0.0} km): il teletrasporto arriva a {range / 1000f:0} km");
            return (true, "");
        }
        static float Shields(PLShipInfoBase s) { try { var st = s.MyStats; return st.ShieldsMax > 0 ? st.ShieldsCurrent / st.ShieldsMax : 0f; } catch { return 1f; } }
        static bool Alive(PLPlayer p) { try { return p != null && p.MyPawn != null && !p.MyPawn.IsDead; } catch { return false; } }
        static PLPlayer ById(int id) { try { return PLServer.Instance.GetPlayerFromPlayerID(id); } catch { return null; } }
        static string Name(PLShipInfoBase s) { try { return "«" + (string)s.ShipName + "»"; } catch { return "la nave"; } }
        static bool Combat() { try { return (int)Stations.Get(Stations.MyShip.ShipID, "drx_phase") == 2; } catch { return false; } }

        // ---- incursioni nemiche a bordo
        sealed class Raid { public int origin; public List<int> members = new(); public float start, nextSab; public int killed; }
        static Raid incoming; static readonly Dictionary<int, float> cooldown = new();
        public static int RaidsRepelled, RaidsLost, Captured;

        static void Incoming(float now)
        {
            var me = Stations.MyShip;
            if (incoming != null)
            {
                var r = incoming; int sub = SubHubOf(me);
                var aboard = r.members.Select(ById).Where(p => Alive(p) && (p.SubHubID & 0x7FFFFFFF) == sub).ToList();
                if (now >= r.nextSab && aboard.Count > 0)
                {
                    r.nextSab = now + 6f;
                    foreach (var p in aboard) { var sy = Stations.SysIds[rng.Next(Stations.SysIds.Length)]; Stations.Set(me.ShipID, "dmx_" + sy, Math.Min(95f, Stations.Get(me.ShipID, "dmx_" + sy) + 5f * (1f - 0.3f * Talents.Ours("sentinella")) * (1f - Math.Min(0.8f, Loot.Mine("sentinella"))))); }
                }
                if (aboard.Count == 0)
                {
                    incoming = null; RaidsRepelled++; Feats.Repel();
                    Morale.Change(3, null); Officers.Credit(3, 2); Officers.Credit(4, 1); Competitors.Renown(5);
                    var got = rng.NextDouble() < 0.4 ? Loot.Grant(me, 1, 3, "armi degli incursori") : null;
                    Director.Notify("INTRUSI RESPINTI", $"La squadra della {Name(PLEncounterManager.Instance.GetShipFromID(r.origin))} è stata respinta." + (got != null ? $" Ci lasciano {got.Name}." : ""));
                    if (rng.NextDouble() < 0.3) Queue("prigioniero_incursore", r.origin.ToString());
                    return;
                }
                if (now - r.start > 100f)
                {
                    incoming = null; RaidsLost++; Talents.NpcXp(PLEncounterManager.Instance.GetShipFromID(r.origin), 30, "ha saccheggiato una nave all'arrembaggio");
                    int cash = 0; try { cash = PLServer.Instance.CurrentCrewCredits; } catch { }
                    int take = Math.Min(1500, cash / 12); try { PLServer.Instance.CurrentCrewCredits -= take; } catch { }
                    string lost = Market.LoseShare(0.2, "portati via dagli incursori");
                    var origin = PLEncounterManager.Instance.GetShipFromID(r.origin);
                    foreach (var p in aboard) if (origin != null) Send(p, origin);
                    Morale.Change(-4, null);
                    Director.Notify("INCURSORI IN FUGA", $"Gli incursori si ritirano col bottino: {take} crediti dalla cassa. {lost}".Trim());
                }
                return;
            }
            if (!Combat() || CanTeleport(null, me).ok == false && Shields(me) > 0.08f) return;
            if (Shields(me) > 0.08f) return;
            foreach (var kv in PLEncounterManager.Instance.AllShips)
            {
                var e = kv.Value; if (e == null || e.HasBeenDestroyed || e.ShipID == me.ShipID || e.TeamID == me.TeamID) continue;
                bool hostile = false; try { hostile = e.HostileShips.Contains(me.ShipID) || (e.TargetShip != null && e.TargetShip.ShipID == me.ShipID); } catch { }
                if (!hostile || (cooldown.TryGetValue(e.ShipID, out var cd) && now < cd)) continue;
                var (ok, _) = CanTeleport(e, me); if (!ok) continue;
                var crew = CrewOf(e).Where(p => (p.SubHubID & 0x7FFFFFFF) == SubHubOf(e)).ToList(); if (crew.Count < 2) continue;
                cooldown[e.ShipID] = now + 90f;
                if (rng.NextDouble() > 0.15 + 0.08 * EnemyAI.Level) continue;
                int n = Math.Min(crew.Count - 1, 2 + EnemyAI.Level / 2);
                var party = crew.Take(n).ToList(); var r = new Raid { origin = e.ShipID, start = now, nextSab = now + 8f };
                foreach (var p in party) if (Send(p, me)) r.members.Add(p.GetPlayerID());
                if (r.members.Count == 0) continue;
                incoming = r;
                Director.Notify("INTRUSI A BORDO", $"Con i nostri scudi a terra, la {Name(e)} ci manda una squadra d'abbordaggio: {r.members.Count} incursori si materializzano al teletrasporto. Respingeteli prima che sabotino la nave!");
                break;
            }
        }

        // ---- nostri assalti
        sealed class Assault { public int target; public float start, nextSab, nextLoot; public int kills, need, loot; public HashSet<int> alive = new(); public bool done; }
        static Assault ours;
        public static string Board()
        {
            var me = Stations.MyShip; PLShipInfoBase t = null; try { t = me.TargetShip; } catch { }
            if (t == null) return "Nessun bersaglio agganciato.";
            bool hostile = false; try { hostile = t.HostileShips.Contains(me.ShipID) || me.HostileShips.Contains(t.ShipID) || (t.TargetShip != null && t.TargetShip.ShipID == me.ShipID); } catch { }
            var (ok, why) = CanTeleport(me, t); if (!ok) return "Abbordaggio impossibile: " + why + ".";
            if (!hostile) { Saga.State.infamia += 2; Wanted.Heat(8); try { me.AddHostileShip(t); t.AddHostileShip(me); } catch { } }
            var crew = CrewOf(t);
            ours = new Assault { target = t.ShipID, start = Time.time, nextSab = Time.time + 8f, nextLoot = Time.time + 12f, need = Math.Max(2, crew.Count), alive = crew.Select(p => p.GetPlayerID()).ToHashSet() };
            try { PLServer.Instance.CaptainSetOrderID(6); } catch { }
            return $"Squadra d'abbordaggio al teletrasporto: obiettivo la {Name(t)}, {crew.Count} uomini d'equipaggio da neutralizzare." + (hostile ? "" : " Abbordare una nave che non ci attaccava è pirateria.");
        }
        public static string Recall()
        {
            var me = Stations.MyShip; try { PLServer.Instance.CaptainSetOrderID(3); } catch { }
            int n = 0; foreach (var p in CrewOf(me)) if ((p.SubHubID & 0x7FFFFFFF) != SubHubOf(me) && Send(p, me)) n++;
            ours = null; return n > 0 ? $"Squadra richiamata: {n} rientrano." : "Squadra richiamata ai posti di combattimento.";
        }
        static void Ours(float now)
        {
            var a = ours; if (a == null || a.done) return; var me = Stations.MyShip;
            var t = PLEncounterManager.Instance.GetShipFromID(a.target);
            var team = CrewOf(me); int tsub = SubHubOf(t);
            var aboard = team.Where(p => (p.SubHubID & 0x7FFFFFFF) == tsub).ToList();
            if (t == null || t.HasBeenDestroyed)
            {
                if (aboard.Count > 0) { Morale.Change(-5, null); foreach (var p in aboard) { var o = Saga.State.officers.FirstOrDefault(x => x.alive && x.cls == p.GetClassID()); if (o != null) Officers.Wound(o, 3); } }
                Director.Notify("ABBORDAGGIO", aboard.Count > 0 ? "La nave è esplosa con la nostra squadra ancora a bordo: i teletrasporti d'emergenza ne salvano la pelle, ma tornano feriti." : "Il bersaglio è stato distrutto: abbordaggio concluso.");
                ours = null; return;
            }
            // abbattimenti: chi dell'equipaggio nemico muore mentre siamo a bordo
            foreach (var id in a.alive.ToList()) { var p = ById(id); if (!Alive(p)) { a.alive.Remove(id); if (aboard.Count > 0) a.kills++; } }
            foreach (var p in CrewOf(t)) if (Alive(p)) a.alive.Add(p.GetPlayerID());
            // feriti della nostra squadra
            foreach (var p in team) if (!Alive(p) && p.IsBot) { var o = Saga.State.officers.FirstOrDefault(x => x.alive && x.cls == p.GetClassID() && x.injuredUntil <= Obscured.Jumps()); if (o != null && now - a.start > 5f) { Officers.Wound(o, 2); Stations.LogEvent(me.ShipID, $"Abbordaggio: {o.first} {o.last} ferito, riportato a bordo"); } }
            if (aboard.Count == 0) return;
            if (now >= a.nextSab) { a.nextSab = now + 6f; foreach (var _ in aboard) { var sy = Stations.SysIds[rng.Next(Stations.SysIds.Length)]; Stations.Set(t.ShipID, "dmx_" + sy, Math.Min(95f, Stations.Get(t.ShipID, "dmx_" + sy) + 8f + 3f * Talents.Ours("arrembaggio"))); } }
            if (now >= a.nextLoot) { a.nextLoot = now + 10f; int cr = (int)(aboard.Count * (40 + rng.Next(80)) * Talents.Mul("arrembaggio", 0.3f) * (1f + Loot.Mine("arrembaggio"))); a.loot += cr; try { PLServer.Instance.CurrentCrewCredits += cr; } catch { } }
            if (a.kills >= a.need)
            {
                a.done = true; Captured++; Feats.Capture();
                Director.Notify("NAVE CATTURATA", $"L'equipaggio della {Name(t)} è stato neutralizzato: la nave è nostra. Bottino dalle casse: {a.loot} crediti.");
                Officers.Credit(3, 3); Competitors.Renown(15); Morale.Change(4, null);
                Queue("nave_catturata", t.ShipID.ToString());
            }
        }
        // Se c'è già un incontro aperto, questo aspetta il suo turno.
        static void Queue(string id, string arg) { if (!Encounters.Start(id, true, null, "", arg)) Saga.Later("incontro", 0, 0, id, arg); }
        public static void Release(PLShipInfoBase t) { ours = null; try { PLServer.Instance.CaptainSetOrderID(3); } catch { } var me = Stations.MyShip; foreach (var p in CrewOf(me)) if ((p.SubHubID & 0x7FFFFFFF) != SubHubOf(me)) Send(p, me); }

        // ---- predoni contro mercanti (traffico): stesse regole
        public static bool Pirates(PLShipInfoBase raider, PLShipInfoBase victim)
        {
            var (ok, _) = CanTeleport(raider, victim); if (!ok) return false;
            var crew = CrewOf(raider).Where(p => (p.SubHubID & 0x7FFFFFFF) == SubHubOf(raider)).Take(2).ToList(); if (crew.Count == 0) return false;
            int n = crew.Count(p => Send(p, victim)); return n > 0;
        }
        public static bool Overrun(PLShipInfoBase victim)
        {
            int sub = SubHubOf(victim); if (sub < 0) return false;
            var aboard = AboardOf(victim); if (aboard.Count == 0) return false;
            int own = aboard.Count(p => p.StartingShip != null && p.StartingShip.ShipID == victim.ShipID);
            return own == 0;
        }

        static float nextTick;
        public static void Tick()
        {
            if (!Enabled || !Host || !Saga.Loaded || Stations.MyShip == null || Time.time < nextTick) return;
            nextTick = Time.time + 1f;
            try { Incoming(Time.time); Ours(Time.time); }
            catch (Exception e) { Plugin.L.LogWarning("Abbordaggi: " + e.Message); }
        }

        public static string Short()
        {
            if (!Host) return GalaxyView.Current?.boarding ?? "";
            var me = Stations.MyShip; var bits = new List<string>();
            if (incoming != null) { int sub = SubHubOf(me); int n = incoming.members.Select(ById).Count(p => Alive(p) && (p.SubHubID & 0x7FFFFFFF) == sub); bits.Add($"[FF8A7A]INTRUSI A BORDO {n}[-]"); }
            if (ours != null && !ours.done) { var t = PLEncounterManager.Instance.GetShipFromID(ours.target); int tsub = SubHubOf(t); int n = CrewOf(me).Count(p => (p.SubHubID & 0x7FFFFFFF) == tsub); bits.Add($"[F2A65A]abbordaggio: {n} a bordo, nemici abbattuti {ours.kills}/{ours.need}[-]"); }
            return string.Join("  ", bits);
        }
        public static string Status()
        {
            var me = Stations.MyShip; PLShipInfoBase t = null; try { t = me.TargetShip; } catch { }
            if (ours != null && !ours.done) return Short();
            if (t == null) return "[8b90a0]Agganciate una nave: si abborda con gli scudi del bersaglio a terra.[-]";
            var (ok, why) = CanTeleport(me, t); return ok ? $"[7CD992]{Name(t)}: abbordaggio possibile[-]" : $"[8b90a0]{why}[-]";
        }
        public static void Request(string op)
        {
            if (Host) { Execute(op); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|abbordaggio|{op}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var p = msg.Split('|'); if (p.Length >= 3 && Host) Execute(p[2]); }
        static void Execute(string op) { string r = op == "abborda" ? Board() : op == "rientro" ? Recall() : ""; if (r.Length > 0) { Stations.Announce("ABBORDAGGIO", r); GalaxyView.Dirty(); } }
        public static string Describe() => $"incursioni respinte {RaidsRepelled}, perse {RaidsLost}, navi catturate {Captured}; {Short()}";
        public static string ForceIncoming(int enemyId)
        {
            var e = PLEncounterManager.Instance.GetShipFromID(enemyId); var me = Stations.MyShip; if (e == null) return "nave non trovata";
            var crew = CrewOf(e).Take(3).ToList(); var r = new Raid { origin = e.ShipID, start = Time.time, nextSab = Time.time + 8f };
            foreach (var p in crew) if (Send(p, me)) r.members.Add(p.GetPlayerID());
            incoming = r; return $"incursione forzata: {r.members.Count} a bordo";
        }
    }
}

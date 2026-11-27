// Vite parallele: i capitani NPC conosciuti vivono anche quando non li vediamo. A ogni salto i mercanti commerciano,
// si arricchiscono e rinnovano la nave, a volte vengono assaltati e scampano (o no); i pirati razziano e a volte
// finiscono abbattuti dalle pattuglie; le pattuglie danno la caccia ai pirati; i veterani si ritirano. Ogni capitano
// tiene un registro delle sue imprese; le storie migliori arrivano nel notiziario, e la morte di un amico ci tocca.
using System;
using System.Linq;

namespace Melchior
{
    // Caccia a un capitano: taglia della Flotta o vendetta per un amico.
    public class CaptainHunt { public string name = "", ship = "", friend = ""; public int level, sector = -1, reward, until, shipId = -1; public bool accepted, spawned, vendetta; }
    // Prestito a un capitano amico: si restituisce con gli interessi, se sopravvive e se gli affari vanno.
    public class Loan { public string name = "", ship = ""; public int amount, due, delays; }

    public static class Lives
    {
        static readonly Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static int lastJump = -1;
        public static int Events, Deaths, Retired;

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                int j = Obscured.Jumps(); if (j == lastJump) return; bool first = lastJump < 0; lastJump = j; if (first) return;
                // anche le conoscenze del traffico hanno un capitano con la sua vita
                foreach (var f in Saga.State.friends.Where(f => f.alive)) Talents.Register(f.captain, f.ship, 1, f.job is "corriere" ? 1 : 3, f.job);
                var present = PLEncounterManager.Instance.AllShips; var here = new System.Collections.Generic.HashSet<string>();
                foreach (var kv in present) try { here.Add((string)kv.Value.ShipName); } catch { }
                foreach (var c in Talents.Known.Where(c => c.alive && !c.retired && !here.Contains(c.ship)).ToList())
                    if (rng.NextDouble() < 0.25) Live(c, j);
                Loans(j);
                if (Saga.State.hunt == null && rng.NextDouble() < 0.07) OfferBounty();
                if (Saga.State.hunt != null && j > Saga.State.hunt.until) { var h = Saga.State.hunt; Saga.State.hunt = null; if (h.accepted) Director.Notify("CACCIA FINITA", $"Le tracce della «{h.ship}» di {h.name} si sono raffreddate: è sparita nella Frontiera. Per ora."); }
                Saga.MarkDirty(); GalaxyView.Dirty();
            }
            catch (Exception e) { Plugin.L.LogWarning("Vite parallele: " + e.Message); }
        }

        static void Live(NpcCaptain c, int j)
        {
            Events++;
            bool friend = Saga.State.friends.Any(f => f.alive && f.ship == c.ship && f.favor >= 2);
            string who = $"La «{c.ship}» di {c.name}";
            switch (c.job)
            {
                case "mercante": case "corriere": case "rifornitore": case "minatore": case "recuperatore":
                {
                    double r = rng.NextDouble();
                    if (r < 0.7) { int g = 200 + rng.Next(600) * c.level; c.wealth += g; Xp(c, 10, null); Talents.Life(c, $"buoni affari, {g} crediti"); }
                    else if (r < 0.98)
                    {
                        if (rng.NextDouble() < 0.75) { Xp(c, 15, null); Talents.Life(c, "nave sfuggita ai predoni"); if (rng.NextDouble() < 0.2) News.Post("commercio", $"{who} è sfuggita ai predoni per un soffio."); }
                        else { c.wealth = Math.Max(0, c.wealth - 1500); Talents.Life(c, "stiva svuotata dai predoni"); if (friend && rng.NextDouble() < 0.5 && !Saga.State.loans.Any(l => l.ship == c.ship)) AskLoan(c); if (rng.NextDouble() < 0.3) News.Post("commercio", $"{who} è stata derubata dai predoni. Nessun ferito, ma la stiva è vuota."); }
                    }
                    else { Die(c, "è stata distrutta dai predoni", friend); return; }
                    if (c.wealth > 5000 && c.level < 10) { c.wealth -= 5000; c.level++; Talents.Life(c, "ha rinnovato la nave"); if (c.level >= 4) News.Post("commercio", $"{c.name} ha rinnovato la «{c.ship}» con i guadagni dei suoi commerci."); }
                    break;
                }
                case "predoni": case "pirata": case "cacciatore":
                {
                    double r = rng.NextDouble();
                    if (r < 0.55) { Xp(c, 20, "le sue razzie"); c.wealth += 300 * c.level; Talents.Life(c, "una razzia riuscita"); if (rng.NextDouble() < 0.15) News.Post("taglie", $"{who} ha razziato un convoglio. Le fazioni promettono vendetta."); }
                    else if (r < 0.97) { Talents.Life(c, "si nasconde e ripara la nave"); }
                    else { Die(c, "è stata abbattuta da una pattuglia", false); return; }
                    break;
                }
                case "pattuglia": case "incursore": case "alleato": case "sentinella":
                    if (rng.NextDouble() < 0.99) { Xp(c, 12, "la caccia ai pirati"); Talents.Life(c, "ha dato la caccia ai pirati"); }
                    else { Die(c, "è andata perduta in servizio", false); return; }
                    break;
                default: Xp(c, 8, null); break;
            }
            if (c.level >= 8 && rng.NextDouble() < 0.03)
            {
                c.retired = true; c.alive = false; Retired++; Talents.Life(c, "ritiro dalla Frontiera");
                News.Post("dicerie", $"{c.name}, capitano di livello {c.level} della «{c.ship}», lascia la Frontiera e si ritira. Si dice che abbia una casa vicino a un sole giallo.");
            }
        }
        static void Xp(NpcCaptain c, int xp, string why) { int before = c.level; c.xp += xp; while (c.xp >= 40 * c.level && c.level < 10) { c.xp -= 40 * c.level; c.level++; } if (c.level > before && c.level >= 10) Feats.Legend(); if (c.level > before && c.level >= 5 && why != null) News.Post("dicerie", $"Il nome di {c.name} della «{c.ship}» corre nei porti per {why}: ora è al livello {c.level}."); }
        static void Die(NpcCaptain c, string how, bool friend)
        {
            c.alive = false; Deaths++; Talents.Life(c, how);
            var f = Saga.State.friends.FirstOrDefault(x => x.ship == c.ship && x.alive); if (f != null) f.alive = false;
            if (friend) { Morale.Change(-2, null); Director.Notify("UN AMICO PERDUTO", $"Arriva la notizia: la «{c.ship}» di {c.name} {how}. Ci aveva aiutati, una volta."); if (how.Contains("predoni")) Vendetta(c); }
            else if (c.level >= 4 || rng.NextDouble() < 0.3) News.Post(c.job is "predoni" or "pirata" ? "taglie" : "commercio", $"La «{c.ship}» di {c.name} {how}.");
        }

        // ---- taglie e vendette
        static int SectorAround(int min, int max)
        {
            var cur = PLServer.GetCurrentSector(); var r1 = Market.Within(cur, min - 1); var cand = Market.Within(cur, max).Where(id => !r1.Contains(id)).ToList();
            cand = cand.Where(id => { var s = PLServer.GetSectorWithID(id); return s != null && s.MissionSpecificID == -1; }).ToList();
            return cand.Count == 0 ? -1 : cand[rng.Next(cand.Count)];
        }
        static void OfferBounty()
        {
            var p = Talents.Known.Where(c => c.alive && !c.retired && c.job is "predoni" or "pirata" && c.level >= 3 && !c.name.Contains("«")).OrderByDescending(c => c.level).FirstOrDefault(); if (p == null) return;
            int sec = SectorAround(2, 4); if (sec < 0) return;
            Saga.State.hunt = new CaptainHunt { name = p.name, ship = p.ship, level = p.level, sector = sec, reward = (int)((1500 + 700 * p.level) * Rules.Reward), until = Obscured.Jumps() + 10 };
            Queue("taglia_capitano");
        }
        static void Vendetta(NpcCaptain victim)
        {
            if (Saga.State.hunt != null && Saga.State.hunt.accepted) return;
            var p = Talents.Known.Where(c => c.alive && !c.retired && c.job is "predoni" or "pirata" && !c.name.Contains("«")).OrderByDescending(c => c.level).FirstOrDefault();
            int sec = SectorAround(2, 4); if (sec < 0) return;
            if (p == null) { p = Talents.Register(Officers.RandomFirst() + " " + Officers.RandomLast(), "Artiglio di " + victim.name.Split(' ')[0], 3 + rng.Next(3), 0, "predoni"); }
            Saga.State.hunt = new CaptainHunt { name = p.name, ship = p.ship, level = p.level, sector = sec, reward = (int)((1000 + 500 * p.level) * Rules.Reward), until = Obscured.Jumps() + 12, vendetta = true, friend = victim.name };
            Queue("vendetta");
        }
        static void Queue(string id) { if (!Encounters.Start(id, true)) Saga.Later("incontro", 0, 1, id); }
        public static string AcceptHunt()
        {
            var h = Saga.State.hunt; if (h == null) return "La caccia non c'è più.";
            h.accepted = true; Course.Plot(h.sector); Saga.MarkDirty(); GalaxyView.Dirty();
            return $"Rotta tracciata verso il settore {Places.Of(h.sector)}: lì è stata vista la «{h.ship}» di {h.name}, livello {h.level}.";
        }
        public static void DropHunt() { Saga.State.hunt = null; Saga.MarkDirty(); GalaxyView.Dirty(); }
        static float nextHunt;
        public static void HuntTick()
        {
            var h = Saga.State.hunt; if (h == null || !h.accepted || UnityEngine.Time.time < nextHunt) return; nextHunt = UnityEngine.Time.time + 1f;
            var me = Stations.MyShip; bool warp = false; try { warp = me.InWarp; } catch { }
            int sec = -1; try { sec = PLServer.GetCurrentSector().ID; } catch { }
            if (warp) { if (h.spawned) { h.spawned = false; h.sector = SectorAround(1, 2); if (h.sector >= 0) { Course.Clear(); Course.Plot(h.sector); Director.Notify("CACCIA", $"La «{h.ship}» è fuggita: l'hanno vista dirigersi verso il settore {Places.Of(h.sector)}."); } else Saga.State.hunt = null; Saga.MarkDirty(); } return; }
            if (!h.spawned && sec == h.sector)
            {
                var s = Traffic.SpawnHunted(h.ship, h.level);
                if (s != null) { h.spawned = true; h.shipId = s.ShipID; Saga.MarkDirty(); Director.Notify(h.vendetta ? "VENDETTA" : "TAGLIA", $"Eccola: la «{h.ship}» di {h.name}, livello {h.level}, esce dalla nebbia dei sensori." + (h.vendetta ? $" Per {h.friend}." : "")); }
                return;
            }
            if (h.spawned)
            {
                PLShipInfoBase t = null; try { t = PLEncounterManager.Instance.GetShipFromID(h.shipId); } catch { }
                if (t == null || t.HasBeenDestroyed)
                {
                    Saga.State.hunt = null; Course.Clear();
                    try { PLServer.Instance.CurrentCrewCredits += h.reward; } catch { }
                    var c = Talents.Known.FirstOrDefault(x => x.ship == h.ship && x.name == h.name); if (c != null) { c.alive = false; Talents.Life(c, "nave abbattuta dalla nostra per la taglia"); }
                    Competitors.Renown(10 + h.level * 3); Saga.State.onore += 1; CrewLives.OnHunt(); Morale.Change(h.vendetta ? 8 : 3, null); Feats.Hunt(h.vendetta);
                    Director.Notify(h.vendetta ? "VENDETTA COMPIUTA" : "TAGLIA RISCOSSA", $"La «{h.ship}» di {h.name} non razzierà più nessuno. {h.reward} crediti" + (h.vendetta ? $", e la memoria di {h.friend} è vendicata." : "."));
                    News.Post("taglie", $"La «{(string)me.ShipName}» ha abbattuto la «{h.ship}» di {h.name}, pirata di livello {h.level}.");
                    Saga.MarkDirty(); GalaxyView.Dirty();
                }
            }
        }

        // ---- prestiti
        static void AskLoan(NpcCaptain c) { if (Saga.State.loans.Count >= 3) return; Pending = c; Queue("prestito"); }
        public static NpcCaptain Pending;
        public static string Lend(int amount)
        {
            var c = Pending; if (c == null) return "Non c'è più nessuno da aiutare.";
            int cash = 0; try { cash = PLServer.Instance.CurrentCrewCredits; } catch { } if (cash < amount) return "Non abbiamo tanti crediti.";
            try { PLServer.Instance.CurrentCrewCredits -= amount; } catch { }
            Saga.State.loans.Add(new Loan { name = c.name, ship = c.ship, amount = amount, due = Obscured.Jumps() + 6 + rng.Next(6) }); c.wealth += amount; Pending = null;
            var f = Saga.State.friends.FirstOrDefault(x => x.ship == c.ship); if (f != null) f.favor = Math.Min(10, f.favor + 2); Saga.MarkDirty();
            return $"{amount} crediti alla «{c.ship}». {c.name} promette di restituirli con gli interessi.";
        }
        static void Loans(int j)
        {
            foreach (var l in Saga.State.loans.ToList())
            {
                if (j < l.due) continue;
                var c = Talents.Known.FirstOrDefault(x => x.ship == l.ship && x.name == l.name);
                if (c == null || !c.alive) { Saga.State.loans.Remove(l); Director.Notify("PRESTITO PERDUTO", $"Il prestito a {l.name} non tornerà: la «{l.ship}» non solca più la Frontiera."); continue; }
                if (c.wealth >= l.amount || l.delays >= 2)
                {
                    int back = (int)(l.amount * (c.wealth >= l.amount ? 1.35 : 0.6)); c.wealth = Math.Max(0, c.wealth - back);
                    try { PLServer.Instance.CurrentCrewCredits += back; } catch { }
                    Saga.State.loans.Remove(l); Feats.Loan(back > l.amount);
                    Director.Notify("PRESTITO RESTITUITO", back > l.amount ? $"{l.name} della «{l.ship}» salda il debito con gli interessi: {back} crediti. «Parola di mercante.»" : $"{l.name} della «{l.ship}» non ce la fa: restituisce quello che può, {back} crediti, e chiede scusa.");
                }
                else { l.delays++; l.due = j + 4; Stations.LogEvent(Stations.MyShip.ShipID, $"{l.name} chiede ancora qualche salto per restituire il prestito"); }
            }
        }

        public static string Line()
        {
            var h = Saga.State.hunt; var bits = new System.Collections.Generic.List<string>();
            if (h != null && h.accepted) bits.Add($"{(h.vendetta ? "vendetta" : "taglia")}: «{h.ship}» al settore {h.sector}");
            if (Saga.State.loans.Count > 0) bits.Add($"prestiti {Saga.State.loans.Sum(l => l.amount)} cr");
            return string.Join(", ", bits);
        }
        public static string Describe() => $"vite parallele: capitani noti {Talents.Known.Count(c => c.alive)} vivi, {Talents.Known.Count(c => !c.alive)} morti o ritirati; eventi {Events}, morti {Deaths}, ritiri {Retired}";
        public static string ForceHunt(bool vendetta) { if (vendetta) { var v = Talents.Known.FirstOrDefault(c => c.alive) ?? Talents.Register("Ada Ferri", "Pellicano", 2, 3, "mercante"); Vendetta(v); } else OfferBounty(); return Saga.State.hunt == null ? "nessun pirata noto da cacciare" : $"caccia: {Saga.State.hunt.ship} di {Saga.State.hunt.name} al settore {Saga.State.hunt.sector}"; }
        public static string ForceLoan() { var c = Talents.Known.FirstOrDefault(x => x.alive && x.job is "mercante" or "corriere") ?? Talents.Register("Ada Ferri", "Pellicano", 2, 3, "mercante"); AskLoan(c); return "prestito chiesto da " + c.name; }
        public static string Force(int n) { int j = Obscured.Jumps(); for (int i = 0; i < n; i++) foreach (var c in Talents.Known.Where(c => c.alive && !c.retired).ToList()) Live(c, j); return Describe(); }
    }
}

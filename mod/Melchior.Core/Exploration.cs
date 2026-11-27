// Esplorazione: circa un settore aperto su quattro nasconde un luogo (relitto antico, rovine, giacimento, anomalia stabile,
// cimitero di navi, faro alieno, deposito nascosto). È deterministico per galassia e settore: chi torna nello stesso
// settore ritrova lo stesso luogo, finché non è stato esplorato. All'arrivo la Scienza ne rileva le tracce e il capitano
// sceglie come esplorarlo (Encounters.cs). Le scoperte finiscono nell'Atlante; i fari e alcuni relitti danno frammenti di
// mappa: con tre frammenti si rivela un luogo leggendario lontano, con rotta tracciata, un guardiano e un leggendario.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public class Discovery { public long t; public int sector; public string kind = "", name = ""; }
    public class LegendSite { public int sector = -1; public string name = ""; }

    public static class Exploration
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        static SagaState St => Saga.State;

        static readonly (string kind, double w)[] Kinds = { ("relitto", 3), ("rovine", 2), ("giacimento", 2.5), ("anomalia", 2), ("cimitero", 1.5), ("faro", 1.2), ("deposito", 2) };
        public static string KindLabel(string k) => k switch { "relitto" => "relitto antico", "rovine" => "rovine", "giacimento" => "giacimento", "anomalia" => "anomalia stabile", "cimitero" => "cimitero di navi", "faro" => "faro alieno", "deposito" => "deposito nascosto", _ => k };
        static readonly string[] Legends = { "la Biblioteca degli Antichi", "il Cantiere Perduto di Teth", "la Tomba dell'Ammiraglio", "il Cuore della Nebulosa", "l'Arca dei Lumeni", "il Trono dei Costruttori" };

        // Luogo del settore: deterministico da seme della galassia e numero del settore.
        public static (string kind, string name)? PoiOf(int seed, int sector)
        {
            if (Wonders.Remnant(sector)) return ("anomalia", "i resti della supernova");
            var r = new System.Random(unchecked(seed * 31 + sector * 7919 + 17));
            if (r.NextDouble() > 0.27) return null;
            double tot = Kinds.Sum(k => k.w), x = r.NextDouble() * tot; string kind = Kinds[^1].kind;
            foreach (var k in Kinds) { x -= k.w; if (x <= 0) { kind = k.kind; break; } }
            string P(params string[] a) => a[r.Next(a.Length)];
            string name = kind switch
            {
                "relitto" => $"il relitto della «{P("Argo", "Prometeo", "Ultima Thule", "Vigilante", "Aurora Australe", "Stella Polare", "Odissea", "Fenice Nera")}»",
                "rovine" => $"le rovine {P("dei Kethri", "degli Antichi Lumeni", "dei Vel'nari", "dei Costruttori", "di una colonia senza nome", "del Primo Insediamento")}",
                "giacimento" => $"un giacimento di {P("iridio", "cristalli di elio-3", "neutronio compresso", "palladio", "ghiaccio di deuterio")}",
                "anomalia" => P("una lente gravitazionale stabile", "una sacca di spazio piegato", "una pulsar in miniatura", "una fenditura subspaziale quieta", "un campo di tachioni immobile"),
                "cimitero" => $"il cimitero di navi {P("di Capo Ombra", "della battaglia di Teth", "della Prima Guerra Coloniale", "della flotta di Halloran", "del Convoglio Perduto")}",
                "faro" => P("un faro alieno antichissimo", "un faro di cristallo sospeso nel vuoto", "un faro di metallo nero, freddo come il vuoto"),
                _ => P("un deposito nascosto dei contrabbandieri", "un deposito militare dimenticato", "un deposito di una gilda scomparsa"),
            };
            return (kind, name);
        }

        static int Seed() { try { return (int)PLServer.Instance.GalaxySeed; } catch { return 0; } }
        static int lastSector = -1, pendingSector = -1; static float pendingAt = -1, pendingUntil;

        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null) return;
            try
            {
                var s = PLServer.GetCurrentSector(); int id = s?.ID ?? -1; if (id < 0) return;
                bool warp = false; try { warp = Stations.MyShip.InWarp; } catch { }
                if (id != lastSector && !warp)
                {
                    lastSector = id;
                    if (s.VisualIndication == ESectorVisualIndication.NONE && s.MissionSpecificID == -1 && !St.explored.Contains(id) && PoiOf(Seed(), id) != null)
                    { pendingSector = id; pendingAt = UnityEngine.Time.time + 5f; pendingUntil = UnityEngine.Time.time + 90f; }
                }
                // la Scienza rileva il luogo poco dopo l'arrivo; se il capitano è occupato con altro, si riprova per un po'
                if (pendingSector == id && UnityEngine.Time.time >= pendingAt)
                {
                    var p = PoiOf(Seed(), id).Value;
                    if (Encounters.Start("luogo_" + p.kind, false, p.kind, p.name)) { pendingSector = -1; Service.Add("esplorazione.rilevamenti", 1); }
                    else if (UnityEngine.Time.time > pendingUntil) pendingSector = -1;
                    else pendingAt = UnityEngine.Time.time + 10f;
                }
                TrackLegend(id, warp);
            }
            catch (Exception e) { Plugin.L.LogWarning("Esplorazione: " + e.Message); }
        }

        public static void Record(string kind, string name, string sector)
        {
            if (!int.TryParse(sector, out int id)) return;
            if (!St.explored.Contains(id)) St.explored.Add(id);
            St.discoveries.Add(new Discovery { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sector = id, kind = kind, name = name });
            if (St.discoveries.Count > 80) St.discoveries.RemoveAt(0);
            St.discovered++; Research.Add(4, "luogo esplorato"); Saga.MarkDirty(); GalaxyView.Dirty(); Competitors.Renown(Calling.Is("fiera") ? 6 : 3); Threat.Add(-1f); Calling.Add("esplorazione");
            Service.Add("esplorazione.scoperte", 1);
            if (St.discovered % 5 == 0) News.Post("scienza", $"L'Accademia delle Scienze cita la «{Ship()}»: {St.discovered} luoghi esplorati e registrati nell'Atlante.");
        }

        public static string Fragment(string from)
        {
            St.fragments++; Saga.MarkDirty(); GalaxyView.Dirty();
            string note = $"frammento di mappa {Math.Min(3, St.fragments)}/3";
            if (St.fragments >= 3 && St.legend == null) { St.fragments -= 3; Reveal(); note = "terzo frammento: la mappa è completa"; }
            return note;
        }

        static void Reveal()
        {
            var cur = PLServer.GetCurrentSector(); var all = new List<PLSectorInfo>();
            foreach (var kv in PLGlobal.Instance.Galaxy.AllSectorInfos) { var s = kv.Value; if (s?.MySPI != null && s.VisualIndication == ESectorVisualIndication.NONE && s.MissionSpecificID == -1 && (cur == null || s.ID != cur.ID)) all.Add(s); }
            var far = all.Where(s => cur == null || Nemesis.Distance(cur, s) is >= 5 and <= 9).ToList(); if (far.Count == 0) far = all; if (far.Count == 0) return;
            var t = far[rng.Next(far.Count)];
            var used = St.discoveries.Where(d => d.kind == "leggenda").Select(d => d.name).ToHashSet();
            var names = Legends.Where(n => !used.Contains(n)).ToList(); if (names.Count == 0) names = Legends.ToList();
            St.legend = new LegendSite { sector = t.ID, name = names[rng.Next(names.Count)] }; St.legendSince = Obscured.Jumps();
            Saga.MarkDirty(); Contracts.Plot(t.ID);
            Director.Notify("MAPPA COMPLETA", $"I tre frammenti combaciano: indicano {St.legend.name}, nel settore {Places.Of(t.ID)}, a {Nemesis.JumpsTo(t.ID)} salti. Rotta tracciata.");
            News.Post("dicerie", $"Si dice che la «{Ship()}» abbia trovato la strada per {St.legend.name}.");
            Saga.Chronicle($"Mappa completa: {St.legend.name}, settore {Places.Of(t.ID)}.");
        }

        // Il luogo leggendario ha un guardiano; abbatterlo vale un pezzo leggendario.
        static readonly List<int> guards = new(); static int guardSector = -1, keeper = -1;
        static void TrackLegend(int sector, bool warp)
        {
            var L = St.legend; if (L == null) return;
            if (guards.Count == 0 && sector == L.sector && !warp)
            {
                var ship = Stations.MyShip; float p = Director.Power(ship, out _, out _);
                var heavy = new[] { "E_DESTROYER", "E_WDCRUISER", "E_CARRIER", "E_ANNIHILATOR" }; var kt = Enum.TryParse<EShipType>(heavy[Math.Clamp((int)(p / 5f), 0, heavy.Length - 1)], out var x) ? x : Director.PickShip(p + 6, 1);
                var k = Director.Spawn(ship, kt, Director.Modifiers(p + 6) | 1 << (int)EShipModifierType.ELITE | 1 << (int)EShipModifierType.ARMORED, "Custode " + Of(L.name));
                if (k == null) return;
                keeper = k.ShipID; guards.Add(k.ShipID); guardSector = sector;
                for (int i = 0; i < 1 + (p >= 10 ? 1 : 0); i++) { var e = Director.Spawn(ship, p < 8 ? EShipType.E_WDDRONE2 : EShipType.E_WDDRONE3, Director.Modifiers(p), "Sentinella"); if (e != null) guards.Add(e.ShipID); }
                Director.Notify("LUOGO LEGGENDARIO", $"Eccolo: {L.name}. E non è incustodito: il suo guardiano si risveglia.");
                return;
            }
            if (guards.Count == 0) return;
            if (sector != guardSector || warp) { guards.Clear(); keeper = -1; return; } // ritirata: il guardiano aspetterà
            guards.RemoveAll(id => { PLShipInfoBase s = null; try { s = PLEncounterManager.Instance.GetShipFromID(id); } catch { } return s == null || s.HasBeenDestroyed || (s.MyStats != null && s.MyStats.HullCurrent <= 0f); });
            if (!guards.Contains(keeper) && keeper >= 0)
            {
                guards.Clear(); keeper = -1;
                var ship = Stations.MyShip;
                var got = Loot.Grant(ship, 7, 7, "luogo leggendario");
                try { PLServer.Instance.CurrentCrewCredits += 3000; } catch { }
                St.onore += 3; St.legendsFound++; Research.Add(20, "luogo leggendario"); Competitors.Renown(60); Threat.Add(-8f, "i segreti del luogo leggendario"); Trophies.Add($"Reliquia {Of(L.name)}", "luogo leggendario"); if (rng.NextDouble() < 0.5) Bases.GiveDeed("laboratorio", "dagli archivi del luogo leggendario"); Legacy.Record("leggenda", $"ha raggiunto {L.name}"); Morale.Change(10, "Abbiamo trovato una leggenda");
                St.discoveries.Add(new Discovery { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sector = L.sector, kind = "leggenda", name = L.name });
                Director.Notify("LUOGO LEGGENDARIO", $"Il guardiano cade. Nel cuore {Of(L.name)} ci aspettava " + (got != null ? got.Name : "un tesoro") + ", e 3000 crediti in reperti.");
                News.Post("scienza", $"La «{Ship()}» ha raggiunto {L.name}. Nei porti non si parla d'altro.");
                Saga.Chronicle($"Raggiunto {L.name}.");
                Service.Add("esplorazione.leggende", 1);
                St.legend = null; Course.Clear(); Saga.MarkDirty(); GalaxyView.Dirty();
            }
        }
        static string Of(string n) => n.StartsWith("la ") ? "della " + n[3..] : n.StartsWith("il ") ? "del " + n[3..] : n.StartsWith("l'") ? "dell'" + n[2..] : "di " + n;
        static string Ship() { try { return (string)Stations.MyShip.ShipName; } catch { return "nostra nave"; } }

        public static string Summary()
        {
            var L = St.legend;
            return $"Trofei {St.trophies.Count} · Atlante {St.discovered} · frammenti {St.fragments}/3" + (L != null ? $" · {L.name}, settore {Places.Of(L.sector)} ({Nemesis.JumpsTo(L.sector)} salti)" : St.legendsFound > 0 ? $" · leggende {St.legendsFound}" : "");
        }
        public static string Describe()
        {
            var cur = PLServer.GetCurrentSector(); var p = cur == null ? null : PoiOf(Seed(), cur.ID);
            return Summary() + $" | qui (settore {cur?.ID}): {(p == null ? "niente" : p.Value.name)}{(cur != null && St.explored.Contains(cur.ID) ? " (esplorato)" : "")}";
        }
        // Solo per le prove: rileva subito il luogo del settore corrente (o ne inventa uno del tipo chiesto).
        public static string Force(string kind)
        {
            var cur = PLServer.GetCurrentSector(); if (cur == null) return "nessun settore";
            var p = PoiOf(Seed(), cur.ID); if (kind != null || p == null) p = (kind ?? "faro", kind == "relitto" ? "il relitto della «Argo»" : "un faro alieno antichissimo");
            return Encounters.Start("luogo_" + p.Value.kind, true, p.Value.kind, p.Value.name) ? Encounters.Describe() : "c'è già un incontro in corso";
        }
        public static string ForceLegend() { if (St.legend == null) { St.fragments = 3; Fragment("prova"); } else St.legend.sector = PLServer.GetCurrentSector().ID; return Summary(); }
    }
}

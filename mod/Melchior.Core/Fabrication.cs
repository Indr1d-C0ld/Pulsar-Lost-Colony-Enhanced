// Fabbricazione a bordo. L'officina trasforma le materie prime della stiva (le merci della borsa) in ciò che serve
// davvero: capsule di carburante per la curvatura, refrigerante per il nucleo, ricambi per la manutenzione, razioni e
// farmaci per la dispensa, vino da vendere, sonde da lanciare gratis e, con la ricerca giusta, componenti nuovi per la
// nave (la loro qualità cresce con l'esperienza dell'ingegnere). Le lavorazioni richiedono tempo e si mettono in coda;
// vanno più veloci con un ingegnere alla stazione (umano o bot, e più è esperto meglio è) e con i tecnici in viaggio
// come passeggeri; in battaglia si fermano. Gira sull'host; la scheda Fabbricazione dell'Ingegneria la comanda.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Melchior
{
    public class FabJob { public string recipe = ""; public float done; }
    public class FabState { public List<FabJob> queue = new(); public int made, fuel, components, probes; public Dictionary<string, int> byRecipe = new(); }

    public static class Fabrication
    {
        static readonly System.Random rng = new();
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        public static FabState F => Saga.State.fab ??= new FabState();

        public sealed class Recipe { public string id = "", name = "", output = "", needs = ""; public (string good, int n)[] inputs = Array.Empty<(string, int)>(); public float seconds; }
        public static readonly Recipe[] Recipes =
        {
            new() { id = "carburante", name = "Capsula di carburante", output = "1 capsula di carburante per la curvatura", inputs = new[] { ("deuterio", 2) }, seconds = 150 },
            new() { id = "refrigerante", name = "Refrigerante del nucleo", output = "refrigerante +40%", inputs = new[] { ("deuterio", 1), ("minerali", 1) }, seconds = 90 },
            new() { id = "ricambi", name = "Ricambi", output = "3 ricambi in stiva", inputs = new[] { ("minerali", 2), ("elettronica", 1) }, seconds = 120 },
            new() { id = "razioni", name = "Razioni", output = "10 razioni nella dispensa", inputs = new[] { ("grano", 2) }, seconds = 60 },
            new() { id = "farmaci", name = "Farmaci", output = "4 dosi in infermeria", inputs = new[] { ("grano", 1), ("elettronica", 1) }, seconds = 150, needs = "medicina" },
            new() { id = "vino", name = "Vino di bordo", output = "1 cassa di vino da vendere", inputs = new[] { ("grano", 3) }, seconds = 360 },
            new() { id = "sonda", name = "Sonda di ricognizione", output = "1 lancio di sonda gratuito", inputs = new[] { ("elettronica", 1), ("ricambi", 1) }, seconds = 120 },
            new() { id = "componente", name = "Componente della nave", output = "un componente nuovo, della qualità dell'ingegnere", inputs = new[] { ("ricambi", 3), ("elettronica", 2) }, seconds = 300, needs = "automazione" },
        };
        public static Recipe R(string id) => Recipes.FirstOrDefault(r => r.id == id);
        public static string Inputs(Recipe r) => string.Join(" + ", r.inputs.Select(i => $"{i.n} {Market.Low(Market.G(i.good)?.name ?? i.good)}"));
        public static bool Unlocked(Recipe r) => r.needs.Length == 0 || Research.Has(r.needs);
        static bool Afford(Recipe r) => r.inputs.All(i => Market.Have(i.good) >= i.n);

        // velocità dell'officina: un ingegnere alla stazione (più esperto, più veloce) e i tecnici in viaggio
        public static float Speed()
        {
            float s = 0.6f;
            try { var p = PLServer.Instance.GetCachedFriendlyPlayerOfClass(4, Stations.MyShip); if (p != null) s = p.IsBot ? 0.9f + 0.05f * CrewMind.Level(Saga.State.officers.FirstOrDefault(o => o.alive && o.cls == 4), "riparazioni") : 1.1f; } catch { }
            if (Passengers.Has("tecnici")) s *= 1.3f;
            return s;
        }
        public static string Enqueue(string id)
        {
            var r = R(id); if (r == null) return "";
            if (!Unlocked(r)) return $"Per {r.name.ToLowerInvariant()} serve il progetto di ricerca «{Research.Projects.FirstOrDefault(p => p.id == r.needs)?.name ?? r.needs}».";
            if (F.queue.Count >= 4) return "La coda dell'officina è piena (quattro lavorazioni).";
            if (!Afford(r)) return $"Mancano le materie prime: servono {Inputs(r)}.";
            foreach (var (g, n) in r.inputs) Market.Take(g, n); // le materie prime si prelevano subito: sono sul banco
            F.queue.Add(new FabJob { recipe = id }); Saga.MarkDirty(); GalaxyView.Dirty();
            return $"In coda: {r.name.ToLowerInvariant()} ({Inputs(r)}).";
        }
        public static string Cancel()
        {
            var j = F.queue.LastOrDefault(); if (j == null) return "La coda è vuota."; var r = R(j.recipe);
            F.queue.Remove(j); if (r != null) foreach (var (g, n) in r.inputs) Market.Add(g, n); Saga.MarkDirty(); GalaxyView.Dirty();
            return $"Annullato: {r?.name.ToLowerInvariant()}. Le materie prime tornano in stiva.";
        }

        static float nextTick;
        public static void Tick()
        {
            if (!Host || !Saga.Loaded || Stations.MyShip == null || F.queue.Count == 0) return;
            float now = Time.time; if (now < nextTick) return; nextTick = now + 5f;
            try
            {
                var me = Stations.MyShip; bool fight = false; try { var t = me.TargetShip; fight = me.m_AlertLevel >= 2 || (t != null && !t.HasBeenDestroyed && t.HostileShips.Contains(me.ShipID)); } catch { }
                if (fight) return; // in battaglia l'officina si ferma
                var j = F.queue[0]; var r = R(j.recipe); if (r == null) { F.queue.RemoveAt(0); return; }
                j.done += 5f * Speed(); GalaxyView.Dirty();
                if (j.done >= r.seconds) { F.queue.RemoveAt(0); Finish(r); Saga.MarkDirty(); }
            }
            catch (Exception e) { Plugin.L.LogWarning("Fabbricazione: " + e.Message); nextTick = Time.time + 30f; }
        }
        static void Finish(Recipe r)
        {
            var me = Stations.MyShip; var f = F; string got = r.output;
            switch (r.id)
            {
                case "carburante": try { me.NumberOfFuelCapsules = me.NumberOfFuelCapsules + 1; } catch { } f.fuel++; break;
                case "refrigerante": try { float c = me.ReactorCoolantLevelPercent; me.ReactorCoolantLevelPercent = c > 1.5f ? Math.Min(100f, c + 40f) : Math.Min(1f, c + 0.4f); } catch { } break;
                case "ricambi": { int n = Market.Add("ricambi", 3); got = n > 0 ? $"{n} ricambi in stiva" : "ricambi pronti, ma la stiva è piena: lasciati sul banco"; break; }
                case "razioni": Logistics.Gift(10, 0); break;
                case "farmaci": Logistics.Gift(0, 4); break;
                case "vino": { int n = Market.Add("vino", 1); got = n > 0 ? "1 cassa di vino di bordo" : "il vino è pronto, ma la stiva è piena"; break; }
                case "sonda": f.probes++; break;
                case "componente":
                    {
                        int lvl = CrewMind.Level(Saga.State.officers.FirstOrDefault(o => o.alive && o.cls == 4), "riparazioni");
                        int lo = Math.Min(5, 1 + lvl / 3), hi = Math.Min(7, 3 + lvl / 2); var g = Loot.Grant(me, lo, hi, "officina di bordo"); got = g != null ? g.Name : "un componente";
                        f.components++; Feats.Craft("componente"); break;
                    }
            }
            f.made++; f.byRecipe[r.id] = f.byRecipe.GetValueOrDefault(r.id) + 1;
            Service.Add("fabbricazione.lavorazioni", 1); if (r.id == "carburante") Service.Add("fabbricazione.carburante", 1); Feats.Craft(r.id);
            Officers.Credit(4, 1);
            Stations.LogEvent(me.ShipID, $"Officina: pronto {r.name.ToLowerInvariant()} ({got}).");
            GalaxyView.Dirty();
        }
        // una sonda fabbricata a bordo si lancia senza pagare
        public static bool TakeProbe() { if (!Host || F.probes <= 0) return false; F.probes--; Saga.MarkDirty(); GalaxyView.Dirty(); return true; }

        // ---------------------------------------------------------------- pannello
        public class Row { public string id = "", name = "", inputs = "", output = "", needs = ""; public int seconds; public bool unlocked, afford; }
        public class Panel { public List<Row> recipes = new(); public List<string> queue = new(); public float progress; public int made, probes, fuelCaps; public float speed; public string stock = ""; }
        public static Panel Snapshot()
        {
            var p = new Panel { made = F.made, probes = F.probes, speed = Speed() };
            try { p.fuelCaps = Stations.MyShip.NumberOfFuelCapsules; } catch { }
            foreach (var r in Recipes) p.recipes.Add(new Row { id = r.id, name = r.name, inputs = Inputs(r), output = r.output, seconds = (int)r.seconds, unlocked = Unlocked(r), afford = Afford(r), needs = r.needs.Length > 0 ? Research.Projects.FirstOrDefault(x => x.id == r.needs)?.name ?? r.needs : "" });
            foreach (var j in F.queue) p.queue.Add(R(j.recipe)?.name ?? j.recipe);
            if (F.queue.Count > 0) { var r = R(F.queue[0].recipe); p.progress = r == null ? 0f : Math.Min(1f, F.queue[0].done / r.seconds); }
            p.stock = string.Join(" · ", new[] { "deuterio", "minerali", "grano", "elettronica", "ricambi" }.Select(g => $"{Market.Low(Market.G(g)?.name ?? g)} {Market.Have(g)}"));
            return p;
        }
        public static void Request(string op, string arg)
        {
            if (Host) { var t = op == "annulla" ? Cancel() : Enqueue(arg); if (t.Length > 0) Stations.Announce("OFFICINA", t); return; }
            try { PhotonNetwork.RaiseEvent(Stations.EventCode, (Il2CppSystem.Object)$"req|fabbrica|{op}|{arg}", true, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }); } catch { }
        }
        public static void OnRequest(string msg) { var q = msg.Split('|'); if (q.Length >= 4 && Host) Request(q[2], q[3]); }
        public static string Describe() { var f = F; return $"officina: coda [{string.Join(", ", f.queue.Select(j => $"{j.recipe} {j.done:0}/{R(j.recipe)?.seconds:0}s"))}], velocità {Speed():0.00}, lavorazioni {f.made}, capsule fatte {f.fuel}, componenti {f.components}, sonde pronte {f.probes}; capsule a bordo {(Stations.MyShip != null ? Stations.MyShip.NumberOfFuelCapsules : 0)}"; }
        public static string Rush() { foreach (var j in F.queue) { var r = R(j.recipe); if (r != null) j.done = r.seconds - 1f; } return Describe(); }
    }
}

// Collaudo interno del sistema di rarità: gira una volta, a gioco avviato, se Debug.RaritySelfTest è attivo.
using System;
using System.Linq;

namespace Melchior
{
    static class SelfTest
    {
        static bool done; static System.Diagnostics.Stopwatch up;

        static bool shipDone; static System.Diagnostics.Stopwatch aboard;

        // Prova sulla nave vera: etichetta i componenti montati e misura l'effetto degli affissi sulle statistiche.
        static void ShipTick()
        {
            if (shipDone || !Plugin.RarityShipTest.Value) return;
            var ship = PLNetworkManager.Instance?.LocalPlayer?.StartingShip;
            var st = ship?.MyStats;
            if (st == null) { aboard = null; return; }
            aboard ??= System.Diagnostics.Stopwatch.StartNew();
            if (aboard.ElapsedMilliseconds < 15000) return;
            shipDone = true;
            try
            {
                var L = Plugin.L;
                long c0 = Rarity.CalcCalls;
                string Snap() => $"scudi {st.ShieldsMax:0.0} ricarica {st.ShieldsChargeRateMax:0.00} scafo {st.HullMax:0.0} reattore {st.ReactorOutputMax:0.0} firmaEM {st.EMSignature:0.00} spinta {st.ThrustOutputMax:0.00}";
                st.CalculateStats();
                L.LogInfo($"[nave] {ship.ShipNameValue}: CalculateStats chiamata {Rarity.CalcCalls} volte in {aboard.ElapsedMilliseconds / 1000}s a bordo");
                L.LogInfo("[nave] prima:  " + Snap());
                var list = st.m_AllComponents; int tagged = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null || !c.IsEquipped) continue;
                    uint h = c.getHash(); int slot = (int)(h & 0x3F);
                    if (slot != 1 && slot != 3 && slot != 6 && slot != 9) continue;
                    var tag = Rarity.MakeTag(h, 7, 0); // grado massimo, senza livelli: isola l'effetto degli affissi
                    Rarity.Register(c.Pointer, tag);
                    tagged++;
                    L.LogInfo($"[nave]   {c.Name} | " + Rarity.Describe(tag).Replace("\n", " | "));
                }
                st.CalculateStats();
                L.LogInfo("[nave] dopo:   " + Snap());
                L.LogInfo($"[nave] componenti etichettati: {tagged}. Restano etichettati per l'ispezione a schermo.");
                // Un Prototipo in stiva, per vederne nome e descrizione nei menu.
                var gift = PLShipComponent.CreateShipComponentFromHash((int)(PLShipComponent.createHashFromInfo(3, 2, 1, 0, 3) | (4u << 29)), null);
                var added = st.AddShipComponent(gift, -1, ESlotType.E_COMP_CARGO);
                L.LogInfo($"[nave] aggiunto in stiva: {(added == null ? "FALLITO" : added.Name)} hash {gift.getHash():X8}");
            }
            catch (Exception e) { Plugin.L.LogError("[nave] prova fallita: " + e); }
        }

        public static void Tick()
        {
            ShipTick();
            if (done || !Plugin.RaritySelfTest.Value) return;
            up ??= System.Diagnostics.Stopwatch.StartNew();
            if (up.ElapsedMilliseconds < 12000) return;
            done = true;
            try { Run(); } catch (Exception e) { Plugin.L.LogError("[rarità] collaudo fallito: " + e); }
        }

        static void Run()
        {
            var L = Plugin.L; int fail = 0;
            void Check(bool ok, string what) { if (!ok) { fail++; L.LogError("[rarità] NO  " + what); } else L.LogInfo("[rarità] ok  " + what); }

            // 1. Andata e ritorno dell'hash per ogni grado, su quattro tipi di componente.
            foreach (int slot in new[] { 3, 1, 6, 10 })
            {
                var plain = PLShipComponent.CreateShipComponentFromHash((int)PLShipComponent.createHashFromInfo(slot, 0, 2, 0, slot), null);
                if (plain == null) { Check(false, $"tipo {slot}: creazione base"); continue; }
                uint body = plain.getHash(); // forma canonica: il gioco riempie da sé il dato extra
                int basePrice = plain.GetScaledMarketPrice(false);
                for (int t = 0; t < Rarity.Cfg.Tiers.Length; t++)
                {
                    uint h = body | ((uint)t << 29);
                    var c = PLShipComponent.CreateShipComponentFromHash((int)h, null);
                    uint back = c.getHash();
                    var def = Rarity.Tier(t);
                    bool ok = back == h && c.Level == 2 + def.LevelBonus && c.GetLevel() == "3";
                    Check(ok, $"tipo {slot} grado {t}: hash {h:X8}->{back:X8} livello reale {c.Level} mostrato {c.GetLevel()} prezzo {c.GetScaledMarketPrice(false)} (base {basePrice}) nome \"{c.Name}\"");
                    if (t == 4 || t == 7) L.LogInfo("[rarità]     descrizione: " + (c.Desc ?? "").Replace("\n", " | "));
                    // Stesso hash, stessi affissi.
                    var twin = PLShipComponent.CreateShipComponentFromHash((int)h, null);
                    var a = Rarity.Get(c.Pointer); var b = Rarity.Get(twin.Pointer);
                    if (t > 0) Check(a != null && b != null && a.Affix.SequenceEqual(b.Affix) && a.Value.SequenceEqual(b.Value) && a.Affix.Length == Math.Min(def.Affixes, a.Affix.Length), $"tipo {slot} grado {t}: affissi deterministici ({a?.Affix.Length})");
                }
            }

            // 2. Distribuzione delle estrazioni.
            foreach (double luck in new[] { 1.0, 1.25, 1.6 })
            {
                var rng = new Random(7); var n = new int[Rarity.Cfg.Tiers.Length];
                for (int i = 0; i < 100000; i++) n[Rarity.Roll(rng, luck)]++;
                L.LogInfo($"[rarità] fortuna {luck:0.00}: " + string.Join("  ", n.Select((v, i) => $"{Rarity.Tier(i).Name} {v / 1000.0:0.0}%")));
            }

            // 3. Generazione casuale reale del gioco: stesso seme, stesso risultato.
            int upgraded = 0, same = 0; const int N = 300;
            for (int seed = 1; seed <= N; seed++)
            {
                var a = PLReactor.CreateRandom(1f, seed); var b = PLReactor.CreateRandom(1f, seed);
                if (a == null || b == null) continue;
                if (a.getHash() == b.getHash()) same++;
                if (Rarity.Get(a.Pointer) != null) upgraded++;
            }
            Check(same == N, $"CreateRandom deterministico: {same}/{N} coppie identiche");
            Check(upgraded > 0 && upgraded < N, $"CreateRandom con grado: {upgraded}/{N} sopra Standard");

            // 4. Il detour sui costruttori base è attivo (cancella le etichette di indirizzi riutilizzati).
            long before = Rarity.CtorCalls;
            var h1 = new PLHull(EHullType.E_CCG_HULL, 1); var r1 = new PLReactor(EReactorType.E_REAC_CU_FUSION_REACTOR, 1);
            Check(Rarity.CtorCalls >= before + 2 && h1 != null && r1 != null, $"detour dei costruttori attivo ({Rarity.CtorCalls - before} chiamate su 2 oggetti)");

            // 5. Un oggetto senza grado resta intatto.
            var std = PLShipComponent.CreateShipComponentFromHash((int)PLShipComponent.createHashFromInfo(3, 1, 4, 0, 3), null);
            Check(Rarity.Get(std.Pointer) == null && std.Level == 4 && Rarity.TierOf(std.getHash()) == 0, "componente Standard invariato");

            L.LogInfo($"[rarità] collaudo concluso: {(fail == 0 ? "TUTTO OK" : fail + " ERRORI")} — etichette vive {Rarity.TagCount}, CalculateStats chiamata {Rarity.CalcCalls} volte in {up.ElapsedMilliseconds / 1000}s");
        }
    }
}

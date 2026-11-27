// Pulizia dei registri del gioco. Molti oggetti del gioco si iscrivono a liste statiche (tutte le porte, tutti gli
// schermi, tutte le navi con interni, tutti i bersagli di chiamata...) e non sempre se ne cancellano quando vengono
// distrutti. Nel gioco originale passano poche navi e nessuno se ne accorge; con il traffico di Melchior ne passano
// una decina a ogni salto, e ogni nave distrutta resterebbe in memoria per sempre con tutto il suo contenuto.
// Durante la curvatura (dove un piccolo scatto non si vede) si tolgono le voci di oggetti già distrutti, ma solo dalle
// liste che il gioco non usa per posizione: togliere voci da porte, stanze o teletrasporti può far cadere il gioco.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Melchior
{
    public static class Janitor
    {
        public static int Runs, Removed;
        static readonly Dictionary<string, int> byList = new();

        static int Sweep<T>(string name, Il2CppSystem.Collections.Generic.List<T> list) where T : UnityEngine.Object
        {
            if (list == null) return 0; int n = 0;
            for (int i = list.Count - 1; i >= 0; i--) { var o = list[i]; if (o == null) { list.RemoveAt(i); n++; } }
            if (n > 0) byList[name] = (byList.TryGetValue(name, out var c) ? c : 0) + n;
            return n;
        }
        static (int all, int dead) Peek<T>(Il2CppSystem.Collections.Generic.List<T> list) where T : UnityEngine.Object
        {
            if (list == null) return (0, 0); int d = 0; for (int i = 0; i < list.Count; i++) if (list[i] == null) d++; return (list.Count, d);
        }

        // la cache della linea di vista del gioco memorizza ogni coppia di posizioni verificata e non si svuota mai:
        // con decine di navi in movimento cresce senza limiti. Si svuota in curvatura e quando supera una soglia.
        public static int LosCleared; static float nextLos;
        // Gli involucri .NET degli oggetti del gioco (ogni nave letta da un dizionario, ogni array di componenti) tengono
        // un handle che li mantiene vivi finché il garbage collector .NET non li raccoglie. La mod alloca poco, quindi quel
        // collector gira di rado: una raccolta ogni minuto (e a ogni salto) libera navi e comparse ormai distrutte.
        static float nextNetGc; public static int NetGcs;
        public static void LosTick()
        {
            float now = UnityEngine.Time.time;
            if (now >= nextNetGc) { nextNetGc = now + 60f; NetGc(); }
            if (now < nextLos) return; nextLos = now + 5f;
            try { var c = PLGlobal.LineOfSightCache; if (c != null && c.Count > 20000) { c.Clear(); LosCleared++; } } catch { }
        }
        public static void Run()
        {
            int n = 0;
            try { var c = PLGlobal.LineOfSightCache; if (c != null && c.Count > 0) { n += c.Count; byList["linea di vista"] = (byList.TryGetValue("linea di vista", out var k) ? k : 0) + c.Count; c.Clear(); LosCleared++; } } catch (Exception e) { Plugin.L.LogWarning("Pulizia (linea di vista): " + e.Message); }
            void T(Func<int> f, string what) { try { n += f(); } catch (Exception e) { Plugin.L.LogWarning($"Pulizia ({what}): {e.Message}"); } }
            T(() => Sweep("navi con interni", PLShipInfo.AllShipInfosWithInteriors), "navi");
            T(() => Sweep("schermi clonati", PLClonedScreen.AllClonedScreens), "schermi clonati");
            T(() => Sweep("dialoghi", PLDialogueActorInstance.AllDialogueActorInstances), "dialoghi");
            try
            {
                var all = PLEncounterManager.Instance.AllShips; var dead = new List<int>();
                foreach (var kv in all) { var v = kv.Value; bool gone = v == null; if (!gone) try { gone = v.HasBeenDestroyed && !v.gameObject.activeInHierarchy; } catch { gone = true; } if (gone) dead.Add(kv.Key); }
                foreach (var k in dead) { all.Remove(k); Director.Names.Remove(k); }
                n += dead.Count; if (dead.Count > 0) byList["registro navi"] = (byList.TryGetValue("registro navi", out var c) ? c : 0) + dead.Count;
            }
            catch (Exception e) { Plugin.L.LogWarning("Pulizia (registro navi): " + e.Message); }
            // la cache dei sensori: ogni nave ricorda le navi che ha visto, anche dopo che sono state distrutte; le navi
            // morte restano così collegate fra loro e a quelle vive, con tutto il loro contenuto (interfacce comprese)
            try
            {
                int c = 0; var alive = new HashSet<int>();
                foreach (var kv in PLEncounterManager.Instance.AllShips) { var sh = kv.Value; if (sh != null && !sh.HasBeenDestroyed) alive.Add(kv.Key); }
                // tutti gli oggetti sensore della scena, non solo quelli delle navi: anche negozi spaziali, stazioni, boe
                foreach (var so in UnityEngine.Resources.FindObjectsOfTypeAll<PLSensorObject>()) // anche gli inattivi (negozi fuori settore)
                {
                    if (so == null) continue; var cache = so.m_CachedSensorData; if (cache == null) continue;
                    for (int j = cache.Count - 1; j >= 0; j--)
                    {
                        var cd = cache[j]; bool dead = cd == null || cd.Ship == null;
                        if (!dead) try { dead = cd.Ship.HasBeenDestroyed || !alive.Contains(cd.Ship.ShipID); } catch { dead = true; }
                        if (dead) { cache.RemoveAt(j); c++; }
                    }
                }
                n += c; if (c > 0) byList["cache dei sensori"] = (byList.TryGetValue("cache dei sensori", out var k0) ? k0 : 0) + c;
            }
            catch (Exception e) { Plugin.L.LogWarning("Pulizia (sensori): " + e.Message); }
            // gli inventari bersaglio dei giocatori (anche dei bot) ricordano pedine che non esistono più
            try
            {
                int c = 0; var all = PLServer.Instance.AllPlayers;
                for (int i = 0; i < all.Count; i++) { var p = all[i]; if (p == null) continue; var l = p.m_PossibleTargetInventorys; if (l == null) continue; for (int j = l.Count - 1; j >= 0; j--) if (l[j] == null) { l.RemoveAt(j); c++; } }
                n += c; if (c > 0) byList["inventari bersaglio"] = (byList.TryGetValue("inventari bersaglio", out var k1) ? k1 : 0) + c;
            }
            catch (Exception e) { Plugin.L.LogWarning("Pulizia (inventari): " + e.Message); }
            try
            {
                var tg = PLEncounterManager.Instance.AllSpaceTargets; var dead = new List<int>();
                foreach (var kv in tg) if (kv.Value == null) dead.Add(kv.Key);
                foreach (var k in dead) tg.Remove(k);
                n += dead.Count; if (dead.Count > 0) byList["bersagli spaziali"] = (byList.TryGetValue("bersagli spaziali", out var c) ? c : 0) + dead.Count;
            }
            catch (Exception e) { Plugin.L.LogWarning("Pulizia (bersagli spaziali): " + e.Message); }
            try
            {
                var views = PhotonNetwork.networkingPeer.photonViewList; var dead = new List<int>();
                foreach (var kv in views) if (kv.Value == null) dead.Add(kv.Key);
                foreach (var k in dead) views.Remove(k);
                n += dead.Count; if (dead.Count > 0) byList["viste di rete"] = (byList.TryGetValue("viste di rete", out var c) ? c : 0) + dead.Count;
            }
            catch (Exception e) { Plugin.L.LogWarning("Pulizia (viste di rete): " + e.Message); }
            // linee di vista: pedine, torrette e bocchette ricordano i tempi di vista verso bersagli che non esistono più
            try
            {
                int c = 0;
                void Los(PLCombatTarget ct) { if (ct == null) return; var l = ct.m_LOSTimingToTargets; if (l == null) return; for (int j = l.Count - 1; j >= 0; j--) { var b = l[j]; if (b == null || b.OtherTarget == null) { l.RemoveAt(j); c++; } } }
                var gs = PLGameStatic.Instance;
                if (gs != null) { var ct = gs.AllCombatTargets; for (int i = 0; ct != null && i < ct.Count; i++) Los(ct[i]); var pw = gs.AllPawns; for (int i = 0; pw != null && i < pw.Count; i++) Los(pw[i]); }
                n += c; Note("linee di vista", c);
            }
            catch (Exception e) { Plugin.L.LogWarning("Pulizia (linee di vista): " + e.Message); }
            // gli schermi ancora vivi tengono in lista etichette e pulsanti già distrutti (destinazioni del teletrasporto, contatti dei sensori)
            try
            {
                int c = 0;
                void W<T>(Il2CppSystem.Collections.Generic.List<T> l) where T : UnityEngine.Object { if (l == null) return; for (int j = l.Count - 1; j >= 0; j--) if (l[j] == null) { l.RemoveAt(j); c++; } }
                foreach (var sc in UnityEngine.Object.FindObjectsOfType<PLUIScreen>()) { if (sc == null) continue; W(sc.AllLabels); W(sc.AllBoxes); W(sc.AllSprites); W(sc.AllStylizedElements); W(sc.AllButtons); }
                n += c; Note("elementi degli schermi", c);
            }
            catch (Exception e) { Plugin.L.LogWarning("Pulizia (schermi): " + e.Message); }
            // gli incontri persistenti (hub, stazioni) tengono la lista di tutte le navi create nel loro settore, anche distrutte
            try
            {
                int c = 0;
                foreach (var kv in PLEncounterManager.Instance.AllPersistantEncounterInstances)
                {
                    var l = kv.Value?.MyCreatedShipInfos; if (l == null) continue;
                    for (int j = l.Count - 1; j >= 0; j--) { var sh = l[j]; bool dead = sh == null; if (!dead) try { dead = sh.HasBeenDestroyed; } catch { dead = true; } if (dead) { l.RemoveAt(j); c++; } }
                }
                n += c; Note("navi create negli incontri", c);
            }
            catch (Exception e) { Plugin.L.LogWarning("Pulizia (incontri): " + e.Message); }
            // cache dei componenti recuperabili e aggiornabili delle navi vive (la nostra compresa): componenti di navi distrutte
            // tenevano in vita le navi intere con tutte le loro interfacce. Sono cache a tempo: il gioco le ricalcola.
            try
            {
                int c = 0;
                foreach (var kv in PLEncounterManager.Instance.AllShips)
                {
                    var sh = kv.Value?.TryCast<PLShipInfo>(); if (sh == null) continue;
                    var sv = sh.m_CachedSalvageableComponents; if (sv != null && sv.Count > 0) { c += sv.Count; sv.Clear(); sh.m_lastCachedSalvageableComponentsTime = 0f; }
                    var up = sh.m_CachedUpgradableComponents; if (up != null && up.Count > 0) { c += up.Count; up.Clear(); sh.m_lastCachedUpgradableComponentsTime = 0f; }
                    var ts = sh.cachedTargetSalvageShip; if (ts != null) { bool dead = false; try { dead = ts.HasBeenDestroyed; } catch { dead = true; } if (dead) { sh.cachedTargetSalvageShip = null; c++; } }
                    else if (!ReferenceEquals(ts, null)) { sh.cachedTargetSalvageShip = null; c++; }
                }
                n += c; Note("cache dei componenti", c);
            }
            catch (Exception e) { Plugin.L.LogWarning("Pulizia (componenti): " + e.Message); }
            // punti di teletrasporto di navi distrutte nella lista statica
            try { var l = PLTeleportationLocationInstance.AllTLIs; int c = 0; if (l != null) for (int j = l.Count - 1; j >= 0; j--) if (l[j] == null) { l.RemoveAt(j); c++; } n += c; Note("teletrasporti", c); }
            catch (Exception e) { Plugin.L.LogWarning("Pulizia (teletrasporti): " + e.Message); }
            // bocchette dei biscotti delle navi Fluffy: la lista statica le conserva dopo la distruzione
            try
            {
                var alive = new HashSet<int>(); foreach (var kv in PLEncounterManager.Instance.AllShips) { var sh = kv.Value; if (sh != null && !sh.HasBeenDestroyed) alive.Add(kv.Key); }
                var v = PLGameStatic.Instance?.AllFBVents; int c = 0;
                if (v != null) for (int j = v.Count - 1; j >= 0; j--) { var fv = v[j]; bool dead = fv == null; if (!dead) try { dead = fv.MyShip == null || !alive.Contains(fv.MyShip.ShipID); } catch { dead = true; } if (dead) { v.RemoveAt(j); c++; } }
                n += c; Note("bocchette", c);
            }
            catch (Exception e) { Plugin.L.LogWarning("Pulizia (bocchette): " + e.Message); }
            // effetti della regata, per nave
            try
            {
                var map = PLGameStatic.Instance?.ShipRaceLightningPSMap; int c = 0;
                if (map != null) { var dead = new List<PLShipInfoBase>(); foreach (var kv in map) if (kv.Key == null) dead.Add(kv.Key); foreach (var k in dead) { map.Remove(k); c++; } }
                n += c; Note("effetti della regata", c);
            }
            catch (Exception e) { Plugin.L.LogWarning("Pulizia (regata): " + e.Message); }
            NetGc();
            Runs++; Removed += n;
        }

        // raccolta completa: i finalizzatori degli involucri liberano gli handle, poi una seconda passata raccoglie il resto
        static void NetGc() { try { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); NetGcs++; } catch { } }
        static void Note(string what, int c) { if (c > 0) byList[what] = (byList.TryGetValue(what, out var k) ? k : 0) + c; }
        public static string Describe()
        {
            var sb = new System.Text.StringBuilder($"raccolte .NET {NetGcs}; pagine dei pannelli {Panels.Count} (saltate {Panels.Skipped}, rilasciate {Panels.Released}); pulizie {Runs}, voci rimosse {Removed}");
            try { sb.Append($"; linea di vista {PLGlobal.LineOfSightCache.Count} (svuotata {LosCleared} volte)"); } catch { }
            if (byList.Count > 0) sb.Append(" (" + string.Join(", ", byList.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")) + ")");
            void P<T>(string name, Il2CppSystem.Collections.Generic.List<T> l) where T : UnityEngine.Object { try { var (a, d) = Peek(l); sb.Append($"; {name} {a}/{d} morte"); } catch { } }
            void D<K, V>(string name, Il2CppSystem.Collections.Generic.Dictionary<K, V> dct) where V : UnityEngine.Object { try { int all = 0, d = 0; foreach (var kv in dct) { all++; if (kv.Value == null) d++; } sb.Append($"; {name} {all}/{d} morte"); } catch { } }
            void C(string name, Func<int> f) { try { sb.Append($"; {name} {f()}"); } catch { } }
            D("bersagli spaziali", PLEncounterManager.Instance.AllSpaceTargets); C("incontri persistenti", () => PLEncounterManager.Instance.AllPersistantEncounterInstances.Count);
            P("giocatori", PLServer.Instance.AllPlayers); C("equipaggi", () => PLServer.Instance.AllCrews.Count); C("tempi componenti", () => PLServer.Instance.LastShipComponentServerUpdateTimes.Count);
            P("pedine", PLGameStatic.Instance.AllPawns); P("basi pedine", PLGameStatic.Instance.AllPawnBases); P("bersagli a terra", PLGameStatic.Instance.AllCombatTargets); P("interni", PLGameStatic.Instance.m_Interiors);
            C("inventari", () => PLNetworkManager.Instance.AllPawnInventories.Count); C("titoli boss", () => PLInGameUI.Instance.BossTitle_AlreadyShownShips.Count); C("proiettili", () => PLServer.Instance.m_ActiveProjectiles.Count);
            try { int all = 0, d = 0; foreach (var kv in PhotonNetwork.networkingPeer.photonViewList) { all++; if (kv.Value == null) d++; } sb.Append($"; viste di rete {all}/{d} morte"); } catch { }
            P("navi con interni", PLShipInfo.AllShipInfosWithInteriors); P("schermi", PLScreenHubBase.AllScreenHubs); P("chiamate", PLHailTarget.AllHailTargets);
            P("danneggiabili", PLDamageableSpaceObject.All); P("porte", PLDoor.AllDoors); P("stanze", PLRoomArea.All); P("teletrasporti", PLTeleportationLocationInstance.AllTLIs);
            P("schermi clonati", PLClonedScreen.AllClonedScreens); P("generatori", PLSpawner.All); P("dialoghi", PLDialogueActorInstance.AllDialogueActorInstances);
            return sb.ToString();
        }
    }
}

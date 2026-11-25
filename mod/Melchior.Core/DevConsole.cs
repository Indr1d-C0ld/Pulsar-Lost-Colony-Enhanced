// Console di sviluppo a file: ogni riga scritta in BepInEx/config/melchior.cmd viene eseguita e il file svuotato.
// Attiva solo con Debug.DevConsole. Comandi: tab <n> | tabclose | compcat <n> | give <hash esadecimale> | stats | cargo | save <nome>
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace Melchior
{
    static class DevConsole
    {
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static long next;

        public static void Tick()
        {
            if (!Plugin.DevConsoleOn.Value || clock.ElapsedMilliseconds < next) return;
            next = clock.ElapsedMilliseconds + 400;
            string path = Path.Combine(Paths.ConfigPath, Plugin.PerInstance("melchior.cmd"));
            if (!File.Exists(path)) return;
            string[] lines;
            try { lines = File.ReadAllLines(path); File.Delete(path); } catch { return; }
            foreach (var raw in lines)
            {
                var a = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (a.Length == 0) continue;
                try { Plugin.L.LogInfo($"[console{Plugin.Inst}] {raw.Trim()} -> {Run(a)}"); }
                catch (Exception e) { Plugin.L.LogError($"[console] {raw.Trim()} -> errore: {e.Message}"); }
            }
        }

        static string Run(string[] a)
        {
            var tab = PLTabMenu.Instance;
            var stats = PLNetworkManager.Instance?.LocalPlayer?.StartingShip?.MyStats;
            switch (a[0])
            {
                case "tab": tab.TabMenuActive = true; if (a.Length > 1) tab.SelectTab(int.Parse(a[1])); return "menu aperto, scheda " + tab.m_CurrentTabIndex;
                case "tabclose": tab.TabMenuActive = false; return "menu chiuso";
                case "compcat": tab.SetComponentCategory(int.Parse(a[1])); return "categoria " + a[1];
                case "give":
                {
                    if (stats == null) return "nessuna nave";
                    var c = PLShipComponent.CreateShipComponentFromHash((int)uint.Parse(a[1], NumberStyles.HexNumber), null);
                    if (c == null) return "hash non valido";
                    stats.AddShipComponent(c, -1, ESlotType.E_COMP_CARGO);
                    return $"in stiva: {c.Name} (liv. {c.GetLevel()}) hash {c.getHash():X8}";
                }
                case "stats":
                    return stats == null ? "nessuna nave" : $"scudi {stats.ShieldsMax:0.0} scafo {stats.HullMax:0.0} reattore {stats.ReactorOutputMax:0.0} firmaEM {stats.EMSignature:0.00} etichette {Rarity.TagCount}";
                case "cargo":
                {
                    if (stats == null) return "nessuna nave";
                    var sb = new System.Text.StringBuilder(); var list = stats.m_AllComponents;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var c = list[i]; if (c == null) continue;
                        uint h = c.getHash();
                        if (Rarity.TierOf(h) != 0 || !c.IsEquipped) sb.Append($"\n      {(c.IsEquipped ? "montato" : "stiva  ")} {h:X8} {c.Name} liv.{c.GetLevel()} (reale {c.Level + 1})");
                    }
                    return sb.Length == 0 ? "nessun componente da mostrare" : sb.ToString();
                }
                case "save":
                {
                    var io = PLSaveGameIO.Instance;
                    string file = Path.Combine((string)io.SaveDir, a[1] + ".plsave");
                    io.SaveToFile(file);
                    return "salvato in " + file;
                }
                case "con":
                    if (a.Length == 1) { StationConsole.Show = !StationConsole.Show; return "console operativa " + (StationConsole.Show ? "aperta" : "chiusa"); }
                    if (a[1] == "tab") { StationConsole.Tab = int.Parse(a[2]); StationConsole.Show = true; return "scheda " + a[2]; }
                    if (a[1] == "set") { var sh = Stations.MyShip; if (sh == null) return "nessuna nave"; Stations.Set(sh.ShipID, a[2], float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture)); return a[2] + " = " + Stations.Get(sh.ShipID, a[2]); }
                    if (a[1] == "get") { var sh = Stations.MyShip; if (sh == null) return "nessuna nave"; return string.Join("  ", a.Skip(2).Select(k => k + "=" + Stations.Get(sh.ShipID, k).ToString("0.#"))); }
                    if (a[1] == "preset") { var sh = Stations.MyShip; var p = Stations.Cfg.Presets.FirstOrDefault(q => q.Name.StartsWith(string.Join(" ", a.Skip(2)), StringComparison.OrdinalIgnoreCase)); if (sh == null || p == null) return "nave o assetto non trovati"; Stations.ApplyPreset(sh.ShipID, p); return "assetto " + p.Name; }
                    return "uso: con | con tab <n> | con set <id> <valore> | con preset <nome>";
                case "lang":
                    if (a.Length > 1) PLLocalize.Instance.ChangeCurrentLanguage(string.Join(" ", a.Skip(1)), false);
                    return Ui.Languages();
                case "class":
                {
                    var cm = PLUIClassSelectionMenu.Instance; if (cm == null || Stations.MyShip == null || cm.ReadyButton == null || !cm.gameObject.activeInHierarchy) return "menu classi non pronto";
                    if (a[1] == "ready") { cm.ClickReady(); return "pronto"; } cm.SelectClassID(int.Parse(a[1])); return "classe " + a[1] + " scelta";
                }
                case "spawn": // spawn <tipo nave numerico>: nemico di prova ostile alla nostra nave (solo host)
                {
                    var me = Stations.MyShip; var cpei = PLEncounterManager.Instance?.GetCPEI(); if (me == null || cpei == null) return "nessuna nave o nessun settore";
                    var type = (EShipType)int.Parse(a[1]);
                    var psi = new PLPersistantShipInfo(type, -1, PLServer.GetCurrentSector(), 0, false, false, false, -1, me.ShipID);
                    var pos = cpei.GetEmptyLocationForEnemy(type, psi, 7);
                    var ship = cpei.SpawnEnemyShip(type, psi, pos, Quaternion.identity);
                    return ship == null ? "nave non creata" : $"creata nave {ship.ShipID} ({type})";
                }
                case "bots": // bots [classi...]: aggiunge bot per le classi indicate (predefinite 1 2 3 4)
                {
                    var srv = PLServer.Instance; var list = a.Length > 1 ? a.Skip(1).Select(int.Parse).ToArray() : new[] { 1, 2, 3, 4 };
                    foreach (int c in list) srv.ServerAddCrewBotPlayer(c); return "aggiunti bot per le classi " + string.Join(" ", list);
                }
                case "ships":
                {
                    var sb = new System.Text.StringBuilder(); var me = Stations.MyShip;
                    foreach (var kv in PLEncounterManager.Instance.AllShips)
                    {
                        var sh = kv.Value; if (sh == null) continue; var st = sh.MyStats;
                        sb.Append($"\n      {kv.Key} {(string)sh.ShipName} tipo {sh.ShipTypeID} squadra {sh.TeamID}{(me != null && me.ShipID == kv.Key ? " (noi)" : "")} scafo {st?.HullCurrent:0}/{st?.HullMax:0} scudi {st?.ShieldsCurrent:0}/{st?.ShieldsMax:0} bersaglio {(sh.TargetShip == null ? "-" : sh.TargetShip.ShipID.ToString())}{(sh.HasBeenDestroyed ? " DISTRUTTA" : "")}");
                    }
                    return sb.Length == 0 ? "nessuna nave" : sb.ToString();
                }
                case "hit": // hit <nave> <danno>: un colpo delle nostre torrette su scudi e scafo del bersaglio, per misurare il puntamento
                {
                    var me = Stations.MyShip; var tgt = PLEncounterManager.Instance.GetShipFromID(int.Parse(a[1])); if (me == null || tgt == null) return "nave non trovata";
                    var tur = me.MyStats.GetRegularTurretAtIndex(0) ?? me.MyStats.GetMainTurret(); if (tur == null) return "nessuna torretta";
                    float dmg = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture); var ts = tgt.MyStats;
                    float s0 = ts.ShieldsCurrent; ts.TakeShieldDamage(dmg, EDamageType.E_ENERGY, 1f, 1f, tur); float s1 = ts.ShieldsCurrent;
                    float h0 = ts.HullCurrent; ts.TakeHullDamage(dmg, EDamageType.E_ENERGY, me, tur); float h1 = ts.HullCurrent; tgt.DestroySelfIfDead(me);
                    return $"mira {Stations.Get(me.ShipID, "wpx_aim"):0}: scudi {s0:0.0}->{s1:0.0} (tolti {s0 - s1:0.0})  scafo {h0:0.0}->{h1:0.0} (tolti {h0 - h1:0.0})";
                }
                case "target": // target <nave>: aggancia il bersaglio come farebbe il capitano; senza argomenti mostra bersaglio e distanza
                {
                    var me = Stations.MyShip; if (me == null) return "nessuna nave";
                    if (a.Length > 1) me.Captain_SetTargetShip(int.Parse(a[1]));
                    var t = me.TargetShip; float d = Stations.TargetDistance(me);
                    return t == null ? "nessun bersaglio" : $"bersaglio {t.ShipID} distanza {d:0} fascia {Stations.BandOf(d)}";
                }
                case "dir": // dir: stato del direttore; dir fire [evento]: forza un evento; dir on|off
                {
                    if (a.Length > 1 && a[1] == "fire") { var me = Stations.MyShip; if (me == null) return "nessuna nave"; return Director.Fire(me, Director.Power(me, out _, out _), a.Length > 2 ? a[2] : null) ? "evento " + Director.LastEvent : "nessun evento adatto"; }
                    if (a.Length > 1 && (a[1] == "on" || a[1] == "off")) Director.Cfg.Enabled = a[1] == "on";
                    return Director.Describe();
                }
                case "rival": return RivalForce.Describe();
                case "map": // map <settore>: apre la mappa stellare su quel settore
                {
                    var sec = PLServer.GetSectorWithID(a.Length > 1 ? int.Parse(a[1]) : PLServer.GetCurrentSector().ID); if (sec == null) return "settore non trovato";
                    var sm = UnityEngine.Object.FindObjectOfType<PLStarmap>(); if (sm == null) return "mappa non trovata";
                    sm.OpenStarmapToSector(sec); return $"mappa aperta sul settore {sec.ID} (fazione {GalaxyWar.Name(sec.MySPI.Faction)}, vicini {string.Join(",", System.Linq.Enumerable.Range(0, sec.Neighbors.Count).Select(i => sec.Neighbors[i].ID + ":" + sec.Neighbors[i].MySPI.Faction))})";
                }
                case "nem": // nem: personaggi; nem appear [nome]: compare subito; nem life [passi]: vita nella galassia
                    if (a.Length > 1 && a[1] == "appear") return Nemesis.Force(a.Length > 2 ? string.Join(" ", a.Skip(2)) : null);
                    if (a.Length > 1 && a[1] == "life") return Nemesis.ForceLife(a.Length > 2 ? int.Parse(a[2]) : 1);
                    return Nemesis.Describe();
                case "crisis": // crisis: stato; crisis start [tipo]; crisis stage; crisis win|lose; crisis arrive (porta la crisi nel settore corrente)
                    if (a.Length > 1 && a[1] == "start") return Crises.Start(a.Length > 2 ? a[2] : null);
                    if (a.Length > 1 && a[1] == "stage") return Crises.ForceStage();
                    if (a.Length > 1 && (a[1] == "win" || a[1] == "lose")) return Crises.ForceEnd(a[1] == "win");
                    if (a.Length > 1 && a[1] == "here" && Crises.Cur != null) { Crises.Cur.sector = PLServer.GetCurrentSector().ID; Crises.Cur.spawned = false; return "settore della crisi: qui"; }
                    return Crises.Describe();
                case "rep": // rep: reputazione; rep set <fazione> <valore>
                    if (a.Length > 3 && a[1] == "set") return Factions.Set(int.Parse(a[2]), int.Parse(a[3]));
                    return Factions.Describe();
                case "bounty": // bounty: mette subito una taglia sulla bacheca; bounty here: sposta il bersaglio della taglia in corso nel settore corrente
                    if (a.Length > 1 && a[1] == "here") { var c = Saga.State.contract; var f = c == null ? null : Nemesis.Find(c.target); if (f == null) return "nessuna taglia in corso"; f.sector = c.sector = PLServer.GetCurrentSector().ID; return $"{f.Full} ora qui"; }
                    return Contracts.ForceBounty();
                case "jump": // jump [settore]: salto immediato verso un settore (predefinito: il primo vicino)
                {
                    var me = Stations.MyShip; var cur = PLServer.GetCurrentSector(); if (me == null || cur == null) return "nessuna nave";
                    int to = a.Length > 1 ? int.Parse(a[1]) : cur.Neighbors[0].ID;
                    me.WarpTargetID = to; PLServer.Instance.CPEI_HandleActivateWarpDrive(me.ShipID, to, PLNetworkManager.Instance.LocalPlayer.GetPlayerID());
                    return $"salto verso il settore {to}";
                }
                case "jumps": return $"salti: gioco {(int)PLServer.Instance.NumJumps}, mod {Obscured.Jumps()}";
                case "skip": { var me = Stations.MyShip; if (me == null) return "nessuna nave"; me.SkipWarp(); return "salto accelerato"; } // come premere F8 nel tunnel di curvatura
                case "course": if (a.Length == 1) return Course.Describe(); if (a.Length > 1 && a[1] == "clear") { Course.Clear(); return "rotta cancellata"; } Course.Plot(int.Parse(a[1])); return "meta aggiunta: " + a[1];
                case "war": if (a.Length > 3 && a[1] == "flip") return GalaxyWar.Flip(int.Parse(a[2]), int.Parse(a[3])); return a.Length > 1 && a[1] == "step" ? GalaxyWar.ForceStep() + " || " + GalaxyWar.Describe() : GalaxyWar.Describe(); // guerra fra fazioni
                case "fleets": return a.Length > 1 && a[1] == "step" ? Fleets.Step() : a.Length > 1 && a[1] == "near" ? Fleets.Approach(a.Length > 2 ? a[2] : null) : Fleets.Describe(); // flotte con un'agenda
                case "news": // news: ultime notizie; news diceria: ne genera una subito; news bacheca: stato della bacheca
                    if (a.Length > 1 && a[1] == "diceria") { News.Rumor(); }
                    if (a.Length > 2 && a[1] == "accetta") { var o = Contracts.Board[int.Parse(a[2])]; Contracts.Request("accetta", o.id); return "accetto: " + o.title; }
                    if (a.Length > 1 && a[1] == "bacheca") return string.Join("\n      ", Contracts.Board.Select(o => $"{o.family} «{o.title}» {o.issuer} {o.reward} Cr scade {o.expires}{(o.urgent ? " URGENTE" : "")}").Prepend("bacheca:"));
                    return string.Join("\n      ", News.Items.TakeLast(8).Select(n => $"[{n.cat}] {n.text}").Prepend("notiziario:"));
                case "inv": // confronto degli elenchi di componenti della nave
                {
                    var me = Stations.MyShip; if (me?.MyStats == null) return "nessuna nave"; var st3 = me.MyStats; var sb = new System.Text.StringBuilder();
                    sb.Append($"m_AllComponents {st3.m_AllComponents?.Count} · AllSlotItems {st3.AllSlotItems?.Count} · powered {st3.m_AllPoweredComponents?.Count}");
                    var items = st3.AllSlotItems; if (items != null) for (int i = 0; i < items.Count && i < 30; i++) { var it = items[i]; var c = it?.TryCast<PLShipComponent>(); sb.Append($"\n      {it?.NetID} {it?.Name} slot {it?.ActualSlotType} equip {c?.IsEquipped} carico {c?.InCargoSlot()}"); }
                    return sb.ToString();
                }
                case "loot": // loot list | loot give <grado> [quanti] | loot equip <netid> | loot bonus
                {
                    var me = Stations.MyShip; if (me?.MyStats == null) return "nessuna nave";
                    var st2 = me.MyStats; var all = st2.m_AllComponents;
                    if (a.Length > 2 && a[1] == "give") { int t = int.Parse(a[2]), n = a.Length > 3 ? int.Parse(a[3]) : 1; var got = new System.Collections.Generic.List<string>(); for (int i = 0; i < n; i++) { var c = Loot.Grant(me, t, t, "console"); if (c != null) got.Add(c.Name); } return "aggiunti: " + string.Join(", ", got); }
                    if (a.Length > 2 && a[1] == "equip")
                    {
                        var c = st2.GetComponentFromNetID(int.Parse(a[2])); if (c == null) return "pezzo non trovato";
                        PLShipComponent cur = null; for (int i = 0; i < all.Count; i++) { var x = all[i]; if (x != null && x.IsEquipped && !x.InCargoSlot() && (x.getHash() & 0x3F) == (c.getHash() & 0x3F)) { cur = x; break; } }
                        if (cur == null) return "nessun pezzo montato dello stesso tipo";
                        st2.Swap(c, cur); return $"montato {c.Name} al posto di {cur.Name}";
                    }
                    if (a.Length > 3 && a[1] == "forge") // loot forge <netid> <grado>: dà un grado a un pezzo già montato (solo per le prove)
                    {
                        var c = st2.GetComponentFromNetID(int.Parse(a[2])); if (c == null) return "pezzo non trovato";
                        uint h = c.getHash(); Rarity.Register(c.Pointer, Rarity.MakeTag(h & Rarity.BodyMask, int.Parse(a[3]), 0));
                        return $"{c.Name} ora ha grado {a[3]}: {Rarity.Describe(Rarity.Get(c.Pointer)).Replace("\n", " | ")}";
                    }
                    if (a.Length > 1 && a[1] == "bonus")
                    {
                        var keys = Loot.KeyLabels.Keys.Where(k => Loot.Bonus(me, k) != 0).Select(k => $"{k}={Loot.Bonus(me, k):0.###}");
                        return $"bonus di stazione: {string.Join(" ", keys.DefaultIfEmpty("nessuno"))} | scudi {st2.ShieldsMax:0} scafo {st2.HullMax:0} reattore {st2.ReactorOutputMax:0} danno {st2.TurretDamageFactor:0.###} firmaEM {st2.EMSignature:0.##}";
                    }
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < all.Count; i++)
                    {
                        var c = all[i]; if (c == null) continue; var tag = Rarity.Get(c.Pointer); if (tag == null && !c.InCargoSlot() && !(a.Length > 1 && a[1] == "all")) continue;
                        sb.Append($"\n      {c.NetID,4} {(c.InCargoSlot() ? "stiva " : c.IsEquipped ? "montato" : "altro ")} {c.Name} [{Rarity.Tier(tag?.Tier ?? 0).Name}]");
                        if (tag != null) sb.Append(" " + string.Join("; ", tag.Defs.Select((d, k) => (d.Drawback ? "DIFETTO " : "") + (d.Key.Length > 0 ? d.Key : d.Stat) + (d.Cond.Length > 0 ? "[" + d.Cond + "]" : "") + "=" + tag.Value[k].ToString("0.###"))));
                    }
                    return sb.Length == 0 ? "niente da mostrare" : sb.ToString();
                }
                case "saga": // saga: stato; saga start [silenzio|taglia|colonia]: inizia un arco; saga due: esegue subito la prossima conseguenza
                {
                    var me = Stations.MyShip; if (a.Length > 1 && me == null) return "nessuna nave";
                    if (a.Length > 1 && a[1] == "start") return Saga.StartArc(Director.Power(me, out _, out _), a.Length > 2 ? a[2] : null) ? "arco iniziato" : "arco non avviabile";
                    if (a.Length > 2 && a[1] == "offline") { Saga.OfflineHours = double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture); return "assenza simulata: " + a[2] + " ore"; }
                    if (a.Length > 1 && a[1] == "due") return Saga.TryDue(me, Director.Power(me, out _, out _), true) ? "conseguenza eseguita" : "niente in attesa";
                    return Saga.Describe();
                }
                case "psi": // navi persistenti del settore corrente (o di tutti con "psi all")
                {
                    var cur = PLServer.GetCurrentSector(); var sb = new System.Text.StringBuilder(); var list = PLServer.Instance.AllPSIs; int n = 0;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var p = list[i]; if (p == null) continue; bool here = p.MyCurrentSector != null && cur != null && p.MyCurrentSector.ID == cur.ID;
                        if (!here && !(a.Length > 1 && a[1] == "all" && p.MyCurrentSector != null && p.MyCurrentSector.MissionSpecificID >= 910000)) continue;
                        n++; sb.Append($"\n      «{p.ShipName}» {p.Type} fazione {p.FactionID} settore {p.MyCurrentSector?.ID} missione {p.MyCurrentSector?.MissionSpecificID} distrutta {p.m_Destroyed}/{p.IsShipDestroyed} istanza {(p.ShipInstance == null ? "no" : p.ShipInstance.ShipID.ToString())} scafo {p.HullPercent:0}%");
                    }
                    return $"settore {cur?.ID} navi persistenti {n}" + sb;
                }
                case "heal": { var me = Stations.MyShip; if (me?.MyStats == null) return "nessuna nave"; me.MyStats.HullCurrent = me.MyStats.HullMax; me.MyStats.ShieldsCurrent = me.MyStats.ShieldsMax; return "nave riparata (solo per le prove)"; }
                case "spawnnamed": // spawnnamed <tipo numerico> <nome>: nave ostile con un nome preciso (per provare gli obiettivi)
                {
                    var me = Stations.MyShip; if (me == null) return "nessuna nave";
                    var sh = Director.Spawn(me, (EShipType)int.Parse(a[1]), 0, string.Join(" ", a.Skip(2)));
                    return sh == null ? "nave non creata" : $"creata nave {sh.ShipID} «{(string)sh.ShipName}»";
                }
                case "mis": // mis: stato; mis <caccia|soccorso|ricognizione> [potenza]: avvia una missione procedurale
                {
                    if (a.Length > 1 && a[1] == "warp") // salto nel settore della missione attiva, come se il pilota attivasse la curvatura
                    {
                        var me = Stations.MyShip; int mid = Missions.ActiveId(); var sec = Missions.SectorOf(mid); if (me == null || sec == null) return "nessuna missione con settore";
                        me.WarpTargetID = sec.ID; PLServer.Instance.CPEI_HandleActivateWarpDrive(me.ShipID, sec.ID, PLNetworkManager.Instance.LocalPlayer.GetPlayerID());
                        return $"salto verso il settore {sec.ID} della missione {mid}";
                    }
                    if (a.Length > 2 && a[1] == "fire") return Missions.Fire(a[2], "Prova: obiettivo " + a[2]) ? "obiettivo segnalato" : "già segnalato";
                    if (a.Length > 1) { var me = Stations.MyShip; if (me == null) return "nessuna nave"; float pw = a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : Director.Power(me, out _, out _); return Missions.Offer(pw, a[1]) ? "missione avviata" : "nessuna missione avviabile"; }
                    return Missions.Describe();
                }
                case "journal": return Stations.Journal.Count == 0 ? "giornale vuoto" : "\n      " + string.Join("\n      ", Stations.Journal.TakeLast(12));
                case "aimstat":
                    return $"scudi: chiamate {AimShields.Calls} nostre {AimShields.Mine} modificate {AimShields.Modified} senza torretta {AimShields.NoTurret} ultimo {AimShields.LastIn:0.0}->{AimShields.LastOut:0.0} | scafo: chiamate {AimHull.Calls} nostre {AimHull.Mine} modificate {AimHull.Modified} ultimo {AimHull.LastIn:0.0}->{AimHull.LastOut:0.0}";
                case "svc":
                    if (a.Length == 1) { ServicePanel.Show = !ServicePanel.Show; return "stato di servizio " + (ServicePanel.Show ? "aperto" : "chiuso"); }
                    if (a[1] == "sync") { Service.Save(); Service.Sync(); return "sincronizzazione avviata"; }
                    if (a[1] == "add") { Service.Add(a[2], double.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture)); return a[2] + " = " + Service.Get(a[2]); }
                    return $"{Service.Rank().Name}, {Service.Points():0} punti, classifica: {Service.BoardStatus} ({Service.Board.Count} righe)";
                case "zoom": // zoom <colonna> <riga> sull'atlante degli schermi (4x4)
                    MelchiorHud.DebugCoords = a.Length < 3 ? new Rect(0, 0, 1, 1) : new Rect(int.Parse(a[1]) * 0.25f, 0.75f - int.Parse(a[2]) * 0.25f, 0.25f, 0.25f);
                    return "zoom impostato";
                case "panels":
                {
                    // elenca i pannelli di stazione; "panels click <n> <nome pulsante>" simula un clic; "panels view <n>" mostra l'atlante degli schermi
                    var all = UnityEngine.Object.FindObjectsOfType<PLUIScreen>(); var sb = new System.Text.StringBuilder(); int k = 0;
                    for (int i = 0; i < all.Length; i++)
                    {
                        var sc = all[i]; string tn = sc.GetIl2CppType().Name; if (!Stations.Cfg.Panels.ContainsKey(tn)) continue;
                        var pg = Panels.Of(sc);
                        if (a.Length > 3 && a[1] == "click" && int.Parse(a[2]) == k)
                        {
                            UIWidget b = null; var bl = sc.AllButtons; string want = Panels.Prefix + a[3];
                            if (bl != null) for (int j = 0; j < bl.Count; j++) if (bl[j] != null && bl[j].name == want) b = bl[j];
                            if (b == null) return "pulsante " + want + " non trovato su " + tn;
                            sc.OnButtonClick(b); return $"clic {a[3]} su {tn}: pagina {(pg != null && pg.Open ? "aperta" : "chiusa")}";
                        }
                        if (a.Length > 2 && a[1] == "seq" && int.Parse(a[2]) == k) return pg == null ? "nessuna pagina" : $"sequenza {pg.VentOrder} posizione {pg.VentPos} segreto {pg.DecSecret} tentativi {pg.DecTries.Count} asse {pg.RouteAxis} nota «{pg.Message}»";
                        if (a.Length > 2 && a[1] == "stray" && int.Parse(a[2]) == k)
                        {
                            // elementi di altri schermi che finiscono dentro il riquadro di questo
                            var rt0 = sc.MyRootPanel.transform; var uiTop = rt0; while (uiTop.parent != null) uiTop = uiTop.parent;
                            var allW = uiTop.GetComponentsInChildren<UIWidget>();
                            for (int j = 0; j < allW.Length; j++)
                            {
                                var w = allW[j]; if (w.transform == rt0 || w.transform.IsChildOf(rt0)) continue;
                                var wc = w.worldCorners; float x0 = 1e9f, x1 = -1e9f, y0 = 1e9f, y1 = -1e9f;
                                for (int q = 0; q < 4; q++) { var lp = rt0.InverseTransformPoint(wc[q]); x0 = Math.Min(x0, lp.x); x1 = Math.Max(x1, lp.x); y0 = Math.Min(y0, lp.y); y1 = Math.Max(y1, lp.y); }
                                if (x1 < -250 || x0 > 250 || y1 < -250 || y0 > 250) continue;
                                var pn = NGUITools.FindInParents<UIPanel>(w.gameObject);
                                sb.Append($"\n      X {w.GetIl2CppType().Name} {Ui.PathOf(w.transform)} x {x0:0}..{x1:0} y {y0:0}..{y1:0} prof {w.depth} pannello {(pn == null ? "?" : pn.name + " " + pn.depth)}");
                            }
                            var myPn = NGUITools.FindInParents<UIPanel>(rt0.gameObject); sb.Append($"\n      mio pannello {(myPn == null ? "?" : myPn.name + " " + myPn.depth)}");
                            // elementi ancora accesi che non appartengono alla nostra pagina
                            var top = sc.MyRootPanel.transform; while (top.parent != null && top.parent.GetComponentsInChildren<PLUIScreen>().Length <= 1 && top.parent.GetComponentsInChildren<UIPanel>().Length <= 3) top = top.parent;
                            sb.Append($"\n      radice {Ui.PathOf(sc.MyRootPanel.transform)} cima {Ui.PathOf(top)}");
                            var ws = top.GetComponentsInChildren<UIWidget>(); var rs = sc.GetComponentsInChildren<Renderer>();
                            var rootT = sc.MyRootPanel.transform;
                            for (int j = 0; j < ws.Length; j++) { var w = ws[j]; var rp = rootT.InverseTransformPoint(w.transform.position);
                                if (pg != null && (w.transform.IsChildOf(pg.Page.transform) || w.transform.IsChildOf(pg.OpenButton.transform))) { if (w.height <= 4 || Math.Abs(rp.y) < 12) sb.Append($"\n      P {w.GetIl2CppType().Name} {w.name} sotto {w.transform.parent.name} {w.width}x{w.height} in radice {rp.x:0},{rp.y:0} perno {w.pivot}"); continue; } sb.Append($"\n      W {w.GetIl2CppType().Name} {Ui.PathOf(w.transform)} {w.width}x{w.height} pos {w.transform.localPosition.x:0},{w.transform.localPosition.y:0} prof {w.depth}"); }
                            for (int j = 0; j < rs.Length; j++) sb.Append($"\n      R {rs[j].GetIl2CppType().Name} {Ui.PathOf(rs[j].transform)}");
                            return sb.ToString();
                        }
                        if (a.Length > 2 && a[1] == "view" && int.Parse(a[2]) == k) { MelchiorHud.DebugTexture = sc.GetLoadedRenderTexture(); return "anteprima di " + tn; }
                        sb.Append($"\n      {k} {tn} pagina {(pg == null ? "NO" : pg.Open ? "aperta" : "pronta")} schermo {sc.ScreenSize.x:0}x{sc.ScreenSize.y:0} radice {sc.MyRootPanel?.width}x{sc.MyRootPanel?.height} scala {sc.transform.localScale.x:0.##}x{sc.transform.localScale.y:0.##}");
                        k++;
                    }
                    if (a.Length > 1 && a[1] == "view" && a.Length == 2) { MelchiorHud.DebugTexture = null; return "anteprima chiusa"; }
                    return sb.Length == 0 ? "nessun pannello" : sb.ToString();
                }
                case "goto":
                {
                    var all = UnityEngine.Object.FindObjectsOfType<PLUIScreen>(); int k = 0; var pw = PLNetworkManager.Instance?.MyLocalPawn; if (pw == null) return "nessun personaggio";
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (!Stations.Cfg.Panels.ContainsKey(all[i].GetIl2CppType().Name)) continue;
                        if (k++ == int.Parse(a[1])) { var t = all[i].transform; pw.transform.position = t.position + t.forward * 1.2f; return "spostato davanti al pannello"; }
                    }
                    return "pannello non trovato";
                }
                case "intro": return Ui.Intro();
                case "menu":
                {
                    var mm = PLNetworkManager.Instance?.MainMenu; if (mm == null) return "menu non pronto";
                    var m = typeof(PLMainMenu).GetMethod("OnActivate" + a[1]) ?? typeof(PLMainMenu).GetMethod(a[1], Type.EmptyTypes); if (m == null) return "voce sconosciuta";
                    m.Invoke(mm, null); return "attivata " + a[1] + ", menu attivi " + mm.GetActiveMenuCount();
                }
                case "buttons": return Ui.Buttons();
                case "press": return Ui.Press(string.Join(" ", a.Skip(1)));
                case "type": return Ui.TypeInto(a[1], string.Join(" ", a.Skip(2)));
                case "shot":
                {
                    string file = Path.Combine(Paths.GameRootPath, "..", "..", "docs", "prove", a[1] + (Plugin.Inst.Length > 0 ? "_" + Plugin.Inst : "") + ".png");
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    ScreenCapture.CaptureScreenshot(file);
                    return "schermata in " + Path.GetFullPath(file);
                }
                case "focus": return $"runInBackground={Application.runInBackground} isFocused={Application.isFocused}";
                case "uitree": return Ui.Tree(string.Join(" ", a.Skip(1)));
                case "uitext": return Ui.FindText(string.Join(" ", a.Skip(1)));
                case "ach":
                    if (a.Length == 1) { MelchiorHud.ShowPanel = !MelchiorHud.ShowPanel; return "pannello obiettivi " + (MelchiorHud.ShowPanel ? "aperto" : "chiuso"); }
                    if (a[1] == "page") { MelchiorHud.Page = int.Parse(a[2]); MelchiorHud.ShowPanel = true; return "pagina " + a[2]; }
                    if (a[1] == "unlock") return Achievements.Unlock(a[2]) ? "sbloccato" : "già raggiunto o sconosciuto";
                    if (a[1] == "reset") { Achievements.Reset(); return "progressi azzerati"; }
                    return "uso: ach | ach page <n> | ach unlock <id> | ach reset";
                default: return "comando sconosciuto";
            }
        }
    }
}

// Melchior Core — plugin base per PULSAR: Lost Colony (BepInEx 6, IL2CPP).
// Primo compito: portare il gioco dal cloud Photon al Subspace Relay locale.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace Melchior
{
    [BepInPlugin(Id, "Melchior Core", "0.1.0")]
    public class Plugin : BasePlugin
    {
        public const string Id = "melchior.core";
        internal static ManualLogSource L;
        // Seconda istanza sullo stesso computer (prove a due client): MELCHIOR_INSTANCE=2 separa profilo, obiettivi e console.
        internal static readonly string Inst = Environment.GetEnvironmentVariable("MELCHIOR_INSTANCE") ?? "";
        // Indirizzo del relay risolto una volta sola. Il gioco, se riceve un nome, lo risolve di continuo e sotto Wine
        // i nomi .local sono lenti al punto da bloccare il caricamento: gli passo sempre un indirizzo IPv4.
        static string relayAddress;
        internal static string RelayAddress
        {
            get
            {
                if (relayAddress != null) return relayAddress;
                string host = RelayHost.Value.Trim(), fallback = RelayFallback.Value.Trim();
                try
                {
                    if (!System.Net.IPAddress.TryParse(host, out _))
                    {
                        var v4 = System.Net.Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                        if (v4 != null) { L.LogInfo($"Relay {host} risolto in {v4}"); host = v4.ToString(); }
                        else if (fallback.Length > 0) { L.LogWarning($"Relay {host}: nessun indirizzo IPv4, uso l'indirizzo di riserva {fallback}."); host = fallback; }
                        else L.LogWarning($"Relay {host}: nessun indirizzo IPv4 trovato, uso il nome così com'è.");
                    }
                }
                catch (Exception e)
                {
                    if (fallback.Length > 0) { L.LogWarning($"Relay {host} non risolto ({e.Message}): uso l'indirizzo di riserva {fallback}."); host = fallback; }
                    else L.LogWarning($"Relay {host} non risolto ({e.Message}): uso il nome così com'è.");
                }
                return relayAddress = host;
            }
        }

        internal static string PerInstance(string file) => Inst.Length == 0 ? file : file + "." + Inst;
        internal static ConfigEntry<bool> RelayEnabled;
        internal static ConfigEntry<string> RelayHost, RelayFallback;
        internal static ConfigEntry<int> RelayPort;
        internal static ConfigEntry<bool> RelayTcp;
        internal static ConfigEntry<bool> NetTrace;
        internal static ConfigEntry<bool> AutoHostTest;
        internal static ConfigEntry<bool> RaritySelfTest;
        internal static ConfigEntry<bool> RarityShipTest;
        internal static ConfigEntry<bool> RarityLog;
        internal static ConfigEntry<bool> DevConsoleOn;

        public override void Load()
        {
            L = Log;
            RelayEnabled = Config.Bind("Relay", "Enabled", true, "Collega il gioco al server locale invece che al cloud Photon.");
            RelayHost = Config.Bind("Relay", "Host", "127.0.0.1", "Indirizzo del master server (Subspace Relay).");
            RelayFallback = Config.Bind("Relay", "Fallback", "", "Indirizzo IP da usare se il nome in Host non si risolve (es. 192.168.178.27 per Balthasar).");
            RelayPort = Config.Bind("Relay", "Port", 4530, "Porta TCP del master server.");
            RelayTcp = Config.Bind("Relay", "ForceTcp", true, "Il Subspace Relay parla solo TCP: lasciare attivo.");
            NetTrace = Config.Bind("Debug", "NetTrace", true, "Scrive nel log i cambi di stato della connessione.");
            AutoHostTest = Config.Bind("Debug", "AutoHostTest", false, "Solo collaudo: appena in lobby crea una stanza di prova, per verificare il passaggio al game server.");
            RaritySelfTest = Config.Bind("Debug", "RaritySelfTest", false, "Solo collaudo: dopo l'avvio prova il sistema di rarità e scrive l'esito nel log.");
            RarityShipTest = Config.Bind("Debug", "RarityShipTest", false, "Solo collaudo: a bordo della nave misura l'effetto degli affissi e mette un Prototipo in stiva.");
            RarityLog = Config.Bind("Debug", "RarityLog", false, "Scrive nel log ogni componente generato sopra il grado Standard.");
            DevConsoleOn = Config.Bind("Debug", "DevConsole", false, "Esegue i comandi scritti in BepInEx/config/melchior.cmd (vedi DevConsole.cs).");
            Rarity.Load(Paths.ConfigPath);
            Ui.Load(Paths.ConfigPath);
            Stations.Load(Paths.ConfigPath);
            Director.Load(Paths.ConfigPath);
            Service.Load(Paths.ConfigPath);
            Achievements.Load(System.IO.Path.Combine(Paths.PluginPath, "Melchior"), Paths.ConfigPath);
            try { RarityCtorHook.Install(); } catch (Exception e) { L.LogError("Detour dei costruttori non installato: " + e.Message); }
            var h = new Harmony(Id);
            h.PatchAll(typeof(Plugin).Assembly);
            L.LogInfo($"Melchior Core attivo. Relay: {(RelayEnabled.Value ? RelayHost.Value + ":" + RelayPort.Value : "disattivato")}");
        }
    }

    // Tutte le vie con cui il gioco apre la connessione al cloud finiscono su ConnectToMaster verso il relay.
    [HarmonyPatch]
    static class RelayRedirect
    {
        static bool busy;

        static IEnumerable<MethodBase> TargetMethods() =>
            typeof(PhotonNetwork).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name is "ConnectUsingSettings" or "ConnectToRegion" or "ConnectToBestCloudServer");

        static bool Prefix(MethodBase __originalMethod, object[] __args, ref bool __result)
        {
            if (!Plugin.RelayEnabled.Value || busy) return true;
            busy = true;
            try
            {
                string version = __args.OfType<string>().FirstOrDefault() ?? PhotonNetwork.gameVersion;
                if (Plugin.RelayTcp.Value)
                    PhotonNetwork.networkingPeer.TransportProtocol = ExitGames.Client.Photon.ConnectionProtocol.Tcp;
                string appId = PhotonNetwork.PhotonServerSettings.AppID;
                __result = PhotonNetwork.ConnectToMaster(Plugin.RelayAddress, Plugin.RelayPort.Value, appId, version);
                Plugin.L.LogInfo($"{__originalMethod.Name} -> relay {Plugin.RelayHost.Value}:{Plugin.RelayPort.Value} (versione '{version}', esito {__result})");
            }
            catch (Exception e) { Plugin.L.LogError("Reindirizzamento fallito: " + e); return true; }
            finally { busy = false; }
            return false;
        }
    }

    [HarmonyPatch(typeof(NetworkingPeer), nameof(NetworkingPeer.OnStatusChanged))]
    static class TraceStatus
    {
        static void Prefix(NetworkingPeer __instance, ExitGames.Client.Photon.StatusCode statusCode)
        {
            if (Plugin.NetTrace.Value) Plugin.L.LogInfo($"[net] stato {statusCode} (server {__instance.Server})");
        }
    }

    [HarmonyPatch(typeof(NetworkingPeer), nameof(NetworkingPeer.OnOperationResponse))]
    static class TraceResponse
    {
        static void Prefix(ExitGames.Client.Photon.OperationResponse operationResponse)
        {
            if (Plugin.NetTrace.Value)
                Plugin.L.LogInfo($"[net] risposta op {operationResponse.OperationCode} ret {operationResponse.ReturnCode} {operationResponse.DebugMessage}");
            if (Plugin.RelayEnabled.Value && operationResponse.OperationCode == 230 && operationResponse.ReturnCode == 0) Achievements.Unlock("MEL_RELAY");
        }
    }

    [HarmonyPatch(typeof(NetworkingPeer), nameof(NetworkingPeer.OnEvent))]
    static class TraceEvent
    {
        static int seen;
        static void Prefix(ExitGames.Client.Photon.EventData photonEvent)
        {
            if (Plugin.NetTrace.Value && seen < 40) { seen++; Plugin.L.LogInfo($"[net] evento {photonEvent.Code}"); }
        }
    }

    // Collaudo: crea una stanza appena il client è in lobby da qualche secondo.
    [HarmonyPatch(typeof(PhotonHandler), "Update")]
    static class AutoHost
    {
        static bool done; static System.Diagnostics.Stopwatch inLobby;
        static void Postfix()
        {
            SelfTest.Tick();
            DevConsole.Tick();
            Ui.Tick();
            MelchiorHud.Tick();
            StationConsole.Tick();
            Stations.Tick();
            Director.Tick();
            RivalForce.Tick();
            Missions.Tick();
            Saga.Tick();
            Loot.Tick();
            Panels.Tick();
            StationBots.Tick();
            Service.Tick();
            ServicePanel.Tick();
            if (done || !Plugin.AutoHostTest.Value) return;
            if (!PhotonNetwork.insideLobby) { inLobby = null; return; }
            inLobby ??= System.Diagnostics.Stopwatch.StartNew();
            if (inLobby.ElapsedMilliseconds < 4000) return;
            done = true;
            bool ok = PhotonNetwork.CreateRoom("melchior-collaudo");
            Plugin.L.LogInfo($"[collaudo] CreateRoom inviato: {ok}");
        }
    }

    [HarmonyPatch(typeof(NetworkingPeer), nameof(NetworkingPeer.DebugReturn))]
    static class TraceDebug
    {
        static void Prefix(ExitGames.Client.Photon.DebugLevel level, string message)
        {
            if (Plugin.NetTrace.Value && (int)level <= 2) Plugin.L.LogWarning($"[photon] {level}: {message}");
        }
    }
}

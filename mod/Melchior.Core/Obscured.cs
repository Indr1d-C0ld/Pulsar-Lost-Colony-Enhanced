// Lettura diretta dei valori cifrati dell'Anti-Cheat Toolkit (ObscuredInt: chiave a +0, valore nascosto a +4).
// La copia della struttura fatta dall'interop non coincide con quella del gioco e restituisce valori sbagliati.
using System;
using System.Runtime.InteropServices;

namespace Melchior
{
    public static class Obscured
    {
        public static int Int(IntPtr at) => Marshal.ReadInt32(at, 4) ^ Marshal.ReadInt32(at, 0);

        // Salti compiuti in questa galassia. PLServer.NumJumps resta a zero nelle partite normali, quindi il mod li conta da sé
        // (cambio del settore corrente) e li salva nella cronaca della galassia; sui client vale il conteggio locale.
        public static int Jumps() => Saga.Loaded && PhotonNetwork.isMasterClient ? Saga.State.jumps : local;
        static int local, lastSector = -1;
        public static void Reset() => lastSector = -1;
        public static void Tick()
        {
            int s = -1; try { s = PLServer.GetCurrentSector()?.ID ?? -1; } catch { }
            if (s < 0) return;
            if (lastSector >= 0 && s != lastSector) { local++; if (Saga.Loaded && PhotonNetwork.isMasterClient) { Saga.State.jumps++; Saga.MarkDirty(); } }
            lastSector = s;
        }
    }
}

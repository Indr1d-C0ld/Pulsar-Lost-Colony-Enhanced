// Rotta tracciata sulla mappa stellare: la destinazione di una taglia o di una crisi diventa una meta della rotta della nave,
// visibile a tutto l'equipaggio come quelle che traccia il capitano.
using System;
using System.Collections.Generic;

namespace Melchior
{
    public static class Course
    {
        static readonly List<int> ours = new(); // mete aggiunte dal mod (si tolgono quando non servono più)

        // Le mete della rotta sono RPC di PLServer: inviate a tutti, arrivano anche all'host e (dal gioco) a chi entra dopo.
        static void Rpc(string method, int sector)
        {
            var args = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(1);
            args[0] = new Il2CppSystem.Int32 { m_value = sector }.BoxIl2CppObject();
            PLServer.Instance.photonView.RPC(method, PhotonTargets.All, args);
        }

        public static void Plot(int sector)
        {
            if (sector < 0) return;
            try
            {
                foreach (int s in ours) if (s != sector) Rpc("RemoveCourseGoal", s);
                ours.Clear();
                Rpc("AddCourseGoal", sector); ours.Add(sector);
                Plugin.L.LogInfo($"[rotta] meta tracciata: settore {sector}");
            }
            catch (Exception e) { Plugin.L.LogWarning("Rotta: " + e.Message); }
        }

        public static void Clear()
        {
            try { foreach (int s in ours) Rpc("RemoveCourseGoal", s); } catch { }
            ours.Clear();
        }

        public static string Describe() { try { var l = PLServer.Instance.m_ShipCourseGoals; var r = new List<string>(); for (int i = 0; i < l.Count; i++) r.Add(l[i].ToString()); return "mete: " + (r.Count == 0 ? "nessuna" : string.Join(", ", r)); } catch (Exception e) { return e.Message; } }
    }
}

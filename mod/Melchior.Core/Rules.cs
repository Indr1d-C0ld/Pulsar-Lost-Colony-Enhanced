// Regole della galassia: tre stili di gioco scelti dal capitano all'inizio di ogni galassia (incontro dopo il primo salto,
// oppure da console), che regolano tutti i sistemi.
// - tranquilla: più calma fra un evento e l'altro, minaccia e taglie più lente, crisi più rare, compensi un po' più bassi;
// - frontiera: l'equilibrio di base;
// - spietata: più combattimenti, minaccia e taglie più rapide, crisi più frequenti, ma compensi più alti.
using System;

namespace Melchior
{
    public static class Rules
    {
        static bool Host { get { try { return PhotonNetwork.inRoom && PhotonNetwork.isMasterClient; } catch { return false; } } }
        public static string Mode => Host ? (Saga.Loaded && Saga.State.rules.Length > 0 ? Saga.State.rules : "frontiera") : GalaxyView.Current?.rules ?? "frontiera";
        public static string Name(string m) => m switch { "tranquilla" => "rotta tranquilla", "spietata" => "frontiera spietata", _ => "frontiera" };

        public static float Calm => Mode switch { "tranquilla" => 1.4f, "spietata" => 0.75f, _ => 1f };          // durata della calma del direttore
        public static float ThreatGrowth => Mode switch { "tranquilla" => 0.6f, "spietata" => 1.5f, _ => 1f };   // crescita della grande minaccia
        public static float Heat => Mode switch { "tranquilla" => 0.6f, "spietata" => 1.4f, _ => 1f };           // taglia sulla nostra testa
        public static float Reward => Mode switch { "tranquilla" => 0.9f, "spietata" => 1.25f, _ => 1f };        // compensi dei contratti
        public static float Encounters => Mode switch { "tranquilla" => 1.3f, "spietata" => 0.85f, _ => 1f };    // incontri all'arrivo
        public static float CrisisGap => Mode switch { "tranquilla" => 1.4f, "spietata" => 0.7f, _ => 1f };      // salti fra una crisi e l'altra

        public static void Set(string mode)
        {
            if (!Host || !Saga.Loaded) return;
            Saga.State.rules = mode; Saga.MarkDirty(); GalaxyView.Dirty();
            Director.Notify("REGOLE DELLA GALASSIA", mode switch
            {
                "tranquilla" => "Rotta tranquilla: più tempo fra un pericolo e l'altro, più storie, una minaccia che cresce piano. I compensi sono un po' più bassi.",
                "spietata" => "Frontiera spietata: combattimenti più frequenti, una minaccia che cresce in fretta, taglie che salgono rapide. In cambio, i compensi sono più alti.",
                _ => "Frontiera: l'equilibrio fra pericolo, storie e compensi pensato per questa galassia.",
            });
            Saga.Chronicle($"Regole della galassia: {Name(mode)}.");
        }
    }
}

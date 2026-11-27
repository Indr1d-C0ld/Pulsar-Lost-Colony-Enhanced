// Diplomazia fra le fazioni: ogni coppia (Colonial Union, Alliance of Gentlemen, W.D. Corporation, Polytechnic) ha un
// rapporto da -100 a +100 che evolve da solo, a ogni passo della guerra: guerra, tensione, pace, alleanza. Gli Infetti
// sono in guerra con tutti. I cambi di stato (trattati, rotture, dichiarazioni di guerra) finiscono nel notiziario.
// Conseguenze: chi è in pace o alleato non si contende i settori; chi è in guerra attacca il doppio; una dichiarazione di
// guerra può aprire la crisi "guerra aperta"; gli alleati condividono un po' della reputazione dell'equipaggio.
// L'equipaggio influisce con le ambasciate (Contracts.cs) e con le scelte degli incontri.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Melchior
{
    public static class Diplomacy
    {
        public static readonly int[] F = { 0, 1, 2, 5 };
        static readonly System.Random rng = new();
        static readonly Dictionary<string, int> Defaults = new() { ["0-1"] = 5, ["0-2"] = -25, ["0-5"] = 25, ["1-2"] = -10, ["1-5"] = 10, ["2-5"] = 0 };
        static readonly string[] Places = { "Kepler", "Tycho", "Cornelia", "Sirio", "Nuova Ravenna", "Hadley", "Vesta", "Cerbero", "Aldebaran", "Solaria" };
        static Dictionary<string, int> R => Saga.State.relations ??= new Dictionary<string, int>();
        static string Key(int a, int b) => a < b ? $"{a}-{b}" : $"{b}-{a}";

        public static int Rel(int a, int b)
        {
            if (a == b) return 100; if (a == 4 || b == 4) return -100; if (a < 0 || b < 0) return 0;
            return R.TryGetValue(Key(a, b), out var v) ? v : Defaults.TryGetValue(Key(a, b), out var d) ? d : 0;
        }
        public static int Status(int v) => v >= 50 ? 2 : v >= 15 ? 1 : v > -30 ? 0 : -1; // 2 alleanza, 1 pace, 0 tensione, -1 guerra
        public static string StatusName(int s) => s switch { 2 => "alleanza", 1 => "pace", -1 => "guerra", _ => "tensione" };
        public static int StatusOf(int a, int b) => Status(Rel(a, b));

        // Chi può contendere un settore a chi (per la guerra sulla mappa).
        public static bool CanAttack(int atk, int def) => def < 0 || def == 6 || atk == 4 || def == 4 || StatusOf(atk, def) <= 0;
        public static int AttackWeight(int atk, int def) => def < 0 || def == 6 ? 1 : StatusOf(atk, def) == -1 || atk == 4 || def == 4 ? 2 : 1;

        public static void Change(int a, int b, int d, string why)
        {
            if (a == b || a < 0 || b < 0 || a == 4 || b == 4 || Array.IndexOf(F, a) < 0 || Array.IndexOf(F, b) < 0) return;
            int before = StatusOf(a, b); R[Key(a, b)] = Math.Clamp(Rel(a, b) + d, -100, 100); int after = StatusOf(a, b);
            Saga.MarkDirty(); GalaxyView.Dirty();
            if (before != after) Announce(a, b, before, after, why);
        }

        static void Announce(int a, int b, int before, int after, string why)
        {
            string A = GalaxyWar.Name(a), B = GalaxyWar.Name(b), place = Places[rng.Next(Places.Length)], text;
            if (after > before)
                text = after switch
                {
                    2 => $"Firmata l'alleanza di {place}: {A} e {B} combatteranno fianco a fianco.",
                    1 => before == -1 ? $"Armistizio fra {A} e {B}: le flotte si ritirano dal fronte comune." : $"Trattato di {place}: {A} e {B} firmano la pace.",
                    _ => $"Cessate il fuoco fra {A} e {B}. Nessuno si fida, ma si smette di sparare.",
                };
            else
                text = after switch
                {
                    -1 => $"Dichiarazione di guerra: {A} e {B} richiamano gli ambasciatori e mobilitano le flotte.",
                    0 => $"Rottura diplomatica fra {A} e {B}: il trattato di pace non è più rispettato.",
                    _ => $"L'alleanza fra {A} e {B} si incrina: restano in pace, ma ognuno per sé.",
                };
            if (why != null) text += " " + why + ".";
            News.Post("fronte", text);
            Saga.Chronicle(text);
            if (after == -1 && Crises.Cur == null && !Crises.Starting && rng.NextDouble() < 0.5) Crises.StartWar(a, b);
            Plugin.L.LogInfo($"[diplomazia] {A}-{B}: {StatusName(before)} -> {StatusName(after)}");
        }

        // Un passo di politica galattica: i rapporti si muovono un po' da soli.
        public static void Step()
        {
            for (int i = 0; i < F.Length; i++)
                for (int j = i + 1; j < F.Length; j++)
                {
                    int a = F[i], b = F[j], v = Rel(a, b);
                    int d = rng.Next(-4, 5) + (Calling.Is("tregua") ? 3 : 0);
                    // nemici comuni avvicinano; un fronte lungo allontana; tutto tende piano verso la tensione
                    int common = F.Count(c => c != a && c != b && StatusOf(a, c) == -1 && StatusOf(b, c) == -1);
                    d += common * 2;
                    int front = GalaxyWar.FrontCounts(a).GetValueOrDefault(b);
                    if (front > 12) d -= 1;
                    if (v > 30) d -= 1; if (v < -40) d += 1;
                    d += Leaders.DiploBias(a, b); // il carattere dei capi
                    if (rng.NextDouble() < 0.04) d += rng.Next(2) == 0 ? -18 : 18; // colpo di scena: un incidente di frontiera o un vertice a sorpresa
                    Change(a, b, d, null);
                }
        }

        // Gli alleati di una fazione condividono un terzo di ciò che l'equipaggio guadagna o perde con lei.
        public static IEnumerable<int> Allies(int f) => F.Where(x => x != f && StatusOf(f, x) == 2);
        public static IEnumerable<int> Enemies(int f) => F.Where(x => x != f && StatusOf(f, x) == -1);

        public static string Summary()
        {
            var pairs = new List<(int a, int b, int v)>();
            for (int i = 0; i < F.Length; i++) for (int j = i + 1; j < F.Length; j++) pairs.Add((F[i], F[j], Rel(F[i], F[j])));
            return string.Join(" · ", pairs.Where(p => StatusOf(p.a, p.b) != 0).OrderBy(p => p.v).Select(p => $"{Factions.Abbr(p.a)}–{Factions.Abbr(p.b)} {StatusName(Status(p.v))}"));
        }
        public static string Describe()
        {
            var sb = new List<string>();
            for (int i = 0; i < F.Length; i++) for (int j = i + 1; j < F.Length; j++) sb.Add($"{Factions.Abbr(F[i])}-{Factions.Abbr(F[j])} {Rel(F[i], F[j])} ({StatusName(StatusOf(F[i], F[j]))})");
            return string.Join(", ", sb);
        }
        public static string Set(int a, int b, int v) { int before = StatusOf(a, b); R[Key(a, b)] = Math.Clamp(v, -100, 100); int after = StatusOf(a, b); if (before != after) Announce(a, b, before, after, "Prova"); Saga.MarkDirty(); return Describe(); }
    }
}

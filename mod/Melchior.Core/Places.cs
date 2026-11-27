// Nomi propri dei settori: quello del gioco quando esiste, altrimenti uno generato, sempre lo stesso per galassia e
// settore. Nei testi compare insieme al numero («settore 706 «Kessil»»), così resta ritrovabile sulla mappa stellare.
using System;
using System.Collections.Generic;

namespace Melchior
{
    public static class Places
    {
        static readonly string[] A = { "Ka", "Ve", "Lo", "Tar", "Mi", "Ses", "Ora", "Un", "Bel", "Cor", "Dra", "El", "Fen", "Gal", "Hel", "Ith", "Jor", "Kel", "Lyr", "Mor", "Nal", "Oss", "Pra", "Quel", "Ras", "Sil", "Thar", "Ul", "Vor", "Zan", "Aur", "Bri", "Cas", "Ery" };
        static readonly string[] B = { "ra", "li", "ne", "va", "tho", "si", "ri", "na", "de", "lu", "ka", "mo", "", "", "" };
        static readonly string[] C = { "s", "n", "r", "ia", "ar", "on", "is", "el", "ix", "um", "a", "e", "os", "ea", "ion" };
        static readonly string[] Suffix = { " Prime", " Minore", " Maggiore", " II", " III", " IV", " Alfa", " Beta" };
        static readonly string[] Form = { "la Nube di ", "la Distesa di ", "l'Abisso di ", "la Soglia di ", "il Cuore di ", "il Passo di " };
        static readonly Dictionary<long, string> cache = new();

        static int Seed() { try { return (int)PLServer.Instance.GalaxySeed; } catch { return 0; } }

        public static string Name(int id)
        {
            int seed = Seed(); long k = ((long)seed << 20) ^ id; if (cache.TryGetValue(k, out var n)) return n;
            string native = null; try { native = PLServer.GetSectorWithID(id)?.Name; } catch { }
            if (!string.IsNullOrEmpty(native)) { native = System.Text.RegularExpressions.Regex.Replace(native, @"\s*\(\d+\)$", ""); if (int.TryParse(native, out _)) native = null; }
            if (string.IsNullOrEmpty(native))
            {
                var r = new Random(unchecked(seed * 7 + id * 31337 + 11));
                string w = A[r.Next(A.Length)] + B[r.Next(B.Length)] + C[r.Next(C.Length)];
                double x = r.NextDouble();
                native = x < 0.12 ? Form[r.Next(Form.Length)] + w : x < 0.35 ? w + Suffix[r.Next(Suffix.Length)] : w;
            }
            if (cache.Count > 4000) cache.Clear();
            cache[k] = native; return native;
        }
        // «706 «Kessil»»: da usare dopo la parola «settore».
        public static string Of(int id) => id < 0 ? "sconosciuto" : $"{id} «{Name(id)}»";
        public static string Of(int? id) => id == null ? "sconosciuto" : Of(id.Value);
        public static string Of(string id) => int.TryParse(id, out var v) ? Of(v) : id;
    }
}

// Scanner dell'heap del gioco, per la caccia alle perdite di memoria. Usa la fotografia della memoria gestita che IL2CPP
// mette a disposizione del Memory Profiler di Unity (il2cpp_capture_memory_snapshot): sezioni dell'heap copiate,
// descrizione di tutti i tipi con i loro campi, variabili statiche e handle del GC. Da queste radici percorre gli oggetti
// seguendo i campi riferimento, conta istanze e byte per tipo e annota, per ogni tipo, da quale campo vengono raggiunti
// i suoi oggetti. Con «heap diff» si confrontano due fotografie: il tipo che cresce e il campo che lo trattiene.
// Solo strumento di sviluppo (console), non gira mai da solo.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace Melchior
{
    public static class HeapScan
    {
        sealed class Sec { public ulong start, end; public IntPtr bytes; }
        sealed class TType
        {
            public uint flags, size, elem; public string name = "?"; public ulong klass; public bool isValue, isArray;
            public int baseIdx = -1;
            public List<(uint off, int type, string field, bool isStatic)> fields = new();
            public List<(uint off, string field)> refs; // riferimenti (anche dentro le strutture incorporate), offset dall'inizio dell'oggetto
        }
        static Sec[] secs; static TType[] types; static Dictionary<ulong, int> byKlass;
        static uint hdr, arrHdr, arrLenOff;
        static Dictionary<ulong, (long n, long bytes)> last; static Dictionary<ulong, Dictionary<string, int>> lastRef; static string lastTag = ""; // per classe: gli indici dei tipi possono cambiare fra due fotografie

        static bool Read(ulong addr, out ulong v)
        {
            v = 0; int lo = 0, hi = secs.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1; var s = secs[mid];
                if (addr < s.start) hi = mid - 1; else if (addr + 8 > s.end) lo = mid + 1;
                else { v = (ulong)Marshal.ReadInt64(s.bytes + (int)(addr - s.start)); return true; }
            }
            return false;
        }
        static bool InHeap(ulong addr)
        {
            if (addr == 0 || (addr & 7) != 0) return false; int lo = 0, hi = secs.Length - 1;
            while (lo <= hi) { int mid = (lo + hi) >> 1; var s = secs[mid]; if (addr < s.start) hi = mid - 1; else if (addr + 16 > s.end) lo = mid + 1; else return true; }
            return false;
        }

        // riferimenti di un tipo, comprese le classi base e le strutture incorporate
        static List<(uint off, string field)> Refs(int ti, int depth = 0)
        {
            var t = types[ti]; if (t.refs != null) return t.refs;
            var l = new List<(uint, string)>(); t.refs = l; if (depth > 20) return l;
            if (t.baseIdx >= 0 && !t.isArray && t.baseIdx != ti) l.AddRange(Refs(t.baseIdx, depth + 1));
            foreach (var f in t.fields)
            {
                if (f.isStatic || f.type < 0 || f.type >= types.Length) continue;
                var ft = types[f.type];
                if (!ft.isValue) l.Add((f.off, f.field));
                else if (f.type != ti) foreach (var (o, n) in Refs(f.type, depth + 1)) l.Add((f.off + o - hdr, f.field + "." + n));
            }
            return l;
        }

        public static string Run(string tag, string pathType = null)
        {
            IntPtr snap = IntPtr.Zero; var t0 = DateTime.Now;
            try
            {
                snap = IL2CPP.il2cpp_capture_memory_snapshot(); if (snap == IntPtr.Zero) return "fotografia non disponibile";
                // Il2CppManagedMemorySnapshot (x64): heap {count, ptr}, stacks {count, ptr}, metadata {count, ptr}, gcHandles {count, ptr}, runtime info
                uint secCount = (uint)Marshal.ReadInt32(snap, 0); IntPtr secPtr = Marshal.ReadIntPtr(snap, 8);
                uint typeCount = (uint)Marshal.ReadInt32(snap, 32); IntPtr typePtr = Marshal.ReadIntPtr(snap, 40);
                uint handleCount = (uint)Marshal.ReadInt32(snap, 48); IntPtr handlePtr = Marshal.ReadIntPtr(snap, 56);
                uint ptrSize = (uint)Marshal.ReadInt32(snap, 64); hdr = (uint)Marshal.ReadInt32(snap, 68); arrHdr = (uint)Marshal.ReadInt32(snap, 72); arrLenOff = (uint)Marshal.ReadInt32(snap, 80);
                if (ptrSize != 8 || hdr != 16 || typeCount == 0 || typeCount > 200000) return $"struttura inattesa: puntatori {ptrSize}, intestazione {hdr}, tipi {typeCount}";
                var sl = new List<Sec>();
                for (int i = 0; i < secCount; i++) { var p = secPtr + i * 24; ulong st = (ulong)Marshal.ReadInt64(p, 0); uint sz = (uint)Marshal.ReadInt32(p, 8); var b = Marshal.ReadIntPtr(p, 16); if (b != IntPtr.Zero && sz > 0) sl.Add(new Sec { start = st, end = st + sz, bytes = b }); }
                secs = sl.OrderBy(s => s.start).ToArray();
                types = new TType[typeCount]; byKlass = new Dictionary<ulong, int>();
                for (int i = 0; i < typeCount; i++)
                {
                    // Il2CppMetadataType (x64): flags, fields*, fieldCount, staticsSize, statics*, baseOrElement, name*, assembly*, typeInfoAddress, size
                    var p = typePtr + i * 72; var t = new TType();
                    t.flags = (uint)Marshal.ReadInt32(p, 0); var fptr = Marshal.ReadIntPtr(p, 8); uint fc = (uint)Marshal.ReadInt32(p, 16);
                    t.elem = (uint)Marshal.ReadInt32(p, 32); var np = Marshal.ReadIntPtr(p, 40); t.name = np == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(np) ?? "?";
                    t.klass = (ulong)Marshal.ReadInt64(p, 56); t.size = (uint)Marshal.ReadInt32(p, 64);
                    t.isValue = (t.flags & 1) != 0; t.isArray = (t.flags & 2) != 0; if (!t.isArray) t.baseIdx = (int)t.elem == -1 ? -1 : (int)t.elem;
                    for (int k = 0; k < fc && fptr != IntPtr.Zero; k++)
                    {
                        var f = fptr + k * 24; var fn = Marshal.ReadIntPtr(f, 8);
                        t.fields.Add(((uint)Marshal.ReadInt32(f, 0), Marshal.ReadInt32(f, 4), fn == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(fn) ?? "?", Marshal.ReadByte(f, 16) != 0));
                    }
                    types[i] = t; if (t.klass != 0) byKlass[t.klass] = i;
                }
                // radici: handle del GC e variabili statiche
                var visited = new HashSet<ulong>(); var stack = new Stack<(ulong addr, int from, string field, ulong parent)>();
                bool census = pathType == "morti"; if (census) pathType = null;
                bool wantLive = pathType != null && pathType.StartsWith("vivo:"); if (wantLive) pathType = pathType[5..]; int seen = 0; var carry = new Dictionary<ulong, (bool live, string owner)>(); var edges = new Dictionary<string, int>();
                var parent = pathType != null ? new Dictionary<ulong, (ulong p, string f)>() : null; var samples = new List<ulong>();
                for (int i = 0; i < handleCount; i++) { ulong a = (ulong)Marshal.ReadInt64(handlePtr, i * 8); if (InHeap(a)) stack.Push((a, -1, "handle GC", 0)); }
                for (int i = 0; i < typeCount; i++)
                {
                    var p = typePtr + i * 72; uint ss = (uint)Marshal.ReadInt32(p, 20); var sp = Marshal.ReadIntPtr(p, 24); if (ss == 0 || sp == IntPtr.Zero) continue;
                    foreach (var f in types[i].fields)
                    {
                        if (!f.isStatic || f.type < 0 || f.type >= types.Length) continue; var ft = types[f.type];
                        var offs = ft.isValue ? Refs(f.type).Select(r => (off: f.off + r.off - hdr, name: f.field + "." + r.field)).ToList() : new List<(uint off, string name)> { (f.off, f.field) };
                        foreach (var (off, name) in offs) { if (off + 8 > ss) continue; ulong a = (ulong)Marshal.ReadInt64(sp, (int)off); if (InHeap(a)) stack.Push((a, i, "statico " + name, 0)); }
                    }
                }
                var count = new Dictionary<int, (long n, long bytes)>(); var refBy = new Dictionary<int, Dictionary<string, int>>(); long unknown = 0;
                while (stack.Count > 0)
                {
                    var (addr, from, field, par) = stack.Pop(); if (!visited.Add(addr)) continue;
                    if (!Read(addr, out var klass) || !byKlass.TryGetValue(klass, out var ti)) { unknown++; continue; }
                    var t = types[ti]; long bytes = t.size;
                    // censimento: chi (oggetto Unity vivo) punta a oggetti Unity già distrutti
                    (bool live, string owner) mine = (true, "radice " + field);
                    if (census)
                    {
                        carry.TryGetValue(addr, out var inh); if (inh.owner == null) inh = (true, $"radice {field} [{t.name}]");
                        uint cp = CachedPtrOffset(ti); mine = inh;
                        if (cp != 0) { bool alive = Read(addr + cp, out var nat) && nat != 0; mine = (alive, t.name); }
                    }
                    void Edge(ulong e, string f)
                    {
                        if (!census) return;
                        if (!Read(e, out var ek) || !byKlass.TryGetValue(ek, out var eti)) return; uint ecp = CachedPtrOffset(eti);
                        // gli oggetti non Unity (liste, dizionari, chiusure) ereditano vita e proprietario da chi li raggiunge per primo
                        if (ecp == 0) { if (!carry.ContainsKey(e)) carry[e] = (mine.live, mine.owner + (f.Length > 0 && !mine.owner.Contains('.') ? "." + f : "")); return; }
                        if (!mine.live) return;
                        if (Read(e + ecp, out var en) && en == 0) { string key = (mine.owner.Contains('.') ? mine.owner : mine.owner + "." + f) + " -> " + types[eti].name; edges[key] = (edges.TryGetValue(key, out var c0) ? c0 : 0) + 1; }
                    }
                    if (parent != null)
                    {
                        parent[addr] = (par, (from >= 0 ? types[from].name : "") + "." + field);
                        // campioni: oggetti del tipo cercato già distrutti lato Unity (m_CachedPtr a zero)
                        if (samples.Count < 6 && t.name == pathType)
                        {
                            uint cp = CachedPtrOffset(ti); bool dead = cp != 0 && Read(addr + cp, out var nat) && nat == 0;
                            if (wantLive ? !dead && (seen++ % 97 == 13) : (cp == 0 || dead)) samples.Add(addr); // tra i vivi, campioni sparsi
                        }
                    }
                    string who = (from >= 0 ? types[from].name : "") + (field.Length > 0 ? "." + field : "");
                    if (!refBy.TryGetValue(ti, out var rb)) refBy[ti] = rb = new Dictionary<string, int>(); if (rb.Count < 200 || rb.ContainsKey(who)) rb[who] = (rb.TryGetValue(who, out var c) ? c : 0) + 1;
                    if (t.isArray)
                    {
                        if (!Read(addr + arrLenOff, out var len) || len > 50_000_000) continue;
                        var et = t.elem < types.Length ? types[t.elem] : null; if (et == null) continue;
                        if (!et.isValue) { bytes = arrHdr + (long)len * 8; for (ulong i = 0; i < len; i++) if (Read(addr + arrHdr + i * 8, out var e) && InHeap(e)) { Edge(e, "[]"); stack.Push((e, ti, "[]", addr)); } }
                        else
                        {
                            long es = Math.Max(1, (long)et.size - hdr); bytes = arrHdr + (long)len * es; var er = Refs((int)t.elem);
                            if (er.Count > 0) for (ulong i = 0; i < len; i++) foreach (var (o, n) in er) if (Read(addr + arrHdr + i * (ulong)es + o - hdr, out var e) && InHeap(e)) { Edge(e, "[]." + n); stack.Push((e, ti, "[]." + n, addr)); }
                        }
                    }
                    else foreach (var (o, n) in Refs(ti)) if (Read(addr + o, out var e) && InHeap(e)) { Edge(e, n); stack.Push((e, ti, n, addr)); }
                    var cur = count.TryGetValue(ti, out var cc) ? cc : (0, 0); count[ti] = (cur.n + 1, cur.bytes + bytes);
                }
                long totN = count.Values.Sum(x => x.n), totB = count.Values.Sum(x => x.bytes);
                var sb = new System.Text.StringBuilder($"fotografia «{tag}»: {secs.Length} sezioni, {types.Length} tipi, {handleCount} handle; oggetti raggiunti {totN} ({totB / 1048576} MB), non riconosciuti {unknown}; {(DateTime.Now - t0).TotalSeconds:0.0} s\n");
                sb.Append("per byte:\n"); foreach (var kv in count.OrderByDescending(x => x.Value.bytes).Take(250)) sb.Append($"  {kv.Value.bytes / 1024,9} KB {kv.Value.n,9}  {types[kv.Key].name}   <- {Top(refBy, kv.Key)}\n");
                if (last != null)
                {
                    sb.Append($"\ncrescita rispetto a «{lastTag}» (per byte):\n");
                    var diff = count.Select(kv => (ti: kv.Key, dn: kv.Value.n - (last.TryGetValue(types[kv.Key].klass, out var l) ? l.n : 0), db: kv.Value.bytes - (last.TryGetValue(types[kv.Key].klass, out var l2) ? l2.bytes : 0))).OrderByDescending(x => x.db).Take(40);
                    foreach (var d in diff) sb.Append($"  {d.db / 1024,9} KB {d.dn,+9}  {types[d.ti].name}   <- {TopDiff(refBy, d.ti)}\n");
                }
                if (census) { sb.Append($"\nriferimenti da oggetti vivi a oggetti già distrutti ({edges.Values.Sum()} in tutto), per campo d'origine:\n"); foreach (var kv in edges.OrderByDescending(x => x.Value).Take(60)) sb.Append($"  {kv.Value,7}  {kv.Key}\n"); }
                if (parent != null)
                {
                    sb.Append($"\npercorsi verso la radice per {pathType} ({(wantLive ? "oggetti vivi" : "oggetti già distrutti lato Unity")}), {samples.Count} campioni:\n");
                    foreach (var smp in samples)
                    {
                        var cur = smp; int guard = 0; var chain = new List<string>();
                        while (cur != 0 && guard++ < 5000 && parent.TryGetValue(cur, out var pp)) { Read(cur, out var kl); string tn = byKlass.TryGetValue(kl, out var x) ? types[x].name : "?"; chain.Add($"      {tn}  <-  {pp.f}"); cur = pp.p; }
                        sb.Append($"  • catena di {chain.Count} anelli; i primi 6 e gli ultimi 14:\n");
                        foreach (var l in chain.Take(6)) sb.Append(l + "\n"); if (chain.Count > 20) sb.Append("      ...\n"); foreach (var l in chain.Skip(Math.Max(6, chain.Count - 14))) sb.Append(l + "\n");
                    }
                }
                last = count.ToDictionary(kv => types[kv.Key].klass, kv => kv.Value); lastRef = refBy.ToDictionary(kv => types[kv.Key].klass, kv => kv.Value); lastTag = tag;
                string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, $"heap_{tag}.txt"); System.IO.File.WriteAllText(path, sb.ToString());
                return $"oggetti {totN} ({totB / 1048576} MB), {(DateTime.Now - t0).TotalSeconds:0.0} s; rapporto in {path}";
            }
            catch (Exception e) { return "errore: " + e; }
            finally { if (snap != IntPtr.Zero) IL2CPP.il2cpp_free_captured_memory_snapshot(snap); secs = null; types = null; byKlass = null; }
        }
        // posizione di UnityEngine.Object.m_CachedPtr nella gerarchia del tipo (0 se non è un oggetto Unity)
        static uint CachedPtrOffset(int ti)
        {
            for (int g = 0, cur = ti; cur >= 0 && cur < types.Length && g < 30; g++)
            {
                var f = types[cur].fields.FirstOrDefault(x => x.field == "m_CachedPtr" && !x.isStatic); if (f.field != null) return f.off;
                if (types[cur].baseIdx == cur) break; cur = types[cur].baseIdx;
            }
            return 0;
        }
        static string Top(Dictionary<int, Dictionary<string, int>> rb, int ti) => rb.TryGetValue(ti, out var d) ? string.Join(", ", d.OrderByDescending(x => x.Value).Take(3).Select(x => $"{x.Key} ×{x.Value}")) : "";
        static string TopDiff(Dictionary<int, Dictionary<string, int>> rb, int ti)
        {
            if (!rb.TryGetValue(ti, out var d)) return ""; Dictionary<string, int> o = null; lastRef?.TryGetValue(types[ti].klass, out o);
            return string.Join(", ", d.Select(x => (x.Key, dv: x.Value - (o != null && o.TryGetValue(x.Key, out var v) ? v : 0))).Where(x => x.dv != 0).OrderByDescending(x => x.dv).Take(3).Select(x => $"{x.Key} {x.dv:+0;-0}"));
        }
    }
}

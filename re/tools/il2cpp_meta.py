#!/usr/bin/env python3
"""Parser minimale di global-metadata.dat (IL2CPP v27, Unity 2020.3).
Estrae tipi, campi, metodi (con token), costanti enum e string literal senza toccare il binario.
Uso: il2cpp_meta.py <global-metadata.dat> <outdir>"""
import struct, sys, json, os
src, out = sys.argv[1], sys.argv[2]
d = open(src, 'rb').read()
magic, ver = struct.unpack_from('<Ii', d, 0)
assert magic == 0xFAB11BAF and ver == 27, (hex(magic), ver)
H = struct.unpack_from('<62i', d, 8)
names = ['stringLiteral','stringLiteralData','string','events','properties','methods','paramDefault',
         'fieldDefault','defaultData','fieldMarshaled','parameters','fields','genericParams','constraints',
         'genericContainers','nested','interfaces','vtable','interfaceOffsets','typeDefs','images']
S = {n: (H[2*i], H[2*i+1]) for i, n in enumerate(names)}
so = S['string'][0]
def s(i):
    e = d.index(b'\0', so + i); return d[so+i:e].decode('utf8', 'replace')
def tab(n, fmt):
    o, sz = S[n]; st = struct.Struct(fmt); assert sz % st.size == 0, (n, sz, st.size)
    return [st.unpack_from(d, o + k) for k in range(0, sz, st.size)]
methods = tab('methods', '<IiiiiIHHHH')
types   = tab('typeDefs', '<iiiiiiiIiiiiiiii8HII')
fields  = tab('fields', '<iiI')
params  = tab('parameters', '<iIi')
images  = tab('images', '<iiiiiiiIii')
fdef    = tab('fieldDefault', '<iii')
lits    = tab('stringLiteral', '<Ii')
ddo = S['defaultData'][0]
fd = sorted(fdef, key=lambda x: x[2]); dsz = {}
for a, b in zip(fd, fd[1:]): dsz[a[0]] = b[2] - a[2]
fdm = {f: di for f, t, di in fdef}
def const(fi):
    di = fdm.get(fi)
    if di is None or di < 0: return None
    n = dsz.get(fi, 4)
    if n in (1, 2, 4, 8): return int.from_bytes(d[ddo+di:ddo+di+n], 'little', signed=True)
    return None
lo = S['stringLiteralData'][0]
with open(os.path.join(out, 'string_literals.txt'), 'w') as f:
    for ln, di in lits: f.write(d[lo+di:lo+di+ln].decode('utf8', 'replace').replace('\n', '\\n') + '\n')
mmap = {}
for im in images:
    iname = s(im[0]); ts, tc = im[2], im[3]
    with open(os.path.join(out, 'dump_' + iname.replace('.dll', '') + '.txt'), 'w') as f:
        for ti in range(ts, ts + tc):
            t = types[ti]
            full = (s(t[1]) + '.' if s(t[1]) else '') + s(t[0])
            if t[3] >= 0: full += '  [nested]'
            mc, pc, fc = t[16], t[17], t[18]
            isenum = (t[24] >> 1) & 1
            f.write(f"\n{'enum' if isenum else 'class'} {full}  // typeIdx {ti}\n")
            for fi in range(t[8], t[8] + fc):
                c = const(fi) if isenum else None
                f.write(f"  F {s(fields[fi][0])}" + (f" = {c}" if c is not None else '') + '\n')
            for mi in range(t[9], t[9] + mc):
                m = methods[mi]
                ps = ', '.join(s(params[p][0]) for p in range(m[3], m[3] + m[9]))
                f.write(f"  M {s(m[0])}({ps})  // tok {m[5]:#x}\n")
                mmap.setdefault(iname, []).append((m[5] & 0xFFFFFF, full.replace('  [nested]', ''), s(m[0])))
json.dump(mmap, open(os.path.join(out, 'method_tokens.json'), 'w'))
print('types', len(types), 'methods', len(methods), 'fields', len(fields), 'literals', len(lits))
for im in images: print(f"{im[3]:6d} {s(im[0])}")

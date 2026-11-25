#!/usr/bin/env python3
"""Risolve token metodo -> indirizzo virtuale leggendo le Il2CppCodeGenModule da GameAssembly.dll (PE64).
Uso: il2cpp_addrs.py <GameAssembly.dll> <method_tokens.json> <out.tsv> [modulo.dll ...]"""
import struct, sys, json, mmap
dll, tok, out, mods = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:] or ['Assembly-CSharp.dll']
f = open(dll, 'rb'); d = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
pe = struct.unpack_from('<I', d, 0x3c)[0]
nsec = struct.unpack_from('<H', d, pe + 6)[0]; opt = pe + 24
base = struct.unpack_from('<Q', d, opt + 24)[0]
so = opt + struct.unpack_from('<H', d, pe + 20)[0]
secs = [struct.unpack_from('<8sIIII', d, so + 40*i) for i in range(nsec)]  # name, vsize, va, rawsize, rawptr
def off2va(o):
    for n, vs, va, rs, rp in secs:
        if rp <= o < rp + rs: return base + va + o - rp
def va2off(v):
    v -= base
    for n, vs, va, rs, rp in secs:
        if va <= v < va + max(vs, rs): return rp + v - va
T = json.load(open(tok)); rows = []
print('imagebase', hex(base), [(s[0].rstrip(b'\0').decode(), hex(s[2])) for s in secs])
for m in mods:
    pos = -1; found = False
    while not found:
        pos = d.find(m.encode() + b'\0', pos + 1)
        if pos < 0: break
        if d[pos-1] != 0: continue
        ptr = struct.pack('<Q', off2va(pos)); p = -1
        while True:
            p = d.find(ptr, p + 1)
            if p < 0: break
            cnt, _, mp = struct.unpack_from('<IIQ', d, p + 8)
            o = va2off(mp) if mp > base else None
            if o and 0 < cnt < 200000:
                ptrs = struct.unpack_from(f'<{cnt}Q', d, o)
                print(m, 'methodPointerCount', cnt, 'tabella @', hex(mp))
                for rid, cls, name in T[m]:
                    if 0 < rid <= cnt and ptrs[rid-1]: rows.append((ptrs[rid-1], m, cls, name))
                found = True; break
rows.sort()
with open(out, 'w') as o:
    for a, m, c, n in rows: o.write(f"{a:#x}\t{m}\t{c}\t{n}\n")
print('metodi risolti', len(rows))

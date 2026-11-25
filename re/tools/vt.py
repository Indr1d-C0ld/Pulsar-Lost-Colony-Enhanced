import sys,struct,re
sys.argv=[sys.argv[0]]+sys.argv[1:]
import lay as L
vt=L.tab('vtable','<I')
def vtable(name):
    for ti,t in enumerate(L.types):
        if L.tdname(ti)==name:
            vs,vc=t[14],t[21]
            out=[]
            for k in range(vc):
                e=vt[vs+k][0]; ty=(e&0xE0000000)>>29; idx=(e&0x1FFFFFFE)>>1
                try: n=L.mdef(idx) if ty==3 else (L.mref(idx) if ty==6 else '?%d'%ty)
                except Exception as ex: n='err'
                out.append(n)
            return out
for n in sys.argv[1:]:
    print('==',n)
    for k,m in enumerate(vtable(n)): print('  slot',k,m)

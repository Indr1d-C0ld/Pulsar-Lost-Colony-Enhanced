import struct,sys,mmap,bisect
dll='/home/randolph/Claude/melchior-pulsar/pre_assets/Pulsar.Lost.Colony.v1.2.10/GameAssembly.dll'
f=open(dll,'rb'); d=mmap.mmap(f.fileno(),0,access=mmap.ACCESS_READ)
pe=struct.unpack_from('<I',d,0x3c)[0]; nsec=struct.unpack_from('<H',d,pe+6)[0]; opt=pe+24
base=struct.unpack_from('<Q',d,opt+24)[0]; so=opt+struct.unpack_from('<H',d,pe+20)[0]
secs=[struct.unpack_from('<8sIIII',d,so+40*i) for i in range(nsec)]
rows=[l.rstrip('\n').split('\t') for l in open('/home/randolph/Claude/melchior-pulsar/re/out/method_addrs.tsv')]
addrs=[int(r[0],16) for r in rows]
name={}
for r in rows: name.setdefault(int(r[0],16),r[2]+'::'+r[3])
def owner(va):
    i=bisect.bisect_right(addrs,va)-1
    return '%s+%#x'%(rows[i][2]+'::'+rows[i][3],va-addrs[i]) if i>=0 else '?'
text=[s for s in secs if s[0].startswith(b'il2cpp')][0]
n,vs,va,rs,rp=text
import re
targets=[int(x,16) for x in sys.argv[1:]]
data=d[rp:rp+rs]
for t in targets:
    print('==',hex(t),name.get(t,'?'))
    pos=0
    # scan E8 and E9
    for m in re.finditer(b'[\xe8\xe9]',data):
        o=m.start()
        if o+5>len(data): break
        rel=struct.unpack_from('<i',data,o+1)[0]
        src=base+va+o
        if src+5+rel==t: print('  ',hex(src),'E8' if data[o]==0xe8 else 'E9',owner(src))

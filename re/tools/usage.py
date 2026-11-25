import sys; import os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__))); import lay as L
import struct,sys,mmap,re
dll='/home/randolph/Claude/melchior-pulsar/pre_assets/Pulsar.Lost.Colony.v1.2.10/GameAssembly.dll'
f=open(dll,'rb'); d=mmap.mmap(f.fileno(),0,access=mmap.ACCESS_READ)
pe=struct.unpack_from('<I',d,0x3c)[0]; nsec=struct.unpack_from('<H',d,pe+6)[0]; opt=pe+24
base=struct.unpack_from('<Q',d,opt+24)[0]; so=opt+struct.unpack_from('<H',d,pe+20)[0]
secs=[struct.unpack_from('<8sIIII',d,so+40*i) for i in range(nsec)]
def va2off(v):
    v-=base
    for n,vs,va,rs,rp in secs:
        if va<=v<va+rs: return rp+v-va
lits=open('/home/randolph/Claude/melchior-pulsar/re/out/string_literals.txt',encoding='utf8',newline='').read().split('\n')
T={1:'TypeInfo',2:'Il2CppType',3:'MethodDef',4:'FieldInfo',5:'Str',6:'MethodRef'}
def dec(va):
    o=va2off(va)
    if o is None: return 'bss/none'
    v=struct.unpack_from('<Q',d,o)[0]
    if not v&1: return 'raw %#x'%v
    v&=0xffffffff; t=(v&0xE0000000)>>29; i=(v&0x1FFFFFFE)>>1
    if t in(1,2):
        try: return '%s[%d] %s'%(T[t],i,L.typename(i))
        except Exception as ex: return 'type?'+str(ex)
    if t==6:
        try: return 'MethodRef '+L.mref(i)
        except Exception as ex: return 'mref?'+str(ex)
    if t==3:
        return 'MethodDef '+L.mdef(i)
    if t==5: return 'Str[%d] "%s"'%(i,lits[i][:120])
    return '%s[%d]'%(T.get(t,t),i)
for path in sys.argv[1:]:
    txt=open(path).read()
    print('==',path.split('/')[-1])
    for a in sorted(set(re.findall(r'DAT_(18[0-9a-f]{7})',txt))|set(re.findall(r'0x(18346[0-9a-f]{4})\)',txt))):
        print(' ',a,dec(int(a,16)))

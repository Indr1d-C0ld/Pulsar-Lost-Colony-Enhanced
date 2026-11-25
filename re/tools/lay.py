import struct,mmap,sys,re
G='/home/randolph/Claude/melchior-pulsar/pre_assets/Pulsar.Lost.Colony.v1.2.10/'
md=open(G+'PULSAR_LostColony_Data/il2cpp_data/Metadata/global-metadata.dat','rb').read()
H=struct.unpack_from('<62i',md,8)
names=['stringLiteral','stringLiteralData','string','events','properties','methods','paramDefault','fieldDefault','defaultData','fieldMarshaled','parameters','fields','genericParams','constraints','genericContainers','nested','interfaces','vtable','interfaceOffsets','typeDefs','images']
S={n:(H[2*i],H[2*i+1]) for i,n in enumerate(names)}
so_=S['string'][0]
def s(i):
    e=md.index(b'\0',so_+i); return md[so_+i:e].decode('utf8','replace')
def tab(n,fmt):
    o,sz=S[n]; st=struct.Struct(fmt); return [st.unpack_from(md,o+k) for k in range(0,sz,st.size)]
types=tab('typeDefs','<iiiiiiiIiiiiiiii8HII'); fields=tab('fields','<iiI')
f=open(G+'GameAssembly.dll','rb'); d=mmap.mmap(f.fileno(),0,access=mmap.ACCESS_READ)
pe=struct.unpack_from('<I',d,0x3c)[0]; nsec=struct.unpack_from('<H',d,pe+6)[0]; opt=pe+24
base=struct.unpack_from('<Q',d,opt+24)[0]; so=opt+struct.unpack_from('<H',d,pe+20)[0]
secs=[struct.unpack_from('<8sIIII',d,so+40*i) for i in range(nsec)]
def v2o(v):
    v-=base
    for n,vs,va,rs,rp in secs:
        if va<=v<va+rs: return rp+v-va
Q=lambda va:struct.unpack_from('<Q',d,v2o(va))[0]
REG=0x1831ef430-80
r=struct.unpack_from('<16Q',d,v2o(REG))
typesCount,typesPtr=r[6],r[7]; foPtr=r[11]
def ityp(i):
    p=Q(typesPtr+8*i); o=v2o(p); data,bits=struct.unpack_from('<QI',d,o); return data,bits&0xffff,(bits>>16)&0xff
def tdname(ti):
    t=types[ti]; n=(s(t[1])+'.' if s(t[1]) else '')+s(t[0])
    if t[3]>=0: n=tdname(ityp(t[3])[0])+'/'+n
    return n
def typename(i):
    data,attrs,ty=ityp(i)
    if ty in (0x11,0x12): return tdname(data)
    if ty==0x15:
        o=v2o(data); tdi=struct.unpack_from('<q',d,o)[0]
        if tdi>base: tdi=struct.unpack_from('<Q',d,v2o(tdi))[0]
        # context class_inst
        ci=struct.unpack_from('<Q',d,o+8)[0]
        args=[]
        if ci:
            oc=v2o(ci); argc,argv=struct.unpack_from('<QQ',d,oc)
            for k in range(argc):
                tp=Q(argv+8*k)
                # find index? just describe
                dd,aa,tt=struct.unpack_from('<QI',d,v2o(tp))[0],0,(struct.unpack_from('<QI',d,v2o(tp))[1]>>16)&0xff
                args.append(tdname(dd) if tt in (0x11,0x12) else {8:'int',0xe:'string',2:'bool',0xc:'float',0x15:'GI'}.get(tt,'prim%x'%tt))
        return tdname(tdi)+'<'+','.join(args)+'>'
    prim={1:'void',2:'bool',3:'char',4:'sbyte',5:'byte',6:'short',7:'ushort',8:'int',9:'uint',0xa:'long',0xb:'ulong',0xc:'float',0xd:'double',0xe:'string',0x1c:'object',0x1d:'T[]',0x14:'Array',0x13:'VAR',0x18:'IntPtr'}
    return prim.get(ty,'t%x'%ty)
def layout(ti):
    t=types[ti]; fs,fc=t[8],t[18]
    po=Q(foPtr+8*ti); out=[]
    for k in range(fc):
        fn,fti,tok=fields[fs+k]
        data,attrs,ty=ityp(fti)
        off=struct.unpack_from('<i',d,v2o(po)+4*k)[0] if po else None
        out.append((off,'static ' if attrs&0x10 else '',s(fn),typename(fti) if ty in (0x11,0x12,0x15) else typename(fti)))
    return out
if __name__!='__main__': pass
elif sys.argv[1]=='-t':
    for a in sys.argv[2:]: print(a,typename(int(a)))
else:
  for pat in sys.argv[1:]:
    for ti,t in enumerate(types):
        if re.fullmatch(pat,tdname(ti)):
            print('==',tdname(ti),'ti',ti,'parent',typename(t[4]) if t[4]>=0 else '')
            for off,st,n,tn in layout(ti): print('  %s%s %s %s'%(st,hex(off) if off is not None else '?',tn,n))
methods=tab('methods','<IiiiiIHHHH')
msPtr=r[9]
def mdef(i):
    m=methods[i]; return tdname(m[1])+'::'+s(m[0])
def mref(i):
    o=v2o(msPtr+12*i); mi,ci,mii=struct.unpack_from('<iii',d,o); return mdef(mi)+('<cls%d>'%ci if ci>=0 else '')+('<m%d>'%mii if mii>=0 else '')

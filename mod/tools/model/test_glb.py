#!/usr/bin/env python3
# Crea un GLB di prova per l'importatore: cubo con texture scritta + cubo figlio rosso traslato e ruotato.
import struct, json, io, sys, math
from PIL import Image, ImageDraw, ImageFont
out = sys.argv[1]
img = Image.new('RGB', (256, 256), (20, 40, 80)); d = ImageDraw.Draw(img)
d.rectangle([0, 0, 127, 127], fill=(240, 160, 60)); d.rectangle([128, 128, 255, 255], fill=(90, 200, 210))
try: f = ImageFont.truetype('/usr/share/fonts/truetype/jetbrains-mono/JetBrainsMono-Bold.ttf', 56)
except Exception: f = None
d.text((20, 150), "ABC", fill=(255, 255, 255), font=f); d.text((150, 30), "R", fill=(0, 0, 0), font=f)
png = io.BytesIO(); img.save(png, 'PNG'); png = png.getvalue()
faces = [((0,0,1),(1,0,0),(0,1,0)), ((0,0,-1),(-1,0,0),(0,1,0)), ((1,0,0),(0,0,-1),(0,1,0)), ((-1,0,0),(0,0,1),(0,1,0)), ((0,1,0),(1,0,0),(0,0,-1)), ((0,-1,0),(1,0,0),(0,0,1))]
pos, nor, uv, idx = [], [], [], []
for n, u, v in faces:
    base = len(pos)
    for (a, b, tu, tv) in [(-1,-1,0,1), (1,-1,1,1), (1,1,1,0), (-1,1,0,0)]:
        pos.append([0.5*(n[i] + a*u[i] + b*v[i]) for i in range(3)]); nor.append(list(n)); uv.append([tu, tv])
    idx += [base, base+1, base+2, base, base+2, base+3]
def pack(fmt, rows): return b''.join(struct.pack(fmt, *r) for r in rows)
bp, bn, bu, bi = pack('<3f', pos), pack('<3f', nor), pack('<2f', uv), struct.pack('<%dH' % len(idx), *idx)
def pad(b): return b + b'\0' * ((4 - len(b) % 4) % 4)
blobs = [bp, bn, bu, pad(bi), pad(png)]; views = []; off = 0; binb = b''
for b in blobs: views.append({"buffer": 0, "byteOffset": off, "byteLength": len(b)}); binb += b; off += len(b)
views[3]["byteLength"] = len(bi); views[4]["byteLength"] = len(png)
mn = [min(p[i] for p in pos) for i in range(3)]; mx = [max(p[i] for p in pos) for i in range(3)]
q = [0, math.sin(math.radians(22.5)), 0, math.cos(math.radians(22.5))]
gltf = {"asset": {"version": "2.0", "generator": "melchior-test"}, "scene": 0, "scenes": [{"nodes": [0]}],
  "nodes": [{"name": "cubo", "mesh": 0, "children": [1]}, {"name": "figlio", "mesh": 1, "translation": [1.5, 0, 0], "rotation": q, "scale": [0.4, 0.4, 0.4]}],
  "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1, "TEXCOORD_0": 2}, "indices": 3, "material": 0}]},
             {"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1}, "indices": 3, "material": 1}]}],
  "materials": [{"pbrMetallicRoughness": {"baseColorTexture": {"index": 0}, "metallicFactor": 0.2, "roughnessFactor": 0.6}},
                {"pbrMetallicRoughness": {"baseColorFactor": [1, 0.1, 0.1, 1], "metallicFactor": 0.0}, "emissiveFactor": [0.6, 0, 0]}],
  "textures": [{"source": 0}], "images": [{"bufferView": 4, "mimeType": "image/png"}],
  "accessors": [{"bufferView": 0, "componentType": 5126, "count": len(pos), "type": "VEC3", "min": mn, "max": mx},
                {"bufferView": 1, "componentType": 5126, "count": len(nor), "type": "VEC3"},
                {"bufferView": 2, "componentType": 5126, "count": len(uv), "type": "VEC2"},
                {"bufferView": 3, "componentType": 5123, "count": len(idx), "type": "SCALAR"}],
  "bufferViews": views, "buffers": [{"byteLength": len(binb)}]}
js = json.dumps(gltf, separators=(',', ':')).encode(); js += b' ' * ((4 - len(js) % 4) % 4); binb = pad(binb)
glb = struct.pack('<3I', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(binb)) + struct.pack('<2I', len(js), 0x4E4F534A) + js + struct.pack('<2I', len(binb), 0x004E4942) + binb
open(out, 'wb').write(glb); print("scritto", out, len(glb), "byte")

#!/usr/bin/env python3
# Genera le icone degli achievement «Galassia viva»: gomito LCARS colorato, anello e un simbolo; versione spenta in grigio.
import sys, json
from PIL import Image, ImageDraw, ImageFont, ImageOps
out = sys.argv[1]; S = int(sys.argv[2]) if len(sys.argv) > 2 else 256
font = '/usr/share/fonts/truetype/jetbrains-mono/JetBrainsMono-Bold.ttf'
defs = json.loads(sys.stdin.read())
for d in defs:
    col = tuple(int(d['color'][i:i+2], 16) for i in (1, 3, 5))
    im = Image.new('RGB', (S, S), (8, 10, 16)); dr = ImageDraw.Draw(im)
    for y in range(0, S, S // 16): dr.line([(0, y), (S, y)], fill=(16, 20, 30))
    for x in range(0, S, S // 16): dr.line([(x, 0), (x, S)], fill=(16, 20, 30))
    m = S // 10
    dr.rounded_rectangle([m // 2, m // 2, S - m // 2, S - m // 2], radius=S // 6, outline=col, width=max(2, S // 40))
    dr.pieslice([m, m, m + S // 3, m + S // 3], 180, 270, fill=col); dr.rectangle([m, m + S // 6, m + S // 14, S - m * 2], fill=col); dr.rectangle([m + S // 6, m, S // 2, m + S // 14], fill=col)
    dr.ellipse([S * 0.28, S * 0.28, S * 0.86, S * 0.86], outline=col, width=max(2, S // 50))
    f = ImageFont.truetype(font, int(S * (0.26 if len(d['glyph']) <= 2 else 0.18)))
    bb = dr.textbbox((0, 0), d['glyph'], font=f); w, h = bb[2] - bb[0], bb[3] - bb[1]
    dr.text((S * 0.57 - w / 2 - bb[0], S * 0.57 - h / 2 - bb[1]), d['glyph'], font=f, fill=(240, 236, 226))
    im.save(f"{out}/{d['id']}.jpg", quality=92)
    off = ImageOps.grayscale(im).point(lambda v: int(v * 0.45)).convert('RGB'); off.save(f"{out}/{d['id']}_off.jpg", quality=92)
print(len(defs), "icone")

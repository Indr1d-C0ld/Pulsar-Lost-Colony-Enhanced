#!/usr/bin/env python3
# Cuoce un atlante bitmap (PNG + metriche JSON) da un font TrueType, per le etichette NGUI delle schede di stazione.
# Uso: bake.py <font.ttf> <uscita senza estensione> [dimensione]
import sys, json
from PIL import Image, ImageDraw, ImageFont
from fontTools.ttLib import TTFont

src, out = sys.argv[1], sys.argv[2]; size = int(sys.argv[3]) if len(sys.argv) > 3 else 32
chars = [chr(c) for c in range(32, 127)]
chars += list("«°·»ÀÈÉÌÒÙàèéìòùáíóúâêîôûäëïöüçñÇÑ×–—‘’“”†…→←↑↓▲▼●○■□◆★✓✗±²³½¼¾§µ¿¡€£¥©®™¬¦")
cmap = TTFont(src).getBestCmap()
chars = [c for c in dict.fromkeys(chars) if ord(c) in cmap or c == ' ']
font = ImageFont.truetype(src, size)
ascent, descent = font.getmetrics(); line = ascent + descent
pad = 3; W = 1024
glyphs = []; x = pad; y = pad; rowh = 0
img = Image.new('L', (W, 1024), 0); d = ImageDraw.Draw(img)
for ch in chars:
    l, t, r, b = font.getbbox(ch) if ch != ' ' else (0, 0, 0, 0)
    w, h = max(0, r - l), max(0, b - t)
    adv = round(font.getlength(ch))
    if x + w + pad > W: x = pad; y += rowh + pad; rowh = 0
    if w > 0 and h > 0: d.text((x - l, y - t), ch, font=font, fill=255)
    glyphs.append({"id": ord(ch), "x": x, "y": y, "w": w, "h": h, "ox": l, "oy": t, "adv": adv})
    x += w + pad; rowh = max(rowh, h)
H = 1
while H < y + rowh + pad: H *= 2
img = img.crop((0, 0, W, H))
rgba = Image.new('RGBA', img.size, (255, 255, 255, 0)); rgba.putalpha(img)
rgba.save(out + '.png')
json.dump({"size": size, "base": ascent, "line": line, "w": W, "h": H, "glyphs": glyphs}, open(out + '.json', 'w'), separators=(',', ':'))
print(f"{len(glyphs)} glifi, atlante {W}x{H}, riga {line}, base {ascent}")

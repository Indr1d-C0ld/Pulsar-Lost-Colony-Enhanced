#!/usr/bin/env python3
"""Verifica un blocco tradotto: check.py <NN>. Esce con 0 se out_NN.jsonl è completo e i segnaposto sono intatti."""
import json, re, sys, collections
n = sys.argv[1]
src = [json.loads(l) for l in open(f'chunks/in_{n}.jsonl', encoding='utf-8') if l.strip()]
try: lines = [l for l in open(f'chunks/out_{n}.jsonl', encoding='utf-8') if l.strip()]
except FileNotFoundError: print('manca out_' + n + '.jsonl'); sys.exit(2)
out = {}; bad = 0
for k, l in enumerate(lines, 1):
    try: o = json.loads(l); out[o['id']] = o['it']
    except Exception as e: print(f'riga {k}: JSON non valido ({e})'); bad += 1
TOK = re.compile(r'\{\d+\}|\[[A-Z0-9_\']{2,}\]|</?(?:color|b|i|size)[^>]*>|\[/?b\]|\[-\]|\[[0-9a-fA-F]{6}\]|\[newline\]|\\n|\\t')
for s in src:
    i = s['id']
    if i not in out: print(f'id {i}: manca'); bad += 1; continue
    it = out[i]
    if not isinstance(it, str): print(f'id {i}: "it" non è una stringa'); bad += 1; continue
    a = collections.Counter(TOK.findall(s['en'])); b = collections.Counter(TOK.findall(it))
    if a != b: print(f'id {i}: segnaposto diversi  en={dict(a)}  it={dict(b)}'); bad += 1
    if s['en'].strip() and not it.strip(): print(f'id {i}: traduzione vuota'); bad += 1
    if s['en'][:1] == '$' and it[:1] != '$': print(f'id {i}: manca il $ iniziale'); bad += 1
    if '\n' in it or '\t' in it: print(f'id {i}: a capo o tabulazione reali nel testo'); bad += 1
extra = set(out) - {s['id'] for s in src}
if extra: print('id in più:', sorted(extra)[:10]); bad += 1
print(f'blocco {n}: {len(out)}/{len(src)} stringhe, {bad} problemi'); sys.exit(1 if bad else 0)

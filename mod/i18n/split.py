#!/usr/bin/env python3
"""Divide il file di traduzione di riferimento in blocchi JSONL (id, en, fr) da tradurre."""
import json, re, sys, os
T = '/home/randolph/Claude/melchior-pulsar/pre_assets/Pulsar.Lost.Colony.v1.2.10/translations'
def parse(p):
    out = {}
    for line in open(p, encoding='utf-8-sig').read().split('\n')[1:]:
        m = re.match(r'^(\d+)\t([^\t]*)\t(.*)$', line.rstrip('\r'))
        if m: out[int(m.group(1))] = (m.group(2), m.group(3))
    return out
fr = parse(T + '/Français/translation.tsv')
ids = sorted(fr); N = int(sys.argv[1]); total = sum(len(fr[i][0]) for i in ids); target = total / N
k = 0; size = 0; f = open(f'chunks/in_{k:02d}.jsonl', 'w', encoding='utf-8')
for i in ids:
    en, frt = fr[i]
    if size > target and k < N - 1:
        f.close(); k += 1; size = 0; f = open(f'chunks/in_{k:02d}.jsonl', 'w', encoding='utf-8')
    f.write(json.dumps({'id': i, 'en': en, 'fr': frt}, ensure_ascii=False) + '\n'); size += len(en)
f.close(); print(k + 1, 'blocchi,', len(ids), 'stringhe,', total, 'caratteri')

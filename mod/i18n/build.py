#!/usr/bin/env python3
"""Assembla translations/Italiano a partire dai blocchi tradotti. Le stringhe mancanti restano in inglese."""
import json, glob, re, os, sys
T = '/home/randolph/Claude/melchior-pulsar/pre_assets/Pulsar.Lost.Colony.v1.2.10/translations'
src = {}
for f in sorted(glob.glob('chunks/in_*.jsonl')):
    for l in open(f, encoding='utf-8'):
        if l.strip(): o = json.loads(l); src[o['id']] = o['en']
it = {}
for f in sorted(glob.glob('chunks/out_*.jsonl')):
    for l in open(f, encoding='utf-8'):
        try: o = json.loads(l); it[o['id']] = o['it']
        except Exception: pass
# Correzioni manuali per singola stringa (id -> testo), applicate sopra ai blocchi.
for k, v in json.load(open('correzioni.json', encoding='utf-8')).items(): it[int(k)] = v
# Uniformità dei termini scelti in modo diverso dai vari traduttori.
FIX = [('Ala Sprecata', 'Ala Desolata'), ('Wasted Wing', 'Ala Desolata'), ('Centro di comando', 'Centro di Comando'),
       ('Dadi del bugiardo', 'Dadi del Bugiardo'), ('Unione Coloniale', 'Colonial Union'), ('Federazione Politecnica', 'Polytechnic Federation')]
missing = 0; rows = ['ID\tOriginal Text\tTranslated Text']
for i in sorted(src):
    t = it.get(i)
    if not isinstance(t, str) or (src[i].strip() and not t.strip()): t = src[i]; missing += 1
    for a, b in FIX: t = t.replace(a, b)
    t = t.replace('\t', ' ').replace('\r', '').replace('\n', '\\n')
    rows.append(f'{i}\t{src[i]}\t{t}')
os.makedirs(T + '/Italiano', exist_ok=True)
open(T + '/Italiano/translation.tsv', 'w', encoding='utf-8', newline='').write('\r\n'.join(rows) + '\r\n')
open(T + '/Italiano/version.xml', 'w', encoding='utf-8').write(open(T + '/Français/version.xml', encoding='utf-8').read())
print(f'{len(src)} stringhe, {len(src) - missing} tradotte, {missing} ancora in inglese')

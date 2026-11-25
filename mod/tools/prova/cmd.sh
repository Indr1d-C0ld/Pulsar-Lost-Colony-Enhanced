#!/usr/bin/env bash
# cmd.sh [-i istanza] [-w secondi] "comando" ... : invia comandi alla console di sviluppo e mostra le risposte
G=/home/randolph/Claude/melchior-pulsar/pre_assets/Pulsar.Lost.Colony.v1.2.10
I=""; W=2
while [ "${1:-}" = "-i" ] || [ "${1:-}" = "-w" ]; do if [ "$1" = "-i" ]; then I=$2; else W=$2; fi; shift 2; done
F=$G/BepInEx/config/melchior.cmd${I:+.$I}
LOGS=$(ls $G/BepInEx/LogOutput*.log* 2>/dev/null)
declare -A N; for l in $LOGS; do N[$l]=$(wc -l < "$l"); done
printf '%s\n' "$@" > "$F"
sleep "$W"
for l in $LOGS; do tail -n +$((N[$l]+1)) "$l"; done | grep -a "\[console$I\]" -A40 | grep -a -v '\[net\]\|\[bot\]' | cut -c1-${COLS:-230}

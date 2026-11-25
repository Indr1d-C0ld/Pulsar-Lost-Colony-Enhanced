#!/usr/bin/env bash
# Prova in gioco una famiglia di missioni: avvio, salto, azioni, esito. Uso: famiglie.sh <famiglia>
cd /home/randolph/Claude/melchior-pulsar; S=mod/tools/prova; F=$1
c(){ COLS=400 $S/cmd.sh "$@" | grep -a -A12 'console\]' | cut -c26-330; }
ids(){ COLS=400 $S/cmd.sh -w 1 ships | grep -a "$1" | grep -v '(noi)' | awk '{print $1}'; }
kill_all(){ for id in $(ids "$1"); do for k in 1 2 3 4; do $S/cmd.sh -w 1 "hit $id 4000" >/dev/null; done; done; }
c -w 3 "mis $F" | head -1; c -w 1 tabclose >/dev/null; c -w 2 "mis warp"
for i in $(seq 1 30); do sleep 8; c -w 1 mis | grep -q 'arrivati True' && break; done
c -w 1 mis
for i in $(seq 1 40); do
  $S/cmd.sh -w 1 heal >/dev/null; r=$(c -w 1 mis); echo "$r" | grep -q 'attive: nessuna' && { echo "== $F: missione conclusa"; break; }
  case $F in
    eco) kill_all 'Eco della' ;;
    interdizione) kill_all 'Incursori' ;;
    scorta) for g in Predoni Sciacalli Razziatori Corvi; do kill_all "$g"; done ;;
    recupero) w=$(ids 'E_ABANDONED' | head -1); [ -n "$w" ] && c -w 1 "target $w" "con set scx_scan 100" >/dev/null; for g in Predoni Sciacalli Razziatori Corvi; do kill_all "$g"; done ;;
    contatto) a=$(ids 'Vascello'); [ -n "$a" ] && c -w 1 "target $a" "con set scx_scan 100" "con set cpx_dir 4" >/dev/null ;;
    caccia|soccorso) for id in $(COLS=400 $S/cmd.sh -w 1 ships | grep -a 'squadra 1' | grep -v 'CIVILIAN' | awk '{print $1}'); do for k in 1 2 3 4; do $S/cmd.sh -w 1 "hit $id 4000" >/dev/null; done; done ;;
    ricognizione) c -w 1 "con set scx_ping 20" >/dev/null ;;
    contattoko) a=$(ids 'Vascello'); [ -n "$a" ] && for k in 1 2 3; do $S/cmd.sh -w 1 "hit $a 1500" >/dev/null; done ;;
    rappresaglia) kill_all 'Guerrieri' ;;
    evacuazione) c -w 1 "con set eps_life 2" "con set dmx_life 0.01" >/dev/null ;;
  esac
  sleep 6
done
c -w 1 mis; c -w 1 journal | tail -6

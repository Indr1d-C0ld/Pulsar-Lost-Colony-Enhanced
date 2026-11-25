#!/usr/bin/env bash
# Avvia il gioco in finestra e porta il giocatore a bordo come classe $1 (predefinita 0), senza usare mouse o tastiera.
S="$(cd "$(dirname "$0")" && pwd)"; G=/home/randolph/Claude/melchior-pulsar/pre_assets/Pulsar.Lost.Colony.v1.2.10
export WINEPREFIX=/home/randolph/Claude/melchior-pulsar/wineprefix; wineserver -k 2>/dev/null; sleep 1; rm -f $G/BepInEx/config/melchior.cmd
(MELCHIOR_WINDOW=1280x720 /home/randolph/Claude/melchior-pulsar/mod/play.sh > /dev/null 2>&1 &)
sleep 10; for i in $(seq 1 70); do grep -qa 'risposta op 229' $G/BepInEx/LogOutput.log 2>/dev/null && break; sleep 1; done; sleep 14
$S/cmd.sh intro > /dev/null; $S/cmd.sh "menu PLAY" > /dev/null; $S/cmd.sh -w 3 "menu START_CREW" > /dev/null
$S/cmd.sh -w 20 "press AVVIA" | grep -a 'premuto\|non trovato'
# attende che la nave esista davvero prima di scegliere la classe (il caricamento dura di più con un relay remoto)
for i in $(seq 1 40); do $S/cmd.sh -w 3 ships | grep -q '(noi)' && break; done
sleep 4
$S/cmd.sh -w 2 "class ${1:-0}" | grep -a "classe"; $S/cmd.sh -w 6 "class ready" > /dev/null

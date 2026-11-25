#!/usr/bin/env bash
# Arco "Il silenzio", ramo negativo: ricognizione riuscita, contatto fallito, rappresaglia.
cd /home/randolph/Claude/melchior-pulsar; S=mod/tools/prova; SP=$(dirname "$0")
c(){ COLS=400 $S/cmd.sh "$@" | grep -a -A6 'console\]' | cut -c26-330; }
G=pre_assets/Pulsar.Lost.Colony.v1.2.10/BepInEx; sed -i 's/^DevConsole = false/DevConsole = true/; s/^NetTrace = false/NetTrace = true/' $G/config/melchior.core.cfg
WINEPREFIX=$PWD/wineprefix wineserver -k; $S/newgame.sh 0 | tail -1; sleep 8
c -w 2 saga; c -w 4 "saga start silenzio"; c -w 2 mis
$SP/famiglie.sh ricognizione; c -w 2 saga
c -w 4 "saga due"; c -w 2 mis; $SP/famiglie.sh contattoko; c -w 2 saga
c -w 4 "saga due"; c -w 2 mis; $SP/famiglie.sh rappresaglia; c -w 2 saga
c -w 2 journal

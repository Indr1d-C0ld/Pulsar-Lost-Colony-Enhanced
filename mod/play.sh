#!/usr/bin/env bash
# Avvia il Subspace Relay (se non è già attivo) e poi PULSAR con BepInEx e Melchior Core sotto Wine.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="$ROOT/pre_assets/Pulsar.Lost.Colony.v1.2.10"
export WINEPREFIX="${WINEPREFIX:-$ROOT/wineprefix}" WINEDEBUG="${WINEDEBUG:--all}"
# Il relay locale parte solo se il gioco è configurato per usare questa macchina; altrimenti si usa quello remoto (Balthasar).
HOST="$(sed -n 's/^Host = //p' "$GAME/BepInEx/config/melchior.core.cfg" 2>/dev/null | tr -d '\r' | head -1)"
case "${HOST:-127.0.0.1}" in
  127.0.0.1|localhost)
    if ! (exec 3<>/dev/tcp/127.0.0.1/4530) 2>/dev/null; then
      (cd "$ROOT/server" && nohup node server.js > "$ROOT/server/relay.log" 2>&1 &)
      echo "Relay locale avviato: pannello su http://127.0.0.1:4580/ (log in server/relay.log)"
    fi ;;
  *) echo "Relay remoto: $HOST (pannello su http://$HOST:4580/)" ;;
esac
cd "$GAME"
# winhttp=n,b fa caricare il loader di BepInEx; mscoree e mshtml vuoti evitano le richieste di Mono e Gecko.
# MELCHIOR_WINDOW=1280x720 apre il gioco dentro una finestra di quella misura (desktop virtuale di Wine).
if [ -n "${MELCHIOR_WINDOW:-}" ]; then
  WINEDLLOVERRIDES="winhttp=n,b;mscoree,mshtml=" exec wine explorer "/desktop=PULSAR${MELCHIOR_DESK:-},$MELCHIOR_WINDOW" "$(winepath -w "$GAME/PULSAR_LostColony.exe")" "$@"
fi
WINEDLLOVERRIDES="winhttp=n,b;mscoree,mshtml=" exec wine PULSAR_LostColony.exe "$@"

#!/usr/bin/env bash
# Crea una copia pulita del gioco con il mod Melchior, pronta per un altro PC della LAN.
# Uso: mod/pacchetto.sh <cartella di destinazione> [nome del giocatore] [--zip]
#   MELCHIOR_HOST=192.168.178.27 mod/pacchetto.sh ...   per usare l'indirizzo IP di Balthasar invece del nome
# Nella copia: niente salvataggi, registri né profilo di questo computer; server Balthasar; strumenti di sviluppo spenti.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/pre_assets/Pulsar.Lost.Colony.v1.2.10"
DEST="${1:?indica la cartella di destinazione}"; NAME="${2:-Ufficiale}"; ZIP="${3:-}"
[ "$NAME" = "--zip" ] && { NAME="Ufficiale"; ZIP="--zip"; }
HOST="${MELCHIOR_HOST:-balthasar.local}"; FALLBACK="${MELCHIOR_FALLBACK:-192.168.178.27}"
OUT="$DEST/PULSAR-Melchior"
[ -d "$SRC" ] || { echo "Gioco non trovato in $SRC"; exit 1; }
[ -e "$OUT" ] && { echo "$OUT esiste già: rimuovila o scegli un'altra destinazione"; exit 1; }
mkdir -p "$OUT"

echo "Copia del gioco in $OUT ..."
rsync -a \
  --exclude '/bak/' --exclude '/Saves/***' --exclude '/progress.data' --exclude '/stored.aidata' \
  --exclude '/BepInEx/*.log' --exclude '/BepInEx/LogOutput*' \
  --exclude '/BepInEx/config/melchior.cmd*' --exclude '/BepInEx/config/melchior.service.json*' \
  --exclude '/BepInEx/config/melchior.achievements.progress.json*' --exclude '/BepInEx/config/melchior.rivals.json*' \
  "$SRC/" "$OUT/"
mkdir -p "$OUT/Saves"

# nome del giocatore e della partita
sed -i "s|<PlayerName>.*</PlayerName>|<PlayerName>$NAME</PlayerName>|; s|<LastCreatedGameName>.*</LastCreatedGameName>|<LastCreatedGameName>$NAME's Game</LastCreatedGameName>|" "$OUT/settings.xml"

# configurazione del mod: server Balthasar, niente strumenti di sviluppo
CFG="$OUT/BepInEx/config/melchior.core.cfg"
sed -i "s/^Host = .*/Host = $HOST/; s/^Fallback = .*/Fallback = $FALLBACK/; s/^DevConsole = .*/DevConsole = false/; s/^NetTrace = .*/NetTrace = false/; s/^AutoHostTest = .*/AutoHostTest = false/; s/^RaritySelfTest = .*/RaritySelfTest = false/; s/^RarityShipTest = .*/RarityShipTest = false/; s/^RarityLog = .*/RarityLog = false/" "$CFG"
grep -q "^Fallback = $FALLBACK" "$CFG" || echo "ATTENZIONE: indirizzo di riserva non impostato in $CFG"
grep -q '^UnityLogListening = false' "$OUT/BepInEx/config/BepInEx.cfg" || echo "ATTENZIONE: UnityLogListening non è false in BepInEx.cfg"

# avvio su Linux con Wine, con un prefisso proprio
cat > "$OUT/gioca.sh" <<'SH'
#!/usr/bin/env bash
# Avvia PULSAR con il mod Melchior sotto Wine. Uso: ./gioca.sh   (oppure MELCHIOR_WINDOW=1280x720 ./gioca.sh per giocare in finestra)
set -euo pipefail
GAME="$(cd "$(dirname "$0")" && pwd)"
export WINEPREFIX="${WINEPREFIX:-$HOME/.local/share/pulsar-melchior/wineprefix}" WINEDEBUG="${WINEDEBUG:--all}"
command -v wine >/dev/null || { echo "Serve Wine (versione 8 o superiore)."; exit 1; }
[ -d "$WINEPREFIX" ] || { echo "Preparo il prefisso Wine in $WINEPREFIX (solo la prima volta)..."; mkdir -p "$WINEPREFIX"; WINEDLLOVERRIDES="mscoree,mshtml=" wineboot -i >/dev/null 2>&1 || true; }
cd "$GAME"
# winhttp=n,b fa caricare il loader di BepInEx; mscoree e mshtml vuoti evitano le richieste di Mono e Gecko
if [ -n "${MELCHIOR_WINDOW:-}" ]; then
  WINEDLLOVERRIDES="winhttp=n,b;mscoree,mshtml=" exec wine explorer "/desktop=PULSAR,$MELCHIOR_WINDOW" "$(winepath -w "$GAME/PULSAR_LostColony.exe")" "$@"
fi
WINEDLLOVERRIDES="winhttp=n,b;mscoree,mshtml=" exec wine PULSAR_LostColony.exe "$@"
SH
chmod +x "$OUT/gioca.sh"

cat > "$OUT/LEGGIMI.txt.tmp" <<TXT
PULSAR: Lost Colony con il mod Melchior
========================================

Giocatore: $NAME
Server: $HOST, di riserva $FALLBACK (Subspace Relay su Balthasar, porte 4530 e 4531 TCP)

WINDOWS (10 o 11, 64 bit)
  1. Copia l'intera cartella PULSAR-Melchior dove preferisci, per esempio
     in C:\Giochi. Evita "Programmi": lì il gioco non può scrivere.
  2. Avvia PULSAR_LostColony.exe. Il primo avvio dura un po' di più.
  3. Il mod si carica da solo tramite winhttp.dll (BepInEx): niente da
     installare. Se l'antivirus mette in quarantena winhttp.dll o file
     nella cartella BepInEx, ripristinali e aggiungi un'eccezione per
     questa cartella.
  Per il trasporto vanno bene una chiavetta (lo zip pesa circa 3,3 GB e
  nessun file supera 1 GB) o una cartella condivisa in rete. Su Windows
  si estrae con il tasto destro > Estrai tutto.

LINUX
  Serve Wine 8 o superiore. Avvia ./gioca.sh (la prima volta prepara un
  prefisso Wine in ~/.local/share/pulsar-melchior). Per giocare in una
  finestra: MELCHIOR_WINDOW=1280x720 ./gioca.sh

PER GIOCARE INSIEME
  Uno crea la partita (Gioca > Crea), gli altri la trovano in
  Gioca > Unisciti. Tutti devono avere questa stessa versione del mod.

SE NON SI COLLEGA
  Se il nome $HOST non si risolve il gioco usa da solo l'indirizzo di
  riserva $FALLBACK. Se anche questo non va, controlla che Balthasar sia
  acceso e raggiungibile, poi apri BepInEx/config/melchior.core.cfg e
  correggi le voci Host e Fallback. Il registro del mod, utile per capire
  cosa succede, è in BepInEx/LogOutput.log.

NOME DEL GIOCATORE
  Si cambia in settings.xml (aprilo con il Blocco note), alla voce
  <PlayerName>. Va fatto a gioco chiuso.
TXT
# a capo di Windows, così il Blocco note lo mostra bene ovunque
sed 's/$/\r/' "$OUT/LEGGIMI.txt.tmp" > "$OUT/LEGGIMI.txt" && rm "$OUT/LEGGIMI.txt.tmp"

SIZE=$(du -sh "$OUT" | cut -f1)
echo "Pacchetto pronto: $OUT ($SIZE), giocatore \"$NAME\", server $HOST"
if [ "$ZIP" = "--zip" ]; then
  command -v 7z >/dev/null || { echo "Per lo zip serve 7z."; exit 1; }
  (cd "$DEST" && 7z a -tzip -mx=1 -mmt=on "PULSAR-Melchior-$NAME.zip" PULSAR-Melchior >/dev/null)
  echo "Archivio: $DEST/PULSAR-Melchior-$NAME.zip ($(du -sh "$DEST/PULSAR-Melchior-$NAME.zip" | cut -f1))"
fi

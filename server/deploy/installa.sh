#!/usr/bin/env bash
# Da eseguire SU BALTHASAR, dentro la cartella "server" copiata da Melchior.
# Installa il relay in ~/subspace-relay e lo avvia come servizio utente (nessun sudo).
set -euo pipefail
SRC="$(cd "$(dirname "$0")/.." && pwd)"; DST="$HOME/subspace-relay"
command -v node >/dev/null || { echo "Serve Node.js 18 o superiore."; exit 1; }
mkdir -p "$DST" "$HOME/.config/systemd/user"
cp -r "$SRC/server.js" "$SRC/admin.html" "$SRC/lib" "$SRC/test" "$DST/"
[ -f "$DST/config.json" ] || cp "$SRC/deploy/config.balthasar.json" "$DST/config.json"
# il pannello di amministrazione è protetto da un token generato qui, alla prima installazione
node -e '
const fs = require("fs"), f = process.argv[1], c = JSON.parse(fs.readFileSync(f, "utf8"));
if (!c.adminToken) { c.adminToken = require("crypto").randomBytes(12).toString("hex"); fs.writeFileSync(f, JSON.stringify(c, null, 2) + "\n"); console.log("Token del pannello creato: lo trovi in " + f + " (adminToken)."); }
' "$DST/config.json"
cp "$SRC/deploy/subspace-relay.service" "$HOME/.config/systemd/user/"
systemctl --user daemon-reload
systemctl --user enable --now subspace-relay.service
loginctl enable-linger "$USER" 2>/dev/null || echo "Nota: senza 'linger' il servizio si ferma quando esci dalla sessione."
grep -q '"adminPassHash": "scrypt' "$DST/config.json" || echo "Imposta le credenziali del pannello: echo 'password' | node $DST/server.js $DST/config.json --set-password admin"
echo "Relay attivo. Pannello: http://$(hostname).local:4580/  —  porte di gioco 4530 e 4531 (TCP)."

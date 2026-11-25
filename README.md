# Melchior · PULSAR: Lost Colony

Mod e server per PULSAR: Lost Colony v1.2.10 (Unity 2020.3, IL2CPP), giocato sotto Wine con BepInEx 6. Tutto in italiano, senza Steam, con un server cooperativo in LAN su Balthasar.

## Struttura

| Cartella | Contenuto |
|---|---|
| `mod/Melchior.Core/` | Plugin BepInEx: rarità del bottino, pannelli LCARS delle stazioni, direttore del ritmo, missioni procedurali, archi narrativi, fazioni rivali, obiettivi, stato di servizio, ufficiali automatici. Si compila con `build.sh`. |
| `mod/i18n/` | Traduzione italiana del gioco: blocchi di testo, glossario, correzioni, script di controllo e generazione. |
| `mod/achievements/` | Obiettivi interni con le icone originali. |
| `mod/tools/` | `sig.sh` per leggere le firme interop; `prova/` con gli script di collaudo in gioco (avvio in finestra, console di sviluppo, missioni, archi). |
| `mod/pacchetto.sh` | Copia pulita del gioco con il mod per un altro PC (Windows o Linux): `mod/pacchetto.sh dist "Nome" --zip`. |
| `mod/play.sh` | Avvio del gioco con il mod; `MELCHIOR_WINDOW=1280x720` lo apre in una finestra. |
| `server/` | Subspace Relay: server compatibile con Photon (master e partite), pannello di amministrazione LCARS, classifiche, cronache, fazioni rivali. Collaudo: `node test/smoke.js`. |
| `server/deploy/` | Installazione su Balthasar come servizio utente systemd (`installa.sh`). |
| `re/tools/` | Strumenti di reverse engineering: metadati IL2CPP, indirizzi dei metodi, decompilazione con Ghidra, layout dei campi. |
| `docs/` | Schermate dei pannelli e delle funzioni. |

## Cose da sapere

- Il gioco, il prefisso Wine, il progetto Ghidra e i pacchetti scaricati non sono nel repository (vedi `.gitignore`).
- Nel gioco non vanno registrati tipi gestiti nel dominio IL2CPP (niente MonoBehaviour propri): sotto Wine la memoria si corrompe. Ci si aggancia a metodi del gioco con Harmony.
- Il pannello del relay è su `http://balthasar.local:4580/`; le credenziali si impostano con `echo 'password' | node server.js config.json --set-password admin`.

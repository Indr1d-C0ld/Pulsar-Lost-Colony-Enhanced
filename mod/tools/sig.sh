#!/usr/bin/env bash
# Stampa le firme interop: sig.sh <Assembly> <regex tipo> [regex membro]
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
export GAME="${GAME:-$HERE/../../pre_assets/Pulsar.Lost.Colony.v1.2.10}"
DOTNET="$HOME/.dotnet/dotnet"; FW="$(ls -d "$HOME"/.dotnet/shared/Microsoft.NETCore.App/8.* | tail -1)"
if [ ! -f "$HERE/bin/Sig.dll" ] || [ "$HERE/Sig.cs" -nt "$HERE/bin/Sig.dll" ]; then
  mkdir -p "$HERE/bin"; refs=(); for f in System.Runtime System.Console System.Linq System.Text.RegularExpressions System.Runtime.Loader System.Private.CoreLib System.Collections; do refs+=("-r:$FW/$f.dll"); done
  "$DOTNET" "$(ls "$HOME"/.dotnet/sdk/*/Roslyn/bincore/csc.dll | tail -1)" -noconfig -nostdlib -nologo -target:exe "${refs[@]}" -out:"$HERE/bin/Sig.dll" "$HERE/Sig.cs"
  echo '{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}' > "$HERE/bin/Sig.runtimeconfig.json"
fi
exec "$DOTNET" "$HERE/bin/Sig.dll" "$@"

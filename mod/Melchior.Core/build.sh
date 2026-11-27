#!/usr/bin/env bash
# Compila Melchior.Core.dll con il compilatore dell'SDK, usando come riferimenti il runtime .NET 6
# fornito da BepInEx e le DLL interop generate al primo avvio. Nessun pacchetto NuGet.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
GAME="${GAME:-$HERE/../../pre_assets/Pulsar.Lost.Colony.v1.2.10}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
CSC="$(ls "$(dirname "$DOTNET")"/sdk/*/Roslyn/bincore/csc.dll | tail -1)"
refs=()
managed() { python3 -I - "$1" <<'PY'
import struct, sys
d = open(sys.argv[1], 'rb').read(4096 * 4)
try:
    pe = struct.unpack_from('<I', d, 0x3c)[0]; magic = struct.unpack_from('<H', d, pe + 24)[0]
    dd = pe + 24 + (112 if magic == 0x20b else 96) + 14 * 8
    sys.exit(0 if struct.unpack_from('<I', d, dd)[0] else 1)
except Exception:
    sys.exit(1)
PY
}
for f in "$GAME"/dotnet/*.dll; do managed "$f" && refs+=("-r:$f"); done
for f in BepInEx.Core BepInEx.Unity.IL2CPP MonoMod.RuntimeDetour MonoMod.Utils 0Harmony Il2CppInterop.Runtime Il2CppInterop.Common; do refs+=("-r:$GAME/BepInEx/core/$f.dll"); done
for f in Behave.Unity.Runtime CrewAILibraryBuild PilotAIBuild UnityEngine.PhysicsModule UnityEngine.ParticleSystemModule ACTk.Runtime com.rlabrecque.steamworks.net UnityEngine.IMGUIModule UnityEngine.VideoModule UnityEngine.ScreenCaptureModule UnityEngine.UI UnityEngine.UIModule UnityEngine.ImageConversionModule UnityEngine.InputLegacyModule UnityEngine.TextRenderingModule Assembly-CSharp Assembly-CSharp-firstpass Photon3Unity3D Il2Cppmscorlib Il2CppSystem Il2CppSystem.Core UnityEngine.CoreModule UnityEngine; do refs+=("-r:$GAME/BepInEx/interop/$f.dll"); done
mkdir -p "$HERE/bin"
"$DOTNET" "$CSC" -noconfig -nostdlib -nologo -target:library -langversion:latest -optimize+ -nowarn:CS1701,CS1702,CS8632 \
  "${refs[@]}" -out:"$HERE/bin/Melchior.Core.dll" "$HERE"/*.cs
mkdir -p "$GAME/BepInEx/plugins" && cp "$HERE/bin/Melchior.Core.dll" "$GAME/BepInEx/plugins/"
A="$GAME/BepInEx/plugins/Melchior/achievements"; mkdir -p "$A/icons"
cp "$HERE/../achievements/achievements.json" "$A/" && cp "$HERE"/../achievements/icons/*.jpg "$A/icons/"
F="$GAME/BepInEx/plugins/Melchior/fonts"; mkdir -p "$F" && cp "$HERE"/../fonts/terminale.png "$HERE"/../fonts/terminale.json "$HERE"/../fonts/JetBrainsMono-LICENSE.txt "$F/"
echo "OK -> $GAME/BepInEx/plugins/Melchior.Core.dll"

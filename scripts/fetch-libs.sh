#!/usr/bin/env bash

# copies the assemblies the mod compiles against out of a celeste installation you own.
# they are git-ignored on purpose: celeste's own assemblies are proprietary and the rest
# belong to their projects, so they never travel with this repository.

set -euo pipefail
cd "$(dirname "$0")/.."

REFS=(Celeste.dll Celeste.Mod.mm.dll FNA.dll MonoMod.Utils.dll MonoMod.RuntimeDetour.dll Mono.Cecil.dll)

game="${1:-}"

if [ -z "$game" ]; then
    for candidate in \
        "$HOME/Library/Application Support/Steam/steamapps/common/Celeste" \
        "$HOME/.steam/steam/steamapps/common/Celeste" \
        "$HOME/.local/share/Steam/steamapps/common/Celeste" \
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Celeste"
    do
        if [ -f "$candidate/Celeste.dll" ] || [ -f "$candidate/Celeste.app/Contents/Resources/Celeste.dll" ]; then
            game="$candidate"
            break
        fi
    done
fi

if [ -z "$game" ]; then
    echo 'no celeste install found. pass the path to your game folder, or to the folder'
    echo 'holding the dlls, e.g. scripts/fetch-libs.sh "/path/to/Celeste/Celeste.app/Contents/Resources"'
    exit 1
fi

# mac installs keep the assemblies inside the app bundle rather than next to the executable
if [ ! -f "$game/Celeste.dll" ]; then
    for sub in Contents/Resources Contents/MacOS Resources; do
        if [ -f "$game/$sub/Celeste.dll" ]; then
            game="$game/$sub"
            break
        fi
    done
fi

if [ ! -f "$game/Celeste.dll" ]; then
    echo "no Celeste.dll in $game"
    exit 1
fi

if [ ! -f "$game/Celeste.Mod.mm.dll" ]; then
    echo "no Celeste.Mod.mm.dll in $game"
    echo 'install everest for this copy first: the mod needs the loader assemblies too'
    exit 1
fi

mkdir -p libs
for ref in "${REFS[@]}"; do
    if [ -f "$game/$ref" ]; then
        cp -f "$game/$ref" libs/
        printf '  %-26s -> libs/\n' "$ref"
    else
        printf '  %-26s MISSING\n' "$ref"
    fi
done

echo
echo "libs/ is ready, now run scripts/build.sh"

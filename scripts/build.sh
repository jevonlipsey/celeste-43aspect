#!/usr/bin/env bash

# builds the mod and packages it as the zip that goes into Mods/.

set -euo pipefail
cd "$(dirname "$0")/.."

if [ ! -f libs/Celeste.dll ] && [ -z "${CelestePrefix:-}" ]; then
    echo 'libs/Celeste.dll is missing - run scripts/fetch-libs.sh first (see BUILD.md)'
    exit 1
fi

dotnet build Source/CelesteFit.csproj -c Release

mkdir -p dist
rm -f dist/CelesteFit.zip
zip -q -r dist/CelesteFit.zip everest.yaml bin/CelesteFit.dll

echo
echo "packaged dist/CelesteFit.zip"
unzip -l dist/CelesteFit.zip

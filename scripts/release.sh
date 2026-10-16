#!/usr/bin/env bash

# cuts a CelesteFit release: bumps the version, builds the mod zip, tags, pushes,
# and publishes dist/CelesteFit.zip as the GitHub release asset.
#
#   scripts/release.sh 1.0.1              cut and publish v1.0.1
#   scripts/release.sh 1.0.1 --dry-run    do everything local, publish nothing
#
# the asset name is always CelesteFit.zip, so releases/latest/download/CelesteFit.zip
# in the readme keeps resolving without being edited.

set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${1:-}"
DRY_RUN="${2:-}"

if [ -z "$VERSION" ]; then
    current=$(awk '/^[[:space:]]*Version:/ {print $2; exit}' everest.yaml)
    echo "usage: scripts/release.sh <version> [--dry-run]"
    echo "       current version: ${current:-unknown}"
    exit 2
fi

if printf '%s' "$VERSION" | grep -qE '[^0-9.]' || ! printf '%s' "$VERSION" | grep -q '\.'; then
    echo "version must look like 1.2.3, got '$VERSION'"
    exit 2
fi

TAG="v$VERSION"

### preflight

[ -f libs/Celeste.dll ] || { echo 'libs/ is empty - run scripts/fetch-libs.sh first, see BUILD.md'; exit 1; }
command -v gh >/dev/null 2>&1 || { echo 'gh is not installed'; exit 1; }
gh auth status >/dev/null 2>&1 || { echo 'gh is not logged in - run gh auth login'; exit 1; }

branch=$(git branch --show-current)
[ "$branch" = main ] || { echo "on branch '$branch', releases are cut from main"; exit 1; }

if [ -n "$(git status --porcelain)" ]; then
    echo 'working tree is not clean - commit or stash first:'
    git status --short
    exit 1
fi

if git rev-parse -q --verify "refs/tags/$TAG" >/dev/null; then
    echo "tag $TAG already exists"
    exit 1
fi

if gh release view "$TAG" >/dev/null 2>&1; then
    echo "a release for $TAG already exists"
    exit 1
fi

### bump, build, verify

old=$(awk '/^[[:space:]]*Version:/ {print $2; exit}' everest.yaml)
VERSION="$VERSION" perl -i -pe 'if (!$done && s/^(\s*Version:\s*).*$/$1$ENV{VERSION}/) { $done = 1 }' everest.yaml

scripts/build.sh

echo
echo "--- verifying the packaged zip ---"
unzip -l dist/CelesteFit.zip
if ! unzip -p dist/CelesteFit.zip everest.yaml | grep -q "Version: $VERSION"; then
    echo "the zip's everest.yaml does not carry version $VERSION"
    git checkout -- everest.yaml
    exit 1
fi
if ! unzip -l dist/CelesteFit.zip | grep -q 'bin/CelesteFit.dll'; then
    echo 'the zip has no bin/CelesteFit.dll'
    git checkout -- everest.yaml
    exit 1
fi
echo "ok: everest.yaml is $old -> $VERSION and the dll is packaged"

### publish

if [ "$DRY_RUN" = '--dry-run' ]; then
    git checkout -- everest.yaml
    cat <<EOF

dry run, nothing published. the real thing would run:

  git commit -am 'release: CelesteFit $VERSION'
  git push origin main
  git tag $TAG && git push origin $TAG
  gh release create $TAG dist/CelesteFit.zip \\
      --title 'CelesteFit $VERSION' --generate-notes

dist/CelesteFit.zip is left in place for inspection.
EOF
    exit 0
fi

git commit -q -am "release: CelesteFit $VERSION"
git push origin main
git tag "$TAG"
git push origin "$TAG"
gh release create "$TAG" dist/CelesteFit.zip \
    --title "CelesteFit $VERSION" \
    --generate-notes

echo
gh release view "$TAG" --json url -q .url

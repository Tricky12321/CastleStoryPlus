#!/usr/bin/env bash
# Publishes a GitHub release for the version in CastleStoryPlus/Plugin.cs:
# packs dist/CastleStoryPlus-v<version>.zip, tags v<version> and uploads it with the GitHub CLI.
# Bump Plugin.Version (and CHANGELOG.md) and commit before running. Installed games see the new
# release in the main menu and installers pick it up.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="v$(sed -n 's/.*public const string Version = "\(.*\)";.*/\1/p' "$ROOT/CastleStoryPlus/Plugin.cs")"

if [ -n "$(git -C "$ROOT" status --porcelain)" ]; then
    echo "Commit your changes first." >&2
    exit 1
fi
if git -C "$ROOT" rev-parse "$VERSION" >/dev/null 2>&1; then
    echo "Tag $VERSION already exists; bump Version in CastleStoryPlus/Plugin.cs." >&2
    exit 1
fi
"$ROOT/tools/package.sh"
git -C "$ROOT" tag "$VERSION"
git -C "$ROOT" push origin "$VERSION"
gh release create "$VERSION" "$ROOT/dist/CastleStoryPlus-$VERSION.zip" --repo Tricky12321/CastleStoryPlus --title "Castle Story Plus $VERSION" --generate-notes

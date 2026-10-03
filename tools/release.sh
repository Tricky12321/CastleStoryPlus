#!/usr/bin/env bash
# Publishes a GitHub release for the version in CastleStoryPlus/Plugin.cs:
# packs dist/CastleStoryPlus-v<version>-linux.zip and -windows.zip, tags v<version> and uploads them with the GitHub CLI, together
# with the stand-alone installers and the install instructions in tools/release-notes.md.
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
gh release create "$VERSION" \
    "$ROOT/dist/CastleStoryPlus-$VERSION-linux.zip" "$ROOT/dist/CastleStoryPlus-$VERSION-windows.zip" \
    "$ROOT/installer/install.sh" "$ROOT/installer/install.ps1" "$ROOT/installer/install.bat" \
    --repo Tricky12321/CastleStoryPlus --title "Castle Story Plus $VERSION" \
    --notes-file "$ROOT/tools/release-notes.md" --generate-notes

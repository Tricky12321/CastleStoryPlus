#!/usr/bin/env bash
# Prints the release notes for a version: the install instructions from tools/release-notes.md and the list of changes
# since the release before it, one line each, from tools/release-changes/<version>.md (or, without that file, the
# version's section of CHANGELOG.md).
#   tools/release-notes.sh v0.3.0 [previous tag]
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$1"
PREVIOUS="${2:-}"

sed "s/<version>/$VERSION/g" "$ROOT/tools/release-notes.md"
echo
if [ -n "$PREVIOUS" ]; then
    echo "## Changes since $PREVIOUS"
else
    echo "## Changes"
fi
echo
if [ -f "$ROOT/tools/release-changes/$VERSION.md" ]; then
    cat "$ROOT/tools/release-changes/$VERSION.md"
    exit 0
fi
# The lines under "## <version> — ..." up to the next "## " heading.
awk -v version="${VERSION#v}" '
    /^## / { inside = (index($0, "## " version " ") == 1); next }
    inside { print }
' "$ROOT/CHANGELOG.md" | sed -e '/./,$!d' -e :a -e '/^\n*$/{$d;N;ba' -e '}'

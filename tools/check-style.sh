#!/usr/bin/env bash
# Checks that the C# code follows the code style: the rules in .editorconfig (braces on every block, Allman braces,
# tab indentation, file-scoped namespaces) with dotnet format, and the date and time rules with a search:
# 24-hour time, day- or year-first dates, no culture-dependent date output.
# Run before pushing; the same check runs on every pull request (.github/workflows/code-style.yml).
#   tools/check-style.sh        report what breaks the style
#   tools/check-style.sh --fix  fix what dotnet format can fix (the date and time rules are fixed by hand)
# Needs no game: the game assemblies are only references, and the style rules do not need them.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FAILED=0

if [ "${1:-}" = "--fix" ]; then
    dotnet format "$ROOT/CastleStoryPlus.sln" --severity error
elif ! dotnet format "$ROOT/CastleStoryPlus.sln" --verify-no-changes --severity error; then
    echo "Code style: run tools/check-style.sh --fix" >&2
    FAILED=1
fi

# 12-hour time (hh, h:mm, AM/PM), month-first dates (MM/dd, M/d) and culture-dependent date output.
DATES='ToShortDateString|ToLongDateString|ToShortTimeString|ToLongTimeString|"[^"]*(\bhh[:.]|\bh:mm|\stt\b|MM/dd|\bM/d\b)[^"]*"'
if git -C "$ROOT" ls-files '*.cs' | (cd "$ROOT" && xargs grep -n -E "$DATES"); then
    echo "Date and time: use HH:mm, dd-MM-yyyy (display) or yyyy-MM-dd (data), and always give the format." >&2
    FAILED=1
fi

# Merge conflict markers left behind in any tracked text file (the CHANGELOG is where they usually end up).
if git -C "$ROOT" grep -n -I -E '^(<<<<<<< |>>>>>>> |=======$)' -- ':!*.png' ':!*.wav'; then
    echo "Merge conflict markers: resolve the conflict and remove the markers." >&2
    FAILED=1
fi

exit $FAILED

#!/usr/bin/env bash
# Puts the DevTools plugin (the API on port 27860, in-game tests) into the installed port of the Steam game:
# <Castle Story>/CastleStoryPlus-Unity6/BepInEx/plugins/CastleStoryPlus.DevTools/. Releases do not ship it; the
# installers keep it on an update (they keep every plugin folder the release does not have), so this is needed once,
# and again after an update when the DevTools built for the old version no longer loads.
#
#   tools/install-devtools.sh             build DevTools for the port player and install it
#   tools/install-devtools.sh --no-build  install the DevTools last built
#
# Safe while the game runs: the DLL is replaced by a rename (it is loaded on the next start).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME_DIR="${CSP_GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/Castle Story}"
PLUGINS="$GAME_DIR/CastleStoryPlus-Unity6/BepInEx/plugins"
BUILT="$ROOT/CastleStoryPlus.Player/Build/Linux/BepInEx/plugins/CastleStoryPlus.DevTools/CastleStoryPlus.DevTools.dll"

if [ ! -d "$PLUGINS/CastleStoryPlus" ]; then
	echo "No Castle Story Plus port in $GAME_DIR (set CSP_GAME_DIR)" >&2
	exit 1
fi
if [ "${1:-}" != "--no-build" ]; then
	dotnet build "$ROOT/CastleStoryPlus.DevTools" -c Release -p:UnityTarget=player -v quiet -nologo
fi
if [ ! -f "$BUILT" ]; then
	echo "No DevTools build at $BUILT" >&2
	exit 1
fi
INSTALLED="$(cat "$PLUGINS/CastleStoryPlus/version.txt" 2>/dev/null || echo unknown)"
BUILT_FOR="$(sed -n 's/.*public const string Version = "\(.*\)";.*/v\1/p' "$ROOT/CastleStoryPlus/Plugin.cs")"
if [ "$INSTALLED" != "$BUILT_FOR" ]; then
	echo "warning: the game has Castle Story Plus $INSTALLED, this DevTools is built from $BUILT_FOR" >&2
fi
mkdir -p "$PLUGINS/CastleStoryPlus.DevTools"
cp "$BUILT" "$PLUGINS/CastleStoryPlus.DevTools/CastleStoryPlus.DevTools.dll.new"
mv -f "$PLUGINS/CastleStoryPlus.DevTools/CastleStoryPlus.DevTools.dll.new" "$PLUGINS/CastleStoryPlus.DevTools/CastleStoryPlus.DevTools.dll"
echo "DevTools installed in $PLUGINS/CastleStoryPlus.DevTools (loaded on the next start of the game)"

#!/usr/bin/env bash
# Builds the release of Castle Story Plus (the Unity 6 port), one zip per system:
#   dist/CastleStoryPlus-<version>-linux.zip     install.sh
#   dist/CastleStoryPlus-<version>-windows.zip   install.bat + install.ps1
#
#   CastleStoryPlus-<version>-<os>/
#     installer(s), README.md, LICENSE, NOTICE, licenses/
#     CastleStoryPlus-Unity6/   the port: a fresh player build (PlayerBuild.Release<os>, nothing of the game), its
#                               first-start installer (Installer/: CastleStoryPlus.Port, classdata.tpk, the game file
#                               manifest), BepInEx 5 and the mod (BepInEx/plugins/CastleStoryPlus, with installer/ for
#                               the in-game updater)
#     launcher/CastleStoryPlus.PortLauncher.dll   doorstop entry point for the original game: starts the port
#     bepinex/                  BepInEx 5 (its doorstop) for the original game, used when the game has none
#
# The installers put CastleStoryPlus-Unity6 in the game's folder and point the game's doorstop at the launcher, so
# Steam's Play starts the port. The zips have the names the mod for the original game had, so its in-game updater
# (0.8.0 and before) finds them and moves over to the port.
#
#   tools/package-port.sh [linux|windows]...   (both when none is given)
#
# The mod is built against the game code an install patched into the development player
# (CastleStoryPlus.Player/Build/<Linux|Windows>), so that must have been installed into first. The Unity editor must be
# closed (UNITY: the editor, default ~/Unity/Hub/Editor/6000.6.7f1/Editor/Unity).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="v$(sed -n 's/.*public const string Version = "\(.*\)";.*/\1/p' "$ROOT/CastleStoryPlus/Plugin.cs")"
BEPINEX_VERSION="5.4.23.5"
UNITY="${UNITY:-$HOME/Unity/Hub/Editor/6000.6.7f1/Editor/Unity}"
PROJECT="$ROOT/CastleStoryPlus.Player"
CACHE="$ROOT/dist/cache"
mkdir -p "$CACHE"

# pack <os> <Platform> <bepinex zip name>
pack() {
	local os="$1" platform="$2" bepinex="$3"
	local name="CastleStoryPlus-$VERSION-$os"
	local out="$ROOT/dist/$name"
	local dev="$PROJECT/Build/$platform"
	local build="$PROJECT/Build/Release/$platform"
	if [ ! -f "$dev/CastleStoryPlus_Data/Managed/game-assemblies.txt" ]; then
		echo "error: no install in $dev; the mod is built against the game code an install patched there" >&2
		exit 1
	fi

	# The player, from an empty folder (with LiteNetLib for its network transport).
	"$ROOT/tools/player-litenetlib.sh"
	"$UNITY" -batchmode -quit -projectPath "$PROJECT" -executeMethod "CastleStoryPlus.Player.Editor.PlayerBuild.Release$platform" \
		-logFile "$ROOT/dist/player-$os.log" >/dev/null 2>&1 || true
	if ! grep -q "\[PlayerBuild\] Succeeded" "$ROOT/dist/player-$os.log"; then
		echo "error: the $platform player did not build, see dist/player-$os.log" >&2
		exit 1
	fi
	"$ROOT/tools/player-installer.sh" "$build"

	# BepInEx, from its release.
	if [ ! -f "$CACHE/$bepinex" ]; then
		curl -fsSL -o "$CACHE/$bepinex" "https://github.com/BepInEx/BepInEx/releases/download/v$BEPINEX_VERSION/$bepinex"
	fi
	rm -rf "$CACHE/bepinex-$os"
	python3 -m zipfile -e "$CACHE/$bepinex" "$CACHE/bepinex-$os"
	"$ROOT/tools/player-bepinex.sh" "$build" "$CACHE/bepinex-$os"

	# The mod (not the developer tools).
	local project
	for project in CastleStoryPlus CastleStoryPlus.UI; do
		dotnet build "$ROOT/$project" -c Release -p:UnityTarget=player -p:PlayerPlatform="$platform" -nologo -v quiet
	done
	local bin="$ROOT/CastleStoryPlus/bin/player/Release/net48"
	local plugin="$build/BepInEx/plugins/CastleStoryPlus"
	mkdir -p "$plugin"
	cp "$bin/CastleStoryPlus.dll" "$ROOT/CastleStoryPlus.UI/bin/player/Release/net48/CastleStoryPlus.UI.dll" "$plugin/"
	cp -r "$bin/Lua" "$bin/Assets" "$plugin/"
	echo "$VERSION" > "$plugin/version.txt"
	# The in-game updater runs the installer shipped with the plugin when it cannot download the release's own.
	mkdir -p "$plugin/installer"
	local installers
	if [ "$os" = windows ]; then
		installers=(install.bat install.ps1)
	else
		installers=(install.sh)
	fi
	local file
	for file in "${installers[@]}"; do
		case "$file" in *.bat) ;; *) cp "$ROOT/installer/$file" "$plugin/installer/" ;; esac
	done

	# The launcher the original game's doorstop starts.
	dotnet build "$ROOT/CastleStoryPlus.PortLauncher" -c Release -nologo -v quiet

	rm -rf "$out" "$ROOT/dist/$name.zip"
	mkdir -p "$out/CastleStoryPlus-Unity6" "$out/launcher" "$out/bepinex"
	cp -r "$build/." "$out/CastleStoryPlus-Unity6/"
	cp "$ROOT/CastleStoryPlus.PortLauncher/bin/Release/net35/CastleStoryPlus.PortLauncher.dll" "$out/launcher/"
	python3 -m zipfile -e "$CACHE/$bepinex" "$out/bepinex"
	for file in "${installers[@]}"; do
		cp "$ROOT/installer/$file" "$out/"
	done
	find "$out" -name "*.sh" -exec chmod +x {} +
	cp "$ROOT/installer/README-Port.md" "$out/README.md"
	cp "$ROOT/LICENSE" "$ROOT/NOTICE" "$out/"
	# The licences of the third-party libraries the port ships (NOTICE lists them).
	cp -r "$ROOT/installer/licenses" "$out/licenses"
	# Unity's debugging symbols for the player are not shipped.
	rm -rf "$out"/CastleStoryPlus-Unity6/*_BurstDebugInformation_DoNotShip "$out"/CastleStoryPlus-Unity6/*_BackUpThisFolder_ButDontShipItWithYourGame

	# Nothing of the game, and nothing for development only.
	local bad
	bad="$(find "$out" \( -iname "*DevTools*" -o -iname "*Mcp*" -o -iname "*.Tests*" -o -iname "*.pdb" \
		-o -name "Content" -o -name "Info" -o -name "game-assemblies.txt" -o -name "install.log" \
		-o -name "Assembly-CSharp*.dll" -o -name "Brix.*.dll" -o -name "globalgamemanagers.player" \) -print)"
	if [ -n "$bad" ]; then
		echo "error: files that do not belong in the release:" >&2
		echo "$bad" >&2
		exit 1
	fi

	(cd "$ROOT/dist" && python3 - "$name" <<'PY'
import os, sys, zipfile
name = sys.argv[1]
with zipfile.ZipFile(name + ".zip", "w", zipfile.ZIP_DEFLATED) as z:
    for folder, _, files in os.walk(name):
        for f in sorted(files):
            path = os.path.join(folder, f)
            info = zipfile.ZipInfo.from_file(path, path)
            info.compress_type = zipfile.ZIP_DEFLATED
            with open(path, "rb") as data:
                z.writestr(info, data.read())
PY
	)
	echo "packed dist/$name.zip"
}

targets=("$@")
if [ ${#targets[@]} -eq 0 ]; then
	targets=(linux windows)
fi
for target in "${targets[@]}"; do
	case "$target" in
		linux) pack linux Linux "BepInEx_linux_x64_$BEPINEX_VERSION.zip" ;;
		windows) pack windows Windows "BepInEx_win_x64_$BEPINEX_VERSION.zip" ;;
		*) echo "unknown system: $target" >&2; exit 1 ;;
	esac
done

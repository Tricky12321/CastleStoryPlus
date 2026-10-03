#!/usr/bin/env bash
# Builds Castle Story Plus once and packs one release zip per system:
#   dist/CastleStoryPlus-<version>-linux.zip     install.sh
#   dist/CastleStoryPlus-<version>-windows.zip   install.bat + install.ps1
#
#   CastleStoryPlus-<version>-<os>/
#     installer(s), README.md
#     bepinex/                                BepInEx 5 for that system (linux_x64 / win_x64), used when the game has none
#     files/BepInEx/core/CastleStoryPlus.Bootstrap.dll
#     files/BepInEx/plugins/CastleStoryPlus/   plugin, Lua, Assets, version.txt, installer/ (for the in-game updater)
#
# The build needs the game's DLLs (GameDir in Directory.Build.props), so releases are built locally.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="v$(sed -n 's/.*public const string Version = "\(.*\)";.*/\1/p' "$ROOT/CastleStoryPlus/Plugin.cs")"
BEPINEX_VERSION="5.4.23.5"
BIN="$ROOT/CastleStoryPlus/bin/Release/net35"
CACHE="$ROOT/dist/cache"

dotnet build "$ROOT/CastleStoryPlus.sln" -c Release -nologo -v quiet
mkdir -p "$CACHE"

# pack <os> <bepinex zip name> <installer files...>
pack() {
    local os="$1" bepinex="$2"
    shift 2
    local name="CastleStoryPlus-$VERSION-$os"
    local out="$ROOT/dist/$name"
    local plugin="$out/files/BepInEx/plugins/CastleStoryPlus"
    rm -rf "$out" "$ROOT/dist/$name.zip"
    mkdir -p "$out/files/BepInEx/core" "$plugin/installer" "$out/bepinex"

    cp "$ROOT/CastleStoryPlus.Bootstrap/bin/Release/net35/CastleStoryPlus.Bootstrap.dll" "$out/files/BepInEx/core/"
    cp "$BIN/CastleStoryPlus.dll" "$plugin/"
    cp -r "$BIN/Lua" "$BIN/Assets" "$plugin/"
    echo "$VERSION" > "$plugin/version.txt"
    local file
    for file in "$@"; do
        cp "$ROOT/installer/$file" "$out/"
        # The in-game updater runs the installer shipped with the plugin (install.sh / install.ps1).
        case "$file" in *.bat) ;; *) cp "$ROOT/installer/$file" "$plugin/installer/" ;; esac
    done
    cp "$ROOT/README.md" "$out/"

    if [ ! -f "$CACHE/$bepinex" ]; then
        curl -fsSL -o "$CACHE/$bepinex" "https://github.com/BepInEx/BepInEx/releases/download/v$BEPINEX_VERSION/$bepinex"
    fi
    python3 -m zipfile -e "$CACHE/$bepinex" "$out/bepinex"
    find "$out" -name "*.sh" -exec chmod +x {} +

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

pack linux "BepInEx_linux_x64_$BEPINEX_VERSION.zip" install.sh
pack windows "BepInEx_win_x64_$BEPINEX_VERSION.zip" install.bat install.ps1

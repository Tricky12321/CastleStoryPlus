#!/usr/bin/env bash
# Builds Castle Story Plus and packs a release zip: dist/CastleStoryPlus-<version>.zip
#
#   CastleStoryPlus-<version>/
#     install.sh, install.ps1, install.bat, README.md
#     files/BepInEx/core/CastleStoryPlus.Bootstrap.dll
#     files/BepInEx/plugins/CastleStoryPlus/   plugin, Lua, Assets, version.txt, installer/
#
# The build needs the game's DLLs (GameDir in Directory.Build.props), so releases are built locally.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="v$(sed -n 's/.*public const string Version = "\(.*\)";.*/\1/p' "$ROOT/CastleStoryPlus/Plugin.cs")"
NAME="CastleStoryPlus-$VERSION"
OUT="$ROOT/dist/$NAME"

dotnet build "$ROOT/CastleStoryPlus.sln" -c Release -nologo -v quiet

rm -rf "$OUT" "$ROOT/dist/$NAME.zip"
PLUGIN="$OUT/files/BepInEx/plugins/CastleStoryPlus"
mkdir -p "$OUT/files/BepInEx/core" "$PLUGIN/installer"
cp "$ROOT/CastleStoryPlus.Bootstrap/bin/Release/net35/CastleStoryPlus.Bootstrap.dll" "$OUT/files/BepInEx/core/"
cp "$ROOT/CastleStoryPlus/bin/Release/net35/CastleStoryPlus.dll" "$PLUGIN/"
cp -r "$ROOT/CastleStoryPlus/bin/Release/net35/Lua" "$ROOT/CastleStoryPlus/bin/Release/net35/Assets" "$PLUGIN/"
echo "$VERSION" > "$PLUGIN/version.txt"
# The in-game updater runs the installer shipped with the plugin.
cp "$ROOT/installer/install.sh" "$ROOT/installer/install.ps1" "$PLUGIN/installer/"
cp "$ROOT/installer/install.sh" "$ROOT/installer/install.ps1" "$ROOT/installer/install.bat" "$ROOT/README.md" "$OUT/"
chmod +x "$OUT/install.sh" "$PLUGIN/installer/install.sh"

(cd "$ROOT/dist" && python3 - "$NAME" <<'PY'
import os, sys, zipfile
name = sys.argv[1]
with zipfile.ZipFile(name + ".zip", "w", zipfile.ZIP_DEFLATED) as z:
    for folder, _, files in os.walk(name):
        for f in files:
            path = os.path.join(folder, f)
            info = zipfile.ZipInfo.from_file(path, path)
            info.compress_type = zipfile.ZIP_DEFLATED
            with open(path, "rb") as data:
                z.writestr(info, data.read())
PY
)
echo "packed dist/$NAME.zip"

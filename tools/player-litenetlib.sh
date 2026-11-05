#!/usr/bin/env bash
# Puts LiteNetLib (MIT, https://github.com/RevenantX/LiteNetLib), the UDP library behind the Unity 6 port's network
# transport (Compat's LlapiHost), into the player project: CastleStoryPlus.Player/Assets/Compat/Plugins/LiteNetLib.dll
# from its NuGet package, checked against a pinned SHA-256. Run it once before opening or building the player project;
# tools/package-port.sh runs it too. The DLL is not in git (*.dll is ignored).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="2.1.4"
SHA256="9d2dd27c43fa1855d2ad306c44c2334bd0917ac9e8b8bc2535f0f781f4173513"
CACHE="$ROOT/dist/cache"
PACKAGE="$CACHE/litenetlib.$VERSION.nupkg"
TARGET="$ROOT/CastleStoryPlus.Player/Assets/Compat/Plugins"

mkdir -p "$CACHE" "$TARGET"
if [ ! -f "$PACKAGE" ]; then
	curl -fsSL -o "$PACKAGE.part" "https://api.nuget.org/v3-flatcontainer/litenetlib/$VERSION/litenetlib.$VERSION.nupkg"
	mv "$PACKAGE.part" "$PACKAGE"
fi
if ! echo "$SHA256  $PACKAGE" | sha256sum -c --status; then
	echo "error: $PACKAGE does not have the expected SHA-256; delete it and run again" >&2
	exit 1
fi
python3 - "$PACKAGE" "$TARGET/LiteNetLib.dll" <<'EOF'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    data = z.read("lib/netstandard2.1/LiteNetLib.dll")
try:
    with open(sys.argv[2], "rb") as f:
        if f.read() == data:
            sys.exit(0)
except FileNotFoundError:
    pass
with open(sys.argv[2], "wb") as f:
    f.write(data)
EOF
echo "LiteNetLib $VERSION in $TARGET"

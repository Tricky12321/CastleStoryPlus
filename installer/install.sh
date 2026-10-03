#!/usr/bin/env bash
# Castle Story Plus installer / updater for Linux.
#
# Installs BepInEx 5 (if missing) and the latest Castle Story Plus release from GitHub into the
# Castle Story folder. Run it again to update. The game uses it too ("Update" in the main menu).
#
#   ./install.sh                  install or update to the latest release
#   ./install.sh --tag v0.3.0     install a specific release
#   ./install.sh --local DIR      install from an unpacked release folder (contains files/)
#   ./install.sh --uninstall      remove Castle Story Plus (BepInEx and its config stay)
#
# Options: --game-dir DIR, --force (reinstall even if up to date), --wait-pid PID (wait for the game
# to exit first), --restart (start the game through Steam afterwards).
set -euo pipefail

REPO="Tricky12321/CastleStoryPlus"
BEPINEX_VERSION="5.4.23.5"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_linux_x64_${BEPINEX_VERSION}.zip"
STEAM_APP_ID="227860"
BOOTSTRAP="BepInEx/core/CastleStoryPlus.Bootstrap.dll"
PLUGIN_DIR="BepInEx/plugins/CastleStoryPlus"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME_DIR=""
TAG=""
LOCAL_DIR=""
UNINSTALL=0
FORCE=0
WAIT_PID=""
RESTART=0

log() { echo "[Castle Story Plus] $*"; }
fail() { echo "[Castle Story Plus] ERROR: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --game-dir) GAME_DIR="$2"; shift 2 ;;
        --tag) TAG="$2"; shift 2 ;;
        --local) LOCAL_DIR="$2"; shift 2 ;;
        --uninstall) UNINSTALL=1; shift ;;
        --force) FORCE=1; shift ;;
        --wait-pid) WAIT_PID="$2"; shift 2 ;;
        --restart) RESTART=1; shift ;;
        -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
        *) fail "unknown option: $1" ;;
    esac
done

# An unpacked release has files/ next to the script.
if [ -z "$LOCAL_DIR" ] && [ -d "$SCRIPT_DIR/files/BepInEx" ]; then
    LOCAL_DIR="$SCRIPT_DIR"
fi

find_game_dir() {
    local libraries=()
    local steam
    for steam in "$HOME/.local/share/Steam" "$HOME/.steam/steam" "$HOME/.steam/root" "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"; do
        [ -d "$steam" ] || continue
        libraries+=("$steam")
        if [ -f "$steam/steamapps/libraryfolders.vdf" ]; then
            while IFS= read -r path; do
                libraries+=("$path")
            done < <(sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"/\1/p' "$steam/steamapps/libraryfolders.vdf")
        fi
    done
    local library
    for library in "${libraries[@]}"; do
        if [ -x "$library/steamapps/common/Castle Story/Castle Story" ]; then
            echo "$library/steamapps/common/Castle Story"
            return 0
        fi
    done
    return 1
}

download() {
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL -o "$2" "$1"
    elif command -v wget >/dev/null 2>&1; then
        wget -q -O "$2" "$1"
    else
        fail "curl or wget is needed"
    fi
}

fetch() {
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL -H "Accept: application/vnd.github+json" "$1"
    else
        wget -q -O - --header="Accept: application/vnd.github+json" "$1"
    fi
}

extract() {
    mkdir -p "$2"
    if command -v unzip >/dev/null 2>&1; then
        unzip -qo "$1" -d "$2"
    elif command -v bsdtar >/dev/null 2>&1; then
        bsdtar -xf "$1" -C "$2"
    elif command -v python3 >/dev/null 2>&1; then
        python3 -m zipfile -e "$1" "$2"
    else
        fail "unzip, bsdtar or python3 is needed to unpack zip files"
    fi
}

if [ -n "$WAIT_PID" ]; then
    log "waiting for the game to close..."
    while kill -0 "$WAIT_PID" 2>/dev/null; do
        sleep 1
    done
fi

if [ -z "$GAME_DIR" ]; then
    GAME_DIR="$(find_game_dir)" || fail "Castle Story not found; pass --game-dir \"/path/to/Castle Story\""
fi
[ -x "$GAME_DIR/Castle Story" ] || fail "not a Castle Story folder: $GAME_DIR"
log "game folder: $GAME_DIR"

if [ "$UNINSTALL" = 1 ]; then
    rm -rf "$GAME_DIR/$PLUGIN_DIR" "$GAME_DIR/$BOOTSTRAP"
    if [ -f "$GAME_DIR/run_bepinex.sh" ]; then
        sed -i 's|^target_assembly=.*|target_assembly="BepInEx/core/BepInEx.Preloader.dll"|' "$GAME_DIR/run_bepinex.sh"
    fi
    log "Castle Story Plus removed. BepInEx is still installed; remove the Steam launch option to start the game without it."
    exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# 1. BepInEx
if [ ! -f "$GAME_DIR/BepInEx/core/BepInEx.Preloader.dll" ]; then
    log "installing BepInEx $BEPINEX_VERSION..."
    download "$BEPINEX_URL" "$TMP/bepinex.zip"
    extract "$TMP/bepinex.zip" "$GAME_DIR"
fi
chmod +x "$GAME_DIR/run_bepinex.sh"

# 2. Castle Story Plus
INSTALLED="$(cat "$GAME_DIR/$PLUGIN_DIR/version.txt" 2>/dev/null || true)"
if [ -n "$LOCAL_DIR" ]; then
    SOURCE="$LOCAL_DIR"
    VERSION="$(cat "$LOCAL_DIR/files/$PLUGIN_DIR/version.txt" 2>/dev/null || echo local)"
else
    if [ -n "$TAG" ]; then
        API="https://api.github.com/repos/$REPO/releases/tags/$TAG"
    else
        API="https://api.github.com/repos/$REPO/releases/latest"
    fi
    RELEASE="$(fetch "$API")" || fail "could not read the release from GitHub ($API)"
    VERSION="$(echo "$RELEASE" | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)"
    URL="$(echo "$RELEASE" | grep -o '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*CastleStoryPlus-[^"]*\.zip"' | sed 's/.*"\(https[^"]*\)"/\1/' | head -1)"
    [ -n "$VERSION" ] && [ -n "$URL" ] || fail "release $API has no CastleStoryPlus zip"
    if [ "$FORCE" = 0 ] && [ "$INSTALLED" = "$VERSION" ]; then
        log "Castle Story Plus $VERSION is already installed."
        SOURCE=""
    else
        log "downloading Castle Story Plus $VERSION..."
        download "$URL" "$TMP/mod.zip"
        extract "$TMP/mod.zip" "$TMP/mod"
        SOURCE="$(dirname "$(find "$TMP/mod" -type d -name files -path '*/files' | head -1)")"
        [ -d "$SOURCE/files/BepInEx" ] || fail "unexpected release layout"
    fi
fi

if [ -n "$SOURCE" ]; then
    # Replace the plugin folder (settings live in BepInEx/config and are kept).
    rm -rf "$GAME_DIR/$PLUGIN_DIR"
    cp -r "$SOURCE/files/BepInEx" "$GAME_DIR/"
    echo "$VERSION" > "$GAME_DIR/$PLUGIN_DIR/version.txt"
    log "installed Castle Story Plus $VERSION${INSTALLED:+ (was $INSTALLED)}"
fi

# 3. Start BepInEx through the bootstrap (needed on the game's old Mono runtime).
sed -i "s|^target_assembly=.*|target_assembly=\"$BOOTSTRAP\"|" "$GAME_DIR/run_bepinex.sh"

if ! grep -qs "run_bepinex.sh" "$HOME"/.local/share/Steam/userdata/*/config/localconfig.vdf "$HOME"/.steam/steam/userdata/*/config/localconfig.vdf 2>/dev/null; then
    log "One-time step: in Steam, right-click Castle Story > Properties > Launch Options and enter:"
    log "    ./run_bepinex.sh %command%"
fi

if [ "$RESTART" = 1 ]; then
    log "starting Castle Story..."
    sleep 5
    (setsid steam -applaunch "$STEAM_APP_ID" >/dev/null 2>&1 < /dev/null &) || xdg-open "steam://rungameid/$STEAM_APP_ID" || true
fi
log "done."

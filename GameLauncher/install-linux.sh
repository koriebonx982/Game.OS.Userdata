#!/usr/bin/env bash
# =============================================================
# Game.OS Launcher – Linux Installer
# =============================================================
# Installs the Game.OS Launcher to ~/.local/bin/ and registers
# it in your application menu (compatible with Bazzite, Fedora,
# Ubuntu, Arch, and any XDG-compliant Linux desktop).
#
# How to run:
#   chmod +x install-linux.sh && ./install-linux.sh
#
# To uninstall:
#   ~/.local/bin/GameLauncher  →  rm ~/.local/bin/GameLauncher
#   Application menu entry     →  rm ~/.local/share/applications/gameos-launcher.desktop
# =============================================================

set -euo pipefail

# ── Paths ─────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_BIN="${HOME}/.local/bin"
APPS_DIR="${HOME}/.local/share/applications"
ICONS_DIR="${HOME}/.local/share/icons/hicolor/256x256/apps"

# ── Colour helpers ────────────────────────────────────────────
GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
BOLD='\033[1m'
NC='\033[0m'

_ok()   { echo -e "${GREEN}✅ $*${NC}"; }
_info() { echo -e "${CYAN}ℹ  $*${NC}"; }
_warn() { echo -e "${YELLOW}⚠  $*${NC}"; }

echo -e "${BOLD}Game.OS Launcher – Linux Installer${NC}"
echo ""

# ── Create directories ────────────────────────────────────────
mkdir -p "${INSTALL_BIN}" "${APPS_DIR}" "${ICONS_DIR}"

# ── Check the binary is present ───────────────────────────────
if [ ! -f "${SCRIPT_DIR}/GameLauncher" ]; then
    echo "Error: GameLauncher binary not found in ${SCRIPT_DIR}"
    echo "Extract the full tarball before running this script."
    exit 1
fi

# ── Install binary ────────────────────────────────────────────
cp "${SCRIPT_DIR}/GameLauncher" "${INSTALL_BIN}/GameLauncher"
chmod +x "${INSTALL_BIN}/GameLauncher"
_ok "Binary installed: ${INSTALL_BIN}/GameLauncher"

# ── Copy companion config files ───────────────────────────────
# These must live next to the binary so the launcher finds them on startup.
for CF in gameos-token.dat gameos-backend.url; do
    if [ -f "${SCRIPT_DIR}/${CF}" ]; then
        cp "${SCRIPT_DIR}/${CF}" "${INSTALL_BIN}/${CF}"
    fi
done

# ── Install icon ──────────────────────────────────────────────
ICON_VALUE="gameos-launcher"   # fallback to theme name

if [ -f "${SCRIPT_DIR}/Assets/avalonia-logo.ico" ]; then
    cp "${SCRIPT_DIR}/Assets/avalonia-logo.ico" "${ICONS_DIR}/gameos-launcher.ico"
    ICON_VALUE="${ICONS_DIR}/gameos-launcher.ico"
    _ok "Icon installed: ${ICONS_DIR}/gameos-launcher.ico"
fi

# ── Create .desktop entry ─────────────────────────────────────
DESKTOP_FILE="${APPS_DIR}/gameos-launcher.desktop"

cat > "${DESKTOP_FILE}" << EOF
[Desktop Entry]
Version=1.0
Type=Application
Name=Game.OS Launcher
GenericName=Game Launcher
Comment=Game Hub Launcher – sign in with your Game.OS account
Exec=${INSTALL_BIN}/GameLauncher
Icon=${ICON_VALUE}
Terminal=false
Categories=Game;
StartupNotify=true
Keywords=games;gaming;launcher;gameos;
EOF

chmod 644 "${DESKTOP_FILE}"
_ok "Desktop entry installed: ${DESKTOP_FILE}"

# ── Refresh desktop database ──────────────────────────────────
if command -v update-desktop-database &>/dev/null; then
    update-desktop-database "${APPS_DIR}" 2>/dev/null || true
fi

# ── Ensure ~/.local/bin is on PATH ───────────────────────────
PATH_OK=false
case ":${PATH}:" in
    *":${INSTALL_BIN}:"*) PATH_OK=true ;;
esac

echo ""
_ok "Game.OS Launcher installed!"
echo ""
echo "  Binary  : ${INSTALL_BIN}/GameLauncher"
echo "  Menu    : ${APPS_DIR}/gameos-launcher.desktop"
echo ""
echo "You can now find 'Game.OS Launcher' in your application menu,"
echo "or run it from a terminal:"
echo ""

if [ "${PATH_OK}" = "true" ]; then
    echo "  GameLauncher"
else
    _warn "${INSTALL_BIN} is not in your PATH."
    echo ""
    echo "  Add it by running (or adding to ~/.bashrc / ~/.zshrc):"
    echo "    export PATH=\"\$HOME/.local/bin:\$PATH\""
    echo ""
    echo "  Or run the launcher directly:"
    echo "    ${INSTALL_BIN}/GameLauncher"
fi

echo ""
echo "To uninstall:"
echo "  rm \"${INSTALL_BIN}/GameLauncher\""
echo "  rm \"${INSTALL_BIN}/gameos-token.dat\""
echo "  rm \"${INSTALL_BIN}/gameos-backend.url\""
echo "  rm \"${DESKTOP_FILE}\""

#!/usr/bin/env bash
#
# Hades installer.
#
#   curl -fsSL https://raw.githubusercontent.com/TheArcForge/Hades/main/install.sh | bash
#
# Downloads the release DMG, verifies its checksum, and copies Hades.app to /Applications.
#
# WHY THIS SCRIPT EXISTS, STATED PLAINLY: Hades is not signed with an Apple Developer ID
# certificate yet. macOS blocks unsigned apps on first launch, but only when the file carries the
# `com.apple.quarantine` attribute - which is set by apps that "receive" a file for you (browsers,
# Mail, Slack, AirDrop) and NOT by curl. So a DMG you download in a browser is blocked and needs a
# trip through System Settings; the same DMG fetched by curl is not. This script uses curl, which
# is why it installs cleanly. It is a stopgap until the app is properly signed and notarized, at
# which point the channel stops mattering and this script becomes unnecessary.
#
# Nothing here disables or works around a security check on your machine. It does not touch
# Gatekeeper settings, does not strip attributes, and does not require sudo. If you would rather
# not run a script you have not read, download it first and read it:
#
#   curl -fsSL -O https://raw.githubusercontent.com/TheArcForge/Hades/main/install.sh
#   less install.sh && bash install.sh
#
# Maintainers: VERSION and SHA256 below are the two values to bump per release. SHA256 comes from
# `shasum -a 256 Hades-<VERSION>-unsigned.dmg` against the artifact actually attached to the
# release - never a value copied from anywhere else.

set -euo pipefail

VERSION="2.0.0"
SHA256="1777a93cf2025aad1e498ee8bd632ecb0130af5c6f52f12212941cfaf51bc8eb"

REPO="TheArcForge/Hades"
DMG_NAME="Hades-${VERSION}-unsigned.dmg"
URL="https://github.com/${REPO}/releases/download/v${VERSION}/${DMG_NAME}"
APP_NAME="Hades.app"
INSTALL_DIR="/Applications"
TARGET="${INSTALL_DIR}/${APP_NAME}"

info()  { printf '\033[1m==>\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die()   { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

MOUNTPOINT=""
WORKDIR=""
cleanup() {
    [[ -n "$MOUNTPOINT" && -d "$MOUNTPOINT" ]] && hdiutil detach "$MOUNTPOINT" -quiet 2>/dev/null || true
    [[ -n "$WORKDIR" && -d "$WORKDIR" ]] && rm -rf "$WORKDIR" || true
}
trap cleanup EXIT

# ----------------------------------------------------------------------------- preconditions

[[ "$(uname -s)" == "Darwin" ]] || die "Hades is a macOS app; this is $(uname -s)."

# Running the whole install as root would leave /Applications/Hades.app owned by root, which then
# fails to update itself and is awkward to remove. Nothing here needs elevated privileges.
[[ "$(id -u)" -ne 0 ]] || die "Do not run this with sudo - Hades installs to /Applications as you."

if [[ "$(uname -m)" != "arm64" ]]; then
    die "Hades requires an Apple Silicon Mac.
  The embedded core is built arm64-only, so it cannot run on an Intel Mac.
  This is not a limitation we can configure around - see the README."
fi

MACOS_MAJOR="$(sw_vers -productVersion | cut -d. -f1)"
if [[ "$MACOS_MAJOR" -lt 14 ]]; then
    die "Hades requires macOS 14 (Sonoma) or later; this is macOS $(sw_vers -productVersion)."
fi

# An install that silently replaces a RUNNING app leaves the old process attached to a bundle that
# no longer exists on disk, which fails in confusing ways later. Refuse instead, and say how to fix.
if pgrep -f "${TARGET}/Contents/MacOS/" >/dev/null 2>&1; then
    die "Hades is currently running.
  Quit it from the menu bar (or: osascript -e 'quit app \"Hades\"') and run this again."
fi

# ----------------------------------------------------------------------------- download + verify

WORKDIR="$(mktemp -d)"
DMG="${WORKDIR}/${DMG_NAME}"

info "Downloading Hades ${VERSION}"
echo "    ${URL}"
curl -fL --proto '=https' --tlsv1.2 --progress-bar -o "$DMG" "$URL" \
    || die "Download failed. Check your connection, or that v${VERSION} exists at:
  https://github.com/${REPO}/releases"

info "Verifying checksum"
ACTUAL="$(shasum -a 256 "$DMG" | awk '{print $1}')"
if [[ "$ACTUAL" != "$SHA256" ]]; then
    die "Checksum mismatch - refusing to install.
  expected: ${SHA256}
  actual:   ${ACTUAL}
  The download may be corrupted or truncated. Try again; if it keeps failing,
  please open an issue at https://github.com/${REPO}/issues rather than installing anyway."
fi
echo "    sha256 OK"

# ----------------------------------------------------------------------------- install

info "Mounting the disk image"
MOUNTPOINT="$(hdiutil attach "$DMG" -nobrowse -readonly | grep -o '/Volumes/.*' | head -1)"
[[ -n "$MOUNTPOINT" && -d "$MOUNTPOINT" ]] || die "Could not mount ${DMG_NAME}."
[[ -d "${MOUNTPOINT}/${APP_NAME}" ]] || die "${APP_NAME} is not present in the disk image."

if [[ -e "$TARGET" ]]; then
    info "Replacing the existing ${APP_NAME}"
    rm -rf "$TARGET" || die "Could not remove ${TARGET}. Remove it manually and run this again."
fi

info "Installing to ${INSTALL_DIR}"
# ditto preserves the bundle's structure, symlinks, and extended attributes correctly; cp -R does
# not, and a mangled .app bundle fails to launch in ways that are hard to diagnose.
ditto "${MOUNTPOINT}/${APP_NAME}" "$TARGET" || die "Could not copy ${APP_NAME} to ${INSTALL_DIR}."

hdiutil detach "$MOUNTPOINT" -quiet 2>/dev/null || true
MOUNTPOINT=""

# ----------------------------------------------------------------------------- report honestly

if xattr "$TARGET" 2>/dev/null | grep -q com.apple.quarantine; then
    warn "The installed app carries com.apple.quarantine, so macOS will ask you to approve it
  on first launch. That is unexpected for a curl download - if you hit this, please report it
  at https://github.com/${REPO}/issues with your macOS version.
  To approve: open System Settings > Privacy & Security, find Hades, click Open Anyway."
else
    echo "    no quarantine attribute - the app will launch without a Gatekeeper prompt"
fi

cat <<EOF

$(printf '\033[1;32m✓\033[0m') Hades ${VERSION} installed to ${TARGET}

Next:
  1. Open Hades from /Applications or Spotlight. It lives in the menu bar.
  2. Add your Unity project when it asks.
  3. Connect Claude Code:  claude --plugin-dir <your-Hades-checkout>/Plugin-ClaudeCode~
     Then run /mcp and confirm 'hades' reports 32 tools.

To uninstall:
  rm -rf ${TARGET}
  rm -rf ~/Library/"Application Support"/Hades          # app data (graph, traces, settings)
  rm -f  ~/Library/Preferences/com.arcforge.hades.shell.plist

Your Unity projects' own .arcforge/ directories are never touched by any of the above -
that is your authored memory, and it lives in your repositories, not here.
EOF

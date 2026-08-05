#!/usr/bin/env bash
#
# Assembles a real HadesApp.app bundle - NSStatusItem (and a hidden Dock icon via LSUIElement)
# require one; a bare `swift build`/`xcodebuild` product is just a Mach-O executable with no
# Info.plist and no Contents/ layout.
#
# Why a bundling script instead of a checked-in .xcodeproj: Plan 12 Task 3 offered both options.
# `xcodebuild` builds this package's HadesApp scheme non-interactively with NO .xcodeproj at all -
# Xcode 26.6 auto-generates a scheme per product for a bare SwiftPM manifest (verified empirically:
# `xcodebuild -list` / `xcodebuild build -scheme HadesApp -destination 'platform=macOS'` against
# this exact package, see the Plan 12 Task 3 report). Hand-authoring a correct .xcodeproj (the
# pbxproj format) without Xcode's own GUI or a generator like XcodeGen (not installed in this
# environment - checked) is exactly the kind of error-prone, hard-to-validate-by-inspection task
# this project's "Simplicity First" rule argues against, and it would make HadesApp the one
# Shell~/ package with a fundamentally different build shape than HadesControl/HadesSupervision.
# This script is the "bundle step" the plan's other option names explicitly.
#
# What it does:
#   1. Builds the HadesApp product via `xcodebuild` (non-interactive, no signing identity needed
#      for local use - see the ad-hoc codesign step below).
#   2. Builds HadesCoreReaper (from the sibling HadesSupervision package) via `swift build`.
#   3. Assembles HadesApp.app/Contents/{MacOS,Resources}, with HadesCoreReaper placed alongside
#      the main executable in Contents/MacOS/ - AppDelegate finds it there via
#      `Bundle.main.url(forAuxiliaryExecutable:)`, the standard mechanism for a bundled helper
#      tool (see AppDelegate.makeConfiguration's own doc comment).
#   4. Writes Info.plist with LSUIElement=true (no Dock icon, no Cmd+Tab entry - a menu-bar-only
#      app) and PkgInfo.
#   5. Ad-hoc code-signs the bundle (`codesign --sign -`) - not the notarized, Developer-ID signing
#      Spec #4 (distribution) will eventually do (explicitly out of scope for phase one), but
#      enough for AppKit/NSStatusItem to behave normally on the machine that built it.
#
# Usage:
#   Shell~/HadesApp/scripts/build-app.sh [Debug|Release]
# Output:
#   Shell~/HadesApp/DerivedData/Build/Products/<config>/HadesApp.app
#   (DerivedData/ is already covered by Shell~/.gitignore's existing "Xcode" rule.)
set -euo pipefail

CONFIGURATION="${1:-Debug}"
case "$CONFIGURATION" in
    Debug) SWIFT_CONFIGURATION=debug ;;
    Release) SWIFT_CONFIGURATION=release ;;
    *)
        echo "build-app.sh: unknown configuration '$CONFIGURATION' (expected Debug or Release)" >&2
        exit 64
        ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HADES_APP_DIR="$(dirname "$SCRIPT_DIR")"          # .../Shell~/HadesApp
SHELL_DIR="$(dirname "$HADES_APP_DIR")"           # .../Shell~
HADES_SUPERVISION_DIR="$SHELL_DIR/HadesSupervision"
DERIVED_DATA="$HADES_APP_DIR/DerivedData"
PRODUCTS_DIR="$DERIVED_DATA/Build/Products/$CONFIGURATION"
APP_BUNDLE="$PRODUCTS_DIR/HadesApp.app"

echo "== Building HadesApp ($CONFIGURATION) via xcodebuild =="
xcodebuild build \
    -scheme HadesApp \
    -configuration "$CONFIGURATION" \
    -destination 'platform=macOS' \
    -derivedDataPath "$DERIVED_DATA" \
    CODE_SIGNING_ALLOWED=NO \
    | { grep -Ev '^\s*$' || true; }

HADES_APP_BINARY="$PRODUCTS_DIR/HadesApp"
if [[ ! -x "$HADES_APP_BINARY" ]]; then
    echo "build-app.sh: expected xcodebuild to produce $HADES_APP_BINARY" >&2
    exit 1
fi

echo "== Building HadesCoreReaper ($SWIFT_CONFIGURATION) via swift build =="
swift build --package-path "$HADES_SUPERVISION_DIR" -c "$SWIFT_CONFIGURATION" --product HadesCoreReaper
REAPER_BIN_DIR="$(swift build --package-path "$HADES_SUPERVISION_DIR" -c "$SWIFT_CONFIGURATION" --show-bin-path)"
REAPER_BINARY="$REAPER_BIN_DIR/HadesCoreReaper"
if [[ ! -x "$REAPER_BINARY" ]]; then
    echo "build-app.sh: expected swift build to produce $REAPER_BINARY" >&2
    exit 1
fi

echo "== Assembling $APP_BUNDLE =="
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"

cp "$HADES_APP_BINARY" "$APP_BUNDLE/Contents/MacOS/HadesApp"
# Alongside the main executable, not Resources/: Bundle.main.url(forAuxiliaryExecutable:) - the
# API AppDelegate uses to find this - only searches Contents/MacOS/.
cp "$REAPER_BINARY" "$APP_BUNDLE/Contents/MacOS/HadesCoreReaper"

cat > "$APP_BUNDLE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>HadesApp</string>
    <key>CFBundleIdentifier</key>
    <string>com.arcforge.hades.shell</string>
    <key>CFBundleName</key>
    <string>Hades</string>
    <key>CFBundleDisplayName</key>
    <string>Hades</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>CFBundleShortVersionString</key>
    <string>0.1.0</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <!-- No Dock icon, no Cmd+Tab entry - see this script's own header and
         HadesMenuBarApp.main()'s matching NSApp.setActivationPolicy(.accessory) call for the
         unbundled-dev-run equivalent of this same declaration. -->
    <key>LSUIElement</key>
    <true/>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSHumanReadableCopyright</key>
    <string>Hades</string>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
</dict>
</plist>
PLIST

printf 'APPL????' > "$APP_BUNDLE/Contents/PkgInfo"

echo "== Ad-hoc code-signing (local use only - Spec #4 covers real notarized signing) =="
codesign --force --deep --sign - "$APP_BUNDLE"

echo "== Done =="
echo "$APP_BUNDLE"

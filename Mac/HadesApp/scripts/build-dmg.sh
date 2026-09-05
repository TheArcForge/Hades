#!/usr/bin/env bash
#
# Packages HadesApp.app into a distributable DMG, laid out for drag-to-Applications install
# (Plan 14 Task 7). Calls build-app.sh itself to produce the .app, then stages Hades.app plus an
# Applications symlink - the standard drag-install DMG layout - and compresses it with hdiutil.
#
# Two very different outputs, and the caller must choose explicitly - never by default:
#
#   1. UNSIGNED (Phase 1, today): pass --allow-unsigned. Ad-hoc signed only (same as build-app.sh's
#      own last step), loudly labeled unsigned in the volume name, the filename, and a README
#      inside the DMG. The frictionless install path for this DMG is install.sh at the repo root,
#      which fetches over curl and is therefore not quarantined - see "The channel matters more
#      than the signature" (Documentation/ReleasePipeline.md section 6.2). Homebrew is NOT that
#      path: it quarantines its own downloads (measured 2026-08-18), unsigned or
#      not. A DMG downloaded from a browser IS quarantined, so it still hits that dialog - see
#      Documentation/ReleasePipeline.md for the System Settings steps that get past it until a
#      certificate exists.
#
#   2. SIGNED + NOTARIZED (Phase 2, once a Developer ID Application certificate exists): pass
#      --sign "<identity>" --notarize-profile <notarytool keychain profile>. Signs with hardened
#      runtime and a secure timestamp, builds the DMG, submits it to Apple's notary service, waits,
#      staples the ticket to the DMG, and re-verifies with spctl. Written now, ahead of the
#      certificate existing, so nothing about this path needs rediscovering later - see
#      "Signed release, step by step" in Documentation/ReleasePipeline.md for the one-time
#      account/certificate/credential setup this assumes. NOT exercised on this machine: verified
#      here via `security find-identity -v -p codesigning` -> 0 valid identities.
#
# Neither path is the default. With no signing inputs and no --allow-unsigned, this script refuses
# to produce a DMG at all - see fail_no_signing_inputs() below. A script that silently emits an
# unsigned DMG is how an unsigned build reaches a user (Plan 14 Task 7 Step 2). The failure message
# follows the same standard Core/src/Hades.Server/Mcp/McpBinding.cs holds for a port conflict
# (see its DescribePortInUseFailure): name what failed, say why the requirement is deliberate, give
# the exact remedy.
#
# Usage:
#   build-dmg.sh [Debug|Release] --allow-unsigned
#   build-dmg.sh [Debug|Release] --sign "Developer ID Application: NAME (TEAMID)" \
#       --notarize-profile PROFILE
#   build-dmg.sh --help
#
# Flags also have env var equivalents (useful for CI, where flags land in a shared build log):
#   HADES_DMG_SIGN_IDENTITY, HADES_DMG_NOTARIZE_PROFILE, HADES_DMG_ALLOW_UNSIGNED=1
# An explicit flag wins over its env var equivalent when both are given.
#
# Output:
#   Mac/HadesApp/DerivedData/dmg/Hades-<version>.dmg           (signed path)
#   Mac/HadesApp/DerivedData/dmg/Hades-<version>-unsigned.dmg  (--allow-unsigned path)
#   (DerivedData/ is already covered by Mac/.gitignore's existing "Xcode" rule.)
set -euo pipefail

usage() {
    cat <<'USAGE'
Usage:
  build-dmg.sh [Debug|Release] --allow-unsigned
  build-dmg.sh [Debug|Release] --sign "Developer ID Application: NAME (TEAMID)" --notarize-profile PROFILE
  build-dmg.sh --help

Exactly one of --allow-unsigned or (--sign AND --notarize-profile) must be given - see this
script's own header comment for what each path does. Env var equivalents:
  HADES_DMG_SIGN_IDENTITY, HADES_DMG_NOTARIZE_PROFILE, HADES_DMG_ALLOW_UNSIGNED=1
USAGE
}

fail_no_signing_inputs() {
    cat >&2 <<EOF
build-dmg.sh: no signing identity and no notarization credentials were given, and
--allow-unsigned was not passed either.

This script will not silently produce an unsigned DMG - an unsigned build that reaches a user by
looking like a normal release is exactly what notarization exists to prevent, and reaching a user
is the whole purpose of a DMG. Give it real signing inputs, or say explicitly that you accept an
unsigned build:

  Signed and notarized (needs a Developer ID Application certificate - none exists on this
  machine today; \`security find-identity -v -p codesigning\` reports 0 valid identities. See
  "Signed release, step by step" in Documentation/ReleasePipeline.md for the one-time setup):
    build-dmg.sh Release --sign "Developer ID Application: NAME (TEAMID)" --notarize-profile PROFILE

  Deliberately unsigned (Phase 1, today - Gatekeeper flags this DMG whenever it is quarantined,
  which a browser download always is; install.sh at the repo root fetches over curl and is not
  quarantined, which is the path that avoids it - see Documentation/ReleasePipeline.md):
    build-dmg.sh Release --allow-unsigned

Check for a certificate with: security find-identity -v -p codesigning
EOF
    exit 1
}

fail_incomplete_signing_inputs() {
    cat >&2 <<EOF
build-dmg.sh: only one of --sign / --notarize-profile was given; a real release needs both.

A Developer-ID-signed but unnotarized DMG still fails Gatekeeper's check on current macOS - signing
alone was never sufficient on its own, so this script treats "half the inputs" the same as "none of
them" rather than producing a DMG that looks legitimate but still gets blocked. Supply both:
    build-dmg.sh Release --sign "Developer ID Application: NAME (TEAMID)" --notarize-profile PROFILE
or drop both and pass --allow-unsigned instead if that is what you actually want.
EOF
    exit 1
}

fail_ambiguous_inputs() {
    cat >&2 <<EOF
build-dmg.sh: --allow-unsigned was given together with --sign and/or --notarize-profile.

These are two different, mutually exclusive outputs (see this script's own header comment) - pick
one. Drop --allow-unsigned for a signed, notarized DMG, or drop --sign/--notarize-profile for a
deliberately unsigned one.
EOF
    exit 1
}

CONFIGURATION=""
SIGN_IDENTITY="${HADES_DMG_SIGN_IDENTITY:-}"
NOTARIZE_PROFILE="${HADES_DMG_NOTARIZE_PROFILE:-}"
ALLOW_UNSIGNED="${HADES_DMG_ALLOW_UNSIGNED:-}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        -h|--help)
            usage
            exit 0
            ;;
        Debug|Release)
            CONFIGURATION="$1"
            shift
            ;;
        --sign)
            [[ $# -ge 2 ]] || { echo "build-dmg.sh: --sign requires a value" >&2; exit 64; }
            SIGN_IDENTITY="$2"
            shift 2
            ;;
        --notarize-profile)
            [[ $# -ge 2 ]] || { echo "build-dmg.sh: --notarize-profile requires a value" >&2; exit 64; }
            NOTARIZE_PROFILE="$2"
            shift 2
            ;;
        --allow-unsigned)
            ALLOW_UNSIGNED=1
            shift
            ;;
        *)
            echo "build-dmg.sh: unknown argument '$1' (see --help)" >&2
            exit 64
            ;;
    esac
done
CONFIGURATION="${CONFIGURATION:-Release}"  # a DMG is a distribution artifact; build-app.sh itself
                                            # still defaults to Debug for local dev use.

HAVE_SIGN=0; [[ -n "$SIGN_IDENTITY" ]] && HAVE_SIGN=1
HAVE_NOTARIZE=0; [[ -n "$NOTARIZE_PROFILE" ]] && HAVE_NOTARIZE=1
HAVE_UNSIGNED=0; [[ -n "$ALLOW_UNSIGNED" ]] && HAVE_UNSIGNED=1

if [[ $HAVE_UNSIGNED -eq 1 && ( $HAVE_SIGN -eq 1 || $HAVE_NOTARIZE -eq 1 ) ]]; then
    fail_ambiguous_inputs
elif [[ $HAVE_UNSIGNED -eq 0 && $HAVE_SIGN -eq 0 && $HAVE_NOTARIZE -eq 0 ]]; then
    fail_no_signing_inputs
elif [[ $HAVE_UNSIGNED -eq 0 && $HAVE_SIGN -ne $HAVE_NOTARIZE ]]; then
    fail_incomplete_signing_inputs
fi
SIGNED_PATH=0; [[ $HAVE_SIGN -eq 1 ]] && SIGNED_PATH=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HADES_APP_DIR="$(dirname "$SCRIPT_DIR")"          # .../Mac/HadesApp

# build-app.sh now cd's to Mac/HadesApp itself - its `xcodebuild -scheme` resolves the scheme from
# the CURRENT DIRECTORY, so it used to fail from anywhere else, including the repo root its own
# Usage line tells you to run it from. This subshell predates that fix and compensated for it here
# instead; it is kept because it is still correct and still what keeps build-dmg.sh's own cwd
# untouched for everything that follows, but it is no longer load-bearing.
echo "== Building Hades.app ($CONFIGURATION) via build-app.sh =="
( cd "$HADES_APP_DIR" && "$SCRIPT_DIR/build-app.sh" "$CONFIGURATION" )

PRODUCTS_DIR="$HADES_APP_DIR/DerivedData/Build/Products/$CONFIGURATION"
APP_BUNDLE_SRC="$PRODUCTS_DIR/HadesApp.app"
if [[ ! -d "$APP_BUNDLE_SRC" ]]; then
    echo "build-dmg.sh: expected build-app.sh to produce $APP_BUNDLE_SRC" >&2
    exit 1
fi

VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP_BUNDLE_SRC/Contents/Info.plist")"

DMG_STAGING="$HADES_APP_DIR/DerivedData/dmg-staging"
DMG_OUT_DIR="$HADES_APP_DIR/DerivedData/dmg"
rm -rf "$DMG_STAGING"
mkdir -p "$DMG_STAGING" "$DMG_OUT_DIR"
# Always clean up the intermediate staging dir, including when signing/notarization fails partway
# through (e.g. a bad identity or a rejected submission) - never the final DMG in DMG_OUT_DIR
# itself, which a notarization failure deliberately leaves in place for inspection (see the
# notarytool failure message below).
trap 'rm -rf "$DMG_STAGING"' EXIT

echo "== Staging $DMG_STAGING for drag-to-Applications =="
# Renamed from HadesApp.app to Hades.app: the build product's folder name just follows the SwiftPM
# scheme name (see build-app.sh); Hades.app matches CFBundleName/CFBundleDisplayName and is what a
# user should actually see. Renaming the enclosing folder does not touch code-signature validity -
# codesign's sealed resources are relative to the bundle root, not its containing path.
cp -R "$APP_BUNDLE_SRC" "$DMG_STAGING/Hades.app"
ln -s /Applications "$DMG_STAGING/Applications"

if [[ $SIGNED_PATH -eq 1 ]]; then
    echo "== Signing Hades.app: $SIGN_IDENTITY =="
    codesign --force --deep --options runtime --timestamp --sign "$SIGN_IDENTITY" "$DMG_STAGING/Hades.app"
    codesign --verify --deep --strict --verbose=2 "$DMG_STAGING/Hades.app"

    VOL_NAME="Hades $VERSION (Apple Silicon)"
    DMG_NAME="Hades-$VERSION.dmg"
else
    echo "== UNSIGNED build (--allow-unsigned): Hades.app keeps build-app.sh's own ad-hoc signature =="
    codesign --verify --deep --strict --verbose=2 "$DMG_STAGING/Hades.app" || true  # ad-hoc: informational only

    VOL_NAME="Hades $VERSION (Unsigned, Apple Silicon)"
    DMG_NAME="Hades-$VERSION-unsigned.dmg"
    cat > "$DMG_STAGING/README - Unsigned Build.txt" <<EOF
Hades requires an Apple Silicon Mac and will not function on an Intel Mac. Its embedded .NET core
is a self-contained osx-arm64 publish (see Documentation/ReleasePipeline.md section 6.9); a
universal core is not produced yet. On an Intel Mac, Hades opens just far enough to explain this,
then quits on its own - it does not install itself or leave anything behind.

Hades is not signed with an Apple Developer ID certificate.

macOS Gatekeeper may warn about this the first time you open it - whether it does depends on how
you got this file, not on anything wrong with the download:

  - Installed via the project's install.sh (curl): no warning. curl does not mark downloaded
    files as quarantined, so Gatekeeper's "unidentified developer" check never runs. This is the
    recommended way to install Hades today:

      curl -fsSL https://raw.githubusercontent.com/TheArcForge/Hades/main/install.sh | bash

  - Downloaded through a browser (this DMG): macOS marks the file quarantined, and opening it
    shows "Apple could not verify that this app is free of malware". This is not a corrupted
    download. Go to System Settings > Privacy & Security, scroll down, and click "Open Anyway"
    next to the message naming Hades. (macOS 15 removed the right-click > Open shortcut that used
    to bypass this, so System Settings is the only way now.)

A self-signed certificate would not change either of the above - Gatekeeper only trusts
Apple-issued Developer ID certificates. See Documentation/ReleasePipeline.md for exactly what a
future signed, notarized release needs.
EOF
fi

echo "== Creating $DMG_OUT_DIR/$DMG_NAME =="
rm -f "$DMG_OUT_DIR/$DMG_NAME"
hdiutil create -volname "$VOL_NAME" -srcfolder "$DMG_STAGING" -ov -format UDZO "$DMG_OUT_DIR/$DMG_NAME"

if [[ $SIGNED_PATH -eq 1 ]]; then
    echo "== Submitting for notarization (profile: $NOTARIZE_PROFILE) =="
    if ! xcrun notarytool submit "$DMG_OUT_DIR/$DMG_NAME" --keychain-profile "$NOTARIZE_PROFILE" --wait; then
        cat >&2 <<EOF
build-dmg.sh: notarization failed or was rejected - the DMG above was built but is NOT stapled and
must not be distributed as-is. Inspect the submission (the id is in notarytool's own "id:" field,
printed above):
  xcrun notarytool log <submission-id> --keychain-profile "$NOTARIZE_PROFILE"
EOF
        exit 1
    fi

    echo "== Stapling notarization ticket =="
    xcrun stapler staple "$DMG_OUT_DIR/$DMG_NAME"

    echo "== Gatekeeper assessment =="
    spctl -a -t open --context context:primary-signature -v "$DMG_OUT_DIR/$DMG_NAME"
fi

echo "== Done =="
echo "$DMG_OUT_DIR/$DMG_NAME"

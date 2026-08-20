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
# Mac/ package with a fundamentally different build shape than HadesControl/HadesSupervision.
# This script is the "bundle step" the plan's other option names explicitly.
#
# What it does:
#   1. Builds the HadesApp product via `xcodebuild`, universal (arm64 + x86_64 - release blocker #3,
#      see the `ARCHS` comment below), non-interactive, no signing identity needed for local use
#      (see the ad-hoc codesign step below).
#   2. Builds HadesCoreReaper (from the sibling HadesSupervision package) via `swift build`.
#   3. Release configuration ONLY: publishes Hades.Server self-contained for osx-arm64 (`dotnet
#      publish -r osx-arm64 --self-contained true`) - see this script's own "Publishing" section
#      below for exactly why this is Release-only, why osx-arm64 only, and why untrimmed.
#   4. Assembles HadesApp.app/Contents/{MacOS,Resources}, with HadesCoreReaper placed alongside
#      the main executable in Contents/MacOS/ - AppDelegate finds it there via
#      `Bundle.main.url(forAuxiliaryExecutable:)`, the standard mechanism for a bundled helper
#      tool (see AppDelegate.makeConfiguration's own doc comment) - and, Release only, the
#      published core copied to Contents/Resources/HadesServer/. A Debug build's Contents/Resources
#      has no HadesServer/ at all - AppDelegate's own fallback (see its makeConfiguration doc
#      comment) is what makes that a working dev build rather than a broken one: it falls back to
#      `dotnet run` against source, loudly logged, exactly as this script did unconditionally
#      before Spec #4.
#   5. Writes Info.plist with LSUIElement=true (no Dock icon, no Cmd+Tab entry - a menu-bar-only
#      app) and PkgInfo.
#   6. Ad-hoc code-signs the bundle (`codesign --sign -`) - not the notarized, Developer-ID signing
#      Spec #4 (distribution) will eventually do (explicitly out of scope for phase one), but
#      enough for AppKit/NSStatusItem to behave normally on the machine that built it. `--deep`
#      recursively signs every nested Mach-O it finds - Contents/Resources/HadesServer's 15 of them
#      (the apphost plus 14 native runtime dylibs, out of ~376 files total; the rest are managed
#      .dll/.pdb/.json, not Mach-O, and are simply sealed as ordinary resource data) included -
#      verified empirically with `codesign --verify --deep --strict` against a real Release build
#      (see Documentation/ReleasePipeline.md section 6.9), not assumed from `codesign`'s own man page.
#
# Usage:
#   Mac/HadesApp/scripts/build-app.sh [Debug|Release]
# Output:
#   Mac/HadesApp/DerivedData/Build/Products/<config>/HadesApp.app
#   (DerivedData/ is already covered by Mac/.gitignore's existing "Xcode" rule.)
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
HADES_APP_DIR="$(dirname "$SCRIPT_DIR")"          # .../Mac/HadesApp
SHELL_DIR="$(dirname "$HADES_APP_DIR")"           # .../Mac
HADES_SUPERVISION_DIR="$SHELL_DIR/HadesSupervision"
DERIVED_DATA="$HADES_APP_DIR/DerivedData"
PRODUCTS_DIR="$DERIVED_DATA/Build/Products/$CONFIGURATION"
APP_BUNDLE="$PRODUCTS_DIR/HadesApp.app"

# Universal (arm64 + x86_64), unlike the embedded .NET core below: release blocker #3 (external
# tester report) is "arm64-only, and the current failure mode is the worst available" - a
# drag-installed Hades.app that silently fails to launch on an Intel Mac, because macOS cannot start
# an arm64-only Mach-O there at all. Product's fix is not a universal core (still real work this
# project cannot verify without an Intel Mac or CI runner for it - see the CORE_RID section below,
# unchanged) - it is a universal SHELL that CAN start on Intel, far enough to show a clear "Hades
# requires Apple Silicon" alert and quit cleanly instead of the OS refusing silently. Swift/Xcode
# builds both slices cheaply (unlike the self-contained .NET publish), so this costs real but modest
# DMG size, not a second expensive artifact. `ARCHS` overrides whatever the SwiftPM-auto-generated
# scheme would otherwise resolve to for a bare `-destination 'platform=macOS'` build - verified
# empirically, not assumed: `lipo -archs` on both Debug and Release builds of this exact script
# before this change reported arm64-only.
# `ONLY_ACTIVE_ARCH=NO` is required alongside `ARCHS` - left at its default (YES for Debug), Xcode
# builds only the active/host architecture regardless of what `ARCHS` lists, which would silently
# defeat this entirely for `build-app.sh` with no argument (Debug). See
# Sources/HadesApp/ShellFacts/ArchitectureGate.swift for the early-startup gate this universal shell
# now enables: on a genuine Intel Mac, that gate is the first thing HadesApp's own `main()` does, and
# it always exits before anything below would spawn or attempt to spawn a core.
#
# HadesCoreReaper (built by `swift build` further below) and the embedded .NET core (published just
# below, CORE_RID) are DELIBERATELY UNCHANGED - both stay arm64-only. Neither needs to run on Intel:
# the gate above always exits first, before either is ever touched, so there is nothing for a
# universal HadesCoreReaper or a universal core to do on Intel that this app would ever reach.
echo "== Building HadesApp ($CONFIGURATION) via xcodebuild =="
xcodebuild build \
    -scheme HadesApp \
    -configuration "$CONFIGURATION" \
    -destination 'platform=macOS' \
    -derivedDataPath "$DERIVED_DATA" \
    CODE_SIGNING_ALLOWED=NO \
    ARCHS="x86_64 arm64" \
    ONLY_ACTIVE_ARCH=NO \
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

# Publishing the .NET core self-contained (Release only)
#
# Self-contained, not framework-dependent: the whole point (Spec #4) is that a recipient's Mac has
# neither the .NET SDK nor any .NET runtime installed - framework-dependent still needs a matching
# shared runtime present system-wide, which does not solve the problem at all.
#
# osx-arm64 only, not a universal build: this project builds on Apple Silicon (verified via
# `dotnet --info` -> RID osx-arm64), and a universal binary needs two full RID publishes
# (osx-arm64 + osx-x64) merged with `lipo` per native file - real work this project cannot verify
# on this machine (no Intel Mac, no CI runner for it today) without risking a merge that LOOKS
# right and silently is not. arm64-only for now, loudly labeled (see build-dmg.sh's volume name
# and DerivedData/dmg's own README for the Unsigned path) rather than a guessed-at universal build.
#
# Untrimmed: PublishTrimmed is NOT passed - trimming is opt-in, so this is the default, not a
# choice requiring its own flag. Chosen deliberately anyway: this core loads Roslyn
# (Microsoft.CodeAnalysis.CSharp), Microsoft.Data.Sqlite/SQLitePCLRaw (P/Invoke plus ADO.NET
# provider-factory reflection), and System.Text.Json - all reflection-adjacent - underneath a
# Microsoft.NET.Sdk.Web app, whose ASP.NET Core minimal-API surface is not fully trim-safe today
# either. Trimming risks a build that compiles and launches but silently breaks a specific MCP
# tool or SQLite path only reflection ever reaches - exactly the failure mode too expensive to
# rule out by inspection alone. Not done; see Documentation/ReleasePipeline.md section 6.9 for the
# measured size cost of that choice.
#
# ReadyToRun: also not passed, for the same "no measured benefit" reason - see section 6.9 for the
# measured cold-start time this was checked against.
#
# Debug never publishes: `dotnet publish --self-contained` takes tens of seconds even
# incrementally - paying that on every `build-app.sh Debug` during ordinary Swift-side iteration
# would be a real tax on the dev loop for a step Debug does not need at all (AppDelegate's own
# fallback makes a core-less Debug bundle a fully working dev build - see its makeConfiguration
# doc comment). Output is NOT deleted between runs (unlike $APP_BUNDLE below) so a second `Release`
# build is a fast, incremental `dotnet publish`, not a cold one.
#
# Only `-o` (the final publish output) is redirected into DerivedData/ - NOT
# BaseIntermediateOutputPath/BaseOutputPath. Tried redirecting those too, to keep Core/src
# completely untouched by even build byproducts; reverted after it actually broke the build
# ("circular dependency in target dependency graph involving target ResolveProjectReferences") -
# forcing Hades.Server, Hades.Core, and Hades.Contract's project-to-project references to share one
# literal obj/ path confuses MSBuild's own reference resolution across a multi-project graph, a
# real failure caught by actually running this, not a hypothetical. Each project's own `obj`/`bin`
# (already git-ignored by Core/.gitignore, already written by the existing `dotnet run` dev flow -
# this adds nothing new in kind) is where intermediate output goes instead - standard, supported
# `dotnet publish` usage, and no different in kind from what this exact tree already accumulates
# from ordinary day-to-day `dotnet run`.
CORE_RID="osx-arm64"
CORE_PUBLISH_DIR="$DERIVED_DATA/PublishedCore/$CORE_RID"
CORE_BINARY="$CORE_PUBLISH_DIR/Hades.Server"
if [[ "$CONFIGURATION" == "Release" ]]; then
    REPO_ROOT="$(dirname "$SHELL_DIR")"
    HADES_SERVER_PROJECT="$REPO_ROOT/Core/src/Hades.Server/Hades.Server.csproj"

    echo "== Publishing Hades.Server self-contained ($CORE_RID) via dotnet publish =="
    dotnet publish "$HADES_SERVER_PROJECT" \
        -c Release \
        -r "$CORE_RID" \
        --self-contained true \
        -o "$CORE_PUBLISH_DIR" \
        | { grep -Ev '^\s*$' || true; }

    if [[ ! -x "$CORE_BINARY" ]]; then
        echo "build-app.sh: expected dotnet publish to produce $CORE_BINARY" >&2
        exit 1
    fi
fi

echo "== Assembling $APP_BUNDLE =="
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"

cp "$HADES_APP_BINARY" "$APP_BUNDLE/Contents/MacOS/HadesApp"
# Alongside the main executable, not Resources/: Bundle.main.url(forAuxiliaryExecutable:) - the
# API AppDelegate uses to find this - only searches Contents/MacOS/.
cp "$REAPER_BINARY" "$APP_BUNDLE/Contents/MacOS/HadesCoreReaper"

if [[ "$CONFIGURATION" == "Release" ]]; then
    # Contents/Resources/, not Contents/MacOS/: see AppDelegate.makeConfiguration's own doc
    # comment for why (an entire runtime tree, not one auxiliary executable). Signing this
    # unsigned copy is deferred to the single `codesign --deep` call below, which recursively
    # signs every Mach-O it finds here too - verified, not assumed (see this script's own header).
    echo "== Embedding self-contained core into Contents/Resources/HadesServer =="
    mkdir -p "$APP_BUNDLE/Contents/Resources/HadesServer"
    cp -R "$CORE_PUBLISH_DIR/." "$APP_BUNDLE/Contents/Resources/HadesServer/"
fi

# App icon: generated here from the single 1024x1024 master in Resources/ rather than checking a
# binary .icns into the repo - one source of truth, and replacing the icon is dropping in a new PNG.
# `sips` and `iconutil` both ship with macOS and this build already requires Xcode, so this depends
# on nothing new. The ten entries below are exactly the set a macOS .iconset must contain; Finder,
# Get Info, the DMG window, and Spotlight each pick a different one, so a partial set silently
# degrades to a blurry upscale in whichever surface is missing.
ICON_MASTER="$HADES_APP_DIR/Resources/AppIcon-1024.png"
if [[ -f "$ICON_MASTER" ]]; then
    echo "== Generating AppIcon.icns from $(basename "$ICON_MASTER") =="
    ICONSET_PARENT="$(mktemp -d)"
    ICONSET_DIR="$ICONSET_PARENT/AppIcon.iconset"
    mkdir -p "$ICONSET_DIR"
    for spec in "16:icon_16x16" "32:icon_16x16@2x" "32:icon_32x32" "64:icon_32x32@2x" \
                "128:icon_128x128" "256:icon_128x128@2x" "256:icon_256x256" \
                "512:icon_256x256@2x" "512:icon_512x512" "1024:icon_512x512@2x"; do
        sips -z "${spec%%:*}" "${spec%%:*}" "$ICON_MASTER" \
            --out "$ICONSET_DIR/${spec##*:}.png" >/dev/null
    done
    iconutil --convert icns "$ICONSET_DIR" --output "$APP_BUNDLE/Contents/Resources/AppIcon.icns"
    rm -rf "$ICONSET_PARENT"
else
    # Loud, not silent: a missing master means the shipped app falls back to the generic
    # blank-document icon, which looks like a packaging bug to anyone who installs it.
    echo "build-app.sh: no icon master at $ICON_MASTER - bundling with the generic app icon" >&2
fi

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
    <!-- Names Contents/Resources/AppIcon.icns, generated above from Resources/AppIcon-1024.png.
         CFBundleIconFile (not CFBundleIconName) is the correct key here: IconName resolves through
         an asset catalog, and this bundle is assembled by hand with no catalog to resolve against.
         Menu-bar-only (LSUIElement) means this icon never appears in the Dock - it is what Finder,
         Get Info, Spotlight, and the DMG window show. -->
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>CFBundleShortVersionString</key>
    <string>2.0.0</string>
    <key>CFBundleVersion</key>
    <string>4</string>
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

# Hard requirement, not informational: Contents/Resources/HadesServer (Release builds - see
# above) adds 15 nested Mach-O files (the apphost plus 14 native runtime dylibs) that must EACH
# carry a valid signature for the outer bundle's own signature to be valid at all - `--deep` above
# is what signs them, this is what proves it actually did. Fails the build immediately
# (set -euo pipefail) rather than shipping a bundle whose signature only turns out to be broken
# later, at notarization or launch time.
echo "== Verifying code signature (--deep --strict) =="
codesign --verify --deep --strict --verbose=2 "$APP_BUNDLE"

echo "== Done =="
echo "$APP_BUNDLE"

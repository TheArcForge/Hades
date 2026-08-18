# Homebrew cask for Hades.app (Plan 14 Task 9).
#
# Layout choice: this cask lives at Casks/hades.rb inside the main TheArcForge/Hades repo, rather
# than in a separate homebrew-hades tap repo. Reasoning:
#   - One source of truth, versioned in lockstep with the app it packages (same principle
#     Documentation/ReleasePipeline.md section 2 already applies to package.json / plugin.json /
#     marketplace.json) - no second repo to keep in sync or forget to update on release.
#   - `Casks/<token>.rb` at the tap root is the standard Homebrew tap layout either way, so nothing
#     changes structurally if this ever needs to become its own `homebrew-hades` tap later for the
#     shorter `brew tap TheArcForge/hades` form - that would be a copy, not a redesign.
#   - Publishing a tap anywhere is explicitly out of scope for this task (needs the user's
#     go-ahead) - a same-repo cask that is never tapped publicly is the smaller footprint until
#     that decision is made.
#
# `url` below encodes the intended release-asset shape for the current version - tag `v#{version}`
# (the `v*` pattern `.github/workflows/release.yml` triggers on) and filename
# `Hades-#{version}-unsigned.dmg` (exactly what `Shell~/HadesApp/scripts/build-dmg.sh Release
# --allow-unsigned` names its output - v1 ships unsigned, by product decision: no Apple Developer
# ID / notarization yet - see Documentation/ReleasePipeline.md §6.1/§6.4/§8). It is a template, not
# an invented value: TheArcForge/Hades has no release workflow yet that builds the DMG and uploads
# it as a GitHub Release asset (release.yml today only publishes the Claude Code plugin to the
# separate hades-plugin repo), so nothing exists at this URL until that release is actually created
# and the DMG attached to it.
#
# Two fields stay genuinely unresolved until that publish happens - neither may be invented:
#   1. `url` - correct in shape, but 404s until the v#{version} GitHub Release exists with the DMG
#      attached.
#   2. `sha256` - cannot be computed without the real file, so it stays `:no_check` (below) until
#      then. Once the DMG is built or downloaded, compute it with:
#        shasum -a 256 Hades-#{version}-unsigned.dmg
#      and replace `:no_check` with the resulting hex digest.
# See Documentation/ReleasePipeline.md ("Testing the cask locally, today") for exactly how this was
# verified against a local build instead: a local tap with `url` swapped to a `file://` path and a
# real computed sha256, installed, launched, and removed - never checked in.
cask "hades" do
  version "2.0.0" # Shell~/HadesApp/scripts/build-app.sh's Info.plist
                          # CFBundleShortVersionString - bump both together.

  # No artifact exists at this URL yet (see header comment) - :no_check is correct only until one
  # does. The moment a real release uploads Hades-#{version}-unsigned.dmg here, replace this with
  # the real checksum: `shasum -a 256 Hades-#{version}-unsigned.dmg`. Do not leave :no_check once a
  # real URL exists; it disables the one check that stops a corrupted or tampered download from
  # installing silently.
  sha256 :no_check

  url "https://github.com/TheArcForge/Hades/releases/download/v#{version}/Hades-#{version}-unsigned.dmg"
  name "Hades"
  desc "Menu-bar app for Hades, a Unity project knowledge graph and MCP server for Claude Code"
  homepage "https://github.com/TheArcForge/Hades"

  depends_on macos: ">= :sonoma" # Info.plist LSMinimumSystemVersion 14.0
  depends_on arch: :arm64 # Embedded core is `dotnet publish -r osx-arm64 --self-contained`, so
                           # Hades cannot function on Intel (Documentation/ReleasePipeline.md §6.9).
                           # The shell is built universal only to show an explanatory alert if it is
                           # launched there anyway; refuse cleanly at install time rather than rely
                           # on that last-resort path.

  app "Hades.app"

  # Hades is not signed with an Apple Developer ID certificate - see "The channel matters more
  # than the signature" in docs/superpowers/plans/2026-08-06-distribution-phase-one.md. Declared
  # here so a user learns this at install time rather than discovering it later.
  caveats <<~EOS
    Hades is not signed with an Apple Developer ID certificate (no such certificate exists for
    this project yet). Installing it this way - via Homebrew - does not trigger a Gatekeeper
    prompt: Homebrew does not mark downloaded files as quarantined, and Gatekeeper's
    "unidentified developer" check only runs against quarantined files.

    That is different from downloading the DMG directly through a browser, which IS quarantined
    and does show that prompt - see Documentation/ReleasePipeline.md in the Hades repo for the
    System Settings steps that get past it, and for what a future signed, notarized release needs.
  EOS

  # Only the application-data root Hades.Core.Storage.AppPaths / HadesControl.Discovery both
  # default to, plus the standard Preferences plist for this bundle ID - nothing else.
  # Deliberately NOT a project's own `.arcforge/memory/`: that directory lives inside the user's
  # own Unity project repositories (e.g. ~/Projects/<their-project>/.arcforge/memory/), never
  # under ~/Library, so it is structurally unreachable from this list - it is the user's authored
  # work, not this app's, and zap must never be able to touch it.
  zap trash: [
    "~/Library/Application Support/Hades",
    "~/Library/Preferences/com.arcforge.hades.shell.plist",
  ]
end

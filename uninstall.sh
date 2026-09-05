#!/usr/bin/env bash
#
# Hades uninstaller.
#
#   curl -fsSL https://raw.githubusercontent.com/TheArcForge/Hades/main/uninstall.sh | bash
#
# Removes Hades.app and everything Hades itself created on this Mac. Pass --dry-run to see exactly
# what would be removed without touching anything.
#
# WHAT THIS DELIBERATELY DOES NOT TOUCH, and why:
#
#   - Your projects' own `.arcforge/` directories. That holds the graph cache AND your authored
#     Asphodel memory - decisions, conventions, patterns you wrote. It lives in your repositories,
#     under version control, and is yours. An uninstaller that deletes the user's own writing
#     because it happens to sit next to a cache is not an uninstaller, it is a data-loss bug.
#   - `Assets/Hades` inside a Unity project. Deleting it is a tracked change in YOUR repo; you
#     should make it as a commit, not as a side effect of removing a menu-bar app.
#   - The Claude Code plugin. `/plugin uninstall hades` owns that, and this script cannot reach
#     into a Claude Code session to do it.
#
# It names all three at the end rather than silently leaving them, so "did it really all go?" has
# an answer.
#
# NEVER GLOB ON "arcforge". `~/Library/Preferences/unity.DefaultCompany.ArcForge.plist` is UNITY's
# own preferences file for a company named ArcForge - not ours. Every path below is exact and
# derived from the bundle identifier or a documented Hades location, so a wildcard can never take
# something we do not own.

set -euo pipefail

BUNDLE_ID="com.arcforge.hades.shell"
APP="/Applications/Hades.app"

# Same default HadesControl's Discovery uses, and the same HADES_HOME override the app honours -
# a developer who moved their app data must not be told "removed" about a directory we never saw.
APP_DATA="${HADES_HOME:-$HOME/Library/Application Support/Hades}"

DRY_RUN=0
[[ "${1:-}" == "--dry-run" ]] && DRY_RUN=1

info() { printf '\033[1m==>\033[0m %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

[[ "$(uname -s)" == "Darwin" ]] || die "Hades is a macOS app; this is $(uname -s)."

# Removing a user-owned app and user-owned Library paths needs no elevation. Running this as root
# would also resolve $HOME to root's, so it would delete the wrong things or nothing at all.
[[ "$(id -u)" -ne 0 ]] || die "Do not run this with sudo - everything Hades owns is yours, not root's."

[[ $DRY_RUN -eq 1 ]] && info "DRY RUN - nothing will be removed."

# ------------------------------------------------------------------------------- 1. stop it

if pgrep -f "$APP/Contents/MacOS/" >/dev/null 2>&1; then
    info "Quitting Hades"
    if [[ $DRY_RUN -eq 0 ]]; then
        osascript -e 'quit app "Hades"' 2>/dev/null || true
        for _ in 1 2 3 4 5 6 7 8 9 10; do
            pgrep -f "$APP/Contents/MacOS/" >/dev/null 2>&1 || break
            sleep 1
        done
        if pgrep -f "$APP/Contents/MacOS/" >/dev/null 2>&1; then
            warn "Hades did not quit. Quit it from the menu bar and run this again."
            exit 1
        fi
    fi
    # Reported inside the branch, not after it. A dry run quits nothing (the guard above), so a
    # flat "stopped" here would have it claim an action it did not take - and describing exactly
    # what WOULD happen is the only thing --dry-run is for.
    if [[ $DRY_RUN -eq 1 ]]; then
        note "would quit Hades"
    else
        note "stopped"
    fi
else
    info "Hades is not running"
fi

# ------------------------------------------------------------------------------- 2. login item

# Registered through SMAppService by the app's own launch-at-login toggle. This is the piece a
# manual `rm -rf` cannot clean up: delete the bundle first and macOS is left with a login item
# pointing at nothing, which the user then cannot remove from the app that no longer exists.
# Hence: before the bundle goes, not after.
info "Removing the launch-at-login item"
if osascript -e 'tell application "System Events" to get the name of every login item' 2>/dev/null | grep -q "Hades"; then
    if [[ $DRY_RUN -eq 0 ]]; then
        osascript -e 'tell application "System Events" to delete login item "Hades"' 2>/dev/null || true
        if osascript -e 'tell application "System Events" to get the name of every login item' 2>/dev/null | grep -q "Hades"; then
            warn "Could not remove it automatically. Open System Settings > General > Login Items
  and remove Hades there - macOS does not always let a script unregister an SMAppService item."
        else
            note "removed"
        fi
    else
        note "would remove login item \"Hades\""
    fi
else
    note "not registered"
fi

# ------------------------------------------------------------------------------- 3. the files

# Exact paths only. The first is Hades' own; the rest are the sidecars macOS creates for any app
# bundle, keyed by bundle id - they are small, but leaving them means "uninstalled" is not true.
TARGETS=(
    "$APP"
    "$APP_DATA"
    "$HOME/Library/Preferences/$BUNDLE_ID.plist"
    "$HOME/Library/Caches/$BUNDLE_ID"
    "$HOME/Library/HTTPStorages/$BUNDLE_ID"
    "$HOME/Library/Saved Application State/$BUNDLE_ID.savedState"
)

# The 'hades' symlink install.sh may have made. Handled separately from TARGETS above for two
# reasons, both of which would otherwise be silent bugs:
#
#   1. TARGETS is tested with `-e`, which is FALSE for a dangling symlink - and $APP is removed
#      first, which is exactly what makes it dangle. It would be listed, then never removed.
#   2. /usr/local/bin/hades might not be ours. A real file there belongs to someone else and must
#      not be touched, so this removes the link ONLY if it is a symlink pointing into the bundle
#      we are uninstalling. readlink works on a dangling link, so this is correct either way.
CLI_LINK="/usr/local/bin/hades"
if [[ -L "$CLI_LINK" ]]; then
    CLI_TARGET="$(readlink "$CLI_LINK" 2>/dev/null || true)"
    if [[ "$CLI_TARGET" == "$APP"/* ]]; then
        if [[ $DRY_RUN -eq 1 ]]; then
            note "would remove  $CLI_LINK"
        elif rm -f "$CLI_LINK" 2>/dev/null; then
            note "removed  $CLI_LINK"
        else
            warn "Could not remove $CLI_LINK - remove it yourself with:
  sudo rm '$CLI_LINK'"
        fi
    else
        # Someone else's hades, or a link a user pointed elsewhere on purpose. Say so rather than
        # deleting it or staying silent about why it was left.
        note "left alone  $CLI_LINK (does not point into $APP)"
    fi
elif [[ -e "$CLI_LINK" ]]; then
    note "left alone  $CLI_LINK (a real file, not our symlink)"
fi

# Strip-and-prepend rather than ${var/pattern/string}, because BOTH spellings of that substitution
# are wrong on one bash or the other and this script has to survive `curl … | bash` on whatever
# /bin/bash the Mac ships.
#
# The REPLACEMENT half of ${var/pattern/string} undergoes tilde expansion in bash 4.3+, so a bare
# `~` expands back to $HOME and prints the full path it was written to shorten. Escaping it as `\~`
# fixes that — and breaks bash 3.2.57, which macOS still ships as /bin/bash: there the escape is
# not consumed and the output carries a literal backslash. Measured on this machine, 2026-09-05:
#
#   bash 3.2.57   ${t/#$HOME/\~}  ->  \~/Library/App     ${t/#$HOME/~}  ->  ~/Library/App
#   bash 4.3+     ${t/#$HOME/\~}  ->  ~/Library/App      ${t/#$HOME/~}  ->  /Users/mike/Library/App
#
# The helper below sidesteps the whole question: ${var#prefix} has no replacement half to expand,
# and the tilde is a plain literal in the surrounding word. Verified identical on both bashes.
#
# The `case` guard is not optional. A bare `~${t#$HOME}` prepends the tilde unconditionally, so a
# TARGET that is not under $HOME - /Applications/Hades.app, the very first one - prints as
# "~/Applications/Hades.app", which is a different path that does not exist. ${var/#pattern/...}
# only substitutes on a prefix match; strip-and-prepend has to be told to.
shorten_home() {
    case "$1" in
        "$HOME"/*) printf '~%s' "${1#$HOME}" ;;
        *)         printf '%s' "$1" ;;
    esac
}
info "Removing Hades' own files"
removed=0
for t in "${TARGETS[@]}"; do
    if [[ -e "$t" ]]; then
        if [[ $DRY_RUN -eq 1 ]]; then
            note "would remove  $(shorten_home "$t")"
        else
            rm -rf "$t"
            note "removed  $(shorten_home "$t")"
        fi
        removed=$((removed + 1))
    fi
done
[[ $removed -eq 0 ]] && note "nothing found - Hades was not installed here"

# cfprefsd caches preferences in memory and will happily rewrite the plist we just deleted, so the
# domain has to go through defaults as well, not only through the filesystem.
if [[ $DRY_RUN -eq 0 ]]; then
    defaults delete "$BUNDLE_ID" 2>/dev/null || true
fi

# ------------------------------------------------------------------------------- 4. be honest

if [[ $DRY_RUN -eq 1 ]]; then
    printf '\n\033[1m==>\033[0m Dry run complete - nothing was removed.\n'
else
    printf '\n\033[1;32m✓\033[0m Hades removed.\n'
fi

cat <<EOF

Left in place, deliberately - remove them yourself if you want them gone:

  • Your projects' .arcforge/ directories
      The graph cache and your authored memory (decisions, conventions, patterns) live together
      there, inside your own repositories. That is your writing, so this script will not touch it.

  • Assets/Hades inside any Unity project
      Deleting it is a tracked change in your repo - worth making as a commit, not a side effect.

  • The Claude Code plugin
      In a claude session:  /plugin uninstall hades
EOF

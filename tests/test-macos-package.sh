#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
rid="${1:?Expected osx-arm64 or osx-x64}"
case "$rid" in osx-arm64) arch=arm64 ;; osx-x64) arch=x86_64 ;; *) exit 2 ;; esac
dmg="artifacts/NengliangReader-$rid-unsigned.dmg"
if [[ -n "${MACOS_SIGN_IDENTITY:-}" ]]; then dmg="artifacts/NengliangReader-$rid.dmg"; fi
hdiutil verify "$dmg"
mount_dir="$(mktemp -d "${TMPDIR:-/tmp}/nengliang-check.XXXXXX")"
mounted=false
cleanup() {
  if $mounted; then hdiutil detach "$mount_dir"; fi
  rmdir "$mount_dir"
}
trap cleanup EXIT
hdiutil attach "$dmg" -readonly -nobrowse -mountpoint "$mount_dir"
mounted=true
[[ -d "$mount_dir/良子小窗.app" ]]
[[ -f "$mount_dir/README.md" ]]
[[ "$(readlink "$mount_dir/Applications")" == /Applications ]]
entries=("$mount_dir"/*)
[[ ${#entries[@]} == 3 ]]
executable="$mount_dir/良子小窗.app/Contents/MacOS/NengliangReader"
[[ -x "$executable" ]]
lipo "$executable" -verify_arch "$arch"
plutil -lint "$mount_dir/良子小窗.app/Contents/Info.plist"
grep -q '仍要打开' "$mount_dir/README.md"
echo "DMG verification, layout, Applications link, executable architecture and installation notes passed: $rid"

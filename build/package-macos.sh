#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
rid="${1:-osx-arm64}"
case "$rid" in osx-arm64|osx-x64) ;; *) echo 'Use osx-arm64 or osx-x64' >&2; exit 1;; esac
version="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' Directory.Build.props | tr -d '\r')"
app="artifacts/$rid/良子小窗.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
dotnet publish src/Reader.Desktop/Reader.Desktop.csproj -c Release -r "$rid" --self-contained true -o "$app/Contents/MacOS"
chmod +x "$app/Contents/MacOS/NengliangReader"
cp README.md LICENSE CHANGELOG.md CONTRIBUTING.md "$app/Contents/Resources/"
cp -R docs "$app/Contents/Resources/"
cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>com.hyacinth.nengliangreader</string>
<key>CFBundleName</key><string>良子小窗</string>
<key>NSLocalNetworkUsageDescription</key><string>用于连接或主持您指定的局域网聊天房间。</string>
<key>CFBundleExecutable</key><string>NengliangReader</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>${version%%-*}</string>
<key>CFBundleVersion</key><string>${version%%-*}</string>
<key>NSHighResolutionCapable</key><true/>
<key>LSUIElement</key><true/>
</dict></plist>
EOF
suffix="-unsigned"
if [[ -n "${MACOS_SIGN_IDENTITY:-}" ]]; then
  while IFS= read -r -d '' file; do
    if file -b "$file" | grep -q 'Mach-O'; then
      codesign --force --timestamp --options runtime --entitlements build/entitlements.plist --sign "$MACOS_SIGN_IDENTITY" "$file"
    fi
  done < <(find "$app/Contents/MacOS" -type f -print0)
  codesign --force --timestamp --options runtime --entitlements build/entitlements.plist --sign "$MACOS_SIGN_IDENTITY" "$app/Contents/MacOS/NengliangReader"
  codesign --force --timestamp --options runtime --entitlements build/entitlements.plist --sign "$MACOS_SIGN_IDENTITY" "$app"
  codesign --verify --deep --strict "$app"
  suffix=""
fi
dmg="artifacts/NengliangReader-$rid$suffix.dmg"
# A separate staging directory keeps build outputs out of the DMG root.
dmg_stage="$(mktemp -d "${TMPDIR:-/tmp}/nengliang-dmg.XXXXXX")"
trap 'rm -rf -- "$dmg_stage"' EXIT
ditto "$app" "$dmg_stage/良子小窗.app"
ln -s /Applications "$dmg_stage/Applications"
cp build/README-macos.md "$dmg_stage/README.md"
hdiutil create -volname '良子小窗' -srcfolder "$dmg_stage" -ov -format UDZO "$dmg"
if [[ -n "${MACOS_NOTARY_PROFILE:-}" ]]; then
  [[ -n "${MACOS_SIGN_IDENTITY:-}" ]] || { echo 'Notarization requires MACOS_SIGN_IDENTITY' >&2; exit 1; }
  codesign --timestamp --sign "$MACOS_SIGN_IDENTITY" "$dmg"
  xcrun notarytool submit "$dmg" --keychain-profile "$MACOS_NOTARY_PROFILE" --wait
  xcrun stapler staple "$dmg"
fi

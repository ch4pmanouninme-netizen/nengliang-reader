#!/usr/bin/env bash
# macOS 打包：发布自包含 .app，生成图标和 Info.plist，按需签名/公证，最后封装成 DMG。
# 未设置 MACOS_SIGN_IDENTITY 时产物名带 -unsigned；公证还需要 MACOS_NOTARY_PROFILE。
set -euo pipefail
cd "$(dirname "$0")/.."
# 只接受 osx-arm64 / osx-x64，默认 Apple Silicon。
rid="${1:-osx-arm64}"
case "$rid" in osx-arm64|osx-x64) ;; *) echo 'Use osx-arm64 or osx-x64' >&2; exit 1;; esac
# 版本号从 Directory.Build.props 读取，供文件名和 Info.plist 共用。
version="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' Directory.Build.props | tr -d '\r')"
# 先在 artifacts/<rid> 下组装 .app，再整体封进 DMG。
app="artifacts/$rid/良子小窗.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
dotnet publish src/Reader.Desktop/Reader.Desktop.csproj -c Release -r "$rid" --self-contained true -o "$app/Contents/MacOS"
chmod +x "$app/Contents/MacOS/NengliangReader"
cp README.md LICENSE CHANGELOG.md CONTRIBUTING.md "$app/Contents/Resources/"
cp -R docs "$app/Contents/Resources/"

# 应用图标：从 build/make-icon.py 生成的同一张 PNG 现场做 .icns（iconutil 只在 macOS 上有）。
# 母图是 512，所以 iconset 不生成 512@2x(1024)，macOS 会退用现有的最大尺寸。
icon_source="src/Reader.Desktop/Assets/icon.png"
[[ -f "$icon_source" ]] || { echo "Missing $icon_source" >&2; exit 1; }
iconset="$(mktemp -d "${TMPDIR:-/tmp}/nengliang-icns.XXXXXX")/AppIcon.iconset"
mkdir -p "$iconset"
# iconset 需要 16/32/128/256/512 及对应的 @2x 尺寸。
for size in 16 32 128 256 512; do
  sips -z $size $size "$icon_source" --out "$iconset/icon_${size}x${size}.png" >/dev/null
done
for size in 16 32 128 256; do
  sips -z $((size * 2)) $((size * 2)) "$icon_source" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"
rm -rf -- "$(dirname "$iconset")"

# LSUIElement 让应用不出现在 Dock；版本号去掉预发布后缀。
cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>com.hyacinth.nengliangreader</string>
<key>CFBundleName</key><string>良子小窗</string>
<key>CFBundleIconFile</key><string>AppIcon</string>
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
  # 先签 Mach-O 二进制，再签 .app 外层，最后整体校验。
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
dmg="artifacts/NengliangReader-v$version-$rid$suffix.dmg"
# A separate staging directory keeps build outputs out of the DMG root.
# 暂存目录只放 .app、Applications 链接和 README，避免构建产物进 DMG 根目录。
dmg_stage="$(mktemp -d "${TMPDIR:-/tmp}/nengliang-dmg.XXXXXX")"
trap 'rm -rf -- "$dmg_stage"' EXIT
ditto "$app" "$dmg_stage/良子小窗.app"
ln -s /Applications "$dmg_stage/Applications"
cp build/README-macos.md "$dmg_stage/README.md"
hdiutil create -volname '良子小窗' -srcfolder "$dmg_stage" -ov -format UDZO "$dmg"
if [[ -n "${MACOS_NOTARY_PROFILE:-}" ]]; then
  # 公证前必须已签名；提交后装订票据，离线也能通过 Gatekeeper 检查。
  [[ -n "${MACOS_SIGN_IDENTITY:-}" ]] || { echo 'Notarization requires MACOS_SIGN_IDENTITY' >&2; exit 1; }
  codesign --timestamp --sign "$MACOS_SIGN_IDENTITY" "$dmg"
  xcrun notarytool submit "$dmg" --keychain-profile "$MACOS_NOTARY_PROFILE" --wait
  xcrun stapler staple "$dmg"
fi

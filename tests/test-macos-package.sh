#!/usr/bin/env bash
# macOS DMG 契约回归：校验镜像完整性，挂载后确认只有一个 .app、README 和 /Applications 链接，
# 再检查可执行文件架构、Info.plist 和未签名包的安装说明。
set -euo pipefail
cd "$(dirname "$0")/.."
# 只接受 Apple Silicon / Intel 两种运行时标识。
rid="${1:?Expected osx-arm64 or osx-x64}"
case "$rid" in osx-arm64) arch=arm64 ;; osx-x64) arch=x86_64 ;; *) exit 2 ;; esac
# 版本号与 build/package-macos.sh 一样从 Directory.Build.props 读取。
version="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' Directory.Build.props | tr -d '\r')"
# 未设置签名身份时产物名带 -unsigned，与打包脚本保持一致。
suffix="-unsigned"
if [[ -n "${MACOS_SIGN_IDENTITY:-}" ]]; then suffix=""; fi
dmg="artifacts/NengliangReader-v$version-$rid$suffix.dmg"
# 先让 hdiutil 校验镜像结构，再挂载做内容检查。
hdiutil verify "$dmg"
# 只读挂载到临时目录；无论断言是否失败都在 EXIT 时卸载并删除目录。
mount_dir="$(mktemp -d "${TMPDIR:-/tmp}/nengliang-check.XXXXXX")"
mounted=false
cleanup() {
  if $mounted; then hdiutil detach "$mount_dir"; fi
  rmdir "$mount_dir"
}
trap cleanup EXIT
hdiutil attach "$dmg" -readonly -nobrowse -mountpoint "$mount_dir"
mounted=true
# 根目录应恰好是 .app、README.md、Applications 三项。
[[ -d "$mount_dir/良子小窗.app" ]]
[[ -f "$mount_dir/README.md" ]]
[[ "$(readlink "$mount_dir/Applications")" == /Applications ]]
entries=("$mount_dir"/*)
[[ ${#entries[@]} == 3 ]]
executable="$mount_dir/良子小窗.app/Contents/MacOS/NengliangReader"
# 可执行文件必须包含请求的架构，Info.plist 语法必须合法。
[[ -x "$executable" ]]
lipo "$executable" -verify_arch "$arch"
plutil -lint "$mount_dir/良子小窗.app/Contents/Info.plist"
# 未签名包需要在说明里告诉用户如何通过「仍要打开」启动。
grep -q '仍要打开' "$mount_dir/README.md"
echo "DMG verification, layout, Applications link, executable architecture and installation notes passed: $rid"

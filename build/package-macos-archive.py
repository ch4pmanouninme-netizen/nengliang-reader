"""Package an existing self-contained publish directory into an unsigned .app tarball.

May run on Windows; does not claim macOS runtime validation, signing, or DMG creation.
Unix executable modes are recorded explicitly so extraction on macOS preserves them.
"""
# 跨平台备用打包：在非 macOS 上把已发布的目录封成 .app.tar.gz，仅用于分发/检查，
# 不做签名、公证或 DMG；可执行权限在 tar 头里显式记录，解压到 macOS 后仍可运行。
import argparse
import pathlib
import plistlib
import tarfile
import xml.etree.ElementTree as ET

# rid 决定目标架构；publish 是 dotnet publish 的输出目录，output 是 tar.gz 路径。
parser = argparse.ArgumentParser()
parser.add_argument("rid", choices=["osx-arm64", "osx-x64"])
parser.add_argument("publish", type=pathlib.Path)
parser.add_argument("output", type=pathlib.Path)
args = parser.parse_args()
repo = pathlib.Path(__file__).resolve().parent.parent
version = ET.parse(repo / "Directory.Build.props").findtext(".//Version")
if not (args.publish / "NengliangReader").is_file():
    parser.error("publish directory must contain NengliangReader apphost")
app = "良子小窗.app"
# Info.plist 与 build/package-macos.sh 保持一致的标识、版本和本地网络说明（归档不含图标）。
plist = plistlib.dumps({
    "CFBundleIdentifier": "com.hyacinth.nengliangreader", "CFBundleName": "良子小窗",
    "CFBundleExecutable": "NengliangReader", "CFBundlePackageType": "APPL",
    "CFBundleShortVersionString": version.split("-")[0], "CFBundleVersion": version.split("-")[0],
    "NSHighResolutionCapable": True, "LSUIElement": True,
    "NSLocalNetworkUsageDescription": "用于连接或主持您指定的局域网聊天房间。",
})
import io
args.output.parent.mkdir(parents=True, exist_ok=True)
with tarfile.open(args.output, "w:gz") as archive:
    entry = tarfile.TarInfo(f"{app}/Contents/Info.plist")
    entry.size = len(plist); entry.mode = 0o644
    archive.addfile(entry, io.BytesIO(plist))
    # uid/gid 归零、按扩展名设置权限，保证归档可复现且解压后可直接执行。
    def add(path, name):
        entry = archive.gettarinfo(str(path), name)
        entry.uid = entry.gid = 0; entry.uname = entry.gname = ""
        entry.mode = 0o755 if path.name in ("NengliangReader", "createdump") or path.suffix == ".dylib" else 0o644
        with path.open("rb") as data:
            archive.addfile(entry, data)
    for path in sorted(args.publish.rglob("*")):
        if path.is_file(): add(path, f"{app}/Contents/MacOS/{path.relative_to(args.publish).as_posix()}")
    for name in ("README.md", "LICENSE", "CHANGELOG.md"):
        add(repo / name, f"{app}/Contents/Resources/{name}")
    for path in sorted((repo / "docs").rglob("*")):
        if path.is_file(): add(path, f"{app}/Contents/Resources/docs/{path.relative_to(repo / 'docs').as_posix()}")
print(args.output)

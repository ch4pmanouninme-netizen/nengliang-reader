# 构建与发布

## 本地验证

安装 .NET 10 SDK，然后在仓库根目录执行：

```sh
dotnet restore NengliangReader.sln --configfile NuGet.Config
dotnet build NengliangReader.sln -c Release --no-restore
dotnet run --project tests/Reader.Core.Tests -c Release --no-build
dotnet run --project tests/Reader.Chat.Tests -c Release --no-build
dotnet run --project src/Reader.Desktop -c Release --no-build
```

测试项目是带非零失败退出码的控制台回归测试；请使用上述 `dotnet run`，不是 `dotnet test`。

Windows 图形冒烟检查（使用独立目录，不读写日常书库）：

```powershell
dotnet run --project src/Reader.Desktop -c Release --no-build -- --data ./artifacts/smoke-data --smoke-test
```

成功时生成 `artifacts/smoke-data/smoke-test.ok` 和 `shell-chat-smoke.ok`，覆盖阅读与真实聊天窗口、TLS 收发、重连、模块生命周期和服务窗口。失败时查看 `.error` 文件。此检查不验证全局快捷键、穿透或真实多屏行为。

## 安装包

Windows，使用 PowerShell 7：

```powershell
./build/package-windows.ps1
```

macOS，在 Mac 上执行：

```sh
bash build/package-macos.sh osx-arm64
bash build/package-macos.sh osx-x64
```

输出到 `artifacts/`。Mac 构建包含 `.app` 和 DMG；Intel 与 Apple Silicon 分开发行。跨架构构建只验证打包，不等于在对应架构运行过。

## macOS 签名与公证

正式对外发行前，准备 Apple Developer ID Application 证书，并在打包机钥匙串中安装。不要把证书、私钥、密码提交到仓库。

打包脚本支持两个环境变量：

- `MACOS_SIGN_IDENTITY`：钥匙串中的 Developer ID Application 身份名称。
- `MACOS_NOTARY_PROFILE`：通过 `xcrun notarytool store-credentials` 配置的钥匙串凭据名称。

两项都设置后，脚本签署原生库和应用、提交 DMG 公证、装订公证票据。只有签名时会签名但不会公证；都未设置时生成明确标注 unsigned 的预览 DMG。

签名使用 hardened runtime，附带 .NET JIT 所需 entitlements。发布者需要在本机运行：

```sh
codesign --verify --deep --strict --verbose=2 artifacts/osx-arm64/良子小窗.app
spctl --assess --type execute --verbose artifacts/osx-arm64/良子小窗.app
```

不要指导普通用户关闭系统安全保护来安装预览包。v2.0.0 按维护者要求使用普通 Release 类型发布；Mac 包仍标记 unsigned，签名、公证和真机验收状态必须在发行说明中明确。普通 Release 类型不代表这些验证已经完成。

## GitHub 流程

如果拿到的是本次交付的 Git bundle，可保留提交历史克隆并推送开发分支：

```sh
git clone -b feat/cross-platform NengliangReader-preview2.bundle nengliang-reader
cd nengliang-reader
git remote set-url origin https://github.com/ch4pmanouninme-netizen/nengliang-reader.git
git push -u origin feat/cross-platform
```

推送需要你自己的 GitHub 仓库写入权限。源码 ZIP 可用于查看或构建，但不含 Git 历史。

1. 将分支推送到仓库并发起 PR；等待 CI 通过。
2. 按下方清单完成 Windows 和 Mac 人工验收，更新 README 的验证状态。
3. 更新 `Directory.Build.props`、CHANGELOG 和发行说明中的版本。
4. 合并后从 Actions 手动运行 Build release draft，或推送与项目版本一致的 v* 标签。
5. `release.yml` 自动构建三种安装包并生成 SHA256，创建普通 Release 草稿，不自动公开。手动运行时使用项目版本和本次运行的提交 SHA。
6. 下载并复核资产和 SHA256。工作流默认没有 Apple 证书，因此 Mac 资产标记 unsigned；发行说明必须披露签名、公证与实机测试状态。
7. 编辑草稿说明，明确验证过的系统/架构和已知限制，再由维护者点击发布。

## 人工验收清单

- [ ] Windows：首次导入、旧版升级、重启恢复进度。
- [ ] Windows：全局热键冲突提示、自定义、关闭后释放。
- [ ] Windows：透明、穿透、托盘恢复、125%/150% 缩放。
- [ ] macOS Apple Silicon：首次启动、菜单栏、快捷键、穿透及恢复。
- [ ] macOS Intel：相同功能回归。
- [ ] macOS：Spaces、全屏应用、不同键盘布局、睡眠唤醒。
- [ ] 两端：左侧/上方副屏、拔掉副屏后恢复可见位置。
- [ ] 两端：中文/英文/emoji 连续翻页，不遗漏字符。
- [ ] 两端：导出后跨平台导入；重复书保留目的地进度。
- [ ] 下载：正常、取消、404、HTML 登录页、无 Content-Length、超限。
- [ ] 正式包：签名、公证、干净设备安装与卸载。


## 与上游 v1.5 的互通验证（Windows）

执行 
```powershell
./tests/build-upstream-bridge.ps1
dotnet run --project tests/Reader.Chat.Tests -c Release -- artifacts/upstream-bridge/UpstreamBridge.exe
```

桥接程序使用存档的原版网络层和 .NET Framework 编译器，测试新版客户端到旧版服务端、旧版客户端到新版服务端。仅绑定回环地址。

- [ ] Windows 与 Mac 两台真实设备互通、网络中断与证书更换。
- [ ] 客户端退出后独立服务继续运行；关闭聊天后在线人数下降。
- [ ] 上游 1.5 书库迁移后地址/昵称保留，房间码和聊天内容不落盘。


# 良子小窗 · macOS 安装说明

1. 根据电脑芯片选择安装包：Apple Silicon（M 系列）使用 `osx-arm64`，Intel 使用 `osx-x64`。
2. 打开 DMG，将「良子小窗.app」拖到旁边的「Applications」文件夹入口。
3. 复制完成后弹出磁盘映像，从 Finder 的「应用程序」中打开「良子小窗」。

应用自带运行环境，无需另行安装 .NET。依赖、资源及许可证均保存在应用包内，请勿移动或删除其中的文件。安装后无需保留 DMG。

## 首次打开（unsigned 包）

两个 Mac DMG 均包含 `良子小窗.app`、`Applications` 快捷入口和 `README.md`。将应用拖入 Applications 后，从“应用程序”启动。

文件名带 `unsigned` 表示尚未完成 Developer ID 分发签名与 Apple 公证。仅在确认安装包来自本仓库且未遭篡改后，对“无法验证开发者”或“Apple 无法检查是否包含恶意软件”的提示，可按以下步骤操作：

1. 先尝试打开应用一次。
2. 打开“系统设置 → 隐私与安全性”，找到该应用对应的拦截提示，点击“仍要打开”。
3. 按系统提示验证身份并确认打开。系统会为此应用保存例外，不需要关闭全局安全保护。

该操作不会让应用变成已签名或已公证的软件。若提示“已损坏”“会损坏电脑”或发现恶意软件，请先停止运行、核对下载与校验值并反馈具体错误，不要直接按上述步骤放行。受管理设备可能不允许添加例外。

参考：[Apple 官方首次打开说明](https://support.apple.com/zh-cn/102445)。



升级前请先退出应用与独立聊天服务，并备份书库，再替换「应用程序」中的旧版本。

- 使用说明：https://github.com/ch4pmanouninme-netizen/nengliang-reader#readme
- 问题反馈：https://github.com/ch4pmanouninme-netizen/nengliang-reader/issues

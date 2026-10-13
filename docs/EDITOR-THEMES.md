# 官方内置主题配色

核对日期：2026-10-13。包含 VS Code 官方安装包的 19 个内置主题，以及 JetBrains IntelliJ Platform 当前文档列出的全部 8 个界面主题。主页 → 切换主题按软件分组；阅读设置 → 外观可独立选择同一套官方主题。旧主题 ID、旧阅读外观序号继续兼容。

## 官方来源与范围

- [JetBrains 官方内置界面主题列表](https://www.jetbrains.com/help/idea/user-interface-themes.html)：8 个主题，与截图一致。Marketplace 的第三方主题不属于内置范围。
- JetBrains 的 JSON 主题定义取自 [intellij-community](https://github.com/JetBrains/intellij-community/tree/master/platform/platform-resources/src/themes)，解析 parentTheme、命名颜色和 Windows 平台分支；阅读文字取自相应官方 editorScheme。颜色数据来源遵循上游 Apache-2.0 许可，版权归 JetBrains 及贡献者所有。
- VS Code 配色来自本机官方安装目录版本 fc3def6774 的 theme-* 扩展，解析 include 继承。缺省控件色依据 [官方颜色注册表](https://github.com/microsoft/vscode/tree/main/src/vs/platform/theme/common/colors)。上游采用 MIT 许可，版权归 Microsoft 及贡献者所有。
- 实际使用的值保存在 `src/Reader.Desktop/OfficialThemes.json`，每个主题附带官方源文件链接。没有运行时网络依赖。

## 颜色映射

窗口主背景与阅读条使用编辑器背景，阅读文字使用编辑器普通文本色；界面文字、输入框、按钮、菜单、强调色按对应官方资源映射。主页标题区及聊天导航区使用工具栏/标题栏配色，因此 Light 与 Light with Light Header 可区分。8 位颜色值从源文件 RRGGBBAA 转为 Avalonia AARRGGBB。

这是官方配色在 Avalonia 控件上的映射，不是 JetBrains/VS Code 整套界面复刻。原生系统标题栏、阴影、字体抗锯齿和部分控件状态仍由系统或 Fluent 控件模板绘制。不同主题的语法高亮差异不会呈现在纯文本小说里；部分内置主题因此具有相同普通文本背景。应用不自动读取 IDE 当前主题。

对照背景时，请将阅读条不透明度设为 100%，并比较 IDE 编辑器区域；侧栏/工具栏可能使用另一种颜色。

## 完整清单

| 软件 | 内置主题 | 编辑器背景 | 阅读文字 | 官方定义 |
| --- | --- | --- | --- | --- |
| VS Code | Abyss | `#000C18` | `#6688CC` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-abyss/themes/abyss-color-theme.json) |
| VS Code | Light 2026 | `#FFFFFF` | `#202020` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/2026-light.json) |
| VS Code | Dark 2026 | `#121314` | `#BBBEBF` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/2026-dark.json) |
| VS Code | Dark+ | `#1E1E1E` | `#D4D4D4` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/dark_plus.json) |
| VS Code | Dark Modern | `#1F1F1F` | `#CCCCCC` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/dark_modern.json) |
| VS Code | Light+ | `#FFFFFF` | `#000000` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/light_plus.json) |
| VS Code | Light Modern | `#FFFFFF` | `#3B3B3B` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/light_modern.json) |
| VS Code | Visual Studio Dark | `#1E1E1E` | `#D4D4D4` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/dark_vs.json) |
| VS Code | Visual Studio Light | `#FFFFFF` | `#000000` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/light_vs.json) |
| VS Code | Default High Contrast | `#000000` | `#FFFFFF` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/hc_black.json) |
| VS Code | Default High Contrast Light | `#FFFFFF` | `#292929` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/hc_light.json) |
| VS Code | Kimbie Dark | `#221A0F` | `#D3AF86` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-kimbie-dark/themes/kimbie-dark-color-theme.json) |
| VS Code | Monokai | `#272822` | `#F8F8F2` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-monokai/themes/monokai-color-theme.json) |
| VS Code | Monokai Dimmed | `#1E1E1E` | `#C5C8C6` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-monokai-dimmed/themes/dimmed-monokai-color-theme.json) |
| VS Code | Quiet Light | `#F5F5F5` | `#616161` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-quietlight/themes/quietlight-color-theme.json) |
| VS Code | Red | `#390000` | `#F8F8F8` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-red/themes/Red-color-theme.json) |
| VS Code | Solarized Dark | `#002B36` | `#839496` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-solarized-dark/themes/solarized-dark-color-theme.json) |
| VS Code | Solarized Light | `#FDF6E3` | `#657B83` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-solarized-light/themes/solarized-light-color-theme.json) |
| VS Code | Tomorrow Night Blue | `#002451` | `#FFFFFF` | [源码](https://github.com/microsoft/vscode/blob/main/extensions/theme-tomorrow-night-blue/themes/tomorrow-night-blue-color-theme.json) |
| JetBrains | Islands Dark | `#191A1C` | `#BCBEC4` | [源码](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-resources/src/themes/islands/ManyIslandsDark.theme.json) |
| JetBrains | Islands Light | `#FFFFFF` | `#080808` | [源码](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-resources/src/themes/islands/ManyIslandsLight.theme.json) |
| JetBrains | Islands Darcula | `#2B2B2B` | `#A9B7C6` | [源码](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-resources/src/themes/islands/ManyIslandsDarcula.theme.json) |
| JetBrains | High Contrast | `#000000` | `#EBEBEB` | [源码](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-resources/src/themes/islands/HighContrast.theme.json) |
| JetBrains | Dark | `#1E1F22` | `#BCBEC4` | [源码](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-resources/src/themes/expUI/expUI_dark.theme.json) |
| JetBrains | Light | `#FFFFFF` | `#080808` | [源码](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-resources/src/themes/expUI/expUI_light.theme.json) |
| JetBrains | Light with Light Header | `#FFFFFF` | `#080808` | [源码](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-resources/src/themes/expUI/expUI_light_with_light_header.theme.json) |
| JetBrains | Darcula | `#2B2B2B` | `#A9B7C6` | [源码](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-resources/src/themes/darcula.theme.json) |

## 验证

运行 `dotnet run --project tests/Reader.Theme.Tests`（需要图形桌面会话）。测试逐套切换 27 个主题，检查现有窗口、按钮、输入框和标题区域的实际画刷，并检查偏好持久化、旧配置和未知 ID 回退。此测试不代替 Windows/macOS 的人工交互验收。

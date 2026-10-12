# 良子阅读器

Windows 免安装悬浮阅读器，支持 TXT、EPUB 和 1～10 行显示。当前版本：1.3.1。

## 下载使用

在本仓库右侧的 **Releases** 中下载「良子阅读器-v1.3.1.zip」，解压后运行「良子阅读器.exe」。

需要 Windows 10 / 11 和 .NET Framework 4.8。

打开 TXT 或 EPUB → 点「显示」开始阅读。在「设置 → 显示行数」中选择 1～10 行。

## 功能

- TXT：UTF-8、UTF-16、GBK / GB18030 中文编码。
- EPUB：按章节顺序读取纯文本。
- 悬浮窗口支持 1～10 行、拖动、调整宽度、字号和不透明度。
- 企业微信浅色、IDEA 深色和透明文字等外观。
- 全局翻页、隐藏、自动阅读与鼠标穿透。
- 自动保存进度，每本书一个书签，支持查找和跳转。
- 本地运行，无账号要求，不联网。

| 快捷键 | 功能 |
| --- | --- |
| Ctrl + Alt + H | 隐藏 / 恢复 |
| Ctrl + Alt + ← / → | 上一页 / 下一页 |
| Ctrl + Alt + 空格 | 自动阅读 / 暂停 |
| Ctrl + Alt + M | 鼠标穿透开关 |

快捷键被占用时可从系统托盘操作。完整说明见 [使用说明.txt](使用说明.txt)。

## 从源码构建

使用 Windows PowerShell，在项目根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\build.ps1
```

脚本使用本机 .NET Framework 自带的 C# 编译器，不下载依赖。生成的 EXE 位于项目根目录。

## 验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\test.ps1
```

1.3.1 版本通过 35 项核心断言和 32 项界面集成检查，覆盖导入、分页、行数、进度保存、外观和穿透。界面检查会临时打开程序自己的测试窗口，结果保存在 `source/test-results`。

## 文件

- `source/Reader.cs`：阅读器实现。
- `source/build.ps1`：构建脚本。
- `source/Reader.Tests.cs`、`source/Reader.UiTests.cs`：核心及界面检查。
- `source/test.ps1`：验证入口。

阅读数据保存在 `%LOCALAPPDATA%\NengliangReader`，与程序文件分开。暂不支持 PDF、加密 EPUB 和纯图片书籍。

## 许可证

[MIT License](LICENSE)。

# 2.0.2 书库管理变更与验证

基线：用户提供的 2.0.1 Windows 包，其 PDB SourceLink 指向提交 `8407253c6de50069c3c839a3b363b4bd9d925421`。
在该提交的源码归档上进行修改，没有提交、推送或发布 GitHub 内容。

## 修改范围

- `src/Reader.Core/Book.cs`：书籍的置顶和分组字段。
- `src/Reader.Core/Store.cs`、`BookLibrary.cs`：兼容旧 XML、持久化、筛选排序、删除与失败/中断恢复。
- `src/Reader.Core/LibraryArchive.cs`：新增书籍导入备份时恢复分类；重复书保留目的地信息。
- `src/Reader.Desktop/MainWindow.cs`、`BookLibraryView.cs`：逐书右键菜单、分组筛选、删除当前书后的窗口清理。
- `src/Reader.Desktop/Dialogs.cs`：删除确认框。
- `tests/Reader.Core.Tests/BookLibraryTests.cs`、`Program.cs`：数据和文件失败回归。
- `src/Reader.Desktop/BookLibrarySmokeTests.cs`：真实窗口行菜单、右鼠标事件、分组对话框和删除确认交互。
- `tests/Test-WindowsPackage.ps1`：安装包检查必须包含书库操作标记。
- 版本、README、CHANGELOG、Windows 安装说明及书库管理文档。

没有修改聊天协议、原生平台集成、导入器或阅读分页算法。没有读取或改写日常书库；测试均使用独立目录。

## 验证

环境：Windows x64、.NET SDK 10.0.401、.NET 10.0.12、Avalonia 11.3.22。

| 检查 | 结果 |
| --- | --- |
| Release 编译 | 通过，零错误、零警告 |
| 既有阅读/下载/备份数据回归 | 通过 |
| 新增书库数据回归 | 21 项通过 |
| 聊天协议与真实本机 TLS | 28 项通过 |
| 新增书库窗口和右鼠标事件检查 | 53 项通过 |
| 既有主页/聊天窗口检查 | 通过，包含收发、隐藏、重连和独立关闭 |
| Windows 安装包 | 检查根目录、中文/空格路径、相对参数、完整冒烟和独立服务启动 |

数据回归包含旧索引兼容、置顶排序、分组规范化、重启恢复、备份迁移、重复书保留、保存失败回滚、删至空库、删除中断前后恢复和原始文件保留。
窗口检查使用实际列表行，通过右鼠标按下事件打开菜单，验证右键不会先切换阅读选择并阻塞菜单。删除确认和分组对话框通过真实控件事件执行。

本次未构建或实机验证 macOS 包，未替代 Windows 多屏、缩放、真实全局热键和鼠标穿透的人工验收。

## 运行

Windows 包解压到新文件夹后运行根目录“良子小窗.exe”。保留旁边的 app 文件夹。
仍使用现有应用数据目录；建议按现有“书库备份”功能保留升级前备份。
完整操作见 [BOOK-LIBRARY.md](BOOK-LIBRARY.md)。

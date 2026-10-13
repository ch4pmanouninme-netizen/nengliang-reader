# 良子小窗（跨平台版）

适用于 Windows 和 macOS 的悬浮阅读与局域网加密聊天工具。基于上游 v1.5.2 迁移，保留 TXT / EPUB、进度保存、全局快捷键，并提供链接下载与跨平台书库备份。

**当前分支：2.0.0-preview.2，跨平台开发预览，尚未发布。** 本次上游基线为 [`f30b346`](https://github.com/ch4pmanouninme-netizen/nengliang-reader/commit/f30b3469879f3d0a72be43557fc2fadb32c1dde2)，Windows Forms v1.5.2 快照保留在 [`legacy/v1.5.2/`](legacy/v1.5.2/)。Mac 原生功能已编写，仍需真机验收；预览包不是已验证的稳定版。

[发行版本](https://github.com/ch4pmanouninme-netizen/nengliang-reader/releases) · [提交问题](https://github.com/ch4pmanouninme-netizen/nengliang-reader/issues) · [更新日志](CHANGELOG.md) · [构建与发布](docs/RELEASE.md)

## 功能

- **统一主页**：阅读和聊天独立开关，关闭主页收进托盘，明确退出才关闭客户端。
- **加密聊天**：兼容上游 v1.5 协议，TLS 1.2、SHA-256 指纹校验、限流、有界缓存、自动重连。
- **独立服务**：Windows/macOS 均能启动聊天服务，客户端退出不影响独立服务。

- **本地阅读**：TXT（UTF-8、UTF-16、GBK/GB18030）和 EPUB 纯文本导入，支持拖放。
- **悬浮阅读**：1～10 行显示、拖动定位、右边缘调整宽度、字号、透明度和多种外观。
- **阅读操作**：前后翻页、自动翻页、查找、百分比跳转，每本书一个书签。
- **快捷控制**：自定义全局快捷键、注册失败提示、鼠标穿透、托盘/菜单栏恢复入口。
- **进度保存**：本机自动保存、备份恢复、相同正文去重，兼容旧版书库。
- **链接下载**：粘贴无需登录的 TXT/EPUB 文件链接，显示进度，支持取消，完成后加入书架。
- **书库迁移**：导出 ZIP 备份，在另一台 Windows 或 Mac 上导入；重复书籍保留目的地进度。

暂不支持 PDF、加密 EPUB、纯图片书籍、在线书源搜索、小说网页抓取、云同步。EPUB 只提取正文，不保留原图文排版。

## 平台与验证状态

| 平台 | 构建目标 | 状态 |
| --- | --- | --- |
| Windows x64 | `win-x64` | 发布构建与窗口冒烟检查通过；真实热键、多屏、缩放待人工验收 |
| macOS Apple Silicon | `osx-arm64` | 发布构建通过，真机交互待验收 |
| macOS Intel | `osx-x64` | 发布构建通过，真机交互待验收 |

技术栈为 **.NET 10 + Avalonia 11.3.22**。自包含发行包无需用户额外安装 .NET。具体系统最低版本应符合 [.NET 10 支持平台](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)与 [Avalonia 平台要求](https://docs.avaloniaui.net/docs/supported-platforms)；本项目尚未对所有版本完成测试。

Mac 在 Spaces、全屏应用、睡眠唤醒及不同键盘布局下的行为需要单独验证。普通窗口置顶不代表能覆盖所有全屏应用。

当前验证记录见 [VALIDATION.md](docs/VALIDATION.md)。

## 界面预览

下图为 Windows 自动化冒烟检查中渲染的书架，使用内置测试正文。

![Windows 书架预览](docs/images/windows-library.png)

![Windows 主页](docs/images/windows-home.png)

![Windows 聊天](docs/images/windows-chat.png)

## 下载与使用

在 [Releases](https://github.com/ch4pmanouninme-netizen/nengliang-reader/releases) 中选择对应平台。若尚无 2.x 包，请按下方步骤从源码运行。

- Windows：解压 `NengliangReader-win-x64.zip`，运行 `NengliangReader.exe`，保留同目录其他文件。
- Mac：根据芯片选择 `osx-arm64` 或 `osx-x64`，打开 DMG，将应用复制到“应用程序”。带 `unsigned` 的文件是开发预览，不是已签名公证的正式安装包。
- 本次交付的 Mac `unsigned.tar.gz` 是交叉构建的 `.app` 归档，在 Mac 解压后用于开发验收；尚未签名、公证或真机运行。正式 DMG 需在 Mac 按发布文档生成。

### 本地阅读

1. 从主页进入“阅读”，点击“打开”选择 TXT/EPUB，或将文件拖入书架。
2. 选择书籍，点击“显示”。
3. 拖动阅读条调整位置，拖动右边缘调整宽度。
4. 右键打开翻页、书签、查找与设置菜单；双击阅读条返回书架。
5. 隐藏阅读条时自动暂停；在托盘或 Mac 菜单栏选择“主页”，再进入“阅读”可关闭穿透并恢复操作。

### 局域网聊天

1. 一位使用者从主页点击“启动独立聊天服务”，选择局域网网卡地址，点击“启动服务”。
2. 点击“复制连接信息”，通过可信渠道分享给参与者。默认端口为 `38417`；本机测试可选择 `127.0.0.1`。
3. 每位参与者从主页进入“聊天”，点击“粘贴连接信息”，填写昵称后连接。
4. Enter 发送，Shift+Enter 换行；“隐藏”或 Esc 保持连接；关闭聊天或“离开”断开连接并清空本机缓存。

Windows 可双击 `Start-ChatServer.cmd`；从源码可运行 `dotnet run --project src/Reader.Desktop -- --server`。Mac 已安装应用也可通过主页启动独立服务。

兼容上游 1.5.0 服务与 1.5.2 客户端的协议 v2，**不兼容 1.4 明文协议**。服务窗口内停止/启动保留证书和房间码；关闭后重开会更换，两端需重新分享信息。

默认最多 30 人、单条 500 字符、每连接每 10 秒 8 条消息。服务历史最多 100 条，客户端显示最多 150 条，待处理消息最多 182 条，每连接发送队列最多 160 条。断线后每 5 秒重试；错误指纹、错误房间码或重复昵称需用户修正后重连。重连只恢复服务端仍保留的历史，不保证消息不丢失。

TLS 保护客户端到服务端的传输，服务端可以读取消息，不是端到端加密。指纹必须通过可信渠道获取。应用不安装信任证书；Windows 使用用户密钥容器，macOS 使用 .NET 管理的临时钥匙串。详见 [跨平台证书说明](https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography#read-a-pkcs12pfx)。

服务仅读取本机主题偏好，不写入书库。退出客户端不会停止独立服务；关闭服务会断开所有连接并清空服务历史。当前只实现文字聊天，无图片附件、账号或审计，不自动配置防火墙。

### 链接下载

点击“链接下载”，粘贴 **HTTP/HTTPS 文件下载链接**。链接可以不带文件后缀，但服务器必须返回可识别的 TXT 或 EPUB 内容。小说介绍页、章节网页、需要登录的链接不适用。

下载最长等待 5 分钟，最多跟随 5 次重定向。TXT 导入上限 24 MB；EPUB 文件上限 100 MB，正文展开另有 24 MB 限制。失败后重新点击“链接下载”重试；当前版本不支持断点续传。

请仅下载你有权获取的电子书。软件不提供书源、账号登录或绕过付费的功能。

### 默认快捷键

Windows 使用 `Ctrl + Alt`，Mac 使用 `Control + Option`，再加下表按键：

| 按键 | 功能 |
| --- | --- |
| H | 隐藏 / 恢复此前可见的主页、阅读和聊天窗口 |
| → | 下一页 |
| ← | 上一页 |
| M | 切换鼠标穿透 |
| Space | 自动阅读 / 暂停 |

在书架“快捷键”中可以修改；注册失败会显示在书架底部。Mac 使用注册热键方式，键位表现仍需在目标键盘布局上验收。

阅读条获得焦点后，方向键/PageUp/PageDown 翻页，Space 切换自动阅读，Esc 隐藏；Ctrl（Mac 也支持 Command）+ B/J/F/G 分别保存书签、回书签、查找、跳转。

## 数据与隐私

| 系统 | 数据目录 |
| --- | --- |
| Windows | `%LOCALAPPDATA%\NengliangReader` |
| macOS | `~/Library/Application Support/NengliangReader` |

正文位于 `books/`，进度与偏好位于 `state.xml`，快捷键位于 `hotkeys.json`。保存使用临时文件替换并保留 `.bak`；损坏时优先读取备份。旧版程序旁边的 `data/` 目录会在目标书库不存在时复制迁移，保留原文件。

正常本地阅读不访问网络。链接下载访问目标服务器及重定向地址；聊天访问用户填写的服务地址；启动服务只监听所选网卡和端口。不上传书库，不包含遥测或广告。开发时恢复依赖会连接 NuGet。聊天只保存地址、端口和昵称；不保存房间码、指纹和聊天正文。

使用“书库备份 → 导出书库”备份书籍与进度；导入时合并书籍，重复书籍不覆盖当前进度。备份不包含全局快捷键；导入不会覆盖目标设备外观或窗口位置。单个备份导入上限为展开后 256 MB。

## 从源码运行

先安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，再执行：

```sh
git clone https://github.com/ch4pmanouninme-netizen/nengliang-reader.git
cd nengliang-reader
dotnet restore NengliangReader.sln --configfile NuGet.Config
dotnet build NengliangReader.sln -c Release --no-restore
dotnet run --project src/Reader.Desktop -c Release --no-build
```

上述命令适用于包含 2.x 源码的分支；若主分支尚未合并，请先切换到对应开发分支。

使用独立书库测试：

```sh
dotnet run --project src/Reader.Desktop -- --data ./artifacts/test-library
```

## 测试与打包

```sh
dotnet run --project tests/Reader.Core.Tests -c Release
dotnet run --project tests/Reader.Chat.Tests -c Release
```

测试是控制台回归程序，失败返回非零退出码。覆盖编码、EPUB 章节顺序、分页、进度与备份、链接下载、取消、重定向和书库迁移。请使用 `dotnet run`，而非 `dotnet test`。

Windows：`pwsh ./build/package-windows.ps1`。Mac：`bash build/package-macos.sh osx-arm64` 或 `osx-x64`。详细签名、公证、验证清单和 GitHub 草稿发布流程见 [发布文档](docs/RELEASE.md)。

## 项目结构

```text
src/Reader.Core/               导入、阅读会话、存储、下载与书库备份
src/Reader.Chat/               v1.5 兼容协议、TLS、服务与客户端连接
src/Reader.Desktop/            Avalonia 界面与交互
src/Reader.Platform.Windows/   Windows 热键和鼠标穿透
src/Reader.Platform.Mac/       macOS 热键和鼠标穿透
tests/Reader.Core.Tests/       跨平台回归测试
tests/Reader.Chat.Tests/       TLS、协议边界与上游互通测试
build/                        安装包与签名脚本
docs/                         实施范围、验证与发布说明
legacy/                       历史 Windows Forms 源码，含 v1.5.2 快照
```

贡献方式见 [CONTRIBUTING.md](CONTRIBUTING.md)。反馈问题时请提供应用版本、系统/芯片、操作步骤及错误提示，不要公开个人书籍或敏感下载链接。

## 许可证

[MIT License](LICENSE)。依赖组件遵循各自许可证；许可不包含用户导入电子书的版权授权。



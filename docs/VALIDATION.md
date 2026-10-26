# 验证记录

## 2.3.8 — 2026-10-26

发布：[v2.3.8](https://github.com/ch4pmanouninme-netizen/nengliang-reader/releases/tag/v2.3.8)。Windows x64、macOS arm64/x64 安装包与 SHA256SUMS 已发布。

### 本版本范围

- **章节选择**：阅读条右键菜单「跳转进度」改为「选择章节」。EPUB 使用书内 `nav.xhtml` / `toc.ncx` 目录，按 spine 映射到正文偏移；TXT 在导入时生成标题索引。索引保存为 `books/<id>.chapters.tsv`，随书库备份迁移。
- **导入与数据完整性**：大 EPUB 解压上限 24 MB → 128 MB（按实际字节累计）；正文原子写入与半截文件修复；统一书籍 ID 校验；空分组拒绝；代理对安全截断；备份分组修正；损坏 `agent.xml` 降级为 `null`。
- **性能**：EPUB 逐章正则复用；`Normalize` 单遍处理；TextLayout 缓存；章节索引后台构建；拖拽/移动事件合并；聊天记录批量刷新。
- **聊天**：非流式超长回答截断后继续分片；JSON 嵌套深度上限；SSE 错误帧广播到房间；服务端拒绝消息移出全局锁；Agent 分片顺序与 Connect/Dispose 竞态修正。
- **备份**：导入改为逐本校验与合并，降低大备份峰值内存。

### 自动验证（已执行）

GitHub Actions `CI` 在 `a0beb88` 上通过（[运行记录](https://github.com/ch4pmanouninme-netizen/nengliang-reader/actions/runs/40277156340)）；`Build release draft` 在 `v2.3.8` 上完成三平台构建并发布。CI 覆盖：

- 解决方案 Release 构建
- `Reader.Core.Tests`：导入、章节索引、备份、持久化、书库管理
- `Reader.Chat.Tests`：TLS、协议、限流、Agent、上游 v1.5 互通
- `Reader.Theme.Tests`
- `Reader.ChatUi.Tests`（Windows）
- 桌面冒烟：真实窗口、书库菜单、聊天窗口
- Windows 安装包目录与启动校验

### 待人工验收（Windows）

- [ ] 导入《剑来》等大 EPUB：进度按章节推进，导入成功
- [ ] 首次打开「选择章节」：阅读条仍可翻页/拖动，出现「正在读取目录…」，章节数正确
- [ ] 章节筛选、当前章节定位、双击/回车跳转
- [ ] 导入 TXT 后目录立即可用；重启后仍可跳转
- [ ] 已有 EPUB/TXT 重新导入一次：不产生重复书，生成 `.chapters.tsv`
- [ ] 书库备份导出/导入：章节索引随备份迁移；重复书保留目的地进度
- [ ] 删除书籍：正文与 `.chapters.tsv` 一并清理
- [ ] 拖拽调整宽度：松手后文字正常重排；窗口移动后重启位置保持
- [ ] 阅读条渲染与 2.3.5/2.3.6 一致（TextLayout 缓存）
- [ ] 聊天：重连回放历史不卡顿；房间已满/重名拒绝仍正常
- [ ] 助手：端点报错时房间出现「（助手回答失败）」，服务窗口状态显示具体错误
- [ ] 旧书库升级后可打开；没有 sidecar 的旧书回退标题识别/百分比跳转

### 待人工验收（macOS）

- [ ] 全局热键**按下即触发**（2.3.6 修正，尚未真机确认）
- [ ] 阅读条缩放、透明、鼠标穿透、多屏/Spaces
- [ ] 章节选择与 TextLayout 缓存后的渲染
- [ ] 聊天 TLS、菜单栏、睡眠唤醒
- [ ] DMG 未签名/未公证的首次打开说明

### 已知限制

- macOS 包未完成 Developer ID 签名与公证；实机交互尚未验收。
- 旧书没有 `.chapters.tsv` 时继续使用标题启发式；重新导入可生成结构目录。
- 书库备份导入仍非事务性：合并中途失败可能留下已加入的书（本次只降低峰值内存）。

---

## 2.0.0-preview.2 — 2026-10-13

基线上游：v1.5.2 / f30b346。

### 本机已执行

环境：Windows x64、.NET SDK 10.0.401、Avalonia 11.3.22。

| 项目 | 结果 |
| --- | --- |
| 完整解决方案 Release 构建 | 通过，0 警告、0 错误 |
| Reader.Core.Tests | 58 项断言通过：阅读、编码、EPUB、下载、持久化、备份及聊天偏好 |
| Reader.Chat.Tests | 28 项协议/TLS/限流/缓存断言通过 |
| 原版 Framework 桥接 | 另 2 项通过，共 30 项聊天检查：新版客户端→原版服务、原版客户端→新版服务 |
| Avalonia 实际窗口冒烟 | 通过；主页状态、模块独立关闭、真实 TLS 收发、全局隐藏/恢复、重连、缓存清理、服务窗口启停 |
| 阅读窗口冒烟 | 通过：窗口、布局、导航、穿透 API 调用及持久化 |
| Windows 自包含 EXE | 独立启动上述图形冒烟，退出码 0，生成两个成功标记 |
| win-x64 / osx-arm64 / osx-x64 自包含 publish | 三项通过 |

测试只用隔离书库及 127.0.0.1，没有在公司网络启动共享房间。图片在 docs/images，不含个人书籍或真实房间信息。
初次恢复依赖出现 NuGet 审计数据不可用警告，恢复网络后最终完整构建已无警告。

### 审查与修复

- Standards：退出期间其他模块更新偏好可能丢失，改为保存至最新 revision 后退出。
- Standards：服务主题文件改后台单任务读取，文件未改变时跳过解析。
- 补充停止服务后锁内 running 检查，防止并发消息重新填入历史。
- Spec：更新本记录，区分交叉构建与实际运行。上述代码修复经复核，限定范围无剩余发现。

### 尚未执行

- macOS 真机运行：证书握手、热键、穿透、菜单栏、Spaces、多屏、睡眠唤醒、键盘布局。
- Windows 全局热键冲突、不同 DPI/多屏人工验收。
- Mac 与 Windows 两台物理设备互通、企业网络/防火墙、长时间负载。
- Mac 上的 DMG 打包、Developer ID 签名、公证与干净设备安装。
- GitHub 远程 CI/Release 执行；本次代码尚未推送。

Mac tar.gz 是 Windows 交叉发布后组装的 **未签名 .app 开发预览**，记录了 Unix 可执行权限；不是已验证或已公证的正式包。
复现命令见 [RELEASE.md](RELEASE.md)。窗口测试需要桌面会话与独立 --data 目录，成功标记为 smoke-test.ok 和 shell-chat-smoke.ok。

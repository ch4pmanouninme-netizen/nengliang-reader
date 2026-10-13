# v1.5.2 跨平台迁移范围

上游仓库：<https://github.com/ch4pmanouninme-netizen/nengliang-reader>

固定基线：`f30b3469879f3d0a72be43557fc2fadb32c1dde2`，客户端 v1.5.2、聊天协议 v2。
原始源码与说明保存在 `legacy/v1.5.2`，其中路径说明属于上游打包布局，不能代替本项目根目录 README 的构建命令。

## 本轮实现要求

1. 用 .NET 10 + Avalonia 支持 Windows x64、macOS Apple Silicon 和 Intel。
2. 保留阅读、下载、备份功能；迁移 v1.5.2 的主页导航和独立模块生命周期。
3. 打开或关闭子模块不改变主页可见性；返回主页不关闭子模块。关闭主页隐藏；明确退出关闭全部客户端模块。
4. 关闭阅读保存进度并释放正文/悬浮窗；关闭聊天断开并清空缓存。隐藏聊天保留连接。全局隐藏可恢复此前可见窗口。
5. 复用上游文字协议、TLS 1.2、指纹验证、房间码、昵称唯一性、容量与限流；验证上游双向互通。
6. 服务支持 Windows/macOS，以独立进程启动；读取本机主题但不回写书库。相同服务窗口内重启保留证书和房间码。
7. 地址/端口/昵称持久化；房间码、指纹、消息不持久化。无明文降级。
8. 提供真实 TLS、协议边界、窗口生命周期测试；更新 README、CI、三架构构建与发布说明。
9. 当前 Windows 主机可验证运行和交叉构建，不能将 macOS 未执行的运行、DMG、签名、公证描述为已通过。

## 文件映射

| 上游 | 跨平台实现 |
| --- | --- |
| Reader.cs | Reader.Core + Reader.Desktop/MainWindow、ReaderWindow |
| AppShell.cs | Reader.Desktop/HomeWindow、App |
| ChatClient.cs | Reader.Chat/ChatClient + Reader.Desktop/ChatWindow |
| ChatServer.cs | Reader.Chat/ChatServer + Reader.Desktop/ServerWindow |
| ChatShared.cs、ChatSecurity.cs | Reader.Chat，同名文件 |
| AppUi.cs | Avalonia 主题、UiActions、只读服务主题同步 |

上游 Reader.cs 相比先前阅读核心的新增偏好是 ChatHost、ChatNick、ChatPort，已迁移到现有 XML Store。
聊天网络类从上游提取，窗口代码重写；证书使用现代加载 API，Windows 使用 UserKeySet，macOS 使用 DefaultKeySet 的临时钥匙串，避免不受支持的 EphemeralKeySet。
网络层补充 InvalidDataException 的预期错误分类，界面通过带代次号的有界 ChatInbox 消化消息，旧连接回调不能污染新会话。

## 结构与边界

Reader.Core 管书库；Reader.Chat 不引用 UI；Reader.Desktop 管窗口和线程调度；平台项目仅负责原生热键和鼠标穿透。
所有偏好写入在 UI 线程创建快照后异步序列化保存。服务进程只读 state.xml 的主题。
聊天窗口保存最多 150 条，队列 182 条；服务器沿用上游 100 条历史、30 人、500 字符、160 条发送队列和每 10 秒 8 条限流。

## 验证命令

见 [RELEASE.md](RELEASE.md) 和 [VALIDATION.md](VALIDATION.md)。`tests/build-upstream-bridge.ps1` 从原始源码提取网络类，用 .NET Framework 编译，避免用同一迁移实现冒充兼容性证据。

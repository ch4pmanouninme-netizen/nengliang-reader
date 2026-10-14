# 启动 / 停止 / 更新

不用再分发压缩包：代码在本机留一份，桌面上点一下就是最新版。

## 一次性准备（每台机器做一次）

1. 装 [Git for Windows](https://git-scm.com/download/win)。
2. 装 **.NET SDK 10.0.1xx**（不是 Runtime）：<https://dotnet.microsoft.com/download/dotnet/10.0>。
   仓库的 `global.json` 要求 `10.0.100`。装 SDK 需要管理员权限，装完 `dotnet --list-sdks` 能看到 10.x 就行。
3. 克隆代码，位置随便挑，之后不用再动：

   ```
   git clone https://github.com/<组织>/cloud-nengliang-reader.git
   ```

4. **双击仓库里 `scripts\install.bat`**。它会在桌面上放三个图标，然后就可以把这个窗口关掉了。

以后就点桌面上的图标：**启动 / 停止 / 更新**。

## 三个入口

| 图标 | 作用 | 要不要联网 |
| --- | --- | --- |
| 启动 | 启动。第一次会自动编译一份到本机固定目录 | 不用 |
| 停止 | 停止。先请程序自己保存退出，等不到回应才强杀 | 不用 |
| 更新 | 停止 → 拉最新代码 → 编译 → 切换 → 启动 | 要（GitHub + nuget.org） |

「停止」和「更新」的窗口跑完不会自己关，这样出错时你能把报错看全 —— 关掉窗口就行。

## 为什么改成这样

发行压缩包跑的是**打包那一刻**的代码：同事改了功能，旧包用户看不到，只能重新发一个几十 MB 的包；
而且每解压到一个新目录，Windows 防火墙的入站规则就失效一次（规则按进程映像的完整路径匹配），
用户点了「取消」就会出现「界面显示已启动、别人连不上」，极难排查。

现在代码只放一份、路径永远不变，更新一下重启就是新版本。

## 目录

```
%LOCALAPPDATA%\
├── NengliangReader\          数据目录：书库、进度、设置、agent.xml。脚本不碰它
└── NengliangReaderCode\      代码目录：脚本管的
    ├── run\                  当前版本。路径固定不变，防火墙规则指向这里
    ├── staging\              编译中途的落脚点，成功后才改名成 run
    ├── previous\             上一版，留着回退
    └── launch.json           上次启动用的参数
```

**`run\` 的路径必须固定** —— 这是整个方案的关键。防火墙入站规则按进程映像的完整路径匹配，
只要监听进程始终是 `%LOCALAPPDATA%\NengliangReaderCode\run\NengliangReader.exe`，
规则建一次就一直有效，换版本不用重新授权。

代码和数据分开，是为了让 `--data` 指向别处的测试或第二份书库不会顺手把代码目录也带过去。

## 常见操作

**启动时带参数。** 参数会原样转交给程序，并且记在 `launch.json` 里，之后「停止」和「更新」
沿用同一份，不会出现「启动用了一个数据目录、停止找的是另一个」。要用参数就先从命令行跑一次：

```
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\path\to\repo\scripts\start.ps1" --data D:\mybooks
```

**更新。** 直接点桌面上的「更新」。加了哪些提交、有没有本机改动，都会打在窗口里。

拉取失败（连不上 GitHub、没权限）就地停下，正在跑的版本一个字都不改。
编译失败会把代码退回更新前那一版 —— 不会留下「代码是新的、跑的是旧的」这种错位。
这两条都实测过：失败时 `run\` 里的文件时间戳一个都不变。

**本机改过仓库里的文件。** 默认会停下并列出是哪些文件，确认不要了再加参数重跑：

```
powershell -NoProfile -ExecutionPolicy Bypass -File "<repo>\scripts\update.ps1" -Force
```

**回退到上一版。** 把 `%LOCALAPPDATA%\NengliangReaderCode\run` 删掉（或改名留档），
再把 `previous` 改名为 `run`。`previous` 里是上一版**编译好**的产物，回退不需要联网也不用编译器。

**指定分支。** 默认跟当前分支的远端跟踪分支走，没有就退回 `main`：`update.ps1 -Branch main`。

**只更新不自动启动。** `update.ps1 -SkipStart`。

**装到别处。** 三个脚本都有 `-CodeRoot`，默认是 `%LOCALAPPDATA%\NengliangReaderCode`。
换一个目录就等于养一条独立通道，两边的版本、进程和启动参数互不干扰。
回归测试也用它：整套流程跑在临时目录里，不碰你正在用的那份。

## 已知的边界

- **每台机器都要装 .NET SDK。** 代码在本机编译，这是「跑本地代码」换来的代价。只装 Runtime 不够。
- **第一次编译要联网。** 要拉 win-x64 的运行时包（`NuGet.Config` 里只有 nuget.org 一个源）。
  之后依赖进了本机缓存，「启动」就完全不需要网络了。
- **停止时聊天服务是被强杀的。** 主程序走控制通道优雅退出（会先保存进度），
  但聊天服务是它拉起来的独立子进程，不参与这套握手，只能收尾时结束掉。
- **更新会丢弃本机对受版本管理文件的改动**（`git reset --hard`）。未跟踪的文件不受影响。
- **脚本可能被执行策略挡住。** 快捷方式里已经带了 `-ExecutionPolicy Bypass`；
  如果单位用组策略强制了更严的策略，需要改成 `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` 或找 IT。
- **从 1.x 旧包升上来的书库不会自动迁移。** 自动迁移只在数据目录旁边还留着旧 `data\` 时生效，
  重构后代码在新位置，找不到旧包。这种情况请手动把旧 `data\` 里的 `state.xml` 和 `books\`
  拷到 `%LOCALAPPDATA%\NengliangReader\`。

## 脚本本身

随仓库走，所以「更新」拉下来的新脚本下次就生效。

| 文件 | 作用 |
| --- | --- |
| `install.bat` | 唯一一个双击文件：生成桌面快捷方式 |
| `install-shortcuts.ps1` | 生成快捷方式的实际逻辑，`-Destination` 可指定输出目录 |
| `common.ps1` | 目录、进程识别、编译、切换。不要直接运行 |
| `start.ps1` / `stop.ps1` / `update.ps1` | 三个动作 |
| `README.md` | 本文件 |

停止链路的回归测试在 [`tests/Test-ReaderScripts.ps1`](../tests/Test-ReaderScripts.ps1)：
它把打包产物复制到临时代码目录里，真跑一遍「启动 → 停止 → 再停止」，并覆盖
「有锁却不回应时停止必须报失败」。打包测试（`tests/Test-WindowsPackage.ps1`）会带上它，
所以在 CI 和发版流程里都会跑到。

**为什么快捷方式直接指向 `.ps1`，而不是套一层 `.bat`：** 更新会把仓库里的文件原地重写，
而 cmd 是**边跑边按行读** `.bat` 的 —— 把正在执行的 `.bat` 换掉，它接下来会从错位的字节位置继续读，
执行到半截的行（实测过一次，冒出 `'-SkipStart' is not recognized` 这种莫名其妙的报错）。
PowerShell 则是先把整个脚本读进内存再执行，跑起来之后文件怎么变都无所谓。
`install.bat` 之所以还是 `.bat`，是因为它只生成快捷方式，什么都不重写，没有这个风险。

**两个编码上的坑，改脚本时注意：**

- `.ps1` **必须带 UTF-8 BOM**。PowerShell 5.1 会把没有 BOM 的 UTF-8 当 ANSI 读，里面的中文全变乱码。
- `.bat` **不要写中文**。cmd 按控制台代码页读 `.bat`，中文同样会乱码。
- git 的输出永远是 UTF-8，而 PowerShell 默认按控制台代码页解码子进程输出，两边混在一起就成了乱码。
  `common.ps1` 开头把控制台切到 UTF-8，别把那两行删了。

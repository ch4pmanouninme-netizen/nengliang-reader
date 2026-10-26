# 启动 / 停止脚本的契约回归。跟着 Test-WindowsPackage.ps1 跑，用打包出来的真实程序当被测对象 ——
# 停止链路横跨脚本、控制通道和主程序三层，只有真跑一遍才算验过。
#
# 只断言退出码和「进程 / 单实例锁」的状态，不断言脚本打印的文案：文案归脚本自己，随时可以改，
# 而且把中文断言写进测试会连带依赖控制台代码页。
#
# 全程跑在一个临时代码目录里（NENGLIANG_CODE_ROOT），开发机上真实的
# %LOCALAPPDATA%\NengliangReaderCode 必须纹丝不动。跑完会核对这一点。
#
# 目录名一律用 ASCII：同目录的 Test-WindowsPackage.ps1 没有 BOM，靠 pwsh 默认按 UTF-8 读才没出问题；
# 这个脚本可能被 5.1 直接运行，写中文会被当 ANSI 读成乱码。名字里带空格是故意的，
# 用来覆盖脚本自己拼参数时的引号处理（同事的用户名里就可能有空格）。
param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'

# 被测脚本一进来就把控制台切到 UTF-8，而本脚本默认按 ANSI 代码页写。两边共用同一个输出管道时，
# CI 日志（和任何重定向到文件的情况）就是两种编码混在一起，谁读都是乱码 —— 测试的全部产出就是
# 这些 PASS / FAIL 行，乱码等于没有证据。跟着切到 UTF-8。没有真实控制台时会抛，忽略即可。
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$readerScripts = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts'
$readerRoot = Join-Path ([IO.Path]::GetFullPath('artifacts')) ('Script Contract ' + [Guid]::NewGuid().ToString('N'))
$readerCodeRoot = Join-Path $readerRoot 'Code Root'
$readerRunDir = Join-Path $readerCodeRoot 'run'
$readerData = Join-Path $readerRoot 'Data Dir'
$readerOtherData = Join-Path $readerRoot 'Other Data Dir'
$readerApp = Join-Path $readerRunDir 'NengliangReader.exe'
# 记录真实代码目录是否存在，最后断言整个测试没有碰过它。
$readerDefaultCodeRoot = Join-Path $env:LOCALAPPDATA 'NengliangReaderCode'
$readerDefaultExisted = Test-Path -LiteralPath $readerDefaultCodeRoot
$readerCodeRootBefore = $env:NENGLIANG_CODE_ROOT
# 所有子进程集中登记，finally 里统一回收。
$readerChildren = @()
$readerPassed = $false

function Assert-Reader($condition, $label) {
    if (!$condition) { throw "FAIL: $label" }
    Write-Host "PASS: $label"
}

# 同事的桌面快捷方式指向 Windows PowerShell，所以这里也走 powershell.exe，
# 而不是测试自己所在的宿主 —— 免得测的是 pwsh 的行为、跑的是 5.1 的脚本。
$readerHost = if (Get-Command powershell.exe -ErrorAction SilentlyContinue) { 'powershell.exe' } else { 'pwsh' }
function Invoke-ReaderScript($name, $arguments) {
    $command = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $readerScripts $name)) + $arguments
    # 不重定向输出：一来出错时能把子进程的原话看全，二来 2>&1 会把子进程的 stderr 变成 ErrorRecord，
    # 而这里 $ErrorActionPreference 是 Stop，会把子进程的正常报错当成测试脚本自己崩了。
    & $readerHost @command | Out-Host
    return $LASTEXITCODE
}

# 只用来搭本地裸仓库的脚手架。子进程的 stderr 一律不重定向：本脚本 EAP 是 Stop，
# 一旦接住 stderr，git 的正常提示就会变成终止性错误，把测试自己打断。
function Invoke-ReaderGit($arguments) {
    & git @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw ("FAIL: git " + ($arguments -join ' ')) }
}

function Get-ReaderHead($repository) {
    return @(& git -C $repository rev-parse HEAD)[0].Trim()
}

function Start-ReaderApp($dataDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $readerApp
    $start.WorkingDirectory = $readerRunDir
    $start.UseShellExecute = $false
    $start.WindowStyle = 'Hidden'
    # 不用 ArgumentList：那是 .NET Core 才有的属性，脚本可能被 5.1（.NET Framework）加载。
    $start.Arguments = '--data "' + $dataDirectory + '"'
    $process = [Diagnostics.Process]::Start($start)
    $script:readerChildren += $process
    return $process
}

# 单实例锁被持有时打不开。这是脚本判断「还有东西在跑」的依据，也是测试观察程序状态的抓手。
#
# 锁文件不存在就直接算没被持有，绝不 OpenOrCreate：轮询正好撞上程序自己去拿锁的那一瞬间，
# 探针就把锁抢走了，程序会以为「已经在运行」然后自己退出。只探测已经存在的文件，
# 程序拿到锁之后才可能发生重叠，那时抢锁的时刻已经过去了。
function Test-ReaderLockHeld($dataDirectory) {
    $path = Join-Path $dataDirectory 'instance.lock'
    if (!(Test-Path -LiteralPath $path)) { return $false }
    try {
        $probe = [IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
        $probe.Dispose()
        return $false
    } catch { return $true }
}

function Wait-ReaderLockHeld($dataDirectory) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-ReaderLockHeld $dataDirectory) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

try {
    # 把打包产物复制到临时 run\，后续启动/停止只作用于这份副本。
    New-Item -ItemType Directory -Path $readerRunDir, $readerData, $readerOtherData -Force | Out-Null
    Copy-Item -Path (Join-Path $AppDirectory '*') -Destination $readerRunDir -Recurse -Force
    $env:NENGLIANG_CODE_ROOT = $readerCodeRoot

    # 什么都没跑的时候，「停止」应该成功返回 —— 脚本对外的承诺是「跑完这里不再有程序运行」，
    # 而不是「我这一下杀掉了一个进程」。同事连点两次图标不该看到红色的失败。
    Assert-Reader ((Invoke-ReaderScript 'stop.ps1' @()) -eq 0) 'stop 在没有实例在跑时成功返回'

    $first = Start-ReaderApp $readerData
    Assert-Reader (Wait-ReaderLockHeld $readerData) '程序启动后持有单实例锁'

    Assert-Reader ((Invoke-ReaderScript 'stop.ps1' @('--data', $readerData)) -eq 0) 'stop 的停止请求被接受'
    Assert-Reader ($first.WaitForExit(30000)) 'stop 之后程序真的退出了'
    Assert-Reader (!(Test-ReaderLockHeld $readerData)) 'stop 之后单实例锁已释放'
    Assert-Reader ((Invoke-ReaderScript 'stop.ps1' @('--data', $readerData)) -eq 0) '重复 stop 仍然成功返回'

    # 有进程占着锁却不接控制通道：脚本必须报失败，而不是让调用方以为已经停了。
    # 真机上这是「升级前那个不认识停止命令的旧版本」，这里用一个喂错数据目录的请求造出同样的情形
    # —— 锁在另一个目录上，管道名就落不到一起。
    $second = Start-ReaderApp $readerData
    Assert-Reader (Wait-ReaderLockHeld $readerData) '第二次启动也持有单实例锁'
    $readerHold = [IO.File]::Open((Join-Path $readerOtherData 'instance.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        Assert-Reader ((Invoke-ReaderScript 'stop.ps1' @('--data', $readerOtherData)) -eq 1) '有锁但不回应时 stop 报失败'
    } finally { $readerHold.Dispose() }
    Assert-Reader (!$second.HasExited) '那次失败的 stop 没有误杀健康的实例'

    Assert-Reader ((Invoke-ReaderScript 'stop.ps1' @('--data', $readerData)) -eq 0) '数据目录对上之后 stop 恢复正常'
    Assert-Reader ($second.WaitForExit(30000)) '第二个实例也正常退出'
    Assert-Reader (!(Test-ReaderLockHeld $readerData)) '第二次 stop 之后锁同样释放了'

    Assert-Reader ((Test-Path -LiteralPath $readerDefaultCodeRoot) -eq $readerDefaultExisted) '脚本没有碰开发机真实的代码目录'

    # 编译失败必须把代码退回更新前那一版。这条以前是坏的：dotnet 把报错写在 stderr 上，
    # 脚本接住输出时 PowerShell 5.1 会把它升级成终止性错误，整条更新链在编译那一步就地断掉，
    # 回滚根本没跑到 —— 代码停在最新版、run\ 还是旧版，正是 README 保证不会出现的那种错开。
    # 这里用本地裸仓库造一次必然失败的编译，断言退出码和代码位置都对得上。
    Assert-Reader ([bool](Get-Command git -ErrorAction SilentlyContinue)) 'git 在 PATH 上，可以验编译回滚'
    $updateRoot = Join-Path $readerRoot 'Update Root'
    $updateOrigin = Join-Path $updateRoot 'origin.git'
    $updateClone = Join-Path $updateRoot 'Clone'
    $updateCodeRoot = Join-Path $readerRoot 'Update Code Root'
    New-Item -ItemType Directory -Path $updateRoot -Force | Out-Null

    Invoke-ReaderGit @('init', '--bare', '--quiet', $updateOrigin)
    Invoke-ReaderGit @('-C', $updateOrigin, 'symbolic-ref', 'HEAD', 'refs/heads/main')
    Invoke-ReaderGit @('clone', '--quiet', $updateOrigin, $updateClone)
    Invoke-ReaderGit @('-C', $updateClone, 'config', 'user.email', 'script-contract@example.invalid')
    Invoke-ReaderGit @('-C', $updateClone, 'config', 'user.name', 'Script Contract')

    # 第一版指向真实存在的 SDK，第二版故意指向一个不存在的 —— 这样失败一定来自编译本身，
    # 而不是「工程文件找不到」那条提前返回，回滚路径才真的被走到。两版都不需要联网 restore。
    $updateProject = Join-Path $updateClone 'src\Reader.Desktop\Reader.Desktop.csproj'
    New-Item -ItemType Directory -Path (Split-Path -Parent $updateProject) -Force | Out-Null
    Set-Content -LiteralPath $updateProject -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
    Invoke-ReaderGit @('-C', $updateClone, 'add', '-A')
    Invoke-ReaderGit @('-C', $updateClone, 'commit', '--quiet', '-m', 'initial')
    $updateBefore = Get-ReaderHead $updateClone

    Set-Content -LiteralPath $updateProject -Value '<Project Sdk="NengliangReader.Broken.Sdk"></Project>'
    Invoke-ReaderGit @('-C', $updateClone, 'add', '-A')
    Invoke-ReaderGit @('-C', $updateClone, 'commit', '--quiet', '-m', 'break the build')
    Invoke-ReaderGit @('-C', $updateClone, 'push', '--quiet', 'origin', 'main')
    # 把工作区退回第一版：于是 update 会走一遍完整的「有更新可拉 → 编译 → 失败 → 回滚」。
    Invoke-ReaderGit @('-C', $updateClone, 'reset', '--hard', '--quiet', $updateBefore)

    $updateExit = Invoke-ReaderScript 'update.ps1' @('-RepoRoot', $updateClone, '-CodeRoot', $updateCodeRoot, '-Branch', 'main', '-SkipStart')
    Assert-Reader ($updateExit -eq 1) '编译失败时 update 报失败'
    Assert-Reader ((Get-ReaderHead $updateClone) -eq $updateBefore) '编译失败后代码退回更新前那一版'
    Assert-Reader (!(Test-Path -LiteralPath (Join-Path $updateCodeRoot 'run'))) '编译失败没有留下半个 run 目录'
    # 全部断言通过才清理证据目录；失败时保留现场方便排查。
    $readerPassed = $true
} finally {
    foreach ($child in $readerChildren) {
        if (!$child.HasExited) { $child.Kill($true); $child.WaitForExit() }
        $child.Dispose()
    }
    $env:NENGLIANG_CODE_ROOT = $readerCodeRootBefore
    if ($readerPassed) {
        # 这次把打包出来的程序整个复制了一份，成功就擦掉，不给 CI 攒垃圾。
        Remove-Item -LiteralPath $readerRoot -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "Script contract failed; evidence kept at $readerRoot"
    }
}
Write-Host 'Script start/stop contract passed.'

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

$readerScripts = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts'
$readerRoot = Join-Path ([IO.Path]::GetFullPath('artifacts')) ('Script Contract ' + [Guid]::NewGuid().ToString('N'))
$readerCodeRoot = Join-Path $readerRoot 'Code Root'
$readerRunDir = Join-Path $readerCodeRoot 'run'
$readerData = Join-Path $readerRoot 'Data Dir'
$readerOtherData = Join-Path $readerRoot 'Other Data Dir'
$readerApp = Join-Path $readerRunDir 'NengliangReader.exe'
$readerDefaultCodeRoot = Join-Path $env:LOCALAPPDATA 'NengliangReaderCode'
$readerDefaultExisted = Test-Path -LiteralPath $readerDefaultCodeRoot
$readerCodeRootBefore = $env:NENGLIANG_CODE_ROOT
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

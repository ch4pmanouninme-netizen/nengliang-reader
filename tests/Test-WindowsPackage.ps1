param([Parameter(Mandatory)][string]$Package)
# Windows 安装包契约回归：解压后只能有一个带版本号的顶层目录，目录内为「良子小窗.exe」、
# README.md 和 app/；用带空格的临时路径验证 Unicode 路径、参数转发、冒烟标记和 --server。
$ErrorActionPreference = 'Stop'
# 证据目录放在 artifacts 下，路径故意带空格，覆盖解压与参数拼接时的引号处理。
$readerTestRoot = Join-Path ([IO.Path]::GetFullPath('artifacts')) ('Windows 打包测试 ' + [Guid]::NewGuid().ToString('N'))
$readerUnpacked = Join-Path $readerTestRoot '解压 目录'
Expand-Archive -LiteralPath $Package -DestinationPath $readerUnpacked
# ZIP 必须自带唯一的版本化顶层目录，避免解压后文件散落。
$readerRootItems = @(Get-ChildItem -LiteralPath $readerUnpacked)
if ($readerRootItems.Count -ne 1 -or !$readerRootItems[0].PSIsContainer) { throw 'Expected a single versioned package folder.' }
$readerPackage = $readerRootItems[0].FullName
# 顶层目录名包含版本号和 win-x64 运行时标识。
if ($readerRootItems[0].Name -notmatch '^NengliangReader-v.+?-win-x64$') { throw "Unexpected package folder: $($readerRootItems[0].Name)" }
# 包内只允许启动器、说明和 app 目录。
$readerEntries = @(Get-ChildItem -LiteralPath $readerPackage | Select-Object -ExpandProperty Name)
if (@(Compare-Object @('良子小窗.exe', 'README.md', 'app') $readerEntries).Count) { throw 'Unexpected package folder entries.' }
# 以隐藏窗口启动，模拟双击 exe；参数逐个加入 ArgumentList 以保留空格。
$readerStart = [Diagnostics.ProcessStartInfo]::new()
$readerStart.FileName = Join-Path $readerPackage '良子小窗.exe'
$readerStart.WorkingDirectory = $readerTestRoot
$readerStart.UseShellExecute = $false
$readerStart.WindowStyle = 'Hidden'
foreach ($readerArgument in @('--smoke-test', '--data', '测试 数据')) { $readerStart.ArgumentList.Add($readerArgument) }
$readerChild = [Diagnostics.Process]::Start($readerStart)
try {
    if (!$readerChild.WaitForExit(60000)) { throw 'Packaged launcher smoke timed out.' }
    if ($readerChild.ExitCode -ne 0) { throw "Packaged smoke failed; see $readerTestRoot/测试 数据/smoke-test.error" }
    # 主程序冒烟写三个标记，分别覆盖桌面外壳、聊天外壳和书库管理。
    foreach ($readerMarker in @('smoke-test.ok', 'shell-chat-smoke.ok', 'book-library-smoke.ok')) {
        if (!(Test-Path -LiteralPath (Join-Path $readerTestRoot "测试 数据/$readerMarker"))) { throw "Missing $readerMarker" }
    }
} finally {
    # 超时或失败都要杀掉子进程，避免残留进程占住数据目录。
    if (!$readerChild.HasExited) { $readerChild.Kill($true); $readerChild.WaitForExit() }
    $readerChild.Dispose()
}
# --server 必须被启动器转发，独立服务以「聊天服务」窗口标题出现。
$readerStart.ArgumentList.Clear()
$readerStart.ArgumentList.Add('--server')
$readerChild = [Diagnostics.Process]::Start($readerStart)
try {
    # 最多等 15 秒；窗口标题出现前进程退出视为失败。
    $readerDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $readerChild.Refresh()
        if ($readerChild.HasExited) { throw 'Independent server exited before opening its window.' }
    } while (!$readerChild.MainWindowTitle.Contains('聊天服务') -and [DateTime]::UtcNow -lt $readerDeadline)
    if (!$readerChild.MainWindowTitle.Contains('聊天服务')) { throw 'Launcher did not forward --server.' }
    # 正常关闭窗口，验证服务能优雅退出。
    if (!$readerChild.CloseMainWindow() -or !$readerChild.WaitForExit(10000)) { throw 'Server window did not close.' }
} finally {
    if (!$readerChild.HasExited) { $readerChild.Kill($true); $readerChild.WaitForExit() }
    $readerChild.Dispose()
}
Write-Host "Package layout, Unicode paths, relative arguments, desktop/chat smoke and --server passed. Evidence: $readerTestRoot"

# 上面验的是打包产物本身，这里接着验团队部署用的启动 / 停止脚本 —— 它把这份产物复制到自己的
# 临时代码目录里再跑一遍，所以顺带证明了「脚本装的这一份」也能起来、也能被优雅停掉。
& (Join-Path $PSScriptRoot 'Test-ReaderScripts.ps1') -AppDirectory (Join-Path $readerPackage 'app')
Write-Host "Start/stop script contract passed. Evidence: $readerTestRoot"

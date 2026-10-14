param([Parameter(Mandatory)][string]$Package)
$ErrorActionPreference = 'Stop'
$readerTestRoot = Join-Path ([IO.Path]::GetFullPath('artifacts')) ('Windows 打包测试 ' + [Guid]::NewGuid().ToString('N'))
$readerUnpacked = Join-Path $readerTestRoot '解压 目录'
Expand-Archive -LiteralPath $Package -DestinationPath $readerUnpacked
$readerEntries = @(Get-ChildItem -LiteralPath $readerUnpacked | Select-Object -ExpandProperty Name)
if (@(Compare-Object @('良子小窗.exe', 'README.md', 'app') $readerEntries).Count) { throw 'Unexpected package root entries.' }
$readerStart = [Diagnostics.ProcessStartInfo]::new()
$readerStart.FileName = Join-Path $readerUnpacked '良子小窗.exe'
$readerStart.WorkingDirectory = $readerTestRoot
$readerStart.UseShellExecute = $false
$readerStart.WindowStyle = 'Hidden'
foreach ($readerArgument in @('--smoke-test', '--data', '测试 数据')) { $readerStart.ArgumentList.Add($readerArgument) }
$readerChild = [Diagnostics.Process]::Start($readerStart)
try {
    if (!$readerChild.WaitForExit(60000)) { throw 'Packaged launcher smoke timed out.' }
    if ($readerChild.ExitCode -ne 0) { throw "Packaged smoke failed; see $readerTestRoot/测试 数据/smoke-test.error" }
    foreach ($readerMarker in @('smoke-test.ok', 'shell-chat-smoke.ok', 'book-library-smoke.ok')) {
        if (!(Test-Path -LiteralPath (Join-Path $readerTestRoot "测试 数据/$readerMarker"))) { throw "Missing $readerMarker" }
    }
} finally {
    if (!$readerChild.HasExited) { $readerChild.Kill($true); $readerChild.WaitForExit() }
    $readerChild.Dispose()
}
$readerStart.ArgumentList.Clear()
$readerStart.ArgumentList.Add('--server')
$readerChild = [Diagnostics.Process]::Start($readerStart)
try {
    $readerDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $readerChild.Refresh()
        if ($readerChild.HasExited) { throw 'Independent server exited before opening its window.' }
    } while (!$readerChild.MainWindowTitle.Contains('聊天服务') -and [DateTime]::UtcNow -lt $readerDeadline)
    if (!$readerChild.MainWindowTitle.Contains('聊天服务')) { throw 'Launcher did not forward --server.' }
    if (!$readerChild.CloseMainWindow() -or !$readerChild.WaitForExit(10000)) { throw 'Server window did not close.' }
} finally {
    if (!$readerChild.HasExited) { $readerChild.Kill($true); $readerChild.WaitForExit() }
    $readerChild.Dispose()
}
Write-Host "Package layout, Unicode paths, relative arguments, desktop/chat smoke and --server passed. Evidence: $readerTestRoot"

# 上面验的是打包产物本身，这里接着验团队部署用的启动 / 停止脚本 —— 它把这份产物复制到自己的
# 临时代码目录里再跑一遍，所以顺带证明了「脚本装的这一份」也能起来、也能被优雅停掉。
& (Join-Path $PSScriptRoot 'Test-ReaderScripts.ps1') -AppDirectory (Join-Path $readerUnpacked 'app')
Write-Host "Start/stop script contract passed. Evidence: $readerTestRoot"

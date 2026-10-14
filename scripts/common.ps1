# 良子小窗的启动 / 停止 / 更新的公共部分。不要直接运行这个文件。
#
# 目录布局是一份契约，改动前先读 scripts/README.md：
#   %LOCALAPPDATA%\NengliangReaderCode\
#   ├── run\        当前版本。这个路径永远不变 —— Windows 防火墙的入站规则按进程映像的完整
#   │               路径匹配，只有路径固定，规则才不用每换一版就重新授权。
#   ├── staging\    新版本先整个发布到这里，确认编译成功后才切换过去。
#   ├── previous\   上一版，仅用于回退。
#   └── launch.json 上次启动用的参数。停止和更新沿用同一份，避免 start 和 stop 用的参数不一致
#                   而出现「明明在跑却说没在跑」。
#
# 数据目录不在这里，仍由程序自己决定（默认 %LOCALAPPDATA%\NengliangReader）。
# 代码和数据分开，是为了让 --data 指向任意目录的冒烟测试 / 多份书库不会顺手牵出代码目录。

# git 的输出永远是 UTF-8，而 PowerShell 默认按控制台代码页（中文 Windows 上是 GBK）去解码子进程
# 的输出，两边混在同一个窗口里，提交信息里的中文就成乱码了。把这个控制台整个切到 UTF-8。
# 赋值会顺带改掉控制台代码页；没有真实控制台时（输出被重定向到文件或管道）会抛异常，忽略即可。
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$ErrorActionPreference = 'Stop'

$ReaderScriptDir = $PSScriptRoot
$ReaderAppName = 'NengliangReader.exe'
# 默认装在 %LOCALAPPDATA% 下。NENGLIANG_CODE_ROOT 可以指到别处：回归测试用它把整套流程跑在临时目录里，
# 团队也可以用它并行养两条通道而互不干扰。三个入口脚本都有 -CodeRoot 参数来设置它。
$ReaderCodeRoot = if ($env:NENGLIANG_CODE_ROOT) { $env:NENGLIANG_CODE_ROOT } else { Join-Path $env:LOCALAPPDATA 'NengliangReaderCode' }
$ReaderRunDir = Join-Path $ReaderCodeRoot 'run'
$ReaderStagingDir = Join-Path $ReaderCodeRoot 'staging'
$ReaderPreviousDir = Join-Path $ReaderCodeRoot 'previous'
$ReaderLaunchState = Join-Path $ReaderCodeRoot 'launch.json'
$ReaderExe = Join-Path $ReaderRunDir $ReaderAppName

function Write-Step($message) { Write-Host ''; Write-Host ('-- ' + $message) -ForegroundColor Cyan }
function Write-Note($message) { Write-Host ('   ' + $message) }
function Fail($message) { Write-Host ''; Write-Host ('失败：' + $message) -ForegroundColor Red; exit 1 }

function Assert-Command($name, $hint) {
    if (!(Get-Command $name -ErrorAction SilentlyContinue)) { Fail ($name + ' 没有安装。' + $hint) }
}

# 只认 run\ 目录里的那一份程序。同事机器上可能还留着旧的发行包，也可能有人从别处跑了一份，
# 按映像路径过滤，避免误杀。
function Get-ReaderProcesses([switch]$Server) {
    $prefix = $ReaderRunDir.TrimEnd('\') + '\'
    $running = @(Get-CimInstance Win32_Process -Filter "Name='$ReaderAppName'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) })
    # 聊天服务是主程序用同一个 exe 拉起来的 --server 子进程，用命令行区分。
    if ($Server) { return @($running | Where-Object { $_.CommandLine -like '*--server*' }) }
    return @($running | Where-Object { $_.CommandLine -notlike '*--server*' })
}

# Start-Process 会把参数拼成一整行再交给 Windows，含空格的路径必须自己加引号
# （同事的用户名里就可能有空格）。
function Quote-Arguments($arguments) {
    return @($arguments | Where-Object { $_ -ne $null -and $_ -ne '' } | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    })
}

function Save-LaunchArguments($arguments) {
    New-Item -ItemType Directory -Path $ReaderCodeRoot -Force | Out-Null
    Set-Content -LiteralPath $ReaderLaunchState -Value (@{ arguments = @($arguments) } | ConvertTo-Json -Compress) -Encoding UTF8
}

function Get-LaunchArguments {
    if (!(Test-Path -LiteralPath $ReaderLaunchState)) { return @() }
    try { return @((Get-Content -LiteralPath $ReaderLaunchState -Raw | ConvertFrom-Json).arguments) } catch { return @() }
}

# 停止 = 「这台机器上不再有 run\ 里的程序在跑」。主程序走控制通道优雅退出（它会先保存进度），
# 聊天服务子进程不走控制通道，只能在这里收掉。
function Stop-Reader($arguments) {
    if ((Test-Path -LiteralPath $ReaderExe) -and @(Get-ReaderProcesses).Count -gt 0) {
        Write-Note '正在请求退出（会先保存进度）……'
        $stop = Start-Process -FilePath $ReaderExe -ArgumentList (Quote-Arguments (@('--stop') + @($arguments))) -PassThru -Wait -WindowStyle Hidden
        switch ($stop.ExitCode) {
            0 { Write-Note '主程序已保存并退出。' }
            1 { Write-Note '主程序当前没有在运行。' }
            2 { Fail '程序正忙（保存 / 导入 / 有对话框没关），这次没有退出。请先处理完再试一次。' }
            3 { Fail '有个旧版本还在运行，它不认识停止命令。请在右下角托盘里的良子小窗图标上右键，选「退出」。' }
            default { Fail ('停止请求没有成功，退出码 ' + $stop.ExitCode + '。') }
        }
        # --stop 返回的只是「已受理」，主程序还要写完盘才真正退出。等它走干净再收尾，
        # 否则会把正在保存的进程强杀掉。
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and @(Get-ReaderProcesses).Count -gt 0) { Start-Sleep -Milliseconds 250 }
        if (@(Get-ReaderProcesses).Count -gt 0) { Fail '主程序在 30 秒内没有退出，已中止。请稍后再试，或用任务管理器结束它。' }
    } else {
        Write-Note '主程序当前没有在运行。'
    }
    $servers = @(Get-ReaderProcesses -Server)
    if ($servers.Count -gt 0) {
        Write-Note ('正在关闭聊天服务（' + $servers.Count + ' 个）……')
        foreach ($server in $servers) { Stop-Process -Id $server.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 500
    }
}

# 跑一个原生命令（git / dotnet），把 stdout 和 stderr 一起接住，返回 @{ Output; Code }。
#
# 这里必须绕一道弯，不能直接写 `$output = @(& $file @args 2>&1)`：只要子进程往 stderr 写了一行，
# PowerShell 5.1 就会在「输出被接住」的情况下把它变成一个错误记录，而本文件里
# $ErrorActionPreference = 'Stop' 会让这个错误记录终止整个脚本。后果实测过：机器上没有
# .NET SDK 10 时，「更新」在编译那一步直接中断，既没打印下面那行人话，也没把代码退回
# 更新前那一版，代码和 run\ 就此错开 —— 正是 scripts/README.md 保证不会发生的那种情况。
# 捕获期间把 EAP 压回 Continue，输出和退出码照旧准确，判断交给调用方。
function Invoke-Captured($file, $arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # 混合流里的 stderr 行是错误记录，直接拼字符串会打出
        # 「System.Management.Automation.RemoteException」这种占位符，把原始报错盖掉。
        $output = @(& $file @arguments 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message }
            elseif ($_ -is [System.Exception]) { $_.Message }
            else { $_ }
        })
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    return [pscustomobject]@{ Output = $output; Code = $code }
}

# 发布成功返回 $true；失败只说明原因并返回 $false，好让 update 有机会先把代码退回去。
function Invoke-Publish($repoRoot, $destination) {
    Assert-Command 'dotnet' '请先装 .NET SDK 10（https://dotnet.microsoft.com/download/dotnet/10.0），注意是 SDK 不是 Runtime。'
    $project = Join-Path $repoRoot 'src\Reader.Desktop\Reader.Desktop.csproj'
    if (!(Test-Path -LiteralPath $project)) {
        Write-Note ('在 ' + $repoRoot + ' 里找不到 src\Reader.Desktop\Reader.Desktop.csproj。')
        return $false
    }
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    Write-Note '正在编译（第一次要下载依赖，可能要几分钟，之后会快很多）……'
    # dotnet 的输出必须先接住再经 Write-Host 转出去。直接 & dotnet publish 的话，它的每一行都会
    # 变成这个函数的「返回值」，调用方拿到的是一个非空数组 + $false —— 非空数组恒为真，于是
    # 编译失败会被当成成功，把残缺的 staging 切换上去，毁掉正在用的版本。
    # 用 Invoke-Captured 而不是就地 2>&1，是为了让 stderr 上的报错（比如这台机器没装 SDK）也
    # 老老实实走到下面那行 return $false，而不是把脚本打断在编译这一步。
    $publish = Invoke-Captured 'dotnet' @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-m:1', '-o', $destination)
    $publish.Output | ForEach-Object { Write-Host ('   ' + $_) }
    if ($publish.Code -ne 0) { Write-Note 'dotnet publish 失败了，上面是它的原始输出。'; return $false }
    if (!(Test-Path -LiteralPath (Join-Path $destination $ReaderAppName))) {
        Write-Note ('编译结果里没有 ' + $ReaderAppName + '。')
        return $false
    }
    return $true
}

# 先让新版本整个躺在 staging\，确认好了才切换。切换是两次目录改名，不会留下半个 run\。
# 调用前必须确保进程都停了：run\ 里的 exe 被占用时改名会失败。
function Switch-ToStaging {
    if (!(Test-Path -LiteralPath $ReaderStagingDir)) { Fail '没有可切换的新版本。' }
    if (Test-Path -LiteralPath $ReaderPreviousDir) { Remove-Item -LiteralPath $ReaderPreviousDir -Recurse -Force }
    if (Test-Path -LiteralPath $ReaderRunDir) {
        try { Move-Item -LiteralPath $ReaderRunDir -Destination $ReaderPreviousDir }
        catch { Fail ('搬走旧版本失败，可能还有进程占着 ' + $ReaderRunDir + '。请先停止再试。' + $_.Exception.Message) }
    }
    try { Move-Item -LiteralPath $ReaderStagingDir -Destination $ReaderRunDir }
    catch {
        if ((Test-Path -LiteralPath $ReaderPreviousDir) -and !(Test-Path -LiteralPath $ReaderRunDir)) {
            Move-Item -LiteralPath $ReaderPreviousDir -Destination $ReaderRunDir
            Fail ('切换新版本失败，原来的版本已经放回去了。' + $_.Exception.Message)
        }
        Fail ('切换新版本失败。' + $_.Exception.Message)
    }
}

function Start-Reader($arguments) {
    if (!(Test-Path -LiteralPath $ReaderExe)) { Fail ('还没有可运行的版本。请先点桌面上的「启动」，它会在 ' + $ReaderRunDir + ' 里编译一份。') }
    # 没有参数时不能传 -ArgumentList：Start-Process 拒绝空数组（参数校验错误）。
    $start = @{ FilePath = $ReaderExe; WorkingDirectory = $ReaderRunDir }
    $quoted = @(Quote-Arguments $arguments)
    if ($quoted.Count -gt 0) { $start.ArgumentList = $quoted }
    Start-Process @start | Out-Null
}

function Resolve-RepoRoot($explicit) {
    $root = if ($explicit) { $explicit } else { Split-Path -Parent $ReaderScriptDir }
    return (Resolve-Path -LiteralPath $root).Path
}

# start 和 stop 不传参数时用同一份「上次启动用的参数」，见文件开头对 launch.json 的说明。
function Resolve-Arguments($appArguments) {
    $arguments = @($appArguments | Where-Object { $_ -ne $null -and $_ -ne '' })
    if ($arguments.Count -eq 0) { $arguments = @(Get-LaunchArguments | Where-Object { $_ -ne $null -and $_ -ne '' }) }
    return $arguments
}

# 启动良子小窗。双击桌面上的「启动」图标即可，不需要联网。
#
# 参数原样转交给程序（--data / --server / --smoke-test 都行），并记下来给 stop 和 update 用。
#
#   -CodeRoot   代码目录，默认 %LOCALAPPDATA%\NengliangReaderCode
[CmdletBinding()]
param(
    [string]$CodeRoot,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArguments)

if ($CodeRoot) { $env:NENGLIANG_CODE_ROOT = $CodeRoot }
. (Join-Path $PSScriptRoot 'common.ps1')

# 先把本次参数记到 launch.json，stop/update 才能按同一数据目录操作。
$arguments = Resolve-Arguments $AppArguments
Save-LaunchArguments $arguments

# run\ 不存在才编译；路径保持稳定，Windows 防火墙规则无需随版本重新授权。
if (!(Test-Path -LiteralPath $ReaderExe)) {
    Write-Step '第一次运行：先准备本机版本'
    Write-Note ('代码会装到 ' + $ReaderRunDir + '，这个路径之后不会再变。')
    if (!(Invoke-Publish (Resolve-RepoRoot $null) $ReaderStagingDir)) { Fail '编译失败，没有改动任何东西。' }
    Switch-ToStaging
    Write-Note '准备完成。'
}

Write-Step '正在启动良子小窗'
Start-Reader $arguments
Write-Note '已启动，这个窗口可以关掉了。'

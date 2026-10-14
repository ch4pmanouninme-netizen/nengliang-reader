# 停止良子小窗。双击桌面上的「停止」图标即可。
#
# 先请求程序自己退出（它会先保存进度），只有等不到回应才强杀。不带参数时沿用 start 记下的参数，
# 所以只要参数不是临时改的，直接双击就行。
#
#   -CodeRoot   代码目录，默认 %LOCALAPPDATA%\NengliangReaderCode
[CmdletBinding()]
param(
    [string]$CodeRoot,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArguments)

if ($CodeRoot) { $env:NENGLIANG_CODE_ROOT = $CodeRoot }
. (Join-Path $PSScriptRoot 'common.ps1')

Write-Step '正在停止良子小窗'
Stop-Reader (Resolve-Arguments $AppArguments)
Write-Host ''
Write-Host '已经全部停止。' -ForegroundColor Green

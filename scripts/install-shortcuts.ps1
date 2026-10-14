# 在桌面生成「启动 / 停止 / 更新」三个快捷方式。装一次就行，代码换版本不影响它们。
#
# 为什么快捷方式直接指向 .ps1 而不是套一层 .bat：
#   更新会把仓库里的文件原地重写（git reset --hard），而 cmd 是按行、边跑边读 .bat 的 ——
#   把正在执行的 .bat 换掉，它接下来会从错位的字节位置继续读，执行到半截的行。
#   PowerShell 则是先把整个脚本读进内存再执行，跑起来之后文件怎么变都无所谓。
#
#   -Destination <目录>   快捷方式生成到哪里，默认桌面（测试时用得上）
#   -RepoRoot    <目录>   代码目录，默认 scripts 的上一级
[CmdletBinding()]
param([string]$Destination, [string]$RepoRoot)

. (Join-Path $PSScriptRoot 'common.ps1')

$repo = Resolve-RepoRoot $RepoRoot
if (!$Destination) { $Destination = [Environment]::GetFolderPath('Desktop') }
if (!(Test-Path -LiteralPath $Destination)) { New-Item -ItemType Directory -Path $Destination -Force | Out-Null }

$PowerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$shell = New-Object -ComObject WScript.Shell

function New-ReaderShortcut($name, $script, $keepOpen, $description) {
    $path = Join-Path $Destination ($name + '.lnk')
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $PowerShellPath
    # -NoExit 让窗口在结束后留着：失败原因要能看见，不然一闪而过就什么都归我们远程猜。
    $arguments = '-NoProfile -ExecutionPolicy Bypass '
    if ($keepOpen) { $arguments += '-NoExit ' }
    $link.Arguments = $arguments + '-File "' + (Join-Path $ReaderScriptDir $script) + '"'
    $link.WorkingDirectory = $repo
    $link.Description = $description
    # 图标借用程序自己的，编译过就用得上；还没有的话保持系统默认。
    if (Test-Path -LiteralPath $ReaderExe) { $link.IconLocation = $ReaderExe + ',0' }
    $link.Save()
    Write-Note ($path)
}

Write-Step '正在生成桌面快捷方式'
New-ReaderShortcut '良子小窗-启动' 'start.ps1'  $false '启动良子小窗'
New-ReaderShortcut '良子小窗-停止' 'stop.ps1'   $true  '停止良子小窗'
New-ReaderShortcut '良子小窗-更新' 'update.ps1' $true  '拉取最新代码并重启良子小窗'

Write-Host ''
Write-Note '桌面上的这三个图标以后就够用了。代码换版本不影响它们，换过代码目录才需要重新跑一次这个安装。'
Write-Note ('「更新」失败时窗口会留着，把里面的报错整段发出来就能定位问题。')

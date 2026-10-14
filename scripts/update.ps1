# 更新良子小窗：停掉 → 从 GitHub 拉最新代码 → 编译 → 切换 → 启动。
#
# 任何一步失败都不会破坏正在用的版本：拉取失败就地停下，编译失败会把代码退回更新前那一版，
# 切换失败会把旧版本搬回去。所以更新中途断网、断电，最坏结果也只是「没更新成功」。
#
#   -Force      覆盖本机对仓库里受版本管理文件的改动（默认遇到改动就停下问你）
#   -Branch     指定分支，默认取当前分支跟踪的远端分支，再退回 main
#   -SkipStart  更新完不自动启动
#   -RepoRoot   指定代码目录，默认是 scripts 的上一级
#   -CodeRoot   程序装到哪，默认 %LOCALAPPDATA%\NengliangReaderCode
[CmdletBinding()]
param(
    [string]$Branch,
    [string]$RepoRoot,
    [string]$CodeRoot,
    [switch]$Force,
    [switch]$SkipStart,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArguments)

if ($CodeRoot) { $env:NENGLIANG_CODE_ROOT = $CodeRoot }
. (Join-Path $PSScriptRoot 'common.ps1')

$repo = Resolve-RepoRoot $RepoRoot
$arguments = Resolve-Arguments $AppArguments

Assert-Command 'git' '请先装 Git for Windows（https://git-scm.com/download/win）。'
$inside = @(& git -C $repo rev-parse --is-inside-work-tree 2>$null)
if ($LASTEXITCODE -ne 0 -or $inside.Count -eq 0 -or $inside[0].Trim() -ne 'true') {
    Fail ($repo + ' 不是 git 仓库。请先 git clone 拿到代码，然后双击仓库里的 scripts\install.bat 生成快捷方式。')
}

Write-Step '先停止正在运行的程序'
Stop-Reader $arguments

if (!$Branch) {
    $tracking = @(& git -C $repo rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null)
    if ($LASTEXITCODE -eq 0 -and $tracking.Count -gt 0 -and $tracking[0]) { $Branch = $tracking[0].Trim() -replace '^origin/', '' }
    else { $Branch = 'main' }
}

Write-Step ('从 GitHub 拉取最新代码（分支 ' + $Branch + '）')
& git -C $repo fetch origin
if ($LASTEXITCODE -ne 0) { Fail '拉取失败：连不上 GitHub，或者当前账号没有权限。程序仍然可以照常启动，只是没有更新。' }

$changes = @(& git -C $repo log --oneline --no-decorate ('HEAD..origin/' + $Branch) 2>$null)
if ($LASTEXITCODE -ne 0) { Fail ('远端没有 ' + $Branch + ' 这个分支。') }
if ($changes.Count -eq 0) {
    Write-Note '已经是最新版本，不需要更新。'
    if (!$SkipStart) { Write-Step '启动良子小窗'; Start-Reader $arguments }
    exit 0
}

Write-Note ('有 ' + $changes.Count + ' 个新提交：')
$changes | Select-Object -First 20 | ForEach-Object { Write-Note ('  ' + $_) }
if ($changes.Count -gt 20) { Write-Note ('  …… 还有 ' + ($changes.Count - 20) + ' 个') }

if (!$Force) {
    $dirty = @(& git -C $repo status --porcelain --untracked-files=no)
    if ($dirty.Count -gt 0) {
        Write-Note '本机改过这些受版本管理的文件：'
        $dirty | ForEach-Object { Write-Note ('  ' + $_) }
        Fail '更新会把它们覆盖掉，所以停下来先问你。确认不要了，就带上 -Force 参数再跑一次：update.ps1 -Force'
    }
}

$before = @(& git -C $repo rev-parse HEAD)[0].Trim()
Write-Step '切到最新代码'
& git -C $repo reset --hard ('origin/' + $Branch)
if ($LASTEXITCODE -ne 0) { Fail '切到最新代码失败，没有做任何改动。' }

Write-Step '编译新版本'
if (!(Invoke-Publish $repo $ReaderStagingDir)) {
    & git -C $repo reset --hard $before | Out-Null
    Fail '编译失败，代码已经退回更新前那一版。现在运行的还是原来的程序，可以直接启动。'
}

Write-Step '切换到新版本'
Switch-ToStaging
Write-Note ('旧版本留在 ' + $ReaderPreviousDir + '，要回退就把 run 删掉、把 previous 改名为 run。')

if ($SkipStart) { Write-Host ''; Write-Host '更新完成，没有自动启动。' -ForegroundColor Green; exit 0 }

Write-Step '启动新版本'
Start-Reader $arguments
Write-Host ''
Write-Host '更新完成，已经是最新代码。' -ForegroundColor Green

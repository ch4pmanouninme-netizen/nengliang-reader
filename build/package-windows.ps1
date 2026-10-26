param([string]$Output = 'artifacts')
# Windows 打包：发布自包含 win-x64 到 artifacts/NengliangReader-v<版本>-win-x64/app，
# 生成原生启动器「良子小窗.exe」，复制文档后打成带顶层目录的 zip。
#
#   -Output  产物根目录，默认 artifacts。
$ErrorActionPreference = 'Stop'
$readerRoot = Split-Path -Parent $PSScriptRoot
Push-Location $readerRoot
try {
    [xml]$project = Get-Content Directory.Build.props
    # 版本号是目录名和 zip 文件名的唯一来源。
    $version = $project.Project.PropertyGroup.Version
    $packageName = "NengliangReader-v$version-win-x64"
    # Build in a versioned folder so the ZIP extracts to a single directory.
    $readerPackage = Join-Path $Output $packageName
    # 重复打包前清掉旧目录，避免旧文件混进新包。
    if (Test-Path -LiteralPath $readerPackage) { Remove-Item -LiteralPath $readerPackage -Recurse -Force }
    $readerDestination = Join-Path $readerPackage 'app'
    dotnet publish src/Reader.Desktop/Reader.Desktop.csproj -c Release -r win-x64 --self-contained true -m:1 -o $readerDestination
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    # 原生启动器由 build/WindowsLauncher 生成，参数会原样转给 app/ 下的 dll。
    dotnet run --project build/WindowsLauncher -c Release -- $readerPackage
    if ($LASTEXITCODE -ne 0) { throw 'Windows launcher generation failed.' }
    Copy-Item README.md,LICENSE,CHANGELOG.md,CONTRIBUTING.md -Destination $readerDestination
    Copy-Item docs -Destination $readerDestination -Recurse -Force
    Copy-Item build/README-windows.md -Destination (Join-Path $readerPackage 'README.md')
    $zip = Join-Path $Output ($packageName + '.zip')
    # 先删旧 zip，CreateFromDirectory 不会覆盖已有归档。
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # includeBaseDirectory: true —— 解压后保留 NengliangReader-v<版本>-win-x64 顶层文件夹。
    [IO.Compression.ZipFile]::CreateFromDirectory($readerPackage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host "Package: $zip"
} finally { Pop-Location }

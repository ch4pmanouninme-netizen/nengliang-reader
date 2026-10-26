param([string]$Output = 'artifacts')
$ErrorActionPreference = 'Stop'
$readerRoot = Split-Path -Parent $PSScriptRoot
Push-Location $readerRoot
try {
    [xml]$project = Get-Content Directory.Build.props
    $version = $project.Project.PropertyGroup.Version
    $packageName = "NengliangReader-v$version-win-x64"
    # Build in a versioned folder so the ZIP extracts to a single directory.
    $readerPackage = Join-Path $Output $packageName
    if (Test-Path -LiteralPath $readerPackage) { Remove-Item -LiteralPath $readerPackage -Recurse -Force }
    $readerDestination = Join-Path $readerPackage 'app'
    dotnet publish src/Reader.Desktop/Reader.Desktop.csproj -c Release -r win-x64 --self-contained true -m:1 -o $readerDestination
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    dotnet run --project build/WindowsLauncher -c Release -- $readerPackage
    if ($LASTEXITCODE -ne 0) { throw 'Windows launcher generation failed.' }
    Copy-Item README.md,LICENSE,CHANGELOG.md,CONTRIBUTING.md -Destination $readerDestination
    Copy-Item docs -Destination $readerDestination -Recurse -Force
    Copy-Item build/README-windows.md -Destination (Join-Path $readerPackage 'README.md')
    $zip = Join-Path $Output ($packageName + '.zip')
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # includeBaseDirectory: true —— 解压后保留 NengliangReader-v<版本>-win-x64 顶层文件夹。
    [IO.Compression.ZipFile]::CreateFromDirectory($readerPackage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host "Package: $zip"
} finally { Pop-Location }

param([string]$Output = 'artifacts')
$ErrorActionPreference = 'Stop'
$readerRoot = Split-Path -Parent $PSScriptRoot
Push-Location $readerRoot
try {
    $readerDestination = Join-Path $Output 'win-x64'
    dotnet publish src/Reader.Desktop/Reader.Desktop.csproj -c Release -r win-x64 --self-contained true -o $readerDestination
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Copy-Item README.md,LICENSE,CHANGELOG.md,CONTRIBUTING.md -Destination $readerDestination
    Copy-Item docs -Destination $readerDestination -Recurse -Force
    Set-Content -Path (Join-Path $readerDestination 'Start-ChatServer.cmd') -Encoding ascii -Value '@start "" "%~dp0NengliangReader.exe" --server'
    Compress-Archive -Path "$readerDestination/*" -DestinationPath (Join-Path $Output 'NengliangReader-win-x64.zip') -Force
} finally { Pop-Location }

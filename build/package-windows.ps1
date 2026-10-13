param([string]$Output = 'artifacts')
$ErrorActionPreference = 'Stop'
$readerRoot = Split-Path -Parent $PSScriptRoot
Push-Location $readerRoot
try {
    # Build in a new directory so files from an older flat package cannot leak in.
    $readerPackage = Join-Path $Output ('win-x64-' + [Guid]::NewGuid().ToString('N'))
    $readerDestination = Join-Path $readerPackage 'app'
    dotnet publish src/Reader.Desktop/Reader.Desktop.csproj -c Release -r win-x64 --self-contained true -m:1 -o $readerDestination
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    dotnet run --project build/WindowsLauncher -c Release -- $readerPackage
    if ($LASTEXITCODE -ne 0) { throw 'Windows launcher generation failed.' }
    Copy-Item README.md,LICENSE,CHANGELOG.md,CONTRIBUTING.md -Destination $readerDestination
    Copy-Item docs -Destination $readerDestination -Recurse -Force
    Copy-Item build/README-windows.md -Destination (Join-Path $readerPackage 'README.md')
    Compress-Archive -Path "$readerPackage/*" -DestinationPath (Join-Path $Output 'NengliangReader-win-x64.zip') -Force
    Write-Host "Uncompressed package: $readerPackage"
} finally { Pop-Location }

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet restore NengliangReader.sln --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    dotnet build NengliangReader.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
} finally { Pop-Location }

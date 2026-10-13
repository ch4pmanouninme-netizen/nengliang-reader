$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --project tests/Reader.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
    dotnet run --project tests/Reader.Chat.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Chat regression tests failed.' }
} finally { Pop-Location }

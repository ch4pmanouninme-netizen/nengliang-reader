$ErrorActionPreference = 'Stop'
$readerSource = $PSScriptRoot
$readerOutput = Split-Path -Parent $readerSource
$readerCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $readerCompiler)) {
    $readerCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
}
if (!(Test-Path -LiteralPath $readerCompiler)) { throw '需要 Windows .NET Framework 4.8。' }
$readerReferences = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Xml.Linq.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll')
& $readerCompiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 ("/out:" + (Join-Path $readerOutput '良子小窗.exe')) @readerReferences (Join-Path $readerSource 'Reader.cs') (Join-Path $readerSource 'ChatShared.cs') (Join-Path $readerSource 'AppUi.cs') (Join-Path $readerSource 'ChatSecurity.cs') (Join-Path $readerSource 'ChatClient.cs') (Join-Path $readerSource 'AppShell.cs')
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Write-Output ('已生成：' + (Join-Path $readerOutput '良子小窗.exe'))

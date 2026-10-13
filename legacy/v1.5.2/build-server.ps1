$ErrorActionPreference = 'Stop'
$windowCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $windowCompiler)) { $windowCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
$windowOutput = Join-Path (Split-Path -Parent $PSScriptRoot) '良子小窗-聊天服务.exe'
& $windowCompiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 /main:OneLineReader.ChatServerProgram ("/out:" + $windowOutput) /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.Linq.dll (Join-Path $PSScriptRoot 'ChatShared.cs') (Join-Path $PSScriptRoot 'AppUi.cs') (Join-Path $PSScriptRoot 'ChatSecurity.cs') (Join-Path $PSScriptRoot 'ChatServer.cs') (Join-Path $PSScriptRoot 'ChatServerAssembly.cs')
if ($LASTEXITCODE -ne 0) { throw '聊天服务编译失败。' }
Write-Output ('已生成：' + $windowOutput)


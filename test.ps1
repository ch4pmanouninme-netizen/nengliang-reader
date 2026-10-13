param([string]$ResultDirectory = (Join-Path $PSScriptRoot 'test-results'))
$ErrorActionPreference = 'Stop'
$readerCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $readerCompiler)) { $readerCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
New-Item -ItemType Directory -Path $ResultDirectory -Force | Out-Null
$readerTestExecutable = Join-Path $ResultDirectory 'Reader.Tests.exe'
$readerReferences = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Xml.Linq.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll')
& $readerCompiler /nologo /target:exe /platform:anycpu /optimize+ /utf8output /codepage:65001 /main:OneLineReader.ReaderTests ("/out:" + $readerTestExecutable) @readerReferences (Join-Path $PSScriptRoot 'Reader.cs') (Join-Path $PSScriptRoot 'ChatShared.cs') (Join-Path $PSScriptRoot 'AppUi.cs') (Join-Path $PSScriptRoot 'ChatSecurity.cs') (Join-Path $PSScriptRoot 'ChatClient.cs') (Join-Path $PSScriptRoot 'AppShell.cs') (Join-Path $PSScriptRoot 'Reader.Tests.cs')
if ($LASTEXITCODE -ne 0) { throw '测试程序编译失败。' }
$readerTestRun = Join-Path $ResultDirectory ('run-' + [guid]::NewGuid().ToString('N'))
& $readerTestExecutable $readerTestRun
if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
$readerUiExecutable = Join-Path $ResultDirectory 'Reader.UiTests.exe'
& $readerCompiler /nologo /target:exe /platform:anycpu /optimize+ /utf8output /codepage:65001 /main:OneLineReader.LayoutCheck ("/out:" + $readerUiExecutable) @readerReferences (Join-Path $PSScriptRoot 'Reader.cs') (Join-Path $PSScriptRoot 'ChatShared.cs') (Join-Path $PSScriptRoot 'AppUi.cs') (Join-Path $PSScriptRoot 'ChatSecurity.cs') (Join-Path $PSScriptRoot 'ChatClient.cs') (Join-Path $PSScriptRoot 'AppShell.cs') (Join-Path $PSScriptRoot 'Reader.UiTests.cs')
if ($LASTEXITCODE -ne 0) { throw '界面测试程序编译失败。' }
& $readerUiExecutable (Join-Path $readerTestRun 'ui-data') (Join-Path $ResultDirectory 'library-preview.png') (Join-Path $ResultDirectory 'reader-preview.png')
if ($LASTEXITCODE -ne 0) { throw '界面集成测试失败。' }
$windowShellTests = Join-Path $ResultDirectory 'Shell.Tests.exe'
& $readerCompiler /nologo /target:exe /platform:anycpu /optimize+ /utf8output /codepage:65001 /main:OneLineReader.ShellTests ("/out:" + $windowShellTests) @readerReferences (Join-Path $PSScriptRoot 'Reader.cs') (Join-Path $PSScriptRoot 'ChatShared.cs') (Join-Path $PSScriptRoot 'AppUi.cs') (Join-Path $PSScriptRoot 'ChatSecurity.cs') (Join-Path $PSScriptRoot 'ChatClient.cs') (Join-Path $PSScriptRoot 'AppShell.cs') (Join-Path $PSScriptRoot 'Shell.Tests.cs')
if ($LASTEXITCODE -ne 0) { throw '主菜单回归测试编译失败。' }
& $windowShellTests (Join-Path $ResultDirectory ('shell-run-' + [guid]::NewGuid().ToString('N')))
if ($LASTEXITCODE -ne 0) { throw '主菜单回归测试失败。' }
$windowChatTests = Join-Path $ResultDirectory 'Chat.Tests.exe'
& $readerCompiler /nologo /target:exe /platform:anycpu /optimize+ /utf8output /codepage:65001 /main:OneLineReader.ChatTests ("/out:" + $windowChatTests) @readerReferences (Join-Path $PSScriptRoot 'Reader.cs') (Join-Path $PSScriptRoot 'ChatShared.cs') (Join-Path $PSScriptRoot 'AppUi.cs') (Join-Path $PSScriptRoot 'ChatSecurity.cs') (Join-Path $PSScriptRoot 'ChatClient.cs') (Join-Path $PSScriptRoot 'AppShell.cs') (Join-Path $PSScriptRoot 'ChatServer.cs') (Join-Path $PSScriptRoot 'Chat.Tests.cs')
if ($LASTEXITCODE -ne 0) { throw '聊天测试程序编译失败。' }
& $windowChatTests (Join-Path $ResultDirectory ('chat-run-' + [guid]::NewGuid().ToString('N')))
if ($LASTEXITCODE -ne 0) { throw '聊天联调检查失败。' }

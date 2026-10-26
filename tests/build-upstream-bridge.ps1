# 从 legacy/v1.5.2 的 WinForms 源码中裁出聊天服务/客户端核心，编译成 UpstreamBridge.exe，
# 供 Reader.Chat.Tests 验证现代客户端与原版 .NET Framework 服务端之间的双向互操作。
$ErrorActionPreference = 'Stop'
$readerRepo = Split-Path -Parent $PSScriptRoot
# 中间源码和产物都放在 artifacts 下，仓库根目录保持干净。
$bridgeDir = Join-Path $readerRepo 'artifacts/upstream-bridge'
New-Item -ItemType Directory -Force $bridgeDir | Out-Null
# 去掉窗体类：桥接程序只需要协议核心，不需要 WinForms 界面。
foreach ($name in 'ChatServer.cs','ChatClient.cs') {
    $text = Get-Content (Join-Path $readerRepo "legacy/v1.5.2/$name") -Raw
    if ($name -eq 'ChatServer.cs') { $text = $text.Substring(0, $text.IndexOf('    internal sealed class ChatServerForm')) + "}`n" }
    else {
        $start = $text.IndexOf('    internal sealed class ChatTranscript')
        $end = $text.IndexOf('    internal sealed class ChatConnection')
        $text = $text.Remove($start, $end - $start)
        $text = $text.Substring(0, $text.IndexOf('    internal sealed class ChatForm')) + "}`n"
    }
    Set-Content (Join-Path $bridgeDir $name) $text -Encoding utf8
}
# 固定用 .NET Framework 4.x 的 csc.exe，才能复现旧版运行环境。
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe /main:UpstreamBridge "/out:$bridgeDir\UpstreamBridge.exe" /r:System.Xml.Linq.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "$bridgeDir\ChatServer.cs" "$bridgeDir\ChatClient.cs" "$readerRepo\legacy\v1.5.2\ChatSecurity.cs" "$readerRepo\legacy\v1.5.2\ChatShared.cs" "$PSScriptRoot\Reader.Chat.Tests\UpstreamBridge.txt"
if ($LASTEXITCODE -ne 0) { throw 'Upstream bridge build failed.' }

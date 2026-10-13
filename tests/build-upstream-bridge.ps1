$ErrorActionPreference = 'Stop'
$readerRepo = Split-Path -Parent $PSScriptRoot
$bridgeDir = Join-Path $readerRepo 'artifacts/upstream-bridge'
New-Item -ItemType Directory -Force $bridgeDir | Out-Null
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
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe /main:UpstreamBridge "/out:$bridgeDir\UpstreamBridge.exe" /r:System.Xml.Linq.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "$bridgeDir\ChatServer.cs" "$bridgeDir\ChatClient.cs" "$readerRepo\legacy\v1.5.2\ChatSecurity.cs" "$readerRepo\legacy\v1.5.2\ChatShared.cs" "$PSScriptRoot\Reader.Chat.Tests\UpstreamBridge.txt"
if ($LASTEXITCODE -ne 0) { throw 'Upstream bridge build failed.' }

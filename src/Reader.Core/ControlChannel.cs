#nullable enable

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Reader.Core;

// A local-only control endpoint the launcher scripts use to ask a running copy to exit.
// Named pipes map to Unix domain sockets off Windows, so one implementation serves both, and
// unlike a TCP port the endpoint needs no firewall rule and cannot be reached from the LAN.
/// <summary>
/// 本地控制通道：启动脚本通过命名管道请求正在运行的实例退出。
/// 管道名由数据目录派生，因此同一书库只有一个端点；非 Windows 上命名管道映射为 Unix 域套接字。
/// </summary>
public static class ControlChannel
{
    /// <summary>请求运行中的实例正常退出的命令文本。</summary>
    public const string ShutdownCommand = "shutdown";
    /// <summary>处理成功时的应答文本。</summary>
    public const string Accepted = "ok";
    /// <summary>实例正忙、拒绝退出时的应答文本。</summary>
    public const string Refused = "busy";
    // 单行命令或应答的字节上限；对端一直不发送换行时也不能无限缓冲。
    private const int MaxLineBytes = 128;

    // One UTF-8 command line in, one UTF-8 reply line out, written straight to the pipe. The
    // protocol is small enough that a byte loop beats a text reader/writer: no buffering to
    // reason about, no preamble bytes, and a peer that never sends a newline cannot make us
    // allocate past MaxLineBytes. No byte order mark either.
    private static readonly UTF8Encoding Wire = new(false);

    // The pipe name is derived from the data directory so it lines up with the single-instance
    // lock: one endpoint per library. Without this, an installed copy and a smoke-test copy (or
    // two builds with different --data) fight over one global name, and the loser silently
    // loses its control channel.
    /// <summary>
    /// 由数据目录派生管道名：同一数据目录对应同一端点，不同数据目录（安装版/测试版）互不干扰。
    /// </summary>
    /// <param name="root">书库/数据根目录；内部取绝对路径并转小写后再哈希。</param>
    /// <returns>形如 NengliangReader.control.XXXXXXXXXXXXXXXX 的管道名。</returns>
    public static string PipeNameFor(string root)
        => "NengliangReader.control." + Convert.ToHexString(SHA256.HashData(Wire.GetBytes(Path.GetFullPath(root).ToLowerInvariant())).AsSpan(0, 8));

    // The loop runs on the thread pool rather than on the caller's thread. Callers start this from
    // a UI thread, and an async loop awaited on a dispatcher would route every continuation back
    // to it, so a busy UI would stall the very channel meant to report that it is busy.
    // onListening fires once the endpoint is accepting, so a caller that needs to send immediately
    // can wait for that instead of guessing at a delay.
    /// <summary>在后台启动控制通道循环，并返回该循环任务。</summary>
    /// <param name="root">书库数据根目录，用于派生管道名。</param>
    /// <param name="handler">收到命令行后的处理函数，返回一行应答。</param>
    /// <param name="cancellation">取消令牌；取消后循环退出。</param>
    /// <param name="onListening">每次管道开始监听时的回调；可为 null。</param>
    /// <returns>控制循环任务；调用方通常不等待它，除非需要感知异常退出。</returns>
    public static Task ServeAsync(string root, Func<CancellationToken, Task<string>> handler, CancellationToken cancellation, Action? onListening = null)
        => Task.Run(() => ServeLoopAsync(PipeNameFor(root), handler, cancellation, onListening), cancellation);

    // 单实例循环：同一时间只服务一个连接；异常后退避 250 ms，避免管道创建失败时忙等。
    private static async Task ServeLoopAsync(string pipe, Func<CancellationToken, Task<string>> handler, CancellationToken cancellation, Action? onListening)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                // CurrentUserOnly 限制同用户访问，防止其他账户通过管道控制本实例。
                await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                onListening?.Invoke();
                await server.WaitForConnectionAsync(cancellation);
                string? command = await ReadLineAsync(server);
                // 对端连接后未发送完整命令行时回 error，不调用业务处理器。
                await WriteLineAsync(server, command == null ? "error" : await handler(cancellation));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Control channel: " + ex.Message);
                // 短暂退避后重试，避免管道名暂时被占用或连接异常时空转。
                await Task.Delay(250, cancellation).ConfigureAwait(false);
            }
        }
    }

    // Returns the reply, or null when nothing answers in time - which is how "not running" is
    // reported. The deadline is enforced with WhenAny instead of a cancellation token because
    // named pipe reads on Windows do not reliably observe cancellation, and a launcher script
    // must never be left hanging.
    /// <summary>向运行中的实例发送命令并等待应答。</summary>
    /// <param name="root">书库数据根目录，用于派生管道名。</param>
    /// <param name="command">命令文本，通常为 <see cref="ShutdownCommand"/>。</param>
    /// <param name="timeout">等待应答的最长时间。</param>
    /// <returns>应答文本；超时无人应答时返回 null（表示实例未运行）。</returns>
    /// <remarks>超时后放弃等待，但不会强制中止底层管道交换。</remarks>
    public static async Task<string?> TrySendAsync(string root, string command, TimeSpan timeout)
    {
        Task<string?> exchange = ExchangeAsync(PipeNameFor(root), command);
        // 用 WhenAny 实现截止时间：Windows 上的命名管道读取不保证响应取消令牌。
        return await Task.WhenAny(exchange, Task.Delay(timeout)) == exchange ? await exchange : null;
    }

    // 所有连接/读写异常都按“无人应答”处理，统一返回 null，不向启动脚本抛管道异常。
    private static async Task<string?> ExchangeAsync(string pipe, string command)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync();
            await WriteLineAsync(client, command);
            return await ReadLineAsync(client);
        }
        catch (Exception) { return null; }
    }

    // 写一条以 \n 结尾的 UTF-8 命令行，并立即刷新。
    private static async Task WriteLineAsync(Stream stream, string line)
    {
        await stream.WriteAsync(Wire.GetBytes(line + "\n"));
        await stream.FlushAsync();
    }

    // 逐字节读取以避免预读超出本行；按字节上限截断，凑齐一行后再按 UTF-8 解码。
    private static async Task<string?> ReadLineAsync(Stream stream)
    {
        byte[] single = new byte[1];
        using var bytes = new MemoryStream();
        while (bytes.Length < MaxLineBytes)
        {
            if (await stream.ReadAsync(single) == 0) return null;
            // 兼容 CRLF：协议行尾的 \r 不属于命令内容。
            if (single[0] == (byte)'\n')
                return Wire.GetString(bytes.GetBuffer(), 0, (int)bytes.Length).TrimEnd('\r');
            bytes.WriteByte(single[0]);
        }
        return Wire.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
    }

    // A copy without a control channel (an older build) still holds the single-instance lock,
    // so the caller can tell "nothing is running" apart from "something is running but deaf".
    /// <summary>探测单实例锁是否被其他进程持有。</summary>
    /// <param name="root">书库数据根目录，锁文件为其中的 instance.lock。</param>
    /// <returns>锁被占用返回 true；没有实例运行或目录不存在时返回 false。</returns>
    /// <remarks>能独占打开锁文件说明当前没有实例持有它。</remarks>
    public static bool LockHeld(string root)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(root, "instance.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // IOException 表示文件已被其他进程锁定，即实例正在运行。
        catch (IOException) { return true; }
    }
}

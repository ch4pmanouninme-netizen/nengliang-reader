#nullable enable

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Reader.Core;

// A local-only control endpoint the launcher scripts use to ask a running copy to exit.
// Named pipes map to Unix domain sockets off Windows, so one implementation serves both, and
// unlike a TCP port the endpoint needs no firewall rule and cannot be reached from the LAN.
public static class ControlChannel
{
    public const string ShutdownCommand = "shutdown";
    public const string Accepted = "ok";
    public const string Refused = "busy";
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
    public static string PipeNameFor(string root)
        => "NengliangReader.control." + Convert.ToHexString(SHA256.HashData(Wire.GetBytes(Path.GetFullPath(root).ToLowerInvariant())).AsSpan(0, 8));

    // The loop runs on the thread pool rather than on the caller's thread. Callers start this from
    // a UI thread, and an async loop awaited on a dispatcher would route every continuation back
    // to it, so a busy UI would stall the very channel meant to report that it is busy.
    // onListening fires once the endpoint is accepting, so a caller that needs to send immediately
    // can wait for that instead of guessing at a delay.
    public static Task ServeAsync(string root, Func<CancellationToken, Task<string>> handler, CancellationToken cancellation, Action? onListening = null)
        => Task.Run(() => ServeLoopAsync(PipeNameFor(root), handler, cancellation, onListening), cancellation);

    private static async Task ServeLoopAsync(string pipe, Func<CancellationToken, Task<string>> handler, CancellationToken cancellation, Action? onListening)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                onListening?.Invoke();
                await server.WaitForConnectionAsync(cancellation);
                string? command = await ReadLineAsync(server);
                await WriteLineAsync(server, command == null ? "error" : await handler(cancellation));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Control channel: " + ex.Message);
                await Task.Delay(250, cancellation).ConfigureAwait(false);
            }
        }
    }

    // Returns the reply, or null when nothing answers in time - which is how "not running" is
    // reported. The deadline is enforced with WhenAny instead of a cancellation token because
    // named pipe reads on Windows do not reliably observe cancellation, and a launcher script
    // must never be left hanging.
    public static async Task<string?> TrySendAsync(string root, string command, TimeSpan timeout)
    {
        Task<string?> exchange = ExchangeAsync(PipeNameFor(root), command);
        return await Task.WhenAny(exchange, Task.Delay(timeout)) == exchange ? await exchange : null;
    }

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

    private static async Task WriteLineAsync(Stream stream, string line)
    {
        await stream.WriteAsync(Wire.GetBytes(line + "\n"));
        await stream.FlushAsync();
    }

    private static async Task<string?> ReadLineAsync(Stream stream)
    {
        byte[] single = new byte[1];
        var text = new StringBuilder();
        while (text.Length < MaxLineBytes)
        {
            if (await stream.ReadAsync(single) == 0) return null;
            if (single[0] == (byte)'\n') return text.ToString().TrimEnd('\r');
            text.Append((char)single[0]);
        }
        return text.ToString();
    }

    // A copy without a control channel (an older build) still holds the single-instance lock,
    // so the caller can tell "nothing is running" apart from "something is running but deaf".
    public static bool LockHeld(string root)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(root, "instance.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
    }
}

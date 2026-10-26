// 聊天消息的线程安全收件箱：限制网络回调的积压量，并为 UI 线程提供批量排空。
namespace Reader.Chat;

/// <summary>Network callbacks cannot accumulate an unbounded UI dispatcher backlog.</summary>
/// <remarks>网络回调线程调用 <see cref="Enqueue"/>，UI 线程调用 <see cref="Drain"/>。
/// 超过容量时清空积压并只保留一条 overflow 通知，防止界面调度队列无限增长。</remarks>
public sealed class ChatInbox
{
    private readonly object gate = new();
    private readonly Queue<ChatPacket> packets = new();
    private int generation;
    private bool overflowed;
    /// <summary>收件箱可积压的消息条数上限。</summary>
    public const int Capacity = 182;
    /// <summary>清空收件箱并递增代次，返回新的代次。</summary>
    /// <returns>重置后的代次，调用方应保存并传给后续的 <see cref="Enqueue"/>。</returns>
    public int Reset() { lock (gate) { packets.Clear(); overflowed = false; return ++generation; } }
    /// <summary>尝试把消息放入收件箱。</summary>
    /// <param name="current">调用方持有的代次；与当前代次不一致时丢弃消息。</param>
    /// <param name="packet">待入队的消息。</param>
    /// <returns>成功入队返回 true；代次过期、已处于溢出状态或本次触发溢出时返回 false。</returns>
    public bool Enqueue(int current, ChatPacket packet)
    {
        lock (gate)
        {
            // 代次不一致说明调用方仍在向重置前的旧会话投递，直接丢弃。
            if (current != generation || overflowed) return false;
            if (packets.Count >= Capacity)
            {
                // 溢出后只保留一条标记；在下次 Reset 前不再接收任何消息。
                packets.Clear(); packets.Enqueue(new ChatPacket { Kind = "overflow" }); overflowed = true; return false;
            }
            packets.Enqueue(packet); return true;
        }
    }
    /// <summary>一次取走全部积压消息并清空收件箱。</summary>
    /// <returns>按入队顺序排列的消息数组；无消息时返回空数组。</returns>
    public ChatPacket[] Drain() { lock (gate) { var batch = packets.ToArray(); packets.Clear(); return batch; } }
}

namespace Reader.Chat;

/// <summary>Network callbacks cannot accumulate an unbounded UI dispatcher backlog.</summary>
public sealed class ChatInbox
{
    private readonly object gate = new();
    private readonly Queue<ChatPacket> packets = new();
    private int generation;
    private bool overflowed;
    public const int Capacity = 182;
    public int Reset() { lock (gate) { packets.Clear(); overflowed = false; return ++generation; } }
    public bool Enqueue(int current, ChatPacket packet)
    {
        lock (gate)
        {
            if (current != generation || overflowed) return false;
            if (packets.Count >= Capacity)
            {
                packets.Clear(); packets.Enqueue(new ChatPacket { Kind = "overflow" }); overflowed = true; return false;
            }
            packets.Enqueue(packet); return true;
        }
    }
    public ChatPacket[] Drain() { lock (gate) { var batch = packets.ToArray(); packets.Clear(); return batch; } }
}

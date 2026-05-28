namespace Rinha.Api.Runtime.EventLoop;

internal struct Connection
{
    public int Fd;
    public int Slot;
    public int UsedBytes;
    public byte Active;
}

internal sealed class ConnectionTable
{
    public const int MaxConnections = 1024;

    private readonly Connection[] _connections = new Connection[MaxConnections];
    private readonly int[] _freeSlots = new int[MaxConnections];
    private int _freeCount;

    public int ActiveCount => MaxConnections - _freeCount;

    public ConnectionTable()
    {
        for (int i = 0; i < MaxConnections; i++)
            _freeSlots[i] = MaxConnections - 1 - i;
        _freeCount = MaxConnections;
    }

    public bool TryAllocate(int fd, out int slot)
    {
        if (_freeCount == 0)
        {
            slot = -1;
            return false;
        }

        slot = _freeSlots[--_freeCount];
        _connections[slot] = new Connection
        {
            Fd = fd,
            Slot = slot,
            UsedBytes = 0,
            Active = 1
        };
        return true;
    }

    public void Release(int slot)
    {
        if ((uint)slot >= (uint)MaxConnections)
            return;

        ref Connection conn = ref _connections[slot];
        if (conn.Active == 0)
            return;

        conn = default;
        _freeSlots[_freeCount++] = slot;
    }

    public ref Connection Get(int slot) => ref _connections[slot];
}

namespace Rinha.Api.Runtime.EventLoop;

internal static class EpollBufferPool
{
    [ThreadStatic] private static byte[][]? _bufferPool;
    [ThreadStatic] private static int _bufferPoolCount;
    private const int MaxPooledBuffers = 256;
    public const int SlotSize = 2048;

    public static byte[] Alloc()
    {
        if (_bufferPool != null && _bufferPoolCount > 0)
            return _bufferPool[--_bufferPoolCount];
        return new byte[SlotSize];
    }

    public static void Free(byte[]? buf)
    {
        if (buf == null) return;
        _bufferPool ??= new byte[MaxPooledBuffers][];
        if (_bufferPoolCount < MaxPooledBuffers)
            _bufferPool[_bufferPoolCount++] = buf;
    }
}

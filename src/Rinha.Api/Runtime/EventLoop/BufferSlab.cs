using System.Runtime.InteropServices;

namespace Rinha.Api.Runtime.EventLoop;

internal sealed unsafe class BufferSlab : IDisposable
{
    public const int SlotSize = 8192;

    private readonly IntPtr _base;
    private readonly nuint _totalBytes;
    private bool _disposed;

    public BufferSlab(int slotCount)
    {
        _totalBytes = (nuint)(slotCount * SlotSize);
        _base = Marshal.AllocHGlobal((nint)_totalBytes);
    }

    public Span<byte> GetSpan(int slot) =>
        new((void*)IntPtr.Add(_base, slot * SlotSize), SlotSize);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_base != IntPtr.Zero)
            Marshal.FreeHGlobal(_base);
    }
}

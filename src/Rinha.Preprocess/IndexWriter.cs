using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Rinha.Preprocess;

public class IndexWriter
{
    private readonly List<byte> _buf = new();

    public void WriteHeader(int referenceCount, ReadOnlySpan<short> cuts)
    {
        _buf.AddRange("RNSPCST2"u8.ToArray());
        WriteI32(Constants.Scale);
        WriteI32(Constants.PackedDim);
        WriteI32(referenceCount);
        WriteI32(0); // partition count placeholder
        WriteI32(0); // node count placeholder
        WriteI32(0); // block count placeholder
        
        foreach (var cut in cuts)
            WriteI16(cut);
    }

    public void WritePartitionCount(int count)
    {
        int offset = 8 + 4 + 4 + 4;
        var span = CollectionsMarshal.AsSpan(_buf);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), count);
    }

    public void WriteNodeCount(int count)
    {
        int offset = 8 + 4 + 4 + 4 + 4;
        var span = CollectionsMarshal.AsSpan(_buf);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), count);
    }

    public void WriteBlockCount(int count)
    {
        int offset = 8 + 4 + 4 + 4 + 4 + 4;
        var span = CollectionsMarshal.AsSpan(_buf);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), count);
    }

    public void WritePartitionEntry(uint key, int root, int len, ReadOnlySpan<short> min, ReadOnlySpan<short> max)
    {
        WriteU32(key);
        WriteI32(root);
        WriteI32(0);
        WriteI32(len);
        foreach (var v in min) WriteI16(v);
        foreach (var v in max) WriteI16(v);
    }

    public void WriteNodeEntry(int left, int right, int start, int len, ReadOnlySpan<short> min, ReadOnlySpan<short> max)
    {
        WriteI32(left);
        WriteI32(right);
        WriteI32(start);
        WriteI32(len);
        foreach (var v in min) WriteI16(v);
        foreach (var v in max) WriteI16(v);
    }

    public void WriteI16(short v)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, v);
        _buf.Add(bytes[0]);
        _buf.Add(bytes[1]);
    }

    public void WriteU8(byte v)
    {
        _buf.Add(v);
    }

    public byte[] IntoBytes() => _buf.ToArray();

    private void WriteU32(uint v)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, v);
        _buf.Add(bytes[0]);
        _buf.Add(bytes[1]);
        _buf.Add(bytes[2]);
        _buf.Add(bytes[3]);
    }

    private void WriteI32(int v)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, v);
        _buf.Add(bytes[0]);
        _buf.Add(bytes[1]);
        _buf.Add(bytes[2]);
        _buf.Add(bytes[3]);
    }
}

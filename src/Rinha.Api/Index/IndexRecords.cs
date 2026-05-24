using System.Runtime.InteropServices;

namespace Rinha.Api.Index;

[StructLayout(LayoutKind.Explicit, Size = IndexFormat.RecordSize)]
internal struct PartitionRecord
{
    [FieldOffset(0)] public int Key;
    [FieldOffset(4)] public int Root;
}

[StructLayout(LayoutKind.Explicit, Size = IndexFormat.RecordSize)]
internal struct NodeRecord
{
    [FieldOffset(0)] public int Left;
    [FieldOffset(4)] public int Right;
    [FieldOffset(8)] public int Start;
    [FieldOffset(12)] public int Len;
}

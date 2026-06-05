namespace Rinha.Api.Index;

public static class IndexFormat
{
    public const string Magic = "RNSPCST5";
    public const int PackedDims = 16;
    public const int Dims = 14;
    public const int K = 5;
    public const int Lanes = 8;
    public const int Scale = 10000;
    public const int RecordSize = 80;
    public const int BoundsMinOffset = 16;
    public const int BoundsMaxOffset = 48;
    public const int PartitionKeySlots = 256;
}

public enum SearchMode
{
    Exact,
    Specialist,
    KeyFirst
}

public static class SearchConstants
{
    public const int K = 5;
    public const int Dims = 14;
    public const int PackedDims = 16;
    public const int Lanes = 8;
    public const int MaxPartitions = 1024; // Increased from 512 to 1024 to support tree256 active keys limit (up to 1024 keys)
    public const int TreeStackCapacity = 128;
    public const int DeferStackCapacity = 4096;
}

public struct TreePredicate
{
    public byte Dim;
    public bool Enabled;
    public short Threshold;
}

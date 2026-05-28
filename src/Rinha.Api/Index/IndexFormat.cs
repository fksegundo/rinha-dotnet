namespace Rinha.Api.Index;

public static class IndexFormat
{
    public const string Magic = "RNSPCST3";
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
    public const int MaxPartitions = 512;
    public const int TreeStackCapacity = 128;
}

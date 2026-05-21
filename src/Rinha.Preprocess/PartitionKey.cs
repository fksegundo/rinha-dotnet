namespace Rinha.Preprocess;

public static class PartitionKey
{
    public static uint Compute(ReadOnlySpan<short> vector)
    {
        uint key = 0;

        if (vector[5] >= 0)
            key |= 1u << 0;
        if (vector[9] > 0)
            key |= 1u << 1;
        if (vector[10] > 0)
            key |= 1u << 2;
        if (vector[11] > 0)
            key |= 1u << 3;

        int mccBucket = vector[12] switch
        {
            <= 2047 => 0,
            <= 4095 => 1,
            <= 6143 => 2,
            _ => 3
        };
        key |= (uint)(mccBucket << 4);

        if (vector[2] > 4096)
            key |= 1u << 6;
        if (vector[8] > 2048)
            key |= 1u << 7;

        return key;
    }
}

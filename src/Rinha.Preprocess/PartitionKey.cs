namespace Rinha.Preprocess;

public static class PartitionKey
{
    public static uint Compute(ReadOnlySpan<short> vector, ReadOnlySpan<short> cuts)
    {
        uint key = 0;

        if (vector[9] > 0)
            key |= 1u << 0;
        if (vector[10] > 0)
            key |= 1u << 1;
        if (vector[11] > 0)
            key |= 1u << 2;
        if (vector[8] > 2048)
            key |= 1u << 3;
        if (vector[2] > 4096)
            key |= 1u << 4;

        uint bucket = 0;
        short v0 = vector[0];
        for (int i = 0; i < cuts.Length; i++)
        {
            if (v0 > cuts[i])
                bucket++;
            else
                break;
        }
        
        key |= bucket << 5;

        return key;
    }
}

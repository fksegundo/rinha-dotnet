using Rinha.Api.Index;
using Rinha.Api.Options;
using Rinha.Api.Vector;

namespace Rinha.Api.Runtime;

public static class StartupWarmup
{
    public static void Run(SpecialistIndex index, int count)
    {
        Span<short> query = stackalloc short[VectorConstants.PackedDims];
        int scale = VectorConstants.Scale;

        for (int i = 0; i < count; i++)
        {
            query.Clear();
            for (int dim = 0; dim < VectorConstants.PackedDims; dim++)
            {
                short raw = (short)((i * 313 + dim * 1009) % (scale + 1));
                query[dim] = (dim == 5 || dim == 6) && i % 4 == 0
                    ? (short)-VectorConstants.Scale
                    : raw;
            }

            _ = index.PredictFraudCount(query);
        }
    }

    public static void RunDefault(SpecialistIndex index) =>
        Run(index, RinhaOptions.WarmupQueries);
}

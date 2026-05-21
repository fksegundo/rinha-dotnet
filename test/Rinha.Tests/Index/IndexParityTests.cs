using Rinha.Api.Index;
using Rinha.Preprocess;

namespace Rinha.Tests.Index;

public class IndexParityTests
{
    [Fact]
    public void Search_MatchesLinearScan_OnSmallFixture()
    {
        var references = new List<Reference>
        {
            MakeRef([1000, 0, 0, 0, 0, -10000, -10000, 0, 0, 0, 0, 0, 0, 0], 0),
            MakeRef([2000, 0, 0, 0, 0, -10000, -10000, 0, 0, 0, 0, 0, 0, 0], 0),
            MakeRef([3000, 0, 0, 0, 0, -10000, -10000, 0, 0, 0, 0, 0, 0, 0], 0),
            MakeRef([4000, 0, 0, 0, 0, -10000, -10000, 0, 0, 0, 0, 0, 0, 0], 0),
            MakeRef([5000, 0, 0, 0, 0, -10000, -10000, 0, 0, 0, 0, 0, 0, 0], 0),
            MakeRef([9000, 0, 0, 0, 0, -10000, -10000, 0, 0, 0, 0, 0, 0, 0], 1),
            MakeRef([10000, 0, 0, 0, 0, -10000, -10000, 0, 0, 0, 0, 0, 0, 0], 1),
            MakeRef([11000, 0, 0, 0, 0, -10000, -10000, 0, 0, 0, 0, 0, 0, 0], 1),
            MakeRef([12000, 0, 0, 0, 0, -10000, -10000, 0, 0, 0, 0, 0, 0, 0], 1),
            MakeRef([13000, 0, 0, 0, 0, -10000, -10000, 0, 0, 0, 0, 0, 0, 0], 1),
        };

        var bytes = new IndexBuilder().BuildIndex(references, 48, 128);
        var path = Path.Combine(Path.GetTempPath(), $"rinha-test-{Guid.NewGuid():N}.idx");
        File.WriteAllBytes(path, bytes);

        try
        {
            Environment.SetEnvironmentVariable("RINHA_SEARCH_MODE", "key-first");
            using var index = SpecialistIndex.Open(path);

            Span<short> query = stackalloc short[16];
            query[0] = 1050;

            byte tree = index.PredictFraudCount(query);
            byte linear = index.PredictFraudCountExact(query);
            Assert.Equal(linear, tree);
            Assert.Equal(0, tree);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Loader_RejectsInvalidMagic()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rinha-bad-{Guid.NewGuid():N}.idx");
        File.WriteAllBytes(path, "BADMAGIC"u8.ToArray());
        try
        {
            Assert.Throws<InvalidOperationException>(() => SpecialistIndex.Open(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Reference MakeRef(short[] dims14, byte label)
    {
        var vector = new short[16];
        Array.Copy(dims14, vector, dims14.Length);
        return new Reference(vector, label);
    }
}

using System.Text.Json;
using Rinha.Api.Index;
using Rinha.Api.Parsing;

namespace Rinha.Tests.Index;

public class OfficialDatasetVerifierTests
{
    [Fact]
    public void VerifyOfficialDataset_WhenPresent()
    {
        string? indexPath = Environment.GetEnvironmentVariable("RINHA_VERIFY_INDEX");
        string? dataPath = Environment.GetEnvironmentVariable("RINHA_VERIFY_DATA");

        indexPath ??= FindUpwards("test-data/rinha-specialist.idx");
        dataPath ??= FindUpwards("test-data/test-data.json")
            ?? "/home/filonsegundo/Documentos/Codigos/desafio/rinha-de-backend-2026-main/test/test-data.json";

        if (indexPath is null || !File.Exists(indexPath) || !File.Exists(dataPath))
            return;

        using var index = SpecialistIndex.Open(indexPath);
        using var stream = File.OpenRead(dataPath);
        using var doc = JsonDocument.Parse(stream);

        var entries = doc.RootElement.GetProperty("entries");

        int queries = 0;
        int mismatches = 0;
        int falsePositives = 0;
        int falseNegatives = 0;
        Span<short> query = stackalloc short[16];

        foreach (var entry in entries.EnumerateArray())
        {
            queries++;
            var request = entry.GetProperty("request").GetRawText();
            bool expectedApproved = entry.GetProperty("expected_approved").GetBoolean();
            double expectedScore = entry.GetProperty("expected_fraud_score").GetDouble();
            int expectedCount = (int)Math.Round(expectedScore * 5.0);

            query.Clear();
            if (PayloadParser.TryParse(System.Text.Encoding.UTF8.GetBytes(request), query) != ParseResult.Ok)
            {
                mismatches++;
                continue;
            }

            int fraudCount = index.PredictFraudCount(query);
            if (fraudCount != expectedCount)
                mismatches++;

            bool approved = fraudCount < 3;
            if (approved && !expectedApproved)
                falsePositives++;
            else if (!approved && expectedApproved)
                falseNegatives++;
        }

        Assert.Equal(0, mismatches);
        Assert.Equal(0, falsePositives);
        Assert.Equal(0, falseNegatives);
        Assert.True(queries > 0, "expected at least one query");
    }

    private static string? FindUpwards(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }
}

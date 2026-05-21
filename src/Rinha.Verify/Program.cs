using System.Diagnostics;
using System.Text.Json;
using Rinha.Api.Index;
using Rinha.Api.Parsing;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Rinha.Verify <index.idx> <test-data.json> [limit]");
    Environment.Exit(1);
}

string indexPath = args[0];
string dataPath = args[1];
int? limit = args.Length > 2 && int.TryParse(args[2], out int n) ? n : null;

using var index = SpecialistIndex.Open(indexPath);
using var stream = File.OpenRead(dataPath);
using var doc = JsonDocument.Parse(stream);

var entries = doc.RootElement.GetProperty("entries");
var sw = Stopwatch.StartNew();

int queries = 0;
int parseErrors = 0;
int scoreMismatches = 0;
int falsePositives = 0;
int falseNegatives = 0;
Span<short> query = stackalloc short[16];

foreach (var entry in entries.EnumerateArray())
{
    if (limit is int max && queries >= max)
        break;

    queries++;
    var request = entry.GetProperty("request").GetRawText();
    bool expectedApproved = entry.GetProperty("expected_approved").GetBoolean();
    double expectedScore = entry.GetProperty("expected_fraud_score").GetDouble();
    int expectedCount = (int)Math.Round(expectedScore * 5.0);

    query.Clear();
    if (PayloadParser.TryParse(System.Text.Encoding.UTF8.GetBytes(request), query) != ParseResult.Ok)
    {
        parseErrors++;
        continue;
    }

    int fraudCount = index.PredictFraudCount(query);
    bool approved = fraudCount < 3;

    if (fraudCount != expectedCount)
        scoreMismatches++;

    if (approved && !expectedApproved)
        falsePositives++;
    else if (!approved && expectedApproved)
        falseNegatives++;
}

sw.Stop();

Console.WriteLine($"queries={queries}");
Console.WriteLine($"parse_errors={parseErrors}");
Console.WriteLine($"mismatches={scoreMismatches}");
Console.WriteLine($"false_positives={falsePositives}");
Console.WriteLine($"false_negatives={falseNegatives}");
Console.WriteLine($"elapsed_ms={sw.ElapsedMilliseconds}");

if (parseErrors != 0 || scoreMismatches != 0 || falsePositives != 0 || falseNegatives != 0)
    Environment.Exit(1);

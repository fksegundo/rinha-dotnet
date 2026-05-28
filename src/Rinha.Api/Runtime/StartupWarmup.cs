using Rinha.Api.Http;
using Rinha.Api.Index;
using Rinha.Api.Options;
using Rinha.Api.Parsing;
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

    public static void RunDefault(SpecialistIndex index)
    {
        Run(index, RinhaOptions.WarmupQueries);
        RunPayloadWarmup(index);
    }

    private static void RunPayloadWarmup(SpecialistIndex index)
    {
        var countStr = Environment.GetEnvironmentVariable("RINHA_PAYLOAD_WARMUP_REQUESTS");
        if (!int.TryParse(countStr, out var count) || count <= 0)
            return;

        Console.WriteLine($"[Warmup] Warming up payload path with {count} requests...");

        var state = new AppState(index);
        state.MarkReady();

        for (int i = 0; i < count; i++)
        {
            var body = WarmupPayloads[i % WarmupPayloads.Length];
            var requestBytes = BuildHttpRequest(body);

            switch (RawHttpParser.TryParse(requestBytes, out var request, out _, out _))
            {
                case RawHttpParseResult.Complete:
                    RawHttpHandler.BuildResponse(request, state);
                    break;
            }
        }

        Console.WriteLine("[Warmup] Payload path warmup complete");
    }

    private static byte[] BuildHttpRequest(byte[] body)
    {
        var headerText = $"POST /fraud-score HTTP/1.1\r\nHost: localhost\r\nContent-Length: {body.Length}\r\n\r\n";
        var header = System.Text.Encoding.ASCII.GetBytes(headerText);
        var result = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(body, 0, result, header.Length, body.Length);
        return result;
    }

    private static readonly byte[][] WarmupPayloads =
    [
        "{\"id\":\"warmup-1\",\"transaction\":{\"amount\":441.59,\"installments\":1,\"requested_at\":\"2027-07-09T16:31:06Z\"},\"customer\":{\"avg_amount\":883.18,\"tx_count_24h\":1,\"known_merchants\":[\"MERC-004\",\"MERC-017\"]},\"merchant\":{\"id\":\"MERC-004\",\"mcc\":\"5411\",\"avg_amount\":302.78},\"terminal\":{\"is_online\":false,\"card_present\":true,\"km_from_home\":33.88},\"last_transaction\":{\"timestamp\":\"2027-06-04T14:14:22Z\",\"km_from_current\":18.43}}"u8.ToArray(),
        "{\"id\":\"warmup-2\",\"transaction\":{\"amount\":5293.06,\"installments\":8,\"requested_at\":\"2028-09-19T03:34:29Z\"},\"customer\":{\"avg_amount\":60.14,\"tx_count_24h\":11,\"known_merchants\":[\"MERC-009\",\"MERC-001\"]},\"merchant\":{\"id\":\"MERC-087\",\"mcc\":\"7995\",\"avg_amount\":21.57},\"terminal\":{\"is_online\":false,\"card_present\":false,\"km_from_home\":265.78},\"last_transaction\":{\"timestamp\":\"2024-01-04T03:43:32Z\",\"km_from_current\":722.93}}"u8.ToArray(),
        "{\"id\":\"warmup-3\",\"transaction\":{\"amount\":7318.26,\"installments\":8,\"requested_at\":\"2028-07-05T03:41:22Z\"},\"customer\":{\"avg_amount\":158.57,\"tx_count_24h\":11,\"known_merchants\":[\"MERC-013\",\"MERC-010\"]},\"merchant\":{\"id\":\"MERC-073\",\"mcc\":\"7801\",\"avg_amount\":37.46},\"terminal\":{\"is_online\":true,\"card_present\":false,\"km_from_home\":417.33},\"last_transaction\":null}"u8.ToArray(),
        "{\"customer\":{\"avg_amount\":68.88,\"tx_count_24h\":18,\"known_merchants\":[\"MERC-004\",\"MERC-015\",\"MERC-007\"]},\"id\":\"warmup-4\",\"last_transaction\":{\"timestamp\":\"2026-03-17T01:58:06Z\",\"km_from_current\":660.92},\"merchant\":{\"id\":\"MERC-062\",\"mcc\":\"7801\",\"avg_amount\":25.55},\"terminal\":{\"is_online\":true,\"card_present\":false,\"km_from_home\":881.61},\"transaction\":{\"amount\":4368.82,\"installments\":8,\"requested_at\":\"2026-03-17T02:04:06Z\"}}"u8.ToArray(),
        "{\"id\":\"warmup-5\",\"transaction\":{\"amount\":29.47,\"installments\":2,\"requested_at\":\"2028-12-24T08:34:05Z\"},\"customer\":{\"avg_amount\":58.94,\"tx_count_24h\":3,\"known_merchants\":[\"MERC-004\",\"MERC-014\"]},\"merchant\":{\"id\":\"MERC-014\",\"mcc\":\"5411\",\"avg_amount\":378.62},\"terminal\":{\"is_online\":false,\"card_present\":true,\"km_from_home\":20.36},\"last_transaction\":{\"timestamp\":\"2027-11-28T15:22:55Z\",\"km_from_current\":16.71}}"u8.ToArray(),
        "{\"id\":\"warmup-6\",\"transaction\":{\"amount\":9797.7,\"installments\":7,\"requested_at\":\"2026-11-14T06:09:00Z\"},\"customer\":{\"avg_amount\":99.49,\"tx_count_24h\":13,\"known_merchants\":[\"MERC-006\",\"MERC-014\",\"MERC-013\"]},\"merchant\":{\"id\":\"MERC-094\",\"mcc\":\"7802\",\"avg_amount\":33.01},\"terminal\":{\"is_online\":false,\"card_present\":true,\"km_from_home\":396.12},\"last_transaction\":{\"timestamp\":\"2026-03-18T15:14:27Z\",\"km_from_current\":712.42}}"u8.ToArray(),
    ];
}

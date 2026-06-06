using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rinha.Api.Runtime;

public static class SelfWarmup
{
    private static readonly string[] FallbackPayloads = new[]
    {
        "{\"id\":\"warmup-1\",\"transaction\":{\"amount\":441.59,\"installments\":1,\"requested_at\":\"2027-07-09T16:31:06Z\"},\"customer\":{\"avg_amount\":883.18,\"tx_count_24h\":1,\"known_merchants\":[\"MERC-004\",\"MERC-017\"]},\"merchant\":{\"id\":\"MERC-004\",\"mcc\":\"5411\",\"avg_amount\":302.78},\"terminal\":{\"is_online\":false,\"card_present\":true,\"km_from_home\":33.88},\"last_transaction\":{\"timestamp\":\"2027-06-04T14:14:22Z\",\"km_from_current\":18.43}}",
        "{\"id\":\"warmup-2\",\"transaction\":{\"amount\":5293.06,\"installments\":8,\"requested_at\":\"2028-09-19T03:34:29Z\"},\"customer\":{\"avg_amount\":60.14,\"tx_count_24h\":11,\"known_merchants\":[\"MERC-009\",\"MERC-001\"]},\"merchant\":{\"id\":\"MERC-087\",\"mcc\":\"7995\",\"avg_amount\":21.57},\"terminal\":{\"is_online\":false,\"card_present\":false,\"km_from_home\":265.78},\"last_transaction\":{\"timestamp\":\"2024-01-04T03:43:32Z\",\"km_from_current\":722.93}}",
        "{\"id\":\"warmup-3\",\"transaction\":{\"amount\":7318.26,\"installments\":8,\"requested_at\":\"2028-07-05T03:41:22Z\"},\"customer\":{\"avg_amount\":158.57,\"tx_count_24h\":11,\"known_merchants\":[\"MERC-013\",\"MERC-010\"]},\"merchant\":{\"id\":\"MERC-073\",\"mcc\":\"7801\",\"avg_amount\":37.46},\"terminal\":{\"is_online\":true,\"card_present\":false,\"km_from_home\":417.33},\"last_transaction\":null}"
    };

    public static void Start(AppState state)
    {
        bool enabled = Environment.GetEnvironmentVariable("RINHA_SELF_WARMUP") == "1";
        if (!enabled)
            return;

        string url = Environment.GetEnvironmentVariable("RINHA_SELF_WARMUP_URL") ?? "http://localhost:9999/fraud-score";
        int durationMs = int.TryParse(Environment.GetEnvironmentVariable("RINHA_SELF_WARMUP_DURATION_MS"), out var d) ? d : 15000;
        int concurrency = int.TryParse(Environment.GetEnvironmentVariable("RINHA_SELF_WARMUP_CONCURRENCY"), out var c) ? c : 4;
        string payloadsPath = Environment.GetEnvironmentVariable("RINHA_SELF_WARMUP_PAYLOADS") ?? "/app/resources/warmup-payloads.jsonl";

        Console.WriteLine($"[SelfWarmup] Starting HTTP warmup (url={url}, duration={durationMs}ms, concurrency={concurrency})");

        var payloads = new List<string>();
        try
        {
            if (File.Exists(payloadsPath))
            {
                using var reader = new StreamReader(payloadsPath);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (!string.IsNullOrEmpty(line))
                        payloads.Add(line);
                }
                Console.WriteLine($"[SelfWarmup] Loaded {payloads.Count} payloads from {payloadsPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SelfWarmup] Failed to load payloads: {ex.Message}");
        }

        if (payloads.Count == 0)
        {
            payloads.AddRange(FallbackPayloads);
            Console.WriteLine($"[SelfWarmup] Loaded {payloads.Count} fallback payloads");
        }

        state.SetAcceptWarmup(true);

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = concurrency
        };
        var client = new HttpClient(handler);

        var deadline = DateTime.UtcNow.AddMilliseconds(durationMs);
        var tasks = new Task[concurrency];
        long totalRequests = 0;

        for (int i = 0; i < concurrency; i++)
        {
            int taskId = i;
            tasks[i] = Task.Run(async () =>
            {
                int loopCount = 0;
                while (DateTime.UtcNow < deadline)
                {
                    string payload = payloads[(taskId + loopCount * concurrency) % payloads.Count];
                    try
                    {
                        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                        using var response = await client.PostAsync(url, content);
                        Interlocked.Increment(ref totalRequests);
                    }
                    catch
                    {
                        // Backoff slightly on errors to avoid tight-looping before server or load balancer is up
                        await Task.Delay(50);
                    }
                    loopCount++;
                }
            });
        }

        Task.WaitAll(tasks);
        state.SetAcceptWarmup(false);

        Console.WriteLine($"[SelfWarmup] Warmup complete. Total HTTP requests sent: {Volatile.Read(ref totalRequests)}");
    }
}

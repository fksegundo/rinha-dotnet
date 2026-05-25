namespace Rinha.Api.Options;

public static class RinhaOptions
{
    public static string IndexPath =>
        Environment.GetEnvironmentVariable("RINHA_INDEX_PATH") ?? "/app/index/rinha-specialist.idx";

    public static string? FdSocketPath =>
        Environment.GetEnvironmentVariable("RINHA_FD_SOCKET");

    public static bool UseFdPassing =>
        !string.IsNullOrWhiteSpace(FdSocketPath);

    public static string RuntimeMode =>
        Environment.GetEnvironmentVariable("RINHA_RUNTIME") ?? "event-loop";

    public static string UnixSocketPath =>
        Environment.GetEnvironmentVariable("RINHA_UDS_SOCKET")
        ?? throw new InvalidOperationException("RINHA_UDS_SOCKET is required when RINHA_FD_SOCKET is not set");

    public static int WarmupQueries =>
        int.TryParse(Environment.GetEnvironmentVariable("RINHA_WARMUP_QUERIES"), out var n) ? n : 256;

    public static int FdThreadPoolSize =>
        int.TryParse(Environment.GetEnvironmentVariable("RINHA_THREAD_POOL_SIZE"), out var n)
            ? Math.Clamp(n, 1, 512)
            : 256;

    public static long EarlyExitThreshold =>
        long.TryParse(Environment.GetEnvironmentVariable("RINHA_EARLY_EXIT_THRESHOLD"), out var n) ? n : 0;

    public static bool MlockAll =>
        Environment.GetEnvironmentVariable("RINHA_MLOCK_ALL") == "1";

    public static bool MlockIndex =>
        Environment.GetEnvironmentVariable("RINHA_MLOCK_INDEX") == "1";

    public static string MlockAllMode =>
        Environment.GetEnvironmentVariable("RINHA_MLOCK_ALL_MODE") ?? "future";

    public static int MaxBodyBytes =>
        int.TryParse(Environment.GetEnvironmentVariable("RINHA_MAX_BODY_BYTES"), out var n) ? n : 8192;

    public static string SearchMode =>
        Environment.GetEnvironmentVariable("RINHA_SEARCH_MODE") ?? "key-first";

    public static int MinThreads
    {
        get
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("RINHA_MIN_THREADS"), out var n))
                return Math.Clamp(n, 1, 512);

            if (int.TryParse(Environment.GetEnvironmentVariable("DOTNET_ThreadPool_MinThreads"), out n))
                return Math.Clamp(n, 1, 512);

            return UseFdPassing ? 64 : 32;
        }
    }

    public static bool UseEventLoop =>
        UseFdPassing && RuntimeMode.Equals("event-loop", StringComparison.OrdinalIgnoreCase);
}

namespace Rinha.Api.Options;

public static class RinhaOptions
{
    public static string IndexPath =>
        Environment.GetEnvironmentVariable("RINHA_INDEX_PATH") ?? "/app/index/rinha-specialist.idx";

    public static string UnixSocketPath =>
        Environment.GetEnvironmentVariable("RINHA_UDS_SOCKET")
        ?? throw new InvalidOperationException("RINHA_UDS_SOCKET is required");

    public static int WarmupQueries =>
        int.TryParse(Environment.GetEnvironmentVariable("RINHA_WARMUP_QUERIES"), out var n) ? n : 256;

    public static int MaxBodyBytes =>
        int.TryParse(Environment.GetEnvironmentVariable("RINHA_MAX_BODY_BYTES"), out var n) ? n : 8192;

    public static string SearchMode =>
        Environment.GetEnvironmentVariable("RINHA_SEARCH_MODE") ?? "key-first";
}

namespace Rinha.Api.Runtime;

public static class ThreadPoolBootstrap
{
    public static void Configure()
    {
        int minWorkers = int.TryParse(Environment.GetEnvironmentVariable("DOTNET_ThreadPool_MinThreads"), out var w)
            ? w
            : 32;

        ThreadPool.GetMinThreads(out _, out int minIo);
        ThreadPool.SetMinThreads(minWorkers, minIo);
    }
}

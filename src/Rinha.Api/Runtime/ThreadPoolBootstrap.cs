using Rinha.Api.Options;

namespace Rinha.Api.Runtime;

public static class ThreadPoolBootstrap
{
    public static void Configure()
    {
        ThreadPool.GetMinThreads(out _, out int minIo);
        ThreadPool.SetMinThreads(RinhaOptions.MinThreads, minIo);
    }
}

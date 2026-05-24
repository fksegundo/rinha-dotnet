using System.Collections.Concurrent;
using System.Net.Sockets;
using Rinha.Api.Http;
using Rinha.Api.Options;

namespace Rinha.Api.Runtime;

internal sealed class FdWorkerPool : IDisposable
{
    private const int WorkerStackBytes = 64 * 1024;

    private readonly BlockingCollection<(Socket Client, AppState State)> _queue;
    private readonly Thread[] _workers;

    public FdWorkerPool(int workerCount)
    {
        _queue = new BlockingCollection<(Socket, AppState)>(workerCount * 4);
        _workers = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _workers[i] = new Thread(WorkerLoop, WorkerStackBytes)
            {
                IsBackground = true,
                Name = $"fd-worker-{i}"
            };
            _workers[i].Start();
        }
    }

    public void Enqueue(Socket client, AppState state) =>
        _queue.Add((client, state));

    private void WorkerLoop()
    {
        foreach (var (client, state) in _queue.GetConsumingEnumerable())
        {
            try
            {
                RawHttpHandler.Handle(client, state);
            }
            catch (Exception)
            {
                try { client.Close(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        foreach (var worker in _workers)
            worker.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}

internal static class FdWorkerPools
{
    private static FdWorkerPool? _pool;

    public static FdWorkerPool GetOrCreate() =>
        _pool ??= new FdWorkerPool(RinhaOptions.FdThreadPoolSize);
}

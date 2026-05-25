using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;
using Rinha.Api.Runtime;

namespace Rinha.Api.Runtime.EventLoop;

public static class EpollLoop
{
    public static void Run(string socketPath, AppState state, Action onListenerReady)
    {
        FdWorkerPools.GetOrCreate();

        string? directory = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(socketPath))
            File.Delete(socketPath);

        if (OperatingSystem.IsLinux())
            Umask(0);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1024);
        onListenerReady();

        while (true)
        {
            Socket control = listener.Accept();
            new Thread(() => ReceivePassedSockets(control, state))
            {
                IsBackground = true,
                Name = "fd-control"
            }.Start();
        }
    }

    private static void ReceivePassedSockets(Socket controlConnection, AppState state)
    {
        var pool = FdWorkerPools.GetOrCreate();
        using (controlConnection)
        {
            while (true)
            {
                int fd = FdPassing.Receive(controlConnection);
                if (fd < 0)
                    return;

                var client = new Socket(new SafeSocketHandle((IntPtr)fd, ownsHandle: true));
                SocketTuning.ConfigureClient(client);
                pool.Enqueue(client, state);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int umask(int mask);

    private static void Umask(int mask) => umask(mask);
}

using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Rinha.Api.Runtime;

internal static class SocketTuning
{
    private const int IpProtoTcp = 6;
    private const int TcpQuickAck = 11;

    public static void ConfigureClient(Socket socket)
    {
        socket.NoDelay = true;
        if (!OperatingSystem.IsLinux())
            return;

        int enabled = 1;
        setsockopt((int)socket.Handle, IpProtoTcp, TcpQuickAck, ref enabled, sizeof(int));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(int socket, int level, int optionName, ref int optionValue, int optionLen);
}

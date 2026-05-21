using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Rinha.Api.Options;

namespace Rinha.Api.Runtime;

public static class KestrelBootstrap
{
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();

        string socketPath = RinhaOptions.UnixSocketPath;
        PrepareSocketDirectory(socketPath);
        RemoveStaleSocket(socketPath);

        if (OperatingSystem.IsLinux())
            Umask(0);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = RinhaOptions.MaxBodyBytes;
            options.Limits.MaxRequestLineSize = 256;
            options.Limits.MaxRequestHeadersTotalSize = 2048;
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.MinResponseDataRate = null;

            options.ListenUnixSocket(socketPath, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });
    }

    private static void PrepareSocketDirectory(string socketPath)
    {
        string? dir = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private static void RemoveStaleSocket(string socketPath)
    {
        if (File.Exists(socketPath))
            File.Delete(socketPath);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int umask(int mask);

    private static void Umask(int mask) => umask(mask);
}

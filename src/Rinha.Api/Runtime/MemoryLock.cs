using System.Runtime.InteropServices;

namespace Rinha.Api.Runtime;

internal static class MemoryLock
{
    private const int McLockCurrent = 1;
    private const int McLockFuture = 2;

    public static void TryLockAll(string mode)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("mlockall is only supported on linux");
            return;
        }

        int flags = mode switch
        {
            "current" => McLockCurrent,
            "current-future" => McLockCurrent | McLockFuture,
            "future" => McLockFuture,
            _ => LogInvalidMode(mode)
        };

        if (mlockall(flags) != 0)
        {
            Console.Error.WriteLine(
                $"mlockall({mode}) failed: {Marshal.GetLastPInvokeErrorMessage()}");
            return;
        }

        Console.Error.WriteLine($"mlockall({mode}) succeeded");
    }

    public static void TryLockRegion(IntPtr ptr, nuint len)
    {
        if (!OperatingSystem.IsLinux() || ptr == IntPtr.Zero || len == 0)
            return;

        if (mlock(ptr, len) != 0)
        {
            Console.Error.WriteLine(
                $"mlock({len} bytes) failed: {Marshal.GetLastPInvokeErrorMessage()}");
            return;
        }

        Console.Error.WriteLine($"mlock({len} bytes) succeeded");
    }

    private static int LogInvalidMode(string mode)
    {
        Console.Error.WriteLine($"invalid RINHA_MLOCK_ALL_MODE='{mode}'; using MCL_FUTURE");
        return McLockFuture;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mlock(IntPtr addr, nuint len);

    [DllImport("libc", SetLastError = true)]
    private static extern int mlockall(int flags);
}

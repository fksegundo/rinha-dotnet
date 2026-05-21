using System.Runtime.InteropServices;

namespace Rinha.Api.Index;

internal static class IndexMemory
{
    private const int MadWillNeed = 3;

    [DllImport("libc", SetLastError = true)]
    private static extern int madvise(IntPtr addr, UIntPtr len, int advice);

    public static void AdviseWillNeed(IntPtr ptr, nint len)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            madvise(ptr, (UIntPtr)(ulong)len, MadWillNeed);
        }
        catch (DllNotFoundException)
        {
        }
    }
}

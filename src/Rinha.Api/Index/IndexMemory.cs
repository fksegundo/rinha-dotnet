using System.Runtime.InteropServices;

namespace Rinha.Api.Index;

internal static class IndexMemory
{
    private const int MadRandom = 1;
    private const int MadWillNeed = 3;
    private const int MadHugePage = 14;

    [DllImport("libc", SetLastError = true)]
    private static extern int madvise(IntPtr addr, UIntPtr len, int advice);

    public static void AdviseRandomAccess(IntPtr ptr, nint len) =>
        Advise(ptr, len, MadRandom);

    public static void AdviseWillNeed(IntPtr ptr, nint len) =>
        Advise(ptr, len, MadWillNeed);

    public static void AdviseHugePage(IntPtr ptr, nint len) =>
        Advise(ptr, len, MadHugePage);

    private static void Advise(IntPtr ptr, nint len, int advice)
    {
        if (!OperatingSystem.IsLinux() || ptr == IntPtr.Zero || len <= 0)
            return;

        try
        {
            madvise(ptr, (UIntPtr)(ulong)len, advice);
        }
        catch (DllNotFoundException)
        {
        }
    }
}

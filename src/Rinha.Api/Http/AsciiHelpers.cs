namespace Rinha.Api.Http;

internal static class AsciiHelpers
{
    public static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            byte a = left[i];
            byte b = right[i];
            if (a >= (byte)'A' && a <= (byte)'Z')
                a = (byte)(a | 0x20);
            if (b >= (byte)'A' && b <= (byte)'Z')
                b = (byte)(b | 0x20);
            if (a != b)
                return false;
        }

        return true;
    }
}

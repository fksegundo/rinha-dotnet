using System.Buffers;
using Rinha.Api.Options;

namespace Rinha.Api.Http;

public static class RequestBody
{
    public static async ValueTask<(bool Ok, byte[] Buffer, int Length)> ReadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        int max = RinhaOptions.MaxBodyBytes;

        if (request.ContentLength is long contentLength && contentLength > max)
            return (false, [], 0);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            int length;
            if (request.ContentLength is long exactLong && exactLong >= 0 && exactLong <= max)
            {
                int exact = (int)exactLong;
                if (exact == 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    return (true, [], 0);
                }

                await request.Body.ReadExactlyAsync(buffer.AsMemory(0, exact), cancellationToken);
                length = exact;
            }
            else if (request.ContentLength is long tooLarge && tooLarge > max)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                return (false, [], 0);
            }
            else
            {
                length = 0;
                while (length < max)
                {
                    int read = await request.Body.ReadAsync(
                        buffer.AsMemory(length, max - length),
                        cancellationToken);
                    if (read == 0)
                        break;
                    length += read;
                }

                if (length >= max)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    return (false, [], 0);
                }
            }

            return (true, buffer, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return (false, [], 0);
        }
    }

    public static void Return(byte[] buffer)
    {
        if (buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}

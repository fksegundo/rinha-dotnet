using Rinha.Api.Http;
using Rinha.Api.Options;
using Rinha.Api.Parsing;
using Rinha.Api.Runtime;
using Rinha.Api.Vector;

namespace Rinha.Api.Runtime.EventLoop;

internal static class FraudScoreFastPath
{
    private static ReadOnlySpan<byte> PostPrefix => "POST /fraud-score HTTP/1.1\r\n"u8;
    private static ReadOnlySpan<byte> ContentLengthNeedle => "Content-Length:"u8;
    private static ReadOnlySpan<byte> ConnectionCloseNeedle => "Connection: close"u8;
    private static readonly int MaxBodyBytes = RinhaOptions.MaxBodyBytes;

    public static bool TryHandle(
        ReadOnlySpan<byte> buffer,
        AppState state,
        out ReadOnlyMemory<byte> response,
        out int consumed,
        out bool keepAlive)
    {
        response = default;
        consumed = 0;
        keepAlive = true;

        if (buffer.Length < PostPrefix.Length)
            return false;

        if (!buffer.StartsWith(PostPrefix))
            return false;

        if (!state.Ready)
        {
            response = RawHttpResponses.NotReady;
            consumed = FindHeaderEnd(buffer);
            if (consumed <= 0)
                return false;
            keepAlive = !ContainsConnectionClose(buffer.Slice(0, consumed));
            return true;
        }

        int headerEnd = FindHeaderEnd(buffer);
        if (headerEnd <= 0)
            return false;

        ReadOnlySpan<byte> headers = buffer.Slice(PostPrefix.Length, headerEnd - PostPrefix.Length);
        if (!TryGetContentLength(headers, out int contentLength))
            return false;

        if (contentLength > MaxBodyBytes)
        {
            response = RawHttpResponses.BadRequest;
            consumed = headerEnd;
            keepAlive = false;
            return true;
        }

        int bodyEnd = headerEnd + contentLength;
        if (buffer.Length < bodyEnd)
            return false;

        keepAlive = !ContainsConnectionClose(buffer.Slice(0, headerEnd));

        Span<short> query = stackalloc short[VectorConstants.PackedDims];
        if (PayloadParser.TryParse(buffer.Slice(headerEnd, contentLength), query) != ParseResult.Ok)
        {
            response = RawHttpResponses.BadRequest;
            consumed = bodyEnd;
            keepAlive = false;
            return true;
        }

        response = RawHttpResponses.ForFraudCount(state.Index.PredictFraudCount(query));
        consumed = bodyEnd;
        return true;
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> buffer)
    {
        for (int i = 3; i < buffer.Length; i++)
        {
            if (buffer[i] == (byte)'\n'
                && buffer[i - 1] == (byte)'\r'
                && buffer[i - 2] == (byte)'\n'
                && buffer[i - 3] == (byte)'\r')
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static bool TryGetContentLength(ReadOnlySpan<byte> headers, out int contentLength)
    {
        contentLength = 0;
        int i = 0;
        while (i + ContentLengthNeedle.Length <= headers.Length)
        {
            if (EqualsAsciiIgnoreCase(headers.Slice(i, ContentLengthNeedle.Length), ContentLengthNeedle))
            {
                ReadOnlySpan<byte> rest = headers.Slice(i + ContentLengthNeedle.Length);
                int valueStart = 0;
                while (valueStart < rest.Length && rest[valueStart] is (byte)' ' or (byte)'\t')
                    valueStart++;

                int value = 0;
                for (int j = valueStart; j < rest.Length; j++)
                {
                    byte b = rest[j];
                    if (b == (byte)'\r' || b is (byte)' ' or (byte)'\t')
                        break;
                    if (b < (byte)'0' || b > (byte)'9')
                        return false;
                    value = value * 10 + (b - (byte)'0');
                }

                contentLength = value;
                return true;
            }

            i++;
        }

        return false;
    }

    private static bool ContainsConnectionClose(ReadOnlySpan<byte> headers)
    {
        for (int i = 0; i + ConnectionCloseNeedle.Length <= headers.Length; i++)
        {
            if (EqualsAsciiIgnoreCase(headers.Slice(i, ConnectionCloseNeedle.Length), ConnectionCloseNeedle))
                return true;
        }

        return false;
    }

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
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

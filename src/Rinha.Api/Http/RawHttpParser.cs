using Rinha.Api.Options;

namespace Rinha.Api.Http;

internal static class RawHttpParser
{
    private static readonly int MaxBodyBytes = RinhaOptions.MaxBodyBytes;

    public static RawHttpParseResult TryParse(
        ReadOnlySpan<byte> buffer,
        out RawHttpRequest request,
        out int consumed,
        out ReadOnlyMemory<byte> reject)
    {
        request = default;
        consumed = 0;
        reject = default;

        if (!TryFindHeaderEnd(buffer, out int headerEnd))
            return RawHttpParseResult.NeedMore;

        if (!TryParseFirstLine(buffer, headerEnd, out RawHttpMethod method, out int pathStart, out int pathEnd, out int headersStart))
        {
            reject = RawHttpResponses.BadRequest;
            consumed = headerEnd;
            return RawHttpParseResult.Reject;
        }

        ReadOnlySpan<byte> path = buffer.Slice(pathStart, pathEnd - pathStart);
        int contentLength = TryGetContentLength(buffer.Slice(headersStart, headerEnd - headersStart));

        if (TryEarlyReject(method, path, contentLength, out ReadOnlyMemory<byte> earlyReject))
        {
            reject = earlyReject;
            consumed = headerEnd;
            return RawHttpParseResult.Reject;
        }

        int bodyEnd = headerEnd + contentLength;
        if (buffer.Length < bodyEnd)
            return RawHttpParseResult.NeedMore;

        bool keepAlive = !ContainsConnectionClose(buffer.Slice(0, headerEnd));
        request = new RawHttpRequest(method, path, buffer.Slice(headerEnd, contentLength), keepAlive);
        consumed = bodyEnd;
        return RawHttpParseResult.Complete;
    }

    private static bool TryEarlyReject(
        RawHttpMethod method,
        ReadOnlySpan<byte> path,
        int contentLength,
        out ReadOnlyMemory<byte> response)
    {
        response = default;
        if (method == RawHttpMethod.Get && path.SequenceEqual("/ready"u8))
            return false;

        if (method == RawHttpMethod.Post && path.SequenceEqual("/fraud-score"u8))
            return contentLength > MaxBodyBytes ? Assign(out response, RawHttpResponses.BadRequest) : false;

        return Assign(out response, RawHttpResponses.NotFound);
    }

    private static bool Assign(out ReadOnlyMemory<byte> target, ReadOnlyMemory<byte> value)
    {
        target = value;
        return true;
    }

    private static bool TryFindHeaderEnd(ReadOnlySpan<byte> buffer, out int headerEnd)
    {
        int idx = buffer.IndexOf("\r\n\r\n"u8);
        if (idx >= 0)
        {
            headerEnd = idx + 4;
            return true;
        }

        headerEnd = 0;
        return false;
    }

    private static bool TryParseFirstLine(
        ReadOnlySpan<byte> buffer,
        int headerEnd,
        out RawHttpMethod method,
        out int pathStart,
        out int pathEnd,
        out int headersStart)
    {
        method = default;
        pathStart = 0;
        pathEnd = 0;
        headersStart = 0;

        ReadOnlySpan<byte> headers = buffer.Slice(0, headerEnd);
        int lineEnd = headers.IndexOf("\r\n"u8);
        if (lineEnd <= 0)
            return false;

        ReadOnlySpan<byte> firstLine = headers.Slice(0, lineEnd);
        int methodEnd = firstLine.IndexOf((byte)' ');
        if (methodEnd <= 0)
            return false;

        if (firstLine.StartsWith("GET"u8))
            method = RawHttpMethod.Get;
        else if (firstLine.StartsWith("POST"u8))
            method = RawHttpMethod.Post;
        else
            return false;

        pathStart = methodEnd + 1;
        if (pathStart >= firstLine.Length)
            return false;

        int relativePathEnd = firstLine.Slice(pathStart).IndexOf((byte)' ');
        if (relativePathEnd <= 0)
            return false;

        pathEnd = pathStart + relativePathEnd;
        headersStart = lineEnd + 2;
        return pathEnd > pathStart;
    }

    private static int TryGetContentLength(ReadOnlySpan<byte> headers)
    {
        const string needle = "content-length:";
        int i = 0;
        while (i + needle.Length <= headers.Length)
        {
            if (headers[i] is (byte)'c' or (byte)'C')
            {
                ReadOnlySpan<byte> window = headers.Slice(i, needle.Length);
                if (AsciiHelpers.EqualsAsciiIgnoreCase(window, "content-length:"u8))
                {
                    ReadOnlySpan<byte> rest = headers.Slice(i + needle.Length);
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
                            return 0;
                        value = value * 10 + (b - (byte)'0');
                    }

                    return value;
                }
            }

            i++;
        }

        return 0;
    }

    private static bool ContainsConnectionClose(ReadOnlySpan<byte> headers)
    {
        if (headers.IndexOf("Connection: close"u8) >= 0)
            return true;

        for (int i = 0; i + 17 <= headers.Length; i++)
        {
            if (AsciiHelpers.EqualsAsciiIgnoreCase(headers.Slice(i, 17), "Connection: close"u8))
                return true;
        }

        return false;
    }
}

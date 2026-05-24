using System.Buffers;
using System.Net.Sockets;
using Rinha.Api.Options;
using Rinha.Api.Parsing;
using Rinha.Api.Runtime;
using Rinha.Api.Vector;

namespace Rinha.Api.Http;

public static class RawHttpHandler
{
    private const int MaxBuffer = 8192;

    public static void Handle(Socket socket, AppState state)
    {
        Span<byte> buffer = stackalloc byte[MaxBuffer];
        int used = 0;
        try
        {
            while (socket.Connected)
            {
                int read = socket.Receive(buffer.Slice(used, MaxBuffer - used), SocketFlags.None);
                if (read == 0)
                    return;

                used += read;
                int processed = 0;

                while (processed < used)
                {
                    switch (RawHttpParser.TryParse(buffer.Slice(processed, used - processed), out var request, out int consumed, out ReadOnlyMemory<byte> reject))
                    {
                        case RawHttpParseResult.Complete:
                        {
                            ReadOnlyMemory<byte> response = BuildResponse(request, state);
                            bool keepAlive = request.KeepAlive;
                            processed += consumed;
                            if (!TrySendAll(socket, response))
                                return;
                            if (!keepAlive)
                                return;
                            break;
                        }

                        case RawHttpParseResult.Reject:
                            if (!TrySendAll(socket, reject))
                                return;
                            return;

                        case RawHttpParseResult.NeedMore:
                            if (used >= MaxBuffer)
                            {
                                TrySendAll(socket, RawHttpResponses.BadRequest);
                                return;
                            }

                            goto shift;

                        default:
                            return;
                    }
                }

            shift:
                if (processed > 0)
                {
                    buffer.Slice(processed, used - processed).CopyTo(buffer);
                    used -= processed;
                }
            }
        }
        catch (SocketException)
        {
        }
        finally
        {
            socket.Close();
        }
    }

    public static async Task HandleAsync(Socket socket, AppState state)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxBuffer);
        try
        {
            int used = 0;
            while (socket.Connected)
            {
                int read = await socket.ReceiveAsync(buffer.AsMemory(used, MaxBuffer - used), SocketFlags.None);
                if (read == 0)
                    return;

                used += read;
                int processed = 0;

                while (processed < used)
                {
                    switch (RawHttpParser.TryParse(buffer.AsSpan(processed, used - processed), out var request, out int consumed, out ReadOnlyMemory<byte> reject))
                    {
                        case RawHttpParseResult.Complete:
                        {
                            ReadOnlyMemory<byte> response = BuildResponse(request, state);
                            bool keepAlive = request.KeepAlive;
                            processed += consumed;
                            if (!await TrySendAllAsync(socket, response))
                                return;
                            if (!keepAlive)
                                return;
                            break;
                        }

                        case RawHttpParseResult.Reject:
                            if (!await TrySendAllAsync(socket, reject))
                                return;
                            return;

                        case RawHttpParseResult.NeedMore:
                            if (used >= MaxBuffer)
                            {
                                await TrySendAllAsync(socket, RawHttpResponses.BadRequest);
                                return;
                            }

                            goto shift;

                        default:
                            return;
                    }
                }

            shift:
                if (processed > 0)
                {
                    Buffer.BlockCopy(buffer, processed, buffer, 0, used - processed);
                    used -= processed;
                }
            }
        }
        catch (SocketException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            socket.Close();
        }
    }

    private static ReadOnlyMemory<byte> BuildResponse(in RawHttpRequest request, AppState state)
    {
        if (request.Method == RawHttpMethod.Get && request.Path.SequenceEqual("/ready"u8))
            return state.Ready ? RawHttpResponses.Ready : RawHttpResponses.NotReady;

        if (request.Method == RawHttpMethod.Post && request.Path.SequenceEqual("/fraud-score"u8))
        {
            if (!state.Ready)
                return RawHttpResponses.NotReady;

            Span<short> query = stackalloc short[VectorConstants.PackedDims];
            if (PayloadParser.TryParse(request.Body, query) != ParseResult.Ok)
                return RawHttpResponses.BadRequest;

            return RawHttpResponses.ForFraudCount(state.Index.PredictFraudCount(query));
        }

        return RawHttpResponses.NotFound;
    }

    private static bool TrySendAll(Socket socket, ReadOnlyMemory<byte> payload)
    {
        while (!payload.IsEmpty)
        {
            int sent = socket.Send(payload.Span, SocketFlags.None);
            if (sent <= 0)
                return false;
            payload = payload[sent..];
        }

        return true;
    }

    private static async Task<bool> TrySendAllAsync(Socket socket, ReadOnlyMemory<byte> payload)
    {
        while (!payload.IsEmpty)
        {
            int sent = await socket.SendAsync(payload, SocketFlags.None);
            if (sent <= 0)
                return false;
            payload = payload[sent..];
        }

        return true;
    }
}

internal enum RawHttpMethod : byte
{
    Get,
    Post
}

internal enum RawHttpParseResult : byte
{
    Complete,
    Reject,
    NeedMore
}

internal ref struct RawHttpRequest
{
    public RawHttpRequest(RawHttpMethod method, ReadOnlySpan<byte> path, ReadOnlySpan<byte> body, bool keepAlive)
    {
        Method = method;
        Path = path;
        Body = body;
        KeepAlive = keepAlive;
    }

    public RawHttpMethod Method { get; }
    public ReadOnlySpan<byte> Path { get; }
    public ReadOnlySpan<byte> Body { get; }
    public bool KeepAlive { get; }
}

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
        for (int i = 3; i < buffer.Length; i++)
        {
            if (buffer[i] == (byte)'\n'
                && buffer[i - 1] == (byte)'\r'
                && buffer[i - 2] == (byte)'\n'
                && buffer[i - 3] == (byte)'\r')
            {
                headerEnd = i + 1;
                return true;
            }
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
                if (EqualsAsciiIgnoreCase(window, "content-length:"u8))
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
        for (int i = 0; i + 17 <= headers.Length; i++)
        {
            if (EqualsAsciiIgnoreCase(headers.Slice(i, 17), "Connection: close"u8))
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

internal static class RawHttpResponses
{
    public static readonly ReadOnlyMemory<byte> Ready =
        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray();

    public static readonly ReadOnlyMemory<byte> NotReady =
        "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n"u8.ToArray();

    public static readonly ReadOnlyMemory<byte> BadRequest =
        "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();

    public static readonly ReadOnlyMemory<byte> NotFound =
        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();

    private static readonly ReadOnlyMemory<byte>[] ByFraudCount =
    [
        "HTTP/1.1 200 OK\r\nContent-Length: 35\r\n\r\n{\"approved\":true,\"fraud_score\":0.0}"u8.ToArray(),
        "HTTP/1.1 200 OK\r\nContent-Length: 35\r\n\r\n{\"approved\":true,\"fraud_score\":0.2}"u8.ToArray(),
        "HTTP/1.1 200 OK\r\nContent-Length: 35\r\n\r\n{\"approved\":true,\"fraud_score\":0.4}"u8.ToArray(),
        "HTTP/1.1 200 OK\r\nContent-Length: 36\r\n\r\n{\"approved\":false,\"fraud_score\":0.6}"u8.ToArray(),
        "HTTP/1.1 200 OK\r\nContent-Length: 36\r\n\r\n{\"approved\":false,\"fraud_score\":0.8}"u8.ToArray(),
        "HTTP/1.1 200 OK\r\nContent-Length: 36\r\n\r\n{\"approved\":false,\"fraud_score\":1.0}"u8.ToArray()
    ];

    public static ReadOnlyMemory<byte> ForFraudCount(int fraudCount) =>
        (uint)fraudCount < (uint)ByFraudCount.Length ? ByFraudCount[fraudCount] : ByFraudCount[5];
}

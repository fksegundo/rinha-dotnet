namespace Rinha.Api.Http;

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

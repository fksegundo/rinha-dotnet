namespace Rinha.Api.Http;

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

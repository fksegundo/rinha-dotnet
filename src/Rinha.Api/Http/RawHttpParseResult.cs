namespace Rinha.Api.Http;

internal enum RawHttpParseResult : byte
{
    Complete,
    Reject,
    NeedMore
}

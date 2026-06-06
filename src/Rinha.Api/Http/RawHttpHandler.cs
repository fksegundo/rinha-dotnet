using System.Buffers;
using System.Net.Sockets;
using Rinha.Api.Parsing;
using Rinha.Api.Runtime;
using Rinha.Api.Runtime.EventLoop;
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
                    ReadOnlySpan<byte> slice = buffer.Slice(processed, used - processed);
                    if (FraudScoreFastPath.TryHandle(slice, state, out ReadOnlyMemory<byte> fastResponse, out int fastConsumed, out bool fastKeepAlive))
                    {
                        processed += fastConsumed;
                        if (!TrySendAll(socket, fastResponse))
                            return;
                        if (!fastKeepAlive)
                            return;
                        continue;
                    }

                    switch (RawHttpParser.TryParse(slice, out var request, out int consumed, out ReadOnlyMemory<byte> reject))
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

    internal static ReadOnlyMemory<byte> BuildResponse(in RawHttpRequest request, AppState state)
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

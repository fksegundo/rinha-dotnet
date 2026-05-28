using Rinha.Api.Http;
using Rinha.Api.Index;
using Rinha.Api.Options;
using Rinha.Api.Parsing;
using Rinha.Api.Runtime;
using Rinha.Api.Runtime.EventLoop;
using Rinha.Api.Vector;

ThreadPoolBootstrap.Configure();

if (RinhaOptions.MlockAll)
    MemoryLock.TryLockAll(RinhaOptions.MlockAllMode);

SpecialistIndex index;
try
{
    index = SpecialistIndex.Open(RinhaOptions.IndexPath);

    if (RinhaOptions.MlockIndex)
        index.MlockMapping();

    if (RinhaOptions.PretouchIndex)
        index.PretouchMapping();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"failed to open index '{RinhaOptions.IndexPath}': {ex.Message}");
    Environment.Exit(1);
    return;
}

var state = new AppState(index);

if (RinhaOptions.UseFdPassing)
{
    if (OperatingSystem.IsLinux())
        Syscalls.IgnoreSigPipe();

    try
    {
        StartupWarmup.RunDefault(state.Index);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"warmup failed: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    Action markReady = () => state.MarkReady();

    if (RinhaOptions.UseEventLoop)
        EpollLoop.Run(RinhaOptions.FdSocketPath!, state, markReady);
    else
        FdSocketServer.Run(RinhaOptions.FdSocketPath!, state, markReady);
    return;
}

_ = Task.Run(() =>
{
    try
    {
        StartupWarmup.RunDefault(state.Index);
        state.MarkReady();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"warmup failed: {ex.Message}");
        Environment.Exit(1);
    }
});

var builder = WebApplication.CreateSlimBuilder(args);
KestrelBootstrap.Configure(builder);
builder.Services.AddSingleton(state);

var app = builder.Build();

app.MapGet("/ready", (AppState s) =>
    s.Ready ? Results.Text("ok", "text/plain") : Results.StatusCode(503));

app.MapPost("/fraud-score", async (HttpRequest request, AppState s, CancellationToken ct) =>
{
    if (!s.Ready)
        return Results.StatusCode(503);

    var (ok, buffer, length) = await RequestBody.ReadAsync(request, ct);
    if (!ok)
        return Results.BadRequest();

    try
    {
        Span<short> query = stackalloc short[VectorConstants.PackedDims];
        if (PayloadParser.TryParse(buffer.AsSpan(0, length), query) != ParseResult.Ok)
            return Results.BadRequest();

        int fraudCount = s.Index.PredictFraudCount(query);
        return Results.Bytes(FraudResponses.ForFraudCount(fraudCount), "application/json");
    }
    finally
    {
        RequestBody.Return(buffer);
    }
});

app.MapFallback(() => Results.NotFound());

app.Run();

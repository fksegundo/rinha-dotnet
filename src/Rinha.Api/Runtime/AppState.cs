using Rinha.Api.Index;

namespace Rinha.Api.Runtime;

public sealed class AppState
{
    private int _ready;
    private int _acceptWarmup;

    public AppState(SpecialistIndex index)
    {
        Index = index;
    }

    public SpecialistIndex Index { get; }

    public bool Ready => Volatile.Read(ref _ready) != 0;
    public bool AcceptWarmup => Volatile.Read(ref _acceptWarmup) != 0;

    public void MarkReady() => Volatile.Write(ref _ready, 1);
    public void SetAcceptWarmup(bool val) => Volatile.Write(ref _acceptWarmup, val ? 1 : 0);
}

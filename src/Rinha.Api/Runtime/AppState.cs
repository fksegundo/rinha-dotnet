using Rinha.Api.Index;

namespace Rinha.Api.Runtime;

public sealed class AppState
{
    private int _ready;

    public AppState(SpecialistIndex index)
    {
        Index = index;
    }

    public SpecialistIndex Index { get; }

    public bool Ready => Volatile.Read(ref _ready) != 0;

    public void MarkReady() => Volatile.Write(ref _ready, 1);
}

namespace Rinha.Api.Runtime.EventLoop;

internal enum ConnPhase { Reading, Writing }
internal enum ReadOutcome { Data, WouldBlock, Closed }
internal enum WriteOutcome { DoneReading, Wait, Closed }

internal struct ConnState
{
    public int Fd;
    public ConnPhase Phase;
    public byte[] Buf;
    public int Used;
    public int Written;
    public ReadOnlyMemory<byte> Response;
    public int LeftoverOff;
    public int LeftoverLen;
    public bool KeepAlive;
}

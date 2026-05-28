using System.Runtime.InteropServices;
using Rinha.Api.Http;
using Rinha.Api.Options;

namespace Rinha.Api.Runtime.EventLoop;

public static unsafe class EpollLoop
{
    private const ulong ControlToken = ulong.MaxValue - 1;
    private const ulong ListenerToken = ulong.MaxValue;
    private const int MaxEvents = 256;
    private const int MaxClientFd = 65536;
    private const int DefaultRecvFdBudget = 32;
    private const int DefaultEpollTimeoutMs = 1;
    private const int SlotSize = 2048;

    public static void Run(string socketPath, AppState state, Action onListenerReady)
    {
        Syscalls.IgnoreSigPipe();

        var epollTimeoutMs = GetEnvInt("RINHA_EPOLL_TIMEOUT_MS", DefaultEpollTimeoutMs);
        var recvFdBudget = GetEnvInt("RINHA_RECV_FD_BUDGET", DefaultRecvFdBudget);
        var acceptBudget = GetEnvInt("RINHA_ACCEPT_BUDGET", 0);
        var clientFdPreconfigured = RinhaOptions.ClientFdPreconfigured;
        var useEdgeTriggered = GetEnvBool("RINHA_EPOLL_EDGE", true);
        var heartbeatMs = GetEnvInt("RINHA_EPOLL_HEARTBEAT_MS", 0);

        Console.WriteLine($"[EpollLoop] Starting on {socketPath} (timeout={epollTimeoutMs}ms, edge={useEdgeTriggered}, recv_budget={recvFdBudget})...");

        string? directory = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(socketPath))
            File.Delete(socketPath);

        Umask(0);

        int listenerFd = Syscalls.Socket(Syscalls.AfUnix, Syscalls.SockStream, 0);
        if (listenerFd < 0)
            throw new Exception("failed to create listener socket");

        Syscalls.SetNonBlocking(listenerFd);

        var addr = new Syscalls.SockAddrUn { SunFamily = (ushort)Syscalls.AfUnix };
        var pathBytes = System.Text.Encoding.ASCII.GetBytes(socketPath);
        Syscalls.SockAddrUn* pAddr = &addr;
        byte* p = pAddr->SunPath;
        Marshal.Copy(pathBytes, 0, (IntPtr)p, Math.Min(pathBytes.Length, 107));

        if (Syscalls.Bind(listenerFd, &addr, (uint)(2 + Math.Min(pathBytes.Length, 107))) != 0)
            throw new Exception($"failed to bind {socketPath}");

        if (Syscalls.Listen(listenerFd, 1024) != 0)
            throw new Exception("failed to listen");

        int epollFd = Syscalls.EpollCreate1(Syscalls.EfdClOexec);
        if (epollFd < 0)
            throw new Exception("failed to create epoll");

        Syscalls.ConfigureEpollBusyPoll(epollFd);

        EpollCtlAdd(epollFd, listenerFd, ListenerToken, Syscalls.EpollIn, useEdgeTriggered);

        var slots = new ConnState[MaxClientFd];
        for (int i = 0; i < MaxClientFd; i++)
            slots[i].Fd = -1;

        onListenerReady();

        // Immediately accept any connections that arrived before we started monitoring
        int controlFd = -1;
        AcceptControl(listenerFd, epollFd, 1, ref controlFd, useEdgeTriggered);
        var events = stackalloc Syscalls.EpollEvent[MaxEvents];
        long totalReqs = 0;
        long heartbeatAt = heartbeatMs > 0 ? Environment.TickCount64 + heartbeatMs : long.MaxValue;

        Console.WriteLine("[EpollLoop] Single-threaded event loop running...");

        while (true)
        {
            try
            {
                int ready = Syscalls.EpollWait(epollFd, events, MaxEvents, epollTimeoutMs);
                if (ready < 0)
                {
                    int err = Marshal.GetLastPInvokeError();
                    if (err == Syscalls.Eintr)
                        continue;
                    Console.WriteLine($"[EpollLoop] epoll_wait error: {err}");
                    break;
                }

                var now = Environment.TickCount64;
                if (now >= heartbeatAt)
                {
                    heartbeatAt = now + heartbeatMs;
                    var active = 0;
                    for (int i = 0; i < MaxClientFd; i++)
                        if (slots[i].Fd != -1) active++;
                    Console.WriteLine($"[EpollLoop] Heartbeat: {totalReqs} total reqs, {active} active conns");
                }

                for (int i = 0; i < ready; i++)
                {
                    ulong token = events[i].Data;
                    uint revents = events[i].Events;

                    if (token == ListenerToken)
                    {
                        AcceptControl(listenerFd, epollFd, acceptBudget, ref controlFd, useEdgeTriggered);
                        continue;
                    }

                    if (token == ControlToken)
                    {
                        if ((revents & (uint)Syscalls.EpollIn) != 0)
                        {
                            DrainFds(controlFd, epollFd, recvFdBudget, slots, state, clientFdPreconfigured, useEdgeTriggered, ref totalReqs);
                        }
                        if ((revents & (uint)(Syscalls.EpollErr | Syscalls.EpollHup)) != 0)
                        {
                            Console.WriteLine("[EpollLoop] LB control connection closed");
                            EpollCtlDel(epollFd, controlFd);
                            Syscalls.Close(controlFd);
                            controlFd = -1;
                        }
                        continue;
                    }

                    int fd = (int)token;
                    if ((revents & (uint)(Syscalls.EpollErr | Syscalls.EpollHup | Syscalls.EpollRdHup)) != 0)
                    {
                        CloseConn(fd, epollFd, slots);
                        continue;
                    }

                    if ((revents & (uint)Syscalls.EpollIn) != 0)
                    {
                        OnReadable(fd, epollFd, slots, state, useEdgeTriggered, ref totalReqs);
                    }
                    if ((revents & (uint)Syscalls.EpollOut) != 0)
                    {
                        OnWritable(fd, epollFd, slots, state, useEdgeTriggered, ref totalReqs);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EpollLoop] CRITICAL ERROR: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    private static void AcceptControl(int listenerFd, int epollFd, int acceptBudget, ref int controlFd, bool useEdgeTriggered)
    {
        var accepted = 0;
        while (true)
        {
            if (acceptBudget > 0 && accepted >= acceptBudget)
                return;

            int fd = Syscalls.Accept4(listenerFd, Syscalls.SockNonBlock | Syscalls.SockClOexec);
            if (fd < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == Syscalls.Eagain)
                    return;
                Console.WriteLine($"[EpollLoop] control accept error: {err}");
                return;
            }

            if (controlFd == -1)
            {
                controlFd = fd;
                Console.WriteLine($"[EpollLoop] LB control connection accepted (fd: {controlFd})");
                EpollCtlAdd(epollFd, controlFd, ControlToken, Syscalls.EpollIn, useEdgeTriggered);
                accepted++;
                if (acceptBudget <= 0)
                    return;
            }
            else
            {
                Console.WriteLine($"[EpollLoop] Rejecting extra LB connection (fd: {fd})");
                Syscalls.Close(fd);
                accepted++;
            }
        }
    }

    private static void DrainFds(int controlFd, int epollFd, int recvFdBudget, ConnState[] slots, AppState state, bool clientFdPreconfigured, bool useEdgeTriggered, ref long totalReqs)
    {
        for (int i = 0; i < recvFdBudget; i++)
        {
            int fd = Syscalls.ReceivePassedFd(controlFd);
            if (fd < 0)
            {
                if (fd == Syscalls.ResultEagain) break;
                EpollCtlDel(epollFd, controlFd);
                break;
            }

            OnNewClient(fd, epollFd, slots, state, clientFdPreconfigured, useEdgeTriggered, ref totalReqs);
        }
    }

    private static void OnNewClient(int fd, int epollFd, ConnState[] slots, AppState state, bool clientFdPreconfigured, bool useEdgeTriggered, ref long totalReqs)
    {
        if (fd < 0 || fd >= MaxClientFd)
        {
            if (fd >= 0) Syscalls.Close(fd);
            return;
        }

        if (!clientFdPreconfigured)
        {
            if (!Syscalls.SetNonBlocking(fd))
            {
                Syscalls.Close(fd);
                return;
            }
            Syscalls.SetQuickAck(fd);
        }

        var buf = AllocBuffer();
        var (outcome, used) = GreedyRead(fd, buf, 0);

        switch (outcome)
        {
            case ReadOutcome.Data:
                DriveReading(fd, epollFd, slots, state, buf, used, false, useEdgeTriggered, ref totalReqs);
                break;
            case ReadOutcome.WouldBlock:
                ArmEpoll(epollFd, fd, Syscalls.EpollIn, false, useEdgeTriggered);
                slots[fd] = new ConnState { Fd = fd, Phase = ConnPhase.Reading, Buf = buf, Used = 0 };
                break;
            case ReadOutcome.Closed:
                FreeBuffer(buf);
                Syscalls.Close(fd);
                break;
        }
    }

    private static void OnReadable(int fd, int epollFd, ConnState[] slots, AppState state, bool useEdgeTriggered, ref long totalReqs)
    {
        if (fd < 0 || fd >= MaxClientFd) return;
        ref var s = ref slots[fd];
        if (s.Fd < 0 || s.Phase != ConnPhase.Reading) return;

        var buf = s.Buf;
        var used = s.Used;
        s.Buf = null!;
        s.Fd = -1;

        var (outcome, newUsed) = GreedyRead(fd, buf, used);
        switch (outcome)
        {
            case ReadOutcome.Data:
                DriveReading(fd, epollFd, slots, state, buf, newUsed, true, useEdgeTriggered, ref totalReqs);
                break;
            case ReadOutcome.WouldBlock:
                slots[fd] = new ConnState { Fd = fd, Phase = ConnPhase.Reading, Buf = buf, Used = newUsed };
                break;
            case ReadOutcome.Closed:
                FreeBuffer(buf);
                CloseConn(fd, epollFd, slots);
                break;
        }
    }

    private static void DriveReading(int fd, int epollFd, ConnState[] slots, AppState state, byte[] buf, int used, bool registered, bool useEdgeTriggered, ref long totalReqs)
    {
        while (true)
        {
            if (used >= SlotSize)
            {
                StartWrite(fd, epollFd, slots, buf, RawHttpResponses.BadRequest, 0, 0, false, registered, useEdgeTriggered, state);
                return;
            }

            var processed = 0;
            while (processed < used)
            {
                var slice = buf.AsSpan(processed, used - processed);
                if (FraudScoreFastPath.TryHandle(slice, state, out var response, out int consumed, out bool keepAlive))
                {
                    totalReqs++;
                    processed += consumed;
                    var leftoverOff = processed;
                    var leftoverLen = used - processed;
                    StartWrite(fd, epollFd, slots, buf, response, leftoverOff, leftoverLen, keepAlive, registered, useEdgeTriggered, state);
                    return;
                }

                switch (RawHttpParser.TryParse(slice, out var request, out int rawConsumed, out var reject))
                {
                    case RawHttpParseResult.Complete:
                        {
                            totalReqs++;
                            var rawResponse = RawHttpHandler.BuildResponse(request, state);
                            processed += rawConsumed;
                            var leftoverOff = processed;
                            var leftoverLen = used - processed;
                            StartWrite(fd, epollFd, slots, buf, rawResponse, leftoverOff, leftoverLen, request.KeepAlive, registered, useEdgeTriggered, state);
                            return;
                        }
                    case RawHttpParseResult.Reject:
                        {
                            processed += rawConsumed;
                            StartWrite(fd, epollFd, slots, buf, reject, 0, 0, false, registered, useEdgeTriggered, state);
                            return;
                        }
                    case RawHttpParseResult.NeedMore:
                        if (processed > 0)
                        {
                            Array.Copy(buf, processed, buf, 0, used - processed);
                            used -= processed;
                        }
                        ArmEpoll(epollFd, fd, Syscalls.EpollIn, registered, useEdgeTriggered);
                        slots[fd] = new ConnState { Fd = fd, Phase = ConnPhase.Reading, Buf = buf, Used = used };
                        return;
                }
            }

            if (processed > 0)
            {
                Array.Copy(buf, processed, buf, 0, used - processed);
                used -= processed;
            }
            ArmEpoll(epollFd, fd, Syscalls.EpollIn, registered, useEdgeTriggered);
            slots[fd] = new ConnState { Fd = fd, Phase = ConnPhase.Reading, Buf = buf, Used = used };
            return;
        }
    }

    private static void StartWrite(int fd, int epollFd, ConnState[] slots, byte[] buf, ReadOnlyMemory<byte> response, int leftoverOff, int leftoverLen, bool keepAlive, bool registered, bool useEdgeTriggered, AppState appState)
    {
        var stateObj = new ConnState
        {
            Fd = fd,
            Phase = ConnPhase.Writing,
            Buf = buf,
            Response = response,
            Written = 0,
            LeftoverOff = leftoverOff,
            LeftoverLen = leftoverLen,
            KeepAlive = keepAlive
        };

        var outcome = FinishWrite(fd, epollFd, slots, stateObj, registered, useEdgeTriggered);
        switch (outcome)
        {
            case WriteOutcome.DoneReading:
                if (stateObj.LeftoverLen > 0)
                {
                    var dummyReqs = 0L;
                    DriveReading(fd, epollFd, slots, appState, stateObj.Buf, stateObj.LeftoverLen, registered, useEdgeTriggered, ref dummyReqs);
                }
                else if (keepAlive)
                {
                    ArmEpoll(epollFd, fd, Syscalls.EpollIn, registered, useEdgeTriggered);
                    slots[fd] = new ConnState { Fd = fd, Phase = ConnPhase.Reading, Buf = stateObj.Buf, Used = 0 };
                }
                else
                {
                    ShutdownClient(fd, epollFd, registered);
                }
                break;
            case WriteOutcome.Wait:
                slots[fd] = stateObj;
                break;
            case WriteOutcome.Closed:
                break;
        }
    }

    private static void OnWritable(int fd, int epollFd, ConnState[] slots, AppState appState, bool useEdgeTriggered, ref long totalReqs)
    {
        if (fd < 0 || fd >= MaxClientFd) return;
        ref var s = ref slots[fd];
        if (s.Fd < 0 || s.Phase != ConnPhase.Writing) return;

        var stateObj = s;
        s.Fd = -1;

        var outcome = FinishWrite(fd, epollFd, slots, stateObj, true, useEdgeTriggered);
        switch (outcome)
        {
            case WriteOutcome.DoneReading:
                if (stateObj.LeftoverLen > 0)
                {
                    DriveReading(fd, epollFd, slots, appState, stateObj.Buf, stateObj.LeftoverLen, true, useEdgeTriggered, ref totalReqs);
                }
                else if (stateObj.KeepAlive)
                {
                    ArmEpoll(epollFd, fd, Syscalls.EpollIn, true, useEdgeTriggered);
                    slots[fd] = new ConnState { Fd = fd, Phase = ConnPhase.Reading, Buf = stateObj.Buf, Used = 0 };
                }
                else
                {
                    ShutdownClient(fd, epollFd, true);
                }
                break;
            case WriteOutcome.Wait:
                slots[fd] = stateObj;
                break;
            case WriteOutcome.Closed:
                break;
        }
    }

    private static WriteOutcome FinishWrite(int fd, int epollFd, ConnState[] slots, ConnState stateObj, bool registered, bool useEdgeTriggered)
    {
        var response = stateObj.Response.Span;
        var written = stateObj.Written;

        while (written < response.Length)
        {
            fixed (byte* p = response)
            {
                nint n = Syscalls.Send(fd, p + written, response.Length - written, Syscalls.MsgDontWait);
                if (n > 0)
                {
                    written += (int)n;
                    if (written == response.Length)
                    {
                        if (stateObj.LeftoverLen > 0)
                        {
                            Array.Copy(stateObj.Buf, stateObj.LeftoverOff, stateObj.Buf, 0, stateObj.LeftoverLen);
                            stateObj.LeftoverOff = 0;
                            return WriteOutcome.DoneReading;
                        }
                        if (stateObj.KeepAlive)
                        {
                            return WriteOutcome.DoneReading;
                        }
                        FreeBuffer(stateObj.Buf);
                        ShutdownClient(fd, epollFd, registered);
                        return WriteOutcome.Closed;
                    }
                    continue;
                }

                if (n == Syscalls.ResultEagain)
                {
                    stateObj.Written = written;
                    ArmEpoll(epollFd, fd, Syscalls.EpollOut, registered, useEdgeTriggered);
                    return WriteOutcome.Wait;
                }

                FreeBuffer(stateObj.Buf);
                ShutdownClient(fd, epollFd, registered);
                return WriteOutcome.Closed;
            }
        }

        return WriteOutcome.DoneReading;
    }

    private static void ShutdownClient(int fd, int epollFd, bool registered)
    {
        if (registered)
            EpollCtlDel(epollFd, fd);
        Syscalls.Close(fd);
    }

    private static void CloseConn(int fd, int epollFd, ConnState[] slots)
    {
        if (fd >= 0 && fd < MaxClientFd)
        {
            ref var s = ref slots[fd];
            if (s.Fd != -1)
            {
                FreeBuffer(s.Buf);
                s = default;
                s.Fd = -1;
            }
        }
        ShutdownClient(fd, epollFd, true);
    }

    private static (ReadOutcome Outcome, int Used) GreedyRead(int fd, byte[] buf, int used)
    {
        var hadData = used > 0;
        while (true)
        {
            if (used >= SlotSize)
                return (ReadOutcome.Data, used);

            nint n = Syscalls.Recv(fd, buf, used, SlotSize - used);
            if (n > 0)
            {
                used += (int)n;
                if (used >= SlotSize)
                    return (ReadOutcome.Data, used);
                continue;
            }

            if (n == 0)
            {
                if (used > 0 || hadData)
                    return (ReadOutcome.Data, used);
                return (ReadOutcome.Closed, 0);
            }

            int err = Marshal.GetLastPInvokeError();
            if (err == Syscalls.Eagain)
            {
                if (used > 0 || hadData)
                    return (ReadOutcome.Data, used);
                return (ReadOutcome.WouldBlock, 0);
            }

            return (ReadOutcome.Closed, 0);
        }
    }

    private static void ArmEpoll(int epollFd, int fd, int events, bool registered, bool useEdgeTriggered)
    {
        var ev = new Syscalls.EpollEvent
        {
            Events = BuildEvents(events, useEdgeTriggered),
            Data = (ulong)fd
        };
        if (registered)
            Syscalls.EpollCtl(epollFd, Syscalls.EpollCtlMod, fd, &ev);
        else
            Syscalls.EpollCtl(epollFd, Syscalls.EpollCtlAdd, fd, &ev);
    }

    private static void EpollCtlAdd(int epollFd, int fd, ulong token, int events, bool useEdgeTriggered)
    {
        var ev = new Syscalls.EpollEvent
        {
            Events = BuildEvents(events, useEdgeTriggered),
            Data = token
        };
        if (Syscalls.EpollCtl(epollFd, Syscalls.EpollCtlAdd, fd, &ev) != 0)
            Console.WriteLine($"[EpollLoop] epoll_ctl ADD failed: {Marshal.GetLastPInvokeError()}");
    }

    private static void EpollCtlDel(int epollFd, int fd)
    {
        Syscalls.EpollCtl(epollFd, Syscalls.EpollCtlDel, fd, null);
    }

    private static uint BuildEvents(int baseEvents, bool useEdgeTriggered)
    {
        var events = (uint)baseEvents;
        if (useEdgeTriggered)
            events |= unchecked((uint)Syscalls.EpollEtRaw);
        return events;
    }

    // Simple buffer pool using a thread-local stack
    [ThreadStatic] private static byte[][]? _bufferPool;
    [ThreadStatic] private static int _bufferPoolCount;
    private const int MaxPooledBuffers = 256;

    private static byte[] AllocBuffer()
    {
        if (_bufferPool != null && _bufferPoolCount > 0)
            return _bufferPool[--_bufferPoolCount];
        return new byte[SlotSize];
    }

    private static void FreeBuffer(byte[]? buf)
    {
        if (buf == null) return;
        _bufferPool ??= new byte[MaxPooledBuffers][];
        if (_bufferPoolCount < MaxPooledBuffers)
            _bufferPool[_bufferPoolCount++] = buf;
    }

    private static int GetEnvInt(string name, int defaultValue)
    {
        var val = Environment.GetEnvironmentVariable(name);
        return val != null && int.TryParse(val, out var n) ? n : defaultValue;
    }

    private static bool GetEnvBool(string name, bool defaultValue)
    {
        var val = Environment.GetEnvironmentVariable(name);
        if (val == null) return defaultValue;
        return val != "0";
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int umask(int mask);

    private static void Umask(int mask) => umask(mask);

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
}

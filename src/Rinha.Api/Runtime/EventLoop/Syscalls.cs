using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Rinha.Api.Runtime.EventLoop;

internal static unsafe class Syscalls
{
    internal const int EpollIn = 1;
    internal const int EpollErr = 8;
    internal const int EpollHup = 16;
    internal const int EpollRdHup = 0x2000;
    internal const int EpollEt = unchecked((int)0x80000000);
    internal const int EpollCtlAdd = 1;
    internal const int EpollCtlDel = 2;
    internal const int EpollClOexec = 0x80000;

    internal const int SolSocket = 1;
    internal const int ScmRights = 1;
    internal const int IpProtoTcp = 6;
    internal const int TcpQuickAck = 11;

    internal const int AfUnix = 1;
    internal const int SockStream = 1;
    internal const int SockClOexec = 0x80000;
    internal const int SockNonBlock = 0x800;
    internal const int MsgNoSignal = 0x4000;
    internal const int MsgDontWait = 0x40;
    internal const int Eagain = 11;
    internal const int Eintr = 4;
    internal const int ResultEagain = -2;
    internal const int EfdClOexec = 0x80000;
    private const int FGetFl = 3;
    private const int FSetFl = 4;
    private const int SigPipe = 13;
    private const nint SigIgn = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct EpollEvent
    {
        public uint Events;
        public ulong Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IOVec
    {
        public void* Base;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MsgHdr
    {
        public void* Name;
        public uint NameLen;
        public IOVec* Iov;
        public nuint IovLen;
        public void* Control;
        public nuint ControlLen;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SockAddrUn
    {
        public ushort SunFamily;
        public fixed byte SunPath[108];
    }

    public static int EpollCreate1(int flags) => epoll_create1(flags);

    public static int EpollCtl(int epfd, int op, int fd, EpollEvent* ev) =>
        epoll_ctl(epfd, op, fd, ev);

    public static int EpollWait(int epfd, EpollEvent* events, int maxEvents, int timeout) =>
        epoll_wait(epfd, events, maxEvents, timeout);

    public static nint Recv(int fd, byte* buf, int len, int flags = 0) =>
        recv(fd, buf, (nuint)len, flags);

    public static nint Send(int fd, byte* buf, int len, int flags = MsgNoSignal) =>
        send(fd, buf, (nuint)len, flags);

    public static int Close(int fd) => close(fd);

    public static int Accept4(int fd, int flags) =>
        accept4(fd, null, null, flags);

    public static int Socket(int domain, int type, int protocol) =>
        socket(domain, type, protocol);

    public static int Bind(int fd, SockAddrUn* addr, uint addrlen) =>
        bind(fd, addr, addrlen);

    public static int Listen(int fd, int backlog) => listen(fd, backlog);

    public static int Umask(int mask) => umask(mask);

    public static int SetsockoptInt(int fd, int level, int optname, int value)
    {
        int opt = value;
        return setsockopt(fd, level, optname, &opt, sizeof(int));
    }

    public static bool SetNonBlocking(int fd)
    {
        int flags = fcntl_getfl(fd, FGetFl);
        if (flags < 0)
            return false;

        return fcntl_setfl(fd, FSetFl, flags | SockNonBlock) >= 0;
    }

    public static void SetQuickAck(int fd) =>
        SetsockoptInt(fd, IpProtoTcp, TcpQuickAck, 1);

    public static void IgnoreSigPipe() =>
        signal(SigPipe, SigIgn);

    public static int EventFd() => eventfd(0, EfdClOexec);

    public static void NotifyEventFd(int fd)
    {
        ulong value = 1;
        write(fd, &value, sizeof(ulong));
    }

    public static void ConsumeEventFd(int fd)
    {
        ulong value = 0;
        read(fd, &value, sizeof(ulong));
    }

    public static int RecvMsg(int fd, MsgHdr* msg, int flags) =>
        recvmsg(fd, msg, flags);

    public static int ReceivePassedFd(int controlFd)
    {
        byte data = 0;
        Span<byte> control = stackalloc byte[64];
        control.Clear();

        byte* dataPtr = &data;
        fixed (byte* controlPtr = control)
        {
            IOVec iov = new() { Base = dataPtr, Length = 1 };
            MsgHdr message = new()
            {
                Iov = &iov,
                IovLen = 1,
                Control = controlPtr,
                ControlLen = (nuint)control.Length
            };

            nint received = RecvMsg(controlFd, &message, MsgDontWait);
            if (received < 0)
                return Marshal.GetLastPInvokeError() == Eagain ? ResultEagain : -1;
            if (received == 0)
                return -1;
        }

        nuint length = IntPtr.Size == 8
            ? (nuint)BinaryPrimitives.ReadUInt64LittleEndian(control)
            : BinaryPrimitives.ReadUInt32LittleEndian(control);

        if (length < 20)
            return -1;

        int level = BinaryPrimitives.ReadInt32LittleEndian(control.Slice(IntPtr.Size, 4));
        int type = BinaryPrimitives.ReadInt32LittleEndian(control.Slice(IntPtr.Size + 4, 4));
        if (level != SolSocket || type != ScmRights)
            return -1;

        return BinaryPrimitives.ReadInt32LittleEndian(control.Slice(16, 4));
    }

    public static int ReceivePassedFdBlocking(int controlFd)
    {
        byte data = 0;
        Span<byte> control = stackalloc byte[64];
        control.Clear();

        byte* dataPtr = &data;
        fixed (byte* controlPtr = control)
        {
            IOVec iov = new() { Base = dataPtr, Length = 1 };
            MsgHdr message = new()
            {
                Iov = &iov,
                IovLen = 1,
                Control = controlPtr,
                ControlLen = (nuint)control.Length
            };

            nint received = RecvMsg(controlFd, &message, 0);
            if (received <= 0)
                return -1;
        }

        nuint length = IntPtr.Size == 8
            ? (nuint)BinaryPrimitives.ReadUInt64LittleEndian(control)
            : BinaryPrimitives.ReadUInt32LittleEndian(control);

        if (length < 20)
            return -1;

        int level = BinaryPrimitives.ReadInt32LittleEndian(control.Slice(IntPtr.Size, 4));
        int type = BinaryPrimitives.ReadInt32LittleEndian(control.Slice(IntPtr.Size + 4, 4));
        if (level != SolSocket || type != ScmRights)
            return -1;

        return BinaryPrimitives.ReadInt32LittleEndian(control.Slice(16, 4));
    }

    public static int RecvSpan(int fd, Span<byte> buffer)
    {
        fixed (byte* ptr = buffer)
        {
            nint n = Recv(fd, ptr, buffer.Length, MsgDontWait);
            return n <= 0 ? (int)n : (int)n;
        }
    }

    public static bool SendAll(int fd, ReadOnlySpan<byte> payload)
    {
        while (!payload.IsEmpty)
        {
            fixed (byte* ptr = payload)
            {
                nint sent = Send(fd, ptr, payload.Length);
                if (sent <= 0)
                    return false;
                payload = payload[(int)sent..];
            }
        }

        return true;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int epoll_create1(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int epoll_ctl(int epfd, int op, int fd, EpollEvent* ev);

    [DllImport("libc", SetLastError = true)]
    private static extern int epoll_wait(int epfd, EpollEvent* events, int maxEvents, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern nint recv(int sockfd, byte* buf, nuint len, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern nint send(int sockfd, byte* buf, nuint len, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int accept4(int sockfd, void* addr, void* addrlen, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern int bind(int sockfd, SockAddrUn* addr, uint addrlen);

    [DllImport("libc", SetLastError = true)]
    private static extern int listen(int sockfd, int backlog);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(int socket, int level, int optionName, int* optionValue, int optionLen);

    [DllImport("libc", SetLastError = true)]
    private static extern int recvmsg(int sockfd, MsgHdr* msg, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int umask(int mask);

    [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int fcntl_getfl(int fd, int cmd);

    [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int fcntl_setfl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern nint signal(int signum, nint handler);

    [DllImport("libc", SetLastError = true)]
    private static extern int eventfd(uint initval, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, void* buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, void* buf, nuint count);
}

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Rinha.Api.Runtime;

internal static unsafe class FdPassing
{
    private const int SolSocket = 1;
    private const int ScmRights = 1;

    public static int Receive(Socket controlSocket)
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

            nint received = recvmsg((int)controlSocket.Handle, &message, 0);
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

    [DllImport("libc", SetLastError = true)]
    private static extern nint recvmsg(int sockfd, MsgHdr* msg, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct IOVec
    {
        public void* Base;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr
    {
        public void* Name;
        public uint NameLen;
        public IOVec* Iov;
        public nuint IovLen;
        public void* Control;
        public nuint ControlLen;
        public int Flags;
    }
}

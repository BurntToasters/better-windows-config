using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Bwc.Oracle.Capture;

// Memory inside another process (regedit), for messages whose lParam/pointer fields must be valid in the
// target's address space (TVM_GETITEMRECT, TVM_GETITEMW, LVM_GETITEMRECT, LVM_GETCOLUMNW, SB_GETTEXTW).
internal sealed class RemoteBuffer : IDisposable
{
    private const uint MemCommitReserve = 0x3000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    private readonly IntPtr _process;

    internal RemoteBuffer(IntPtr process, int size)
    {
        _process = process;
        Size = size;
        Address = Kernel.VirtualAllocEx(process, IntPtr.Zero, (IntPtr)size, MemCommitReserve, PageReadWrite);
        if (Address == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx in regedit failed.");
        }
    }

    internal IntPtr Address { get; }

    internal int Size { get; }

    internal void Write(byte[] data)
    {
        if (data.Length > Size)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "data larger than remote buffer");
        }

        if (!Kernel.WriteProcessMemory(_process, Address, data, (IntPtr)data.Length, out IntPtr written) || written.ToInt64() != data.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory into regedit failed.");
        }
    }

    internal byte[] Read(int count)
    {
        byte[] buffer = new byte[count];
        if (!Kernel.ReadProcessMemory(_process, Address, buffer, (IntPtr)count, out IntPtr read) || read.ToInt64() != count)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadProcessMemory from regedit failed.");
        }

        return buffer;
    }

    internal byte[] ReadAll() => Read(Size);

    // Reads memory that this tool did not allocate, such as the inherit-type table aclui keeps in regedit.
    internal static byte[] ReadRemote(IntPtr process, IntPtr address, int count)
    {
        byte[] buffer = new byte[count];
        if (!Kernel.ReadProcessMemory(process, address, buffer, (IntPtr)count, out IntPtr read) || read.ToInt64() != count)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"ReadProcessMemory at 0x{address:X} from regedit failed.");
        }

        return buffer;
    }

    public void Dispose()
    {
        Kernel.VirtualFreeEx(_process, Address, IntPtr.Zero, MemRelease);
    }

    private static class Kernel
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, IntPtr size, uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool VirtualFreeEx(IntPtr process, IntPtr address, IntPtr size, uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, IntPtr size, out IntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, IntPtr size, out IntPtr read);
    }
}

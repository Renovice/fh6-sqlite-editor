using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FH6SQLiteEditorNative;

internal sealed class GameProcess : IDisposable
{
    private GameProcess(IntPtr handle, int pid, ulong baseAddress, ulong imageSize)
    {
        Handle = handle;
        ProcessId = pid;
        BaseAddress = baseAddress;
        ImageSize = imageSize;
    }

    public IntPtr Handle { get; }
    public int ProcessId { get; }
    public ulong BaseAddress { get; }
    public ulong ImageSize { get; }

    public static GameProcess Open(string processName = "forzahorizon6")
    {
        var process = Process.GetProcessesByName(processName).FirstOrDefault()
            ?? throw new InvalidOperationException("Game process not found. Start FH6 first.");

        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessAllAccess, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not open {processName}.exe. Try running the editor as administrator if the game is elevated.");
        }

        try
        {
            var modules = new IntPtr[1];
            if (!NativeMethods.EnumProcessModules(handle, modules, (uint)(IntPtr.Size * modules.Length), out _) || modules[0] == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not resolve the game's main module.");
            }

            if (!NativeMethods.GetModuleInformation(handle, modules[0], out var moduleInfo, (uint)Marshal.SizeOf<NativeMethods.ModuleInfo>()))
            {
                throw new InvalidOperationException("Could not read game module information.");
            }

            return new GameProcess(handle, process.Id, (ulong)moduleInfo.BaseOfDll.ToInt64(), moduleInfo.SizeOfImage);
        }
        catch
        {
            NativeMethods.CloseHandle(handle);
            throw;
        }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(Handle);
        }
    }

    public byte[] ReadBytes(ulong address, int size)
    {
        var buffer = new byte[size];
        if (!NativeMethods.ReadProcessMemory(Handle, new IntPtr(unchecked((long)address)), buffer, (nuint)size, out var bytesRead) ||
            bytesRead != (nuint)size)
        {
            throw new InvalidOperationException($"Failed to read game memory at 0x{address:X}.");
        }
        return buffer;
    }

    public bool TryReadBytes(ulong address, byte[] buffer, out int bytesRead)
    {
        var ok = NativeMethods.ReadProcessMemory(Handle, new IntPtr(unchecked((long)address)), buffer, (nuint)buffer.Length, out var read);
        bytesRead = checked((int)read);
        return ok;
    }

    public int ReadInt32(ulong address) => BitConverter.ToInt32(ReadBytes(address, 4));

    public ulong ReadUInt64(ulong address) => BitConverter.ToUInt64(ReadBytes(address, 8));

    public string ReadMsvcString(ulong address)
    {
        var buffer = ReadBytes(address, 32);
        var length = BitConverter.ToUInt64(buffer, 16);
        var capacity = BitConverter.ToUInt64(buffer, 24);

        if (length == 0)
        {
            return string.Empty;
        }
        if (length > 4 * 1024 * 1024)
        {
            throw new InvalidOperationException("Game returned an invalid string length.");
        }

        if (capacity <= 15)
        {
            return Encoding.UTF8.GetString(buffer, 0, checked((int)length));
        }

        var heapPtr = BitConverter.ToUInt64(buffer, 0);
        var heap = ReadBytes(heapPtr, checked((int)length));
        return Encoding.UTF8.GetString(heap);
    }
}

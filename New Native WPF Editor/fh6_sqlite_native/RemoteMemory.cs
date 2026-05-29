namespace FH6SQLiteEditorNative;

internal sealed class RemoteAllocation : IDisposable
{
    public RemoteAllocation(IntPtr process, nuint size, uint protect)
    {
        Process = process;
        Size = size;
        Address = NativeMethods.VirtualAllocEx(
            process,
            IntPtr.Zero,
            size,
            NativeMethods.MemCommit | NativeMethods.MemReserve,
            protect);

        if (Address == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate memory inside the game process.");
        }
    }

    public IntPtr Process { get; }
    public IntPtr Address { get; private set; }
    public nuint Size { get; }
    public ulong UlongAddress => unchecked((ulong)Address.ToInt64());

    public void Write(byte[] data)
    {
        if (!NativeMethods.WriteProcessMemory(Process, Address, data, (nuint)data.Length, out var written) ||
            written != (nuint)data.Length)
        {
            throw new InvalidOperationException("Failed to write memory inside the game process.");
        }
    }

    public void Dispose()
    {
        if (Address != IntPtr.Zero)
        {
            NativeMethods.VirtualFreeEx(Process, Address, 0, NativeMethods.MemRelease);
            Address = IntPtr.Zero;
        }
    }
}

internal static class RemoteThread
{
    public static uint Execute(IntPtr process, IntPtr codeAddress, IntPtr parameter, uint timeoutMs = 15000)
    {
        var thread = NativeMethods.CreateRemoteThread(process, IntPtr.Zero, 0, codeAddress, parameter, 0, IntPtr.Zero);
        if (thread == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create remote thread inside the game process.");
        }

        try
        {
            var wait = NativeMethods.WaitForSingleObject(thread, timeoutMs);
            if (wait != NativeMethods.WaitObject0)
            {
                throw new TimeoutException("Remote SQL execution timed out.");
            }

            if (!NativeMethods.GetExitCodeThread(thread, out var exitCode))
            {
                throw new InvalidOperationException("Failed to read remote thread exit code.");
            }
            return exitCode;
        }
        finally
        {
            NativeMethods.CloseHandle(thread);
        }
    }
}

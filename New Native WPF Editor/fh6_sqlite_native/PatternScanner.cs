namespace FH6SQLiteEditorNative;

internal static class PatternScanner
{
    public static IReadOnlyList<ulong> ScanProcess(GameProcess process, string signature)
    {
        var (pattern, mask) = ParseSignature(signature);
        var results = new List<ulong>();
        var scanEnd = process.BaseAddress + process.ImageSize;
        var address = process.BaseAddress;
        var mbiSize = (nuint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();

        while (address < scanEnd)
        {
            var query = NativeMethods.VirtualQueryEx(
                process.Handle,
                new IntPtr(unchecked((long)address)),
                out var mbi,
                mbiSize);
            if (query == 0)
            {
                break;
            }

            var regionBase = unchecked((ulong)mbi.BaseAddress.ToInt64());
            var regionSize = (ulong)mbi.RegionSize;
            if (regionSize == 0)
            {
                break;
            }

            if (regionBase + regionSize <= process.BaseAddress)
            {
                address = regionBase + regionSize;
                continue;
            }

            var readable =
                mbi.State == NativeMethods.MemCommitState &&
                (mbi.Protect & NativeMethods.PageNoAccess) == 0 &&
                (mbi.Protect & NativeMethods.PageGuard) == 0;

            if (readable)
            {
                var readStart = Math.Max(regionBase, process.BaseAddress);
                var readEnd = Math.Min(regionBase + regionSize, scanEnd);
                var readSize = checked((int)(readEnd - readStart));
                if (readSize > 0)
                {
                    var buffer = new byte[readSize];
                    if (process.TryReadBytes(readStart, buffer, out var bytesRead) && bytesRead > 0)
                    {
                        foreach (var offset in ScanBuffer(buffer.AsSpan(0, bytesRead), pattern, mask))
                        {
                            results.Add(readStart + (ulong)offset);
                        }
                    }
                }
            }

            var next = regionBase + regionSize;
            if (next <= regionBase)
            {
                break;
            }
            address = next;
        }

        return results;
    }

    private static (byte[] Pattern, bool[] Mask) ParseSignature(string signature)
    {
        var tokens = signature.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var pattern = new byte[tokens.Length];
        var mask = new bool[tokens.Length];

        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is "?" or "??")
            {
                pattern[i] = 0;
                mask[i] = false;
            }
            else
            {
                pattern[i] = Convert.ToByte(tokens[i], 16);
                mask[i] = true;
            }
        }

        if (pattern.Length == 0)
        {
            throw new ArgumentException("Empty signature.", nameof(signature));
        }

        return (pattern, mask);
    }

    private static List<int> ScanBuffer(ReadOnlySpan<byte> data, byte[] pattern, bool[] mask)
    {
        var results = new List<int>();
        if (data.Length < pattern.Length)
        {
            return results;
        }

        var end = data.Length - pattern.Length;
        for (var i = 0; i <= end; i++)
        {
            var found = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (mask[j] && data[i + j] != pattern[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                results.Add(i);
            }
        }

        return results;
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace FH6SQLiteEditorNative;

public sealed class LiveTuningLimitEdit : INotifyPropertyChanged
{
    private double _currentMin;
    private double _currentMax;
    private double _targetMin;
    private double _targetMax;

    internal LiveTuningLimitEdit(LiveTuningLimitSpec spec)
    {
        Spec = spec;
        Key = spec.Key;
        Name = spec.Name;
        Notes = spec.Notes;
        CurrentMin = spec.DefaultMin;
        CurrentMax = spec.DefaultMax;
        TargetMin = spec.DefaultMin;
        TargetMax = spec.DefaultMax;
    }

    internal LiveTuningLimitSpec Spec { get; }
    public string Key { get; }
    public string Name { get; }
    public string Notes { get; }

    public double CurrentMin
    {
        get => _currentMin;
        set => SetField(ref _currentMin, value);
    }

    public double CurrentMax
    {
        get => _currentMax;
        set => SetField(ref _currentMax, value);
    }

    public double TargetMin
    {
        get => _targetMin;
        set => SetField(ref _targetMin, value);
    }

    public double TargetMax
    {
        get => _targetMax;
        set => SetField(ref _targetMax, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref double field, double value, [CallerMemberName] string? propertyName = null)
    {
        if (Math.Abs(field - value) < 0.000001)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed record LiveTuningLimitSpec(
    string Key,
    string Name,
    int MinIndex,
    int MaxIndex,
    double DefaultMin,
    double DefaultMax,
    string Notes);

internal sealed record LiveTuningMemoryBlock(ulong Address, string Kind, float[] Values);

internal sealed record LiveTuningScanResult(IReadOnlyList<LiveTuningMemoryBlock> Blocks, string Report);

internal sealed record LiveTuningAddressCache(int ProcessId, ulong BaseAddress, IReadOnlyList<ulong> Addresses);

internal static class LiveTuningLimitsPatcher
{
    private const uint MemPrivate = 0x20000;
    private const uint MemImage = 0x1000000;
    private const ulong MaxPrivateRegionBytes = 1UL * 1024UL * 1024UL;
    private const ulong KnownCarTuningBlockRva = 0xA7F89DC;
    private const int TuningFloatCount = 31;
    private const int MaxBlocksToPatch = 8;
    private const float MaxPatchedClampMagnitude = 100000f;
    private static readonly object CacheLock = new();
    private static LiveTuningAddressCache? _lastAddressCache;

    public static readonly IReadOnlyList<LiveTuningLimitSpec> Specs =
    [
        new("finalDrive", "Final Drive Ratio", 0, 1, 2.2, 6.1, "Global tuning clamp; transmission rows can still define installed ratios."),
        new("gearRatio", "Gear Ratio", 2, 3, 0.48, 6.0, "Global per-gear tuning clamp."),
        new("handbrake", "Handbrake Pressure", 4, 5, 0.0, 5.5, "Live global clamp."),
        new("absRelease", "ABS Release Point", 6, 7, 0.5, 5.0, "Live global clamp; may only matter when the game exposes this tune."),
        new("absDuration", "ABS Duration", 8, 9, 0.0, 1.0, "Live global clamp; may only matter when the game exposes this tune."),
        new("centerDiff", "Center Diff Split", 10, 11, 0.01, 0.99, "Center torque split clamp."),
        new("stmScale", "STM Scale", 12, 13, 0.0, 10.0, "Live global clamp; may only matter when the game exposes this tune."),
        new("tcsSlip", "TCS Slip", 14, 15, 0.1, 30.0, "Live global clamp; may only matter when the game exposes this tune."),
        new("tirePressure", "Tire Pressure (PSI)", 16, 17, 15.0, 55.0, "Internal clamp is PSI; metric/bar display is converted by the game."),
        new("camber", "Camber", 18, 19, -5.0, 5.0, "Alignment clamp not controlled by List_SpringDamperPhysics."),
        new("toe", "Toe In / Toe Out", 20, 21, -5.0, 5.0, "Alignment clamp; negative/positive values are both allowed by this range."),
        new("caster", "Caster", 22, 23, 1.0, 7.0, "Front caster tuning clamp."),
        new("brakePressure", "Brake Pressure", 24, 25, 0.0, 10.0, "Live global clamp."),
        new("diffAccel", "Diff Accel Limit", 26, 27, 0.0, 1.0, "Shared clamp for front/rear acceleration diff sliders; game displays this as 0-100%."),
        new("diffDecel", "Diff Decel Limit", 26, 28, 0.0, 1.0, "Shared clamp for front/rear deceleration diff sliders; game displays this as 0-100%.")
    ];

    public static ObservableCollection<LiveTuningLimitEdit> CreateDefaultRows()
    {
        return new ObservableCollection<LiveTuningLimitEdit>(Specs.Select(spec => new LiveTuningLimitEdit(spec)));
    }

    public static LiveTuningScanResult Scan(CancellationToken cancellationToken)
    {
        using var process = GameProcess.Open();
        var blocks = FindBlocks(process, cancellationToken);
        var report = BuildScanReport(process, blocks);
        return new LiveTuningScanResult(blocks, report);
    }

    public static string ScanIntoRows(ObservableCollection<LiveTuningLimitEdit> rows, CancellationToken cancellationToken)
    {
        var result = Scan(cancellationToken);
        if (result.Blocks.Count == 0)
        {
            return result.Report;
        }

        var block = result.Blocks[0];
        foreach (var row in rows)
        {
            row.CurrentMin = block.Values[row.Spec.MinIndex];
            row.CurrentMax = block.Values[row.Spec.MaxIndex];
            row.TargetMin = row.CurrentMin;
            row.TargetMax = row.CurrentMax;
        }

        return result.Report;
    }

    public static string Apply(ObservableCollection<LiveTuningLimitEdit> rows, CancellationToken cancellationToken)
    {
        using var process = GameProcess.Open();
        var blocks = FindBlocks(process, cancellationToken);
        if (blocks.Count == 0)
        {
            return "No live CarTuning clamp block was found. Open the game and enter a garage/tuning context, then scan again.";
        }
        if (blocks.Count > MaxBlocksToPatch)
        {
            return $"Found {blocks.Count} candidate blocks, which is too many to patch safely. No memory was changed.";
        }

        var valuesByIndex = new Dictionary<int, float>();
        foreach (var row in rows)
        {
            ValidateRow(row);
            valuesByIndex[row.Spec.MinIndex] = checked((float)row.TargetMin);
            valuesByIndex[row.Spec.MaxIndex] = checked((float)row.TargetMax);
        }

        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in valuesByIndex)
            {
                WriteFloat(process, block.Address + checked((ulong)(item.Key * 4)), item.Value);
            }
        }

        var refreshed = FindBlocks(process, cancellationToken);
        if (refreshed.Count > 0)
        {
            var block = refreshed[0];
            foreach (var row in rows)
            {
                row.CurrentMin = block.Values[row.Spec.MinIndex];
                row.CurrentMax = block.Values[row.Spec.MaxIndex];
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Patched {blocks.Count} live CarTuning clamp block(s).");
        foreach (var block in blocks)
        {
            sb.AppendLine($"  {block.Kind} 0x{block.Address:X}");
        }
        sb.AppendLine("Patch lasts until the game reloads/restarts these settings.");
        return sb.ToString().TrimEnd();
    }

    public static void RestoreDefaults(ObservableCollection<LiveTuningLimitEdit> rows)
    {
        foreach (var row in rows)
        {
            row.TargetMin = row.Spec.DefaultMin;
            row.TargetMax = row.Spec.DefaultMax;
        }
    }

    private static void ValidateRow(LiveTuningLimitEdit row)
    {
        if (!double.IsFinite(row.TargetMin) || !double.IsFinite(row.TargetMax))
        {
            throw new InvalidOperationException($"{row.Name}: enter finite numeric min/max values.");
        }
        if (row.TargetMax <= row.TargetMin)
        {
            throw new InvalidOperationException($"{row.Name}: max must be greater than min.");
        }
        if (Math.Abs(row.TargetMin) > 100000 || Math.Abs(row.TargetMax) > 100000)
        {
            throw new InvalidOperationException($"{row.Name}: value is too large for a tuning clamp.");
        }
    }

    private static List<LiveTuningMemoryBlock> FindBlocks(GameProcess process, CancellationToken cancellationToken)
    {
        var blocks = new List<LiveTuningMemoryBlock>();
        var seen = new HashSet<ulong>();
        var knownAddress = process.BaseAddress + KnownCarTuningBlockRva;
        if (TryReadBlock(process, knownAddress, out var knownValues) && LooksLikeKnownCarTuningBlock(knownValues))
        {
            blocks.Add(new LiveTuningMemoryBlock(knownAddress, "module", knownValues));
            seen.Add(knownAddress);
            AddCachedBlocks(process, blocks, seen);
            FindExactCopies(process, knownValues, blocks, seen, cancellationToken);
            return RememberAndSortBlocks(process, blocks);
        }

        AddCachedBlocks(process, blocks, seen);
        if (blocks.Count > 0)
        {
            return RememberAndSortBlocks(process, blocks);
        }

        var scanEnd = process.BaseAddress + process.ImageSize;
        var address = process.BaseAddress;
        var mbiSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();

        while (address < scanEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.VirtualQueryEx(
                    process.Handle,
                    new IntPtr(unchecked((long)address)),
                    out var mbi,
                    mbiSize) == 0)
            {
                break;
            }

            var regionBase = unchecked((ulong)mbi.BaseAddress.ToInt64());
            var regionSize = (ulong)mbi.RegionSize;
            if (regionSize == 0)
            {
                break;
            }

            if (IsReadable(mbi) && IsWritable(mbi.Protect))
            {
                ScanRegion(process, regionBase, regionSize, "module", blocks, seen, cancellationToken);
            }

            var next = regionBase + regionSize;
            if (next <= regionBase)
            {
                break;
            }
            address = next;
        }

        address = 0;
        while (address < 0x0000800000000000UL)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.VirtualQueryEx(
                    process.Handle,
                    new IntPtr(unchecked((long)address)),
                    out var mbi,
                    mbiSize) == 0)
            {
                break;
            }

            var regionBase = unchecked((ulong)mbi.BaseAddress.ToInt64());
            var regionSize = (ulong)mbi.RegionSize;
            if (regionSize == 0)
            {
                break;
            }

            var isMainModule = regionBase >= process.BaseAddress && regionBase < scanEnd;
            if (!isMainModule &&
                IsReadable(mbi) &&
                IsWritable(mbi.Protect) &&
                mbi.Type == MemPrivate &&
                regionSize <= MaxPrivateRegionBytes)
            {
                ScanRegion(process, regionBase, regionSize, mbi.Type == MemPrivate ? "runtime" : "image", blocks, seen, cancellationToken);
            }

            var next = regionBase + regionSize;
            if (next <= regionBase)
            {
                break;
            }
            address = next;
        }

        return RememberAndSortBlocks(process, blocks);
    }

    private static void AddCachedBlocks(GameProcess process, List<LiveTuningMemoryBlock> blocks, HashSet<ulong> seen)
    {
        LiveTuningAddressCache? cache;
        lock (CacheLock)
        {
            cache = _lastAddressCache;
        }

        if (cache is null ||
            cache.ProcessId != process.ProcessId ||
            cache.BaseAddress != process.BaseAddress)
        {
            return;
        }

        var knownAddress = process.BaseAddress + KnownCarTuningBlockRva;
        foreach (var address in cache.Addresses)
        {
            if (!seen.Add(address) ||
                !TryReadBlock(process, address, out var values) ||
                !LooksLikeKnownCarTuningBlock(values))
            {
                continue;
            }

            var kind = address == knownAddress ? "module cached" : "runtime cached";
            blocks.Add(new LiveTuningMemoryBlock(address, kind, values));
        }
    }

    private static List<LiveTuningMemoryBlock> RememberAndSortBlocks(GameProcess process, IEnumerable<LiveTuningMemoryBlock> blocks)
    {
        var sorted = blocks
            .OrderByDescending(block => block.Kind.Contains("module", StringComparison.OrdinalIgnoreCase))
            .ThenBy(block => block.Address)
            .ToList();

        if (sorted.Count > 0 && sorted.Count <= MaxBlocksToPatch)
        {
            lock (CacheLock)
            {
                _lastAddressCache = new LiveTuningAddressCache(
                    process.ProcessId,
                    process.BaseAddress,
                    sorted.Select(block => block.Address).Distinct().ToArray());
            }
        }

        return sorted;
    }

    private static void ScanRegion(
        GameProcess process,
        ulong regionBase,
        ulong regionSize,
        string kind,
        List<LiveTuningMemoryBlock> blocks,
        HashSet<ulong> seen,
        CancellationToken cancellationToken)
    {
        if (regionSize < TuningFloatCount * 4UL || regionSize > int.MaxValue)
        {
            return;
        }

        var buffer = new byte[regionSize];
        if (!process.TryReadBytes(regionBase, buffer, out var bytesRead) || bytesRead < TuningFloatCount * 4)
        {
            return;
        }

        var maxOffset = bytesRead - (TuningFloatCount * 4);
        for (var offset = 0; offset <= maxOffset; offset += 4)
        {
            if ((offset & 0x3FFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!TryReadCandidate(buffer, offset, out var values) || !LooksLikeDefaultCarTuningBlock(values))
            {
                continue;
            }

            var address = regionBase + checked((ulong)offset);
            if (seen.Add(address))
            {
                blocks.Add(new LiveTuningMemoryBlock(address, kind, values));
            }
        }
    }

    private static void FindExactCopies(
        GameProcess process,
        IReadOnlyList<float> knownValues,
        List<LiveTuningMemoryBlock> blocks,
        HashSet<ulong> seen,
        CancellationToken cancellationToken)
    {
        var needle = FloatsToBytes(knownValues);
        var scanEnd = process.BaseAddress + process.ImageSize;
        var address = process.BaseAddress;
        var mbiSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();

        while (address < scanEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.VirtualQueryEx(
                    process.Handle,
                    new IntPtr(unchecked((long)address)),
                    out var mbi,
                    mbiSize) == 0)
            {
                break;
            }

            var regionBase = unchecked((ulong)mbi.BaseAddress.ToInt64());
            var regionSize = (ulong)mbi.RegionSize;
            if (regionSize == 0)
            {
                break;
            }

            if (IsReadable(mbi) && IsWritable(mbi.Protect))
            {
                ScanExactRegion(process, regionBase, regionSize, "module", needle, blocks, seen, cancellationToken);
            }

            var next = regionBase + regionSize;
            if (next <= regionBase)
            {
                break;
            }
            address = next;
        }

        address = 0;
        while (address < 0x0000800000000000UL)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.VirtualQueryEx(
                    process.Handle,
                    new IntPtr(unchecked((long)address)),
                    out var mbi,
                    mbiSize) == 0)
            {
                break;
            }

            var regionBase = unchecked((ulong)mbi.BaseAddress.ToInt64());
            var regionSize = (ulong)mbi.RegionSize;
            if (regionSize == 0)
            {
                break;
            }

            var isMainModule = regionBase >= process.BaseAddress && regionBase < scanEnd;
            if (!isMainModule &&
                IsReadable(mbi) &&
                IsWritable(mbi.Protect) &&
                mbi.Type is MemPrivate or MemImage &&
                regionSize <= MaxPrivateRegionBytes)
            {
                ScanExactRegion(process, regionBase, regionSize, "runtime", needle, blocks, seen, cancellationToken);
            }

            var next = regionBase + regionSize;
            if (next <= regionBase)
            {
                break;
            }
            address = next;
        }
    }

    private static void ScanExactRegion(
        GameProcess process,
        ulong regionBase,
        ulong regionSize,
        string kind,
        byte[] needle,
        List<LiveTuningMemoryBlock> blocks,
        HashSet<ulong> seen,
        CancellationToken cancellationToken)
    {
        if (regionSize < (ulong)needle.Length || regionSize > int.MaxValue)
        {
            return;
        }

        var buffer = new byte[regionSize];
        if (!process.TryReadBytes(regionBase, buffer, out var bytesRead) || bytesRead < needle.Length)
        {
            return;
        }

        var maxOffset = bytesRead - needle.Length;
        for (var offset = 0; offset <= maxOffset; offset += 4)
        {
            if ((offset & 0x3FFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (!BytesEqual(buffer, offset, needle))
            {
                continue;
            }

            var address = regionBase + checked((ulong)offset);
            if (seen.Add(address) && TryReadBlock(process, address, out var values))
            {
                blocks.Add(new LiveTuningMemoryBlock(address, kind, values));
            }
        }
    }

    private static bool BytesEqual(byte[] haystack, int offset, byte[] needle)
    {
        for (var i = 0; i < needle.Length; i++)
        {
            if (haystack[offset + i] != needle[i])
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] FloatsToBytes(IReadOnlyList<float> values)
    {
        var bytes = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
        {
            BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 4);
        }

        return bytes;
    }

    private static bool TryReadBlock(GameProcess process, ulong address, out float[] values)
    {
        values = [];
        var bytes = new byte[TuningFloatCount * 4];
        if (!process.TryReadBytes(address, bytes, out var bytesRead) || bytesRead != bytes.Length)
        {
            return false;
        }

        return TryReadCandidate(bytes, 0, out values);
    }

    private static bool TryReadCandidate(byte[] buffer, int offset, out float[] values)
    {
        values = new float[TuningFloatCount];
        for (var i = 0; i < values.Length; i++)
        {
            var value = BitConverter.ToSingle(buffer, offset + i * 4);
            if (!float.IsFinite(value))
            {
                return false;
            }
            values[i] = value;
        }

        return true;
    }

    private static bool LooksLikeKnownCarTuningBlock(IReadOnlyList<float> v)
    {
        return v.Count == TuningFloatCount &&
               v.All(value => float.IsFinite(value) && Math.Abs(value) <= MaxPatchedClampMagnitude) &&
               IsMinMax(v[0], v[1]) &&
               IsMinMax(v[2], v[3]) &&
               IsMinMax(v[4], v[5]) &&
               IsMinMax(v[6], v[7]) &&
               IsMinMax(v[8], v[9]) &&
               IsMinMax(v[10], v[11]) &&
               IsMinMax(v[12], v[13]) &&
               IsMinMax(v[14], v[15]) &&
               IsMinMax(v[16], v[17]) &&
               IsMinMax(v[18], v[19]) &&
               IsMinMax(v[20], v[21]) &&
               IsMinMax(v[22], v[23]) &&
               IsMinMax(v[24], v[25]) &&
               IsMinMax(v[26], v[27]) &&
               v[28] > v[26] &&
               IsMinMax(v[29], v[30]) &&
               // Fuel load is not exposed in the live UI, and tire pressure uses PSI internally.
               // These broad anchors let patched camber/toe/caster values be loose without accepting random float blocks.
               v[16] >= 1f &&
               v[17] > v[16] &&
               v[17] <= 250f &&
               v[29] >= -0.01f &&
               v[30] <= 2f;
    }

    private static bool LooksLikeDefaultCarTuningBlock(IReadOnlyList<float> v)
    {
        return LooksLikeKnownCarTuningBlock(v) &&
               Near(v[0], 2.2f, 0.35f) &&
               Near(v[1], 6.1f, 0.35f) &&
               Near(v[2], 0.48f, 0.08f) &&
               Near(v[3], 6.0f, 0.35f) &&
               Near(v[10], 0.01f, 0.03f) &&
               Near(v[11], 0.99f, 0.05f) &&
               Near(v[16], 15f, 1f) &&
               Near(v[17], 55f, 1f) &&
               Near(v[24], 0f, 0.05f) &&
               Near(v[25], 10f, 0.5f);
    }

    private static bool Near(float value, float target, float tolerance)
    {
        return Math.Abs(value - target) <= tolerance;
    }

    private static bool IsMinMax(float min, float max)
    {
        return max > min &&
               Math.Abs(min) <= MaxPatchedClampMagnitude &&
               Math.Abs(max) <= MaxPatchedClampMagnitude;
    }

    private static string BuildScanReport(GameProcess process, IReadOnlyList<LiveTuningMemoryBlock> blocks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Found forzahorizon6.exe PID {process.ProcessId}");
        sb.AppendLine($"Found {blocks.Count} live CarTuning clamp block(s).");
        foreach (var block in blocks)
        {
            sb.AppendLine($"  {block.Kind} 0x{block.Address:X}");
        }
        if (blocks.Count == 0)
        {
            sb.AppendLine("Open the game and enter a garage/tuning context, then scan again.");
        }
        return sb.ToString().TrimEnd();
    }

    private static void WriteFloat(GameProcess process, ulong address, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (!NativeMethods.WriteProcessMemory(
                process.Handle,
                new IntPtr(unchecked((long)address)),
                bytes,
                (nuint)bytes.Length,
                out var written) ||
            written != (nuint)bytes.Length)
        {
            throw new InvalidOperationException($"Failed to write tuning clamp at 0x{address:X}.");
        }
    }

    private static bool IsReadable(NativeMethods.MemoryBasicInformation mbi)
    {
        return mbi.State == NativeMethods.MemCommitState &&
               (mbi.Protect & NativeMethods.PageNoAccess) == 0 &&
               (mbi.Protect & NativeMethods.PageGuard) == 0;
    }

    private static bool IsWritable(uint protect)
    {
        return (protect & (NativeMethods.PageReadWrite |
                           NativeMethods.PageExecuteReadWrite |
                           NativeMethods.PageExecuteWriteCopy)) != 0;
    }

    public static string FormatRows(IEnumerable<LiveTuningLimitEdit> rows)
    {
        return string.Join(
            Environment.NewLine,
            rows.Select(row =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{row.Name}: {row.CurrentMin:0.###}..{row.CurrentMax:0.###} -> {row.TargetMin:0.###}..{row.TargetMax:0.###}")));
    }
}

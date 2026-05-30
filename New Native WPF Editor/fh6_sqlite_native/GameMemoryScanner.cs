using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace FH6SQLiteEditorNative;

internal sealed record GameStringHit(ulong Address, string EncodingName, int Score, int Count, string Text);

internal sealed record ExecutableRegion(ulong Address, byte[] Bytes);

internal sealed record HighValueString(GameStringHit Hit, string Reason);

internal sealed record NearbyString(ulong Address, string EncodingName, string Text);

internal sealed record CodeReference(ulong DisplacementAddress, string Kind);

internal static class GameMemoryScanner
{
    private const int MaxStringLength = 1200;
    private const int MaxHits = 220;
    private const int MaxHighValueHits = 10;
    private const int MaxXrefsPerHit = 16;
    private const int MaxCodeClusters = 7;
    private const int NearbyBytesBefore = 0x500;
    private const int NearbyBytesAfter = 0x900;

    public static string ScanUpgradeFilterCandidates(
        string selectedTable,
        IReadOnlyCollection<string> partNames,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        using var process = GameProcess.Open();
        log.Report($"Found forzahorizon6.exe PID {process.ProcessId}");
        log.Report($"Scanning game image 0x{process.BaseAddress:X}-0x{process.BaseAddress + process.ImageSize:X} for upgrade/menu filter strings...");

        var keywords = BuildKeywords(selectedTable, partNames);
        var hits = new Dictionary<string, GameStringHit>(StringComparer.OrdinalIgnoreCase);
        var executableRegions = new List<ExecutableRegion>();
        var scanEnd = process.BaseAddress + process.ImageSize;
        var address = process.BaseAddress;
        var mbiSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();

        while (address < scanEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            var readable = IsReadable(mbi);

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
                        var span = buffer.AsSpan(0, bytesRead);
                        AddStrings(hits, span, readStart, "ascii", keywords);
                        AddUtf16Strings(hits, span, readStart, keywords);

                        if (IsExecutable(mbi.Protect))
                        {
                            executableRegions.Add(new ExecutableRegion(readStart, buffer[..bytesRead]));
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

        var ordered = hits.Values
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Count)
            .ThenBy(h => h.Address)
            .Take(MaxHits)
            .ToList();

        log.Report("Grouping high-value hits and scanning executable code for references...");
        return BuildReport(process, selectedTable, partNames, keywords, ordered, executableRegions, cancellationToken);
    }

    private static List<string> BuildKeywords(string selectedTable, IReadOnlyCollection<string> partNames)
    {
        var keywords = new List<string>
        {
            selectedTable,
            selectedTable.Replace("List_UpgradeEngine", "", StringComparison.OrdinalIgnoreCase),
            "List_UpgradeEngine",
            "Data_UpgradePart",
            "UpgradeTypes",
            "Upgrades",
            "UpgradeAreaForUpgradeType",
            "List_Aspiration",
            "PartName",
            "TypeId",
            "Level",
            "LIMIT",
            "WHERE",
            "ORDER BY",
            "DisplayOrder",
            "SortOrder",
            "Engine"
        };

        foreach (var partName in partNames)
        {
            keywords.Add(partName);
            keywords.Add(HumanPartKeyword(partName));
        }

        return keywords
            .Where(k => !string.IsNullOrWhiteSpace(k) && k.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string HumanPartKeyword(string partName)
    {
        return partName switch
        {
            "TwinTurbo" => "Twin Turbo",
            "SingleTurbo" => "Single Turbo",
            "QuadTurbo" => "Quad Turbo",
            "SuperchargerDSC" => "Supercharger",
            "SuperchargerCSC" => "Supercharger",
            _ => partName
        };
    }

    private static void AddStrings(
        Dictionary<string, GameStringHit> hits,
        ReadOnlySpan<byte> buffer,
        ulong baseAddress,
        string encodingName,
        IReadOnlyList<string> keywords)
    {
        var start = -1;
        for (var i = 0; i <= buffer.Length; i++)
        {
            var b = i < buffer.Length ? buffer[i] : (byte)0;
            if (i < buffer.Length && IsAsciiStringByte(b))
            {
                if (start < 0)
                {
                    start = i;
                }
                continue;
            }

            if (start >= 0)
            {
                var length = i - start;
                if (length >= 6)
                {
                    var slice = buffer.Slice(start, Math.Min(length, MaxStringLength));
                    var text = Encoding.UTF8.GetString(slice);
                    AddHit(hits, baseAddress + (ulong)start, encodingName, text, keywords);
                }
                start = -1;
            }
        }
    }

    private static void AddUtf16Strings(
        Dictionary<string, GameStringHit> hits,
        ReadOnlySpan<byte> buffer,
        ulong baseAddress,
        IReadOnlyList<string> keywords)
    {
        var start = -1;
        var i = 0;
        while (i + 1 < buffer.Length)
        {
            var ch = (char)(buffer[i] | (buffer[i + 1] << 8));
            if (IsTextChar(ch))
            {
                if (start < 0)
                {
                    start = i;
                }
                i += 2;
                continue;
            }

            if (start >= 0)
            {
                var length = i - start;
                if (length >= 12)
                {
                    var byteLength = Math.Min(length, MaxStringLength * 2);
                    var text = Encoding.Unicode.GetString(buffer.Slice(start, byteLength));
                    AddHit(hits, baseAddress + (ulong)start, "utf16", text, keywords);
                }
                start = -1;
            }
            i += 2;
        }
    }

    private static void AddHit(
        Dictionary<string, GameStringHit> hits,
        ulong address,
        string encodingName,
        string text,
        IReadOnlyList<string> keywords)
    {
        text = CleanText(text);
        if (text.Length < 6)
        {
            return;
        }

        var score = Score(text, keywords);
        if (score < 5)
        {
            return;
        }

        var key = text.Length > 260 ? text[..260] : text;
        if (hits.TryGetValue(key, out var existing))
        {
            hits[key] = existing with
            {
                Score = Math.Max(existing.Score, score),
                Count = existing.Count + 1
            };
            return;
        }

        hits[key] = new GameStringHit(address, encodingName, score, 1, text);
    }

    private static int Score(string text, IReadOnlyList<string> keywords)
    {
        var score = 0;
        foreach (var keyword in keywords)
        {
            if (!text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            score += keyword switch
            {
                "Level" or "LIMIT" or "WHERE" or "ORDER BY" => 3,
                "Upgrades" or "UpgradeTypes" or "Data_UpgradePart" or "List_Aspiration" => 5,
                "TypeId" or "PartName" or "DisplayOrder" or "SortOrder" => 3,
                _ when keyword.StartsWith("List_UpgradeEngine", StringComparison.OrdinalIgnoreCase) => 7,
                _ => 4
            };
        }

        var looksSql = text.Contains("SELECT", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("FROM", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("WHERE", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase);
        if (looksSql)
        {
            score += 5;
        }

        if (text.Contains("Level", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("<=", StringComparison.Ordinal) ||
             text.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("max", StringComparison.OrdinalIgnoreCase)))
        {
            score += 6;
        }

        return score;
    }

    private static string BuildReport(
        GameProcess process,
        string selectedTable,
        IReadOnlyCollection<string> partNames,
        IReadOnlyList<string> keywords,
        IReadOnlyList<GameStringHit> hits,
        IReadOnlyList<ExecutableRegion> executableRegions,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Game Upgrade Filter String Scan");
        sb.AppendLine($"PID: {process.ProcessId}");
        sb.AppendLine($"Image: 0x{process.BaseAddress:X}-0x{process.BaseAddress + process.ImageSize:X}");
        sb.AppendLine($"Selected table: {selectedTable}");
        sb.AppendLine($"PartName(s): {(partNames.Count == 0 ? "(unknown)" : string.Join(", ", partNames))}");
        sb.AppendLine($"Keywords: {string.Join(", ", keywords)}");
        sb.AppendLine();
        sb.AppendLine("How to use this:");
        sb.AppendLine("  - Strong hits are static strings in the game image that mention upgrade tables, levels, limits, or menu metadata.");
        sb.AppendLine("  - If no exact cap query appears here, the game may build SQL dynamically or enforce the cap in code after reading DB rows.");
        sb.AppendLine("  - High-value hits include nearby strings and possible RIP-relative code references. These are patch candidates, not patches by themselves.");
        sb.AppendLine();

        if (hits.Count == 0)
        {
            sb.AppendLine("No relevant static strings were found in the main game image.");
            sb.AppendLine("Next step would be a runtime hook around the game's DB query function or SQLite prepare/step path.");
            return sb.ToString();
        }

        var highValue = SelectHighValueHits(hits, selectedTable);
        var xrefs = FindPossibleXrefs(executableRegions, highValue.Select(h => h.Hit).ToList(), cancellationToken);

        sb.AppendLine("High-value hits");
        if (highValue.Count == 0)
        {
            sb.AppendLine("  No high-value upgrade view/level strings were found. The raw hits below are still useful.");
        }
        else
        {
            foreach (var item in highValue)
            {
                var hit = item.Hit;
                sb.AppendLine($"0x{hit.Address:X} [{hit.EncodingName}] score {hit.Score}, seen {hit.Count}");
                sb.AppendLine($"  Why: {item.Reason}");
                sb.AppendLine("  Text: " + hit.Text);

                if (xrefs.TryGetValue(hit.Address, out var refs) && refs.Count > 0)
                {
                    sb.AppendLine("  Possible code refs:");
                    foreach (var reference in refs)
                    {
                        var likelyInstruction = reference.DisplacementAddress >= 3
                            ? reference.DisplacementAddress - 3
                            : reference.DisplacementAddress;
                        sb.AppendLine($"    {reference.Kind}@{FormatAddress(process, reference.DisplacementAddress)} (instruction near {FormatAddress(process, likelyInstruction)})");
                    }
                }
                else
                {
                    sb.AppendLine("  Possible code refs: none found with simple RIP-relative scan");
                }

                var nearby = ReadNearbyStrings(process, hit).Take(14).ToList();
                if (nearby.Count > 0)
                {
                    sb.AppendLine("  Nearby strings:");
                    foreach (var nearbyString in nearby)
                    {
                        sb.AppendLine($"    0x{nearbyString.Address:X} [{nearbyString.EncodingName}] {nearbyString.Text}");
                    }
                }

                sb.AppendLine();
            }
        }

        AppendCodeClusters(sb, process, highValue, xrefs);

        sb.AppendLine("Interpretation:");
        sb.AppendLine("  - If extra rows and metadata exist but the game still hides them, focus on code that creates %sView_UpgradeParts / %sView_UpgradeTypes or iterates Level values.");
        sb.AppendLine("  - Strings like SELECT COALESCE(MAX(Level), -1) and SELECT * FROM %s WHERE ... Level = %d point to native level-loop/query code.");
        sb.AppendLine("  - A hardcoded cap is likely near code refs to those strings, not necessarily visible as SQL text.");
        sb.AppendLine();

        sb.AppendLine($"Hits: {hits.Count.ToString(CultureInfo.InvariantCulture)} shown");
        foreach (var hit in hits)
        {
            sb.AppendLine($"0x{hit.Address:X} [{hit.EncodingName}] score {hit.Score}, seen {hit.Count}");
            sb.AppendLine("  " + hit.Text);
        }

        return sb.ToString();
    }

    private static List<HighValueString> SelectHighValueHits(IReadOnlyList<GameStringHit> hits, string selectedTable)
    {
        var selected = new List<(int Priority, HighValueString Item)>();

        foreach (var hit in hits)
        {
            var text = hit.Text;
            var reason = "";
            var priority = 0;

            if (text.Contains("CREATE VIEW %sView_UpgradeTypes", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Upgrade type/menu view builder. This decides which part families appear in the menu.";
                priority = 100;
            }
            else if (text.Contains("%sView_UpgradeParts", StringComparison.OrdinalIgnoreCase) &&
                     text.Contains("UpgradeTypes", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Join between generated upgrade parts and menu metadata.";
                priority = 95;
            }
            else if (text.Contains("PartTable.IsStock = 1 AND Upgrades.Level = 0", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Actual part tile query. It maps stock rows to metadata level 0 and upgrade rows to matching levels.";
                priority = 90;
            }
            else if (text.Contains("SELECT COALESCE(MAX(Level), -1)", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Likely max-level discovery for a part table.";
                priority = 88;
            }
            else if (text.Contains("SELECT * FROM %s WHERE EngineID", StringComparison.OrdinalIgnoreCase) &&
                     text.Contains("Level = %d", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Parameterized engine-part lookup by exact level. The native caller probably controls the level loop.";
                priority = 86;
            }
            else if (text.Contains("SELECT * FROM %s WHERE Ordinal", StringComparison.OrdinalIgnoreCase) &&
                     text.Contains("Level = 3", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Hardcoded level-3 lookup. Useful because several body/aero upgrade paths stop at fixed levels.";
                priority = 80;
            }
            else if (text.Contains("ORDER BY PartTable.IsStock DESC, PartTable.Level", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Final tile ordering by stock state and level.";
                priority = 76;
            }
            else if (text.Contains("MAX( PartLevel )", StringComparison.OrdinalIgnoreCase) &&
                     text.Contains("UpgradeWizardParts", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Upgrade wizard max-part-level lookup. May be separate from manual upgrade menu, but still level-cap relevant.";
                priority = 72;
            }
            else if (text.Contains(selectedTable, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Selected raw part table string. Usually a support clue, less useful than query-builder strings.";
                priority = 50;
            }

            if (priority > 0)
            {
                selected.Add((priority, new HighValueString(hit, reason)));
            }
        }

        return selected
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.Item.Hit.Score)
            .ThenBy(item => item.Item.Hit.Address)
            .Select(item => item.Item)
            .Take(MaxHighValueHits)
            .ToList();
    }

    private static Dictionary<ulong, List<CodeReference>> FindPossibleXrefs(
        IReadOnlyList<ExecutableRegion> executableRegions,
        IReadOnlyList<GameStringHit> hits,
        CancellationToken cancellationToken)
    {
        var result = hits.ToDictionary(hit => hit.Address, _ => new List<CodeReference>());
        var rawFallback = hits.ToDictionary(hit => hit.Address, _ => new List<CodeReference>());
        if (hits.Count == 0 || executableRegions.Count == 0)
        {
            return result;
        }

        var ranges = hits
            .Select(hit =>
            {
                var textBytes = hit.EncodingName.Equals("utf16", StringComparison.OrdinalIgnoreCase)
                    ? hit.Text.Length * 2
                    : hit.Text.Length;
                var length = (ulong)Math.Clamp(textBytes + 8, 16, 0x1000);
                return (hit.Address, End: hit.Address + length);
            })
            .ToList();

        foreach (var region in executableRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = region.Bytes;
            for (var offset = 0; offset + 4 <= bytes.Length; offset++)
            {
                if ((offset & 0xFFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var displacement = BitConverter.ToInt32(bytes, offset);
                var displacementAddress = region.Address + (ulong)offset;
                var nextInstruction = unchecked((long)(displacementAddress + 4));
                var targetSigned = nextInstruction + displacement;
                if (targetSigned < 0)
                {
                    continue;
                }

                var target = unchecked((ulong)targetSigned);
                foreach (var range in ranges)
                {
                    if (target < range.Address || target >= range.End)
                    {
                        continue;
                    }

                    var kind = ClassifyReference(bytes, offset);
                    var refs = kind is null ? rawFallback[range.Address] : result[range.Address];
                    if (refs.Count < MaxXrefsPerHit && refs.All(existing => existing.DisplacementAddress != displacementAddress))
                    {
                        refs.Add(new CodeReference(displacementAddress, kind ?? "raw-disp"));
                    }
                }
            }
        }

        foreach (var key in result.Keys.ToList())
        {
            if (result[key].Count == 0)
            {
                result[key].AddRange(rawFallback[key]);
            }
        }

        foreach (var refs in result.Values)
        {
            refs.Sort((left, right) => left.DisplacementAddress.CompareTo(right.DisplacementAddress));
        }

        return result;
    }

    private static string? ClassifyReference(byte[] bytes, int displacementOffset)
    {
        if (displacementOffset >= 1)
        {
            var modRm = bytes[displacementOffset - 1];
            if ((modRm & 0xC7) == 0x05)
            {
                return "rip";
            }

            var opcode = bytes[displacementOffset - 1];
            if (opcode is 0xE8 or 0xE9)
            {
                return "rel32";
            }
        }

        if (displacementOffset >= 2 &&
            bytes[displacementOffset - 2] == 0x0F &&
            bytes[displacementOffset - 1] is >= 0x80 and <= 0x8F)
        {
            return "jcc32";
        }

        return null;
    }

    private static void AppendCodeClusters(
        StringBuilder sb,
        GameProcess process,
        IReadOnlyList<HighValueString> highValue,
        IReadOnlyDictionary<ulong, List<CodeReference>> xrefs)
    {
        var interestingRefs = highValue
            .Where(item => IsCodeWindowInteresting(item.Hit.Text))
            .SelectMany(item =>
            {
                if (!xrefs.TryGetValue(item.Hit.Address, out var refs))
                {
                    return Array.Empty<(CodeReference Reference, GameStringHit Hit, string Reason)>();
                }

                return refs.Select(reference => (Reference: reference, Hit: item.Hit, item.Reason));
            })
            .OrderBy(item => item.Reference.DisplacementAddress)
            .ToList();

        sb.AppendLine("Code reference clusters");
        if (interestingRefs.Count == 0)
        {
            sb.AppendLine("  No code refs were found for the high-value query strings.");
            sb.AppendLine();
            return;
        }

        var clusters = new List<List<(CodeReference Reference, GameStringHit Hit, string Reason)>>();
        foreach (var item in interestingRefs)
        {
            if (clusters.Count == 0 ||
                item.Reference.DisplacementAddress - clusters[^1][^1].Reference.DisplacementAddress > 0x220)
            {
                clusters.Add([item]);
            }
            else
            {
                clusters[^1].Add(item);
            }
        }

        foreach (var cluster in clusters
                     .OrderByDescending(ScoreCodeCluster)
                     .ThenBy(cluster => cluster[0].Reference.DisplacementAddress)
                     .Take(MaxCodeClusters))
        {
            var first = cluster[0].Reference.DisplacementAddress;
            var last = cluster[^1].Reference.DisplacementAddress;
            var clusterStart = first > 0x70 ? first - 0x70 : first;
            var clusterEnd = last + 0xB0;
            var size = checked((int)Math.Min(clusterEnd - clusterStart, 0x420UL));
            var buffer = new byte[size];

            sb.AppendLine($"  Cluster {FormatAddress(process, first)} - {FormatAddress(process, last)}");
            sb.AppendLine("  Referenced strings:");
            foreach (var target in cluster
                         .GroupBy(item => item.Hit.Address)
                         .Select(group => group.First())
                         .Take(5))
            {
                sb.AppendLine($"    {ShortTargetName(target.Hit.Text)}");
            }

            if (!process.TryReadBytes(clusterStart, buffer, out var bytesRead) || bytesRead <= 0)
            {
                sb.AppendLine("  Could not read this code window.");
                sb.AppendLine();
                continue;
            }

            var markers = cluster
                .Select(item => item.Reference.DisplacementAddress)
                .Where(address => address >= clusterStart && address < clusterStart + (ulong)bytesRead)
                .ToHashSet();

            var patterns = FindInterestingCodePatterns(buffer, bytesRead, clusterStart, process.BaseAddress).Take(16).ToList();
            if (patterns.Count > 0)
            {
                sb.AppendLine("  Nearby compare/jump/call hints:");
                foreach (var pattern in patterns)
                {
                    sb.AppendLine("    " + pattern);
                }
            }

            sb.AppendLine("  Code bytes (* line contains a string ref displacement):");
            foreach (var line in FormatHexWindow(buffer, bytesRead, clusterStart, process.BaseAddress, markers).Take(34))
            {
                sb.AppendLine("    " + line);
            }

            sb.AppendLine();
        }
    }

    private static bool IsCodeWindowInteresting(string text)
    {
        return text.Contains("SELECT COALESCE(MAX(Level), -1)", StringComparison.OrdinalIgnoreCase) ||
               (text.Contains("SELECT * FROM %s WHERE EngineID", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("Level = %d", StringComparison.OrdinalIgnoreCase)) ||
               text.Contains("PartTable.IsStock = 1 AND Upgrades.Level = 0", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("CREATE VIEW %sView_UpgradeTypes", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ORDER BY PartTable.IsStock DESC, PartTable.Level", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreCodeCluster(List<(CodeReference Reference, GameStringHit Hit, string Reason)> cluster)
    {
        var score = cluster.Select(item => item.Hit.Address).Distinct().Count() * 20 + cluster.Count;
        if (cluster.Any(item => item.Reference.Kind.Equals("rip", StringComparison.OrdinalIgnoreCase)))
        {
            score += 30;
        }
        if (cluster.Any(item => item.Hit.Text.Contains("SELECT * FROM %s WHERE EngineID", StringComparison.OrdinalIgnoreCase)))
        {
            score += 20;
        }
        if (cluster.Any(item => item.Hit.Text.Contains("SELECT COALESCE(MAX(Level)", StringComparison.OrdinalIgnoreCase)))
        {
            score += 15;
        }

        return score;
    }

    private static string ShortTargetName(string text)
    {
        if (text.Contains("CREATE VIEW %sView_UpgradeTypes", StringComparison.OrdinalIgnoreCase))
        {
            return "CREATE VIEW %sView_UpgradeTypes";
        }
        if (text.Contains("PartTable.IsStock = 1 AND Upgrades.Level = 0", StringComparison.OrdinalIgnoreCase))
        {
            return "part tile query stock/level join";
        }
        if (text.Contains("SELECT COALESCE(MAX(Level), -1)", StringComparison.OrdinalIgnoreCase))
        {
            return "SELECT COALESCE(MAX(Level), -1)";
        }
        if (text.Contains("SELECT * FROM %s WHERE EngineID", StringComparison.OrdinalIgnoreCase))
        {
            return "SELECT * FROM %s WHERE EngineID ... Level = %d";
        }
        if (text.Contains("ORDER BY PartTable.IsStock DESC, PartTable.Level", StringComparison.OrdinalIgnoreCase))
        {
            return "ORDER BY PartTable.IsStock DESC, PartTable.Level";
        }

        return text.Length > 90 ? text[..90] + "..." : text;
    }

    private static IEnumerable<string> FindInterestingCodePatterns(byte[] bytes, int length, ulong startAddress, ulong imageBase)
    {
        for (var i = 0; i < length; i++)
        {
            var address = startAddress + (ulong)i;
            var rva = address - imageBase;

            if (i + 2 < length && bytes[i] == 0x83 && ((bytes[i + 1] >> 3) & 0x7) == 7)
            {
                var imm = unchecked((sbyte)bytes[i + 2]);
                if (imm is >= -1 and <= 12)
                {
                    yield return $"{FormatAddress(address, rva)} cmp r/m, {imm}";
                }
            }
            else if (i + 5 < length && bytes[i] == 0x81 && ((bytes[i + 1] >> 3) & 0x7) == 7)
            {
                var imm = BitConverter.ToInt32(bytes, i + 2);
                if (imm is >= -1 and <= 20)
                {
                    yield return $"{FormatAddress(address, rva)} cmp r/m, {imm}";
                }
            }
            else if (i + 1 < length && bytes[i] is >= 0x70 and <= 0x7F)
            {
                var rel = unchecked((sbyte)bytes[i + 1]);
                var target = unchecked((ulong)((long)address + 2 + rel));
                yield return $"{FormatAddress(address, rva)} short conditional jump -> 0x{target:X}";
            }
            else if (i + 5 < length && bytes[i] == 0x0F && bytes[i + 1] is >= 0x80 and <= 0x8F)
            {
                var rel = BitConverter.ToInt32(bytes, i + 2);
                var target = unchecked((ulong)((long)address + 6 + rel));
                yield return $"{FormatAddress(address, rva)} conditional jump -> 0x{target:X}";
            }
            else if (i + 4 < length && bytes[i] is 0xE8 or 0xE9)
            {
                var rel = BitConverter.ToInt32(bytes, i + 1);
                var kind = bytes[i] == 0xE8 ? "call" : "jump";
                var target = unchecked((ulong)((long)address + 5 + rel));
                yield return $"{FormatAddress(address, rva)} {kind} -> 0x{target:X}";
            }
        }
    }

    private static IEnumerable<string> FormatHexWindow(
        byte[] bytes,
        int length,
        ulong startAddress,
        ulong imageBase,
        HashSet<ulong> markers)
    {
        for (var offset = 0; offset < length; offset += 16)
        {
            var lineAddress = startAddress + (ulong)offset;
            var count = Math.Min(16, length - offset);
            var mark = Enumerable.Range(offset, count).Any(i => markers.Contains(startAddress + (ulong)i)) ? "*" : " ";
            var hex = string.Join(" ", bytes.Skip(offset).Take(count).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            yield return $"{mark} {FormatAddress(lineAddress, lineAddress - imageBase)}  {hex}";
        }
    }

    private static string FormatAddress(GameProcess process, ulong address)
    {
        return FormatAddress(address, address - process.BaseAddress);
    }

    private static string FormatAddress(ulong address, ulong rva)
    {
        return $"0x{address:X} / RVA 0x{rva:X}";
    }

    private static List<NearbyString> ReadNearbyStrings(GameProcess process, GameStringHit hit)
    {
        var query = NativeMethods.VirtualQueryEx(
            process.Handle,
            new IntPtr(unchecked((long)hit.Address)),
            out var mbi,
            (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>());
        if (query == 0 || !IsReadable(mbi))
        {
            return [];
        }

        var regionBase = unchecked((ulong)mbi.BaseAddress.ToInt64());
        var regionEnd = regionBase + (ulong)mbi.RegionSize;
        var scanStart = hit.Address > NearbyBytesBefore ? hit.Address - NearbyBytesBefore : regionBase;
        scanStart = Math.Max(scanStart, regionBase);
        var scanEnd = Math.Min(hit.Address + NearbyBytesAfter, regionEnd);
        if (scanEnd <= scanStart)
        {
            return [];
        }

        var size = checked((int)(scanEnd - scanStart));
        var buffer = new byte[size];
        if (!process.TryReadBytes(scanStart, buffer, out var bytesRead) || bytesRead <= 0)
        {
            return [];
        }

        var strings = new Dictionary<string, NearbyString>(StringComparer.OrdinalIgnoreCase);
        AddNearbyAscii(strings, buffer.AsSpan(0, bytesRead), scanStart);
        AddNearbyUtf16(strings, buffer.AsSpan(0, bytesRead), scanStart);

        return strings.Values
            .Where(item => item.Address != hit.Address)
            .OrderBy(item => item.Address)
            .ToList();
    }

    private static void AddNearbyAscii(Dictionary<string, NearbyString> strings, ReadOnlySpan<byte> buffer, ulong baseAddress)
    {
        var start = -1;
        for (var i = 0; i <= buffer.Length; i++)
        {
            var b = i < buffer.Length ? buffer[i] : (byte)0;
            if (i < buffer.Length && IsAsciiStringByte(b))
            {
                if (start < 0)
                {
                    start = i;
                }
                continue;
            }

            if (start >= 0)
            {
                var length = i - start;
                if (length >= 4)
                {
                    var text = CleanText(Encoding.UTF8.GetString(buffer.Slice(start, Math.Min(length, 260))));
                    AddNearby(strings, baseAddress + (ulong)start, "ascii", text);
                }
                start = -1;
            }
        }
    }

    private static void AddNearbyUtf16(Dictionary<string, NearbyString> strings, ReadOnlySpan<byte> buffer, ulong baseAddress)
    {
        var start = -1;
        var i = 0;
        while (i + 1 < buffer.Length)
        {
            var ch = (char)(buffer[i] | (buffer[i + 1] << 8));
            if (IsTextChar(ch))
            {
                if (start < 0)
                {
                    start = i;
                }
                i += 2;
                continue;
            }

            if (start >= 0)
            {
                var length = i - start;
                if (length >= 8)
                {
                    var text = CleanText(Encoding.Unicode.GetString(buffer.Slice(start, Math.Min(length, 520))));
                    AddNearby(strings, baseAddress + (ulong)start, "utf16", text);
                }
                start = -1;
            }
            i += 2;
        }
    }

    private static void AddNearby(Dictionary<string, NearbyString> strings, ulong address, string encodingName, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 4)
        {
            return;
        }

        text = text.Length > 220 ? text[..220] + "..." : text;
        strings.TryAdd($"{address:X}:{encodingName}", new NearbyString(address, encodingName, text));
    }

    private static bool IsReadable(NativeMethods.MemoryBasicInformation mbi)
    {
        return mbi.State == NativeMethods.MemCommitState &&
               (mbi.Protect & NativeMethods.PageNoAccess) == 0 &&
               (mbi.Protect & NativeMethods.PageGuard) == 0;
    }

    private static bool IsExecutable(uint protect)
    {
        return (protect & (NativeMethods.PageExecute |
                           NativeMethods.PageExecuteRead |
                           NativeMethods.PageExecuteReadWrite |
                           NativeMethods.PageExecuteWriteCopy)) != 0;
    }

    private static bool IsAsciiStringByte(byte value)
    {
        return value is >= 0x20 and <= 0x7E || value is 0x09;
    }

    private static bool IsTextChar(char value)
    {
        return value is >= ' ' and <= '~' || value == '\t';
    }

    private static string CleanText(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }
        return text.Trim('\0', ' ');
    }
}

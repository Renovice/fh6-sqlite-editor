using System.Globalization;
using System.Text;

namespace FH6SQLiteEditorNative;

internal sealed record CDatabase(ulong GlobalAddress, ulong Instance, ulong VTable, ulong ExecuteQuery);

internal sealed record GameColumn(string Name, ulong Type);

internal sealed class GameSqlResult
{
    public List<GameColumn> Columns { get; } = [];
    public List<object?[]> Rows { get; } = [];
}

internal static class GameSql
{
    private const string CDatabaseAob =
        "48 8B 0D ?? ?? ?? ?? 48 8B 01 4C 8D 45 ?? 48 8D 55 ?? FF 50 48 90 48 8B 4D ?? 48 85 C9";

    private const int ExecuteQueryVTableOffset = 9 * 8;
    private const int ResultHeaderSize = 72;
    private const int ColumnDefSize = 40;
    private const int CellSize = 16;

    public static CDatabase ResolveDatabase(GameProcess process)
    {
        var matches = PatternScanner.ScanProcess(process, CDatabaseAob);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException("Could not resolve CDatabase. The game version may be incompatible.");
        }

        var match = matches[0];
        var disp = process.ReadInt32(match + 3);
        var global = unchecked((ulong)((long)match + 7 + disp));
        var instance = process.ReadUInt64(global);
        if (instance == 0)
        {
            throw new InvalidOperationException("CDatabase global pointer was empty.");
        }

        var vtable = process.ReadUInt64(instance);
        var executeQuery = process.ReadUInt64(vtable + ExecuteQueryVTableOffset);
        if (executeQuery == 0)
        {
            throw new InvalidOperationException("CDatabase ExecuteQuery pointer was empty.");
        }

        return new CDatabase(global, instance, vtable, executeQuery);
    }

    public static GameSqlResult Execute(GameProcess process, CDatabase database, string sql)
    {
        using var code = new RemoteAllocation(process.Handle, 64, NativeMethods.PageExecuteReadWrite);
        using var sqlText = new RemoteAllocation(process.Handle, (nuint)(Encoding.UTF8.GetByteCount(sql) + 1), NativeMethods.PageReadWrite);
        using var resultPtr = new RemoteAllocation(process.Handle, 8, NativeMethods.PageReadWrite);

        var sqlBytes = Encoding.UTF8.GetBytes(sql + "\0");
        sqlText.Write(sqlBytes);
        resultPtr.Write(new byte[8]);

        var shellcode = BuildExecuteQueryShellcode(resultPtr.UlongAddress, sqlText.UlongAddress, database.ExecuteQuery);
        code.Write(shellcode);

        RemoteThread.Execute(process.Handle, code.Address, new IntPtr(unchecked((long)database.Instance)));

        var resultAddress = process.ReadUInt64(resultPtr.UlongAddress);
        return resultAddress == 0 ? new GameSqlResult() : ParseResult(process, resultAddress);
    }

    public static string CellToDisplay(object? value)
    {
        return value switch
        {
            null or DBNull => "NULL",
            double d => d.ToString("G17", CultureInfo.InvariantCulture),
            float f => f.ToString("G9", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static byte[] BuildExecuteQueryShellcode(ulong resultAddress, ulong sqlAddress, ulong executeQueryAddress)
    {
        var shellcode = new byte[34];
        var offset = 0;

        shellcode[offset++] = 0x48;
        shellcode[offset++] = 0xBA;
        BitConverter.GetBytes(resultAddress).CopyTo(shellcode, offset);
        offset += 8;

        shellcode[offset++] = 0x49;
        shellcode[offset++] = 0xB8;
        BitConverter.GetBytes(sqlAddress).CopyTo(shellcode, offset);
        offset += 8;

        shellcode[offset++] = 0xFF;
        shellcode[offset++] = 0x25;
        shellcode[offset++] = 0x00;
        shellcode[offset++] = 0x00;
        shellcode[offset++] = 0x00;
        shellcode[offset++] = 0x00;
        BitConverter.GetBytes(executeQueryAddress).CopyTo(shellcode, offset);
        offset += 8;

        Array.Resize(ref shellcode, offset);
        return shellcode;
    }

    private static GameSqlResult ParseResult(GameProcess process, ulong resultAddress)
    {
        var header = process.ReadBytes(resultAddress, ResultHeaderSize);
        var colBegin = BitConverter.ToUInt64(header, 8);
        var colEnd = BitConverter.ToUInt64(header, 16);
        var rowBegin = BitConverter.ToUInt64(header, 32);
        var rowEnd = BitConverter.ToUInt64(header, 40);

        var parsed = new GameSqlResult();
        if (colBegin == 0 || colEnd < colBegin)
        {
            return parsed;
        }

        var colBytes = checked((int)(colEnd - colBegin));
        var numCols = colBytes / ColumnDefSize;
        if (numCols is <= 0 or >= 1000)
        {
            return parsed;
        }

        var columnBytes = process.ReadBytes(colBegin, colBytes);
        for (var i = 0; i < numCols; i++)
        {
            var off = i * ColumnDefSize;
            var name = process.ReadMsvcString(colBegin + (ulong)off);
            var type = BitConverter.ToUInt64(columnBytes, off + 32);
            parsed.Columns.Add(new GameColumn(name, type));
        }

        if (rowEnd < rowBegin || rowBegin == 0)
        {
            return parsed;
        }

        var rowBytes = checked((int)(rowEnd - rowBegin));
        var numRows = rowBytes / 8;
        if (numRows <= 0 || numRows >= 100000)
        {
            return parsed;
        }

        var rowPtrBytes = process.ReadBytes(rowBegin, rowBytes);
        var rowDataSize = checked(numCols * CellSize);
        for (var r = 0; r < numRows; r++)
        {
            var rowPtr = BitConverter.ToUInt64(rowPtrBytes, r * 8);
            var row = new object?[numCols];
            if (rowPtr == 0)
            {
                parsed.Rows.Add(row);
                continue;
            }

            var cellData = process.ReadBytes(rowPtr, rowDataSize);
            for (var c = 0; c < numCols; c++)
            {
                var cellOff = c * CellSize;
                var type = cellData[cellOff];
                row[c] = type switch
                {
                    2 => BitConverter.ToInt64(cellData, cellOff + 8),
                    3 => BitConverter.ToDouble(cellData, cellOff + 8),
                    4 => ReadTextCell(process, cellData, cellOff),
                    _ => DBNull.Value
                };
            }
            parsed.Rows.Add(row);
        }

        return parsed;
    }

    private static string ReadTextCell(GameProcess process, byte[] cellData, int cellOff)
    {
        var strPtr = BitConverter.ToUInt64(cellData, cellOff + 8);
        return strPtr == 0 ? string.Empty : process.ReadMsvcString(strPtr);
    }
}

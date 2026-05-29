using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace FH6SQLiteEditorNative;

internal sealed record GameSchemaItem(string Name, string Sql);

internal sealed class GameDatabaseDumper
{
    private const int BatchSize = 500;

    public Task DumpAsync(string outputPath, IProgress<string> log, CancellationToken cancellationToken)
    {
        return Task.Run(() => Dump(outputPath, log, cancellationToken), cancellationToken);
    }

    private static void Dump(string outputPath, IProgress<string> log, CancellationToken cancellationToken)
    {
        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var tempPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "." + Path.GetFileName(outputPath) + ".tmp");
        File.Delete(tempPath);
        AppPaths.RemoveSqliteSidecars(tempPath);

        using var process = GameProcess.Open();
        log.Report($"Found forzahorizon6.exe PID {process.ProcessId}");
        log.Report($"Game base 0x{process.BaseAddress:X}, image 0x{process.ImageSize:X}");

        var db = GameSql.ResolveDatabase(process);
        log.Report($"CDatabase 0x{db.Instance:X}, ExecuteQuery 0x{db.ExecuteQuery:X}");

        var test = GameSql.Execute(process, db, "SELECT count(*) FROM sqlite_master");
        if (test.Rows.Count == 0)
        {
            throw new InvalidOperationException("SQL execution verification failed.");
        }

        using var local = new SqliteConnection(SqliteHelpers.ReadWriteConnectionString(tempPath));
        local.Open();
        ExecLocal(local, "PRAGMA journal_mode=OFF");
        ExecLocal(local, "PRAGMA synchronous=OFF");
        ExecLocal(local, "PRAGMA locking_mode=EXCLUSIVE");
        ExecLocal(local, "PRAGMA temp_store=MEMORY");
        ExecLocal(local, "PRAGMA foreign_keys=OFF");

        log.Report("Fetching schema...");
        var tables = GetTables(process, db);
        var indexes = GetSchema(process, db, "index");
        var views = GetSchema(process, db, "view");
        if (tables.Count == 0)
        {
            throw new InvalidOperationException("No game DB tables were returned.");
        }

        log.Report($"Found {tables.Count} tables, {indexes.Count} indexes, {views.Count} views");

        using var tx = local.BeginTransaction();
        try
        {
            foreach (var table in tables)
            {
                ExecLocal(local, table.Sql + ";", tx);
            }

            long totalRows = 0;
            for (var t = 0; t < tables.Count; t++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var table = tables[t];
                var count = GetGameCount(process, db, table.Name);
                if (count == 0)
                {
                    log.Report($"[{t + 1}/{tables.Count}] {table.Name}: 0 rows");
                    continue;
                }

                long dumped = 0;
                SqliteCommand? insert = null;
                try
                {
                    for (long offset = 0; offset < count; offset += BatchSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var rows = GameSql.Execute(
                            process,
                            db,
                            $"SELECT * FROM {SqliteHelpers.GameIdent(table.Name)} LIMIT {BatchSize} OFFSET {offset}");

                        if (rows.Rows.Count == 0)
                        {
                            break;
                        }

                        insert ??= PrepareInsert(local, tx, table.Name, rows.Columns.Count);
                        foreach (var row in rows.Rows)
                        {
                            insert.Parameters.Clear();
                            for (var i = 0; i < row.Length; i++)
                            {
                                insert.Parameters.AddWithValue("$p" + i, NormalizeLocalValue(row[i]));
                            }
                            insert.ExecuteNonQuery();
                            dumped++;
                        }
                    }
                }
                finally
                {
                    insert?.Dispose();
                }

                totalRows += dumped;
                log.Report($"[{t + 1}/{tables.Count}] {table.Name}: {dumped.ToString(CultureInfo.InvariantCulture)} rows");
            }

            foreach (var item in indexes)
            {
                ExecLocal(local, item.Sql + ";", tx);
            }
            foreach (var item in views)
            {
                ExecLocal(local, item.Sql + ";", tx);
            }

            tx.Commit();
            log.Report($"Done. {totalRows.ToString(CultureInfo.InvariantCulture)} rows dumped.");
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        local.Close();
        AppPaths.RemoveSqliteSidecars(tempPath);
        AppPaths.RemoveSqliteSidecars(outputPath);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
        File.Move(tempPath, outputPath);
        AppPaths.RemoveSqliteSidecars(outputPath);
        log.Report($"Output: {outputPath}");
    }

    private static List<GameSchemaItem> GetTables(GameProcess process, CDatabase db)
    {
        var result = GameSql.Execute(
            process,
            db,
            "SELECT tbl_name, sql FROM sqlite_master WHERE type='table' AND sql IS NOT NULL ORDER BY tbl_name");

        var tables = new List<GameSchemaItem>();
        foreach (var row in result.Rows)
        {
            if (row.Length >= 2 && row[0] is string name && row[1] is string sql && !name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                tables.Add(new GameSchemaItem(name, sql));
            }
        }
        return tables;
    }

    private static List<GameSchemaItem> GetSchema(GameProcess process, CDatabase db, string type)
    {
        var result = GameSql.Execute(
            process,
            db,
            $"SELECT name, sql FROM sqlite_master WHERE type={SqliteHelpers.QuoteSqlString(type)} AND sql IS NOT NULL ORDER BY name");

        var items = new List<GameSchemaItem>();
        foreach (var row in result.Rows)
        {
            if (row.Length >= 2 && row[0] is string name && row[1] is string sql)
            {
                items.Add(new GameSchemaItem(name, sql));
            }
        }
        return items;
    }

    private static long GetGameCount(GameProcess process, CDatabase db, string table)
    {
        var result = GameSql.Execute(process, db, "SELECT count(*) FROM " + SqliteHelpers.GameIdent(table));
        if (result.Rows.Count > 0 && result.Rows[0].Length > 0)
        {
            return Convert.ToInt64(result.Rows[0][0], CultureInfo.InvariantCulture);
        }
        return 0;
    }

    private static SqliteCommand PrepareInsert(SqliteConnection local, SqliteTransaction tx, string table, int columnCount)
    {
        var cmd = local.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"INSERT INTO {SqliteHelpers.Ident(table)} VALUES (" +
            string.Join(", ", Enumerable.Range(0, columnCount).Select(i => "$p" + i)) +
            ")";
        return cmd;
    }

    private static object NormalizeLocalValue(object? value)
    {
        return value switch
        {
            null or DBNull => DBNull.Value,
            _ => value
        };
    }

    private static void ExecLocal(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

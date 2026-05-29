using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace FH6SQLiteEditorNative;

internal sealed class GameDatabaseImporter
{
    private const string ResetSchema = "__fh6_base_reset";

    public Task ImportAsync(
        string editedDbPath,
        string baseDbPath,
        bool importAll,
        IProgress<string> log,
        CancellationToken cancellationToken,
        IEnumerable<string>? forceReplaceTables = null)
    {
        var forced = ForceSet(forceReplaceTables);
        return Task.Run(() => Import(editedDbPath, baseDbPath, importAll, log, cancellationToken, forced), cancellationToken);
    }

    public Task ResetGameToBaseAsync(
        string baseDbPath,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        return ResetGameToBaseAsync(baseDbPath, null, log, cancellationToken);
    }

    public Task ResetGameToBaseAsync(
        string baseDbPath,
        string? changedHintDbPath,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => ResetGameToBase(baseDbPath, changedHintDbPath, log, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<LocalTable> PreviewChangedTables(
        string editedDbPath,
        string baseDbPath,
        bool importAll,
        IEnumerable<string>? forceReplaceTables = null)
    {
        var forced = ForceSet(forceReplaceTables);
        using var edited = new SqliteConnection(SqliteHelpers.ReadOnlyConnectionString(editedDbPath));
        edited.Open();
        var tables = LocalTables(edited);
        if (!importAll)
        {
            MarkChangedTables(tables, baseDbPath, editedDbPath);
        }
        return tables.Where(t => t.Changed || forced.Contains(t.Name)).ToList();
    }

    private static void Import(
        string editedDbPath,
        string baseDbPath,
        bool importAll,
        IProgress<string> log,
        CancellationToken cancellationToken,
        IReadOnlySet<string> forceReplaceTables)
    {
        editedDbPath = Path.GetFullPath(editedDbPath);
        baseDbPath = Path.GetFullPath(baseDbPath);

        using var edited = new SqliteConnection(SqliteHelpers.ReadOnlyConnectionString(editedDbPath));
        edited.Open();
        var tables = LocalTables(edited);
        if (!importAll)
        {
            MarkChangedTables(tables, baseDbPath, editedDbPath);
        }

        var changed = tables.Where(t => t.Changed || forceReplaceTables.Contains(t.Name)).ToList();
        if (changed.Count == 0)
        {
            log.Report("No changed tables found. Nothing to import.");
            return;
        }

        log.Report($"Tables to import: {changed.Count}");
        foreach (var table in changed)
        {
            log.Report($"  {table.Name} ({table.RowCount} rows)");
        }

        using var process = GameProcess.Open();
        log.Report($"Found forzahorizon6.exe PID {process.ProcessId}");
        var db = GameSql.ResolveDatabase(process);
        log.Report($"CDatabase 0x{db.Instance:X}, ExecuteQuery 0x{db.ExecuteQuery:X}");

        var gameTables = GetGameTables(process, db);
        using var diff = importAll ? null : OpenDiffDb(baseDbPath, editedDbPath);

        var began = false;
        try
        {
            GameSql.Execute(process, db, "BEGIN TRANSACTION");
            began = true;

            foreach (var table in changed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!gameTables.Contains(table.Name))
                {
                    log.Report($"Skipping {table.Name}: table not present in running game DB");
                    continue;
                }

                var forceReplace = forceReplaceTables.Contains(table.Name);
                if (forceReplace)
                {
                    log.Report($"{table.Name}: forced full replace so live game deletions are applied");
                }

                var patched = !forceReplace && !importAll && diff is not null && PatchTable(process, db, diff, table, log);
                if (!patched)
                {
                    ImportWholeTable(process, db, edited, table, log, cancellationToken);
                }
            }

            GameSql.Execute(process, db, "COMMIT");
            began = false;
            log.Report("Import finished.");
        }
        catch
        {
            if (began)
            {
                try { GameSql.Execute(process, db, "ROLLBACK"); } catch { }
            }
            throw;
        }
    }

    private static HashSet<string> ForceSet(IEnumerable<string>? tables)
    {
        return tables is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : tables.Where(t => !string.IsNullOrWhiteSpace(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void ResetGameToBase(string baseDbPath, string? changedHintDbPath, IProgress<string> log, CancellationToken cancellationToken)
    {
        baseDbPath = Path.GetFullPath(baseDbPath);
        if (!File.Exists(baseDbPath))
        {
            throw new FileNotFoundException("Base DB was not found.", baseDbPath);
        }

        using var baseDb = new SqliteConnection(SqliteHelpers.ReadOnlyConnectionString(baseDbPath));
        baseDb.Open();
        var baseTables = LocalTables(baseDb);
        if (baseTables.Count == 0)
        {
            throw new InvalidOperationException("Base DB has no tables.");
        }

        using var process = GameProcess.Open();
        log.Report($"Found forzahorizon6.exe PID {process.ProcessId}");
        var db = GameSql.ResolveDatabase(process);
        log.Report($"CDatabase 0x{db.Instance:X}, ExecuteQuery 0x{db.ExecuteQuery:X}");

        var gameTables = GetGameTables(process, db);
        if (TryResetGameToBaseViaAttach(process, db, baseDbPath, baseTables, gameTables, log, cancellationToken))
        {
            return;
        }

        ResetGameToBaseByStreaming(process, db, baseDb, baseDbPath, changedHintDbPath, baseTables, gameTables, log, cancellationToken);
    }

    private static bool TryResetGameToBaseViaAttach(
        GameProcess process,
        CDatabase db,
        string baseDbPath,
        IReadOnlyList<LocalTable> baseTables,
        HashSet<string> gameTables,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        var changed = new List<LocalTable>();
        var attached = false;
        var resetStarted = false;

        try
        {
            TryDetachResetSchema(process, db);
            GameSql.Execute(process, db, $"ATTACH DATABASE {SqliteHelpers.QuoteSqlString(ReadOnlySqliteUri(baseDbPath))} AS {SqliteHelpers.GameIdent(ResetSchema)}");
            attached = true;

            var attachedCount = GameScalarLong(process, db, $"SELECT count(*) FROM {SqliteHelpers.GameIdent(ResetSchema)}.sqlite_master WHERE type='table'");
            if (attachedCount <= 0)
            {
                log.Report("Game-side ATTACH saw zero BASE DB tables; using streaming reset fallback.");
                return false;
            }

            log.Report($"Checking live game DB against BASE DB ({baseTables.Count} table(s))...");
            foreach (var table in baseTables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!gameTables.Contains(table.Name))
                {
                    log.Report($"Skipping {table.Name}: table not present in running game DB");
                    continue;
                }

                if (GameTableChanged(process, db, table.Name))
                {
                    changed.Add(table);
                    log.Report($"Changed: {table.Name} ({table.RowCount} base row(s))");
                }
            }

            if (changed.Count == 0)
            {
                log.Report("Live game DB already matches BASE DB. Nothing to reset.");
                return true;
            }

            log.Report($"Resetting {changed.Count} changed table(s) to BASE DB...");
            var began = false;
            try
            {
                resetStarted = true;
                GameSql.Execute(process, db, "BEGIN TRANSACTION");
                began = true;
                foreach (var table in changed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ResetWholeTableFromAttachedBase(process, db, table);
                    log.Report($"{table.Name}: restored {table.RowCount} row(s)");
                }
                GameSql.Execute(process, db, "COMMIT");
                began = false;
                log.Report("Game DB reset to BASE DB finished.");
                return true;
            }
            catch
            {
                if (began)
                {
                    try { GameSql.Execute(process, db, "ROLLBACK"); } catch { }
                }
                throw;
            }
        }
        catch (Exception ex) when (!resetStarted)
        {
            log.Report("Fast game-side BASE DB compare is unavailable; using streaming reset fallback. " + ex.Message);
            return false;
        }
        finally
        {
            if (attached)
            {
                TryDetachResetSchema(process, db);
            }
            AppPaths.RemoveSqliteSidecars(baseDbPath);
        }
    }

    private static void ResetGameToBaseByStreaming(
        GameProcess process,
        CDatabase db,
        SqliteConnection baseDb,
        string baseDbPath,
        string? changedHintDbPath,
        IReadOnlyList<LocalTable> baseTables,
        HashSet<string> gameTables,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        var tablesToRestore = TablesToRestoreByStreaming(baseTables, baseDbPath, changedHintDbPath, gameTables, log);
        if (tablesToRestore.Count == 0)
        {
            log.Report("No reset candidate tables found.");
            return;
        }

        log.Report($"Streaming {tablesToRestore.Count} BASE DB table(s) into the running game...");
        var began = false;
        try
        {
            GameSql.Execute(process, db, "BEGIN TRANSACTION");
            began = true;
            foreach (var table in tablesToRestore)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImportWholeTable(process, db, baseDb, table, log, cancellationToken);
            }
            GameSql.Execute(process, db, "COMMIT");
            began = false;
            log.Report("Game DB reset to BASE DB finished.");
        }
        catch
        {
            if (began)
            {
                try { GameSql.Execute(process, db, "ROLLBACK"); } catch { }
            }
            throw;
        }
    }

    private static List<LocalTable> TablesToRestoreByStreaming(
        IReadOnlyList<LocalTable> baseTables,
        string baseDbPath,
        string? changedHintDbPath,
        HashSet<string> gameTables,
        IProgress<string> log)
    {
        var commonBaseTables = baseTables
            .Where(t => gameTables.Contains(t.Name))
            .ToList();

        if (!string.IsNullOrWhiteSpace(changedHintDbPath) &&
            File.Exists(changedHintDbPath) &&
            !Path.GetFullPath(baseDbPath).Equals(Path.GetFullPath(changedHintDbPath), StringComparison.OrdinalIgnoreCase))
        {
            var hinted = commonBaseTables.ToList();
            MarkChangedTables(hinted, baseDbPath, changedHintDbPath);
            var changed = hinted.Where(t => t.Changed).ToList();
            if (changed.Count > 0)
            {
                log.Report($"Using open editor DB to choose reset tables ({changed.Count} changed table(s)).");
                return changed;
            }

            log.Report("Open editor DB matches BASE DB; streaming all common BASE DB tables as a full reset.");
        }
        else
        {
            log.Report("No editor DB was available for a changed-table hint; streaming all common BASE DB tables as a full reset.");
        }

        return commonBaseTables;
    }

    private static bool GameTableChanged(GameProcess process, CDatabase db, string table)
    {
        var liveTable = SqliteHelpers.GameIdent(table);
        var baseTable = GameQualified(ResetSchema, table);

        var liveCount = GameScalarLong(process, db, $"SELECT count(*) FROM {liveTable}");
        var baseCount = GameScalarLong(process, db, $"SELECT count(*) FROM {baseTable}");
        if (liveCount != baseCount)
        {
            return true;
        }

        return GameScalarExists(process, db, $"SELECT 1 FROM (SELECT * FROM {liveTable} EXCEPT SELECT * FROM {baseTable}) LIMIT 1") ||
               GameScalarExists(process, db, $"SELECT 1 FROM (SELECT * FROM {baseTable} EXCEPT SELECT * FROM {liveTable}) LIMIT 1");
    }

    private static void ResetWholeTableFromAttachedBase(GameProcess process, CDatabase db, LocalTable table)
    {
        GameSql.Execute(process, db, "DELETE FROM " + SqliteHelpers.GameIdent(table.Name));
        GameSql.Execute(process, db, $"INSERT INTO {SqliteHelpers.GameIdent(table.Name)} SELECT * FROM {GameQualified(ResetSchema, table.Name)}");
    }

    private static List<LocalTable> LocalTables(SqliteConnection db)
    {
        var tables = new List<LocalTable>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT tbl_name FROM sqlite_master WHERE type='table' ORDER BY tbl_name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            tables.Add(new LocalTable(name, TableCount(db, SqliteHelpers.Ident(name)), true));
        }
        return tables;
    }

    private static void MarkChangedTables(List<LocalTable> tables, string basePath, string editedPath)
    {
        if (!File.Exists(basePath) || Path.GetFullPath(basePath).Equals(Path.GetFullPath(editedPath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var diff = OpenDiffDb(basePath, editedPath);
        for (var i = 0; i < tables.Count; i++)
        {
            tables[i] = tables[i] with { Changed = TableChanged(diff, tables[i].Name) };
        }
    }

    private static SqliteConnection OpenDiffDb(string basePath, string editedPath)
    {
        var conn = new SqliteConnection(SqliteHelpers.ReadOnlyConnectionString(basePath));
        conn.Open();
        using var cmd = conn.CreateCommand();
        try
        {
            cmd.CommandText = "DETACH DATABASE edited";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
        cmd.CommandText = "ATTACH DATABASE " + SqliteHelpers.QuoteSqlString(editedPath) + " AS edited";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private static bool TableChanged(SqliteConnection db, string table)
    {
        if (!TableExists(db, "main", table) || !TableExists(db, "edited", table))
        {
            return true;
        }

        var mainTable = QualifiedTable("main", table);
        var editedTable = QualifiedTable("edited", table);
        if (TableCount(db, mainTable) != TableCount(db, editedTable))
        {
            return true;
        }

        return ScalarExists(db, $"SELECT 1 FROM (SELECT * FROM {editedTable} EXCEPT SELECT * FROM {mainTable}) LIMIT 1") ||
               ScalarExists(db, $"SELECT 1 FROM (SELECT * FROM {mainTable} EXCEPT SELECT * FROM {editedTable}) LIMIT 1");
    }

    private static bool PatchTable(GameProcess process, CDatabase gameDb, SqliteConnection diff, LocalTable table, IProgress<string> log)
    {
        if (!TableExists(diff, "main", table.Name) || !TableExists(diff, "edited", table.Name))
        {
            return false;
        }

        var baseColumns = TableColumns(diff, "main", table.Name);
        var editedColumns = TableColumns(diff, "edited", table.Name);
        if (baseColumns.Count == 0 || !SameColumnLayout(baseColumns, editedColumns))
        {
            return false;
        }

        var pk = PrimaryKeyIndexes(baseColumns);
        if (pk.Count == 0)
        {
            return false;
        }

        var mainTable = QualifiedTable("main", table.Name);
        var editedTable = QualifiedTable("edited", table.Name);
        var joinSql = JoinOnPk(baseColumns, pk);
        var changeSql = ChangedColumnsWhere(baseColumns);
        var firstPk = baseColumns[pk[0]].Name;

        var deletedCount = ScalarLong(diff,
            $"SELECT count(*) FROM {mainTable} b LEFT JOIN {editedTable} e ON {joinSql} WHERE {AliasedCol("e", firstPk)} IS NULL");
        var insertedCount = ScalarLong(diff,
            $"SELECT count(*) FROM {editedTable} e LEFT JOIN {mainTable} b ON {joinSql} WHERE {AliasedCol("b", firstPk)} IS NULL");
        var updatedCount = ScalarLong(diff,
            $"SELECT count(*) FROM {editedTable} e JOIN {mainTable} b ON {joinSql} WHERE {changeSql}");
        var total = deletedCount + insertedCount + updatedCount;

        log.Report($"{table.Name}: patch {updatedCount} update(s), {insertedCount} insert(s), {deletedCount} delete(s)");
        if (total == 0)
        {
            return true;
        }

        long applied = 0;
        using (var cmd = diff.CreateCommand())
        {
            cmd.CommandText =
                "SELECT " + string.Join(", ", pk.Select(i => AliasedCol("b", baseColumns[i].Name))) +
                $" FROM {mainTable} b LEFT JOIN {editedTable} e ON {joinSql} WHERE {AliasedCol("e", firstPk)} IS NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var sql = $"DELETE FROM {SqliteHelpers.GameIdent(table.Name)} WHERE {WherePkFromPkReader(reader, baseColumns, pk)}";
                GameSql.Execute(process, gameDb, sql);
                applied++;
            }
        }

        using (var cmd = diff.CreateCommand())
        {
            cmd.CommandText = $"SELECT e.* FROM {editedTable} e JOIN {mainTable} b ON {joinSql} WHERE {changeSql}";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var assignments = AssignmentsFromRow(reader, baseColumns);
                if (assignments.Length == 0)
                {
                    continue;
                }
                var sql = $"UPDATE {SqliteHelpers.GameIdent(table.Name)} SET {assignments} WHERE {WherePkFromRow(reader, baseColumns, pk)}";
                GameSql.Execute(process, gameDb, sql);
                applied++;
            }
        }

        using (var cmd = diff.CreateCommand())
        {
            cmd.CommandText = $"SELECT e.* FROM {editedTable} e LEFT JOIN {mainTable} b ON {joinSql} WHERE {AliasedCol("b", firstPk)} IS NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var sql = $"INSERT INTO {SqliteHelpers.GameIdent(table.Name)} VALUES ({ValuesFromRow(reader)})";
                GameSql.Execute(process, gameDb, sql);
                applied++;
            }
        }

        log.Report($"{table.Name}: applied {applied} statement(s)");
        return true;
    }

    private static void ImportWholeTable(
        GameProcess process,
        CDatabase gameDb,
        SqliteConnection edited,
        LocalTable table,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        log.Report($"{table.Name}: full replace ({table.RowCount} rows)");
        GameSql.Execute(process, gameDb, "DELETE FROM " + SqliteHelpers.GameIdent(table.Name));

        long inserted = 0;
        using var cmd = edited.CreateCommand();
        cmd.CommandText = "SELECT * FROM " + SqliteHelpers.Ident(table.Name);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sql = $"INSERT INTO {SqliteHelpers.GameIdent(table.Name)} VALUES ({ValuesFromRow(reader)})";
            GameSql.Execute(process, gameDb, sql);
            inserted++;
        }
        log.Report($"{table.Name}: {inserted} inserted");
    }

    private static HashSet<string> GetGameTables(GameProcess process, CDatabase db)
    {
        var result = GameSql.Execute(process, db, "SELECT tbl_name FROM sqlite_master WHERE type='table'");
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in result.Rows)
        {
            if (row.Length > 0 && row[0] is string name)
            {
                tables.Add(name);
            }
        }
        return tables;
    }

    private static List<ColumnInfo> TableColumns(SqliteConnection db, string schema, string table)
    {
        var columns = new List<ColumnInfo>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA {schema}.table_info({SqliteHelpers.Ident(table)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(new ColumnInfo(reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2), reader.GetInt32(5)));
        }
        return columns;
    }

    private static bool SameColumnLayout(IReadOnlyList<ColumnInfo> a, IReadOnlyList<ColumnInfo> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Name.Equals(b[i].Name, StringComparison.OrdinalIgnoreCase) ||
                a[i].PrimaryKeyRank != b[i].PrimaryKeyRank)
            {
                return false;
            }
        }
        return true;
    }

    private static List<int> PrimaryKeyIndexes(IReadOnlyList<ColumnInfo> columns)
    {
        return columns
            .Select((c, i) => (Column: c, Index: i))
            .Where(x => x.Column.PrimaryKeyRank > 0)
            .OrderBy(x => x.Column.PrimaryKeyRank)
            .Select(x => x.Index)
            .ToList();
    }

    private static string JoinOnPk(IReadOnlyList<ColumnInfo> columns, IReadOnlyList<int> pk)
    {
        return string.Join(" AND ", pk.Select(i =>
        {
            var col = columns[i].Name;
            return $"{AliasedCol("e", col)} = {AliasedCol("b", col)}";
        }));
    }

    private static string ChangedColumnsWhere(IReadOnlyList<ColumnInfo> columns)
    {
        var parts = columns
            .Where(c => c.PrimaryKeyRank == 0)
            .Select(c => $"{AliasedCol("e", c.Name)} IS NOT {AliasedCol("b", c.Name)}")
            .ToList();
        return parts.Count == 0 ? "0" : string.Join(" OR ", parts);
    }

    private static string AssignmentsFromRow(SqliteDataReader reader, IReadOnlyList<ColumnInfo> columns)
    {
        var assignments = new List<string>();
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].PrimaryKeyRank > 0)
            {
                continue;
            }
            assignments.Add($"{SqliteHelpers.GameIdent(columns[i].Name)} = {CellToGameSql(reader, i)}");
        }
        return string.Join(", ", assignments);
    }

    private static string WherePkFromRow(SqliteDataReader reader, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<int> pk)
    {
        return string.Join(" AND ", pk.Select(i => $"{SqliteHelpers.GameIdent(columns[i].Name)} = {CellToGameSql(reader, i)}"));
    }

    private static string WherePkFromPkReader(SqliteDataReader reader, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<int> pk)
    {
        return string.Join(" AND ", pk.Select((columnIndex, readerIndex) =>
            $"{SqliteHelpers.GameIdent(columns[columnIndex].Name)} = {CellToGameSql(reader, readerIndex)}"));
    }

    private static string ValuesFromRow(SqliteDataReader reader)
    {
        return string.Join(", ", Enumerable.Range(0, reader.FieldCount).Select(i => CellToGameSql(reader, i)));
    }

    private static string CellToGameSql(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return "NULL";
        }

        return SqliteHelpers.ToGameSqlLiteral(reader.GetValue(ordinal));
    }

    private static bool TableExists(SqliteConnection db, string schema, string table)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM {SqliteHelpers.Ident(schema)}.sqlite_master WHERE type='table' AND tbl_name=$table LIMIT 1";
        cmd.Parameters.AddWithValue("$table", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static long TableCount(SqliteConnection db, string qualifiedTable)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM " + qualifiedTable;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool ScalarExists(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() is not null;
    }

    private static long ScalarLong(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long GameScalarLong(GameProcess process, CDatabase db, string sql)
    {
        var result = GameSql.Execute(process, db, sql);
        if (result.Rows.Count == 0 || result.Rows[0].Length == 0 || result.Rows[0][0] is null or DBNull)
        {
            throw new InvalidOperationException("The running game DB did not return a scalar result for: " + sql);
        }
        return Convert.ToInt64(result.Rows[0][0], CultureInfo.InvariantCulture);
    }

    private static bool GameScalarExists(GameProcess process, CDatabase db, string sql)
    {
        var result = GameSql.Execute(process, db, sql);
        return result.Rows.Count > 0;
    }

    private static void TryDetachResetSchema(GameProcess process, CDatabase db)
    {
        try { GameSql.Execute(process, db, $"DETACH DATABASE {SqliteHelpers.GameIdent(ResetSchema)}"); } catch { }
    }

    private static string ReadOnlySqliteUri(string path)
    {
        return new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro&immutable=1";
    }

    private static string QualifiedTable(string schema, string table) => $"{SqliteHelpers.Ident(schema)}.{SqliteHelpers.Ident(table)}";

    private static string GameQualified(string schema, string table) => $"{SqliteHelpers.GameIdent(schema)}.{SqliteHelpers.GameIdent(table)}";

    private static string AliasedCol(string alias, string column) => $"{alias}.{SqliteHelpers.Ident(column)}";
}

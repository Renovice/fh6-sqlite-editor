using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FH6SQLiteEditorNative;

internal sealed class SqliteEditorService : IDisposable
{
    private sealed record AspirationPartSpec(
        string TableName,
        string PartName,
        long AspirationId,
        string FallbackPartName,
        long FallbackAspirationId);

    private sealed record UpgradePartMetadataSpec(
        long Id,
        string TableName,
        string CategoryName,
        string PartName);

    private sealed record PartLevelProbe(long Level, long RowCount, long StockRows, string Ids);

    private sealed record UpgradeTypeProbe(long Id, long IsException, long DisplayOrder, string Name, string Description, string IconPath, string ImagePath);

    private sealed record UpgradeLevelProbe(long Level, long RowCount, string Ids, string Names);

    private sealed record UpgradeWizardPartProbe(long Level, long RowCount, string Sorts);

    private sealed record AeroOptionAddResult(long Id, long Level, long Sequence, long? AeroPhysicsId);

    private readonly Dictionary<string, List<ColumnInfo>> _schema = new(StringComparer.OrdinalIgnoreCase);
    private readonly SqliteConnection _connection;

    public string SourcePath { get; }
    public string SessionPath { get; }

    public SqliteEditorService(string sourcePath)
    {
        SourcePath = Path.GetFullPath(sourcePath);
        Directory.CreateDirectory(AppPaths.SessionsDir);
        SessionPath = Path.Combine(AppPaths.SessionsDir, $"fh6_session_{Environment.ProcessId}_{Guid.NewGuid():N}.sqlite");
        File.Copy(SourcePath, SessionPath, overwrite: true);
        AppPaths.RemoveSqliteSidecars(SessionPath);

        _connection = new SqliteConnection(SqliteHelpers.ReadWriteConnectionString(SessionPath));
        _connection.Open();
        ExecuteNonQuery("PRAGMA foreign_keys=OFF");
        ExecuteNonQuery("PRAGMA busy_timeout=5000");
        ExecuteNonQuery("PRAGMA journal_mode=DELETE");
        ExecuteNonQuery("PRAGMA synchronous=NORMAL");
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { File.Delete(SessionPath); } catch { }
        AppPaths.RemoveSqliteSidecars(SessionPath);
    }

    public IReadOnlyList<string> ExistingTables(IEnumerable<string> preferred)
    {
        return preferred.Where(TableExists).ToList();
    }

    public IReadOnlyList<string> AllTables()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT tbl_name FROM sqlite_master WHERE type='table' ORDER BY tbl_name";
        using var reader = cmd.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (!name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                tables.Add(name);
            }
        }
        return tables;
    }

    public IReadOnlySet<string> WritableColumnNames(string table)
    {
        if (!TableExists(table))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return GetColumns(table)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<long> EngineIdsForCar(long carId)
    {
        if (!TableExists("List_UpgradeEngine"))
        {
            return [];
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT EngineID FROM List_UpgradeEngine WHERE Ordinal=$carId ORDER BY IsStock DESC, Level, EngineID";
        cmd.Parameters.AddWithValue("$carId", carId);
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                ids.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
            }
        }
        return ids;
    }

    public IReadOnlyList<EngineChoice> EngineSwapsForCar(long carId)
    {
        if (!TableExists("List_UpgradeEngine"))
        {
            return [];
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT ue.EngineID, ue.IsStock, ue.Level, e.MediaName " +
            "FROM List_UpgradeEngine ue " +
            "LEFT JOIN Data_Engine e ON e.EngineID = ue.EngineID " +
            "WHERE ue.Ordinal=$carId " +
            "ORDER BY ue.IsStock DESC, ue.Level, ue.Id";
        cmd.Parameters.AddWithValue("$carId", carId);
        using var reader = cmd.ExecuteReader();
        var engines = new List<EngineChoice>();
        while (reader.Read())
        {
            var engineId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            var isStock = !reader.IsDBNull(1) && Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture) != 0;
            var level = reader.IsDBNull(2) ? "" : $" Level {reader.GetValue(2)}";
            var name = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var label = $"{engineId} | {FirstNonEmpty(name, "Unknown engine")}{level}" + (isStock ? " | Stock" : "");
            engines.Add(new EngineChoice(engineId, label));
        }
        return engines;
    }

    public IReadOnlyList<EngineChoice> SearchEngineCatalog(string search)
    {
        if (!TableExists("Data_Engine"))
        {
            return [];
        }

        using var cmd = _connection.CreateCommand();
        var where = "";
        if (!string.IsNullOrWhiteSpace(search))
        {
            where = "WHERE MediaName LIKE $search OR CAST(EngineID AS TEXT) LIKE $search ";
            cmd.Parameters.AddWithValue("$search", "%" + search.Trim() + "%");
        }

        cmd.CommandText =
            "SELECT EngineID, MediaName, ConfigID, CylinderID, AspirationID_Stock, EngineGraphingMaxPower, EngineGraphingMaxTorque " +
            "FROM Data_Engine " +
            where +
            "ORDER BY MediaName LIMIT 200";

        using var reader = cmd.ExecuteReader();
        var engines = new List<EngineChoice>();
        while (reader.Read())
        {
            var engineId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var config = reader.IsDBNull(2) ? "" : $" Config {reader.GetValue(2)}";
            var cylinders = reader.IsDBNull(3) ? "" : $" Cyl {reader.GetValue(3)}";
            var power = reader.IsDBNull(5) ? "" : $" Power {reader.GetValue(5)}";
            engines.Add(new EngineChoice(engineId, $"{engineId} | {FirstNonEmpty(name, "Unknown engine")}{config}{cylinders}{power}"));
        }
        return engines;
    }

    public bool AddEngineSwap(long carId, long engineId, int price = 50000)
    {
        if (!TableExists("List_UpgradeEngine"))
        {
            throw new InvalidOperationException("List_UpgradeEngine table is missing.");
        }

        using var existing = _connection.CreateCommand();
        existing.CommandText = "SELECT 1 FROM List_UpgradeEngine WHERE Ordinal=$carId AND EngineID=$engineId LIMIT 1";
        existing.Parameters.AddWithValue("$carId", carId);
        existing.Parameters.AddWithValue("$engineId", engineId);
        if (existing.ExecuteScalar() is not null)
        {
            return false;
        }

        using var templateCmd = _connection.CreateCommand();
        templateCmd.CommandText =
            "SELECT * FROM List_UpgradeEngine WHERE Ordinal=$carId AND IsStock=1 LIMIT 1";
        templateCmd.Parameters.AddWithValue("$carId", carId);

        var columns = GetColumns("List_UpgradeEngine").Select(c => c.Name).ToList();
        var row = ReadFirstRow(templateCmd, columns);
        if (row.Count == 0)
        {
            using var fallback = _connection.CreateCommand();
            fallback.CommandText = "SELECT * FROM List_UpgradeEngine WHERE Ordinal=$carId LIMIT 1";
            fallback.Parameters.AddWithValue("$carId", carId);
            row = ReadFirstRow(fallback, columns);
        }
        if (row.Count == 0)
        {
            throw new InvalidOperationException("No existing engine row is available as a template.");
        }

        row["Id"] = NextId("List_UpgradeEngine", "Id");
        row["Ordinal"] = carId;
        row["EngineID"] = engineId;
        if (columns.Contains("IsStock", StringComparer.OrdinalIgnoreCase))
        {
            row["IsStock"] = 0;
        }
        if (columns.Contains("Level", StringComparer.OrdinalIgnoreCase))
        {
            row["Level"] = MaxInt("List_UpgradeEngine", "Level", "Ordinal=$carId", ("$carId", carId)) + 1;
        }
        if (columns.Contains("Price", StringComparer.OrdinalIgnoreCase))
        {
            row["Price"] = price;
        }

        using var tx = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            "INSERT INTO List_UpgradeEngine (" +
            string.Join(", ", columns.Select(SqliteHelpers.Ident)) +
            ") VALUES (" +
            string.Join(", ", columns.Select((_, i) => "$p" + i)) +
            ")";
        for (var i = 0; i < columns.Count; i++)
        {
            insert.Parameters.AddWithValue("$p" + i, NormalizeDbValue(row.GetValueOrDefault(columns[i], DBNull.Value)));
        }
        insert.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public DataTable LoadEngineSwaps(long carId)
    {
        if (!TableExists("List_UpgradeEngine"))
        {
            return new DataTable("List_UpgradeEngine");
        }

        var sql =
            "SELECT ue.*, e.MediaName AS EngineMediaName, e.[EngineMass-kg] AS EngineMassKg, " +
            "stockEngine.[EngineMass-kg] AS StockEngineMassKg, " +
            "(e.[EngineMass-kg] - stockEngine.[EngineMass-kg]) AS EngineMassDeltaKg, " +
            "ue.MassDiff AS EffectiveMenuMassDiffKg, " +
            "e.ConfigID, e.CylinderID, e.AspirationID_Stock, " +
            "e.EngineGraphingMaxPower, e.EngineGraphingMaxTorque " +
            "FROM List_UpgradeEngine ue " +
            "LEFT JOIN Data_Engine e ON e.EngineID = ue.EngineID " +
            "LEFT JOIN List_UpgradeEngine stockUpgrade ON stockUpgrade.Ordinal = ue.Ordinal AND stockUpgrade.IsStock = 1 " +
            "LEFT JOIN Data_Engine stockEngine ON stockEngine.EngineID = stockUpgrade.EngineID " +
            "WHERE ue.Ordinal=$carId " +
            "ORDER BY ue.IsStock DESC, ue.Level, ue.Id";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$carId", carId);
        return MarkReadOnlyColumns(
            ReadDataTable("List_UpgradeEngine", cmd),
            "EngineMediaName",
            "EngineMassKg",
            "StockEngineMassKg",
            "EngineMassDeltaKg",
            "EffectiveMenuMassDiffKg",
            "ConfigID",
            "CylinderID",
            "AspirationID_Stock",
            "EngineGraphingMaxPower",
            "EngineGraphingMaxTorque");
    }

    public DataTable LoadEngineBase(long engineId)
    {
        if (!TableExists("Data_Engine"))
        {
            return new DataTable("Data_Engine");
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Data_Engine WHERE EngineID=$engineId LIMIT 1";
        cmd.Parameters.AddWithValue("$engineId", engineId);
        var table = ReadDataTable("Data_Engine", cmd);
        table.AcceptChanges();
        return table;
    }

    public DataTable LoadEngineParts(string table, long engineId)
    {
        if (!EditorConstants.EnginePartTables.Contains(table, StringComparer.OrdinalIgnoreCase) || !TableExists(table))
        {
            return new DataTable(table);
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {SqliteHelpers.Ident(table)} WHERE EngineID=$engineId ORDER BY Level, Id";
        cmd.Parameters.AddWithValue("$engineId", engineId);
        return ReadDataTable(table, cmd);
    }

    public List<CarListItem> SearchCars(string search, string visibilityFilter)
    {
        if (!TableExists("Data_Car"))
        {
            return [];
        }

        var columns = GetColumns("Data_Car").Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var makeColumns = TableExists("List_CarMake")
            ? GetColumns("List_CarMake").Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Expr(string column, string fallback) => columns.Contains(column) ? "c." + SqliteHelpers.Ident(column) : fallback;
        string MakeExpr(string column, string fallback) => makeColumns.Contains(column) ? "mk." + SqliteHelpers.Ident(column) : fallback;

        var where = new List<string>();
        using var cmd = _connection.CreateCommand();

        if (!string.IsNullOrWhiteSpace(search))
        {
            cmd.Parameters.AddWithValue("$search", "%" + search.Trim() + "%");
            where.Add("(" +
                      $"{Expr("MediaName", "''")} LIKE $search OR " +
                      $"{Expr("ModelShort", "''")} LIKE $search OR " +
                      $"{MakeExpr("ManufacturerCode", "''")} LIKE $search OR " +
                      $"{MakeExpr("IconPathBase", "''")} LIKE $search OR " +
                      $"{Expr("CurbWeight", "''")} LIKE $search OR " +
                      (columns.Contains("CurbWeight") ? "CAST(c." + SqliteHelpers.Ident("CurbWeight") + " * 100.0 AS TEXT) LIKE $search OR " : "") +
                      $"{Expr("Id", "''")} LIKE $search)");
        }

        if (visibilityFilter == "autoshow" && columns.Contains("NotAvailableInAutoshow"))
        {
            where.Add($"{SqliteHelpers.Ident("NotAvailableInAutoshow")} <> 0");
        }
        else if (visibilityFilter == "auction" && columns.Contains("NotAvailableInAuctionHouse"))
        {
            where.Add($"{SqliteHelpers.Ident("NotAvailableInAuctionHouse")} <> 0");
        }
        else if (visibilityFilter == "visible" && columns.Contains("NotAvailableInAutoshow"))
        {
            where.Add($"({SqliteHelpers.Ident("NotAvailableInAutoshow")} = 0 OR {SqliteHelpers.Ident("NotAvailableInAutoshow")} IS NULL)");
        }

        cmd.CommandText =
            "SELECT " +
            $"{Expr("Id", "rowid")} AS Id, " +
            $"{Expr("Year", "NULL")} AS Year, " +
            $"{MakeExpr("ManufacturerCode", "NULL")} AS ManufacturerCode, " +
            $"{Expr("MediaName", "NULL")} AS MediaName, " +
            $"{Expr("PI", "NULL")} AS PI, " +
            $"{Expr("ClassID", "NULL")} AS ClassID, " +
            $"{Expr("CurbWeight", "NULL")} AS CurbWeight " +
            $"FROM {SqliteHelpers.Ident("Data_Car")} c " +
            (TableExists("List_CarMake") ? $"LEFT JOIN {SqliteHelpers.Ident("List_CarMake")} mk ON mk.ID = c.MakeID " : "") +
            (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : "") +
            "ORDER BY MediaName, Id LIMIT 1000";

        var result = new List<CarListItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var year = reader.IsDBNull(1) ? "" : reader.GetValue(1).ToString();
            var make = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var media = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var pi = reader.IsDBNull(4) ? "" : " PI " + reader.GetValue(4);
            var cls = reader.IsDBNull(5) ? "" : " Class " + reader.GetValue(5);
            var weight = reader.IsDBNull(6)
                ? ""
                : " Weight " + Math.Round(Convert.ToDouble(reader.GetValue(6), CultureInfo.InvariantCulture) * 100.0, 1).ToString("0.#", CultureInfo.InvariantCulture) + " kg";

            var title = FirstNonEmpty(media, $"Car {id}");
            var subtitle = string.Join(" | ", new[] { year, make, $"ID {id}{pi}{cls}{weight}" }.Where(v => !string.IsNullOrWhiteSpace(v)));
            result.Add(new CarListItem(id, title, subtitle));
        }

        return result;
    }

    public DataTable LoadTable(string table, long? selectedCarId, int limit = 5000)
    {
        if (!TableExists(table))
        {
            return new DataTable(table);
        }

        var enhancedTable = TryLoadEnhancedDisplayTable(table, selectedCarId, limit);
        if (enhancedTable is not null)
        {
            return enhancedTable;
        }

        var columns = GetColumns(table);
        var visibleColumnSql = string.Join(", ", columns.Select(c => SqliteHelpers.Ident(c.Name)));
        var hasPk = columns.Any(c => c.PrimaryKeyRank > 0);
        var selectPrefix = hasPk ? "" : "rowid AS __fh6_rowid, ";
        var where = DefaultWhereForTable(table, selectedCarId, columns);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT {selectPrefix}{visibleColumnSql} FROM {SqliteHelpers.Ident(table)} " +
            (where.Sql.Length > 0 ? $"WHERE {where.Sql} " : "") +
            $"LIMIT {limit}";
        foreach (var (name, value) in where.Parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return ReadDataTable(table, cmd);
    }

    public DataTable LoadRowsWhere(string table, string column, object value, int limit = 250)
    {
        if (!TableExists(table))
        {
            return new DataTable(table);
        }

        var columns = GetColumns(table);
        var actualColumn = columns
            .Select(c => c.Name)
            .FirstOrDefault(c => c.Equals(column, StringComparison.OrdinalIgnoreCase));
        if (actualColumn is null)
        {
            throw new InvalidOperationException($"{table} does not have column {column}.");
        }

        var visibleColumnSql = string.Join(", ", columns.Select(c => SqliteHelpers.Ident(c.Name)));
        var hasPk = columns.Any(c => c.PrimaryKeyRank > 0);
        var selectPrefix = hasPk ? "" : "rowid AS __fh6_rowid, ";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT {selectPrefix}{visibleColumnSql} FROM {SqliteHelpers.Ident(table)} " +
            $"WHERE {SqliteHelpers.Ident(actualColumn)}=$value " +
            $"LIMIT {limit}";
        cmd.Parameters.AddWithValue("$value", NormalizeDbValue(value));
        return ReadDataTable(table, cmd);
    }

    private DataTable? TryLoadEnhancedDisplayTable(string table, long? selectedCarId, int limit)
    {
        var tuningTable = TryLoadTuningDisplayTable(table, selectedCarId, limit);
        if (tuningTable is not null)
        {
            return tuningTable;
        }

        if (!TableExists("List_TireCompound"))
        {
            return null;
        }
        var hasUpgradeTireCompound = TableExists("List_UpgradeTireCompound");

        if (table.Equals("List_UpgradeTireCompound", StringComparison.OrdinalIgnoreCase))
        {
            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "utc.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT " + selectPrefix + "utc.*, " +
                "tc.DisplayName AS TireCompoundName, " +
                "tc.DefaultPressure AS BaseDefaultPressure, " +
                "tc.TorqueFreeLatFrictionScale AS BaseLatFrictionScale, " +
                "tc.TorqueFreeLongFrictionScaleBrake AS BaseBrakeFrictionScale, " +
                "tc.TorqueFreeLongFrictionScaleAccel0 AS BaseAccelFrictionScale, " +
                "tc.WetFrictionModFrictionScale AS BaseWetFrictionScale, " +
                "tc.TireRollResistance AS BaseRollResistance " +
                "FROM List_UpgradeTireCompound utc " +
                "LEFT JOIN List_TireCompound tc ON tc.TireCompoundID = utc.TireCompoundID " +
                (selectedCarId.HasValue ? "WHERE utc.Ordinal=$carId " : "") +
                "ORDER BY utc.Ordinal, utc.IsStock DESC, utc.Level, utc.Id " +
                "LIMIT $limit";
            if (selectedCarId.HasValue)
            {
                cmd.Parameters.AddWithValue("$carId", selectedCarId.Value);
            }
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(
                ReadDataTable(table, cmd),
                "TireCompoundName",
                "BaseDefaultPressure",
                "BaseLatFrictionScale",
                "BaseBrakeFrictionScale",
                "BaseAccelFrictionScale",
                "BaseWetFrictionScale",
                "BaseRollResistance");
        }

        if (table.Equals("List_TireCompound", StringComparison.OrdinalIgnoreCase) && hasUpgradeTireCompound)
        {
            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "tc.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT " + selectPrefix + "tc.*, labels.KnownUpgradeLabels, labels.CarsUsingCompound " +
                "FROM List_TireCompound tc " +
                "LEFT JOIN (" + TireCompoundLabelSql() + ") labels ON labels.TireCompoundID = tc.TireCompoundID " +
                "ORDER BY tc.TireCompoundID LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(ReadDataTable(table, cmd), "KnownUpgradeLabels", "CarsUsingCompound");
        }

        if (table.Equals("List_TyreCurveDB", StringComparison.OrdinalIgnoreCase) && hasUpgradeTireCompound)
        {
            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "tdb.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT " + selectPrefix + "tdb.*, labels.KnownUpgradeLabels, labels.CarsUsingCompound " +
                "FROM List_TyreCurveDB tdb " +
                "LEFT JOIN (" + TireCompoundLabelSql() + ") labels ON labels.TireCompoundID = tdb.TireCompoundID " +
                "ORDER BY tdb.TireCompoundID LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(ReadDataTable(table, cmd), "KnownUpgradeLabels", "CarsUsingCompound");
        }

        if (table.Equals("Combo_TireBrandCompound", StringComparison.OrdinalIgnoreCase) && hasUpgradeTireCompound)
        {
            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "cbc.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT " + selectPrefix + "cbc.*, tc.DisplayName AS TireCompoundName, labels.KnownUpgradeLabels " +
                "FROM Combo_TireBrandCompound cbc " +
                "LEFT JOIN List_TireCompound tc ON tc.TireCompoundID = cbc.TireCompoundId " +
                "LEFT JOIN (" + TireCompoundLabelSql() + ") labels ON labels.TireCompoundID = cbc.TireCompoundId " +
                "ORDER BY cbc.TireBrandId, cbc.TireCompoundId LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(ReadDataTable(table, cmd), "TireCompoundName", "KnownUpgradeLabels");
        }

        if (table.Equals("List_UpgradeTireCompoundFictionModOverride", StringComparison.OrdinalIgnoreCase) && hasUpgradeTireCompound)
        {
            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "tfo.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT " + selectPrefix + "tfo.*, utc.TireModelName, utc.TireCompoundID, tc.DisplayName AS TireCompoundName " +
                "FROM List_UpgradeTireCompoundFictionModOverride tfo " +
                "LEFT JOIN List_UpgradeTireCompound utc ON utc.Id = tfo.PartId " +
                "LEFT JOIN List_TireCompound tc ON tc.TireCompoundID = utc.TireCompoundID " +
                (selectedCarId.HasValue ? "WHERE utc.Ordinal=$carId " : "") +
                "ORDER BY tfo.PartId LIMIT $limit";
            if (selectedCarId.HasValue)
            {
                cmd.Parameters.AddWithValue("$carId", selectedCarId.Value);
            }
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(ReadDataTable(table, cmd), "TireModelName", "TireCompoundID", "TireCompoundName");
        }

        return null;
    }

    private DataTable? TryLoadTuningDisplayTable(string table, long? selectedCarId, int limit)
    {
        if (table.Equals("Data_Car", StringComparison.OrdinalIgnoreCase))
        {
            var columns = GetColumns(table);
            if (!columns.Any(c => c.Name.Equals("CurbWeight", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "c.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT " + selectPrefix + "c.*, (c.CurbWeight * 100.0) AS CurbWeightKg " +
                "FROM Data_Car c " +
                (selectedCarId.HasValue ? "WHERE c.Id=$carId " : "") +
                "ORDER BY c.Id LIMIT $limit";
            if (selectedCarId.HasValue)
            {
                cmd.Parameters.AddWithValue("$carId", selectedCarId.Value);
            }
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadDataTable(table, cmd);
        }

        if (!selectedCarId.HasValue)
        {
            return null;
        }

        if (table.Equals("List_UpgradeCarBodyWeight", StringComparison.OrdinalIgnoreCase))
        {
            var bodyId = StockLinkedId("List_UpgradeCarBody", "CarBodyID", selectedCarId.Value);
            if (!bodyId.HasValue)
            {
                return null;
            }

            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "w.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT " + selectPrefix + "w.*, (w.Mass - w.InitialMass) AS EffectiveMassDiffKg " +
                "FROM List_UpgradeCarBodyWeight w " +
                "WHERE w.CarBodyId=$bodyId " +
                "ORDER BY w.IsStock DESC, w.Level, w.Id LIMIT $limit";
            cmd.Parameters.AddWithValue("$bodyId", bodyId.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(ReadDataTable(table, cmd), "EffectiveMassDiffKg");
        }

        if (table.Equals("List_UpgradeSpringDamper", StringComparison.OrdinalIgnoreCase) &&
            TableExists("List_SpringDamperPhysics"))
        {
            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "usd.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT " + selectPrefix + "usd.*, " +
                "f.DefRideHeight AS FrontDefRideHeight, f.MinRideHeight AS FrontMinRideHeight, f.MaxRideHeight AS FrontMaxRideHeight, " +
                "f.DefSpringRate AS FrontDefSpringRate, f.MinSpringRate AS FrontMinSpringRate, f.MaxSpringRate AS FrontMaxSpringRate, " +
                "f.DefDampenBumpRate AS FrontDefBump, f.MinDampenBumpRate AS FrontMinBump, f.MaxDampenBumpRate AS FrontMaxBump, " +
                "f.DefDampenReboundRate AS FrontDefRebound, f.MinDampenReboundRate AS FrontMinRebound, f.MaxDampenReboundRate AS FrontMaxRebound, " +
                "r.DefRideHeight AS RearDefRideHeight, r.MinRideHeight AS RearMinRideHeight, r.MaxRideHeight AS RearMaxRideHeight, " +
                "r.DefSpringRate AS RearDefSpringRate, r.MinSpringRate AS RearMinSpringRate, r.MaxSpringRate AS RearMaxSpringRate, " +
                "r.DefDampenBumpRate AS RearDefBump, r.MinDampenBumpRate AS RearMinBump, r.MaxDampenBumpRate AS RearMaxBump, " +
                "r.DefDampenReboundRate AS RearDefRebound, r.MinDampenReboundRate AS RearMinRebound, r.MaxDampenReboundRate AS RearMaxRebound " +
                "FROM List_UpgradeSpringDamper usd " +
                "LEFT JOIN List_SpringDamperPhysics f ON f.SpringDamperPhysicsID = usd.FrontSpringDamperPhysicsID " +
                "LEFT JOIN List_SpringDamperPhysics r ON r.SpringDamperPhysicsID = usd.RearSpringDamperPhysicsID " +
                "WHERE usd.Ordinal=$carId ORDER BY usd.IsStock DESC, usd.Level, usd.Id LIMIT $limit";
            cmd.Parameters.AddWithValue("$carId", selectedCarId.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(
                ReadDataTable(table, cmd),
                "FrontDefRideHeight", "FrontMinRideHeight", "FrontMaxRideHeight",
                "FrontDefSpringRate", "FrontMinSpringRate", "FrontMaxSpringRate",
                "FrontDefBump", "FrontMinBump", "FrontMaxBump",
                "FrontDefRebound", "FrontMinRebound", "FrontMaxRebound",
                "RearDefRideHeight", "RearMinRideHeight", "RearMaxRideHeight",
                "RearDefSpringRate", "RearMinSpringRate", "RearMaxSpringRate",
                "RearDefBump", "RearMinBump", "RearMaxBump",
                "RearDefRebound", "RearMinRebound", "RearMaxRebound");
        }

        if (table.Equals("List_SpringDamperPhysics", StringComparison.OrdinalIgnoreCase) &&
            TableExists("List_UpgradeSpringDamper"))
        {
            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "sp.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "WITH refs AS (" +
                "SELECT FrontSpringDamperPhysicsID AS PhysicsID, 'Front' AS Axle, Id AS UpgradeId, Level AS UpgradeLevel, IsStock AS UpgradeIsStock FROM List_UpgradeSpringDamper WHERE Ordinal=$carId " +
                "UNION ALL " +
                "SELECT RearSpringDamperPhysicsID AS PhysicsID, 'Rear' AS Axle, Id AS UpgradeId, Level AS UpgradeLevel, IsStock AS UpgradeIsStock FROM List_UpgradeSpringDamper WHERE Ordinal=$carId" +
                ") " +
                "SELECT " + selectPrefix + "sp.*, refs.Axle, refs.UpgradeId, refs.UpgradeLevel, refs.UpgradeIsStock " +
                "FROM refs JOIN List_SpringDamperPhysics sp ON sp.SpringDamperPhysicsID = refs.PhysicsID " +
                "ORDER BY refs.UpgradeIsStock DESC, refs.UpgradeLevel, refs.UpgradeId, refs.Axle LIMIT $limit";
            cmd.Parameters.AddWithValue("$carId", selectedCarId.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(ReadDataTable(table, cmd), "Axle", "UpgradeId", "UpgradeLevel", "UpgradeIsStock");
        }

        if ((table.Equals("List_UpgradeAntiSwayFront", StringComparison.OrdinalIgnoreCase) ||
             table.Equals("List_UpgradeAntiSwayRear", StringComparison.OrdinalIgnoreCase)) &&
            TableExists("List_AntiSwayPhysics"))
        {
            var columns = GetColumns(table);
            var alias = table.Equals("List_UpgradeAntiSwayFront", StringComparison.OrdinalIgnoreCase) ? "Front" : "Rear";
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "u.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT " + selectPrefix + "u.*, " +
                "asp.DefSwaybarStiffness AS " + alias + "DefSwaybar, " +
                "asp.MinSwaybarStiffness AS " + alias + "MinSwaybar, " +
                "asp.MaxSwaybarStiffness AS " + alias + "MaxSwaybar, " +
                "asp.SwaybarDamping AS " + alias + "SwaybarDamping " +
                "FROM " + SqliteHelpers.Ident(table) + " u " +
                "LEFT JOIN List_AntiSwayPhysics asp ON asp.AntiSwayPhysicsID = u.AntiSwayPhysicsID " +
                "WHERE u.Ordinal=$carId ORDER BY u.IsStock DESC, u.Level, u.Id LIMIT $limit";
            cmd.Parameters.AddWithValue("$carId", selectedCarId.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(
                ReadDataTable(table, cmd),
                alias + "DefSwaybar",
                alias + "MinSwaybar",
                alias + "MaxSwaybar",
                alias + "SwaybarDamping");
        }

        if (table.Equals("List_AntiSwayPhysics", StringComparison.OrdinalIgnoreCase) &&
            TableExists("List_UpgradeAntiSwayFront") &&
            TableExists("List_UpgradeAntiSwayRear"))
        {
            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "asp.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "WITH refs AS (" +
                "SELECT AntiSwayPhysicsID, 'Front' AS Axle, Id AS UpgradeId, Level AS UpgradeLevel, IsStock AS UpgradeIsStock FROM List_UpgradeAntiSwayFront WHERE Ordinal=$carId " +
                "UNION ALL " +
                "SELECT AntiSwayPhysicsID, 'Rear' AS Axle, Id AS UpgradeId, Level AS UpgradeLevel, IsStock AS UpgradeIsStock FROM List_UpgradeAntiSwayRear WHERE Ordinal=$carId" +
                ") " +
                "SELECT " + selectPrefix + "asp.*, refs.Axle, refs.UpgradeId, refs.UpgradeLevel, refs.UpgradeIsStock " +
                "FROM refs JOIN List_AntiSwayPhysics asp ON asp.AntiSwayPhysicsID = refs.AntiSwayPhysicsID " +
                "ORDER BY refs.UpgradeIsStock DESC, refs.UpgradeLevel, refs.UpgradeId, refs.Axle LIMIT $limit";
            cmd.Parameters.AddWithValue("$carId", selectedCarId.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(ReadDataTable(table, cmd), "Axle", "UpgradeId", "UpgradeLevel", "UpgradeIsStock");
        }

        if (table.Equals("List_UpgradeBrakes", StringComparison.OrdinalIgnoreCase) &&
            TableExists("List_BrakeProfile"))
        {
            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "b.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT " + selectPrefix + "b.*, bp.Name AS BrakeProfileName " +
                "FROM List_UpgradeBrakes b " +
                "LEFT JOIN List_BrakeProfile bp ON bp.BrakesProfileID = b.BrakesProfileID " +
                "WHERE b.Ordinal=$carId ORDER BY b.IsStock DESC, b.Level, b.Id LIMIT $limit";
            cmd.Parameters.AddWithValue("$carId", selectedCarId.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(ReadDataTable(table, cmd), "BrakeProfileName");
        }

        if (table.Equals("List_AeroPhysics", StringComparison.OrdinalIgnoreCase) &&
            TableExists("List_UpgradeRearWing"))
        {
            var bodyId = StockLinkedId("List_UpgradeCarBody", "CarBodyID", selectedCarId.Value);
            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "ap.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            var frontBumperRef = bodyId.HasValue && TableExists("List_UpgradeCarBodyFrontBumper")
                ? "UNION ALL SELECT AeroPhysicsID, 'FrontBumper' AS Part, Id AS UpgradeId, Level AS UpgradeLevel, IsStock AS UpgradeIsStock FROM List_UpgradeCarBodyFrontBumper WHERE CarBodyID=$bodyId AND AeroPhysicsID IS NOT NULL "
                : "";
            cmd.CommandText =
                "WITH refs AS (" +
                "SELECT AeroPhysicsID, 'RearWing' AS Part, Id AS UpgradeId, Level AS UpgradeLevel, IsStock AS UpgradeIsStock FROM List_UpgradeRearWing WHERE Ordinal=$carId AND AeroPhysicsID IS NOT NULL " +
                frontBumperRef +
                ") " +
                "SELECT " + selectPrefix + "ap.*, refs.Part, refs.UpgradeId, refs.UpgradeLevel, refs.UpgradeIsStock " +
                "FROM refs JOIN List_AeroPhysics ap ON ap.AeroPhysicsID = refs.AeroPhysicsID " +
                "ORDER BY refs.UpgradeIsStock DESC, refs.Part, refs.UpgradeLevel, refs.UpgradeId LIMIT $limit";
            cmd.Parameters.AddWithValue("$carId", selectedCarId.Value);
            if (bodyId.HasValue)
            {
                cmd.Parameters.AddWithValue("$bodyId", bodyId.Value);
            }
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(ReadDataTable(table, cmd), "Part", "UpgradeId", "UpgradeLevel", "UpgradeIsStock");
        }

        if (table.Equals("List_UpgradeRearWing", StringComparison.OrdinalIgnoreCase) &&
            TableExists("List_AeroPhysics"))
        {
            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "rw.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT " + selectPrefix + "rw.*, ap.DefaultTuneSlider, ap.Drag0, ap.Downforce0, ap.Drag1, ap.Downforce1 " +
                "FROM List_UpgradeRearWing rw " +
                "LEFT JOIN List_AeroPhysics ap ON ap.AeroPhysicsID = rw.AeroPhysicsID " +
                "WHERE rw.Ordinal=$carId ORDER BY rw.IsStock DESC, rw.Sequence, rw.Level, rw.Id LIMIT $limit";
            cmd.Parameters.AddWithValue("$carId", selectedCarId.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(ReadDataTable(table, cmd), "DefaultTuneSlider", "Drag0", "Downforce0", "Drag1", "Downforce1");
        }

        if (table.Equals("List_UpgradeCarBodyFrontBumper", StringComparison.OrdinalIgnoreCase) &&
            TableExists("List_AeroPhysics"))
        {
            var bodyId = StockLinkedId("List_UpgradeCarBody", "CarBodyID", selectedCarId.Value);
            if (!bodyId.HasValue)
            {
                return null;
            }

            var columns = GetColumns(table);
            var selectPrefix = columns.Any(c => c.PrimaryKeyRank > 0) ? "" : "fb.rowid AS __fh6_rowid, ";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT " + selectPrefix + "fb.*, ap.DefaultTuneSlider, ap.Drag0, ap.Downforce0, ap.Drag1, ap.Downforce1 " +
                "FROM List_UpgradeCarBodyFrontBumper fb " +
                "LEFT JOIN List_AeroPhysics ap ON ap.AeroPhysicsID = fb.AeroPhysicsID " +
                "WHERE fb.CarBodyID=$bodyId ORDER BY fb.IsStock DESC, fb.Sequence, fb.Level, fb.Id LIMIT $limit";
            cmd.Parameters.AddWithValue("$bodyId", bodyId.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            return MarkReadOnlyColumns(ReadDataTable(table, cmd), "DefaultTuneSlider", "Drag0", "Downforce0", "Drag1", "Downforce1");
        }

        return null;
    }

    public int ApplyTableChanges(string table, DataTable data)
    {
        if (!TableExists(table))
        {
            return 0;
        }

        var changed = data.GetChanges();
        if (changed is null)
        {
            return 0;
        }

        var columns = GetColumns(table);
        var writableColumns = columns.Select(c => c.Name).Where(data.Columns.Contains).ToList();
        var pk = columns.Where(c => c.PrimaryKeyRank > 0).OrderBy(c => c.PrimaryKeyRank).Select(c => c.Name).ToList();
        var useRowId = pk.Count == 0 && data.Columns.Contains("__fh6_rowid");

        using var tx = _connection.BeginTransaction();
        var applied = 0;
        try
        {
            foreach (DataRow row in data.Rows)
            {
                if (row.RowState == DataRowState.Unchanged || row.RowState == DataRowState.Detached)
                {
                    continue;
                }

                switch (row.RowState)
                {
                    case DataRowState.Added:
                        InsertRow(table, writableColumns, row, tx);
                        applied++;
                        break;
                    case DataRowState.Modified:
                        UpdateRow(table, writableColumns, pk, useRowId, row, tx);
                        applied++;
                        break;
                    case DataRowState.Deleted:
                        DeleteRow(table, pk, useRowId, row, tx);
                        applied++;
                        break;
                }
            }

            tx.Commit();
            data.AcceptChanges();
            return applied;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public void CloneTableRow(string table, DataRow source)
    {
        if (!TableExists(table))
        {
            throw new InvalidOperationException($"Table does not exist: {table}");
        }

        var columns = GetColumns(table).Select(c => c.Name).ToList();
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            row[column] = source.Table.Columns.Contains(column) ? source[column] : DBNull.Value;
        }

        if (columns.Contains("Id", StringComparer.OrdinalIgnoreCase))
        {
            row["Id"] = NextId(table, "Id");
        }

        if (columns.Contains("IsStock", StringComparer.OrdinalIgnoreCase))
        {
            row["IsStock"] = 0;
        }

        var linkColumn = FirstExisting(columns, "Ordinal", "EngineID", "CarBodyID", "CarBodyId", "DrivetrainID", "MotorID", "CarId");
        if (linkColumn is not null && source.Table.Columns.Contains(linkColumn))
        {
            row[linkColumn] = source[linkColumn];
            if (columns.Contains("Level", StringComparer.OrdinalIgnoreCase))
            {
                row["Level"] = MaxInt(table, "Level", $"{SqliteHelpers.Ident(linkColumn)}=$target", ("$target", source[linkColumn])) + 1;
            }
            if (columns.Contains("Sequence", StringComparer.OrdinalIgnoreCase))
            {
                row["Sequence"] = MaxInt(table, "Sequence", $"{SqliteHelpers.Ident(linkColumn)}=$target", ("$target", source[linkColumn])) + 1;
            }
        }

        using var tx = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            $"INSERT INTO {SqliteHelpers.Ident(table)} (" +
            string.Join(", ", columns.Select(SqliteHelpers.Ident)) +
            ") VALUES (" +
            string.Join(", ", columns.Select((_, i) => "$p" + i)) +
            ")";
        for (var i = 0; i < columns.Count; i++)
        {
            insert.Parameters.AddWithValue("$p" + i, NormalizeDbValue(row.GetValueOrDefault(columns[i], DBNull.Value)));
        }
        insert.ExecuteNonQuery();
        tx.Commit();

        if (row.TryGetValue("Level", out var level))
        {
            TryEnsureMenuMetadataForTableLevels(table, [ToLongOrZero(level)]);
        }
    }

    public string Validate()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        using var reader = cmd.ExecuteReader();
        var messages = new List<string>();
        while (reader.Read())
        {
            messages.Add(reader.GetString(0));
        }
        return string.Join(Environment.NewLine, messages);
    }

    public string ProbeUpgradeMenuChain(string table, long engineId)
    {
        if (!TableExists(table))
        {
            throw new InvalidOperationException($"Table does not exist: {table}");
        }

        var columns = GetColumns(table).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("EngineID") || !columns.Contains("Level"))
        {
            throw new InvalidOperationException($"{table} is not an engine part table with EngineID and Level columns.");
        }

        var sb = new StringBuilder();
        var findings = new List<string>();
        var selectedLevels = EnginePartLevelProbeRows(table, engineId);
        var selectedLevelSet = selectedLevels.Select(r => r.Level).ToHashSet();
        var observedMax = EditorConstants.ObservedEnginePartMenuMaxLevel(table);
        var specs = UpgradePartSpecsForTable(table);
        var stockAspirationId = EngineStockAspirationId(engineId);
        var aspirationSpec = AspirationSpecForTable(table);

        sb.AppendLine($"Upgrade Menu Probe");
        sb.AppendLine($"Table: {table}");
        sb.AppendLine($"EngineID: {engineId}");
        if (stockAspirationId.HasValue)
        {
            sb.AppendLine($"Engine stock AspirationID: {stockAspirationId.Value}");
        }
        sb.AppendLine();

        sb.AppendLine("1. Selected engine part rows");
        if (selectedLevels.Count == 0)
        {
            sb.AppendLine("   FAIL: no rows exist for this EngineID in this part table.");
            findings.Add("Add part rows first. Without rows for this EngineID, the upgrade menu has nothing to show.");
        }
        else
        {
            foreach (var row in selectedLevels)
            {
                var stock = row.StockRows > 0 ? $", stock rows {row.StockRows}" : "";
                sb.AppendLine($"   Level {row.Level}: {row.RowCount} row(s){stock}, Ids {Shorten(row.Ids, 140)}");
                if (row.RowCount > 1)
                {
                    findings.Add($"Duplicate Level {row.Level} rows exist for EngineID {engineId}. The game may pick one unpredictably.");
                }
            }
        }

        var allLevels = ExistingLevelsForUpgradeTable(table).OrderBy(v => v).ToList();
        sb.AppendLine($"   Table-wide levels: {FormatLevels(allLevels)}");
        if (observedMax.HasValue)
        {
            sb.AppendLine($"   Base-data max upgrade level: {observedMax.Value} (stock Level 0 is separate; base visible tiles can be stock + upgrades)");
            sb.AppendLine($"   Base-data expected visible tiles: {BaseVisibleTileText(observedMax.Value)}");
            var aboveCap = selectedLevels.Where(r => r.Level > observedMax.Value).Select(r => r.Level).Distinct().OrderBy(v => v).ToList();
            if (aboveCap.Count > 0)
            {
                findings.Add($"Levels {FormatLevels(aboveCap)} are above the base-data max upgrade level {observedMax.Value}. If imported with complete metadata and still hidden in-game, that proves a UI/filter cap candidate.");
            }
        }
        sb.AppendLine();

        sb.AppendLine("2. Data_UpgradePart mapping");
        if (specs.Count == 0)
        {
            sb.AppendLine("   FAIL: no Data_UpgradePart row maps this table to a menu PartName.");
            findings.Add("Data_UpgradePart is missing. Wire Menu cannot infer the menu type for this table.");
        }
        else
        {
            foreach (var spec in specs)
            {
                sb.AppendLine($"   Id {spec.Id}: Category={Blank(spec.CategoryName)}, PartName={Blank(spec.PartName)}");
            }
        }
        sb.AppendLine();

        foreach (var spec in specs)
        {
            sb.AppendLine($"3. Menu metadata for PartName {spec.PartName}");
            var typeRows = UpgradeTypeProbeRows(spec.PartName);
            if (typeRows.Count == 0)
            {
                sb.AppendLine("   FAIL: no UpgradeTypes row exists for this PartName.");
                findings.Add($"UpgradeTypes.{spec.PartName} is missing. Use Wire Menu before expecting this part to appear.");
                sb.AppendLine();
                continue;
            }

            if (typeRows.Count > 1)
            {
                findings.Add($"{spec.PartName} has {typeRows.Count} UpgradeTypes rows. Duplicates can make the UI pick unexpected labels/levels.");
            }

            foreach (var type in typeRows)
            {
                sb.AppendLine($"   TypeId {type.Id}: IsException={type.IsException}, DisplayOrder={type.DisplayOrder}, Name={Blank(type.Name)}");
                sb.AppendLine($"      Description={Blank(type.Description)}");
                sb.AppendLine($"      Icon={Blank(type.IconPath)}, Image={Blank(type.ImagePath)}");
                var areaLinks = UpgradeAreaLinksForType(type.Id);
                if (!string.IsNullOrWhiteSpace(areaLinks))
                {
                    sb.AppendLine($"      UpgradeArea links: {areaLinks}");
                }

                var menuLevels = UpgradeLevelProbeRows(type.Id);
                if (menuLevels.Count == 0)
                {
                    sb.AppendLine("      FAIL: no Upgrades rows exist for this TypeId.");
                    findings.Add($"Upgrades rows are missing for TypeId {type.Id} ({spec.PartName}).");
                    continue;
                }

                sb.AppendLine($"      Upgrades levels: {FormatLevels(menuLevels.Select(r => r.Level))}");
                foreach (var level in menuLevels)
                {
                    sb.AppendLine($"         Level {level.Level}: {level.RowCount} row(s), Ids {Shorten(level.Ids, 100)}, Names {Shorten(level.Names, 160)}");
                    if (level.RowCount > 1)
                    {
                        findings.Add($"TypeId {type.Id} has duplicate Upgrades rows for Level {level.Level}.");
                    }
                }

                var menuLevelSet = menuLevels.Select(r => r.Level).ToHashSet();
                var missingForRows = selectedLevelSet.Where(level => !menuLevelSet.Contains(level)).OrderBy(v => v).ToList();
                if (missingForRows.Count > 0)
                {
                    findings.Add($"Part rows include levels {FormatLevels(missingForRows)} but TypeId {type.Id} has no matching Upgrades metadata.");
                }
                var menuWithoutRows = menuLevelSet.Where(level => !selectedLevelSet.Contains(level)).OrderBy(v => v).ToList();
                if (selectedLevelSet.Count > 0 && menuWithoutRows.Count > 0)
                {
                    sb.AppendLine($"      Metadata levels not present on this engine: {FormatLevels(menuWithoutRows)}");
                }
            }
            sb.AppendLine();
        }

        foreach (var spec in specs)
        {
            sb.AppendLine($"4. UpgradeWizardParts level list for {spec.PartName}");
            var wizardLevels = UpgradeWizardPartProbeRows(spec.PartName);
            if (wizardLevels.Count == 0)
            {
                sb.AppendLine("   No rows. This may stop some upgrade menu/wizard paths from requesting extra levels.");
                if (selectedLevels.Any(r => r.RowCount > r.StockRows && r.Level > 0))
                {
                    findings.Add($"UpgradeWizardParts has no {spec.PartName} rows. Use Wire Menu so the game can see purchasable levels for this part family.");
                }
            }
            else
            {
                sb.AppendLine($"   PartLevels: {FormatLevels(wizardLevels.Select(r => r.Level))}");
                foreach (var level in wizardLevels)
                {
                    sb.AppendLine($"      Level {level.Level}: {level.RowCount} row(s), SortBalanced {Shorten(level.Sorts, 120)}");
                }
            }

            var wizardLevelSet = wizardLevels.Select(r => r.Level).ToHashSet();
            var missingWizardLevels = selectedLevels
                .Where(r => r.RowCount > r.StockRows && r.Level > 0 && !wizardLevelSet.Contains(r.Level))
                .Select(r => r.Level)
                .Distinct()
                .OrderBy(v => v)
                .ToList();
            if (missingWizardLevels.Count > 0)
            {
                findings.Add($"UpgradeWizardParts.{spec.PartName} is missing purchasable level(s) {FormatLevels(missingWizardLevels)}. Wire Menu now adds these rows.");
            }
            sb.AppendLine();
        }

        if (aspirationSpec is not null)
        {
            sb.AppendLine("5. Aspiration conversion chain");
            var aspirationRow = AspirationProbeRow(aspirationSpec.AspirationId);
            if (string.IsNullOrWhiteSpace(aspirationRow))
            {
                sb.AppendLine($"   FAIL: List_Aspiration row {aspirationSpec.AspirationId} is missing.");
                findings.Add($"List_Aspiration row {aspirationSpec.AspirationId} is missing, so the conversion category cannot point to {aspirationSpec.PartName}.");
            }
            else
            {
                sb.AppendLine($"   {aspirationRow}");
            }

            var aspirationMenu = AspirationMenuRows(aspirationSpec.AspirationId);
            if (string.IsNullOrWhiteSpace(aspirationMenu))
            {
                sb.AppendLine($"   FAIL: Upgrades TypeId 57 has no conversion row for AspirationID {aspirationSpec.AspirationId}.");
                findings.Add($"The Body Kits/Conversions aspiration tile for {HumanizePartName(aspirationSpec.PartName)} is missing from Upgrades TypeId 57.");
            }
            else
            {
                sb.AppendLine($"   Conversion menu row(s): {aspirationMenu}");
            }

            if (stockAspirationId.HasValue && stockAspirationId.Value != aspirationSpec.AspirationId)
            {
                sb.AppendLine($"   This engine is stock AspirationID {stockAspirationId.Value}; {aspirationSpec.AspirationId} must be selected as a conversion before its upgrade submenu is relevant.");
            }
            sb.AppendLine();
        }

        sb.AppendLine("6. Result");
        if (findings.Count == 0)
        {
            sb.AppendLine("   PASS: SQLite-side rows and metadata look complete for the selected engine/table.");
            if (observedMax.HasValue)
            {
                sb.AppendLine($"   No selected row is above the base-data max upgrade level {observedMax.Value}. To prove a cap, add a higher level, wire metadata, import, then check the game.");
            }
        }
        else
        {
            foreach (var finding in findings.Distinct())
            {
                sb.AppendLine("   - " + finding);
            }
        }

        return sb.ToString();
    }

    public string ProbeUpgradeMenuOverview(long engineId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Upgrade Menu Probe Overview");
        sb.AppendLine($"EngineID: {engineId}");
        var stockAspirationId = EngineStockAspirationId(engineId);
        if (stockAspirationId.HasValue)
        {
            sb.AppendLine($"Engine stock AspirationID: {stockAspirationId.Value}");
        }
        sb.AppendLine("Base-data max means max upgrade Level, not tile count. Stock is usually Level 0, so visible tiles can be stock + upgrade levels. A real cap is proven only after a higher level has complete metadata, is imported, and still does not show in-game.");
        sb.AppendLine();

        foreach (var table in EditorConstants.EnginePartTables.Where(TableExists))
        {
            var columns = GetColumns(table).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!columns.Contains("EngineID") || !columns.Contains("Level"))
            {
                continue;
            }

            var selectedLevels = EnginePartLevelProbeRows(table, engineId);
            var selectedLevelSet = selectedLevels.Select(r => r.Level).ToHashSet();
            var observedMax = EditorConstants.ObservedEnginePartMenuMaxLevel(table);
            var specs = UpgradePartSpecsForTable(table);
            var status = new List<string>();
            var wizardPieces = new List<string>();

            if (selectedLevels.Count == 0)
            {
                status.Add("no rows for this engine");
            }

            if (specs.Count == 0)
            {
                status.Add("missing Data_UpgradePart mapping");
            }

            var metadataPieces = new List<string>();
            foreach (var spec in specs)
            {
                var typeIds = UpgradeTypeIdsForPartName(spec.PartName);
                if (typeIds.Count == 0)
                {
                    status.Add($"missing UpgradeTypes.{spec.PartName}");
                    metadataPieces.Add($"{spec.PartName}: no UpgradeTypes");
                    continue;
                }

                foreach (var typeId in typeIds)
                {
                    var menuLevels = UpgradeLevelProbeRows(typeId);
                    var menuLevelSet = menuLevels.Select(r => r.Level).ToHashSet();
                    metadataPieces.Add($"{spec.PartName}/TypeId {typeId}: {FormatLevels(menuLevelSet)}");
                    var missing = selectedLevelSet.Where(level => !menuLevelSet.Contains(level)).OrderBy(v => v).ToList();
                    if (missing.Count > 0)
                    {
                        status.Add($"missing metadata for row levels {FormatLevels(missing)}");
                    }
                    var duplicateLevels = menuLevels.Where(r => r.RowCount > 1).Select(r => r.Level).Distinct().OrderBy(v => v).ToList();
                    if (duplicateLevels.Count > 0)
                    {
                        status.Add($"duplicate metadata levels {FormatLevels(duplicateLevels)}");
                    }
                }

                var wizardLevels = UpgradeWizardPartProbeRows(spec.PartName);
                wizardPieces.Add($"{spec.PartName}: {FormatLevels(wizardLevels.Select(r => r.Level))}");
                var wizardSet = wizardLevels.Select(r => r.Level).ToHashSet();
                var missingWizard = selectedLevels
                    .Where(r => r.RowCount > r.StockRows && r.Level > 0 && !wizardSet.Contains(r.Level))
                    .Select(r => r.Level)
                    .Distinct()
                    .OrderBy(v => v)
                    .ToList();
                if (missingWizard.Count > 0)
                {
                    status.Add($"missing UpgradeWizardParts levels {FormatLevels(missingWizard)}");
                }
            }

            var duplicatePartLevels = selectedLevels.Where(r => r.RowCount > 1).Select(r => r.Level).Distinct().OrderBy(v => v).ToList();
            if (duplicatePartLevels.Count > 0)
            {
                status.Add($"duplicate part levels {FormatLevels(duplicatePartLevels)}");
            }

            if (observedMax.HasValue)
            {
                var above = selectedLevels.Where(r => r.Level > observedMax.Value).Select(r => r.Level).Distinct().OrderBy(v => v).ToList();
                if (above.Count > 0)
                {
                    status.Add($"above base-data max upgrade level {observedMax.Value}: {FormatLevels(above)}");
                }
            }

            if (status.Count == 0)
            {
                status.Add("SQLite chain looks complete within base-data levels");
            }

            var aspirationSpec = AspirationSpecForTable(table);
            var aspirationText = aspirationSpec is null
                ? ""
                : $"; aspiration {aspirationSpec.AspirationId} {HumanizePartName(aspirationSpec.PartName)}";

            sb.AppendLine(table);
            sb.AppendLine($"  selected rows: {FormatPartLevelRows(selectedLevels)}");
            sb.AppendLine($"  table-wide levels: {FormatLevels(ExistingLevelsForUpgradeTable(table))}");
            sb.AppendLine($"  base-data max upgrade level: {(observedMax.HasValue ? observedMax.Value.ToString(CultureInfo.InvariantCulture) : "unknown")}{aspirationText}");
            if (observedMax.HasValue)
            {
                sb.AppendLine($"  base-data expected visible tiles: {BaseVisibleTileText(observedMax.Value)}");
            }
            sb.AppendLine($"  metadata: {(metadataPieces.Count == 0 ? "(none)" : string.Join("; ", metadataPieces))}");
            sb.AppendLine($"  wizard levels: {(wizardPieces.Count == 0 ? "(none)" : string.Join("; ", wizardPieces))}");
            sb.AppendLine($"  status: {string.Join("; ", status.Distinct())}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private IReadOnlyList<PartLevelProbe> EnginePartLevelProbeRows(string table, long engineId)
    {
        var columns = GetColumns(table).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var idColumn = columns.Contains("Id") ? "Id" : "rowid";
        var stockExpr = columns.Contains("IsStock") ? "SUM(CASE WHEN CAST(IsStock AS INTEGER) <> 0 THEN 1 ELSE 0 END)" : "0";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT CAST(Level AS INTEGER) AS LevelValue, COUNT(*) AS RowCount, {stockExpr} AS StockRows, " +
            $"GROUP_CONCAT(CAST({SqliteHelpers.Ident(idColumn)} AS TEXT), ',') AS Ids " +
            $"FROM {SqliteHelpers.Ident(table)} WHERE EngineID=$engineId " +
            "GROUP BY CAST(Level AS INTEGER) ORDER BY CAST(Level AS INTEGER)";
        cmd.Parameters.AddWithValue("$engineId", engineId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<PartLevelProbe>();
        while (reader.Read())
        {
            rows.Add(new PartLevelProbe(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }
        return rows;
    }

    private IReadOnlyList<UpgradeTypeProbe> UpgradeTypeProbeRows(string partName)
    {
        if (!TableExists("UpgradeTypes"))
        {
            return [];
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, COALESCE(IsException, 0), COALESCE(DisplayOrder, 0), " +
            "COALESCE(Name, ''), COALESCE(Description, ''), COALESCE(IconPath, ''), COALESCE(ImagePath, '') " +
            "FROM UpgradeTypes WHERE PartName=$partName ORDER BY IsException, id";
        cmd.Parameters.AddWithValue("$partName", partName);
        using var reader = cmd.ExecuteReader();
        var rows = new List<UpgradeTypeProbe>();
        while (reader.Read())
        {
            rows.Add(new UpgradeTypeProbe(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }
        return rows;
    }

    private IReadOnlyList<UpgradeLevelProbe> UpgradeLevelProbeRows(long typeId)
    {
        if (!TableExists("Upgrades"))
        {
            return [];
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT CAST(Level AS INTEGER) AS LevelValue, COUNT(*) AS RowCount, " +
            "GROUP_CONCAT(CAST(id AS TEXT), ','), " +
            "GROUP_CONCAT(CAST(id AS TEXT) || ':' || COALESCE(Name, ''), ' | ') " +
            "FROM Upgrades WHERE CAST(TypeId AS INTEGER)=$typeId " +
            "GROUP BY CAST(Level AS INTEGER) ORDER BY CAST(Level AS INTEGER), id";
        cmd.Parameters.AddWithValue("$typeId", typeId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<UpgradeLevelProbe>();
        while (reader.Read())
        {
            rows.Add(new UpgradeLevelProbe(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }
        return rows;
    }

    private IReadOnlyList<UpgradeWizardPartProbe> UpgradeWizardPartProbeRows(string partName)
    {
        if (!TableExists("UpgradeWizardParts"))
        {
            return [];
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT CAST(PartLevel AS INTEGER) AS LevelValue, COUNT(*) AS RowCount, " +
            "GROUP_CONCAT(CAST(SortBalanced AS TEXT), ',') " +
            "FROM UpgradeWizardParts WHERE PartName=$partName " +
            "GROUP BY CAST(PartLevel AS INTEGER) ORDER BY CAST(PartLevel AS INTEGER)";
        cmd.Parameters.AddWithValue("$partName", partName);
        using var reader = cmd.ExecuteReader();
        var rows = new List<UpgradeWizardPartProbe>();
        while (reader.Read())
        {
            rows.Add(new UpgradeWizardPartProbe(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? "" : reader.GetString(2)));
        }
        return rows;
    }

    private string UpgradeAreaLinksForType(long typeId)
    {
        if (!TableExists("UpgradeAreaForUpgradeType"))
        {
            return "";
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT GROUP_CONCAT(CAST(UpgradeAreaId AS TEXT), ',') " +
            "FROM UpgradeAreaForUpgradeType WHERE CAST(UpgradeTypeId AS INTEGER)=$typeId";
        cmd.Parameters.AddWithValue("$typeId", typeId);
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    private string AspirationProbeRow(long aspirationId)
    {
        if (!TableExists("List_Aspiration"))
        {
            return "";
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT AspirationID, Aspiration, DisplayName, ShortDisplayName, KeyPartName " +
            "FROM List_Aspiration WHERE CAST(AspirationID AS INTEGER)=$id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", aspirationId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return "";
        }

        var aspiration = reader.IsDBNull(1) ? "" : reader.GetString(1);
        var displayName = reader.IsDBNull(2) ? "" : reader.GetString(2);
        var shortDisplayName = reader.IsDBNull(3) ? "" : reader.GetString(3);
        var keyPartName = reader.IsDBNull(4) ? "" : reader.GetString(4);
        return $"List_Aspiration {reader.GetValue(0)}: Aspiration={Blank(aspiration)}, DisplayName={Blank(displayName)}, ShortDisplayName={Blank(shortDisplayName)}, KeyPartName={Blank(keyPartName)}";
    }

    private string AspirationMenuRows(long aspirationId)
    {
        if (!TableExists("Upgrades"))
        {
            return "";
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT GROUP_CONCAT(CAST(id AS TEXT) || ':L' || CAST(Level AS TEXT) || ':' || COALESCE(Name, ''), ' | ') " +
            "FROM Upgrades WHERE CAST(TypeId AS INTEGER)=57 AND CAST(IsException AS INTEGER)=0 AND CAST(Level AS INTEGER)=$level";
        cmd.Parameters.AddWithValue("$level", aspirationId);
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    private static string FormatLevels(IEnumerable<long> levels)
    {
        var ordered = levels.Distinct().OrderBy(v => v).ToList();
        return ordered.Count == 0 ? "(none)" : string.Join(", ", ordered);
    }

    private static string FormatPartLevelRows(IReadOnlyList<PartLevelProbe> rows)
    {
        if (rows.Count == 0)
        {
            return "(none)";
        }

        return string.Join(", ", rows.Select(row =>
        {
            var stock = row.StockRows > 0 ? " stock" : "";
            return $"L{row.Level}x{row.RowCount}{stock}";
        }));
    }

    private static string BaseVisibleTileText(long maxUpgradeLevel)
    {
        if (maxUpgradeLevel <= 0)
        {
            return "stock Level 0 only";
        }

        return $"Level 0 stock + Levels 1-{maxUpgradeLevel} upgrades = {maxUpgradeLevel + 1} tile(s) when stock is shown";
    }

    private static string Blank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(blank)" : value;
    }

    private static string Shorten(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }
        return value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";
    }

    public void SaveAs(string outputPath)
    {
        ApplyWalCheckpointIfPossible();
        var finalPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var tempPath = Path.Combine(Path.GetDirectoryName(finalPath)!, "." + Path.GetFileName(finalPath) + ".tmp");
        AppPaths.RemoveSqliteSidecars(tempPath);
        File.Delete(tempPath);

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "VACUUM main INTO " + SqliteHelpers.QuoteSqlString(tempPath);
            cmd.ExecuteNonQuery();
        }

        AppPaths.RemoveSqliteSidecars(finalPath);
        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }
        File.Move(tempPath, finalPath);
        AppPaths.RemoveSqliteSidecars(finalPath);
    }

    public void FlushForImport()
    {
        ApplyWalCheckpointIfPossible();
    }

    public List<(string SourceTable, long SourceId, string Label)> EnginePartTemplates(string targetTable, long? engineId)
    {
        if (!TableExists(targetTable))
        {
            return [];
        }

        var sourceTables = EditorConstants.CompatibleEnginePartSourceTables(targetTable).Where(TableExists).ToList();
        var templates = new List<(string SourceTable, long SourceId, string Label)>();
        foreach (var sourceTable in sourceTables)
        {
            var columns = GetColumns(sourceTable).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!columns.Contains("Id") || !columns.Contains("EngineID"))
            {
                continue;
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"SELECT t.*, e.MediaName AS EngineMediaName FROM {SqliteHelpers.Ident(sourceTable)} t " +
                "LEFT JOIN Data_Engine e ON e.EngineID = t.EngineID " +
                "ORDER BY CASE WHEN t.EngineID=$engineId THEN 0 ELSE 1 END, t.EngineID, t.Level, t.Id LIMIT 250";
            cmd.Parameters.AddWithValue("$engineId", engineId ?? -1);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var idOrdinal = reader.GetOrdinal("Id");
                var sourceId = Convert.ToInt64(reader.GetValue(idOrdinal), CultureInfo.InvariantCulture);
                templates.Add((sourceTable, sourceId, EnginePartTemplateLabel(sourceTable, reader)));
            }
        }
        return templates;
    }

    public List<(string SourceTable, long SourceId, string Label)> AeroPartTemplates(string targetTable, long? carId)
    {
        if (!EditorConstants.AeroOptionTables.Contains(targetTable, StringComparer.OrdinalIgnoreCase) || !TableExists(targetTable))
        {
            return [];
        }

        var columns = GetColumns(targetTable).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("Id"))
        {
            return [];
        }

        var linkColumn = targetTable.Equals("List_UpgradeRearWing", StringComparison.OrdinalIgnoreCase)
            ? "Ordinal"
            : "CarBodyID";
        if (!columns.Contains(linkColumn))
        {
            return [];
        }

        long? target = null;
        if (carId.HasValue)
        {
            try
            {
                target = AeroTarget(targetTable, carId.Value).Target;
            }
            catch
            {
                target = null;
            }
        }

        var hasAeroPhysics = columns.Contains("AeroPhysicsID") && TableExists("List_AeroPhysics");
        var isRearWing = targetTable.Equals("List_UpgradeRearWing", StringComparison.OrdinalIgnoreCase);
        using var cmd = _connection.CreateCommand();
        var sourceCarJoin = isRearWing
            ? "LEFT JOIN Data_Car dc ON dc.Id = t.Ordinal "
            : "LEFT JOIN List_UpgradeCarBody cb ON cb.CarBodyID = t.CarBodyID LEFT JOIN Data_Car dc ON dc.Id = cb.Ordinal ";
        cmd.CommandText =
            "SELECT t.*, dc.MediaName AS SourceCarName, dc.Year AS SourceCarYear, " +
            (TableExists("List_CarMake") ? "mk.ManufacturerCode AS SourceMake, " : "NULL AS SourceMake, ") +
            (hasAeroPhysics ? "ap.DefaultTuneSlider, ap.Drag0, ap.Downforce0, ap.Drag1, ap.Downforce1 " : "NULL AS DefaultTuneSlider, NULL AS Drag0, NULL AS Downforce0, NULL AS Drag1, NULL AS Downforce1 ") +
            $"FROM {SqliteHelpers.Ident(targetTable)} t " +
            sourceCarJoin +
            (TableExists("List_CarMake") ? "LEFT JOIN List_CarMake mk ON mk.ID = dc.MakeID " : "") +
            (hasAeroPhysics ? "LEFT JOIN List_AeroPhysics ap ON ap.AeroPhysicsID = t.AeroPhysicsID " : "") +
            "ORDER BY " +
            $"CASE WHEN $target >= 0 AND t.{SqliteHelpers.Ident(linkColumn)}=$target THEN 0 ELSE 1 END, " +
            "CAST(COALESCE(t.IsStock, 0) AS INTEGER) ASC, " +
            (hasAeroPhysics ? AeroPhysicsHasRangeSql("ap") + " DESC, " + AeroPhysicsAllZeroSql("ap") + " ASC, " : "") +
            "CAST(COALESCE(t.Level, 0) AS INTEGER) DESC, CAST(COALESCE(t.Sequence, 0) AS INTEGER) DESC, t.Id DESC";
        cmd.Parameters.AddWithValue("$target", target ?? -1);

        var templates = new List<(string SourceTable, long SourceId, string Label)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sourceId = Convert.ToInt64(reader.GetValue(reader.GetOrdinal("Id")), CultureInfo.InvariantCulture);
            templates.Add((targetTable, sourceId, AeroPartTemplateLabel(targetTable, reader, hasAeroPhysics)));
        }
        return templates;
    }

    public List<AspirationConversionChoice> AspirationConversionChoices(long engineId)
    {
        if (!TableExists("List_Aspiration") || !TableExists("Data_UpgradePart"))
        {
            return [];
        }

        var stockAspirationId = EngineStockAspirationId(engineId);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT a.AspirationID, a.KeyPartName, p.TableName " +
            "FROM List_Aspiration a " +
            "JOIN Data_UpgradePart p ON p.PartName=a.KeyPartName " +
            "WHERE a.KeyPartName IS NOT NULL AND a.KeyPartName <> '' " +
            "ORDER BY CAST(a.AspirationID AS INTEGER)";
        using var reader = cmd.ExecuteReader();
        var mappedRows = new List<(long AspirationId, string PartName, string TableName)>();
        while (reader.Read())
        {
            var aspirationId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            var partName = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var tableName = reader.IsDBNull(2) ? "" : reader.GetString(2);
            mappedRows.Add((aspirationId, partName, tableName));
        }

        var choices = new List<AspirationConversionChoice>();
        foreach (var (aspirationId, partName, tableName) in mappedRows)
        {
            if (string.IsNullOrWhiteSpace(tableName) ||
                !EditorConstants.EnginePartTables.Contains(tableName, StringComparer.OrdinalIgnoreCase) ||
                !TableExists(tableName))
            {
                continue;
            }

            var existingRows = EnginePartRowCount(tableName, engineId);
            var stock = stockAspirationId.HasValue && stockAspirationId.Value == aspirationId;
            var label = $"{aspirationId} | {HumanizePartName(partName)} -> {tableName} | {existingRows} row(s)";
            if (stock)
            {
                label += " | stock";
            }
            choices.Add(new AspirationConversionChoice(aspirationId, tableName, partName, label, existingRows, stock));
        }

        return choices;
    }

    public string AddAspirationConversion(long engineId, long aspirationId)
    {
        var choice = AspirationConversionChoices(engineId).FirstOrDefault(c => c.AspirationId == aspirationId);
        if (choice is null)
        {
            throw new InvalidOperationException("That aspiration conversion is not mapped to an engine part table in this DB.");
        }

        var sourceRows = EnginePartConversionSourceRows(choice.TableName, engineId);
        if (sourceRows.Count == 0)
        {
            throw new InvalidOperationException($"No compatible source rows exist to build {HumanizePartName(choice.PartName)} for EngineID {engineId}.");
        }
        var seedRow = sourceRows[0];
        sourceRows = sourceRows.Where(row => row.Level > 0).ToList();
        if (sourceRows.Count == 0)
        {
            sourceRows.Add((seedRow.SourceTable, seedRow.SourceId, 1));
        }
        if (sourceRows.All(row => row.Level != 1))
        {
            var seed = sourceRows[0];
            sourceRows.Insert(0, (seed.SourceTable, seed.SourceId, 1));
        }

        var added = 0;
        var skipped = 0;
        foreach (var source in sourceRows)
        {
            if (EnginePartLevelExists(choice.TableName, engineId, source.Level))
            {
                skipped++;
                continue;
            }

            AddEnginePartFromTemplateCore(choice.TableName, engineId, source.SourceTable, source.SourceId, source.Level, preferEngineLevelId: true);
            added++;
        }

        var metadata = EnsureMenuMetadataForTable(choice.TableName);
        var action = added == 0
            ? $"{HumanizePartName(choice.PartName)} rows already existed for EngineID {engineId}"
            : $"Added {added} {HumanizePartName(choice.PartName)} row(s) to {choice.TableName} for EngineID {engineId}";
        if (skipped > 0)
        {
            action += $" ({skipped} level(s) already existed)";
        }
        return $"{action}. {metadata}";
    }

    public string RemoveAspirationConversion(long engineId, long aspirationId)
    {
        var choice = AspirationConversionChoices(engineId).FirstOrDefault(c => c.AspirationId == aspirationId);
        if (choice is null)
        {
            throw new InvalidOperationException("That aspiration conversion is not mapped to an engine part table in this DB.");
        }
        if (!TableExists(choice.TableName))
        {
            throw new InvalidOperationException($"Table does not exist: {choice.TableName}");
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {SqliteHelpers.Ident(choice.TableName)} WHERE EngineID=$engineId";
        cmd.Parameters.AddWithValue("$engineId", engineId);
        var removed = cmd.ExecuteNonQuery();
        var cleanup = new List<string>();
        CleanupAspirationMetadataAfterRemove(choice, cleanup);

        var message = removed == 0
            ? $"{choice.TableName}: no {HumanizePartName(choice.PartName)} rows existed for EngineID {engineId}."
            : $"{choice.TableName}: removed {removed} {HumanizePartName(choice.PartName)} row(s) for EngineID {engineId}.";
        return cleanup.Count == 0 ? message : $"{message} {string.Join(", ", cleanup)}.";
    }

    public void AddEnginePartFromTemplate(string targetTable, long engineId, string sourceTable, long sourceId)
    {
        AddEnginePartFromTemplateCore(targetTable, engineId, sourceTable, sourceId, desiredLevel: null, preferEngineLevelId: false);
    }

    private long AddEnginePartFromTemplateCore(
        string targetTable,
        long engineId,
        string sourceTable,
        long sourceId,
        long? desiredLevel,
        bool preferEngineLevelId)
    {
        if (!TableExists(targetTable) || !TableExists(sourceTable))
        {
            throw new InvalidOperationException("Target or source table does not exist.");
        }
        if (!EditorConstants.CompatibleEnginePartSourceTables(targetTable).Contains(sourceTable, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Selected template table is not compatible with this engine part table.");
        }

        var targetColumns = GetColumns(targetTable);
        var targetNames = targetColumns.Select(c => c.Name).ToList();
        if (!targetNames.Contains("Id", StringComparer.OrdinalIgnoreCase) ||
            !targetNames.Contains("EngineID", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Target engine part table needs Id and EngineID columns.");
        }

        var sourceColumns = GetColumns(sourceTable).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT * FROM {SqliteHelpers.Ident(sourceTable)} WHERE Id = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", sourceId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("Template row was not found.");
            }

            foreach (var column in targetNames)
            {
                if (sourceColumns.Contains(column) && TryGetOrdinal(reader, column, out var ordinal))
                {
                    values[column] = SqliteHelpers.ReaderValue(reader, ordinal);
                }
                else
                {
                    var info = targetColumns.First(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
                    values[column] = DefaultCloneValue(info.Type);
                }
            }
        }

        values["EngineID"] = engineId;
        if (values.TryGetValue("IsStock", out var isStock) && Convert.ToInt64(isStock, CultureInfo.InvariantCulture) != 0)
        {
            values["IsStock"] = 0;
        }
        if (targetNames.Contains("Level", StringComparer.OrdinalIgnoreCase))
        {
            values["Level"] = desiredLevel ?? NextEnginePartLevel(targetTable, engineId, values.GetValueOrDefault("Level"));
        }
        if (targetNames.Contains("Sequence", StringComparer.OrdinalIgnoreCase))
        {
            values["Sequence"] = MaxInt(targetTable, "Sequence", "EngineID=$engineId", ("$engineId", engineId)) + 1;
        }
        values["Id"] = preferEngineLevelId && targetNames.Contains("Level", StringComparer.OrdinalIgnoreCase)
            ? NextEnginePartId(targetTable, engineId, ToLongOrZero(values.GetValueOrDefault("Level")), ToLongOrZero(values.GetValueOrDefault("IsStock")) != 0)
            : NextId(targetTable, "Id");

        var insertColumns = targetNames.Where(values.ContainsKey).ToList();
        using var tx = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            $"INSERT INTO {SqliteHelpers.Ident(targetTable)} (" +
            string.Join(", ", insertColumns.Select(SqliteHelpers.Ident)) +
            ") VALUES (" +
            string.Join(", ", insertColumns.Select((_, i) => "$p" + i)) +
            ")";
        for (var i = 0; i < insertColumns.Count; i++)
        {
            insert.Parameters.AddWithValue("$p" + i, NormalizeDbValue(values[insertColumns[i]]));
        }
        insert.ExecuteNonQuery();
        tx.Commit();

        if (values.TryGetValue("Level", out var level))
        {
            TryEnsureMenuMetadataForTableLevels(targetTable, [ToLongOrZero(level)]);
        }
        return ToLongOrZero(values.GetValueOrDefault("Id"));
    }

    private long? EngineStockAspirationId(long engineId)
    {
        if (!TableExists("Data_Engine") ||
            !GetColumns("Data_Engine").Any(c => c.Name.Equals("AspirationID_Stock", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT AspirationID_Stock FROM Data_Engine WHERE EngineID=$engineId LIMIT 1";
        cmd.Parameters.AddWithValue("$engineId", engineId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private long EnginePartRowCount(string table, long engineId)
    {
        if (!TableExists(table) ||
            !GetColumns(table).Any(c => c.Name.Equals("EngineID", StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {SqliteHelpers.Ident(table)} WHERE EngineID=$engineId";
        cmd.Parameters.AddWithValue("$engineId", engineId);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private bool EnginePartLevelExists(string table, long engineId, long level)
    {
        var columns = GetColumns(table).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("EngineID") || !columns.Contains("Level"))
        {
            return false;
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT 1 FROM {SqliteHelpers.Ident(table)} " +
            "WHERE EngineID=$engineId AND CAST(Level AS INTEGER)=$level LIMIT 1";
        cmd.Parameters.AddWithValue("$engineId", engineId);
        cmd.Parameters.AddWithValue("$level", level);
        return cmd.ExecuteScalar() is not null;
    }

    private List<(string SourceTable, long SourceId, long Level)> EnginePartConversionSourceRows(string targetTable, long engineId)
    {
        var sourceTables = EditorConstants.CompatibleEnginePartSourceTables(targetTable)
            .Where(TableExists)
            .OrderBy(t => t.Equals(targetTable, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();

        foreach (var sourceTable in sourceTables)
        {
            var sameEngine = EnginePartSourceRowsForEngine(sourceTable, engineId);
            if (sameEngine.Count > 0)
            {
                return sameEngine;
            }
        }

        foreach (var sourceTable in sourceTables)
        {
            var fallbackEngine = BestFallbackEngineIdForPartTable(sourceTable);
            if (fallbackEngine.HasValue)
            {
                var fallbackRows = EnginePartSourceRowsForEngine(sourceTable, fallbackEngine.Value);
                if (fallbackRows.Count > 0)
                {
                    return fallbackRows;
                }
            }
        }

        return [];
    }

    private List<(string SourceTable, long SourceId, long Level)> EnginePartSourceRowsForEngine(string sourceTable, long engineId)
    {
        var columns = GetColumns(sourceTable).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("Id") || !columns.Contains("EngineID") || !columns.Contains("Level"))
        {
            return [];
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT Id, CAST(COALESCE(Level, 0) AS INTEGER) AS LevelValue FROM {SqliteHelpers.Ident(sourceTable)} " +
            "WHERE EngineID=$engineId " +
            "ORDER BY CAST(Level AS INTEGER), Id";
        cmd.Parameters.AddWithValue("$engineId", engineId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string SourceTable, long SourceId, long Level)>();
        var seenLevels = new HashSet<long>();
        while (reader.Read())
        {
            var sourceId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            var level = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (seenLevels.Add(level))
            {
                rows.Add((sourceTable, sourceId, level));
            }
        }
        return rows;
    }

    private long? BestFallbackEngineIdForPartTable(string sourceTable)
    {
        var columns = GetColumns(sourceTable).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("EngineID") || !columns.Contains("Level"))
        {
            return null;
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT EngineID FROM {SqliteHelpers.Ident(sourceTable)} " +
            "GROUP BY EngineID " +
            "ORDER BY COUNT(*) DESC, EngineID LIMIT 1";
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private long NextEnginePartLevel(string targetTable, long engineId, object? sourceLevel)
    {
        var existingMax = MaxInt(targetTable, "Level", "EngineID=$engineId", ("$engineId", engineId));
        return existingMax > 0 ? existingMax + 1 : Math.Max(1, ToLongOrZero(sourceLevel));
    }

    private long NextEnginePartId(string targetTable, long engineId, long level, bool isStock)
    {
        long candidate;
        try
        {
            candidate = checked((engineId * 1000) + (isStock ? 0 : Math.Max(1, level)));
        }
        catch (OverflowException)
        {
            return NextId(targetTable, "Id");
        }

        while (ColumnValueExists(targetTable, "Id", candidate))
        {
            candidate++;
        }
        return candidate;
    }

    public string EnsureAspirationTypeForEnginePart(string table)
    {
        if (!EditorConstants.IsAspirationEnginePartTable(table))
        {
            throw new InvalidOperationException("This table is not a turbo or supercharger aspiration table.");
        }

        return EnsureMenuMetadataForTable(table);
    }

    public bool CanWireMenuMetadata(string table)
    {
        return TableExists("Data_UpgradePart") &&
               TableExists("UpgradeTypes") &&
               TableExists("Upgrades") &&
               UpgradePartSpecsForTable(table).Count > 0;
    }

    public IReadOnlyList<string> UpgradePartNamesForTable(string table)
    {
        return UpgradePartSpecsForTable(table)
            .Select(spec => spec.PartName)
            .Where(partName => !string.IsNullOrWhiteSpace(partName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string EnsureMenuMetadataForTable(string table)
    {
        var specs = UpgradePartSpecsForTable(table);
        if (specs.Count == 0)
        {
            throw new InvalidOperationException($"{table} is not mapped in Data_UpgradePart, so the editor cannot infer menu metadata.");
        }
        if (!TableExists("List_Aspiration") || !TableExists("UpgradeTypes") || !TableExists("Upgrades"))
        {
            throw new InvalidOperationException("The DB is missing the global upgrade metadata tables.");
        }

        var levels = ExistingLevelsForUpgradeTable(table);
        return EnsureMenuMetadataForTableLevels(table, levels);
    }

    private string EnsureMenuMetadataForTableLevels(string table, IReadOnlyCollection<long> levels)
    {
        var specs = UpgradePartSpecsForTable(table);
        if (specs.Count == 0)
        {
            throw new InvalidOperationException($"{table} is not mapped in Data_UpgradePart, so the editor cannot infer menu metadata.");
        }

        var changes = new List<string>();
        foreach (var spec in specs)
        {
            EnsureMenuMetadataForSpec(spec, levels, changes);
        }

        return changes.Count == 0
            ? $"{table} menu metadata already exists for the loaded levels."
            : $"Wired {table} menu metadata: {string.Join(", ", changes)}.";
    }

    private void TryEnsureMenuMetadataForTableLevels(string table, IReadOnlyCollection<long> levels)
    {
        if (levels.Count == 0 || !CanWireMenuMetadata(table))
        {
            return;
        }

        EnsureMenuMetadataForTableLevels(table, levels);
    }

    public void AddLinkedUpgradeOption(string table, long carId)
    {
        if (!EditorConstants.UpgradeTableLinks.TryGetValue(table, out var link))
        {
            throw new InvalidOperationException("This is not a per-car upgrade table.");
        }

        var target = LinkedUpgradeTarget(carId, link.LinkKind);
        if (!target.HasValue)
        {
            throw new InvalidOperationException($"Could not resolve {link.LinkKind} target for the selected car.");
        }

        AddLinkedOptionRow(table, link.LinkColumn, target.Value);
    }

    public string AddLinkedAeroOption(string table, long carId, long? sourceId = null)
    {
        if (!EditorConstants.AeroOptionTables.Contains(table, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This aero table cannot add options directly. Use a List_Upgrade* aero table such as List_UpgradeRearWing.");
        }

        var (linkColumn, target) = AeroTarget(table, carId);
        var result = AddAeroOptionRow(table, linkColumn, target, carId, sourceId);
        var physics = result.AeroPhysicsId.HasValue
            ? $", dedicated AeroPhysicsID {result.AeroPhysicsId.Value}"
            : "";
        return $"Added {table} Id {result.Id} Level {result.Level} Sequence {result.Sequence}{physics}. Menu metadata was wired for this aero table.";
    }

    private AeroOptionAddResult AddAeroOptionRow(string table, string linkColumn, long target, long carId, long? sourceId)
    {
        if (!TableExists(table))
        {
            throw new InvalidOperationException($"Table does not exist: {table}");
        }

        var columns = GetColumns(table);
        var names = columns.Select(c => c.Name).ToList();
        if (!names.Contains("Id", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{table} needs an Id column to add aero options.");
        }
        var actualLinkColumn = names.FirstOrDefault(c => c.Equals(linkColumn, StringComparison.OrdinalIgnoreCase));
        if (actualLinkColumn is null)
        {
            throw new InvalidOperationException($"{table} does not have link column {linkColumn}.");
        }

        var source = sourceId.HasValue
            ? SourceAeroRowById(table, names, "Id", sourceId.Value)
            : SourceAeroRowForClone(table, names, "Id", actualLinkColumn, target);
        if (source.Count == 0)
        {
            throw new InvalidOperationException(sourceId.HasValue
                ? $"Selected aero template Id {sourceId.Value} does not exist in {table}."
                : $"No source rows exist in {table}.");
        }

        var row = CloneOrDefaultRow(columns, source);
        var id = NextLinkedOptionId(table, "Id", actualLinkColumn, target, carId);
        var sourceLevel = ToLongOrZero(source.GetValueOrDefault("Level"));
        var level = names.Contains("Level", StringComparer.OrdinalIgnoreCase)
            ? sourceLevel > 0
                ? sourceLevel
                : MaxInt(table, "Level", $"{SqliteHelpers.Ident(actualLinkColumn)}=$target", ("$target", target)) + 1
            : 0;
        var sequence = names.Contains("Sequence", StringComparer.OrdinalIgnoreCase)
            ? MaxInt(table, "Sequence", $"{SqliteHelpers.Ident(actualLinkColumn)}=$target", ("$target", target)) + 1
            : 0;

        row["Id"] = id;
        row[actualLinkColumn] = target;
        if (names.Contains("IsStock", StringComparer.OrdinalIgnoreCase))
        {
            row["IsStock"] = 0;
        }
        if (names.Contains("Level", StringComparer.OrdinalIgnoreCase))
        {
            row["Level"] = level;
        }
        if (names.Contains("Sequence", StringComparer.OrdinalIgnoreCase))
        {
            row["Sequence"] = sequence;
        }
        if (names.Contains("RequiresGraphics", StringComparer.OrdinalIgnoreCase))
        {
            row["RequiresGraphics"] = 1;
        }
        if (names.Contains("Price", StringComparer.OrdinalIgnoreCase) && ToLongOrZero(row.GetValueOrDefault("Price")) <= 0)
        {
            row["Price"] = 1000;
        }

        long? aeroPhysicsId = null;
        if (names.Contains("AeroPhysicsID", StringComparer.OrdinalIgnoreCase) && TableExists("List_AeroPhysics"))
        {
            aeroPhysicsId = CreateDedicatedAeroPhysicsRow(
                table,
                actualLinkColumn,
                target,
                carId,
                row.GetValueOrDefault("AeroPhysicsID"),
                preferCurrentPhysics: sourceId.HasValue);
            if (aeroPhysicsId.HasValue)
            {
                row["AeroPhysicsID"] = aeroPhysicsId.Value;
            }
        }

        InsertDictionaryRow(table, names, row);
        if (names.Contains("Level", StringComparer.OrdinalIgnoreCase))
        {
            TryEnsureMenuMetadataForTableLevels(table, ExistingLevelsForUpgradeTable(table));
        }

        return new AeroOptionAddResult(id, level, sequence, aeroPhysicsId);
    }

    private Dictionary<string, object?> SourceAeroRowForClone(
        string table,
        IReadOnlyList<string> columns,
        string pk,
        string linkColumn,
        long target)
    {
        var sameTarget = SourceAeroRowForClone(table, columns, pk, $"{SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident(linkColumn)}=$target", ("$target", target));
        if (sameTarget.Count > 0)
        {
            return sameTarget;
        }

        return SourceAeroRowForClone(table, columns, pk, "1=1", null);
    }

    private Dictionary<string, object?> SourceAeroRowById(
        string table,
        IReadOnlyList<string> columns,
        string pk,
        long sourceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT * FROM {SqliteHelpers.Ident(table)} " +
            $"WHERE {SqliteHelpers.Ident(pk)}=$id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", sourceId);
        return ReadFirstRow(cmd, columns);
    }

    private Dictionary<string, object?> SourceAeroRowForClone(
        string table,
        IReadOnlyList<string> columns,
        string pk,
        string where,
        (string Name, object Value)? parameter)
    {
        var hasAeroPhysics = columns.Contains("AeroPhysicsID", StringComparer.OrdinalIgnoreCase) && TableExists("List_AeroPhysics");
        var order = new List<string>();
        if (columns.Contains("IsStock", StringComparer.OrdinalIgnoreCase))
        {
            order.Add($"CAST(COALESCE({SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident("IsStock")}, 0) AS INTEGER) ASC");
        }
        if (columns.Contains("RequiresGraphics", StringComparer.OrdinalIgnoreCase))
        {
            order.Add($"CAST(COALESCE({SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident("RequiresGraphics")}, 0) AS INTEGER) DESC");
        }
        if (hasAeroPhysics)
        {
            order.Add(AeroPhysicsAllZeroSql("ap") + " ASC");
            order.Add(AeroPhysicsHasRangeSql("ap") + " DESC");
            order.Add(AeroPhysicsHasSliderCurveSql("ap") + " DESC");
        }
        if (columns.Contains("Level", StringComparer.OrdinalIgnoreCase))
        {
            order.Add($"CAST(COALESCE({SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident("Level")}, 0) AS INTEGER) DESC");
        }
        if (columns.Contains("Sequence", StringComparer.OrdinalIgnoreCase))
        {
            order.Add($"CAST(COALESCE({SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident("Sequence")}, 0) AS INTEGER) DESC");
        }
        order.Add($"{SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident(pk)} DESC");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT {SqliteHelpers.Ident("t")}.* FROM {SqliteHelpers.Ident(table)} {SqliteHelpers.Ident("t")} " +
            (hasAeroPhysics
                ? $"LEFT JOIN {SqliteHelpers.Ident("List_AeroPhysics")} ap ON ap.AeroPhysicsID = {SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident("AeroPhysicsID")} "
                : "") +
            $"WHERE {where} ORDER BY {string.Join(", ", order)} LIMIT 1";
        if (parameter.HasValue)
        {
            cmd.Parameters.AddWithValue(parameter.Value.Name, parameter.Value.Value);
        }
        return ReadFirstRow(cmd, columns);
    }

    private long NextLinkedOptionId(string table, string pk, string linkColumn, long target, long carId)
    {
        var currentMax = MaxInt(table, pk, $"{SqliteHelpers.Ident(linkColumn)}=$target", ("$target", target));
        long candidate;
        if (currentMax > 0)
        {
            candidate = currentMax + 1;
        }
        else
        {
            try
            {
                candidate = linkColumn.Equals("Ordinal", StringComparison.OrdinalIgnoreCase)
                    ? checked(carId * 1000)
                    : target;
            }
            catch (OverflowException)
            {
                candidate = NextId(table, pk);
            }
        }

        while (ColumnValueExists(table, pk, candidate))
        {
            candidate++;
        }
        return candidate;
    }

    private long? CreateDedicatedAeroPhysicsRow(
        string optionTable,
        string linkColumn,
        long target,
        long carId,
        object? currentPhysicsId,
        bool preferCurrentPhysics)
    {
        var source = BestAeroPhysicsForOption(optionTable, linkColumn, target, currentPhysicsId, preferCurrentPhysics);
        if (source is null || source.Count == 0)
        {
            return null;
        }

        var columns = GetColumns("List_AeroPhysics");
        var names = columns.Select(c => c.Name).ToList();
        var row = CloneOrDefaultRow(columns, source);
        var newId = NextAeroPhysicsId(carId);
        row["AeroPhysicsID"] = newId;
        if (names.Contains("Ordinal", StringComparer.OrdinalIgnoreCase))
        {
            row["Ordinal"] = carId;
        }
        EnsureUsableAeroPhysicsDefaults(row);
        InsertDictionaryRow("List_AeroPhysics", names, row);
        return newId;
    }

    private Dictionary<string, object?>? BestAeroPhysicsForOption(
        string optionTable,
        string linkColumn,
        long target,
        object? currentPhysicsId,
        bool preferCurrentPhysics)
    {
        var current = ReadAeroPhysicsById(ToLongOrZero(currentPhysicsId));
        if (preferCurrentPhysics && current is not null && !AeroPhysicsAllZero(current))
        {
            return current;
        }

        var sameTarget = BestAeroPhysicsFromOptionRows(
            optionTable,
            $"{SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident(linkColumn)}=$target",
            ("$target", target));
        if (sameTarget is not null && !AeroPhysicsAllZero(sameTarget))
        {
            return sameTarget;
        }

        if (current is not null && !AeroPhysicsAllZero(current))
        {
            return current;
        }

        var tableWide = BestAeroPhysicsFromOptionRows(optionTable, "1=1", null);
        if (tableWide is not null && !AeroPhysicsAllZero(tableWide))
        {
            return tableWide;
        }

        return current;
    }

    private Dictionary<string, object?>? BestAeroPhysicsFromOptionRows(
        string optionTable,
        string where,
        (string Name, object Value)? parameter)
    {
        var columns = GetColumns("List_AeroPhysics").Select(c => c.Name).ToList();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT ap.* FROM {SqliteHelpers.Ident(optionTable)} {SqliteHelpers.Ident("t")} " +
            $"JOIN {SqliteHelpers.Ident("List_AeroPhysics")} ap ON ap.AeroPhysicsID = {SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident("AeroPhysicsID")} " +
            $"WHERE {where} " +
            "ORDER BY " +
            AeroPhysicsAllZeroSql("ap") + " ASC, " +
            AeroPhysicsHasRangeSql("ap") + " DESC, " +
            AeroPhysicsHasSliderCurveSql("ap") + " DESC, " +
            $"CAST(COALESCE({SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident("IsStock")}, 0) AS INTEGER) ASC, " +
            $"CAST(COALESCE({SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident("Level")}, 0) AS INTEGER) DESC, " +
            $"CAST(COALESCE({SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident("Sequence")}, 0) AS INTEGER) DESC, " +
            $"{SqliteHelpers.Ident("t")}.{SqliteHelpers.Ident("Id")} DESC LIMIT 1";
        if (parameter.HasValue)
        {
            cmd.Parameters.AddWithValue(parameter.Value.Name, parameter.Value.Value);
        }
        var row = ReadFirstRow(cmd, columns);
        return row.Count == 0 ? null : row;
    }

    private Dictionary<string, object?>? ReadAeroPhysicsById(long aeroPhysicsId)
    {
        if (aeroPhysicsId <= 0 || !TableExists("List_AeroPhysics"))
        {
            return null;
        }

        var columns = GetColumns("List_AeroPhysics").Select(c => c.Name).ToList();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM List_AeroPhysics WHERE AeroPhysicsID=$id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", aeroPhysicsId);
        var row = ReadFirstRow(cmd, columns);
        return row.Count == 0 ? null : row;
    }

    private long NextAeroPhysicsId(long carId)
    {
        var existingForCar = MaxInt("List_AeroPhysics", "AeroPhysicsID", "Ordinal=$carId", ("$carId", carId));
        long candidate;
        if (existingForCar > 0)
        {
            candidate = existingForCar + 1;
        }
        else
        {
            try
            {
                candidate = checked(carId * 1000 + 901);
            }
            catch (OverflowException)
            {
                candidate = NextId("List_AeroPhysics", "AeroPhysicsID");
            }
        }

        while (ColumnValueExists("List_AeroPhysics", "AeroPhysicsID", candidate))
        {
            candidate++;
        }
        return candidate;
    }

    private static string AeroPhysicsAllZeroSql(string alias)
    {
        string c(string column) => $"{alias}.{SqliteHelpers.Ident(column)}";
        return "(CASE WHEN " +
               string.Join(" AND ", AeroPhysicsEffectColumns().Select(column => $"COALESCE({c(column)}, 0)=0")) +
               " THEN 1 ELSE 0 END)";
    }

    private static string AeroPhysicsHasRangeSql(string alias)
    {
        return "(CASE WHEN " +
               $"COALESCE({alias}.{SqliteHelpers.Ident("Drag0")}, 0)<>COALESCE({alias}.{SqliteHelpers.Ident("Drag1")}, 0) OR " +
               $"COALESCE({alias}.{SqliteHelpers.Ident("Downforce0")}, 0)<>COALESCE({alias}.{SqliteHelpers.Ident("Downforce1")}, 0) " +
               "THEN 1 ELSE 0 END)";
    }

    private static string AeroPhysicsHasSliderCurveSql(string alias)
    {
        return "(CASE WHEN " +
               $"COALESCE({alias}.{SqliteHelpers.Ident("DefaultTuneSlider")}, 0)<>0 AND " +
               $"COALESCE({alias}.{SqliteHelpers.Ident("DFTorqueScaleOneSliderInput1")}, 0)=0.5 AND " +
               $"COALESCE({alias}.{SqliteHelpers.Ident("DFTorqueScaleOneSliderInput2")}, 0)=1 AND " +
               $"COALESCE({alias}.{SqliteHelpers.Ident("DFTorqueScaleOneTorqueScaleOutput2")}, 0)=1 AND " +
               $"COALESCE({alias}.{SqliteHelpers.Ident("DFTorqueScaleTwoSliderInput1")}, 0)=0.5 AND " +
               $"COALESCE({alias}.{SqliteHelpers.Ident("DFTorqueScaleTwoSliderInput2")}, 0)=1 AND " +
               $"COALESCE({alias}.{SqliteHelpers.Ident("DFTorqueScaleTwoTorqueScaleOutput0")}, 0)=1 AND " +
               $"COALESCE({alias}.{SqliteHelpers.Ident("DFTorqueScaleTwoTorqueScaleOutput1")}, 0)=1 AND " +
               $"COALESCE({alias}.{SqliteHelpers.Ident("DFTorqueScaleTwoTorqueScaleOutput2")}, 0)=1 " +
               "THEN 1 ELSE 0 END)";
    }

    private static IReadOnlyList<string> AeroPhysicsEffectColumns() =>
    [
        "DefaultTuneSlider",
        "Drag0",
        "Downforce0",
        "Drag1",
        "Downforce1",
        "AngleZeroDownforce",
        "LateralDrag",
        "DFTorqueScaleOneSliderInput0",
        "DFTorqueScaleOneSliderInput1",
        "DFTorqueScaleOneSliderInput2",
        "DFTorqueScaleOneTorqueScaleOutput0",
        "DFTorqueScaleOneTorqueScaleOutput1",
        "DFTorqueScaleOneTorqueScaleOutput2",
        "DFTorqueScaleTwoSliderInput0",
        "DFTorqueScaleTwoSliderInput1",
        "DFTorqueScaleTwoSliderInput2",
        "DFTorqueScaleTwoTorqueScaleOutput0",
        "DFTorqueScaleTwoTorqueScaleOutput1",
        "DFTorqueScaleTwoTorqueScaleOutput2",
        "DownforceOffThrottleOffset",
        "DragOffThrottleOffset",
        "DownforceBrakingOffset",
        "DragBrakingOffset"
    ];

    private static bool AeroPhysicsAllZero(IReadOnlyDictionary<string, object?> row)
    {
        return AeroPhysicsEffectColumns().All(column => Math.Abs(ToDoubleOrZero(row.GetValueOrDefault(column))) < 0.000001);
    }

    private static void EnsureUsableAeroPhysicsDefaults(IDictionary<string, object?> row)
    {
        row.TryGetValue("DefaultTuneSlider", out var defaultTuneSlider);
        if (ToDoubleOrZero(defaultTuneSlider) <= 0)
        {
            row["DefaultTuneSlider"] = 0.5;
        }

        row["DFTorqueScaleOneSliderInput0"] = 0.0;
        row["DFTorqueScaleOneSliderInput1"] = 0.5;
        row["DFTorqueScaleOneSliderInput2"] = 1.0;
        row["DFTorqueScaleOneTorqueScaleOutput0"] = 0.0;
        row["DFTorqueScaleOneTorqueScaleOutput1"] = 0.0;
        row["DFTorqueScaleOneTorqueScaleOutput2"] = 1.0;
        row["DFTorqueScaleTwoSliderInput0"] = 0.0;
        row["DFTorqueScaleTwoSliderInput1"] = 0.5;
        row["DFTorqueScaleTwoSliderInput2"] = 1.0;
        row["DFTorqueScaleTwoTorqueScaleOutput0"] = 1.0;
        row["DFTorqueScaleTwoTorqueScaleOutput1"] = 1.0;
        row["DFTorqueScaleTwoTorqueScaleOutput2"] = 1.0;
    }

    private void AddLinkedOptionRow(string table, string linkColumn, long target)
    {
        if (!TableExists(table))
        {
            throw new InvalidOperationException($"Table does not exist: {table}");
        }

        var columns = GetColumns(table);
        var names = columns.Select(c => c.Name).ToList();
        if (!names.Contains("Id", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{table} needs an Id column to add options.");
        }
        var actualLinkColumn = names.FirstOrDefault(c => c.Equals(linkColumn, StringComparison.OrdinalIgnoreCase));
        if (actualLinkColumn is null)
        {
            throw new InvalidOperationException($"{table} does not have link column {linkColumn}.");
        }

        var source = SourceRowForClone(table, names, "Id", actualLinkColumn, target);
        if (source.Count == 0)
        {
            throw new InvalidOperationException($"No source rows exist in {table}.");
        }

        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            row[column.Name] = source.TryGetValue(column.Name, out var value)
                ? value
                : DefaultCloneValue(column.Type);
        }

        row["Id"] = NextId(table, "Id");
        row[actualLinkColumn] = target;
        if (names.Contains("IsStock", StringComparer.OrdinalIgnoreCase))
        {
            row["IsStock"] = 0;
        }
        if (names.Contains("Level", StringComparer.OrdinalIgnoreCase))
        {
            row["Level"] = MaxInt(table, "Level", $"{SqliteHelpers.Ident(actualLinkColumn)}=$target", ("$target", target)) + 1;
        }
        if (names.Contains("Sequence", StringComparer.OrdinalIgnoreCase))
        {
            row["Sequence"] = MaxInt(table, "Sequence", $"{SqliteHelpers.Ident(actualLinkColumn)}=$target", ("$target", target)) + 1;
        }

        InsertDictionaryRow(table, names, row);
        if (row.TryGetValue("Level", out var level))
        {
            TryEnsureMenuMetadataForTableLevels(table, [ToLongOrZero(level)]);
        }
    }

    private void EnsureMenuMetadataForSpec(UpgradePartMetadataSpec spec, IReadOnlyCollection<long> levels, List<string> changes)
    {
        var aspirationSpec = AspirationSpecForTable(spec.TableName);
        string? fallbackPartName = aspirationSpec?.FallbackPartName;
        string? typeName = null;
        string? typeDescription = null;
        if (aspirationSpec is not null && aspirationSpec.PartName.Equals(spec.PartName, StringComparison.OrdinalIgnoreCase))
        {
            var aspiration = AspirationText(aspirationSpec.AspirationId);
            typeName = aspiration.DisplayName;
            typeDescription = aspiration.ShortDisplayName;
        }

        var typeIds = UpgradeTypeIdsForPartName(spec.PartName);
        if (typeIds.Count == 0)
        {
            var fallbackForNewType = fallbackPartName ?? FallbackPartNameForSpec(spec, required: true)!;
            fallbackPartName = fallbackForNewType;
            typeIds = [EnsureUpgradeType(spec.PartName, fallbackForNewType, typeName, typeDescription, changes)];
        }

        fallbackPartName ??= FallbackPartNameForSpec(spec, required: false);
        var fallbackTypeId = string.IsNullOrWhiteSpace(fallbackPartName) ? null : UpgradeTypeIdForPartName(fallbackPartName);
        foreach (var typeId in typeIds)
        {
            EnsureUpgradeAreaLinks(typeId, fallbackTypeId, spec.CategoryName, changes);
            EnsureUpgradeRowsForLevels(spec.PartName, typeId, fallbackTypeId, levels, changes);
        }
        EnsureUpgradeWizardPartLevels(spec.PartName, fallbackPartName, levels, changes);

        if (aspirationSpec is not null && aspirationSpec.PartName.Equals(spec.PartName, StringComparison.OrdinalIgnoreCase))
        {
            EnsureAspirationConversionRow(aspirationSpec, changes);
            NormalizeSyntheticAspirationUpgradeText(aspirationSpec, changes);
        }
    }

    private long EnsureUpgradeType(string partName, string fallbackPartName, string? name, string? description, List<string> changes)
    {
        var existing = UpgradeTypeIdForPartName(partName);
        if (existing.HasValue)
        {
            return existing.Value;
        }

        var fallback = ReadUpgradeTypeByPartName(fallbackPartName);
        if (fallback.Count == 0)
        {
            throw new InvalidOperationException($"Cannot create {partName}; fallback upgrade type {fallbackPartName} is missing.");
        }

        var columns = GetColumns("UpgradeTypes");
        var names = columns.Select(c => c.Name).ToList();
        var row = CloneOrDefaultRow(columns, fallback);
        var newId = NextId("UpgradeTypes", "id");
        row["id"] = newId;
        row["PartName"] = partName;
        row["Name"] = FirstNonEmpty(name, ValueAsString(row.GetValueOrDefault("Name")));
        row["Description"] = FirstNonEmpty(description, ValueAsString(row.GetValueOrDefault("Description")));
        InsertDictionaryRow("UpgradeTypes", names, row);
        changes.Add($"UpgradeTypes.{partName}");
        return newId;
    }

    private void EnsureUpgradeAreaLinks(long typeId, long? fallbackTypeId, string categoryName, List<string> changes)
    {
        if (!TableExists("UpgradeAreaForUpgradeType"))
        {
            return;
        }

        using (var existing = _connection.CreateCommand())
        {
            existing.CommandText = "SELECT 1 FROM UpgradeAreaForUpgradeType WHERE CAST(UpgradeTypeId AS INTEGER)=$typeId LIMIT 1";
            existing.Parameters.AddWithValue("$typeId", typeId);
            if (existing.ExecuteScalar() is not null)
            {
                return;
            }
        }

        var sourceLinks = fallbackTypeId.HasValue
            ? ReadRows("UpgradeAreaForUpgradeType", "CAST(UpgradeTypeId AS INTEGER)=$typeId", ("$typeId", fallbackTypeId.Value), "id")
            : [];
        var areaIds = sourceLinks
            .Where(row => row.TryGetValue("UpgradeAreaId", out var value) && value is not null and not DBNull)
            .Select(row => ToLongOrZero(row["UpgradeAreaId"]))
            .Distinct()
            .ToList();
        if (areaIds.Count == 0 && DefaultUpgradeAreaForCategory(categoryName) is { } defaultArea)
        {
            areaIds.Add(defaultArea);
        }
        if (areaIds.Count == 0)
        {
            return;
        }

        var columns = GetColumns("UpgradeAreaForUpgradeType");
        var names = columns.Select(c => c.Name).ToList();
        foreach (var areaId in areaIds)
        {
            var row = CloneOrDefaultRow(columns, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
            row["id"] = NextId("UpgradeAreaForUpgradeType", "id");
            row["UpgradeAreaId"] = areaId;
            row["UpgradeTypeId"] = typeId;
            InsertDictionaryRow("UpgradeAreaForUpgradeType", names, row);
        }
        changes.Add("upgrade area link");
    }

    private void EnsureUpgradeRowsForLevels(string partName, long typeId, long? fallbackTypeId, IReadOnlyCollection<long> levels, List<string> changes)
    {
        if (UpgradeRowCountForType(typeId) == 0 && fallbackTypeId.HasValue)
        {
            CopyFallbackUpgradeRows(partName, typeId, fallbackTypeId.Value, changes);
        }

        foreach (var level in levels.Distinct().OrderBy(v => v))
        {
            if (UpgradeLevelExists(typeId, level))
            {
                RepairFallbackCopiedUpgradeLevel(partName, typeId, fallbackTypeId, level, changes);
                continue;
            }

            var source = ReadUpgradeRowForLevelClone(typeId, fallbackTypeId, level);
            if (source.Count == 0)
            {
                continue;
            }

            InsertUpgradeLevelRow(partName, typeId, level, source, changes);
        }
    }

    private void CopyFallbackUpgradeRows(string partName, long typeId, long fallbackTypeId, List<string> changes)
    {
        var rows = ReadRows("Upgrades", "CAST(TypeId AS INTEGER)=$typeId", ("$typeId", fallbackTypeId), "Level, SortOrder, id");
        if (rows.Count == 0)
        {
            return;
        }

        var columns = GetColumns("Upgrades");
        var names = columns.Select(c => c.Name).ToList();
        foreach (var source in rows)
        {
            var row = CloneOrDefaultRow(columns, source);
            row["id"] = NextId("Upgrades", "id");
            row["TypeId"] = typeId.ToString(CultureInfo.InvariantCulture);
            InsertDictionaryRow("Upgrades", names, row);
        }
        changes.Add($"{partName} base level rows");
    }

    private void InsertUpgradeLevelRow(string partName, long typeId, long level, IReadOnlyDictionary<string, object?> source, List<string> changes)
    {
        var columns = GetColumns("Upgrades");
        var names = columns.Select(c => c.Name).ToList();
        var row = CloneOrDefaultRow(columns, source);
        row["id"] = NextId("Upgrades", "id");
        row["TypeId"] = typeId.ToString(CultureInfo.InvariantCulture);
        row["Level"] = level;
        row["SortOrder"] = level;
        InsertDictionaryRow("Upgrades", names, row);
        changes.Add($"{partName} level {level}");
    }

    private void RepairFallbackCopiedUpgradeLevel(string partName, long typeId, long? fallbackTypeId, long level, List<string> changes)
    {
        if (!fallbackTypeId.HasValue || level <= 0)
        {
            return;
        }

        var fallback = ReadUpgradeRowForClone(
            "CAST(TypeId AS INTEGER)=$typeId AND CAST(Level AS INTEGER)=$level",
            fallbackTypeId.Value,
            level);
        if (fallback is null)
        {
            return;
        }

        var source =
            ReadUpgradeRowForClone("CAST(TypeId AS INTEGER)=$typeId AND CAST(Level AS INTEGER)<$level", typeId, level) ??
            ReadUpgradeRowForClone("CAST(TypeId AS INTEGER)=$typeId", typeId, null);
        if (source is null || UpgradeDisplayMatches(source, fallback))
        {
            return;
        }

        var currentRows = ReadUpgradeRowsForTypeLevel(typeId, level);

        var repaired = 0;
        foreach (var row in currentRows)
        {
            if (!UpgradeDisplayMatches(row, fallback) || UpgradeDisplayMatches(row, source))
            {
                continue;
            }

            UpdateUpgradeDisplay(row, source, level);
            repaired++;
        }

        if (repaired > 0)
        {
            changes.Add($"{partName} level {level} display repaired");
        }
    }

    private void UpdateUpgradeDisplay(IReadOnlyDictionary<string, object?> target, IReadOnlyDictionary<string, object?> source, long level)
    {
        if (!target.TryGetValue("id", out var id))
        {
            return;
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "UPDATE Upgrades SET Name=$name, Description=$description, IconPath=$icon, ImagePath=$image, SortOrder=$sortOrder " +
            "WHERE id=$id";
        cmd.Parameters.AddWithValue("$name", DbValue(source, "Name"));
        cmd.Parameters.AddWithValue("$description", DbValue(source, "Description"));
        cmd.Parameters.AddWithValue("$icon", DbValue(source, "IconPath"));
        cmd.Parameters.AddWithValue("$image", DbValue(source, "ImagePath"));
        cmd.Parameters.AddWithValue("$sortOrder", level);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private List<Dictionary<string, object?>> ReadUpgradeRowsForTypeLevel(long typeId, long level)
    {
        var columns = GetColumns("Upgrades").Select(c => c.Name).ToList();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT * FROM Upgrades WHERE CAST(TypeId AS INTEGER)=$typeId AND CAST(Level AS INTEGER)=$level ORDER BY id";
        cmd.Parameters.AddWithValue("$typeId", typeId);
        cmd.Parameters.AddWithValue("$level", level);
        using var reader = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                row[column] = SqliteHelpers.ReaderValue(reader, reader.GetOrdinal(column));
            }
            rows.Add(row);
        }
        return rows;
    }

    private static bool UpgradeDisplayMatches(IReadOnlyDictionary<string, object?> row, IReadOnlyDictionary<string, object?> source)
    {
        var compared = 0;
        var matched = 0;
        foreach (var column in new[] { "Name", "Description", "IconPath", "ImagePath" })
        {
            var left = ValueText(row, column);
            var right = ValueText(source, column);
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                continue;
            }

            compared++;
            if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
            {
                matched++;
            }
        }

        return compared > 0 && (matched >= Math.Min(2, compared) ||
                                ValueText(row, "IconPath").Equals(ValueText(source, "IconPath"), StringComparison.OrdinalIgnoreCase) ||
                                ValueText(row, "ImagePath").Equals(ValueText(source, "ImagePath"), StringComparison.OrdinalIgnoreCase));
    }

    private static object DbValue(IReadOnlyDictionary<string, object?> row, string column)
    {
        return row.TryGetValue(column, out var value) && value is not null ? value : DBNull.Value;
    }

    private static string ValueText(IReadOnlyDictionary<string, object?> row, string column)
    {
        return row.TryGetValue(column, out var value) ? ValueAsString(value) ?? "" : "";
    }

    private void EnsureUpgradeWizardPartLevels(string partName, string? fallbackPartName, IReadOnlyCollection<long> levels, List<string> changes)
    {
        if (!TableExists("UpgradeWizardParts"))
        {
            return;
        }

        var columns = GetColumns("UpgradeWizardParts");
        var names = columns.Select(c => c.Name).ToList();
        if (!names.Contains("PartName", StringComparer.OrdinalIgnoreCase) ||
            !names.Contains("PartLevel", StringComparer.OrdinalIgnoreCase) ||
            !names.Contains("SortBalanced", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var added = new List<long>();
        foreach (var level in levels.Where(level => level > 0).Distinct().OrderBy(v => v))
        {
            if (UpgradeWizardPartLevelExists(partName, level))
            {
                continue;
            }

            var row = CloneOrDefaultRow(columns, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
            row["PartName"] = partName;
            row["PartLevel"] = level;
            row["SortBalanced"] = SortBalancedForWizardPartLevel(partName, fallbackPartName, level);
            InsertDictionaryRow("UpgradeWizardParts", names, row);
            added.Add(level);
        }

        if (added.Count > 0)
        {
            changes.Add($"UpgradeWizardParts.{partName} levels {FormatLevels(added)}");
        }
    }

    private bool UpgradeWizardPartLevelExists(string partName, long level)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM UpgradeWizardParts WHERE PartName=$partName AND CAST(PartLevel AS INTEGER)=$level LIMIT 1";
        cmd.Parameters.AddWithValue("$partName", partName);
        cmd.Parameters.AddWithValue("$level", level);
        return cmd.ExecuteScalar() is not null;
    }

    private long SortBalancedForWizardPartLevel(string partName, string? fallbackPartName, long level)
    {
        var samePart = WizardSortForPartLevel(partName, level);
        if (samePart.HasValue)
        {
            return samePart.Value;
        }

        if (!string.IsNullOrWhiteSpace(fallbackPartName))
        {
            var fallbackExact = WizardSortForPartLevel(fallbackPartName, level);
            if (fallbackExact.HasValue)
            {
                return fallbackExact.Value;
            }
        }

        var sameNearest = NearestWizardSort(partName, level);
        if (sameNearest.HasValue)
        {
            return sameNearest.Value.Sort + Math.Max(1, level - sameNearest.Value.Level);
        }

        if (!string.IsNullOrWhiteSpace(fallbackPartName))
        {
            var fallbackNearest = NearestWizardSort(fallbackPartName, level);
            if (fallbackNearest.HasValue)
            {
                return fallbackNearest.Value.Sort + Math.Max(1, level - fallbackNearest.Value.Level);
            }
        }

        return MaxInt("UpgradeWizardParts", "SortBalanced") + 1;
    }

    private long? WizardSortForPartLevel(string partName, long level)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT CAST(SortBalanced AS INTEGER) FROM UpgradeWizardParts " +
            "WHERE PartName=$partName AND CAST(PartLevel AS INTEGER)=$level " +
            "ORDER BY CAST(SortBalanced AS INTEGER) LIMIT 1";
        cmd.Parameters.AddWithValue("$partName", partName);
        cmd.Parameters.AddWithValue("$level", level);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private (long Level, long Sort)? NearestWizardSort(string partName, long level)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT CAST(PartLevel AS INTEGER), CAST(SortBalanced AS INTEGER) FROM UpgradeWizardParts " +
            "WHERE PartName=$partName ORDER BY ABS(CAST(PartLevel AS INTEGER)-$level), CAST(PartLevel AS INTEGER) DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$partName", partName);
        cmd.Parameters.AddWithValue("$level", level);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture));
    }

    private void EnsureAspirationConversionRow(AspirationPartSpec spec, List<string> changes)
    {
        using (var existing = _connection.CreateCommand())
        {
            existing.CommandText =
                "SELECT 1 FROM Upgrades WHERE CAST(TypeId AS INTEGER)=57 AND CAST(IsException AS INTEGER)=0 AND CAST(Level AS INTEGER)=$level LIMIT 1";
            existing.Parameters.AddWithValue("$level", spec.AspirationId);
            if (existing.ExecuteScalar() is not null)
            {
                return;
            }
        }

        var sourceRows = ReadRows(
            "Upgrades",
            "CAST(TypeId AS INTEGER)=57 AND CAST(IsException AS INTEGER)=0 AND CAST(Level AS INTEGER)=$level",
            ("$level", spec.FallbackAspirationId),
            "SortOrder, id");
        if (sourceRows.Count == 0)
        {
            throw new InvalidOperationException($"Cannot create {spec.PartName}; fallback aspiration conversion row is missing.");
        }

        var columns = GetColumns("Upgrades");
        var names = columns.Select(c => c.Name).ToList();
        var row = CloneOrDefaultRow(columns, sourceRows[0]);
        var aspiration = AspirationText(spec.AspirationId);
        row["id"] = NextId("Upgrades", "id");
        row["TypeId"] = "57";
        row["IsException"] = 0L;
        row["Level"] = spec.AspirationId;
        row["SortOrder"] = spec.AspirationId;
        row["Name"] = FirstNonEmpty(aspiration.DisplayName, ValueAsString(row.GetValueOrDefault("Name")));
        row["Description"] = FirstNonEmpty(aspiration.ShortDisplayName, ValueAsString(row.GetValueOrDefault("Description")));
        InsertDictionaryRow("Upgrades", names, row);
        changes.Add($"Aspiration level {spec.AspirationId}");
    }

    private void NormalizeSyntheticAspirationUpgradeText(AspirationPartSpec spec, List<string> changes)
    {
        if (!spec.PartName.Equals("QuadTurbo", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var typeIds = UpgradeTypeIdsForPartName(spec.PartName);
        if (typeIds.Count == 0)
        {
            return;
        }

        var updated = 0;
        foreach (var typeId in typeIds)
        {
            updated += UpdateSyntheticTextIfNeeded(
                "UpgradeTypes",
                "id",
                typeId,
                "Quad Turbocharged",
                "Quad turbo conversion");

            using var select = _connection.CreateCommand();
            select.CommandText =
                "SELECT id, CAST(Level AS INTEGER), Name, Description FROM Upgrades " +
                "WHERE CAST(TypeId AS INTEGER)=$typeId ORDER BY CAST(Level AS INTEGER), id";
            select.Parameters.AddWithValue("$typeId", typeId);
            using var reader = select.ExecuteReader();
            var rows = new List<(long Id, long Level, string? Name, string? Description)>();
            while (reader.Read())
            {
                rows.Add((
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }

            foreach (var row in rows)
            {
                var name = row.Level switch
                {
                    0 => "Stock Quad Turbo",
                    1 => "Street Quad Turbo",
                    2 => "Sport Quad Turbo",
                    3 => "Race Quad Turbo",
                    4 => "Ultimate Quad Turbo",
                    _ => $"Quad Turbo Level {row.Level}"
                };
                updated += UpdateSyntheticTextIfNeeded("Upgrades", "id", row.Id, name, "Synthetic quad turbo upgrade");
            }
        }

        if (updated > 0)
        {
            changes.Add("QuadTurbo display labels");
        }
    }

    private int UpdateSyntheticTextIfNeeded(string table, string keyColumn, long key, string name, string description)
    {
        using var select = _connection.CreateCommand();
        select.CommandText = $"SELECT Name, Description FROM {SqliteHelpers.Ident(table)} WHERE {SqliteHelpers.Ident(keyColumn)}=$key LIMIT 1";
        select.Parameters.AddWithValue("$key", key);
        using var reader = select.ExecuteReader();
        if (!reader.Read())
        {
            return 0;
        }

        var currentName = reader.IsDBNull(0) ? null : reader.GetString(0);
        var currentDescription = reader.IsDBNull(1) ? null : reader.GetString(1);
        var updateName = ShouldReplaceSyntheticText(currentName);
        var updateDescription = ShouldReplaceSyntheticText(currentDescription);
        reader.Dispose();

        if (!updateName && !updateDescription)
        {
            return 0;
        }

        using var update = _connection.CreateCommand();
        var assignments = new List<string>();
        if (updateName)
        {
            assignments.Add("Name=$name");
            update.Parameters.AddWithValue("$name", name);
        }
        if (updateDescription)
        {
            assignments.Add("Description=$description");
            update.Parameters.AddWithValue("$description", description);
        }
        update.CommandText =
            $"UPDATE {SqliteHelpers.Ident(table)} SET {string.Join(", ", assignments)} " +
            $"WHERE {SqliteHelpers.Ident(keyColumn)}=$key";
        update.Parameters.AddWithValue("$key", key);
        return update.ExecuteNonQuery();
    }

    private static bool ShouldReplaceSyntheticText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.StartsWith("_&", StringComparison.Ordinal) ||
               value.Contains("Twin Turbo", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<UpgradePartMetadataSpec> UpgradePartSpecsForTable(string table)
    {
        if (!TableExists("Data_UpgradePart"))
        {
            return [];
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, TableName, CategoryName, PartName FROM Data_UpgradePart " +
            "WHERE TableName=$table AND PartName IS NOT NULL AND PartName <> '' ORDER BY Id";
        cmd.Parameters.AddWithValue("$table", table);
        using var reader = cmd.ExecuteReader();
        var specs = new List<UpgradePartMetadataSpec>();
        while (reader.Read())
        {
            specs.Add(new UpgradePartMetadataSpec(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }
        return specs;
    }

    private IReadOnlyCollection<long> ExistingLevelsForUpgradeTable(string table)
    {
        if (!TableExists(table) ||
            !GetColumns(table).Any(c => c.Name.Equals("Level", StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT DISTINCT CAST(Level AS INTEGER) FROM {SqliteHelpers.Ident(table)} " +
            "WHERE Level IS NOT NULL ORDER BY CAST(Level AS INTEGER)";
        using var reader = cmd.ExecuteReader();
        var levels = new List<long>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                levels.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
            }
        }
        return levels;
    }

    private IReadOnlyList<long> UpgradeTypeIdsForPartName(string partName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM UpgradeTypes WHERE PartName=$partName ORDER BY IsException, id";
        cmd.Parameters.AddWithValue("$partName", partName);
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        }
        return ids;
    }

    private string? FallbackPartNameForSpec(UpgradePartMetadataSpec spec, bool required)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, PartName FROM Data_UpgradePart " +
            "WHERE PartName IS NOT NULL AND PartName <> '' AND PartName <> $partName " +
            "AND (($category = '' AND (CategoryName IS NULL OR CategoryName = '')) OR CategoryName = $category) " +
            "ORDER BY ABS(CAST(Id AS INTEGER) - $id), Id";
        cmd.Parameters.AddWithValue("$partName", spec.PartName);
        cmd.Parameters.AddWithValue("$category", spec.CategoryName);
        cmd.Parameters.AddWithValue("$id", spec.Id);
        var candidates = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var candidate = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        foreach (var candidate in candidates)
        {
            if (UpgradeTypeIdsForPartName(candidate).Count > 0)
            {
                return candidate;
            }
        }

        if (required)
        {
            throw new InvalidOperationException($"Cannot create {spec.PartName}; no nearby {spec.CategoryName} fallback upgrade type exists.");
        }
        return null;
    }

    private long? DefaultUpgradeAreaForCategory(string categoryName)
    {
        return categoryName switch
        {
            "Engine" => 1,
            "Drivetrain" => 3,
            "CarBody" => 5,
            "Motor" => 9,
            "Car" => 8,
            _ => null
        };
    }

    private long UpgradeRowCountForType(long typeId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Upgrades WHERE CAST(TypeId AS INTEGER)=$typeId";
        cmd.Parameters.AddWithValue("$typeId", typeId);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private bool UpgradeLevelExists(long typeId, long level)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM Upgrades WHERE CAST(TypeId AS INTEGER)=$typeId AND CAST(Level AS INTEGER)=$level LIMIT 1";
        cmd.Parameters.AddWithValue("$typeId", typeId);
        cmd.Parameters.AddWithValue("$level", level);
        return cmd.ExecuteScalar() is not null;
    }

    private Dictionary<string, object?> ReadUpgradeRowForLevelClone(long typeId, long? fallbackTypeId, long level)
    {
        return ReadUpgradeRowForClone("CAST(TypeId AS INTEGER)=$typeId AND CAST(Level AS INTEGER)=$level", typeId, level)
               ?? ReadUpgradeRowForClone("CAST(TypeId AS INTEGER)=$typeId AND CAST(Level AS INTEGER)<=$level", typeId, level)
               ?? ReadUpgradeRowForClone("CAST(TypeId AS INTEGER)=$typeId", typeId, null)
               ?? (fallbackTypeId.HasValue ? ReadUpgradeRowForClone("CAST(TypeId AS INTEGER)=$typeId AND CAST(Level AS INTEGER)=$level", fallbackTypeId.Value, level) : null)
               ?? (fallbackTypeId.HasValue ? ReadUpgradeRowForClone("CAST(TypeId AS INTEGER)=$typeId AND CAST(Level AS INTEGER)<=$level", fallbackTypeId.Value, level) : null)
               ?? (fallbackTypeId.HasValue ? ReadUpgradeRowForClone("CAST(TypeId AS INTEGER)=$typeId", fallbackTypeId.Value, null) : null)
               ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, object?>? ReadUpgradeRowForClone(string where, long typeId, long? level)
    {
        var columns = GetColumns("Upgrades").Select(c => c.Name).ToList();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT * FROM Upgrades WHERE " + where + " " +
            "ORDER BY CAST(Level AS INTEGER) DESC, SortOrder DESC, id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$typeId", typeId);
        if (level.HasValue)
        {
            cmd.Parameters.AddWithValue("$level", level.Value);
        }
        var row = ReadFirstRow(cmd, columns);
        return row.Count == 0 ? null : row;
    }

    private long? UpgradeTypeIdForPartName(string partName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM UpgradeTypes WHERE PartName=$partName ORDER BY IsException, id LIMIT 1";
        cmd.Parameters.AddWithValue("$partName", partName);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private Dictionary<string, object?> ReadUpgradeTypeByPartName(string partName)
    {
        var columns = GetColumns("UpgradeTypes").Select(c => c.Name).ToList();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM UpgradeTypes WHERE PartName=$partName ORDER BY IsException, id LIMIT 1";
        cmd.Parameters.AddWithValue("$partName", partName);
        return ReadFirstRow(cmd, columns);
    }

    private (string? DisplayName, string? ShortDisplayName) AspirationText(long aspirationId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DisplayName, ShortDisplayName FROM List_Aspiration WHERE AspirationID=$id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", aspirationId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return (null, null);
        }

        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private List<Dictionary<string, object?>> ReadRows(
        string table,
        string where,
        (string Name, object Value) parameter,
        string orderBy)
    {
        var columns = GetColumns(table).Select(c => c.Name).ToList();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT * FROM {SqliteHelpers.Ident(table)} WHERE {where} ORDER BY {orderBy}";
        cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);
        using var reader = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                row[column] = SqliteHelpers.ReaderValue(reader, reader.GetOrdinal(column));
            }
            rows.Add(row);
        }
        return rows;
    }

    private static Dictionary<string, object?> CloneOrDefaultRow(
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyDictionary<string, object?> source)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            row[column.Name] = source.TryGetValue(column.Name, out var value)
                ? value
                : DefaultCloneValue(column.Type);
        }
        return row;
    }

    private static string? ValueAsString(object? value)
    {
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static AspirationPartSpec? AspirationSpecForTable(string table)
    {
        if (table.Equals("List_UpgradeEngineTurboSingle", StringComparison.OrdinalIgnoreCase))
        {
            return new AspirationPartSpec(table, "SingleTurbo", 2, "SingleTurbo", 2);
        }
        if (table.Equals("List_UpgradeEngineTurboTwin", StringComparison.OrdinalIgnoreCase))
        {
            return new AspirationPartSpec(table, "TwinTurbo", 3, "TwinTurbo", 3);
        }
        if (table.Equals("List_UpgradeEngineTurboQuad", StringComparison.OrdinalIgnoreCase))
        {
            return new AspirationPartSpec(table, "QuadTurbo", 4, "TwinTurbo", 3);
        }
        if (table.Equals("List_UpgradeEngineDSC", StringComparison.OrdinalIgnoreCase))
        {
            return new AspirationPartSpec(table, "SuperchargerDSC", 5, "SuperchargerDSC", 5);
        }
        if (table.Equals("List_UpgradeEngineCSC", StringComparison.OrdinalIgnoreCase))
        {
            return new AspirationPartSpec(table, "SuperchargerCSC", 6, "SuperchargerCSC", 6);
        }
        return null;
    }

    private void CleanupAspirationMetadataAfterRemove(AspirationConversionChoice choice, List<string> changes)
    {
        if (!choice.PartName.Equals("QuadTurbo", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (TableTotalRowCount(choice.TableName) > 0 || !TableExists("Upgrades"))
        {
            return;
        }

        using var deleteAspiration = _connection.CreateCommand();
        deleteAspiration.CommandText =
            "DELETE FROM Upgrades WHERE CAST(TypeId AS INTEGER)=57 AND CAST(IsException AS INTEGER)=0 AND CAST(Level AS INTEGER)=$level";
        deleteAspiration.Parameters.AddWithValue("$level", choice.AspirationId);
        var removed = deleteAspiration.ExecuteNonQuery();
        if (removed > 0)
        {
            changes.Add("removed global Quad Turbo aspiration menu row because no Quad rows remain");
        }
    }

    private long TableTotalRowCount(string table)
    {
        if (!TableExists(table))
        {
            return 0;
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {SqliteHelpers.Ident(table)}";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private Dictionary<string, object?> SourceRowForClone(
        string table,
        IReadOnlyList<string> columns,
        string pk,
        string linkColumn,
        long target)
    {
        if (columns.Contains(linkColumn, StringComparer.OrdinalIgnoreCase))
        {
            var order = new List<string>();
            if (columns.Contains("IsStock", StringComparer.OrdinalIgnoreCase))
            {
                order.Add("IsStock ASC");
            }
            if (columns.Contains("Level", StringComparer.OrdinalIgnoreCase))
            {
                order.Add("Level DESC");
            }
            if (columns.Contains("Sequence", StringComparer.OrdinalIgnoreCase))
            {
                order.Add("Sequence DESC");
            }
            order.Add($"{SqliteHelpers.Ident(pk)} DESC");

            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"SELECT * FROM {SqliteHelpers.Ident(table)} " +
                $"WHERE {SqliteHelpers.Ident(linkColumn)}=$target " +
                $"ORDER BY {string.Join(", ", order)} LIMIT 1";
            cmd.Parameters.AddWithValue("$target", target);
            var sameTarget = ReadFirstRow(cmd, columns);
            if (sameTarget.Count > 0)
            {
                return sameTarget;
            }
        }

        using var fallback = _connection.CreateCommand();
        fallback.CommandText =
            $"SELECT * FROM {SqliteHelpers.Ident(table)} " +
            $"ORDER BY {SqliteHelpers.Ident(pk)} LIMIT 1";
        return ReadFirstRow(fallback, columns);
    }

    private long? LinkedUpgradeTarget(long carId, string kind)
    {
        return kind switch
        {
            "car" => carId,
            "body" => StockLinkedId("List_UpgradeCarBody", "CarBodyID", carId),
            "drivetrain" => StockLinkedId("List_UpgradeDrivetrain", "DrivetrainID", carId),
            "motor" => StockLinkedId("List_UpgradeMotor", "MotorID", carId),
            _ => throw new InvalidOperationException($"Unknown upgrade link kind: {kind}")
        };
    }

    private (string LinkColumn, long Target) AeroTarget(string table, long carId)
    {
        if (table.Equals("List_UpgradeRearWing", StringComparison.OrdinalIgnoreCase))
        {
            return ("Ordinal", carId);
        }

        var bodyId = StockLinkedId("List_UpgradeCarBody", "CarBodyID", carId);
        if (!bodyId.HasValue)
        {
            throw new InvalidOperationException("Selected car has no car body row to link this aero part to.");
        }
        return ("CarBodyID", bodyId.Value);
    }

    private void InsertDictionaryRow(string table, IReadOnlyList<string> columns, IReadOnlyDictionary<string, object?> row)
    {
        using var tx = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            $"INSERT INTO {SqliteHelpers.Ident(table)} (" +
            string.Join(", ", columns.Select(SqliteHelpers.Ident)) +
            ") VALUES (" +
            string.Join(", ", columns.Select((_, i) => "$p" + i)) +
            ")";
        for (var i = 0; i < columns.Count; i++)
        {
            insert.Parameters.AddWithValue("$p" + i, NormalizeDbValue(row.TryGetValue(columns[i], out var value) ? value : DBNull.Value));
        }
        insert.ExecuteNonQuery();
        tx.Commit();
    }

    public bool TableExists(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND tbl_name=$table LIMIT 1";
        cmd.Parameters.AddWithValue("$table", table);
        return cmd.ExecuteScalar() is not null;
    }

    public List<ColumnInfo> GetColumns(string table)
    {
        if (_schema.TryGetValue(table, out var cached))
        {
            return cached;
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({SqliteHelpers.Ident(table)})";
        using var reader = cmd.ExecuteReader();
        var columns = new List<ColumnInfo>();
        while (reader.Read())
        {
            columns.Add(new ColumnInfo(reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2), reader.GetInt32(5)));
        }
        _schema[table] = columns;
        return columns;
    }

    private void InsertRow(string table, IReadOnlyList<string> columns, DataRow row, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"INSERT INTO {SqliteHelpers.Ident(table)} (" +
            string.Join(", ", columns.Select(SqliteHelpers.Ident)) +
            ") VALUES (" +
            string.Join(", ", columns.Select((_, i) => "$p" + i)) +
            ")";

        for (var i = 0; i < columns.Count; i++)
        {
            cmd.Parameters.AddWithValue("$p" + i, NormalizeDbValue(row[columns[i]]));
        }
        cmd.ExecuteNonQuery();
    }

    private void UpdateRow(string table, IReadOnlyList<string> columns, IReadOnlyList<string> pk, bool useRowId, DataRow row, SqliteTransaction tx)
    {
        var setColumns = pk.Count > 0 ? columns.Where(c => !pk.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList() : columns.ToList();
        if (setColumns.Count == 0)
        {
            return;
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"UPDATE {SqliteHelpers.Ident(table)} SET " +
            string.Join(", ", setColumns.Select((c, i) => $"{SqliteHelpers.Ident(c)}=$v{i}")) +
            " WHERE " + WhereClauseForRow(pk, useRowId, row, cmd);

        for (var i = 0; i < setColumns.Count; i++)
        {
            cmd.Parameters.AddWithValue("$v" + i, NormalizeDbValue(row[setColumns[i], DataRowVersion.Current]));
        }
        cmd.ExecuteNonQuery();
    }

    private void DeleteRow(string table, IReadOnlyList<string> pk, bool useRowId, DataRow row, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {SqliteHelpers.Ident(table)} WHERE " + WhereClauseForRow(pk, useRowId, row, cmd);
        cmd.ExecuteNonQuery();
    }

    private static string WhereClauseForRow(IReadOnlyList<string> pk, bool useRowId, DataRow row, SqliteCommand cmd)
    {
        if (pk.Count > 0)
        {
            var parts = new List<string>();
            for (var i = 0; i < pk.Count; i++)
            {
                parts.Add($"{SqliteHelpers.Ident(pk[i])}=$pk{i}");
                cmd.Parameters.AddWithValue("$pk" + i, NormalizeDbValue(row[pk[i], DataRowVersion.Original]));
            }
            return string.Join(" AND ", parts);
        }

        if (useRowId)
        {
            cmd.Parameters.AddWithValue("$rowid", NormalizeDbValue(row["__fh6_rowid", DataRowVersion.Original]));
            return "rowid=$rowid";
        }

        throw new InvalidOperationException("This table has no primary key or rowid column available for updates.");
    }

    private (string Sql, List<(string Name, object Value)> Parameters) DefaultWhereForTable(
        string table,
        long? selectedCarId,
        IReadOnlyList<ColumnInfo> columns)
    {
        if (!selectedCarId.HasValue)
        {
            return ("", []);
        }

        var names = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var p = new List<(string, object)> { ("$carId", selectedCarId.Value) };

        if (table.Equals("Data_Car", StringComparison.OrdinalIgnoreCase) && names.Contains("Id"))
        {
            return ($"{SqliteHelpers.Ident("Id")}=$carId", p);
        }
        if (table.Equals("NewProfile_Career_Garage", StringComparison.OrdinalIgnoreCase) && names.Contains("CarId"))
        {
            return ($"{SqliteHelpers.Ident("CarId")}=$carId", p);
        }
        if (table.Equals("Data_Engine", StringComparison.OrdinalIgnoreCase) && names.Contains("EngineID"))
        {
            var engineId = StockLinkedId("List_UpgradeEngine", "EngineID", selectedCarId.Value);
            return engineId.HasValue ? ($"{SqliteHelpers.Ident("EngineID")}=$linkedId", [("$linkedId", engineId.Value)]) : ("", []);
        }
        if (table.Equals("Data_Motor", StringComparison.OrdinalIgnoreCase) && names.Contains("MotorID"))
        {
            var motorId = StockLinkedId("List_UpgradeMotor", "MotorID", selectedCarId.Value);
            return motorId.HasValue ? ($"{SqliteHelpers.Ident("MotorID")}=$linkedId", [("$linkedId", motorId.Value)]) : ("", []);
        }
        if (names.Contains("Ordinal"))
        {
            return ($"{SqliteHelpers.Ident("Ordinal")}=$carId", p);
        }
        if (names.Contains("CarId"))
        {
            return ($"{SqliteHelpers.Ident("CarId")}=$carId", p);
        }
        if (names.Contains("CarID"))
        {
            return ($"{SqliteHelpers.Ident("CarID")}=$carId", p);
        }
        if (names.Contains("EngineID") && EditorConstants.EnginePartTables.Contains(table, StringComparer.OrdinalIgnoreCase))
        {
            return (
                $"{SqliteHelpers.Ident("EngineID")} IN (SELECT EngineID FROM {SqliteHelpers.Ident("List_UpgradeEngine")} WHERE Ordinal=$carId)",
                p);
        }
        if (names.Contains("CarBodyID"))
        {
            var bodyId = StockLinkedId("List_UpgradeCarBody", "CarBodyID", selectedCarId.Value);
            return bodyId.HasValue ? ($"{SqliteHelpers.Ident("CarBodyID")}=$linkedId", [("$linkedId", bodyId.Value)]) : ("", []);
        }
        if (names.Contains("CarBodyId"))
        {
            var bodyId = StockLinkedId("List_UpgradeCarBody", "CarBodyID", selectedCarId.Value);
            return bodyId.HasValue ? ($"{SqliteHelpers.Ident("CarBodyId")}=$linkedId", [("$linkedId", bodyId.Value)]) : ("", []);
        }
        if (names.Contains("DrivetrainID"))
        {
            var drivetrainId = StockLinkedId("List_UpgradeDrivetrain", "DrivetrainID", selectedCarId.Value);
            return drivetrainId.HasValue ? ($"{SqliteHelpers.Ident("DrivetrainID")}=$linkedId", [("$linkedId", drivetrainId.Value)]) : ("", []);
        }
        if (names.Contains("MotorID"))
        {
            var motorId = StockLinkedId("List_UpgradeMotor", "MotorID", selectedCarId.Value);
            return motorId.HasValue ? ($"{SqliteHelpers.Ident("MotorID")}=$linkedId", [("$linkedId", motorId.Value)]) : ("", []);
        }

        return ("", []);
    }

    private long? StockLinkedId(string table, string column, long carId)
    {
        if (!TableExists(table))
        {
            return null;
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT {SqliteHelpers.Ident(column)} FROM {SqliteHelpers.Ident(table)} " +
            "WHERE Ordinal=$carId ORDER BY IsStock DESC, Level, Id LIMIT 1";
        cmd.Parameters.AddWithValue("$carId", carId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private long NextId(string table, string column)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MAX({SqliteHelpers.Ident(column)}), 0) + 1 FROM {SqliteHelpers.Ident(table)}";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private bool ColumnValueExists(string table, string column, object value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM {SqliteHelpers.Ident(table)} WHERE {SqliteHelpers.Ident(column)}=$value LIMIT 1";
        cmd.Parameters.AddWithValue("$value", value);
        return cmd.ExecuteScalar() is not null;
    }

    private long MaxInt(string table, string column, string? where = null, params (string Name, object Value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT COALESCE(MAX({SqliteHelpers.Ident(column)}), 0) FROM {SqliteHelpers.Ident(table)}" +
            (string.IsNullOrWhiteSpace(where) ? "" : " WHERE " + where);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, object?> ReadFirstRow(SqliteCommand cmd, IReadOnlyList<string> columns)
    {
        using var reader = cmd.ExecuteReader();
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!reader.Read())
        {
            return row;
        }

        foreach (var column in columns)
        {
            var ordinal = reader.GetOrdinal(column);
            row[column] = SqliteHelpers.ReaderValue(reader, ordinal);
        }
        return row;
    }

    private static DataTable ReadDataTable(string tableName, SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var dt = new DataTable(tableName) { Locale = CultureInfo.InvariantCulture };
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var column = dt.Columns.Add(reader.GetName(i), typeof(object));
            if (column.ColumnName == "__fh6_rowid")
            {
                column.ColumnMapping = MappingType.Hidden;
            }
        }

        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = SqliteHelpers.ReaderValue(reader, i) ?? DBNull.Value;
            }
            dt.Rows.Add(values);
        }

        dt.AcceptChanges();
        return dt;
    }

    private static DataTable MarkReadOnlyColumns(DataTable table, params string[] columns)
    {
        foreach (var columnName in columns)
        {
            if (table.Columns.Contains(columnName))
            {
                table.Columns[columnName]!.ReadOnly = true;
            }
        }
        return table;
    }

    private static string TireCompoundLabelSql()
    {
        return
            "SELECT TireCompoundID, " +
            "group_concat(DISTINCT NULLIF(TireModelName, '')) AS KnownUpgradeLabels, " +
            "COUNT(DISTINCT Ordinal) AS CarsUsingCompound " +
            "FROM List_UpgradeTireCompound " +
            "GROUP BY TireCompoundID";
    }

    private void ApplyWalCheckpointIfPossible()
    {
        try { ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE)"); } catch { }
        try { ExecuteNonQuery("PRAGMA optimize"); } catch { }
    }

    private void ExecuteNonQuery(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object NormalizeDbValue(object? value)
    {
        if (value is null || value == DBNull.Value)
        {
            return DBNull.Value;
        }
        return value;
    }

    private static object DefaultCloneValue(string declaredType)
    {
        var type = declaredType.ToUpperInvariant();
        if (type.Contains("INT"))
        {
            return 0L;
        }
        if (type.Contains("REAL") ||
            type.Contains("FLOA") ||
            type.Contains("DOUB") ||
            type.Contains("NUM") ||
            type.Contains("DEC"))
        {
            return 0.0;
        }
        if (type.Contains("CHAR") ||
            type.Contains("CLOB") ||
            type.Contains("TEXT"))
        {
            return "";
        }
        return DBNull.Value;
    }

    private static long ToLongOrZero(object? value)
    {
        if (value is null || value == DBNull.Value)
        {
            return 0;
        }
        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static double ToDoubleOrZero(object? value)
    {
        if (value is null || value == DBNull.Value)
        {
            return 0;
        }
        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static string EnginePartTemplateLabel(string sourceTable, SqliteDataReader reader)
    {
        var pieces = new List<string> { sourceTable.Replace("List_UpgradeEngine", "", StringComparison.OrdinalIgnoreCase) };
        if (TryGetOrdinal(reader, "EngineID", out var engineOrdinal))
        {
            pieces.Add($"Engine {reader.GetValue(engineOrdinal)}");
        }
        if (TryGetOrdinal(reader, "EngineMediaName", out var nameOrdinal) && !reader.IsDBNull(nameOrdinal))
        {
            pieces.Add(reader.GetString(nameOrdinal));
        }
        if (TryGetOrdinal(reader, "Level", out var levelOrdinal))
        {
            pieces.Add($"Level {reader.GetValue(levelOrdinal)}");
        }
        if (TryGetOrdinal(reader, "Price", out var priceOrdinal))
        {
            pieces.Add($"CR {reader.GetValue(priceOrdinal)}");
        }
        foreach (var column in new[] { "MaxScale", "PowerMaxScale", "ZeroRPMScale", "RedlineRPMScale", "RobScale", "TorqueScale" })
        {
            if (TryGetOrdinal(reader, column, out var ordinal) && !reader.IsDBNull(ordinal))
            {
                pieces.Add($"{column} {reader.GetValue(ordinal)}");
                break;
            }
        }
        if (TryGetOrdinal(reader, "Id", out var idOrdinal))
        {
            pieces.Add($"Id {reader.GetValue(idOrdinal)}");
        }
        return string.Join(" | ", pieces.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string AeroPartTemplateLabel(string sourceTable, SqliteDataReader reader, bool hasAeroPhysics)
    {
        var pieces = new List<string> { HumanizeAeroPartTable(sourceTable) };
        if (TryGetOrdinal(reader, "SourceCarYear", out var yearOrdinal) && !reader.IsDBNull(yearOrdinal))
        {
            pieces.Add(Convert.ToString(reader.GetValue(yearOrdinal), CultureInfo.InvariantCulture) ?? "");
        }
        if (TryGetOrdinal(reader, "SourceMake", out var makeOrdinal) && !reader.IsDBNull(makeOrdinal))
        {
            pieces.Add(reader.GetString(makeOrdinal));
        }
        if (TryGetOrdinal(reader, "SourceCarName", out var carOrdinal) && !reader.IsDBNull(carOrdinal))
        {
            pieces.Add(reader.GetString(carOrdinal));
        }
        if (TryGetOrdinal(reader, "Level", out var levelOrdinal))
        {
            pieces.Add($"Level {reader.GetValue(levelOrdinal)}");
        }
        if (TryGetOrdinal(reader, "IsStock", out var stockOrdinal) && !reader.IsDBNull(stockOrdinal) &&
            Convert.ToInt64(reader.GetValue(stockOrdinal), CultureInfo.InvariantCulture) != 0)
        {
            pieces.Add("stock");
        }
        if (TryGetOrdinal(reader, "RequiresGraphics", out var graphicsOrdinal) && !reader.IsDBNull(graphicsOrdinal) &&
            Convert.ToInt64(reader.GetValue(graphicsOrdinal), CultureInfo.InvariantCulture) == 0)
        {
            pieces.Add("no graphics");
        }
        if (hasAeroPhysics)
        {
            var hasRange = TryReadDouble(reader, "Drag0", out var drag0) &&
                           TryReadDouble(reader, "Drag1", out var drag1) &&
                           TryReadDouble(reader, "Downforce0", out var downforce0) &&
                           TryReadDouble(reader, "Downforce1", out var downforce1) &&
                           (Math.Abs(drag0 - drag1) > 0.000001 || Math.Abs(downforce0 - downforce1) > 0.000001);
            pieces.Add(hasRange ? "aero range" : "fixed aero");
        }
        if (TryGetOrdinal(reader, "Price", out var priceOrdinal) && !reader.IsDBNull(priceOrdinal))
        {
            pieces.Add($"CR {reader.GetValue(priceOrdinal)}");
        }
        if (TryGetOrdinal(reader, "Id", out var idOrdinal))
        {
            pieces.Add($"Id {reader.GetValue(idOrdinal)}");
        }
        return string.Join(" | ", pieces.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static bool TryReadDouble(SqliteDataReader reader, string column, out double value)
    {
        if (TryGetOrdinal(reader, column, out var ordinal) && !reader.IsDBNull(ordinal))
        {
            value = Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
            return true;
        }

        value = 0;
        return false;
    }

    private static string HumanizeAeroPartTable(string table)
    {
        return table switch
        {
            "List_UpgradeRearWing" => "Rear Wing",
            "List_UpgradeCarBodyFrontBumper" => "Front Bumper",
            "List_UpgradeCarBodyRearBumper" => "Rear Bumper",
            "List_UpgradeCarBodyHood" => "Hood",
            "List_UpgradeCarBodySideSkirt" => "Side Skirts",
            _ => table
        };
    }

    private static string HumanizePartName(string partName)
    {
        return partName switch
        {
            "SingleTurbo" => "Single Turbo",
            "TwinTurbo" => "Twin Turbo",
            "QuadTurbo" => "Quad Turbo",
            "SuperchargerDSC" => "Positive-Displacement Supercharger",
            "SuperchargerCSC" => "Centrifugal Supercharger",
            "Manifold" => "Naturally Aspirated",
            _ => RegexLikeHumanize(partName)
        };
    }

    private static string RegexLikeHumanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var pieces = new List<char>();
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsWhiteSpace(value[i - 1]))
            {
                pieces.Add(' ');
            }
            pieces.Add(value[i]);
        }
        return new string(pieces.ToArray());
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    }

    private static string? FirstExisting(IEnumerable<string> columns, params string[] candidates)
    {
        var set = columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.FirstOrDefault(set.Contains);
    }

    private static bool TryGetOrdinal(SqliteDataReader reader, string column, out int ordinal)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                ordinal = i;
                return true;
            }
        }
        ordinal = -1;
        return false;
    }

}

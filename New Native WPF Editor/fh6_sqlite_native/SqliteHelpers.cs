using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FH6SQLiteEditorNative;

internal static class SqliteHelpers
{
    public static string Ident(string name)
    {
        if (name.Contains('\0'))
        {
            throw new ArgumentException("Invalid SQLite identifier.", nameof(name));
        }
        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }

    public static string GameIdent(string name)
    {
        if (name.Contains('\0'))
        {
            throw new ArgumentException("Invalid game SQL identifier.", nameof(name));
        }
        return "[" + name.Replace("]", "]]") + "]";
    }

    public static string QuoteSqlString(string value) => "'" + value.Replace("'", "''") + "'";

    public static string ToGameSqlLiteral(object? value)
    {
        if (value is null || value is DBNull)
        {
            return "NULL";
        }

        return value switch
        {
            byte b => b.ToString(CultureInfo.InvariantCulture),
            short s => s.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString("G9", CultureInfo.InvariantCulture),
            double d => d.ToString("G17", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            byte[] blob => BlobLiteral(blob),
            string text => QuoteSqlString(text),
            bool flag => flag ? "1" : "0",
            _ => QuoteSqlString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };
    }

    public static object? ReaderValue(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return DBNull.Value;
        }

        return reader.GetFieldType(ordinal) switch
        {
            var t when t == typeof(long) => reader.GetInt64(ordinal),
            var t when t == typeof(double) => reader.GetDouble(ordinal),
            var t when t == typeof(byte[]) => (byte[])reader.GetValue(ordinal),
            _ => reader.GetValue(ordinal)
        };
    }

    public static string ReadOnlyConnectionString(string path)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString();
    }

    public static string ReadWriteConnectionString(string path)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString();
    }

    private static string BlobLiteral(byte[] blob)
    {
        var sb = new StringBuilder(blob.Length * 2 + 3);
        sb.Append("X'");
        foreach (var b in blob)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        sb.Append('\'');
        return sb.ToString();
    }
}

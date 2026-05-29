using System.IO;
using System.Diagnostics;

namespace FH6SQLiteEditorNative;

internal static class AppPaths
{
    public static string LocalStateDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FH6 SQLite Editor Native");

    public static string SessionsDir { get; } = Path.Combine(LocalStateDir, "sessions");

    public static string PackageRoot { get; } = FindPackageRoot();

    public static string DefaultDbPath => Path.Combine(PackageRoot, "fh6_db.sqlite");

    public static string BaseDbDir => Path.Combine(PackageRoot, "BASE DB");

    static AppPaths()
    {
        Directory.CreateDirectory(LocalStateDir);
        Directory.CreateDirectory(SessionsDir);
    }

    public static string? FindBaseDb()
    {
        var preferred = new[]
        {
            Path.Combine(BaseDbDir, "fh6_db.sqlite"),
            Path.Combine(BaseDbDir, "FH6_Database.sqlite"),
            Path.Combine(BaseDbDir, "base.sqlite"),
            Path.Combine(BaseDbDir, "base.db")
        };

        foreach (var path in preferred)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        if (!Directory.Exists(BaseDbDir))
        {
            return null;
        }

        return Directory.EnumerateFiles(BaseDbDir)
            .FirstOrDefault(path =>
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                return ext is ".sqlite" or ".db" or ".slt";
            });
    }

    public static void RemoveSqliteSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            try { File.Delete(path + suffix); }
            catch { /* Best-effort cleanup only. */ }
        }
    }

    public static void CleanOldSessions()
    {
        if (!Directory.Exists(SessionsDir))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(SessionsDir, "fh6_session_*.sqlite*"))
        {
            if (!CanRemoveSessionFile(path))
            {
                continue;
            }

            try { File.Delete(path); }
            catch { /* A live editor or scanner may still hold it. */ }
        }
    }

    private static bool CanRemoveSessionFile(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith("fh6_session_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = name["fh6_session_".Length..];
        var firstUnderscore = rest.IndexOf('_');
        if (firstUnderscore <= 0 || !int.TryParse(rest[..firstUnderscore], out var processId))
        {
            return true;
        }

        if (processId == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static string FindPackageRoot()
    {
        var candidates = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        foreach (var candidate in candidates)
        {
            var dir = new DirectoryInfo(candidate);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "fh6_db.sqlite")) ||
                    Directory.Exists(Path.Combine(dir.FullName, "BASE DB")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }

        return AppContext.BaseDirectory;
    }
}

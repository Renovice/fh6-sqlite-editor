using System.IO;
using System.Text.Json;

namespace FH6SQLiteEditorNative;

internal sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(AppPaths.LocalStateDir, "settings.json");

    public bool DarkMode { get; set; }
    public string? LastDbPath { get; set; }
    public string? LastExportDir { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt settings should not block the editor from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.LocalStateDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}

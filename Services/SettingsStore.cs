using System.IO;
using System.Text.Json;

namespace D365EntitySqlGenerator.Services;

public sealed class AppSettings
{
    public string? PackagesLocalDirectory { get; set; }
}

/// <summary>Persists app settings to %AppData%\D365EntitySqlGenerator\settings.json.</summary>
public sealed class SettingsStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "D365EntitySqlGenerator");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt settings fall back to defaults */ }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}

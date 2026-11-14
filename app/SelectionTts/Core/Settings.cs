using System.Text.Json;

namespace SelectionTts.Core;

public sealed class AppSettings
{
    public string Voice { get; set; } = VoiceCatalog.Default;
    public string Rate { get; set; } = "+0%";
    public bool AutoPopup { get; set; } = true;
    public bool PauseInFullscreen { get; set; } = true;
    public List<string> ExcludedProcesses { get; set; } = [];
    public bool UseUia { get; set; } = true;
    public bool ClipboardRestore { get; set; } = true;
    public int PopupDelayMs { get; set; } = 120;
    public int PopupTimeoutMs { get; set; } = 4000;
    public int MaxChars { get; set; } = 5000;
    public string Engine { get; set; } = "edge";
    public string EdgeProxy { get; set; } = "";
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigPath))
            {
                var json = File.ReadAllText(AppPaths.ConfigPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null)
                {
                    Normalize(settings);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取配置失败，使用默认配置", ex);
            try
            {
                File.Copy(AppPaths.ConfigPath, AppPaths.ConfigPath + ".bad", overwrite: true);
            }
            catch
            {
                // ignore
            }
        }

        var defaults = new AppSettings();
        Save(defaults);
        return defaults;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Normalize(settings);
            var json = JsonSerializer.Serialize(settings, Options);
            File.WriteAllText(AppPaths.ConfigPath, json);
        }
        catch (Exception ex)
        {
            Log.Error("保存配置失败", ex);
        }
    }

    private static void Normalize(AppSettings settings)
    {
        if (!VoiceCatalog.IsKnown(settings.Voice))
        {
            settings.Voice = VoiceCatalog.Default;
        }

        if (!VoiceCatalog.Rates.Contains(settings.Rate))
        {
            settings.Rate = "+0%";
        }

        settings.ExcludedProcesses = settings.ExcludedProcesses
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item =>
            {
                var name = item.Trim();
                return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        settings.PopupDelayMs = Math.Clamp(settings.PopupDelayMs, 0, 2000);
        settings.PopupTimeoutMs = Math.Clamp(settings.PopupTimeoutMs, 500, 30000);
        settings.MaxChars = Math.Clamp(settings.MaxChars, 50, 20000);
    }
}

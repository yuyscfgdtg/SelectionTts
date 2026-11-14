using System.Text.Json;

namespace SelectionTts.Core;

public sealed class AppSettings
{
    public string Voice { get; set; } = VoiceCatalog.Default;
    public string Rate { get; set; } = "+0%";
    public int MaxChars { get; set; } = 5000;
    public string Engine { get; set; } = "edge";
    public string EdgeProxy { get; set; } = "";

    /// <summary>监听剪贴板：复制文本后自动朗读。默认关闭。</summary>
    public bool WatchEnabled { get; set; }
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

        settings.MaxChars = Math.Clamp(settings.MaxChars, 50, 20000);
    }
}

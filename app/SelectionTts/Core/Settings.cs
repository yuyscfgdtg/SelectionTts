using System.Text.Json;
using System.Text.Json.Nodes;

namespace SelectionTts.Core;

public sealed class AppSettings
{
    /// <summary>朗读板默认尺寸（DIP），改动时注意保持 1.31:1 的宽高比。</summary>
    public const int BoardDefaultWidth = 420;

    public const int BoardDefaultHeight = 320;

    /// <summary>朗读板最小尺寸：宽度再小，按比例算出来的字号就不好读了。</summary>
    public const int BoardMinWidth = 360;

    public const int BoardMinHeight = 275;

    /// <summary>朗读板最大宽度：再宽一行会塞进太多字，反而难读。</summary>
    public const int BoardMaxWidth = 900;

    public string Voice { get; set; } = VoiceCatalog.Default;
    public string Rate { get; set; } = "+0%";
    public int MaxChars { get; set; } = 5000;
    public string Engine { get; set; } = "edge";

    /// <summary>监听剪贴板：复制文本后自动朗读。默认关闭。</summary>
    public bool WatchEnabled { get; set; }

    /// <summary>
    /// 朗读板位置。哨兵值 <see cref="int.MinValue"/> 表示"未设置"——
    /// 不能用 -1 或 0：主屏左侧/上方的副屏坐标本来就是负数，那是合法位置。
    /// </summary>
    public int BoardX { get; set; } = int.MinValue;

    public int BoardY { get; set; } = int.MinValue;

    /// <summary>朗读板宽度（DIP）。上限不写死在配置里，运行时按工作区再夹一次。</summary>
    public int BoardWidth { get; set; } = BoardDefaultWidth;

    public int BoardHeight { get; set; } = BoardDefaultHeight;

    /// <summary>朗读板是否显示（用户点 × 隐藏后不再自动弹出）。</summary>
    public bool BoardVisible { get; set; } = true;

    /// <summary>Azure 备用引擎的区域，例如 eastasia。</summary>
    public string AzureRegion { get; set; } = "";

    /// <summary>Azure Key（DPAPI 加密后的 Base64，不存明文）。</summary>
    public string AzureKeyProtected { get; set; } = "";

    /// <summary>动作 id → 组合键文本，例如 "toggle-watch": "Ctrl+Alt+R"。</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = [];
}

/// <summary>配置保存结果。失败时带上给人看的原因。</summary>
public readonly record struct SettingsSaveResult(bool Ok, string Error)
{
    public static SettingsSaveResult Success => new(true, "");

    public static SettingsSaveResult Failure(string error) => new(false, error);
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
        if (!File.Exists(AppPaths.ConfigPath))
        {
            // 没有配置文件就用默认值：不写盘，等用户真的改了设置再落盘，
            // 免得"只是启动了一次"就把默认值固化成文件。
            return new AppSettings();
        }

        string json;
        try
        {
            json = File.ReadAllText(AppPaths.ConfigPath);
        }
        catch (Exception ex)
        {
            // 文件被占用/没权限：用默认值跑，但不覆盖原文件，用户下次还能读到
            Log.Error("读取配置失败，本次使用默认配置（原文件保留）", ex);
            return new AppSettings();
        }

        var settings = TryParse(json);
        if (settings is not null)
        {
            return settings;
        }

        // 解析失败：先把现场留档，再尝试"只丢掉坏字段"修复。
        // 绝不能直接写默认值覆盖——那会把语音、热键、Azure Key、板子位置一起抹掉。
        try
        {
            File.Copy(AppPaths.ConfigPath, AppPaths.ConfigPath + ".bad", overwrite: true);
        }
        catch
        {
            // 留档失败不影响修复
        }

        settings = TryRepair(json);
        if (settings is not null)
        {
            Log.Warn("配置里有无法识别的字段，已丢弃这些字段并保留其余设置");
            _ = Save(settings);
            return settings;
        }

        Log.Error("配置无法解析，本次使用默认配置（原文件已备份为 config.json.bad）");
        return new AppSettings();
    }

    private static AppSettings? TryParse(string json, bool normalize = true)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (settings is null)
            {
                return null;
            }

            if (normalize)
            {
                Normalize(settings);
            }

            return settings;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 手改过或旧版本留下的配置里，值类型字段可能是 null（例如 "boardVisible": null），
    /// 整份反序列化会失败。这里把值为 null 的属性摘掉再试一次：
    /// 坏字段回落到默认值，其余设置全部保住。
    /// </summary>
    private static AppSettings? TryRepair(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return null;
            }

            RemoveNulls(root);
            return TryParse(root.ToJsonString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void RemoveNulls(JsonObject node)
    {
        foreach (var name in node.Select(pair => pair.Key).ToList())
        {
            var value = node[name];
            switch (value)
            {
                case null:
                    node.Remove(name);
                    break;
                case JsonObject child:
                    RemoveNulls(child);
                    break;
            }
        }
    }

    /// <summary>深拷贝一份配置，用于「设置窗口先改草稿、保存成功才提交」。</summary>
    public static AppSettings Clone(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, Options);
        return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
    }

    /// <summary>
    /// 保存配置：先写同目录临时文件，再原子替换目标文件。
    /// 返回结果而不是静默吞异常——调用方必须知道失败，否则会出现「界面说保存了，磁盘上还是旧配置」。
    /// </summary>
    public static SettingsSaveResult Save(AppSettings settings)
    {
        var temp = AppPaths.ConfigPath + ".tmp";

        try
        {
            Normalize(settings);
            var json = JsonSerializer.Serialize(settings, Options);
            var directory = Path.GetDirectoryName(AppPaths.ConfigPath)!;
            Directory.CreateDirectory(directory);

            File.WriteAllText(temp, json);

            if (File.Exists(AppPaths.ConfigPath))
            {
                File.Replace(temp, AppPaths.ConfigPath, null);
            }
            else
            {
                File.Move(temp, AppPaths.ConfigPath);
            }

            return SettingsSaveResult.Success;
        }
        catch (UnauthorizedAccessException)
        {
            Log.Error("保存配置失败：没有写入权限");
            RemoveTemp(temp);
            return SettingsSaveResult.Failure("没有写入权限（配置文件可能被设为只读，或被其它程序占用）");
        }
        catch (Exception ex)
        {
            Log.Error("保存配置失败", ex);
            RemoveTemp(temp);
            return SettingsSaveResult.Failure(ex.Message);
        }
    }

    private static void RemoveTemp(string temp)
    {
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        catch
        {
            // 临时文件删不掉也不影响结论
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

        // 只夹下限：上限取决于当前屏幕，运行时由朗读板自己按工作区算
        settings.BoardWidth = Math.Max(AppSettings.BoardMinWidth, settings.BoardWidth);
        settings.BoardHeight = Math.Max(AppSettings.BoardMinHeight, settings.BoardHeight);

        // 旧版用 -1/-1 表示"未设置"；负数坐标现在可能是合法的副屏位置
        if (settings.BoardX == -1 && settings.BoardY == -1)
        {
            settings.BoardX = int.MinValue;
            settings.BoardY = int.MinValue;
        }

        settings.Hotkeys ??= [];
    }
}

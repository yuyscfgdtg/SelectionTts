namespace SelectionTts.Core;

/// <summary>
/// 便携优先：配置、日志、缓存都放在 exe 旁边的 data 目录。
/// 如果 exe 所在目录不可写（例如放在受保护位置），退回 %LOCALAPPDATA%\SelectionTts。
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; private set; } = "";
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string CacheDir => Path.Combine(DataDir, "cache");

    /// <summary>
    /// 配置文件路径。自检时会被指到临时目录（见 <see cref="UseTemporaryConfig"/>），
    /// 所以这里是可写属性，不是每次现算。
    /// </summary>
    public static string ConfigPath { get; private set; } = "";

    public static void Initialize()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        DataDir = IsWritable(portable)
            ? portable
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SelectionTts");

        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(CacheDir);

        ConfigPath = Path.Combine(DataDir, "config.json");
    }

    /// <summary>
    /// 自检专用：配置读写改到临时目录里的独立文件，绝不碰用户的真实 config.json。
    ///
    /// 之前 --selftest-config 是"先备份真实配置、跑完再写回"：中途被结束（任务管理器、
    /// 崩溃、断电）就会把用户配置留在被改过的状态。改成自检在临时文件上跑，
    /// 真实配置从头到尾不被打开。
    ///
    /// 日志仍然写真实 data/logs：自检结果要能翻得到。
    /// </summary>
    public static void UseTemporaryConfig()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SelectionTts-selftest");
        Directory.CreateDirectory(directory);
        ConfigPath = Path.Combine(directory, "config.json");

        try
        {
            if (File.Exists(ConfigPath))
            {
                File.Delete(ConfigPath);
            }
        }
        catch (IOException ex)
        {
            Log.Warn($"自检临时配置未清理：{ex.Message}");
        }
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

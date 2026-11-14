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
    public static string ConfigPath => Path.Combine(DataDir, "config.json");

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

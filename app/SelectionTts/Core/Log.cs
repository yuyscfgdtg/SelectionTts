namespace SelectionTts.Core;

public static class Log
{
    private static readonly object Gate = new();
    private static string _path = "";

    public static void Initialize(string dataDir)
    {
        _path = Path.Combine(dataDir, "logs", "app.log");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Rotate();
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        if (string.IsNullOrEmpty(_path))
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // 日志失败不能影响主流程
            }
        }
    }

    private static void Rotate()
    {
        try
        {
            var info = new FileInfo(_path);
            if (info.Exists && info.Length > 2 * 1024 * 1024)
            {
                var old = Path.Combine(info.DirectoryName!, "app.old.log");
                File.Delete(old);
                File.Move(_path, old);
            }
        }
        catch
        {
            // ignore
        }
    }
}

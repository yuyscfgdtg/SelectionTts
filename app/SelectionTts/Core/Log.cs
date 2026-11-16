namespace SelectionTts.Core;

using System.Text;

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

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}" + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);

        lock (Gate)
        {
            // 用 FileShare.ReadWrite 打开：另一个实例在写、或者用户正开着日志文件看，
            // 都不会因为独占句柄把这一行丢掉（以前 File.AppendAllText 撞上冲突就静吞）。
            // 仍然失败就重试一次：杀毒/索引服务偶尔会短暂占用文件。
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var stream = new FileStream(
                        _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    stream.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException) when (attempt == 0)
                {
                    Thread.Sleep(10);
                }
                catch
                {
                    // 日志失败不能影响主流程
                    return;
                }
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

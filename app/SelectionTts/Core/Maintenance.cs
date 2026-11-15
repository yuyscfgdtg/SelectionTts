using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace SelectionTts.Core;

/// <summary>
/// 设置页「立即清理」：清掉磁盘上的缓存/旧日志，再把 GC 后闲置的内存页还给系统。
///
/// 刻意不碰当前朗读的音频缓存：那是「点任意行从那里读」依赖的数据，
/// 清掉会让正在读的文本失去跳读能力。
/// </summary>
public static class Maintenance
{
    public sealed record CleanupResult(
        long FreedBytes,
        int Files,
        int Failed,
        long MemoryBeforeMb,
        long MemoryAfterMb);

    public static CleanupResult Clean()
    {
        var before = WorkingSetMb();
        var (bytes, files, failed) = CleanDisk();
        TrimMemory();
        var after = WorkingSetMb();

        Log.Info(
            $"手动清理：释放 {bytes / 1024.0:F1} KB / {files} 个文件（失败 {failed}）| " +
            $"工作集 {before} → {after} MB");

        return new CleanupResult(bytes, files, failed, before, after);
    }

    private static (long Bytes, int Files, int Failed) CleanDisk()
    {
        long bytes = 0;
        var files = 0;
        var failed = 0;

        foreach (var file in EnumerateSafe(AppPaths.CacheDir))
        {
            try
            {
                // 先删再计数：删除失败的文件不能算进「已释放」
                var length = new FileInfo(file).Length;
                File.Delete(file);
                bytes += length;
                files++;
            }
            catch
            {
                // 个别文件被别的进程占着就跳过，不影响其它文件
                failed++;
            }
        }

        // 轮转出来的旧日志一并清掉；当前 app.log 保留，出问题还要看
        var oldLog = Path.Combine(AppPaths.LogDir, "app.old.log");
        try
        {
            if (File.Exists(oldLog))
            {
                var length = new FileInfo(oldLog).Length;
                File.Delete(oldLog);
                bytes += length;
                files++;
            }
        }
        catch
        {
            failed++;
        }

        return (bytes, files, failed);
    }

    private static IEnumerable<string> EnumerateSafe(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
        }
        catch
        {
            return [];
        }
    }

    private static void TrimMemory()
    {
        try
        {
            // 音频 byte[] 都落在大对象堆上，GC 的 compacting 参数管不到它，要单独置一次
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            // 把已经空闲的页从工作集里摘掉，任务管理器里的占用会立刻降下来
            EmptyWorkingSet(GetCurrentProcess());
        }
        catch (Exception ex)
        {
            Log.Warn($"内存整理失败：{ex.Message}");
        }
    }

    private static long WorkingSetMb()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.WorkingSet64 / (1024 * 1024);
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}

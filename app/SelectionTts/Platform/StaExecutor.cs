using SelectionTts.Core;

namespace SelectionTts.Platform;

/// <summary>
/// 剪贴板和 UI Automation 都要求 STA。朗读流程跑在后台线程（MTA），
/// 这里用一次性 STA 线程执行，并带超时保护，避免 UIA 卡死整个朗读。
/// </summary>
internal static class StaExecutor
{
    public static bool TryRun<T>(Func<T> func, int timeoutMs, out T? result)
    {
        T? value = default;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                value = func();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
            Name = "sta-selection",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(timeoutMs))
        {
            Log.Warn($"STA 任务超过 {timeoutMs}ms，放弃等待");
            result = default;
            return false;
        }

        if (error is not null)
        {
            Log.Error("STA 任务异常", error);
            result = default;
            return false;
        }

        result = value;
        return true;
    }
}

using System.Diagnostics;
using SelectionTts.Core;

namespace SelectionTts.Platform;

public sealed record SelectionResult(
    string Text,
    string Method,
    int ElapsedMs,
    bool? ClipboardRestored = null);

public static class SelectionReader
{
    private static readonly HashSet<string> ConsoleClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass",
        "CASCADIA_HOSTING_WINDOW_CLASS",
        "PseudoConsoleWindow",
    };

    // 文件管理器复制的是文件列表（CF_HDROP），永远不是文本；
    // 在这类窗口里按 Ctrl+C 只会覆盖用户剪贴板，所以直接跳过。
    private static readonly HashSet<string> FileSelectionClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CabinetWClass",
        "ExploreWClass",
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    private const int UiaTimeoutMs = 3000;
    private const int ClipboardTimeoutMs = 1600;

    public static SelectionResult Read(
        bool preferUia,
        bool restoreClipboard,
        int waitReleaseMs = 220,
        bool allowSelf = false)
    {
        var watch = Stopwatch.StartNew();

        var foreground = ForegroundInfo.Current();
        Log.Info($"取词前台 class={foreground.ClassName} pid={foreground.ProcessId} process={foreground.ProcessName}");

        if (preferUia)
        {
            if (StaExecutor.TryRun(UiaReader.TryRead, UiaTimeoutMs, out var uia) && uia != default)
            {
                if (!string.IsNullOrWhiteSpace(uia.Text))
                {
                    Log.Info($"UIA 取词成功：{uia.Text.Length} 字");
                    return new SelectionResult(uia.Text, "uia", (int)watch.ElapsedMilliseconds);
                }

                if (uia.Status is "self" or "password")
                {
                    Log.Info($"UIA 状态 {uia.Status}，跳过剪贴板兜底");
                    return new SelectionResult("", "uia", (int)watch.ElapsedMilliseconds);
                }

                Log.Info($"UIA 未取到选区（{uia.Status}），改用剪贴板");
            }
            else
            {
                Log.Warn("UIA 取词超时或失败");
            }
        }

        if (foreground.ProcessId == Environment.ProcessId)
        {
            if (!allowSelf)
            {
                Log.Info("前台窗口属于本程序，跳过取词");
                return new SelectionResult("", "clipboard", (int)watch.ElapsedMilliseconds);
            }
        }

        if (FileSelectionClasses.Contains(foreground.ClassName))
        {
            Log.Info($"文件管理器窗口（{foreground.ClassName}）跳过剪贴板兜底");
            return new SelectionResult("", "clipboard", (int)watch.ElapsedMilliseconds);
        }

        if (ConsoleClasses.Contains(foreground.ClassName))
        {
            Log.Info($"控制台窗口（{foreground.ClassName}）跳过剪贴板兜底");
            return new SelectionResult("", "clipboard", (int)watch.ElapsedMilliseconds);
        }

        if (waitReleaseMs > 0)
        {
            Thread.Sleep(waitReleaseMs);
        }

        var started = watch.ElapsedMilliseconds;
        var clipboard = RunClipboardCopy(restoreClipboard);
        if (clipboard is null)
        {
            return new SelectionResult("", "clipboard", (int)(watch.ElapsedMilliseconds - started));
        }

        Log.Info($"剪贴板取词成功：{clipboard.Value.Text.Length} 字");
        return new SelectionResult(
            clipboard.Value.Text,
            "clipboard",
            (int)(watch.ElapsedMilliseconds - started),
            clipboard.Value.Restored);
    }

    private static (string Text, bool Restored)? RunClipboardCopy(bool restore)
    {
        var ok = StaExecutor.TryRun(() => CopyAndRead(restore), ClipboardTimeoutMs + 1500, out var result);
        return ok ? result : null;
    }

    private static (string Text, bool Restored)? CopyAndRead(bool restore)
    {
        var snapshot = ClipboardAccess.Capture();
        KeyboardInput.SendCtrlC();

        var deadline = Environment.TickCount64 + ClipboardTimeoutMs;
        string? captured = null;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(30);
            var text = ClipboardAccess.TryGetText();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (snapshot.Text is not null && text == snapshot.Text)
            {
                // 还没有变化，可能目标窗口是延迟渲染的（例如 Excel）
                continue;
            }

            captured = text;
            break;
        }

        if (captured is null)
        {
            Log.Info("Ctrl+C 未改变剪贴板，视为没有选中文本");
            return null;
        }

        var restored = false;
        if (restore)
        {
            restored = ClipboardAccess.Restore(snapshot);
        }

        return (captured, restored);
    }
}

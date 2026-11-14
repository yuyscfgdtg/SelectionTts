using System.Runtime.InteropServices;
using System.Diagnostics;

namespace SelectionTts.Platform;

internal static class ForegroundInfo
{
    public static IntPtr Handle => NativeMethods.GetForegroundWindow();

    public static uint ProcessId
    {
        get
        {
            var handle = Handle;
            return handle == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(handle, out var pid) == 0 ? 0 : pid;
        }
    }

    public static string ClassName
    {
        get
        {
            var handle = Handle;
            if (handle == IntPtr.Zero)
            {
                return "";
            }

            var buffer = new char[256];
            var length = NativeMethods.GetClassName(handle, buffer, buffer.Length);
            return length > 0 ? new string(buffer, 0, length) : "";
        }
    }

    public static string ProcessName(uint pid)
    {
        if (pid == 0)
        {
            return "";
        }

        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
        {
            return "";
        }

        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            if (!NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size))
            {
                return "";
            }

            return Path.GetFileName(new string(buffer, 0, size));
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    public static (uint ProcessId, string ClassName, string ProcessName) Current()
    {
        var pid = ProcessId;
        return (pid, ClassName, ProcessName(pid));
    }

    /// <summary>
    /// 从后台进程把自己创建的窗口带到前台：先 AttachThreadInput 到当前前台线程，
    /// 再 SetForegroundWindow，最后解除附加。
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();

        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        }

        NativeMethods.SetForegroundWindow(hwnd);

        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }
}

using System.Windows.Forms;
using SelectionTts.Core;

namespace SelectionTts.Platform;

/// <summary>
/// 用 AddClipboardFormatListener + WM_CLIPBOARDUPDATE 监听剪贴板变化（事件驱动，不轮询）。
///
/// 规则：
/// - 只在 Enabled 时处理；关闭期间复制的内容不会补读
/// - 连续快速复制会在 100ms 窗口内合并，只读最后一次
/// - 事件到达后等 100ms 再读（Excel / 远程桌面延迟渲染），被占用时重试 3 次
/// - 同一文本 3 秒内不重复朗读
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int ReadDelayMs = 100;
    private const int DuplicateWindowMs = 3000;
    private const int ReadTimeoutMs = 1200;

    private readonly ClipboardWindow _window = new();
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private int _busy;
    private int _pending;
    private string? _lastText;
    private long _lastAt;

    public ClipboardWatcher()
    {
        _timer = new System.Threading.Timer(_ => ReadLatest(), null, Timeout.Infinite, Timeout.Infinite);

        _window.ClipboardChanged = OnClipboardUpdate;
        _window.CreateHandle(new CreateParams
        {
            Caption = "SelectionTts.ClipboardWatcher",
            Parent = NativeMethods.HWND_MESSAGE,
        });

        if (!NativeMethods.AddClipboardFormatListener(_window.Handle))
        {
            Log.Warn("注册剪贴板监听失败");
        }
    }

    public bool Enabled { get; private set; }

    public event Action<string>? TextCopied;

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;

        lock (_gate)
        {
            // 记住当前内容：开启瞬间已经存在的文本不会被当成"新复制"
            _lastText = enabled ? SafePeek() : _lastText;
            _lastAt = Environment.TickCount64;
        }

        Log.Info($"剪贴板监听：{(enabled ? "开启" : "关闭")}");
    }

    private void OnClipboardUpdate()
    {
        if (!Enabled)
        {
            return;
        }

        // 记下"又有新内容"再重启定时器：连续事件只会在最后一次之后 100ms 触发一次读取。
        // pending 的作用是防止读取较慢时，期间到达的新复制被丢掉。
        Interlocked.Exchange(ref _pending, 1);
        _timer.Change(ReadDelayMs, Timeout.Infinite);
    }

    private void ReadLatest()
    {
        if (!Enabled)
        {
            return;
        }

        // 已有读取在跑：pending 保留，等那一次收尾后自动补读
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        // 本次读取就是来处理 pending 的
        Interlocked.Exchange(ref _pending, 0);

        try
        {
            var text = ReadWithRetry();
            if (string.IsNullOrWhiteSpace(text) || !Enabled)
            {
                return;
            }

            var now = Environment.TickCount64;
            lock (_gate)
            {
                if (text == _lastText && now - _lastAt < DuplicateWindowMs)
                {
                    Log.Info("剪贴板内容与上次相同且间隔很短，忽略");
                    return;
                }

                _lastText = text;
                _lastAt = now;
            }

            Log.Info($"检测到复制：{text.Length} 字");
            TextCopied?.Invoke(text);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);

            // 读取期间又发生过复制：立刻补一次，最终只朗读最新内容
            if (Interlocked.Exchange(ref _pending, 0) == 1 && Enabled)
            {
                _timer.Change(ReadDelayMs, Timeout.Infinite);
            }
        }
    }

    /// <summary>
    /// 读不到文本时再试两次：有些程序（Excel、远程桌面）用延迟渲染，
    /// 剪贴板事件到达时还没准备好。只在"读空"时重试，读到就不再看第二眼——
    /// 同一次复制只会提交一次结果，不会重复朗读。
    /// </summary>
    private static string? ReadWithRetry()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (!StaExecutor.TryRun(ClipboardAccess.Read, ReadTimeoutMs, out var result))
            {
                return null;
            }

            if (!result.Retry)
            {
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    return result.Text;
                }

                if (attempt == 3)
                {
                    return null;
                }

                Thread.Sleep(attempt == 1 ? 120 : 180);
                continue;
            }

            if (attempt < 3)
            {
                Thread.Sleep(60);
            }
        }

        return null;
    }

    private static string? SafePeek()
    {
        if (!StaExecutor.TryRun(ClipboardAccess.Read, ReadTimeoutMs, out var result))
        {
            return null;
        }

        return result.Text;
    }

    public void Dispose()
    {
        Enabled = false;
        _timer.Dispose();

        if (_window.Handle != IntPtr.Zero)
        {
            NativeMethods.RemoveClipboardFormatListener(_window.Handle);
            _window.DestroyHandle();
        }
    }

    private sealed class ClipboardWindow : System.Windows.Forms.NativeWindow
    {
        public Action? ClipboardChanged { get; set; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
            {
                ClipboardChanged?.Invoke();
                return;
            }

            base.WndProc(ref m);
        }
    }
}

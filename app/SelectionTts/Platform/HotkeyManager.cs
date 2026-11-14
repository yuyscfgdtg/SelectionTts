using System.Runtime.InteropServices;
using System.Windows.Forms;
using SelectionTts.Core;

namespace SelectionTts.Platform;

/// <summary>
/// 用一个 message-only 窗口接收热键，Ctrl+Alt+R 切换"监听剪贴板"。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int ToggleWatchId = 1;
    private const uint VkR = 0x52;

    private readonly HotkeyWindow _window = new();

    public event Action? ToggleWatchRequested;

    public HotkeyManager()
    {
        _window.HotkeyPressed += OnHotkeyPressed;
        _window.EnsureHandle();
    }

    public bool RegisterDefaults()
    {
        var ok = NativeMethods.RegisterHotKey(
            _window.Handle,
            ToggleWatchId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            VkR);

        if (!ok)
        {
            Log.Warn($"注册 Ctrl+Alt+R 失败，错误码 {Marshal.GetLastWin32Error()}");
        }

        return ok;
    }

    private void OnHotkeyPressed(int id)
    {
        if (id != ToggleWatchId)
        {
            return;
        }

        Log.Info("热键触发：切换监听");
        ToggleWatchRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_window.Handle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, ToggleWatchId);
        }

        _window.DestroyHandle();
    }

    private sealed class HotkeyWindow : NativeWindow
    {
        public event Action<int>? HotkeyPressed;

        public void EnsureHandle()
        {
            if (Handle == IntPtr.Zero)
            {
                CreateHandle(new CreateParams
                {
                    Caption = "SelectionTts.HotkeyWindow",
                    Parent = NativeMethods.HWND_MESSAGE,
                });
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                HotkeyPressed?.Invoke(m.WParam.ToInt32());
                return;
            }

            base.WndProc(ref m);
        }
    }
}

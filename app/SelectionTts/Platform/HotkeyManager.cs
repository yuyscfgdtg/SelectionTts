using System.Runtime.InteropServices;
using System.Windows.Forms;
using SelectionTts.Core;

namespace SelectionTts.Platform;

/// <summary>
/// 用 Win32 RegisterHotKey 注册全局热键（不使用键盘钩子）。
/// 消息通过一个 message-only 窗口接收。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int ReadId = 1;
    private const int TogglePopupId = 2;
    private const uint VkR = 0x52;
    private const uint VkP = 0x50;

    private readonly HotkeyWindow _window = new();

    public event Action? ReadRequested;
    public event Action? TogglePopupRequested;

    public HotkeyManager()
    {
        _window.HotkeyPressed += OnHotkeyPressed;
        _window.EnsureHandle();
    }

    public bool RegisterDefaults()
    {
        var okRead = NativeMethods.RegisterHotKey(
            _window.Handle,
            ReadId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            VkR);

        var okToggle = NativeMethods.RegisterHotKey(
            _window.Handle,
            TogglePopupId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT,
            VkP);

        if (!okRead)
        {
            Log.Warn($"注册 Ctrl+Alt+R 失败，错误码 {Marshal.GetLastWin32Error()}");
        }

        if (!okToggle)
        {
            Log.Warn($"注册 Ctrl+Alt+Shift+P 失败，错误码 {Marshal.GetLastWin32Error()}");
        }

        return okRead && okToggle;
    }

    private void OnHotkeyPressed(int id)
    {
        Log.Info($"热键触发 id={id}");
        switch (id)
        {
            case ReadId:
                ReadRequested?.Invoke();
                break;
            case TogglePopupId:
                TogglePopupRequested?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_window.Handle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, ReadId);
            NativeMethods.UnregisterHotKey(_window.Handle, TogglePopupId);
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

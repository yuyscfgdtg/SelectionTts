using System.Runtime.InteropServices;
using System.Windows.Forms;
using SelectionTts.Core;

namespace SelectionTts.Platform;

/// <summary>
/// 热键注册器。只负责"把动作绑到组合键"和"按下时报告动作 id"，
/// 不关心动作是什么，业务方在 App 里查表分发。
///
/// 换绑是"先注册新的、成功后才释放旧的"：如果新组合被别的程序占用，
/// 旧热键原样保留，不会出现"改完热键直接失效"。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private sealed record Entry(int Id, HotkeyGesture Gesture);

    private readonly HotkeyWindow _window = new();
    private readonly Dictionary<string, Entry> _current = [];
    private readonly Dictionary<int, string> _byId = [];
    private int _nextId = 1;

    public event Action<string>? Pressed;

    public HotkeyManager()
    {
        _window.HotkeyPressed += id =>
        {
            if (_byId.TryGetValue(id, out var actionId))
            {
                Log.Info($"热键触发：{HotkeyActions.LabelOf(actionId)}");
                Pressed?.Invoke(actionId);
            }
        };

        _window.EnsureHandle();
    }

    public bool TryApply(IEnumerable<(string ActionId, HotkeyGesture Gesture)> desired, out string error)
    {
        error = "";
        var list = desired.ToList();

        var used = new Dictionary<HotkeyGesture, string>();
        foreach (var (actionId, gesture) in list)
        {
            if (used.TryGetValue(gesture, out var other))
            {
                error = $"{HotkeyActions.LabelOf(actionId)} 与 {HotkeyActions.LabelOf(other)} 使用了同一个组合键";
                return false;
            }

            used[gesture] = actionId;
        }

        var added = new List<(string ActionId, int Id, HotkeyGesture Gesture)>();

        foreach (var (actionId, gesture) in list)
        {
            if (_current.TryGetValue(actionId, out var existing) && existing.Gesture == gesture)
            {
                continue;
            }

            var id = _nextId++;
            if (!Register(id, gesture))
            {
                foreach (var (_, addedId, _) in added)
                {
                    Unregister(addedId);
                }

                error = $"{HotkeyActions.LabelOf(actionId)} 的 {gesture} 已被其它程序占用";
                Log.Warn($"注册热键失败：{actionId} {gesture} 错误码 {Marshal.GetLastWin32Error()}");
                return false;
            }

            added.Add((actionId, id, gesture));
        }

        foreach (var (actionId, id, gesture) in added)
        {
            if (_current.TryGetValue(actionId, out var existing))
            {
                Unregister(existing.Id);
                _byId.Remove(existing.Id);
            }

            _current[actionId] = new Entry(id, gesture);
            _byId[id] = actionId;
            Log.Info($"热键绑定：{HotkeyActions.LabelOf(actionId)} = {gesture}");
        }

        return true;
    }

    /// <summary>当前生效的组合键文本，用于托盘菜单显示。</summary>
    public string Describe(string actionId) =>
        _current.TryGetValue(actionId, out var entry) ? entry.Gesture.ToString() : "";

    /// <summary>临时释放全部热键（录制新组合键时用，避免按到当前组合键触发动作）。</summary>
    public void UnregisterAll()
    {
        foreach (var entry in _current.Values)
        {
            Unregister(entry.Id);
        }

        _current.Clear();
        _byId.Clear();
    }

    private bool Register(int id, HotkeyGesture gesture) =>
        NativeMethods.RegisterHotKey(
            _window.Handle,
            id,
            gesture.Modifiers | HotkeyGesture.ModNoRepeat,
            gesture.VirtualKey);

    private void Unregister(int id) =>
        NativeMethods.UnregisterHotKey(_window.Handle, id);

    public void Dispose()
    {
        foreach (var entry in _current.Values)
        {
            Unregister(entry.Id);
        }

        _current.Clear();
        _byId.Clear();
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

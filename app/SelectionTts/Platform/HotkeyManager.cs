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

        var failures = new List<(string ActionId, HotkeyGesture Gesture, string Reason)>();

        // 逐个注册、成功立刻提交：某个动作注册失败时，它的旧热键原样保留，
        // 其它动作（比如总开关）照常换到新组合键。
        foreach (var (actionId, gesture) in list)
        {
            TryBind(actionId, gesture, failures);
        }

        // 换绑交换场景：A 的新键 = B 的旧键、B 的新键 = A 的旧键，
        // 第一轮两边都注册不上（被对方旧键占着）。把占着的旧键先松开再试一次；
        // 松开后仍然失败（真被外部程序占用）就把旧键恢复回去。
        for (var pass = 0; failures.Count > 0 && pass < list.Count; pass++)
        {
            var pending = failures.ToArray();
            failures.Clear();

            foreach (var (actionId, gesture, _) in pending)
            {
                // 占着这个组合键的若是"自己人"（它自己也要换键），先让它让位
                (string ActionId, Entry Entry)? released = null;
                var holder = FindHolder(gesture);
                if (holder is not null && MakeRoom(holder, list, out var room))
                {
                    released = room;
                }

                var before = failures.Count;
                TryBind(actionId, gesture, failures);

                if (failures.Count > before && released is not null)
                {
                    // 让了位还是不行：把刚松开的旧绑定恢复，别让热键凭空消失
                    if (!Restore(released.Value, failures))
                    {
                        failures.Add((
                            released.Value.ActionId,
                            released.Value.Entry.Gesture,
                            $"{HotkeyActions.LabelOf(released.Value.ActionId)} 的 {released.Value.Entry.Gesture} 重新注册失败"));
                    }
                }
            }
        }

        // 配置里已经没有的动作（用户清除了绑定）要真正释放，
        // 否则"点默认清除"要等到重启才生效。
        foreach (var actionId in _current.Keys.ToList())
        {
            if (list.Any(item => item.ActionId == actionId))
            {
                continue;
            }

            var entry = _current[actionId];
            Unregister(entry.Id);
            _byId.Remove(entry.Id);
            _current.Remove(actionId);
            Log.Info($"热键解绑：{HotkeyActions.LabelOf(actionId)}");
        }

        if (failures.Count > 0)
        {
            error = string.Join("；", failures.Select(f => f.Reason));

            foreach (var (_, _, reason) in failures)
            {
                Log.Warn($"注册热键失败：{reason}");
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// 设置窗口保存专用：整批生效，任一动作失败就回滚到保存前的绑定。
    ///
    /// 启动时仍走 <see cref="TryApply"/> 的「部分成功」语义——一个动作撞键不该阻止
    /// 其它动作换键；但保存是用户的一次显式操作，失败必须什么都不变，否则
    /// 配置文件和实际生效的热键会对不上。
    /// </summary>
    public bool TryApplyExclusive(
        IReadOnlyList<(string ActionId, HotkeyGesture Gesture)> desired,
        out string error)
    {
        var snapshot = _current
            .Select(pair => (ActionId: pair.Key, Gesture: pair.Value.Gesture))
            .ToList();

        if (TryApply(desired, out error))
        {
            return true;
        }

        var failure = error;

        // 回滚：先全部松开，再按快照恢复。恢复也可能失败（组合键被外部抢占），
        // 那种情况只能如实报告，不能让调用方以为已经还原。
        UnregisterAll();

        var restoreFailures = new List<(string ActionId, HotkeyGesture Gesture, string Reason)>();
        foreach (var (actionId, gesture) in snapshot)
        {
            TryBind(actionId, gesture, restoreFailures);
        }

        error = restoreFailures.Count == 0
            ? $"{failure}（设置未保存，原热键已恢复）"
            : $"{failure}；恢复原热键也失败：{string.Join("；", restoreFailures.Select(f => f.Reason))}";

        return false;
    }

    /// <summary>注册一个组合键，成功立刻替换该动作的旧绑定；失败记入 failures。</summary>
    private void TryBind(string actionId, HotkeyGesture gesture, List<(string, HotkeyGesture, string)> failures)
    {
        if (_current.TryGetValue(actionId, out var existing) && existing.Gesture == gesture)
        {
            return;
        }

        var id = _nextId++;
        var registered = Register(id, gesture);

        // 错误码必须紧跟 P/Invoke 取，中间再调 Unregister 之类就会被覆盖成 0
        var code = registered ? 0 : Marshal.GetLastWin32Error();

        if (!registered)
        {
            failures.Add((actionId, gesture, $"{HotkeyActions.LabelOf(actionId)} 的 {gesture} {DescribeError(code)}"));
            return;
        }

        if (existing is not null)
        {
            Unregister(existing.Id);
            _byId.Remove(existing.Id);
        }

        _current[actionId] = new Entry(id, gesture);
        _byId[id] = actionId;
        Log.Info($"热键绑定：{HotkeyActions.LabelOf(actionId)} = {gesture}");
    }

    private Entry? FindHolder(HotkeyGesture gesture) =>
        _current.Values.FirstOrDefault(entry => entry.Gesture == gesture);

    /// <summary>
    /// 交换场景专用：holder 现在占着别人想要的组合键，而它自己也要换键，
    /// 先把它松开（记下原状），让想注册的那个动作先试。
    /// </summary>
    private bool MakeRoom(
        Entry holder,
        List<(string ActionId, HotkeyGesture Gesture)> desired,
        out (string ActionId, Entry Entry) released)
    {
        released = default;

        var holderAction = _byId.TryGetValue(holder.Id, out var actionId) ? actionId : null;
        if (holderAction is null)
        {
            return false;
        }

        // 持有者自己不改键就不能松：松了它就没有热键了
        var holderDesired = desired.FirstOrDefault(item => item.ActionId == holderAction).Gesture;
        if (holderDesired == holder.Gesture)
        {
            return false;
        }

        Unregister(holder.Id);
        _byId.Remove(holder.Id);
        _current.Remove(holderAction);
        Log.Info($"热键临时释放：{HotkeyActions.LabelOf(holderAction)} 的 {holder.Gesture}（换绑让位）");
        released = (holderAction, holder);
        return true;
    }

    /// <summary>让位之后注册还是没成功：把刚松开的旧绑定恢复回去。</summary>
    private bool Restore((string ActionId, Entry Entry) released, List<(string, HotkeyGesture, string)> failures)
    {
        var registered = Register(released.Entry.Id, released.Entry.Gesture);
        if (!registered)
        {
            return false;
        }

        _current[released.ActionId] = released.Entry;
        _byId[released.Entry.Id] = released.ActionId;
        Log.Info($"热键恢复：{HotkeyActions.LabelOf(released.ActionId)} = {released.Entry.Gesture}");
        return true;
    }

    private static string DescribeError(int code) => code switch
    {
        1409 => "已被其它程序占用",
        5 => "权限不足（可能被系统或管理员程序占用）",
        _ => $"注册失败（系统错误 {code}）",
    };

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

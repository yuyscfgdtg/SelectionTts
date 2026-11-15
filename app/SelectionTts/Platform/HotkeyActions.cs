namespace SelectionTts.Platform;

/// <summary>
/// 可配热键的动作清单。
///
/// 以后要加"停止朗读""显示朗读板"之类的热键，只需要在 All 里加一行，
/// 设置窗口会自动多出一项，注册器不需要改。
/// </summary>
public sealed record HotkeyAction(string Id, string Label, string DefaultGesture);

public static class HotkeyActions
{
    public static readonly HotkeyAction ToggleWatch =
        new("toggle-watch", "开启/关闭朗读", "Ctrl+Alt+R");

    /// <summary>出厂不绑定组合键：留空，由用户自己录。</summary>
    public static readonly HotkeyAction TogglePause =
        new("toggle-pause", "暂停/继续朗读", "");

    public static IReadOnlyList<HotkeyAction> All { get; } = [ToggleWatch, TogglePause];

    public static string DefaultOf(string actionId) =>
        All.FirstOrDefault(action => action.Id == actionId)?.DefaultGesture ?? "";

    public static string LabelOf(string actionId) =>
        All.FirstOrDefault(action => action.Id == actionId)?.Label ?? actionId;
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SelectionTts.Core;
using SelectionTts.Platform;
using SelectionTts.Tts;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace SelectionTts.Ui;

/// <summary>
/// 设置窗口：引擎（Edge / Azure）、Azure 区域与 Key、快捷键录制。
///
/// 窗口只负责改配置，真正的"换引擎 / 重新注册热键"由 App 在 Saved 之后执行，
/// 这样设置逻辑和运行状态不会互相纠缠。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, HotkeyGesture> _gestures = [];
    private readonly Dictionary<string, TextBlock> _gestureTexts = [];

    private string? _recording;

    /// <summary>点过「恢复默认尺寸」之后置真，保存时才真正写回默认尺寸。</summary>
    private bool _boardSizeReset;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        LoadValues();
        BuildHotkeyRows();

        EdgeRadio.Checked += (_, _) => UpdateAzurePanel();
        AzureRadio.Checked += (_, _) => UpdateAzurePanel();
        RevealKey.Checked += (_, _) => RevealKeyChanged(true);
        RevealKey.Unchecked += (_, _) => RevealKeyChanged(false);

        TestButton.Click += OnTestClicked;
        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();
        ResetBoardSizeButton.Click += (_, _) => MarkBoardSizeReset();
        CleanupButton.Click += (_, _) => RunCleanup();

        PreviewKeyDown += OnPreviewKeyDown;
        RefreshGestureTexts();
        UpdateAzurePanel();
        RefreshBoardSizeText();
    }

    /// <summary>录制热键前挂起全局热键，避免按到当前组合键时把软件开关掉。</summary>
    public Action? SuspendHotkeys { get; set; }

    /// <summary>按当前配置重新注册热键（取消录制、保存失败回滚时用）。</summary>
    public Action? ResumeHotkeys { get; set; }

    /// <summary>
    /// 设置窗口保存时先让全部新热键生效；返回失败时注册器已经回滚到原绑定。
    /// 只有成功才允许落盘，保证「磁盘里的配置 = 实际生效的热键」。
    /// </summary>
    public Func<IReadOnlyList<(string ActionId, HotkeyGesture Gesture)>, (bool Ok, string Error)>? ApplyHotkeys { get; set; }

    /// <summary>「立即清理」由 App 注入实际实现，窗口只负责展示结果。</summary>
    public Func<Maintenance.CleanupResult>? Cleanup { get; set; }

    public bool Saved { get; private set; }

    private void LoadValues()
    {
        var isAzure = string.Equals(_settings.Engine, "azure", StringComparison.OrdinalIgnoreCase);
        AzureRadio.IsChecked = isAzure;
        EdgeRadio.IsChecked = !isAzure;
        RegionBox.Text = _settings.AzureRegion;
        KeyPassword.Password = Dpapi.Unprotect(_settings.AzureKeyProtected);
        AutoStartCheck.IsChecked = AutoStart.IsEnabled;
    }

    private void UpdateAzurePanel() =>
        AzurePanel.IsEnabled = AzureRadio.IsChecked == true;

    private void RefreshBoardSizeText()
    {
        BoardSizeText.Text = _boardSizeReset
            ? $"已恢复默认 {AppSettings.BoardDefaultWidth} × {AppSettings.BoardDefaultHeight}（保存后生效）"
            : $"尺寸：{_settings.BoardWidth} × {_settings.BoardHeight}";
        BoardSizeText.Foreground = _boardSizeReset
            ? System.Windows.Media.Brushes.DodgerBlue
            : System.Windows.Media.Brushes.Black;
    }

    private void MarkBoardSizeReset()
    {
        _boardSizeReset = true;
        RefreshBoardSizeText();
    }

    private async void RunCleanup()
    {
        if (Cleanup is null)
        {
            CleanupResult.Text = "清理功能不可用";
            return;
        }

        // 两次完整 GC 在长文朗读后可能造成可感知停顿：放到后台，按钮先禁用防重复点击
        CleanupButton.IsEnabled = false;
        CleanupResult.Text = "清理中…";

        try
        {
            var result = await Task.Run(Cleanup);

            var parts = new List<string>();

            if (result.Files > 0)
            {
                var kb = result.FreedBytes / 1024.0;
                parts.Add(kb >= 1 ? $"已清理 {kb:F1} KB（{result.Files} 个文件）" : $"已清理 {result.FreedBytes} B（{result.Files} 个文件）");
            }
            else
            {
                parts.Add("缓存已是空的");
            }

            parts.Add($"内存 {result.MemoryBeforeMb} → {result.MemoryAfterMb} MB");

            if (result.Failed > 0)
            {
                parts.Add($"（{result.Failed} 个文件被占用，已跳过）");
            }

            CleanupResult.Text = string.Join(" · ", parts);
        }
        catch (Exception ex)
        {
            // Clean 内部已经逐项兜底，这里只防极端情况（磁盘被拔、句柄失效）拖垮 UI
            CleanupResult.Text = $"清理失败：{ex.Message}";
            Log.Error("手动清理失败", ex);
        }
        finally
        {
            CleanupButton.IsEnabled = true;
        }
    }

    private void RevealKeyChanged(bool reveal)
    {
        if (reveal)
        {
            KeyText.Text = KeyPassword.Password;
            KeyText.Visibility = Visibility.Visible;
            KeyPassword.Visibility = Visibility.Collapsed;
            KeyText.Focus();
            KeyText.CaretIndex = KeyText.Text.Length;
        }
        else
        {
            KeyPassword.Password = KeyText.Text;
            KeyText.Visibility = Visibility.Collapsed;
            KeyPassword.Visibility = Visibility.Visible;
        }
    }

    private string CurrentKey() =>
        RevealKey.IsChecked == true ? KeyText.Text : KeyPassword.Password;

    private void BuildHotkeyRows()
    {
        foreach (var action in HotkeyActions.All)
        {
            var gesture = HotkeyGesture.TryParse(
                _settings.Hotkeys.GetValueOrDefault(action.Id), out var stored)
                ? stored
                : (HotkeyGesture.TryParse(action.DefaultGesture, out var fallback) ? fallback : default);

            _gestures[action.Id] = gesture;
            HotkeyRows.Children.Add(BuildHotkeyRow(action, gesture));
        }
    }

    private FrameworkElement BuildHotkeyRow(HotkeyAction action, HotkeyGesture gesture)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = action.Label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var value = new TextBlock
        {
            Text = gesture.ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Margin = new Thickness(0, 0, 12, 0),
            MinWidth = 110,
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        _gestureTexts[action.Id] = value;

        var record = new Button
        {
            Content = "录制",
            Padding = new Thickness(12, 4, 12, 4),
            Tag = action.Id,
        };
        record.Click += (_, _) => StartRecording(action.Id);
        Grid.SetColumn(record, 2);
        grid.Children.Add(record);

        var reset = new Button
        {
            Content = "默认",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            Tag = action.Id,
        };
        reset.Click += (_, _) => ResetHotkey(action.Id);
        Grid.SetColumn(reset, 3);
        grid.Children.Add(reset);

        return grid;
    }

    private void ResetHotkey(string actionId)
    {
        if (HotkeyGesture.TryParse(HotkeyActions.DefaultOf(actionId), out var fallback))
        {
            _gestures[actionId] = fallback;
            RefreshGestureTexts();
        }
    }

    private void StartRecording(string actionId)
    {
        if (_recording is not null)
        {
            return;
        }

        _recording = actionId;
        SuspendHotkeys?.Invoke();
        RefreshGestureTexts();
        Activate();
        Focus();
    }

    private void StopRecording()
    {
        if (_recording is null)
        {
            return;
        }

        _recording = null;
        ResumeHotkeys?.Invoke();
        RefreshGestureTexts();
    }

    private void RefreshGestureTexts()
    {
        foreach (var (actionId, block) in _gestureTexts)
        {
            if (actionId == _recording)
            {
                block.Text = "按下组合键…（Esc 取消）";
                block.Foreground = System.Windows.Media.Brushes.DarkOrange;
            }
            else if (!_gestures[actionId].IsValid)
            {
                block.Text = "未设置";
                block.Foreground = System.Windows.Media.Brushes.Gray;
            }
            else
            {
                block.Text = _gestures[actionId].ToString();
                block.Foreground = System.Windows.Media.Brushes.Black;
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            StopRecording();
            return;
        }

        // 按住 Alt 时 WPF 把真实键放在 SystemKey 里，e.Key 只是 Key.System；
        // 直接用 e.Key 会把 Ctrl+Alt+R 录成 Ctrl+NoName。
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (!HotkeyGesture.TryFromKeyboard(Keyboard.Modifiers, key, out var gesture))
        {
            return;
        }

        _gestures[_recording] = gesture;
        StopRecording();
    }

    private async void OnTestClicked(object sender, RoutedEventArgs e)
    {
        var key = CurrentKey();
        var region = RegionBox.Text.Trim();

        if (region.Length == 0 || key.Length == 0)
        {
            TestResult.Text = "请先填写区域和 Key";
            return;
        }

        TestButton.IsEnabled = false;
        TestResult.Text = "合成中…";

        try
        {
            var client = new AzureTtsClient(region, key);
            using var buffer = new MemoryStream();
            var summary = await client.StreamAsync(
                "这是一段 Azure 引擎的测试语音。",
                _settings.Voice,
                _settings.Rate,
                chunk => buffer.Write(chunk.Span),
                null,
                CancellationToken.None);

            TestResult.Text = $"成功：{summary.ElapsedMs}ms，音频 {summary.DurationSeconds:F1}s";
            Log.Info($"Azure 测试成功 | 字节={summary.AudioBytes} | 耗时={summary.ElapsedMs}ms");
        }
        catch (Exception ex)
        {
            TestResult.Text = $"失败：{ex.Message}";
            Log.Warn($"Azure 测试失败：{ex.Message}");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Save()
    {
        // 只结束录制态，不在这里恢复热键：此时新配置还没写进去，
        // 恢复了也是旧组合键，等于"保存了新热键但要用到重启"。
        if (_recording is not null)
        {
            _recording = null;
            RefreshGestureTexts();
        }

        var useAzure = AzureRadio.IsChecked == true;
        var key = CurrentKey().Trim();
        var region = RegionBox.Text.Trim();

        if (useAzure && (key.Length == 0 || region.Length == 0))
        {
            MessageBox.Show(this, "选择 Azure 引擎时必须填写区域和 Key。", "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 撞键要在保存前拦住：HotkeyManager 会整批拒绝，但那时配置已经写进去了
        var used = new Dictionary<HotkeyGesture, string>();
        foreach (var (actionId, gesture) in _gestures)
        {
            if (!gesture.IsValid)
            {
                continue;
            }

            if (used.TryGetValue(gesture, out var other))
            {
                MessageBox.Show(
                    this,
                    $"「{HotkeyActions.LabelOf(actionId)}」与「{HotkeyActions.LabelOf(other)}」使用了同一个组合键 {gesture}。\n" +
                    "请给其中一个换一组键，或点「默认」清除。",
                    "设置",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            used[gesture] = actionId;
        }

        // —— 校验全部通过：所有改动先落在草稿上 ——
        // _settings 是和朗读板/托盘共享的实例，保存失败时它必须保持原样。
        var draft = SettingsStore.Clone(_settings);
        draft.Engine = useAzure ? "azure" : "edge";
        draft.AzureRegion = region;
        draft.AzureKeyProtected = key.Length == 0 ? "" : Dpapi.Protect(key);

        if (_boardSizeReset)
        {
            draft.BoardWidth = AppSettings.BoardDefaultWidth;
            draft.BoardHeight = AppSettings.BoardDefaultHeight;
        }

        draft.Hotkeys ??= [];

        foreach (var (actionId, gesture) in _gestures)
        {
            // 未绑定写成空串，等价于"这个动作不注册热键"
            draft.Hotkeys[actionId] = gesture.IsValid ? gesture.ToString() : "";
        }

        // 先让热键生效；失败时注册器已回滚，配置保持不动
        if (ApplyHotkeys is not null)
        {
            var desired = _gestures
                .Where(pair => pair.Value.IsValid)
                .Select(pair => (pair.Key, pair.Value))
                .ToList();

            var applied = ApplyHotkeys(desired);
            if (!applied.Ok)
            {
                MessageBox.Show(
                    this,
                    $"热键没有生效，设置未保存：\n{applied.Error}",
                    "设置",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        // 原子落盘：失败就不提交、不关窗
        var saved = SettingsStore.Save(draft);
        if (!saved.Ok)
        {
            ResumeHotkeys?.Invoke();
            MessageBox.Show(
                this,
                $"设置保存失败：{saved.Error}\n\n配置没有改变。请检查磁盘空间或文件权限后重试。",
                "设置",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        // 配置已经落盘：把草稿提交到共享实例
        _settings.Engine = draft.Engine;
        _settings.AzureRegion = draft.AzureRegion;
        _settings.AzureKeyProtected = draft.AzureKeyProtected;
        _settings.BoardWidth = draft.BoardWidth;
        _settings.BoardHeight = draft.BoardHeight;
        _settings.Hotkeys.Clear();

        foreach (var (actionId, gestureText) in draft.Hotkeys)
        {
            _settings.Hotkeys[actionId] = gestureText;
        }

        AutoStart.Apply(AutoStartCheck.IsChecked == true);

        Saved = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_recording is not null)
        {
            _recording = null;
            ResumeHotkeys?.Invoke();
        }

        base.OnClosing(e);
    }
}

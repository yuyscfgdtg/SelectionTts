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

        PreviewKeyDown += OnPreviewKeyDown;
        RefreshGestureTexts();
        UpdateAzurePanel();
    }

    /// <summary>录制热键前挂起全局热键，避免按到当前组合键时把软件开关掉。</summary>
    public Action? SuspendHotkeys { get; set; }

    public Action? ResumeHotkeys { get; set; }

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
            block.Text = actionId == _recording
                ? "按下组合键…（Esc 取消）"
                : _gestures[actionId].ToString();
            block.Foreground = actionId == _recording
                ? System.Windows.Media.Brushes.DarkOrange
                : System.Windows.Media.Brushes.Black;
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

        if (!HotkeyGesture.TryFromKeyboard(Keyboard.Modifiers, e.Key, out var gesture))
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
        StopRecording();

        var useAzure = AzureRadio.IsChecked == true;
        var key = CurrentKey().Trim();
        var region = RegionBox.Text.Trim();

        if (useAzure && (key.Length == 0 || region.Length == 0))
        {
            MessageBox.Show(this, "选择 Azure 引擎时必须填写区域和 Key。", "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.Engine = useAzure ? "azure" : "edge";
        _settings.AzureRegion = region;
        _settings.AzureKeyProtected = key.Length == 0 ? "" : Dpapi.Protect(key);

        _settings.Hotkeys ??= [];
        foreach (var (actionId, gesture) in _gestures)
        {
            _settings.Hotkeys[actionId] = gesture.ToString();
        }

        SettingsStore.Save(_settings);
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

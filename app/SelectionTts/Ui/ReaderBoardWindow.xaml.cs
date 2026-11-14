using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SelectionTts.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace SelectionTts.Ui;

/// <summary>
/// 朗读板：常驻浮窗，显示当前朗读目标、状态、进度和高亮位置。
///
/// 关键约束：不抢焦点。用 WS_EX_NOACTIVATE + SW_SHOWNOACTIVATE 显示，
/// 复制时焦点仍留在原来的程序里，光标不会跳。
///
/// 板子只读状态，不持有业务逻辑——文本和进度都在 ReaderService 里。
/// </summary>
public partial class ReaderBoardWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private static readonly Brush ActiveBackground =
        Freeze(new SolidColorBrush(Color.FromArgb(0x3A, 0x3D, 0xDC, 0x84)));

    private static readonly Brush NormalForeground =
        Freeze(new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)));

    private static readonly Brush ActiveForeground =
        Freeze(new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)));

    private static readonly Brush DotIdle =
        Freeze(new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)));

    private static readonly Brush DotLive =
        Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84)));

    private static readonly Brush DotPaused =
        Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24)));

    private static readonly Brush DotFailed =
        Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));

    private sealed record BlockView(TextBlock Element, int Start, int End);

    private readonly AppSettings _settings;
    private readonly ReaderService _reader;
    private readonly DispatcherTimer _timer;
    private readonly List<BlockView> _blocks = [];

    private string _renderedText = "";
    private int _activeBlock = -1;
    private bool _enabled;
    private long _lastUserScrollAt;
    private Point _dragStartScreen;
    private Point _dragStartWindow;
    private bool _dragging;

    public ReaderBoardWindow(AppSettings settings, ReaderService reader)
    {
        _settings = settings;
        _reader = reader;

        InitializeComponent();
        RestorePosition();

        CloseButton.Click += (_, _) => HideBoard();
        PauseButton.Click += (_, _) => TogglePause();
        StopButton.Click += (_, _) => _reader.Stop();

        Header.MouseLeftButtonDown += OnHeaderMouseDown;
        Header.MouseMove += OnHeaderMouseMove;
        Header.MouseLeftButtonUp += OnHeaderMouseUp;
        PreviewMouseWheel += (_, _) => _lastUserScrollAt = Environment.TickCount64;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _timer.Tick += (_, _) => Render();
        _timer.Start();
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Render();
    }

    /// <summary>底部提示跟着当前热键走，改过热键之后不会显示旧组合。</summary>
    public void SetHotkeyText(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            HintText.Text = $"{text} 开关";
        }
    }

    public void ShowBoard()
    {
        if (!IsVisible)
        {
            Show();
        }

        _settings.BoardVisible = true;
        SettingsStore.Save(_settings);
        Render();
    }

    public bool IsBoardVisible => IsVisible;

    public void ToggleBoard()
    {
        if (IsVisible)
        {
            HideBoard();
        }
        else
        {
            ShowBoard();
        }
    }

    private void HideBoard()
    {
        Hide();
        _settings.BoardVisible = false;
        SettingsStore.Save(_settings);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void TogglePause()
    {
        var state = _reader.GetState();
        if (state.Status == ReadStatus.Paused)
        {
            _reader.Resume();
        }
        else
        {
            _reader.Pause();
        }
    }

    private void Render()
    {
        if (!_enabled)
        {
            RenderDisabled();
            return;
        }

        var state = _reader.GetState();

        PauseButton.IsEnabled = state.Status is ReadStatus.Playing or ReadStatus.Paused;
        StopButton.IsEnabled = state.Status
            is ReadStatus.Preparing or ReadStatus.Playing or ReadStatus.Paused;
        PauseButton.Content = state.Status == ReadStatus.Paused ? "继续" : "暂停";

        StatusText.Text = state.StatusText;
        StatusDot.Fill = state.Status switch
        {
            ReadStatus.Playing => DotLive,
            ReadStatus.Completed => DotLive,
            ReadStatus.Paused => DotPaused,
            ReadStatus.Failed => DotFailed,
            _ => DotIdle,
        };

        if (state.CharCount == 0)
        {
            ClearBlocks();
            MetaText.Text = "复制文本后自动朗读";
            ProgressFill.Width = 0;
            return;
        }

        if (state.Text != _renderedText)
        {
            RebuildBlocks(state.Text);
        }

        var remaining = TimeSpan.FromSeconds(state.RemainingSeconds);
        MetaText.Text = remaining.TotalHours >= 1
            ? $"{state.CharIndex}/{state.CharCount} 字 · 剩余 {remaining:hh\\:mm\\:ss}"
            : $"{state.CharIndex}/{state.CharCount} 字 · 剩余 {remaining:mm\\:ss}";

        ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * state.Progress);

        var index = FindBlockIndex(state.CharIndex);
        if (index != _activeBlock)
        {
            SetActiveBlock(index);
        }
    }

    private void RenderDisabled()
    {
        StatusText.Text = "已关闭";
        MetaText.Text = "按 Ctrl+Alt+R 启用朗读";
        StatusDot.Fill = DotIdle;
        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        PauseButton.Content = "暂停";
        ProgressFill.Width = 0;

        if (_renderedText.Length > 0)
        {
            ClearBlocks();
        }
    }

    private void ClearBlocks()
    {
        _renderedText = "";
        _activeBlock = -1;
        _blocks.Clear();
        BlocksPanel.Children.Clear();
    }

    private void RebuildBlocks(string text)
    {
        _renderedText = text;
        _activeBlock = -1;
        _blocks.Clear();
        BlocksPanel.Children.Clear();

        foreach (var block in TextSegmenter.SplitDisplayBlocks(text))
        {
            var element = new TextBlock
            {
                Text = block.Text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = NormalForeground,
                FontSize = 13.5,
                LineHeight = 21,
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
            };

            var start = block.Start;
            element.MouseLeftButtonUp += (_, _) =>
            {
                if (_enabled)
                {
                    _reader.JumpTo(start);
                }
            };

            _blocks.Add(new BlockView(element, block.Start, block.End));
            BlocksPanel.Children.Add(element);
        }
    }

    private int FindBlockIndex(int charIndex)
    {
        for (var i = 0; i < _blocks.Count; i++)
        {
            if (charIndex < _blocks[i].End)
            {
                return i;
            }
        }

        return _blocks.Count - 1;
    }

    private void SetActiveBlock(int index)
    {
        if (_activeBlock >= 0 && _activeBlock < _blocks.Count)
        {
            _blocks[_activeBlock].Element.Background = Brushes.Transparent;
            _blocks[_activeBlock].Element.Foreground = NormalForeground;
        }

        _activeBlock = index;
        if (index < 0 || index >= _blocks.Count)
        {
            return;
        }

        var block = _blocks[index];
        block.Element.Background = ActiveBackground;
        block.Element.Foreground = ActiveForeground;

        // 用户刚手动滚过就不抢滚动条，避免"你滚你的它滚它的"
        if (Environment.TickCount64 - _lastUserScrollAt > 3000)
        {
            block.Element.BringIntoView();
        }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _dragging = true;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragStartWindow = new Point(Left, Top);
        Header.CaptureMouse();
        e.Handled = true;
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var current = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _dragStartWindow.X + (current.X - _dragStartScreen.X) / dpi.DpiScaleX;
        Top = _dragStartWindow.Y + (current.Y - _dragStartScreen.Y) / dpi.DpiScaleY;
    }

    private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Header.ReleaseMouseCapture();

        _settings.BoardX = (int)Math.Round(Left);
        _settings.BoardY = (int)Math.Round(Top);
        SettingsStore.Save(_settings);
    }

    private void RestorePosition()
    {
        var left = _settings.BoardX;
        var top = _settings.BoardY;

        if (IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
            return;
        }

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Bottom - Height - 24;
    }

    private static bool IsOnScreen(double left, double top)
    {
        if (left < 0 || top < 0)
        {
            return false;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        // 至少要留出 80×80 在屏幕内，否则当作无效位置
        return left + 80 <= virtualRight && top + 80 <= virtualBottom &&
               left >= virtualLeft - 40 && top >= virtualTop - 40;
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

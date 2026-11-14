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
/// 朗读板：常驻浮窗，显示当前朗读目标、合成进度和"读到第几行"。
///
/// 关键约束：不抢焦点。用 WS_EX_NOACTIVATE + SW_SHOWNOACTIVATE 显示，
/// 复制时焦点仍留在原来的程序里，光标不会跳。
///
/// 正文按板子宽度自己折行，一行就是一个单元格：
/// 读到哪行哪行变绿，点哪行就从哪行第一个字开始读。
/// </summary>
public partial class ReaderBoardWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const double BodyFontSize = 13.5;
    private const double BodyLineHeight = 21;

    private static readonly Brush ActiveBackground =
        Freeze(new SolidColorBrush(Color.FromArgb(0x3A, 0x3D, 0xDC, 0x84)));

    private static readonly Brush HoverBackground =
        Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));

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

    private static readonly Brush DotPreparing =
        Freeze(new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)));

    private static readonly Brush DotFailed =
        Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));

    private sealed record LineView(TextBlock Element, int Start, int End, int Index);

    private readonly AppSettings _settings;
    private readonly ReaderService _reader;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _resizeTimer;
    private readonly List<LineView> _lines = [];

    private string _renderedText = "";
    private double _renderedWidth;
    private int _activeLine = -1;
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
        StopButton.Click += (_, _) => ToggleMaster?.Invoke();

        Header.MouseLeftButtonDown += OnHeaderMouseDown;
        Header.MouseMove += OnHeaderMouseMove;
        Header.MouseLeftButtonUp += OnHeaderMouseUp;
        PreviewMouseWheel += (_, _) => _lastUserScrollAt = Environment.TickCount64;
        Scroller.SizeChanged += (_, _) => ScheduleRelayout();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _timer.Tick += (_, _) => Render();
        _timer.Start();

        // 拖动窗口时尺寸不变，但会被反复触发；用一个短延迟把连续变化合并成一次排版
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            RelayoutIfNeeded();
        };
    }

    /// <summary>板子上的"停止/启动"就是软件总开关，由 App 接到 SetWatching。</summary>
    public event Action? ToggleMaster;

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
        if (_reader.GetState().Status == ReadStatus.Paused)
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

        PauseButton.IsEnabled = state.Status
            is ReadStatus.Preparing or ReadStatus.Playing or ReadStatus.Paused;
        PauseButton.Content = state.Status == ReadStatus.Paused ? "继续" : "暂停";

        // 右按钮永远是总开关：亮着显示"停止"，关掉之后显示"启动"
        StopButton.IsEnabled = true;
        StopButton.Content = "停止";

        StatusText.Text = state.Status == ReadStatus.Preparing
            ? $"正在合成 {(int)Math.Round(state.SynthesisProgress * 100)}%"
            : state.StatusText;

        StatusDot.Fill = state.Status switch
        {
            ReadStatus.Preparing => DotPreparing,
            ReadStatus.Playing => DotLive,
            ReadStatus.Completed => DotLive,
            ReadStatus.Paused => DotPaused,
            ReadStatus.Failed => DotFailed,
            _ => DotIdle,
        };

        if (state.CharCount == 0)
        {
            ClearLines();
            MetaText.Text = "复制文本后自动朗读";
            ProgressFill.Width = 0;
            return;
        }

        if (state.Text != _renderedText)
        {
            RebuildLines(state.Text);
        }

        MetaText.Text = state.Status switch
        {
            ReadStatus.Preparing => $"已合成 {state.SynthChars}/{state.SynthTotal} 字",
            ReadStatus.Completed => state.SynthesisDone
                ? $"共 {state.CharCount} 字"
                : $"补合成 {(int)Math.Round(state.SynthesisProgress * 100)}%",
            ReadStatus.Failed => state.Error ?? "",
            _ => $"剩余 {FormatSeconds(state.RemainingSeconds)}",
        };

        // 进度条只表示"引擎合成到哪了"；全部缓存好才收起。
        var showBar = state.CharCount > 0 &&
                      !state.SynthesisDone &&
                      state.Status != ReadStatus.Failed;
        ProgressFill.Width = showBar
            ? Math.Max(0, ProgressTrack.ActualWidth * state.SynthesisProgress)
            : 0;

        var index = FindLineIndex(state.CharIndex);
        if (index != _activeLine)
        {
            SetActiveLine(index);
        }
    }

    private void RenderDisabled()
    {
        StatusText.Text = "已关闭";
        MetaText.Text = "按 Ctrl+Alt+R 启用朗读";
        StatusDot.Fill = DotIdle;

        PauseButton.IsEnabled = false;
        PauseButton.Content = "暂停";

        // 关掉之后还能从板子上打开，不用再按热键
        StopButton.IsEnabled = true;
        StopButton.Content = "启动";

        ProgressFill.Width = 0;

        if (_renderedText.Length > 0)
        {
            ClearLines();
        }
    }

    private static string FormatSeconds(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    private void ScheduleRelayout()
    {
        if (!IsVisible || _renderedText.Length == 0)
        {
            return;
        }

        var width = AvailableWidth();
        if (Math.Abs(width - _renderedWidth) < 2)
        {
            return;
        }

        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void RelayoutIfNeeded()
    {
        if (_renderedText.Length == 0)
        {
            return;
        }

        var width = AvailableWidth();
        if (Math.Abs(width - _renderedWidth) >= 2)
        {
            RebuildLines(_renderedText);
        }
    }

    private double AvailableWidth()
    {
        // 预留滚动条宽度：文本不该跑到滑块下面去
        const double ScrollGutter = 10;

        var viewport = Scroller.ViewportWidth;
        if (viewport > 32)
        {
            return Math.Max(80, viewport - ScrollGutter - 4);
        }

        return Math.Max(80, Scroller.ActualWidth - 28);
    }

    private void ClearLines()
    {
        _renderedText = "";
        _activeLine = -1;
        _lines.Clear();
        BlocksPanel.Children.Clear();
    }

    private void RebuildLines(string text)
    {
        var width = AvailableWidth();
        _renderedText = text;
        _renderedWidth = width;
        _activeLine = -1;
        _lines.Clear();
        BlocksPanel.Children.Clear();

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var dpi = VisualTreeHelper.GetDpi(this);
        var layout = BoardTextLayout.Build(text, width, typeface, BodyFontSize, dpi.PixelsPerDip);

        foreach (var line in layout)
        {
            var element = new TextBlock
            {
                Text = line.Text.Length == 0 ? "\u00A0" : line.Text,
                FontSize = BodyFontSize,
                LineHeight = BodyLineHeight,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = NormalForeground,
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                Cursor = Cursors.Hand,
            };

            var view = new LineView(element, line.Start, line.Start + Math.Max(1, line.Length), _lines.Count);

            element.MouseLeftButtonUp += (_, _) =>
            {
                if (_enabled)
                {
                    _reader.JumpTo(view.Start);
                }
            };

            element.MouseEnter += (_, _) =>
            {
                if (view.Index != _activeLine)
                {
                    element.Background = HoverBackground;
                }
            };

            element.MouseLeave += (_, _) =>
            {
                if (view.Index != _activeLine)
                {
                    element.Background = Brushes.Transparent;
                }
            };

            _lines.Add(view);
            BlocksPanel.Children.Add(element);
        }
    }

    private int FindLineIndex(int charIndex)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (charIndex < _lines[i].End)
            {
                return i;
            }
        }

        return _lines.Count - 1;
    }

    private void SetActiveLine(int index)
    {
        if (_activeLine >= 0 && _activeLine < _lines.Count)
        {
            var previous = _lines[_activeLine];
            previous.Element.Background = Brushes.Transparent;
            previous.Element.Foreground = NormalForeground;
        }

        _activeLine = index;
        if (index < 0 || index >= _lines.Count)
        {
            return;
        }

        var line = _lines[index];
        line.Element.Background = ActiveBackground;
        line.Element.Foreground = ActiveForeground;

        // 用户刚手动滚过就不抢滚动条
        if (Environment.TickCount64 - _lastUserScrollAt > 3000)
        {
            line.Element.BringIntoView();
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

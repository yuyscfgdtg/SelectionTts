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

    /// <summary>420 宽时的基准字号与行高；实际值按窗口宽度等比缩放。</summary>
    private const double BaseWidth = 420;

    private const double BaseFontSize = 13.5;

    private const double BaseLineHeight = 21;

    private const double MinScale = 0.85;

    private const double MaxScale = 2.15;

    /// <summary>距窗口边缘多少 DIP 以内算拖拽缩放区域。</summary>
    private const double ResizeBorder = 5;

    private const int WM_NCHITTEST = 0x0084;

    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

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
    private readonly DispatcherTimer _persistTimer;
    private readonly List<LineView> _lines = [];

    private HwndSource? _hwndSource;
    private double _bodyFontSize = BaseFontSize;
    private double _bodyLineHeight = BaseLineHeight;
    private double _renderedScale = 1;
    private string _renderedText = "";
    private double _renderedWidth;
    private int _activeLine = -1;
    private bool _enabled;
    private string _hotkeyText = "Ctrl+Alt+R";
    private long _lastUserScrollAt;
    private Point _dragStartScreen;
    private Point _dragStartWindow;
    private bool _dragging;

    public ReaderBoardWindow(AppSettings settings, ReaderService reader)
    {
        _settings = settings;
        _reader = reader;

        InitializeComponent();
        ApplyStoredSize();
        RestorePosition();

        CloseButton.Click += (_, _) => HideBoard();
        PauseButton.Click += (_, _) => TogglePauseRequested?.Invoke();
        StopButton.Click += (_, _) => ToggleMaster?.Invoke();

        Header.MouseLeftButtonDown += OnHeaderMouseDown;
        Header.MouseMove += OnHeaderMouseMove;
        Header.MouseLeftButtonUp += OnHeaderMouseUp;
        PreviewMouseWheel += (_, _) => _lastUserScrollAt = Environment.TickCount64;
        // 拖滑块 / 点轨道也算用户自己在滚，别让自动跟随马上把视图拽回去
        Scroller.PreviewMouseDown += (_, _) => _lastUserScrollAt = Environment.TickCount64;
        Scroller.SizeChanged += (_, _) => ScheduleRelayout();
        SizeChanged += OnWindowSizeChanged;

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

        // 尺寸写回配置也防抖：拖拽过程中每帧都写盘既慢又没必要
        _persistTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _persistTimer.Tick += (_, _) =>
        {
            _persistTimer.Stop();
            PersistSizeIfChanged();
        };
    }

    /// <summary>板子上的"停止/启动"就是软件总开关，由 App 接到 SetWatching。</summary>
    public event Action? ToggleMaster;

    /// <summary>板子上的"暂停/继续"，和暂停热键共用 App 里的同一个入口。</summary>
    public event Action? TogglePauseRequested;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Render();
    }

    /// <summary>底部提示跟着当前热键走，改过热键之后不会显示旧组合。</summary>
    public void SetHotkeyText(string text)
    {
        _hotkeyText = string.IsNullOrWhiteSpace(text) ? "" : text;
        HintText.Text = _hotkeyText.Length == 0 ? "未设置热键" : $"{_hotkeyText} 开关";
    }

    /// <summary>
    /// 设置里「恢复默认尺寸」保存后调用：按配置里的尺寸重设窗口，
    /// 并把位置夹回可见区域（窄了之后可能超出屏幕边缘）。
    /// </summary>
    public void ApplySavedSize()
    {
        ApplyStoredSize();
        ClampToVirtualScreen();
    }

    /// <summary>套用上次退出时的尺寸；上限跟着当前屏幕走，不写死在配置里。</summary>
    private void ApplyStoredSize()
    {
        var area = SystemParameters.WorkArea;

        var maxWidth = Math.Max(AppSettings.BoardMinWidth, Math.Min(AppSettings.BoardMaxWidth, area.Width));
        var maxHeight = Math.Max(AppSettings.BoardMinHeight, area.Height);

        MaxWidth = maxWidth;
        MaxHeight = maxHeight;

        Width = Math.Clamp(_settings.BoardWidth, AppSettings.BoardMinWidth, maxWidth);
        Height = Math.Clamp(_settings.BoardHeight, AppSettings.BoardMinHeight, maxHeight);
    }

    /// <summary>字号比例：只跟宽度走，拖高只是多显示几行。</summary>
    private double CurrentScale() =>
        double.IsNaN(Width) || Width <= 0
            ? 1
            : Math.Clamp(Width / BaseWidth, MinScale, MaxScale);

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 只拖高度时不需要重新折行，但尺寸要照样记住
        _persistTimer.Stop();
        _persistTimer.Start();

        if (e.WidthChanged)
        {
            ScheduleRelayout();
        }
    }

    private void PersistSizeIfChanged()
    {
        var width = (int)Math.Round(Width);
        var height = (int)Math.Round(Height);

        if (width == _settings.BoardWidth && height == _settings.BoardHeight)
        {
            return;
        }

        _settings.BoardWidth = width;
        _settings.BoardHeight = height;
        _ = SettingsStore.Save(_settings);
        Log.Info($"朗读板尺寸：{width}×{height}");
    }

    public void ShowBoard()
    {
        if (!IsVisible)
        {
            ClampToVirtualScreen();
            Show();
        }

        _settings.BoardVisible = true;
        _ = SettingsStore.Save(_settings);
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
        _ = SettingsStore.Save(_settings);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        // WindowStyle=None 的窗口没有系统边框，自己告诉系统哪几个像素是拖拽边
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        base.OnClosed(e);
    }

    /// <summary>
    /// 把靠近边缘的鼠标位置翻译成系统的缩放命中码（HTLEFT/HTBOTTOMRIGHT…），
    /// 之后由 Windows 自己完成拖拽缩放，光标形状和跟手程度都是原生的。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST)
        {
            return IntPtr.Zero;
        }

        // lParam 低字是 x、高字是 y（屏幕坐标，多显示器可能是负数，所以要按 short 解）
        var packed = lParam.ToInt64();
        var screenX = unchecked((short)(packed & 0xFFFF));
        var screenY = unchecked((short)((packed >> 16) & 0xFFFF));

        var local = PointFromScreen(new Point(screenX, screenY));
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        var nearLeft = local.X <= ResizeBorder;
        var nearRight = local.X >= width - ResizeBorder;
        var nearTop = local.Y <= ResizeBorder;
        var nearBottom = local.Y >= height - ResizeBorder;

        var hit = (nearTop, nearBottom, nearLeft, nearRight) switch
        {
            (true, _, true, _) => HTTOPLEFT,
            (true, _, _, true) => HTTOPRIGHT,
            (_, true, true, _) => HTBOTTOMLEFT,
            (_, true, _, true) => HTBOTTOMRIGHT,
            (true, _, _, _) => HTTOP,
            (_, true, _, _) => HTBOTTOM,
            (_, _, true, _) => HTLEFT,
            (_, _, _, true) => HTRIGHT,
            _ => HTCLIENT,
        };

        if (hit == HTCLIENT)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(hit);
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
        MetaText.Text = _hotkeyText.Length == 0
            ? "在托盘菜单启用朗读"
            : $"按 {_hotkeyText} 启用朗读";
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

        if (!NeedsRelayout())
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

        if (NeedsRelayout())
        {
            RebuildLines(_renderedText);
        }
    }

    /// <summary>宽度或字号比例变了都要重新折行。</summary>
    private bool NeedsRelayout() =>
        Math.Abs(AvailableWidth() - _renderedWidth) >= 2 ||
        Math.Abs(CurrentScale() - _renderedScale) >= 0.01;

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
        var scale = CurrentScale();
        _bodyFontSize = Math.Round(BaseFontSize * scale, 1);
        _bodyLineHeight = Math.Round(BaseLineHeight * scale, 1);

        _renderedText = text;
        _renderedWidth = width;
        _renderedScale = scale;
        _activeLine = -1;
        _lines.Clear();
        BlocksPanel.Children.Clear();

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var dpi = VisualTreeHelper.GetDpi(this);
        var layout = BoardTextLayout.Build(text, width, typeface, _bodyFontSize, dpi.PixelsPerDip);

        foreach (var line in layout)
        {
            var element = new TextBlock
            {
                Text = line.Text.Length == 0 ? "\u00A0" : line.Text,
                FontSize = _bodyFontSize,
                LineHeight = _bodyLineHeight,
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
        _ = SettingsStore.Save(_settings);
    }

    private void RestorePosition()
    {
        var area = SystemParameters.WorkArea;

        var hasSaved = _settings.BoardX != int.MinValue && _settings.BoardY != int.MinValue;
        var left = hasSaved ? _settings.BoardX : area.Right - Width - 24;
        var top = hasSaved ? _settings.BoardY : area.Bottom - Height - 24;

        if (!IsOnScreen(left, top))
        {
            left = area.Right - Width - 24;
            top = area.Bottom - Height - 24;
        }

        Left = left;
        Top = top;

        // 换到更小的屏幕、或分辨率变低之后，保证整块板子仍在桌面内
        ClampToVirtualScreen();
    }

    /// <summary>把窗口夹回虚拟桌面范围（多屏时以整个虚拟桌面为准）。</summary>
    private void ClampToVirtualScreen()
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        Left = Math.Clamp(Left, virtualLeft, Math.Max(virtualLeft, virtualRight - width));
        Top = Math.Clamp(Top, virtualTop, Math.Max(virtualTop, virtualBottom - height));
    }

    private static bool IsOnScreen(double left, double top)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        // 至少要留出 80×80 在虚拟桌面内，否则当作无效位置。
        // 注意不能把负坐标直接判无效：主屏左侧/上方的副屏就是负坐标。
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

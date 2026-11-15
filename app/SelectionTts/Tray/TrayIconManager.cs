using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using SelectionTts.Core;

namespace SelectionTts.Tray;

/// <summary>
/// 托盘：总开关、朗读板开关、停止朗读、音色、语速、退出。
///
/// 总开关是"软件级"的（类似 VPN 的连接开关）：关闭时图标变灰、状态显示"已关闭"。
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _boardItem;
    private readonly Dictionary<string, ToolStripMenuItem> _voiceItems = [];
    private readonly Dictionary<string, ToolStripMenuItem> _rateItems = [];
    private readonly Icon _normalIcon;
    private readonly Icon _disabledIcon;

    private bool _enabled;
    private string _status = "待命";
    private long _lastTrayClickAt;

    public event Action? ExitRequested;
    public event Action? ToggleWatchRequested;
    public event Action? ToggleBoardRequested;
    public event Action? SettingsRequested;

    private string _hotkeyText = "Ctrl+Alt+R";

    public TrayIconManager(AppSettings settings)
    {
        _settings = settings;
        _normalIcon = LoadIcon();
        _disabledIcon = Grayscale(_normalIcon);

        _enabledItem = new ToolStripMenuItem
        {
            CheckOnClick = false,
        };
        _enabledItem.Click += (_, _) =>
        {
            Log.Info("托盘菜单：切换总开关");
            ToggleWatchRequested?.Invoke();
        };

        _boardItem = new ToolStripMenuItem
        {
            Text = "显示朗读板",
            CheckOnClick = false,
            Checked = settings.BoardVisible,
        };
        _boardItem.Click += (_, _) => ToggleBoardRequested?.Invoke();

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_boardItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildVoiceMenu());
        menu.Items.Add(BuildRateMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("打开数据目录", null, (_, _) => OpenDataDir()));
        menu.Items.Add(new ToolStripMenuItem("打开日志", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new NotifyIcon
        {
            Icon = _disabledIcon,
            Text = "剪贴板朗读 · 已关闭",
            ContextMenuStrip = menu,
            Visible = true,
        };

        // 左键单击 = 显示/隐藏朗读板。双击会连发两次 MouseClick，
        // 不节流就会"开了又关"，看起来像没反应。
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (now - _lastTrayClickAt < 300)
            {
                return;
            }

            _lastTrayClickAt = now;
            Log.Info("托盘左键：切换朗读板");
            ToggleBoardRequested?.Invoke();
        };

        UpdateEnabledText();
    }

    public void Notify(string message) =>
        _icon.ShowBalloonTip(2000, "剪贴板朗读", message, ToolTipIcon.None);

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _icon.Icon = enabled ? _normalIcon : _disabledIcon;
        _boardItem.Enabled = true;
        UpdateEnabledText();
        UpdateTip();
    }

    /// <summary>朗读状态（合成中/朗读中/已停止…）。</summary>
    public void SetStatus(string status)
    {
        _status = status;
        UpdateTip();
    }

    public void SetBoardVisible(bool visible) => _boardItem.Checked = visible;

    public void SetHotkeyText(string text)
    {
        // 暂停/继续这类动作可以出厂不绑定，这里要能显示"未设置"
        _hotkeyText = string.IsNullOrWhiteSpace(text) ? "未设置热键" : text;
        UpdateEnabledText();
    }

    private void UpdateEnabledText()
    {
        _enabledItem.Text = _enabled
            ? $"● 朗读已启用（点击关闭）    {_hotkeyText}"
            : $"○ 朗读已关闭（点击启用）    {_hotkeyText}";
    }

    /// <summary>托盘悬停提示，最多 63 个字符（Win32 限制）。</summary>
    private void UpdateTip()
    {
        var text = _enabled ? $"剪贴板朗读 · {_status}" : "剪贴板朗读 · 已关闭";
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    private ToolStripMenuItem BuildVoiceMenu()
    {
        var root = new ToolStripMenuItem("音色");
        foreach (var voice in VoiceCatalog.Voices)
        {
            var item = new ToolStripMenuItem(voice.Label)
            {
                Checked = voice.Id == _settings.Voice,
                Tag = voice.Id,
            };
            item.Click += (sender, _) =>
            {
                var id = (string)((ToolStripMenuItem)sender!).Tag!;
                _settings.Voice = id;
                SettingsStore.Save(_settings);
                UpdateChecks(_voiceItems, id);
                Log.Info($"切换音色 {id}");
                Notify($"音色：{VoiceCatalog.LabelOf(id)}");
            };
            _voiceItems[voice.Id] = item;
            root.DropDownItems.Add(item);
        }

        return root;
    }

    private ToolStripMenuItem BuildRateMenu()
    {
        var root = new ToolStripMenuItem("语速");
        var labels = new Dictionary<string, string>
        {
            ["-20%"] = "慢（-20%）",
            ["+0%"] = "正常",
            ["+20%"] = "快（+20%）",
            ["+50%"] = "很快（+50%）",
        };

        foreach (var rate in VoiceCatalog.Rates)
        {
            var item = new ToolStripMenuItem(labels[rate])
            {
                Checked = rate == _settings.Rate,
                Tag = rate,
            };
            item.Click += (sender, _) =>
            {
                var value = (string)((ToolStripMenuItem)sender!).Tag!;
                _settings.Rate = value;
                SettingsStore.Save(_settings);
                UpdateChecks(_rateItems, value);
                Log.Info($"切换语速 {value}");
                Notify($"语速：{labels[value]}");
            };
            _rateItems[rate] = item;
            root.DropDownItems.Add(item);
        }

        return root;
    }

    private static void UpdateChecks(Dictionary<string, ToolStripMenuItem> items, string selected)
    {
        foreach (var (key, item) in items)
        {
            item.Checked = key == selected;
        }
    }

    private static void OpenDataDir()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.DataDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开数据目录失败", ex);
        }
    }

    private static void OpenLog()
    {
        try
        {
            var path = Path.Combine(AppPaths.LogDir, "app.log");
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error("打开日志失败", ex);
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (icon is not null)
            {
                return icon;
            }
        }
        catch
        {
            // ignore
        }

        return SystemIcons.Application;
    }

    /// <summary>关闭态图标：灰度 + 半透明，一眼能看出"没在工作"。</summary>
    private static Icon Grayscale(Icon source)
    {
        try
        {
            using var bitmap = source.ToBitmap();
            using var gray = new Bitmap(bitmap.Width, bitmap.Height);
            using (var graphics = Graphics.FromImage(gray))
            using (var attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(new ColorMatrix(
                [
                    [0.30f, 0.30f, 0.30f, 0f, 0f],
                    [0.59f, 0.59f, 0.59f, 0f, 0f],
                    [0.11f, 0.11f, 0.11f, 0f, 0f],
                    [0f, 0f, 0f, 0.45f, 0f],
                    [0f, 0f, 0f, 0f, 1f],
                ]));

                graphics.DrawImage(
                    bitmap,
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    0,
                    0,
                    bitmap.Width,
                    bitmap.Height,
                    GraphicsUnit.Pixel,
                    attributes);
            }

            return Icon.FromHandle(gray.GetHicon());
        }
        catch
        {
            return source;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
